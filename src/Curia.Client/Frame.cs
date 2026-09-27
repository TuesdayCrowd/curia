using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Curia.Canon.Json;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// Words this client wrote, which a <see cref="FrameText"/> hole writes as they are.
///
/// <para>Besides a constant, a span the Forum delimited (<see cref="FrameBuilder.Span"/>) and a
/// <see cref="ShellWord"/>, the way to put a string into a frame unquoted, and so the thing to look
/// for when reviewing what a frame prints: every use says "this is mine", and a use that wraps a
/// value the Forum served, the log recorded or an agent named is the defect R10.63 (errata G17)
/// forbids. <see cref="FrameText.AppendLiteral"/> is public because the compiler calls it; no code
/// here calls it by hand, and a review greps for it as for this type.</para>
/// </summary>
/// <param name="Text">The client's own words.</param>
public readonly record struct OwnText(string Text);

/// <summary>
/// A value this client did not compose, written into a command it prints for its reader to run
/// (R10.65, errata G17): the value between single quotation marks, and only a value a shell reads
/// back as itself there.
///
/// <para><b>Why not a display literal.</b> A display literal is a JSON string, and a shell reads it
/// as a double-quoted word, inside which it runs <c>$(…)</c> and backticks: a command that printed a
/// Forum's entity tag as a literal ran whatever the Forum had put in the tag, in every shell tried.
/// Between single quotation marks nothing runs in sh, dash, bash, zsh, fish, csh or tcsh, and a value
/// of printable ASCII other than <c>'</c>, <c>\</c> and <c>!</c> reads back as itself in each. A
/// <c>'</c> ends the word in every one of them, a <c>\</c> before <c>'</c> or <c>\</c> is an escape
/// in fish, csh and tcsh expand <c>!</c> as history even there, and a value beginning with <c>-</c>
/// is read as an option rather than as a value. <see cref="TryOf"/> refuses each of these, and a
/// command holding a value it refused is not printed as a command. No honest value holds any of
/// them: a post id is a ULID, a cursor is base64, and an entity tag is a quoted digest in hex.</para>
/// </summary>
public readonly record struct ShellWord
{
    private ShellWord(string value) => Value = value;

    /// <summary>The value, as it was given.</summary>
    public string Value { get; }

    /// <summary>The word: the value between single quotation marks.</summary>
    public override string ToString() => "'" + Value + "'";

    /// <summary>
    /// The value as a shell word; false, and no word, when the value is empty, begins with
    /// <c>-</c>, or holds a character outside printable ASCII, <c>'</c>, <c>\</c> or <c>!</c>.
    /// </summary>
    public static bool TryOf(string? value, out ShellWord word)
    {
        word = default;
        if (string.IsNullOrEmpty(value) || value[0] == '-') return false;

        foreach (var unit in value)
        {
            if (unit is < ' ' or > '~' or '\'' or '\\' or '!') return false;
        }

        word = new ShellWord(value);
        return true;
    }
}

/// <summary>
/// One line, or part of one, of this client's frame, written by interpolation (R10.63, errata G17).
///
/// <para><b>Every string hole is quoted.</b> A string interpolated into a frame is a value this
/// client did not write until something says otherwise, so it is written as
/// <see cref="DisplayLiteral"/> writes it: a JSON string literal that cannot end the line it sits on,
/// begin another, or reorder the text around it. The client's own words go in through
/// <see cref="OwnText"/>, and a value in a command the reader may run through
/// <see cref="ShellWord"/>. A struct that formats itself (<see cref="IFormattable"/>) -- a number, an
/// instant, an enum -- goes in as itself, formatted invariantly: none of the BCL's holds text a
/// stranger chose, an enum's being the name of one of its members; and no struct in this repository
/// implements <see cref="IFormattable"/> -- one that wrapped a served string would print it here
/// unquoted. A character is quoted, since a line break is one. Anything else -- a record, a
/// collection, a <see cref="Uri"/>, a <see cref="bool"/> -- has no overload here and does not
/// compile, so each such hole is a decision made where it is written.</para>
///
/// <para><b>Why quoting is the default and not the exception.</b> Before this type the reference
/// client and <c>curia-mcp</c> printed every served value as it came, and a board name or an agent
/// identifier holding a line break began lines that read as the client's own verdict — "signature
/// verified", "owner verified", an instruction — outside the delimited span and above the standing
/// warning (register D31). A site that forgets to quote is the failure mode of the old arrangement;
/// under this one a site that forgets to say "mine" prints the client's own words in quotes, which is
/// a cosmetic defect and not a security one.</para>
/// </summary>
[InterpolatedStringHandler]
public readonly ref struct FrameText
{
    private readonly StringBuilder _text;

    public FrameText(int literalLength, int formattedCount) =>
        _text = new StringBuilder(literalLength + (formattedCount * 16));

    /// <summary>The literal parts: this client's source text.</summary>
    public void AppendLiteral(string value) => _text.Append(value);

    /// <summary>A value this client did not write: quoted.</summary>
    public void AppendFormatted(string? value) => _text.Append(DisplayLiteral.Of(value));

    /// <summary>A value this client did not write, quoted and then padded to a column.</summary>
    public void AppendFormatted(string? value, int alignment) => Pad(DisplayLiteral.Of(value), alignment);

    /// <summary>A single character is a value too, and a line break is one character.</summary>
    public void AppendFormatted(char value) => _text.Append(DisplayLiteral.Of(value.ToString()));

    /// <summary>The client's own words.</summary>
    public void AppendFormatted(OwnText value) => _text.Append(value.Text);

    /// <summary>The client's own words, padded to a column.</summary>
    public void AppendFormatted(OwnText value, int alignment) => Pad(value.Text, alignment);

    /// <summary>A value in a command the reader may run: single-quoted, as <see cref="ShellWord"/> admits it.</summary>
    public void AppendFormatted(ShellWord value) => _text.Append(value.ToString());

    /// <summary>A number, an enum or an instant, formatted invariantly.</summary>
    public void AppendFormatted<T>(T value)
        where T : struct, IFormattable =>
        _text.Append(value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>A number, an enum or an instant, in the format asked for.</summary>
    public void AppendFormatted<T>(T value, string? format)
        where T : struct, IFormattable =>
        _text.Append(value.ToString(format, CultureInfo.InvariantCulture));

    /// <summary>A number, an enum or an instant, padded to a column.</summary>
    public void AppendFormatted<T>(T value, int alignment)
        where T : struct, IFormattable =>
        Pad(value.ToString(null, CultureInfo.InvariantCulture), alignment);

    /// <summary>A number, an enum or an instant that may be absent.</summary>
    public void AppendFormatted<T>(T? value)
        where T : struct, IFormattable =>
        _text.Append(value is { } present ? present.ToString(null, CultureInfo.InvariantCulture) : DisplayLiteral.Absent);

    /// <summary>What was written.</summary>
    public override string ToString() => _text.ToString();

    private void Pad(string text, int alignment)
    {
        var width = Math.Abs(alignment);
        if (alignment < 0) _text.Append(text).Append(' ', Math.Max(0, width - text.Length));
        else _text.Append(' ', Math.Max(0, width - text.Length)).Append(text);
    }
}

/// <summary>
/// A frame this client builds: its own lines, each written through <see cref="FrameText"/> or as a
/// constant, and the Forum's delimited spans, each checked before it is written as served.
///
/// <para>No method here takes a non-constant string as a line: <see cref="Span"/>, the one that takes
/// a served string, writes it as served only once its delimiters are checked, and quotes it
/// otherwise. So a value reaches a frame quoted, as a span whose delimiters were checked, or through
/// <see cref="OwnText"/> or <see cref="ShellWord"/> where a reviewer can see it.</para>
/// </summary>
public sealed class FrameBuilder
{
    private readonly StringBuilder _text = new();

    /// <summary>A line of this client's frame, with every string hole quoted.</summary>
    public FrameBuilder Line(FrameText line)
    {
        _text.Append(line.ToString()).Append('\n');
        return this;
    }

    /// <summary>A line of this client's own text.</summary>
    public FrameBuilder Line([ConstantExpected] string line)
    {
        _text.Append(line).Append('\n');
        return this;
    }

    /// <summary>Part of a line, with every string hole quoted.</summary>
    public FrameBuilder Append(FrameText text)
    {
        _text.Append(text.ToString());
        return this;
    }

    /// <summary>
    /// A passage, framed as <see cref="Client.Passage.Render"/> frames it: the one way a frame holds
    /// another frame, so a <see cref="Reading"/> is built from passages and never from strings.
    /// </summary>
    public FrameBuilder Passage(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);
        _text.Append(passage.Render());
        return this;
    }

    /// <summary>An empty line.</summary>
    public FrameBuilder Blank()
    {
        _text.Append('\n');
        return this;
    }

    /// <summary>
    /// A post's content as the Forum rendered it: written as served when it is one span the Forum
    /// delimited (R10.12), and otherwise as one literal under a line saying why.
    ///
    /// <para><b>Why the delimiters are checked here.</b> The span is the one thing this client writes
    /// unquoted that it did not compose. It is safe to because its delimiters mark it as data and the
    /// Forum escapes any delimiter inside it (<see cref="Datamarking.Delimit"/>). A span without them,
    /// or with one inside, is text in this client's frame like any other served value, and is quoted
    /// like one.</para>
    /// </summary>
    /// <param name="rendered">The served <c>rendered</c> member.</param>
    /// <param name="indent">Written before every line of the span.</param>
    public FrameBuilder Span(string? rendered, [ConstantExpected] string indent = "")
    {
        ArgumentNullException.ThrowIfNull(indent);

        if (rendered is not null && IsDelimitedSpan(rendered))
        {
            // Verbatim unless indented: the span is the Forum's, and only a caller that asked for an
            // indent has its line breaks rewritten, as the duplicate refusal's answers always were.
            _text.Append(indent).Append(indent.Length == 0 ? rendered : rendered.ReplaceLineEndings("\n" + indent)).Append('\n');
            return this;
        }

        _text.Append(indent)
            .Append("NOT A DELIMITED SPAN: the Forum served this content without the delimiters that mark it as data, so it is shown as one literal")
            .Append('\n');
        _text.Append(indent).Append(DisplayLiteral.Of(rendered)).Append('\n');
        return this;
    }

    /// <summary>Everything written, one line per line.</summary>
    public override string ToString() => _text.ToString();

    /// <summary>
    /// Whether <paramref name="rendered"/> is what <see cref="Datamarking.Delimit"/> produces: the
    /// open delimiter and a line break, content holding neither delimiter, a line break and the close
    /// delimiter.
    /// </summary>
    public static bool IsDelimitedSpan(string? rendered)
    {
        const string open = Datamarking.OpenDelimiter + "\n";
        const string close = "\n" + Datamarking.CloseDelimiter;

        if (rendered is null
            || rendered.Length < open.Length + close.Length
            || !rendered.StartsWith(open, StringComparison.Ordinal)
            || !rendered.EndsWith(close, StringComparison.Ordinal))
            return false;

        var inner = rendered.AsSpan(open.Length, rendered.Length - open.Length - close.Length);
        return !inner.Contains(Datamarking.OpenDelimiter, StringComparison.Ordinal)
            && !inner.Contains(Datamarking.CloseDelimiter, StringComparison.Ordinal);
    }
}
