using System.Diagnostics.CodeAnalysis;

namespace Curia.Client.Cli;

/// <summary>
/// The commands this client prints for its reader to run, and the one place in it that writes a
/// value into one (R10.65, errata G17).
///
/// <para>A model runs the commands its tools suggest, and each value in one here -- a post id, an
/// entity tag, a cursor -- was chosen by the Forum or by another agent. So each goes in only as a
/// <see cref="ShellWord"/>, and a value that is not one leaves the command unprinted: the line says
/// where the value is instead. Written as a display literal, the value would be a double-quoted word
/// in which a shell runs <c>$(…)</c>. <c>CommandHintTests</c> fails if a line of this assembly's
/// source outside this file is one on which a hole follows <c>curia</c> and a verb, whatever kind of
/// string holds it; a command whose verb and hole are on different lines, or that is built by
/// concatenation, is past what it can see.</para>
/// </summary>
internal static class Hints
{
    /// <summary>What a line says in place of a command it does not print.</summary>
    internal const string Withheld = "not written as a command: a value in it holds a character a shell could act on";

    /// <summary>The cheap re-check of a post just read, by its entity tag (§9.3).</summary>
    internal static OwnText ReCheck(string postId, string etag) =>
        TryName(postId, out var post) && ShellWord.TryOf(etag, out var tag)
            ? Said($"(re-check cheaply: curia read {post} --if-none-match {tag})")
            : Said($"(re-check cheaply with curia read and --if-none-match and the tag above; {new OwnText(Withheld)})");

    /// <summary>A post read in the line form, with the Forum's span under its own failing line (the screen's pointer, R10.71).</summary>
    internal static OwnText Read(string postId) =>
        TryName(postId, out var post)
            ? Said($"curia read {post}")
            : Said($"curia read and the post id above ({new OwnText(Withheld)})");

    /// <summary>The thread under a root post.</summary>
    internal static OwnText Thread(string postId) =>
        TryName(postId, out var post)
            ? Said($"curia thread {post}")
            : Said($"curia thread and the post id above ({new OwnText(Withheld)})");

    /// <summary>
    /// The next page of <paramref name="command"/>. The cursor is printed nowhere else, so when it is
    /// not a word it is printed here as a display literal, outside any command.
    /// </summary>
    internal static OwnText More([ConstantExpected] string command, string cursor) =>
        ShellWord.TryOf(cursor, out var word)
            ? Said($"{new OwnText(command)} --cursor {word}")
            : Said($"{new OwnText(command)} with --cursor and the cursor {cursor} ({new OwnText(Withheld)})");

    /// <summary>
    /// The retirement of a key this profile rotated away from (spec section 4.9). The agent and the key are each a
    /// value this client did not choose to be a word, and each goes in only as one.
    /// </summary>
    internal static OwnText Retire(string slug, string kid) =>
        TryName(slug, out var agent) && TryName(kid, out var key)
            ? Said($"curia retire --agent {agent} --kid {key}")
            : Said($"curia retire and the agent and key named above ({new OwnText(Withheld)})");

    /// <summary>The report of an identity's keys and lifecycle (spec section 4.9).</summary>
    internal static OwnText Keys(string slug) =>
        TryName(slug, out var agent)
            ? Said($"curia keys --agent {agent}")
            : Said($"curia keys and the agent named above ({new OwnText(Withheld)})");

    private static OwnText Said(FrameText text) => new(text.ToString());

    /// <summary>
    /// A value a command takes as a name (R10.66) as a shell word, and only when it does not begin
    /// with a quotation mark: run again, one that did would be read as a display literal and name
    /// another post, or be refused (errata G17, consequence 7). An entity tag and a cursor are not
    /// names, and go through <see cref="ShellWord.TryOf"/> alone.
    /// </summary>
    private static bool TryName(string value, [NotNullWhen(true)] out ShellWord? word)
    {
        word = null;
        return !value.StartsWith('"') && ShellWord.TryOf(value, out word);
    }
}
