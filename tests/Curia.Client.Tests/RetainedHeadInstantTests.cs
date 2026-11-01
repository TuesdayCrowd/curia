using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// WAF16, WAF17, WAF19 and WAF22 (errata G53, R11.74 (a) and (b)): the instant a retained head was stored, the three forms of the equal-head line and its
/// <c>NOT STATED</c> form, the two meanings of "retained at", and the closing of a <c>NOT ESTABLISHED</c> over a leaf past the head. Every line, form and
/// fragment is read from the errata's <c>R11.74 words</c> (<see cref="PublishedClientWords"/>); the oracles of an instant are the instants the test moved its own
/// clock to, never a line's statement of itself. The Forum is a stub (<see cref="StubLog"/>) and the clock a <see cref="TimerManualTimeProvider"/>, so no probe
/// depends on wall-clock speed. The probes that need the real Forum and a real signed head are the Api suite's (<c>WordsAfterFaultKestrelTests</c>).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RetainedHeadInstantTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 30, 0, 123, TimeSpan.Zero);

    private readonly List<string> _homes = [];
    private readonly List<StubLog> _logs = [];

    public void Dispose()
    {
        foreach (var log in _logs) log.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"retained at ([^\s;,)]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RetainedAt();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex MillisecondInstant();

    private string NewHome()
    {
        var home = Directory.CreateTempSubdirectory("curia-retained-instant-").FullName;
        _homes.Add(home);
        return home;
    }

    private StubLog NewLog(int treeSize = 8, int postIndex = 2)
    {
        var log = new StubLog(treeSize: treeSize, postIndex: postIndex);
        _logs.Add(log);
        return log;
    }

    private static string InstantOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static PostVerifier VerifierOn(StubLog log, HeadStore heads, TimeProvider clock) => new(log.Client(), heads, clock, TimeSpan.FromSeconds(30));

    private static async Task<PostVerification> VerifyAsync(PostVerifier verifier)
    {
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, Ct);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }

    private static string Describe(PostVerification verification) => PublishedClientWords.Fold(verification.Consistency.Describe);

    /// <summary>The equal-head line the entry publishes, for a head stored at <paramref name="instant"/> that was <paramref name="when"/> before the check began, at tree size <paramref name="size"/>.</summary>
    private static string ExpectedEqual(string instant, string when, int size) =>
        PublishedClientWords.Fold(PublishedClientWords.EqualHeadLine
            .Replace("<instant>", instant, StringComparison.Ordinal)
            .Replace("<when>", when, StringComparison.Ordinal)
            .Replace("<n>", size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private static async Task<string> RetainedAtOnDiskAsync(HeadStore heads) =>
        JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.json"), Ct))?["retained_at"]?.GetValue<string>() ?? "(no retained_at member)";

    /// <summary>Rewrites the retained file without its <c>retained_at</c> member: the file an older build wrote.</summary>
    private static async Task StripRetainedAtAsync(HeadStore heads)
    {
        var path = Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        node.Remove("retained_at");
        await File.WriteAllTextAsync(path, node.ToJsonString(), Ct);
    }

    // ----- WAF16 -----

    /// <summary>
    /// WAF16. The instant is stored by the one write: <c>Write</c> then <c>ReadRetained</c> returns it, to the millisecond, and <c>head.json</c> holds it as
    /// <c>retained_at</c> in the published form; a file without the member reads as no instant and still reads as a head; an extension replaces the instant and an equal
    /// head leaves it alone.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF16_TheInstantIsStoredByTheOneWriteAndOnlyByIt()
    {
        var problems = new List<string>();

        // The head to store: a head some verification retained (this is the only place the tests get a verified head from).
        var log = NewLog();
        var donor = new HeadStore(NewHome());
        _ = await VerifyAsync(VerifierOn(log, donor, new TimerManualTimeProvider(T0)));
        var head = donor.Read(StubLog.Forum) ?? throw new InvalidOperationException("the donor verification retained no head");

        var heads = new HeadStore(NewHome());
        heads.Write(StubLog.Forum, head, T0);
        var read = heads.ReadRetained(StubLog.Forum);
        if (read is null) problems.Add("ReadRetained returned nothing for a head just written");
        else if (read.Value.RetainedAt != T0) problems.Add($"ReadRetained returned the instant {(read.Value.RetainedAt is { } at ? InstantOf(at) : "null")}, expected {InstantOf(T0)}");

        var onDisk = await RetainedAtOnDiskAsync(heads);
        if (!MillisecondInstant().IsMatch(onDisk) || onDisk != InstantOf(T0)) problems.Add($"retained_at is {onDisk} in head.json, expected {InstantOf(T0)} (the published form {PublishedClientWords.MillisecondInstantExample})");

        // A file with no member: the head reads, the instant is null (the control: the file of an older build).
        var older = new HeadStore(NewHome());
        older.Write(StubLog.Forum, head, T0);
        await StripRetainedAtAsync(older);
        var olderRead = older.ReadRetained(StubLog.Forum);
        if (olderRead is null || olderRead.Value.RetainedAt is not null) problems.Add("a file with no retained_at did not read as a head with no instant");

        // An equal head leaves the instant; an extension replaces it.
        var growing = NewLog(treeSize: 12);
        growing.HeadTreeSize = 8;
        var clock = new TimerManualTimeProvider(T0);
        var home = new HeadStore(NewHome());
        var verifier = VerifierOn(growing, home, clock);
        _ = await VerifyAsync(verifier);
        var first = await RetainedAtOnDiskAsync(home);
        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await VerifyAsync(verifier);
        var afterEqual = await RetainedAtOnDiskAsync(home);
        clock.Advance(TimeSpan.FromSeconds(5));
        growing.HeadTreeSize = 12;
        _ = await VerifyAsync(verifier);
        var afterExtension = await RetainedAtOnDiskAsync(home);
        if (first != InstantOf(T0)) problems.Add($"the first retention stored {first}, expected {InstantOf(T0)}");
        if (afterEqual != first) problems.Add($"an equal head moved the instant from {first} to {afterEqual}");
        if (afterExtension != InstantOf(T0.AddSeconds(10))) problems.Add($"an extension stored {afterExtension}, expected {InstantOf(T0.AddSeconds(10))}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- WAF17 -----

    /// <summary>
    /// WAF17. The equal-head line in its three forms and its <c>NOT STATED</c> form, on a clock that moves: a head stored 10 s before the check began, 0.2 s before,
    /// and (while the lock is held by another check) after it began, and a file with no instant. Each line is the entry's words with the instant the test moved the
    /// clock to, and no line says "retained earlier".
    /// </summary>
    [Fact]
    public async Task R11_74_WAF17_TheEqualHeadLineSaysWhenTheHeadWasStoredInThreeFormsAndNotStated()
    {
        var problems = new List<string>();
        var forms = PublishedClientWords.WhenForms();
        const int Size = 8;

        void Check(string leg, PostVerification verification, string expected)
        {
            var line = Describe(verification);
            if (!line.Contains(expected, StringComparison.Ordinal)) problems.Add($"{leg}: lacks \"{expected}\"; printed \"{line}\"");
            if (line.Contains("retained earlier", StringComparison.Ordinal)) problems.Add($"{leg}: says \"retained earlier\"");
        }

        // A second or more: the first verification stores at T0, the second begins 10 s later.
        {
            var log = NewLog();
            var clock = new TimerManualTimeProvider(T0);
            var verifier = VerifierOn(log, new HeadStore(NewHome()), clock);
            _ = await VerifyAsync(verifier);
            clock.Advance(TimeSpan.FromSeconds(10));
            Check("10 s", await VerifyAsync(verifier), ExpectedEqual(InstantOf(T0), forms[0].Replace("<k>", "10", StringComparison.Ordinal), Size));
        }

        // Less than a second.
        {
            var log = NewLog();
            var clock = new TimerManualTimeProvider(T0);
            var verifier = VerifierOn(log, new HeadStore(NewHome()), clock);
            _ = await VerifyAsync(verifier);
            clock.Advance(TimeSpan.FromMilliseconds(200));
            Check("0.2 s", await VerifyAsync(verifier), ExpectedEqual(InstantOf(T0), forms[1], Size));
        }

        // After the check began: a sibling stores the head while this check waits for the lock.
        {
            var log = NewLog();
            var sibling = new HeadStore(NewHome());
            _ = await VerifyAsync(VerifierOn(log, sibling, new TimerManualTimeProvider(T0)));
            var head = sibling.Read(StubLog.Forum) ?? throw new InvalidOperationException("the sibling retained no head");

            var heads = new HeadStore(NewHome());
            var clock = new TimerManualTimeProvider(T0);
            var held = await heads.LockAsync(StubLog.Forum, TimeSpan.FromSeconds(30), clock, Ct);
            Assert.True(held.TryGetValue(out var lease, out var lockRefusal), lockRefusal?.Detail);

            var verifying = VerifyAsync(VerifierOn(log, heads, clock));
            Assert.True(SpinWait.SpinUntil(() => clock.Armed > 0, TimeSpan.FromSeconds(10)), "the verification never waited on the lock, so the third form was not reached");
            Assert.False(verifying.IsCompleted, "the verification completed while the lock was held");

            clock.Advance(TimeSpan.FromMilliseconds(500));
            heads.Write(StubLog.Forum, head, clock.GetUtcNow());
            lease!.Dispose();

            for (var i = 0; i < 400 && !verifying.IsCompleted; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(60));
                await Task.Delay(5, Ct);
            }

            Check("after began", await verifying.WaitAsync(TimeSpan.FromSeconds(30), Ct), ExpectedEqual(InstantOf(T0.AddMilliseconds(500)), forms[2], Size));
        }

        // A file with no instant (stored by a build before this entry).
        {
            var log = NewLog();
            var heads = new HeadStore(NewHome());
            var clock = new TimerManualTimeProvider(T0);
            var verifier = VerifierOn(log, heads, clock);
            _ = await VerifyAsync(verifier);
            await StripRetainedAtAsync(heads);
            clock.Advance(TimeSpan.FromSeconds(3));

            var expected = PublishedClientWords.Fold(PublishedClientWords.NotStatedLine.Replace("<n>", Size.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            Check("no instant", await VerifyAsync(verifier), expected);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ----- WAF19 -----

    /// <summary>
    /// WAF19, a control (green on the seam tree: the equal line has no "retained at" yet; red under FWAF11's second form). The two meanings of "retained at" are told
    /// apart by form wherever both appear: the token after "retained at" in the extension line is an integer (a tree size) and is unchanged, and the token after
    /// it in an equal line, where the line has one, is an instant and not an integer.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF19_RetainedAtMeansATreeSizeInTheExtensionLineAndAnInstantInTheEqualLine()
    {
        var problems = new List<string>();
        var log = NewLog(treeSize: 12);
        log.HeadTreeSize = 8;
        var clock = new TimerManualTimeProvider(T0);
        var verifier = VerifierOn(log, new HeadStore(NewHome()), clock);
        _ = await VerifyAsync(verifier);

        clock.Advance(TimeSpan.FromSeconds(2));
        var equal = Describe(await VerifyAsync(verifier));
        if (RetainedAt().Match(equal) is { Success: true } equalToken)
        {
            var token = equalToken.Groups[1].Value;
            if (!MillisecondInstant().IsMatch(token)) problems.Add($"the equal line's \"retained at\" is followed by \"{token}\", not an instant");
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        log.HeadTreeSize = 12;
        var extension = Describe(await VerifyAsync(verifier));
        var extensionToken = RetainedAt().Match(extension);
        if (!extensionToken.Success) problems.Add($"the extension line has no \"retained at\": \"{extension}\"");
        else if (!int.TryParse(extensionToken.Groups[1].Value, CultureInfo.InvariantCulture, out var size) || size != 8)
            problems.Add($"the extension line's \"retained at\" is followed by \"{extensionToken.Groups[1].Value}\", not the tree size 8");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- WAF22 -----

    private static string ClosingOf(PostVerification verification)
    {
        var rendered = verification.Render();
        return PublishedClientWords.Fold(rendered[(rendered.LastIndexOf("\n\n", StringComparison.Ordinal) + 2)..]).Trim();
    }

    /// <summary>
    /// WAF22. No sole cause is claimed over another (the plan's ruling 13). (a) A leaf past the served head, a head retained earlier that is smaller, and the
    /// consistency route refusing: the closing holds the published cause with the head's tree size and the published sentence that another check could not be made,
    /// and the cause is the typed fact the verification carries. (b) No head at all: no "newer than the latest signed head", the cause is not carried. (c) A post the
    /// head covers with the key set unreachable: <c>NOT ESTABLISHED</c> with no past-head words. (d) A leaf past the served head, a head retained earlier that is
    /// smaller, and the consistency route serving a valid extension proof: the closing equals the published closing exactly (a whole-string comparison after the
    /// fold, never <c>Contains</c>), so a suffix appended to every closing is red here.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF22_ACauseIsNamedWhereItIsOneAndNeverClaimedAsTheSoleCause()
    {
        var problems = new List<string>();
        const string Newer = "newer than the latest signed head";

        // (a)
        {
            var log = NewLog(treeSize: 12, postIndex: 9);
            log.HeadTreeSize = 4;
            var verifier = VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0));
            _ = await VerifyAsync(verifier);
            log.HeadTreeSize = 6;
            log.ConsistencyRouteRefuses = true;
            var verification = await VerifyAsync(verifier);

            var closing = ClosingOf(verification);
            var expected = PublishedClientWords.Fold(PublishedClientWords.PastHeadClosing.Replace("<n>", "6", StringComparison.Ordinal));
            if (!closing.Contains(expected, StringComparison.Ordinal)) problems.Add($"(a) lacks \"{expected}\"");
            if (!closing.Contains(PublishedClientWords.AnotherCheckSuffix, StringComparison.Ordinal)) problems.Add($"(a) lacks \"{PublishedClientWords.AnotherCheckSuffix}\"");
            if (verification.PastHeadTreeSize != 6) problems.Add($"(a) PastHeadTreeSize is {verification.PastHeadTreeSize?.ToString(CultureInfo.InvariantCulture) ?? "null"}, expected 6");
            if (verification.Consistency.Outcome is not CheckOutcome.CouldNotCheck) problems.Add("(a) the consistency check was not could-not-check, so the leg does not hold the case");
        }

        // (d)
        {
            var log = NewLog(treeSize: 12, postIndex: 9);
            log.HeadTreeSize = 4;
            var verifier = VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0));
            _ = await VerifyAsync(verifier);
            log.HeadTreeSize = 6;
            var verification = await VerifyAsync(verifier);

            var closing = ClosingOf(verification);
            var expected = PublishedClientWords.Fold(PublishedClientWords.PastHeadClosing.Replace("<n>", "6", StringComparison.Ordinal));
            if (closing != expected) problems.Add($"(d) closing is \"{closing}\", expected exactly \"{expected}\"");
            if (verification.PastHeadTreeSize != 6) problems.Add($"(d) PastHeadTreeSize is {verification.PastHeadTreeSize?.ToString(CultureInfo.InvariantCulture) ?? "null"}, expected 6");
            if (verification.Consistency.Outcome is not CheckOutcome.Verified) problems.Add($"(d) the consistency check was {verification.Consistency.Outcome}, not verified, so the leg does not hold the case");
        }

        // (b)
        {
            var log = NewLog();
            log.NoSignedHead = true;
            var verification = await VerifyAsync(VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0)));
            var closing = ClosingOf(verification);
            if (!closing.StartsWith("NOT ESTABLISHED", StringComparison.Ordinal)) problems.Add($"(b) closing is not NOT ESTABLISHED: \"{closing}\"");
            if (closing.Contains(Newer, StringComparison.Ordinal)) problems.Add("(b) says the post is newer than the latest signed head where there is no head");
            if (verification.PastHeadTreeSize is not null) problems.Add("(b) carries a past-head tree size where there is no head");
        }

        // (c)
        {
            var log = NewLog();
            log.JwksUnreachable = true;
            var verification = await VerifyAsync(VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0)));
            var closing = ClosingOf(verification);
            if (!closing.StartsWith("NOT ESTABLISHED", StringComparison.Ordinal)) problems.Add($"(c) closing is not NOT ESTABLISHED: \"{closing}\"");
            if (closing.Contains(Newer, StringComparison.Ordinal)) problems.Add("(c) says the post is newer than the latest signed head where the head covers it");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// WAF37. The suffix follows the verdict's predicate (errata G53, R11.74 (b), ruling 19): a table over the four consistency outcomes of a post past the head.
    /// Verified: no suffix. Nothing to compare (a first verification from a home that retained no head): no suffix, because <c>Overall</c> does not count it against
    /// the verdict. Could not be checked proper (a retained head and the route refusing): the suffix. Failed: the <c>FAILED.</c> closing, with no past-head words and
    /// no suffix. The closing is compared whole, after the fold.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF37_TheSuffixFollowsTheVerdictsPredicateOverTheFourConsistencyOutcomes()
    {
        var problems = new List<string>();
        var published = PublishedClientWords.Fold(PublishedClientWords.PastHeadClosing.Replace("<n>", "6", StringComparison.Ordinal));
        var withSuffix = published + " " + PublishedClientWords.AnotherCheckSuffix.Trim();

        void Check(string leg, PostVerification verification, string? expectedClosing, CheckOutcome expectedConsistency, bool nothingToCompare)
        {
            if (verification.Consistency.Outcome != expectedConsistency || verification.Consistency.NothingToCompare != nothingToCompare)
                problems.Add($"{leg}: consistency is {verification.Consistency.Outcome} (nothing to compare: {verification.Consistency.NothingToCompare}), so the leg does not hold the case");
            var closing = ClosingOf(verification);
            if (expectedClosing is null)
            {
                if (!closing.StartsWith("FAILED.", StringComparison.Ordinal)) problems.Add($"{leg}: closing is not the FAILED line: \"{closing}\"");
                if (closing.Contains(PublishedClientWords.AnotherCheckSuffix, StringComparison.Ordinal)) problems.Add($"{leg}: the FAILED closing carries the suffix");
            }
            else if (closing != expectedClosing) problems.Add($"{leg}: closing is \"{closing}\", expected exactly \"{expectedClosing}\"");
        }

        // Verified: a retained smaller head and a valid extension proof.
        {
            var log = NewLog(treeSize: 12, postIndex: 9);
            log.HeadTreeSize = 4;
            var verifier = VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0));
            _ = await VerifyAsync(verifier);
            log.HeadTreeSize = 6;
            Check("verified", await VerifyAsync(verifier), published, CheckOutcome.Verified, nothingToCompare: false);
        }

        // Nothing to compare: the first verification from a home with no retained head.
        {
            var log = NewLog(treeSize: 12, postIndex: 9);
            log.HeadTreeSize = 6;
            Check("nothing to compare", await VerifyAsync(VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0))), published, CheckOutcome.CouldNotCheck, nothingToCompare: true);
        }

        // Could not be checked proper: a retained smaller head and the route refusing.
        {
            var log = NewLog(treeSize: 12, postIndex: 9);
            log.HeadTreeSize = 4;
            var verifier = VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0));
            _ = await VerifyAsync(verifier);
            log.HeadTreeSize = 6;
            log.ConsistencyRouteRefuses = true;
            Check("could not be checked", await VerifyAsync(verifier), withSuffix, CheckOutcome.CouldNotCheck, nothingToCompare: false);
        }

        // Failed: a retained smaller head and a served head whose root the retained head does not extend to.
        {
            var log = NewLog(treeSize: 12, postIndex: 9);
            log.HeadTreeSize = 4;
            var verifier = VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0));
            _ = await VerifyAsync(verifier);
            log.HeadTreeSize = 6;
            log.HeadCommitsToTheWrongRoot = true;
            Check("failed", await VerifyAsync(verifier), null, CheckOutcome.Failed, nothingToCompare: false);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// WAF16(b). A <c>retained_at</c> that is not in the published form reads as no instant and the head still reads: a number, a word that is not an instant, and an
    /// instant without its milliseconds. The line says <c>NOT STATED</c> for each, in the published words with the tree size.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF16b_AMalformedInstantReadsAsNotStated()
    {
        var problems = new List<string>();
        var expected = PublishedClientWords.Fold(PublishedClientWords.NotStatedLine.Replace("<n>", "8", StringComparison.Ordinal));

        foreach (var member in new[] { "5", "\"yesterday\"", "\"2026-11-01T09:30:00Z\"" })
        {
            var log = NewLog();
            var heads = new HeadStore(NewHome());
            var clock = new TimerManualTimeProvider(T0);
            var verifier = VerifierOn(log, heads, clock);
            _ = await VerifyAsync(verifier);

            var path = Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.json");
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
            node["retained_at"] = JsonNode.Parse(member);
            await File.WriteAllTextAsync(path, node.ToJsonString(), Ct);

            var read = heads.ReadRetained(StubLog.Forum);
            if (read is null || read.Value.RetainedAt is not null) problems.Add($"retained_at {member}: did not read as a head with no instant");

            clock.Advance(TimeSpan.FromSeconds(3));
            var line = Describe(await VerifyAsync(verifier));
            if (!line.Contains(expected, StringComparison.Ordinal)) problems.Add($"retained_at {member}: lacks \"{expected}\"; printed \"{line}\"");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// WAF23, a control (green on the seam tree; red under FWAF14 and FWAF16). A failed check is not softened: a post past the head whose signature does not verify
    /// closes <c>FAILED.</c> in the words it always had, with no past-head words and no cause carried.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF23_AFailedSignatureOnAPostPastTheHeadIsNotSoftened()
    {
        var log = NewLog(treeSize: 12, postIndex: 9);
        log.HeadTreeSize = 4;
        log.ServedSignature = log.Answer.Signature;

        var verification = await VerifyAsync(VerifierOn(log, new HeadStore(NewHome()), new TimerManualTimeProvider(T0)));
        var closing = ClosingOf(verification);
        var problems = new List<string>();

        if (!closing.StartsWith("FAILED. At least one check ran and did not hold.", StringComparison.Ordinal)) problems.Add($"the closing is not the FAILED line: \"{closing}\"");
        if (closing.Contains("newer than the latest signed head", StringComparison.Ordinal)) problems.Add("the FAILED closing names the past-head cause");
        if (verification.PastHeadTreeSize is not null) problems.Add("a failed verification carries a past-head tree size");
        if (verification.Overall is not CheckOutcome.Failed) problems.Add($"the verdict is {verification.Overall}, not failed");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
