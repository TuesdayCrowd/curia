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
/// <para>The way to put a string of the client's choosing into a frame unquoted, and so the thing to
/// look for when reviewing what a frame prints: every use says "this is mine", and a use that wraps
/// a value the Forum served, the log recorded or an agent named is the defect R10.63 (errata G17)
/// forbids. <see cref="FrameBuilder"/> lists every other way a frame writes what it did not quote.
/// A <see cref="FrameText"/> built by hand is one of them, so a review greps for
/// <c>new FrameText(</c> as for this type.</para>
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
/// is read as an option rather than as a value. <see cref="TryOf"/> refuses each of these. No honest
/// value holds any of them: a post id is a ULID, a cursor is base64, and an entity tag is a quoted
/// digest in hex.</para>
///
/// <para><b>Only <see cref="TryOf"/> makes one</b> (Task 4's review, m3). The constructor is private
/// and this is a class, so a refused value leaves <see langword="null"/> behind, not a word holding
/// nothing that would print as <c>''</c>: a caller that uses the word without asking whether it
/// got one is a nullable warning, which this build makes an error, and a <see cref="FrameText"/>
/// hole handed none throws. What a command does without its word is the caller's; R10.65 says it
/// is not printed as a command.</para>
/// </summary>
public sealed record ShellWord
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
    public static bool TryOf(string? value, [NotNullWhen(true)] out ShellWord? word)
    {
        word = null;
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
/// <para><b>Every string hole is quoted, and every character.</b> A string interpolated into a frame
/// is a value this client did not write until something says otherwise, so it is written as
/// <see cref="DisplayLiteral"/> writes it: a JSON string literal that cannot end the line it sits on,
/// begin another, or reorder the text around it, and padded to its column only after it is quoted.
/// A <see cref="char"/> or a <see cref="Rune"/> is quoted the same way, since a line break is one
/// character: bare, with an alignment, with a format, or absent-or-present. Each of those forms has
/// an overload of its own here, which C# prefers to the generic ones below; without them a character
/// with an alignment or a format, a <c>char?</c> and a <see cref="Rune"/> were taken by the generic
/// ones and written as they are (Task 4's review, I1). The client's own words go in through
/// <see cref="OwnText"/>, and a value in a command the reader may run through
/// <see cref="ShellWord"/>.</para>
///
/// <para><b>What else compiles, and what does not.</b> A struct that formats itself
/// (<see cref="IFormattable"/>) -- a number, an instant, an enum -- goes in as itself, formatted
/// invariantly, in a format that must be a constant; absent, it is <see cref="DisplayLiteral.Absent"/>.
/// Of the BCL's, only a character and a rune hold text a stranger chose, and they are quoted above;
/// an enum's text is the name of one of its members. No struct in this repository implements
/// <see cref="IFormattable"/>: one that wrapped a served string would print it here unquoted.
/// Anything else has no overload here and does not compile, so each such hole is a decision made
/// where it is written: a class -- a <see cref="Uri"/>, a record, a list -- (CS0453); a struct that
/// does not format itself -- a <see cref="bool"/>, an <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// -- (CS0315); a span (CS9244); a string or an <see cref="OwnText"/> with a format; and an absent
/// number or character with an alignment or a format.</para>
///
/// <para><b>Built by hand, it writes what it is given.</b> Its constructor and
/// <see cref="AppendLiteral"/> are public because the compiler calls them for every interpolation,
/// and a call written by hand is not an interpolation. So <see cref="AppendLiteral"/>'s text and
/// every format must be constants (<see cref="ConstantExpectedAttribute"/>, CA1857 at a call), and a
/// review greps for <c>new FrameText(</c> as for <see cref="OwnText"/> (Task 4's review, m2).</para>
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
    public void AppendLiteral([ConstantExpected] string value) => _text.Append(value);

    /// <summary>A value this client did not write: quoted.</summary>
    public void AppendFormatted(string? value) => _text.Append(DisplayLiteral.Of(value));

    /// <summary>A value this client did not write, quoted and then padded to a column.</summary>
    public void AppendFormatted(string? value, int alignment) => Pad(DisplayLiteral.Of(value), alignment);

    /// <summary>A single character is a value too, and a line break is one character: quoted.</summary>
    public void AppendFormatted(char value) => _text.Append(DisplayLiteral.Of(value.ToString()));

    /// <summary>A character, quoted and then padded to a column.</summary>
    public void AppendFormatted(char value, int alignment) => Pad(DisplayLiteral.Of(value.ToString()), alignment);

    /// <summary>A character in a format, which a character ignores: quoted.</summary>
    public void AppendFormatted(char value, [ConstantExpected] string? format) =>
        _text.Append(DisplayLiteral.Of(((IFormattable)value).ToString(format, CultureInfo.InvariantCulture)));

    /// <summary>A character that may be absent: quoted, or <see cref="DisplayLiteral.Absent"/>.</summary>
    public void AppendFormatted(char? value) =>
        _text.Append(value is { } present ? DisplayLiteral.Of(present.ToString()) : DisplayLiteral.Absent);

    /// <summary>A scalar value, which may be two UTF-16 code units, as a tag character is: quoted.</summary>
    public void AppendFormatted(Rune value) => _text.Append(DisplayLiteral.Of(value.ToString()));

    /// <summary>A scalar value, quoted and then padded to a column.</summary>
    public void AppendFormatted(Rune value, int alignment) => Pad(DisplayLiteral.Of(value.ToString()), alignment);

    /// <summary>A scalar value in a format, which a scalar value ignores: quoted.</summary>
    public void AppendFormatted(Rune value, [ConstantExpected] string? format) =>
        _text.Append(DisplayLiteral.Of(((IFormattable)value).ToString(format, CultureInfo.InvariantCulture)));

    /// <summary>A scalar value that may be absent: quoted, or <see cref="DisplayLiteral.Absent"/>.</summary>
    public void AppendFormatted(Rune? value) =>
        _text.Append(value is { } present ? DisplayLiteral.Of(present.ToString()) : DisplayLiteral.Absent);

    /// <summary>The client's own words.</summary>
    public void AppendFormatted(OwnText value) => _text.Append(value.Text);

    /// <summary>The client's own words, padded to a column.</summary>
    public void AppendFormatted(OwnText value, int alignment) => Pad(value.Text, alignment);

    /// <summary>
    /// A value in a command the reader may run: single-quoted, as <see cref="ShellWord"/> admits it.
    /// Handed no word -- a refused value leaves none -- it throws rather than print an empty one.
    /// </summary>
    public void AppendFormatted(ShellWord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _text.Append(value.ToString());
    }

    /// <summary>A number, an enum or an instant, formatted invariantly.</summary>
    public void AppendFormatted<T>(T value)
        where T : struct, IFormattable =>
        _text.Append(value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary>A number, an enum or an instant, in the format asked for, which must be a constant.</summary>
    public void AppendFormatted<T>(T value, [ConstantExpected] string? format)
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
/// <para><b>What a frame writes that it did not quote, and why no stranger's words are among
/// it</b> (R10.63, errata G17; Task 4's review listed each way):</para>
/// <list type="bullet">
/// <item>Constants. <see cref="Line(string)"/> and <see cref="Span"/>'s indent take only a constant
/// (CA1857 at every call). A method-group conversion is not a call and would pass any string, so
/// <c>ConstantArgumentTests</c> fails on one in any assembly built from <c>src/</c>.</item>
/// <item>The literal parts of an interpolation, which are this client's source text. A
/// <see cref="FrameText"/> built by hand writes what it is given; its literal text and its formats
/// must be constants, and a review greps for <c>new FrameText(</c>.</item>
/// <item><see cref="OwnText"/>, each use a claim that the words are the client's. Two carry sentences
/// composed elsewhere: <see cref="SignatureVerdict.Detail"/>, through
/// <see cref="SignatureVerdict.Describe"/>, and <see cref="Refusal.Summary"/>. Every verdict in
/// <c>src/</c> is composed in <see cref="SignatureCheck"/>, whose details are its own sentences with
/// each value they name quoted (an error's detail quoted whole), and a summary quotes the problem
/// document's words where it is composed. A new place that composes either is an
/// <see cref="OwnText"/> by another name, and is reviewed as one.</item>
/// <item>A <see cref="ShellWord"/>: printable ASCII between single quotation marks, which cannot end
/// a line.</item>
/// <item>A number, an enum or an instant, formatted invariantly (see <see cref="FrameText"/>).</item>
/// <item>The Forum's span, once <see cref="IsDelimitedSpan"/> says the Forum delimited it.</item>
/// <item>Another passage's frame, through <c>Passage</c>, built the same way.</item>
/// </list>
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
