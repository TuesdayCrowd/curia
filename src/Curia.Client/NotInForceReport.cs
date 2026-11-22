using System.Diagnostics.CodeAnalysis;

namespace Curia.Client;

/// <summary>
/// The words <c>curia rotate</c> and <c>curia retire</c> print when the Forum answers <c>500</c> <c>curia/keys/not-honoured</c> (errata G73, R11.98 words): the Forum recorded the act and does not act on it, and
/// the profile has recorded it as made and not in force. Written once, here; the CLI and nothing else calls it. Every value the Forum served is written as a display literal, a kid as <c>curia keys</c> writes it,
/// and the retire command only where each value is a shell word (R10.65).
/// </summary>
public static class NotInForceReport
{
    /// <summary>The problem type a route answers when it appended a lifecycle row the Forum does not honour (R4.42, R4.44).</summary>
    public const string NotHonouredType = "curia/keys/not-honoured";

    /// <summary>Whether the refusal is the answer R11.98 reads: a <c>500</c> of the type <see cref="NotHonouredType"/>. Every other refusal of a rotation or a retirement leaves the profile as it was.</summary>
    public static bool Answers(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal.Status == 500 && string.Equals(refusal.Error.Type, NotHonouredType, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row's log index the answer named (R4.44): its <c>log_index</c>, when that is a whole number of at least 0 and within a <see cref="long"/>; null otherwise (absent, negative, fractional, a string, null, or
    /// beyond a <see cref="long"/>), in which case no act is recorded.
    /// </summary>
    public static long? LogIndexOf(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        // 2^63 is the first double no long holds: a bound written as the double, so no value is narrowed that does not fit.
        const double beyondLong = 9223372036854775808.0;
        return refusal.Document is Curia.Canon.Json.JsonValue.Object document
            && ClientJson.Number(document, "log_index") is { } value
            && double.IsInteger(value)
            && value is >= 0 and < beyondLong
                ? (long)value
                : null;
    }

    /// <summary>
    /// The command that retires <paramref name="kid"/>, as <c>curia rotate</c> prints it (spec section 4.9): the agent and the key each go in only as a shell word that does not begin with a quotation mark
    /// (R10.65, R10.66), and a value that is not one leaves the command unprinted, with the line saying where the values are instead. The same text the CLI's own hint writes; this assembly cannot reach
    /// that one, so a test compares the two.
    /// </summary>
    public static OwnText RetireCommand(string slug, string kid)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(kid);

        return TryName(slug, out var agent) && TryName(kid, out var key)
            ? new OwnText(new FrameBuilder().Append($"curia retire --agent {agent} --kid {key}").ToString())
            : new OwnText(new FrameBuilder().Append($"curia retire and the agent and key named above ({new OwnText(PrintedCommand.Withheld)})").ToString());
    }

    /// <summary>
    /// The command that completes a rotation this profile sent and did not hear an answer to, as <c>curia keys</c> prints it (errata G74, R11.100 words): the agent goes in as a bare word when it is made of letters, digits, dots,
    /// underscores and hyphens (a word no shell reads as anything but itself), as a shell word (R10.65, R10.66) otherwise, and the command is not printed when it is neither.
    /// </summary>
    public static OwnText RotateCommand(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);

        if (slug.Length > 0 && slug[0] != '-' && slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            return new OwnText("curia rotate --agent " + slug);

        return TryName(slug, out var agent)
            ? new OwnText(new FrameBuilder().Append($"curia rotate --agent {agent}").ToString())
            : new OwnText(new FrameBuilder().Append($"curia rotate and the agent named above ({new OwnText(PrintedCommand.Withheld)})").ToString());
    }

    /// <summary>
    /// The words for a rotation the Forum recorded and does not act on: <paramref name="newKid"/> is the key the rotation generated (kept, not bound) and <paramref name="kid"/> the key the profile still signs
    /// with. <paramref name="logIndex"/> is the index the profile recorded the act at, or null when the Forum named none the client can use (no act was recorded).
    /// </summary>
    public static FrameBuilder Rotation(string slug, string kid, string newKid, long? logIndex)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(newKid);

        var frame = new FrameBuilder();
        if (logIndex is { } index)
            frame.Line($"not in force   the Forum recorded this rotation at log index {index} and does not act on it (curia/keys/not-honoured): key {newKid} is not bound, and this profile still signs with {kid}.");
        else
            frame.Line($"not in force   the Forum recorded this rotation and did not say at which log index, so this profile records no act and does not act on it (curia/keys/not-honoured): key {newKid} is not bound, and this profile still signs with {kid}.");

        frame.Line($"The row stays in the log. This profile keeps key {newKid}; if the Forum later acts on the row, that key becomes live, and this command ends it:");
        frame.Line($"{RetireCommand(slug, newKid)}");
        frame.Line("Report it to the Forum's operator before you rotate or retire again: an act made now links from the Forum's present lifecycle, which acting on this row would change.");
        return frame;
    }

    /// <summary>
    /// The words for a retirement of <paramref name="kid"/> the Forum recorded and does not act on. <paramref name="logIndex"/> is the index the profile recorded the act at, or null when the Forum named none the
    /// client can use (no act was recorded).
    /// </summary>
    public static FrameBuilder Retirement(string kid, long? logIndex)
    {
        ArgumentNullException.ThrowIfNull(kid);

        var frame = new FrameBuilder();
        if (logIndex is { } index)
            frame.Line($"not in force   the Forum recorded the retirement of key {kid} at log index {index} and does not act on it (curia/keys/not-honoured): {kid} is still live.");
        else
            frame.Line($"not in force   the Forum recorded the retirement of key {kid} and did not say at which log index, so this profile records no act and does not act on it (curia/keys/not-honoured): {kid} is still live.");

        frame.Line($"The row stays in the log, and if the Forum later acts on it, {kid} is ended. Report it to the Forum's operator before you rotate or retire again: an act made now links from the Forum's present lifecycle, which acting on this row would change.");
        return frame;
    }

    private static bool TryName(string value, [NotNullWhen(true)] out ShellWord? word)
    {
        word = null;
        return !value.StartsWith('"') && ShellWord.TryOf(value, out word);
    }
}
