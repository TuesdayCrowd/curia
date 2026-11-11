using System.Globalization;

namespace Curia.Client;

/// <summary>
/// Errata G67, R11.94: the words a verification past the latest signed head says about when that head was signed. Pure: the head's own signed timestamp and
/// the instant the check began in, the client's own rendering out, and never the signed string itself. Both clients (<c>curia verify</c> and <c>curia_verify</c>)
/// read the one definition.
///
/// <para><b>The seam of PR H's Task 1:</b> the closing past the head is the one the clients print today, the clause and the inclusion sentence are empty, and
/// the timestamp and the clock are carried and ignored. Task 5 writes the words.</para>
/// </summary>
internal static class HeadSignedWords
{
    /// <summary>The <c>&lt;signed&gt;</c> clause of R11.94 words, for the head timestamp <paramref name="headTimestamp"/> and the instant the check began.</summary>
    internal static string Signed(string? headTimestamp, DateTimeOffset checkBegan) => string.Empty;

    /// <summary>The sentence <c>That head was &lt;signed&gt;.</c> the inclusion line holds between its first sentence and its second.</summary>
    internal static string InclusionSentence(string? headTimestamp, DateTimeOffset checkBegan) => string.Empty;

    /// <summary>
    /// The closing line of a verification whose post lies past the latest head and in which no check failed: R11.94 words, whose ending past the head is
    /// <c>Another check could not be made: read the lines above.</c> where <paramref name="anotherCheckCouldNotBeMade"/> and not otherwise.
    /// </summary>
    internal static string Closing(long treeSize, string? headTimestamp, DateTimeOffset checkBegan, bool anotherCheckCouldNotBeMade) =>
        "NOT ESTABLISHED. Nothing failed: the post is newer than the latest signed head (tree size "
        + treeSize.ToString(CultureInfo.InvariantCulture)
        + "), so its inclusion could not be checked, and the key and lifecycle checks, which need it, were not run. Verify again after the operator signs a head."
        + (anotherCheckCouldNotBeMade ? " Another check could not be made: read the lines above." : string.Empty);
}
