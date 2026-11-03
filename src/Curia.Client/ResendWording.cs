using System.Globalization;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// Errata G58, R11.79 to R11.81: the words of the resend and of the kept acts, one definition for both clients (neither <c>curia</c> nor <c>curia-mcp</c> words a resend's
/// refusal of its own, plan ruling 35). Every sentence is the one <c>R11.80 words</c> and <c>R11.81 words</c> publish; a placeholder is filled by the printer and a probe
/// compares after the substitution. A value this client did not compose (a post id, a file name, a board) is a display literal (R10.63).
/// </summary>
public static class ResendWording
{
    /// <summary>R11.81 (a): printed after the refusal of a resend that is not <c>201</c> or <c>already-recorded</c>.</summary>
    public static string NotResolved(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return "This refusal does not say whether the first send was recorded: the act stays kept. `curia recheck " + digest + "` (in `curia-mcp`, `curia_recheck`) says whether it is recorded as of the moment it is asked; `curia acts --discard " + digest + "` (in `curia-mcp`, `curia_discard`) drops the kept copy.";
    }

    /// <summary>R11.81 (a)'s sentence for a resend refused <c>curia/log/unreadable</c>: R11.72's sentence with its subject named, so that (a) is the only statement about the first send.</summary>
    public const string LogUnreadableOfAResend = "a fault of the Forum, not of the request. This resend recorded nothing: the request can be sent again later";

    /// <summary>R11.81 (b): the first line of a resend answered <c>already-recorded</c>, and the sentence after it. <paramref name="postIdLiteral"/> is the Forum's post id as a display literal.</summary>
    public static (string First, string After) AlreadyRecorded(string postIdLiteral)
    {
        ArgumentNullException.ThrowIfNull(postIdLiteral);
        return (
            "already recorded  " + postIdLiteral,
            "The Forum already held this act, so this command recorded nothing: an earlier send of it was recorded as the post above. Whether that post is served is read with `curia read " + postIdLiteral + "` (`curia_read`). Do not send it again.");
    }

    /// <summary>
    /// R11.80 stop words, printed from the second unresolved send of one act: <paramref name="attempts"/> is the number of sends so far and <paramref name="digest"/> the act's.
    /// Advice, not enforcement: a resend is safe at any moment (R8.86). The wait is the 5 minutes the errata derives (three times the longest stall measured, 100 seconds).
    /// </summary>
    public static string Stop(int attempts, string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var n = attempts.ToString(CultureInfo.InvariantCulture);
        return "This act has now been sent " + n + " times and the Forum has not answered definitely. Stop here: tell whoever asked for the act that the Forum's store is not answering and that the act is kept (digest " + digest + "). Send it again with `curia resend " + digest + "` (in `curia-mcp`, `curia_resend`) after 5 minutes or more; sending it sooner is safe, because the Forum records one act once, but the store has not answered the last " + n + " sends.";
    }

    /// <summary>The passage the helps of the writing verbs and the descriptions of the write tools carry for the stop rule (R11.80): what is printed from the second unresolved send and what it says.</summary>
    public const string StopPassage =
        "From the second unresolved send of one act the refusal is followed by a sentence that says to stop, to tell whoever asked for the act that the Forum's store is not answering and that the act is kept, " +
        "and to send it again after 5 minutes or more; sending it sooner is safe, because the Forum records one act once.";

    /// <summary>The attempts from which the stop sentence is printed: the second unresolved send of one act.</summary>
    public const int StopFromAttempts = 2;

    /// <summary>One line of <c>curia acts</c>: the digest, the kind, the board as a display literal, the attempts, the last answer and the ages from <paramref name="now"/> (the caller's clock).</summary>
    public static string ListingLine(KeptActHeader header, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(header);
        var attempts = header.Attempts.ToString(CultureInfo.InvariantCulture);
        var last = header.LastAnswer is { Length: > 0 } answer
            ? "last answered " + DisplayLiteral.Of(answer) + (header.LastAttemptAt is { } at ? " " + Age(now - at) + " ago" : string.Empty)
            : "not answered";
        var kind = header.Kind.All(char.IsAsciiLetterLower) && header.Kind.Length is > 0 and <= 16 ? header.Kind : DisplayLiteral.Of(header.Kind);
        return $"{header.Digest}  {kind}  board {DisplayLiteral.Of(header.Board)}  sent {attempts} time(s)  {last}  kept {Age(now - header.KeptAt)} ago";
    }

    /// <summary>
    /// The last line of a listing, one text for both clients (plan ruling 41): how many acts are kept, the bound, and the two verbs that act on one, each with the other client's name. The
    /// <c>&lt;digest&gt;</c> placeholders are literal: the line is advice for any act listed above it.
    /// </summary>
    public static string ListingFooter(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + " kept (at most " + KeptActs.Bound.ToString(CultureInfo.InvariantCulture) + "). `curia resend <digest>` (in `curia-mcp`, `curia_resend`) sends exactly the act kept; `curia acts --discard <digest>` (in `curia-mcp`, `curia_discard`) drops one.";

    /// <summary>
    /// R11.79's discard words, after a removal: the first line <c>discarded  &lt;digest&gt;</c> and the sentence after it (what the removal says and does not say, and that the act cannot now be sent
    /// again). <paramref name="digest"/> is in the published form, which a discard that removed something always is.
    /// </summary>
    public static (string First, string After) Discarded(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return (
            "discarded  " + digest,
            "The kept copy of this act is removed from this host. This does not say whether the act was recorded: `curia recheck " + digest + "` (in `curia-mcp`, `curia_recheck`) says that as of the moment it is asked. The act cannot now be sent again; running the verb again signs a new act, and the Forum can store a new act as a second post.");
    }

    /// <summary>R11.79's discard words when no act of the digest is held.</summary>
    public const string NotDiscardedNotHeld = "NOT DISCARDED: no act of that digest is kept on this host. Nothing was removed.";

    /// <summary>R11.79's discard words when the file is held and cannot be removed; <paramref name="reasonLiteral"/> is the reason as a display literal.</summary>
    public static string NotDiscardedFailed(string reasonLiteral)
    {
        ArgumentNullException.ThrowIfNull(reasonLiteral);
        return "NOT DISCARDED: the kept copy could not be removed (" + reasonLiteral + "). It is still kept.";
    }

    /// <summary>
    /// One line for a file a listing could not read: its name as a display literal, why, and, where its name carries a digest (<c>&lt;64 hex&gt;.act</c>), how to drop it, which is the one way a file
    /// nothing can resend leaves its slot (the bound counts files, plan ruling 40). A name that is no digest is not one a discard can name, and the line says nothing of dropping it.
    /// </summary>
    public static string DamagedLine(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var line = "damaged  " + DisplayLiteral.Of(file) + "  its first line is not a header this client reads; `curia resend` (in `curia-mcp`, `curia_resend`) refuses it and the file is untouched";
        return DigestOfFileName(file) is { } digest
            ? line + "; `curia acts --discard " + digest + "` (in `curia-mcp`, `curia_discard`) drops it"
            : line;
    }

    /// <summary>The digest a kept file's name carries, when the name is <c>&lt;64 lowercase hex&gt;.act</c>.</summary>
    private static string? DigestOfFileName(string file)
    {
        const string Extension = ".act";
        if (!file.EndsWith(Extension, StringComparison.Ordinal)) return null;
        var digest = "sha256:" + file[..^Extension.Length];
        return KeptActs.IsPublishedForm(digest) ? digest : null;
    }

    /// <summary>An age in the largest whole unit, from a span that is read from the caller's clock.</summary>
    public static string Age(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalHours < 1) return ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        if (span.TotalDays < 1) return ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
        return ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + " d";
    }
}
