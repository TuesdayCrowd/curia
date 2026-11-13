using System.Text;

namespace Curia.Infrastructure.Embedder.Tokenization;

/// <summary>What the tokenizer read: the content ids (no <c>[CLS]</c> or <c>[SEP]</c>), how many characters of the text it consumed, and whether it stopped at the cap.</summary>
internal sealed record TokenizeResult(IReadOnlyList<int> ContentIds, int CharactersConsumed, bool StoppedAtCap);

/// <summary>
/// BERT-uncased WordPiece, written in this repository to the behaviour of Hugging Face's tokenizer (errata G65, Amendment 1 ruling 22), streaming: it stops on a word
/// boundary after the <c>maxContentTokens</c>th content token and reads no further. The text is prepared in the order Hugging Face prepares it (lone surrogates to U+FFFD; the five special
/// tokens matched case-sensitively on the raw text; control characters dropped and white space made a space; CJK ideographs spaced; decomposed, non-spacing marks removed, lower-cased; split at
/// white space and punctuation) and each word is matched greedily, longest first, against the vocabulary. Every step is local to a character or to a word, so the tokens of a prefix are a prefix
/// of the tokens of the text and the cap never cuts a word the text goes on with (Ruling 10).
/// </summary>
internal sealed class WordPieceTokenizer
{
    private static readonly string[] Specials = ["[CLS]", "[SEP]", "[PAD]", "[UNK]", "[MASK]"];

    /// <summary>A word of more than this many characters is the unknown token.</summary>
    private const int MaxWordCharacters = 100;

    private readonly Dictionary<string, int> _ids;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly int?[] _specialIds;

    internal WordPieceTokenizer(IReadOnlyList<string> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        Vocabulary = vocabulary;
        _ids = new Dictionary<string, int>(vocabulary.Count, StringComparer.Ordinal);
        for (var i = 0; i < vocabulary.Count; i++) _ids.TryAdd(vocabulary[i], i);
        _lookup = _ids.GetAlternateLookup<ReadOnlySpan<char>>();
        _specialIds = [.. Specials.Select(s => _ids.TryGetValue(s, out var id) ? (int?)id : null)];
    }

    internal IReadOnlyList<string> Vocabulary { get; }

    /// <summary>The id of <c>[UNK]</c>, or -1 where the vocabulary has none.</summary>
    internal int UnknownId => _specialIds[3] ?? -1;

    /// <summary>The id of <c>[CLS]</c>, or -1.</summary>
    internal int ClsId => _specialIds[0] ?? -1;

    /// <summary>The id of <c>[SEP]</c>, or -1.</summary>
    internal int SepId => _specialIds[1] ?? -1;

    internal TokenizeResult Tokenize(string text, int maxContentTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxContentTokens <= 0) return new TokenizeResult([], 0, true);

        var scan = new Scan(this, maxContentTokens);
        var i = 0;
        while (i < text.Length && !scan.Full)
        {
            if (text[i] == '[' && SpecialAt(text, i) is { } special)
            {
                scan.Special(special.Id);
                i += special.Length;
                continue;
            }

            Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var used);
            scan.Push(rune);
            i += used;
        }

        if (!scan.Full) scan.Finish();
        return new TokenizeResult(scan.Ids, scan.Full ? i : text.Length, scan.Full);
    }

    private (int Id, int Length)? SpecialAt(string text, int index)
    {
        for (var s = 0; s < Specials.Length; s++)
            if (_specialIds[s] is { } id && text.AsSpan(index).StartsWith(Specials[s], StringComparison.Ordinal))
                return (id, Specials[s].Length);
        return null;
    }

    /// <summary>Appends the ids of one word: its pieces by greedy longest match, or the unknown token when any part has none or the word is too long.</summary>
    private void WordIds(string word, List<int> into)
    {
        var runes = 0;
        foreach (var unused in word.EnumerateRunes()) runes++;
        if (runes > MaxWordCharacters)
        {
            into.Add(UnknownId);
            return;
        }

        Span<char> buffer = stackalloc char[(MaxWordCharacters * 2) + 2];
        var added = into.Count;
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (end > start)
            {
                var prefix = start > 0 ? 2 : 0;
                if (prefix == 2)
                {
                    buffer[0] = '#';
                    buffer[1] = '#';
                }

                word.AsSpan(start, end - start).CopyTo(buffer[prefix..]);
                if (_lookup.TryGetValue(buffer[..(prefix + end - start)], out var id))
                {
                    found = id;
                    break;
                }

                end -= char.IsLowSurrogate(word[end - 1]) && end - 2 >= start ? 2 : 1;
            }

            if (found < 0)
            {
                into.RemoveRange(added, into.Count - added);
                into.Add(UnknownId);
                return;
            }

            into.Add(found);
            start = end;
        }
    }

    /// <summary>One pass over a text: the cleaned characters, grouped with the marks that follow them, normalised, and split into words as they arrive.</summary>
    private sealed class Scan(WordPieceTokenizer owner, int max)
    {
        private readonly StringBuilder _cluster = new();
        private readonly StringBuilder _word = new();
        private readonly List<int> _pieces = [];

        internal List<int> Ids { get; } = [];

        internal bool Full { get; private set; }

        internal void Special(int id)
        {
            FlushCluster();
            EndWord();
            Add(id);
        }

        internal void Push(Rune rune)
        {
            if (TextCleaner.IsDropped(rune)) return;
            if (Rune.IsWhiteSpace(rune))
            {
                Cleaned(new Rune(' '));
                return;
            }

            if (TextCleaner.IsChinese(rune.Value))
            {
                Cleaned(new Rune(' '));
                Cleaned(rune);
                Cleaned(new Rune(' '));
                return;
            }

            Cleaned(rune);
        }

        internal void Finish()
        {
            FlushCluster();
            EndWord();
        }

        private void Cleaned(Rune rune)
        {
            if (_cluster.Length > 0 && _cluster.Length < TextCleaner.MaxCluster && TextCleaner.IsMark(rune))
            {
                _cluster.Append(rune.ToString());
                return;
            }

            FlushCluster();
            _cluster.Append(rune.ToString());

            // An ASCII character takes no mark into its normalisation (a mark after it starts a piece of its own and is removed or kept as it would have been), so it is
            // not held back for one: the word it ends is complete when its delimiter has been read, and not one character later.
            if (rune.Value < 0x80) FlushCluster();
        }

        private void FlushCluster()
        {
            if (_cluster.Length == 0) return;
            TextCleaner.AppendNormalised(_cluster, Normalised);
            _cluster.Clear();
        }

        private void Normalised(Rune rune)
        {
            if (Full) return;
            if (Rune.IsWhiteSpace(rune))
            {
                EndWord();
            }
            else if (TextCleaner.IsPunctuation(rune))
            {
                EndWord();
                _word.Append(rune.ToString());
                EndWord();
            }
            else
            {
                _word.Append(rune.ToString());
            }
        }

        private void EndWord()
        {
            if (_word.Length == 0 || Full) return;
            _pieces.Clear();
            owner.WordIds(_word.ToString(), _pieces);
            _word.Clear();
            foreach (var id in _pieces)
            {
                Add(id);
                if (Full) return;
            }
        }

        private void Add(int id)
        {
            if (Full) return;
            Ids.Add(id);
            if (Ids.Count >= max) Full = true;
        }
    }
}
