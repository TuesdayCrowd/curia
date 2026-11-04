using System.Collections.Immutable;
using System.Globalization;

namespace Curia.Client;

/// <summary>
/// The words of the retained head's outcomes, from one definition both clients print (errata G59, R6.61, R11.83): a retained head that does not verify is <i>could not be checked</i> in one of two
/// states, and no cause is claimed; a failure is reported only between two heads that both verify, each named by its role (the retained head and the served head) with both tree sizes, both kids,
/// the retained head's <c>retained_at</c> (an unsigned member of the client's own file, R11.74, <c>NOT STATED</c> where the file records none) and both roots; the lagging replica's verified line
/// is labelled by role. Nothing here verifies anything: <c>PostVerifier</c> verifies the retained head (<c>ActaCheck.HeadSignature</c>) and hands the outcome here to be worded.
///
/// <para>The direction of a computation is never a label. A consistency proof runs from the smaller tree to the larger; which of the two heads is the smaller depends on whether the Forum
/// served a lagging replica, so a line that said "from" and "to" would name the wrong head in the case where it matters most.</para>
/// </summary>
public static class RetainedHeadCheck
{
    /// <summary>
    /// The retained head does not verify under the log's published key set (R6.61), worded for the state it is in: the Forum publishes no key with the head's <c>kid</c> (the key words) or publishes one
    /// and the signature does not hold under it (the signature words). The file <c>head.json</c> is left exactly as it is, in <paramref name="directory"/>, and the words name it and say the lock stays (plan rulings 44 and 45). A <see cref="CheckOutcome.CouldNotCheck"/>, never a failure.
    /// </summary>
    public static Check NotUsable(SignedHeadDocument retained, DateTimeOffset? retainedAt, ImmutableArray<LogJwk> keys, string directory)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(directory);

        var named = $"the head this client retained {Described(retained, retainedAt)}";
        var published = !keys.IsDefaultOrEmpty && keys.Any(k => string.Equals(k.Key.Kid, retained.Kid, StringComparison.Ordinal));
        return Check.CouldNotCheck(published
            ? $"{named} does not verify under the key the log publishes as that kid, so this client cannot use it as evidence, and nothing was compared with it. "
                + "It cannot tell whether the file was changed or damaged, is from another log, or the Forum now publishes other key material under that kid; this check makes no claim about the Forum's history. "
                + $"The file {HeadStore.HeadFile} is left exactly as it is, in {Check.Quote(directory)}. To start again remove {HeadStore.HeadFile} from that directory and nothing else in it ({HeadStore.LockFile} stays): the next verification retains the head the Forum serves, as a first read, and consistency is not checked that once. "
                + $"If you cannot run commands on this host, ask whoever runs it to remove {HeadStore.HeadFile}."
            : $"{named} names a key the log does not publish, though the log publishes every key it has used (R6.50), so nothing was compared. "
                + "The file was changed or damaged, is from another log, or the Forum has stopped publishing a key; this client cannot tell which. "
                + $"It is left exactly as it is: the file {HeadStore.HeadFile}, in {Check.Quote(directory)}; copy {HeadStore.HeadFile} aside before removing it, because a removed file starts the next verification as a first read, and leave {HeadStore.LockFile} where it is. "
                + $"If you cannot run commands on this host, ask whoever runs it to copy and remove {HeadStore.HeadFile}.");
    }

    /// <summary>Two heads that both verify carry different roots at one tree size.</summary>
    internal static Check FailedEqualSizes(SignedHeadDocument retained, DateTimeOffset? retainedAt, SignedHeadDocument served) =>
        Check.Failed(
            $"{Pair(retained, retainedAt, served)} are both signed by the log's published keys and carry different roots at that size: retained {Check.Quote(retained.RootHash)}, served {Check.Quote(served.RootHash)}. "
            + "The log has equivocated (R6.24). The retained head is kept.");

    /// <summary>Two heads that both verify, and a <paramref name="reason"/> the Forum's proof does not connect them (the list R11.83 publishes, each naming a head by its role).</summary>
    internal static Check FailedProof(SignedHeadDocument retained, DateTimeOffset? retainedAt, SignedHeadDocument served, string reason) =>
        Check.Failed(
            $"{Pair(retained, retainedAt, served)} are both signed by the log's published keys, and {reason}. The log has equivocated (R6.24). The retained head is kept.");

    /// <summary>The retained head is the larger and the Forum served an older head that it extends: a lagging replica, not a rewrite.</summary>
    internal static Check LaggingReplica(SignedHeadDocument retained, SignedHeadDocument served) =>
        Check.Verified(
            $"the retained head (tree size {Size(retained)}) extends the head the Forum now serves (tree size {Size(served)}): the Forum served an older head than this client retains, a lagging replica rather than a rewrite; the retained head stands.");

    /// <summary>The Forum served the larger head and it extends the retained one.</summary>
    internal static Check ServedExtends(SignedHeadDocument retained, SignedHeadDocument served) =>
        Check.Verified($"the head the Forum now serves (tree size {Size(served)}) extends the retained head (tree size {Size(retained)})");

    /// <summary>The instant a retained head was stored, or <c>NOT STATED</c> for a file that records none (R11.74).</summary>
    internal static string RetainedAtWords(DateTimeOffset? retainedAt) =>
        retainedAt is { } stored ? stored.UtcDateTime.ToString(HeadStore.InstantFormat, CultureInfo.InvariantCulture) : "NOT STATED";

    /// <summary><c>(tree size n, kid k, retained at t)</c>: the retained head as the lines name it.</summary>
    internal static string Described(SignedHeadDocument retained, DateTimeOffset? retainedAt) =>
        $"(tree size {Size(retained)}, kid {Check.Quote(retained.Kid)}, retained at {RetainedAtWords(retainedAt)})";

    private static string Pair(SignedHeadDocument retained, DateTimeOffset? retainedAt, SignedHeadDocument served) =>
        $"the head this client retained {Described(retained, retainedAt)} and the head the Forum serves (tree size {Size(served)}, kid {Check.Quote(served.Kid)})";

    private static string Size(SignedHeadDocument head) => head.TreeSize.ToString(CultureInfo.InvariantCulture);
}
