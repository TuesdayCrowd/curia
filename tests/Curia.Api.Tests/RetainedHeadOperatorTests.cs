#pragma warning disable CA1849 // the probes read and write small files in a temporary directory; the file calls are the fixture
#pragma warning disable CA2000 // each lagging client is a using declaration or handed to a using; the analyzer loses the ownership of a handler the client owns

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using CliProgram = Curia.Client.Cli.Program;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Two real Forums over two databases and the log keys <c>curia-operator sign-head</c> signs with (errata G59, plan Task 0 M4 and M6, ruling 28 and ruling 11 of the plan's finding list): the world every
/// retained-head probe runs in. Forum A and Forum B perform the same operations by the same agent (so their logs have the same number of leaves at each step) with different text (so the roots
/// differ), and the operator signs heads with <c>KeyPem</c> and, at the third step, <c>OtherKeyPem</c> (so Forum A publishes both kids). Forum B also signs one head with <c>ThirdKeyPem</c>, a key Forum A
/// never publishes. A head's own signature is real everywhere: nothing here is a stub of the verifier's input, and an edited head is a real head edited (trap 148).
/// </summary>
[SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "A test world: plain data read by the probes.")]
public sealed class HeadWorld : IAsyncLifetime
{
    internal static readonly string KeyPem = LogSigningKey.GeneratePem();
    internal static readonly string OtherKeyPem = LogSigningKey.GeneratePem();
    internal static readonly string ThirdKeyPem = LogSigningKey.GeneratePem();

    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    internal ForumFixture A { get; } = new();

    internal ForumFixture B { get; } = new();

    /// <summary>Forum A's heads at three sizes (the first two signed with <see cref="KeyPem"/>, the third with <see cref="OtherKeyPem"/>), and the first one's served bytes.</summary>
    internal SignedHeadDocument A1 { get; private set; } = null!;

    internal SignedHeadDocument A2 { get; private set; } = null!;

    internal SignedHeadDocument A3 { get; private set; } = null!;

    internal string A1Raw { get; private set; } = string.Empty;

    /// <summary>Forum B's heads at the same three sizes, signed by the same keys in the same order, with other roots; <c>B4</c> is one more, signed by <see cref="ThirdKeyPem"/>.</summary>
    internal SignedHeadDocument B1 { get; private set; } = null!;

    internal SignedHeadDocument B2 { get; private set; } = null!;

    internal SignedHeadDocument B3 { get; private set; } = null!;

    internal SignedHeadDocument B4 { get; private set; } = null!;

    /// <summary>The first post of Forum A: covered by every head of A.</summary>
    internal string PostId { get; private set; } = string.Empty;

    internal string KeyKid { get; private set; } = string.Empty;

    internal string OtherKid { get; private set; } = string.Empty;

    internal Uri Origin { get; private set; } = new("http://localhost/");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await ((IAsyncLifetime)A).InitializeAsync();
        await ((IAsyncLifetime)B).InitializeAsync();
        Origin = A.Client.BaseAddress!;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/hw-" + suffix, "hw-" + suffix);
        var onA = await agent.AuthenticateAsync(A.Client, TokenEndpoint, A.Now, ct);
        var onB = await agent.AuthenticateAsync(B.Client, TokenEndpoint, B.Now, ct);

        PostId = await PostAsync(A, agent, onA, "a-first", ct);
        await PostAsync(B, agent, onB, "b-first", ct);
        await SignAsync(A, KeyPem, ct);
        await SignAsync(B, KeyPem, ct);
        A1 = await HeadAsync(A, ct);
        A1Raw = await RawHeadAsync(A, ct);
        B1 = await HeadAsync(B, ct);

        await PostAsync(A, agent, onA, "a-second", ct);
        await PostAsync(B, agent, onB, "b-second", ct);
        await SignAsync(A, KeyPem, ct);
        await SignAsync(B, KeyPem, ct);
        A2 = await HeadAsync(A, ct);
        B2 = await HeadAsync(B, ct);

        await PostAsync(A, agent, onA, "a-third", ct);
        await PostAsync(B, agent, onB, "b-third", ct);
        await SignAsync(A, OtherKeyPem, ct);
        await SignAsync(B, OtherKeyPem, ct);
        A3 = await HeadAsync(A, ct);
        B3 = await HeadAsync(B, ct);

        await SignAsync(B, ThirdKeyPem, ct);
        B4 = await HeadAsync(B, ct);

        KeyKid = A1.Kid;
        OtherKid = A3.Kid;
        Assert.NotEqual(KeyKid, OtherKid);
    }

    public async ValueTask DisposeAsync()
    {
        await A.DisposeAsync();
        await B.DisposeAsync();
    }

    private static async Task<string> PostAsync(ForumFixture forum, ForumAgent agent, (DpopClient Client, string Token) session, string text, CancellationToken ct)
    {
        using var posted = await session.Client.PostAsync(forum.Client, PostsUrl, session.Token, agent.SignQuestion("board-hw", "Does the head check hold? " + text, "R6.61 says " + text, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var body = await posted.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("post_id").GetString()!;
    }

    internal static async Task SignAsync(ForumFixture forum, string pem, CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct, readLogSigningKeyPem: () => pem);
        Assert.Equal(0, exit);
    }

    private static async Task<SignedHeadDocument> HeadAsync(ForumFixture forum, CancellationToken ct)
    {
        var http = forum.Client;
        var read = await new ForumClient(http, http.BaseAddress!).GetLogHeadAsync(ct);
        Assert.True(read.TryGetValue(out var head, out var refusal), refusal?.Summary);
        return head!;
    }

    private static async Task<string> RawHeadAsync(ForumFixture forum, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri("/v1/log/head", UriKind.Relative), ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>A client of <paramref name="forum"/> whose <c>/v1/log/head</c> serves <paramref name="head"/>'s bytes: a lagging replica, every other route being the Forum's own.</summary>
    internal HttpClient Lagging(ForumFixture forum, string head) => new(new LagHandler(forum.Server.CreateHandler(), head)) { BaseAddress = Origin };

    private sealed class LagHandler(HttpMessageHandler inner, string head) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/log/head")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(head, Encoding.UTF8, "application/json") };

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>What one in-process run of <c>curia</c> printed, the streams kept apart.</summary>
internal sealed record Captured(int Exit, string Stdout, string Stderr);

/// <summary>The run of <c>curia</c> in process takes the console and the environment, which are the process's: the probes that do it run alone.</summary>
[CollectionDefinition("head-cli", DisableParallelization = true)]
public sealed class HeadCliGroup;

/// <summary>
/// CWK20 to CWK32, CWK35 and CWK36 (errata G59, R6.61, R11.83; plan Task 0's M4 and M6) in host K: a retained head is verified before it is compared, a failure is between two verified heads by role, and
/// a head that does not verify is could-not-be-checked and its file is left as it is. Each probe is in its failing form: on the seam the retained head is never verified, a damaged signature reads
/// as <c>verified</c> and the lagging replica's line states the wrong sizes. The probes that print a line compare it with the stretches of the published words between placeholders (trap 141), the
/// sizes, kids, instant and directory the line must name being read from the plant and not from the code.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("head-cli")]
public sealed class RetainedHeadOperatorTests(HeadWorld world) : IClassFixture<HeadWorld>, IDisposable
{
    private static readonly DateTimeOffset RetainedAt = new(2026, 11, 1, 10, 30, 0, TimeSpan.Zero);
    private const string RetainedAtText = "2026-11-01T10:30:00.000Z";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-retained-head-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HeadStore Heads() => new(_home);

    private string FileOf(HeadStore heads) => Path.Combine(heads.DirectoryFor(world.Origin), "head.json");

    private static string Fold(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private async Task<PostVerification> VerifyAsync(HttpClient http, HeadStore heads)
    {
        var result = await new PostVerifier(new ForumClient(http, world.Origin), heads).VerifyAsync(world.PostId, MarkingMode.None, Ct);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }

    private static string Line(PostVerification v) => Fold("consistency " + v.Consistency.Describe);

    private static string Start(string text) => text.Length <= 150 ? text : text[..150] + "...";

    private static JsonValue.Object With(JsonValue.Object o, string key, JsonValue value) =>
        new([.. o.Members.Select(m => m.Key == key ? new KeyValuePair<string, JsonValue>(key, value) : m)]);

    private static string Flip(string signature)
    {
        var middle = signature.Length / 2;
        return signature[..middle] + (signature[middle] == 'A' ? 'B' : 'A') + signature[(middle + 1)..];
    }

    private static SignedHeadDocument FlippedSignature(SignedHeadDocument h) => h with { Signature = Flip(h.Signature) };

    private static SignedHeadDocument EditedRoot(SignedHeadDocument h) => h with { Head = With(h.Head, "root_hash", new JsonValue.String("sha256:" + new string('1', 64))) };

    private static SignedHeadDocument EditedSize(SignedHeadDocument h) => h with { Head = With(h.Head, "tree_size", new JsonValue.Number(h.TreeSize + 1)) };

    private static SignedHeadDocument EditedKid(SignedHeadDocument h, string kid) => h with { Kid = kid };

    private void Retain(HeadStore heads, SignedHeadDocument head) => heads.Write(world.Origin, head, RetainedAt);

    /// <summary>The stretches of a published paragraph, each of which must be in <paramref name="printed"/>; returns the ones missing.</summary>
    private static IEnumerable<string> Missing(string printed, string words) => PublishedAgentWords.Stretches(words).Where(s => !printed.Contains(s, StringComparison.Ordinal)).Select(s => $"missing \"{(s.Length > 70 ? s[..70] + "..." : s)}\"");

    private static string Said(PostVerification v) => $"{v.Consistency.Outcome} \"{Start(Line(v))}\"";

    // ----- CWK20 -----

    /// <summary>
    /// CWK20. The plant is real: a post verified against Forum A with a head from <c>sign-head</c> retains it; a head from Forum B at the same size has a different root; both verify under
    /// <c>ActaCheck.HeadSignature</c> with the key set A publishes. A control on the seam.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK20_ThePlantsAreRealHeadsTheOperatorSigned()
    {
        var heads = Heads();
        var first = await VerifyAsync(world.A.Client, heads);
        var keys = await new ForumClient(world.A.Client, world.Origin).GetLogJwksAsync(Ct);
        Assert.True(keys.TryGetValue(out var published, out var refusal), refusal?.Summary);

        var problems = new List<string>();
        if (heads.Read(world.Origin)?.TreeSize != world.A3.TreeSize) problems.Add("the first verification did not retain Forum A's head");
        if (first.Consistency.Outcome != CheckOutcome.CouldNotCheck || !first.Consistency.NothingToCompare) problems.Add("the first read is not the first-retained outcome");
        if (world.B3.TreeSize != world.A3.TreeSize) problems.Add($"the heads' sizes differ ({world.A3.TreeSize} and {world.B3.TreeSize})");
        if (world.B3.RootHash == world.A3.RootHash) problems.Add("the two forums' roots at one size are equal, so the plant is no fork");
        foreach (var (name, head) in new[] { ("A3", world.A3), ("B3", world.B3), ("B2", world.B2) })
            if (ActaCheck.HeadSignature(head, published).Outcome != CheckOutcome.Verified) problems.Add($"{name} does not verify under the log's published keys");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- CWK21, CWK22 -----

    /// <summary>
    /// CWK21. A real fork at an equal size: the retained head from B, the served from A: <c>FAILED</c>; the line holds both tree sizes, both kids, the retained instant, both roots, "both signed by the log's
    /// published keys" and "equivocated"; the file byte-identical. Predicted red: <c>expected "kid" and "retained at" in the detail</c>.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK21_ARealForkAtOneSizeIsFailedWithBothHeadsNamed()
    {
        var heads = Heads();
        Retain(heads, world.B3);
        var before = File.ReadAllBytes(FileOf(heads));
        var run = await VerifyAsync(world.A.Client, heads);
        var line = Line(run);

        var problems = new List<string>();
        if (run.Consistency.Outcome != CheckOutcome.Failed) problems.Add($"expected FAILED, was {Said(run)}");
        problems.AddRange(Missing(line, PublishedAgentWords.FailedEqualWords));
        foreach (var (what, value) in new[] { ("the retained head's tree size", $"tree size {world.B3.TreeSize}"), ("the retained head's kid", world.B3.Kid), ("the served head's kid", world.A3.Kid), ("retained at", RetainedAtText), ("the retained root", world.B3.RootHash!), ("the served root", world.A3.RootHash!) })
            if (!line.Contains(value, StringComparison.Ordinal)) problems.Add($"expected {what} (\"{value}\") in the detail");
        if (!line.Contains("equivocated", StringComparison.Ordinal)) problems.Add("lacks \"equivocated\"");
        if (!File.ReadAllBytes(FileOf(heads)).SequenceEqual(before)) problems.Add("the retained file changed");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK22. A real fork at unequal sizes, the retained head the smaller: <c>FAILED</c>, both heads named by role with sizes and kids, a reason of R11.83's list, and the words of the direction a
    /// proof's roots happen to be in (<c>from_root</c>, <c>to_root</c>) nowhere. Predicted red: <c>expected role words, found "from_root"</c>.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK22_ARealForkAtUnequalSizesNamesTheHeadsByRole()
    {
        var heads = Heads();
        Retain(heads, world.B2);
        var run = await VerifyAsync(world.A.Client, heads);
        var line = Line(run);

        var problems = new List<string>();
        if (run.Consistency.Outcome != CheckOutcome.Failed) problems.Add($"expected FAILED, was {Said(run)}");
        problems.AddRange(Missing(line, PublishedAgentWords.FailedProofWords));
        if (line.Contains("from_root", StringComparison.Ordinal) || line.Contains("to_root", StringComparison.Ordinal)) problems.Add($"expected role words, found \"{(line.Contains("from_root", StringComparison.Ordinal) ? "from_root" : "to_root")}\"");
        foreach (var (what, value) in new[] { ("the retained head's tree size", $"tree size {world.B2.TreeSize}"), ("the served head's tree size", $"tree size {world.A3.TreeSize}"), ("the retained head's kid", world.B2.Kid), ("the served head's kid", world.A3.Kid), ("retained at", RetainedAtText) })
            if (!line.Contains(value, StringComparison.Ordinal)) problems.Add($"expected {what} (\"{value}\") in the detail");
        if (!PublishedAgentWords.ProofReasons.Any(r => PublishedAgentWords.Stretches(r).Any(s => line.Contains(s, StringComparison.Ordinal)))) problems.Add("no reason of R11.83's list is in the line");
        if (!line.Contains("equivocated", StringComparison.Ordinal)) problems.Add("lacks \"equivocated\"");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- CWK23, CWK24 -----

    /// <summary>
    /// CWK23. The lagging replica: the retained head larger, the served head smaller, one honest log: <c>verified</c> with the corrected line (the retained head extends the served head), and the file
    /// stands. Predicted red: <c>the line says "the log at 26 leaves extends the head this client retained at 23"</c> (measured by Task 0: the retained head is at 26).
    /// </summary>
    [Fact]
    public async Task R6_61_CWK23_ALaggingReplicaIsVerifiedWithTheRetainedHeadAsTheLargerOne()
    {
        var heads = Heads();
        Retain(heads, world.A3);
        var before = File.ReadAllBytes(FileOf(heads));
        using var lagging = world.Lagging(world.A, world.A1Raw);
        var run = await VerifyAsync(lagging, heads);
        var line = Line(run);

        var problems = new List<string>();
        if (run.Consistency.Outcome != CheckOutcome.Verified) problems.Add($"expected verified, was {Said(run)}");
        problems.AddRange(Missing(line, PublishedAgentWords.LaggingWords));
        if (!line.Contains($"retained head (tree size {world.A3.TreeSize})", StringComparison.Ordinal)) problems.Add($"does not name the retained head at tree size {world.A3.TreeSize}");
        if (!line.Contains($"tree size {world.A1.TreeSize}", StringComparison.Ordinal)) problems.Add($"does not name the served head at tree size {world.A1.TreeSize}");
        if (line.Contains($"retained at {world.A1.TreeSize}", StringComparison.Ordinal)) problems.Add("says the head was retained at the served head's size");
        if (!File.ReadAllBytes(FileOf(heads)).SequenceEqual(before)) problems.Add("the retained head did not stand");
        if (problems.Count > 0) problems.Insert(0, $"actual line: \"{Start(line)}\"");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK24. A lagging replica that forked: the retained head (Forum B's, larger) against the served head (Forum A's older head): <c>FAILED</c>, the retained head's log named as not extending the
    /// served head's, the roles right. Predicted red: <c>to_root: the line prints the retained head's root as "the served head's"</c> (measured by Task 0; the real plant reaches <c>to_root</c>).
    /// </summary>
    [Fact]
    public async Task R6_61_CWK24_ALaggingReplicaThatForkedNamesTheRolesRight()
    {
        var heads = Heads();
        Retain(heads, world.B3);
        using var lagging = world.Lagging(world.A, world.A1Raw);
        var run = await VerifyAsync(lagging, heads);
        var line = Line(run);

        var problems = new List<string>();
        if (run.Consistency.Outcome != CheckOutcome.Failed) problems.Add($"expected FAILED, was {Said(run)}");
        problems.AddRange(Missing(line, PublishedAgentWords.FailedProofWords));
        if (line.Contains("from_root", StringComparison.Ordinal) || line.Contains("to_root", StringComparison.Ordinal)) problems.Add($"{(line.Contains("to_root", StringComparison.Ordinal) ? "to_root" : "from_root")}: the line prints the proof's direction, not the heads' roles");
        if (!line.Contains($"the retained head's log at {world.B3.TreeSize} leaves does not extend the served head's at {world.A1.TreeSize} leaves", StringComparison.Ordinal)) problems.Add("lacks the reason for the Forum having served the smaller head");
        if (problems.Count > 0) problems.Insert(0, $"actual line: \"{Start(line)}\"");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- CWK25 -----

    private enum Relation { Equal, Smaller, Larger }

    private (SignedHeadDocument Base, HttpClient Client) Scenario(Relation relation) => relation switch
    {
        Relation.Equal => (world.A3, world.A.Client),
        Relation.Smaller => (world.A2, world.A.Client),
        Relation.Larger => (world.A3, world.Lagging(world.A, world.A1Raw)),
        _ => throw new ArgumentOutOfRangeException(nameof(relation)),
    };

    /// <summary>
    /// CWK25. The retained head does not verify. A matrix of five plants (a flipped signature character, an edited root, an edited tree size, an edited <c>kid</c> to another published kid, an edited
    /// <c>kid</c> to a kid the log does not publish) at each of the three size relations: <c>COULD NOT BE CHECKED</c> (the signature words, or the key words for the unpublished kid), the line naming
    /// size, kid, the retained instant and the directory, never <c>FAILED</c> or <c>equivocated</c>, the file byte-identical; the positive in the same fact: the same scenario with an intact head
    /// verifies. Every cell that is not as required is listed with what it actually was (plan ruling 20). Predicted red: a flipped signature and an edited <c>kid</c> <c>expected COULD NOT BE
    /// CHECKED, exit 8, was verified, exit 0</c> (smaller: file replaced); an edited root or tree size <c>was FAILED, exit 6</c>.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK25_ARetainedHeadThatDoesNotVerifyIsNeverComparedAndNeverReplaced()
    {
        var problems = new List<string>();
        foreach (var relation in Enum.GetValues<Relation>())
        {
            // The positive: the intact head.
            using (var control = Scenario(relation).Client)
            {
                var heads = Heads();
                var (head, client) = (Scenario(relation).Base, control);
                Retain(heads, head);
                var run = await VerifyAsync(client, heads);
                if (run.Consistency.Outcome != CheckOutcome.Verified) problems.Add($"control@{relation}: an intact head is not verified: {Said(run)}");
                Directory.Delete(heads.DirectoryFor(world.Origin), recursive: true);
            }

            var otherKid = Scenario(relation).Base.Kid == world.KeyKid ? world.OtherKid : world.KeyKid;
            var plants = new (string Name, Func<SignedHeadDocument, SignedHeadDocument> Edit, string Words)[]
            {
                ("flipped signature", FlippedSignature, PublishedAgentWords.SignatureWords),
                ("edited root", EditedRoot, PublishedAgentWords.SignatureWords),
                ("edited tree size", EditedSize, PublishedAgentWords.SignatureWords),
                ("edited kid to a published kid", h => EditedKid(h, otherKid), PublishedAgentWords.SignatureWords),
                ("edited kid to an unpublished kid", h => EditedKid(h, "unpublished-kid"), PublishedAgentWords.KeyWords),
            };
            foreach (var (name, edit, words) in plants)
            {
                var heads = Heads();
                var (head, client) = Scenario(relation);
                using (client)
                {
                    var planted = edit(head);
                    Retain(heads, planted);
                    var before = File.ReadAllBytes(FileOf(heads));
                    var run = await VerifyAsync(client, heads);
                    var line = Line(run);
                    var found = new List<string>();
                    if (run.Consistency.Outcome != CheckOutcome.CouldNotCheck || run.Consistency.NothingToCompare) found.Add($"expected COULD NOT BE CHECKED, was {Said(run)}");
                    else
                    {
                        found.AddRange(Missing(line, words));
                        foreach (var (what, value) in new[] { ("the tree size", $"tree size {planted.TreeSize}"), ("the kid", planted.Kid), ("retained at", RetainedAtText), ("the directory", heads.DirectoryFor(world.Origin)) })
                            if (!line.Contains(value, StringComparison.Ordinal)) found.Add($"does not name {what}");
                        if (line.Contains("FAILED", StringComparison.Ordinal) || line.Contains("equivocated", StringComparison.Ordinal)) found.Add("says FAILED or equivocated");
                    }

                    if (!File.ReadAllBytes(FileOf(heads)).SequenceEqual(before)) found.Add("the file changed");
                    if (run.Overall == CheckOutcome.Verified) found.Add("the overall verdict is verified");
                    if (found.Count > 0) problems.Add($"{name}@{relation}: {string.Join(", ", found)}");
                }

                Directory.Delete(heads.DirectoryFor(world.Origin), recursive: true);
            }
        }

        Assert.True(problems.Count == 0, "Expected: COULD NOT BE CHECKED with the published words, the file untouched, in all 15 cells, and the intact head verified. Actual: " + string.Join("; ", problems));
    }

    // ----- CWK26, CWK27, CWK28 -----

    /// <summary>
    /// CWK26. A kid the Forum does not publish: a retained head signed by a key Forum A's set lacks (Forum B's third key): <c>COULD NOT BE CHECKED</c> in the key words (<c>copy it aside</c>); the control
    /// (M6): a head signed by a log key that is still published but no longer the newest verifies. Predicted red: <c>FAILED</c>.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK26_AHeadSignedByAKeyTheLogDoesNotPublishIsTheKeyCase()
    {
        var problems = new List<string>();
        var heads = Heads();
        Retain(heads, world.B4);
        var run = await VerifyAsync(world.A.Client, heads);
        if (run.Consistency.Outcome != CheckOutcome.CouldNotCheck || run.Consistency.NothingToCompare) problems.Add($"expected COULD NOT BE CHECKED, was {Said(run)}");
        problems.AddRange(Missing(Line(run), PublishedAgentWords.KeyWords));

        var retired = Heads();
        Directory.Delete(heads.DirectoryFor(world.Origin), recursive: true);
        Retain(retired, world.A2);
        var control = await VerifyAsync(world.A.Client, retired);
        if (control.Consistency.Outcome != CheckOutcome.Verified) problems.Add($"control: a head signed by a key that is still published (kid {world.KeyKid}) is not verified: {Said(control)}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK27. A stranger's key under a published kid: Forum B's third-key head whose <c>kid</c> member names a kid the log does publish: the signature fails, so the signature words and not the key words.
    /// Predicted red: <c>FAILED</c>.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK27_AStrangersKeyUnderAPublishedKidIsTheSignatureCase()
    {
        var heads = Heads();
        Retain(heads, EditedKid(world.B4, world.KeyKid));
        var run = await VerifyAsync(world.A.Client, heads);
        var problems = new List<string>();
        if (run.Consistency.Outcome != CheckOutcome.CouldNotCheck || run.Consistency.NothingToCompare) problems.Add($"expected COULD NOT BE CHECKED, was {Said(run)}");
        problems.AddRange(Missing(Line(run), PublishedAgentWords.SignatureWords));
        if (Line(run).Contains("names a key the log does not publish", StringComparison.Ordinal)) problems.Add("the key words were printed for a published kid");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK28. <c>retained_at</c> absent or unparsable: the instant reads <c>NOT STATED</c> in both could-not-be-checked outcomes and in the failure. Predicted red: <c>retained at &lt;empty&gt;</c> (or no outcome
    /// at all on the seam).
    /// </summary>
    [Fact]
    public async Task R6_61_CWK28_AnAbsentOrUnparsableInstantReadsNotStated()
    {
        var problems = new List<string>();
        var plants = new (string Name, SignedHeadDocument Head, CheckOutcome Outcome)[]
        {
            ("signature case", FlippedSignature(world.A3), CheckOutcome.CouldNotCheck),
            ("key case", EditedKid(world.A3, "unpublished-kid"), CheckOutcome.CouldNotCheck),
            ("failure", world.B3, CheckOutcome.Failed),
        };
        foreach (var (name, head, outcome) in plants)
        {
            foreach (var damage in new[] { "absent", "unparsable" })
            {
                var heads = Heads();
                Retain(heads, head);
                var file = FileOf(heads);
                using (var json = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    var members = json.RootElement.EnumerateObject().Where(p => p.Name != "retained_at").Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}");
                    var tail = damage == "unparsable" ? ",\"retained_at\":\"not an instant\"" : string.Empty;
                    File.WriteAllText(file, "{" + string.Join(",", members) + tail + "}");
                }

                var run = await VerifyAsync(world.A.Client, heads);
                if (run.Consistency.Outcome != outcome) problems.Add($"{name}/{damage}: expected {outcome}, was {Said(run)}");
                else if (!Line(run).Contains("retained at NOT STATED", StringComparison.Ordinal)) problems.Add($"{name}/{damage}: the instant does not read \"retained at NOT STATED\" (\"{Start(Line(run))}\")");
                Directory.Delete(heads.DirectoryFor(world.Origin), recursive: true);
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- CWK29, CWK30 -----

    /// <summary>
    /// CWK29. Never replaced: after a could-not-be-checked and after a failure the file is byte-identical; after a verified extension it is replaced (the control); after a first read it is written. Predicted
    /// red: green for the failure; red for the new outcomes (a flipped signature on the smaller head: md5 changed, the file replaced by the served head).
    /// </summary>
    [Fact]
    public async Task R6_61_CWK29_TheRetainedFileIsReplacedOnlyByAVerifiedExtension()
    {
        var problems = new List<string>();

        foreach (var (name, head) in new[] { ("a flipped signature on the smaller head", FlippedSignature(world.A2)), ("a flipped signature on the equal head", FlippedSignature(world.A3)), ("a fork", world.B3) })
        {
            var heads = Heads();
            Retain(heads, head);
            var before = File.ReadAllBytes(FileOf(heads));
            await VerifyAsync(world.A.Client, heads);
            if (!File.ReadAllBytes(FileOf(heads)).SequenceEqual(before)) problems.Add($"{name}: the file changed (sha256 {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(before))} before)");
            Directory.Delete(heads.DirectoryFor(world.Origin), recursive: true);
        }

        var extension = Heads();
        Retain(extension, world.A2);
        var older = File.ReadAllBytes(FileOf(extension));
        await VerifyAsync(world.A.Client, extension);
        if (File.ReadAllBytes(FileOf(extension)).SequenceEqual(older) || extension.Read(world.Origin)?.TreeSize != world.A3.TreeSize) problems.Add("control: a verified extension did not replace the file with the served head");
        Directory.Delete(extension.DirectoryFor(world.Origin), recursive: true);

        var first = Heads();
        await VerifyAsync(world.A.Client, first);
        if (first.Read(world.Origin)?.TreeSize != world.A3.TreeSize) problems.Add("control: a first read did not write the served head");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK30. The served head outranks: a served head that does not verify gives the existing verdict and the retained head is not what is compared (a bad retained head is neither read as a fork nor
    /// replaced). A control on the seam.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK30_AServedHeadThatDoesNotVerifyOutranksTheRetainedHead()
    {
        var heads = Heads();
        Retain(heads, world.B3);
        var before = File.ReadAllBytes(FileOf(heads));
        var tampered = FlippedSignatureRaw(world.A1Raw);
        using var lagging = world.Lagging(world.A, tampered);
        var run = await VerifyAsync(lagging, heads);

        var problems = new List<string>();
        if (run.Consistency.Outcome != CheckOutcome.Failed || !Line(run).Contains("does not verify", StringComparison.Ordinal)) problems.Add($"expected the served head's own failure, was {Said(run)}");
        if (Line(run).Contains("equivocated", StringComparison.Ordinal)) problems.Add("the retained head was compared and called a fork");
        if (!File.ReadAllBytes(FileOf(heads)).SequenceEqual(before)) problems.Add("the retained file changed");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static string FlippedSignatureRaw(string raw)
    {
        var signature = JsonDocument.Parse(raw).RootElement.GetProperty("signature").GetString()!;
        return raw.Replace(signature, Flip(signature), StringComparison.Ordinal);
    }

    // ----- CWK31, CWK32, CWK35 -----

    private async Task<Captured> VerifyWithTheCliAsync(HttpClient http, string home)
    {
        var savedHome = Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME");
        var savedTestis = Environment.GetEnvironmentVariable("CURIA_TESTIS_BIN");
        var savedFactory = CliProgram.HttpClientFactory;
        try
        {
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", home);
            Environment.SetEnvironmentVariable("CURIA_TESTIS_BIN", TestisBinary.Locate());
            CliProgram.HttpClientFactory = _ => http;
            var captured = await ConsoleCaptureAsync(() => CliProgram.RunAsync(["verify", world.PostId, "--forum", world.Origin.ToString()]));
            return captured;
        }
        finally
        {
            CliProgram.HttpClientFactory = savedFactory;
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", savedHome);
            Environment.SetEnvironmentVariable("CURIA_TESTIS_BIN", savedTestis);
        }
    }

    private static async Task<Captured> ConsoleCaptureAsync(Func<Task<int>> action)
    {
        var (savedOut, savedError) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await action();
            return new Captured(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
        }
    }

    // The age of the retained file is the one figure that differs between two runs a second apart; it is replaced by N, and nothing else is.
    private static string ConsistencyLine(string text) =>
        Regex.Replace(Fold(Regex.Match(text, @"consistency (?:verified|FAILED|COULD NOT BE CHECKED)[^\n]*", RegexOptions.CultureInvariant).Value), @"\(\d+ s before", "(N s before", RegexOptions.CultureInvariant);

    /// <summary>
    /// CWK31. Both clients, one line: <c>curia verify</c> and <c>curia_verify</c> print the same consistency line over a real fork (CWK21's plant) and over a flipped signature (CWK25's). Green on the seam by
    /// sharing; red under FCWK30.
    /// </summary>
    [Fact]
    public async Task R11_83_CWK31_TheTwoClientsPrintOneConsistencyLine()
    {
        var problems = new List<string>();
        foreach (var (name, head) in new[] { ("fork", world.B3), ("flipped signature", FlippedSignature(world.A3)) })
        {
            var cliHome = Directory.CreateTempSubdirectory("curia-cli-home-").FullName;
            var heads = new HeadStore(cliHome);
            heads.Write(world.Origin, head, RetainedAt);
            var cli = await VerifyWithTheCliAsync(world.A.Client, cliHome);

            var mcpHeads = Heads();
            Retain(mcpHeads, head);
            var tools = new ForumTools(new ForumClient(world.A.Client, world.Origin), MarkingMode.None, mcpHeads);
            var result = await tools.VerifyAsync(world.PostId, null, Ct);
            var mcpText = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

            var (cliLine, mcpLine) = (ConsistencyLine(cli.Stdout + cli.Stderr), ConsistencyLine(mcpText));
            if (cliLine.Length == 0 || mcpLine.Length == 0) problems.Add($"{name}: a client printed no consistency line (cli \"{Start(cliLine)}\", mcp \"{Start(mcpLine)}\")");
            else if (cliLine != mcpLine) problems.Add($"{name}: lines differ (cli \"{Start(cliLine)}\", mcp \"{Start(mcpLine)}\")");
            Directory.Delete(cliHome, recursive: true);
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK32. The lock: two verifications over a bad retained file at once: neither writes, both say could not be checked. Predicted red (the plan said green; on the seam a flipped signature is <c>verified</c>
    /// and the smaller case replaces the file): <c>verified</c>.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK32_TwoVerificationsOverABadRetainedFileNeitherWritesAndBothSayCouldNotBeChecked()
    {
        var heads = Heads();
        Retain(heads, FlippedSignature(world.A2));
        var before = File.ReadAllBytes(FileOf(heads));
        var both = await Task.WhenAll(VerifyAsync(world.A.Client, heads), VerifyAsync(world.A.Client, Heads()));

        var problems = new List<string>();
        for (var i = 0; i < both.Length; i++)
            if (both[i].Consistency.Outcome != CheckOutcome.CouldNotCheck || both[i].Consistency.NothingToCompare) problems.Add($"verification {i + 1}: expected COULD NOT BE CHECKED, was {Said(both[i])}");
        if (!File.ReadAllBytes(FileOf(heads)).SequenceEqual(before)) problems.Add("a verification wrote the file");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CWK35. The verdicts: a bad retained head with a good post is exit 8 and <c>NOT ESTABLISHED</c>, never 6; a fork is 6; the first read is the existing line (exit 0). Predicted red: <c>exit 6</c> for an edited
    /// root, <c>exit 0</c> for a flipped signature.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK35_ABadRetainedHeadIsEightAndAForkIsSix()
    {
        var problems = new List<string>();
        var cases = new (string Name, SignedHeadDocument? Head, int Exit)[]
        {
            ("a flipped signature", FlippedSignature(world.A3), 8),
            ("an edited root", EditedRoot(world.A3), 8),
            ("a fork", world.B3, 6),
            ("no retained head", null, 0),
        };
        foreach (var (name, head, exit) in cases)
        {
            var home = Directory.CreateTempSubdirectory("curia-cli-home-").FullName;
            if (head is not null) new HeadStore(home).Write(world.Origin, head, RetainedAt);
            var run = await VerifyWithTheCliAsync(world.A.Client, home);
            if (run.Exit != exit) problems.Add($"{name}: exit {run.Exit}, expected {exit}");
            if (exit == 8 && !(run.Stdout + run.Stderr).Contains("NOT ESTABLISHED", StringComparison.Ordinal)) problems.Add($"{name}: no \"NOT ESTABLISHED\" in the verdict");
            Directory.Delete(home, recursive: true);
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- CWK36 -----

    /// <summary>
    /// CWK36. An independent verifier: the plants of CWK21, CWK25 and CWK26 given to <c>curia-testis log head --head &lt;retained file&gt; --log-jwks &lt;jwks&gt;</c>: it accepts the real heads and rejects the flipped,
    /// edited and foreign ones, and the client's verdict on the retained head (<c>RetainedHeadCheck.Signature</c>) is asserted equal in accept or reject on each; the reasons are recorded, not asserted
    /// equal (an edited <c>kid</c> is <c>kid-mismatch</c> there, signature or key words here). A missing binary is a failure, not a skip. Predicted red: the seam's check accepts every head.
    /// </summary>
    [Fact]
    public async Task R6_61_CWK36_TheIndependentVerifierAndTheClientAgreeOnEveryPlant()
    {
        var jwks = Path.Combine(_home, "log-jwks.json");
        using (var response = await world.A.Client.GetAsync(new Uri("/v1/log/jwks", UriKind.Relative), Ct))
            await File.WriteAllBytesAsync(jwks, await response.Content.ReadAsByteArrayAsync(Ct), Ct);

        var keys = await new ForumClient(world.A.Client, world.Origin).GetLogJwksAsync(Ct);
        Assert.True(keys.TryGetValue(out var published, out var refusal), refusal?.Summary);

        var binary = TestisBinary.Locate();
        var plants = new (string Name, SignedHeadDocument Head)[]
        {
            ("A3 intact", world.A3), ("B3 intact (a real fork's head)", world.B3), ("A2 intact", world.A2),
            ("flipped signature", FlippedSignature(world.A3)), ("edited root", EditedRoot(world.A3)), ("edited tree size", EditedSize(world.A3)),
            ("edited kid to a published kid", EditedKid(world.A3, world.KeyKid)), ("edited kid to an unpublished kid", EditedKid(world.A3, "unpublished-kid")),
            ("signed by a key the log does not publish", world.B4),
        };
        var problems = new List<string>();
        var reasons = new List<string>();
        foreach (var (name, head) in plants)
        {
            var heads = Heads();
            Retain(heads, head);
            var (code, stdout, stderr) = TestisBinary.Run(binary, $"log head --head \"{FileOf(heads)}\" --log-jwks \"{jwks}\"");
            var testisAccepts = code == 0;
            var clientAccepts = RetainedHeadCheck.Signature(head, published).Outcome == CheckOutcome.Verified;
            reasons.Add($"{name}: testis {(testisAccepts ? "accepts" : "rejects")} ({Start(Fold((stdout + " " + stderr).Trim()))})");
            if (testisAccepts != clientAccepts) problems.Add($"{name}: the independent verifier {(testisAccepts ? "accepts" : "rejects")} and the client {(clientAccepts ? "accepts" : "rejects")}");
            Directory.Delete(heads.DirectoryFor(world.Origin), recursive: true);
        }

        Assert.True(problems.Count == 0, "Expected: the two verifiers agree on accept and reject for every plant. Actual: " + string.Join("; ", problems) + " | recorded reasons: " + string.Join(" | ", reasons));
    }
}
