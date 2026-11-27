using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.107 (errata G79): <c>curia enrol</c> fetches the Forum's nonce, signs the proof once through the agent's signer, sends once, retries on <c>proof-nonce-stale</c> alone, words every refusal as the
/// errata publishes it, and continues an enrollment that was never completed. Each probe runs the real command in process (<c>AgentHost</c>) against <see cref="EnrolForum"/>, which verifies the proof it
/// is sent with a verifier the test project wrote from the errata's sentence. <b>Written before the client is changed</b>: at the Task 2 tree <c>curia enrol</c> fetches no nonce and sends no proof, so
/// the double refuses it <c>proof-required</c> and every probe is red by its own assertion; the controls (a profile that records an enrollment, or other identifiers, is refused as today) are green.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class EnrolProofClientTests
{
    private const string Forum = "http://forum.test/";

    private static string IdOf(string slug) => "https://agents.example/" + slug;

    private static string[] Enrol(string slug, string? signer = null, string? agentId = null) =>
        [.. new[] { "enrol", "--agent", slug, "--agent-id", agentId ?? IdOf(slug), "--forum", Forum }, .. signer is null ? [] : new[] { "--signer", signer }];

    private static string Requests(AgentRun run) => string.Join(", ", run.Requests.Select(r => $"{r.Method} {r.Path}"));

    private static string Words(string span, string? type = null, string? directory = null) =>
        span.Replace("<type>", type is null ? "<type>" : DisplayLiteral.Of(type), StringComparison.Ordinal)
            .Replace("<directory>", directory is null ? "<directory>" : DisplayLiteral.Of(directory), StringComparison.Ordinal);

    private static void AssertSays(AgentRun run, string words) =>
        Assert.True(PublishedClientWords.Says(run.Text, words), $"the run did not print the errata's words.\nexpected: {words}\nprinted:  {run.Text}");

    /// <summary>
    /// EPP15: against a double that verifies the proof it receives, <c>curia enrol</c> succeeds with two requests (one <c>GET</c>, one <c>POST</c>) and, with an external signer, exactly <b>one</b> <c>sign</c>
    /// call: a proof an owner must sign twice is a prompt an owner must answer twice.
    /// </summary>
    [Fact]
    public async Task R11_107_EPP15_TheClientFetchesTheNonceSignsOnceAndSendsOnce()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum();
        host.OnOther = forum.Handle;
        using var signer = TestSigner.Create("epp15-signer-kid");

        var run = await host.RunAsync(Enrol("epp15", signer.Command));

        Assert.True(run.Exit == 0, $"curia enrol was refused (exit {run.Exit}): {run.Text}");
        Assert.Equal("GET /v1/enroll/nonce, POST /v1/agents", Requests(run));
        Assert.Single(forum.Verifier.Accepted);
        Assert.Equal(1, signer.Signatures);
    }

    /// <summary>EPP15's in-process key: the same two requests and one accepted proof, signed with the profile's registered key.</summary>
    [Fact]
    public async Task R11_107_EPP15_TheClientsOwnKeySignsTheProofTheForumVerifies()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum();
        host.OnOther = forum.Handle;

        var run = await host.RunAsync(Enrol("epp15k"));

        Assert.True(run.Exit == 0, $"curia enrol was refused (exit {run.Exit}): {run.Text}");
        Assert.Equal("GET /v1/enroll/nonce, POST /v1/agents", Requests(run));
        Assert.Single(forum.Verifier.Accepted);
    }

    /// <summary>
    /// EPP16, the one retry: the double answers <c>proof-nonce-stale</c> once and the client fetches a <b>new</b> nonce, signs a <b>new</b> proof (a new <c>jti</c>) and succeeds: three requests, two signatures.
    /// </summary>
    [Fact]
    public async Task R11_107_EPP16_AStaleNonceIsRetriedOnceWithANewNonceAndANewProof()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(400, "curia/enroll/proof-nonce-stale");
        host.OnOther = forum.Handle;
        using var signer = TestSigner.Create("epp16-signer-kid");

        var run = await host.RunAsync(Enrol("epp16", signer.Command));

        Assert.True(run.Exit == 0, $"the retry did not succeed (exit {run.Exit}): {run.Text}");
        Assert.Equal("GET /v1/enroll/nonce, POST /v1/agents, GET /v1/enroll/nonce, POST /v1/agents", Requests(run));
        Assert.Equal(2, signer.Signatures);
        Assert.NotEqual(EnrolForum.JtiOf(forum.Proofs[0]!), EnrolForum.JtiOf(forum.Proofs[1]!));
    }

    /// <summary>EPP16, the stop: a second <c>proof-nonce-stale</c> ends the run with the <c>Proof, nonce stale</c> words (F1 of the live run: not <c>Proof, other</c>, which blames the client), two signatures in all, and the exit every <c>400</c> has (3).</summary>
    [Fact]
    public async Task R11_107_EPP16_ASecondStaleNonceStopsTheRunAndSaysSo()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(400, "curia/enroll/proof-nonce-stale").Refuse(400, "curia/enroll/proof-nonce-stale");
        host.OnOther = forum.Handle;
        using var signer = TestSigner.Create("epp16b-signer-kid");

        var run = await host.RunAsync(Enrol("epp16b", signer.Command));

        Assert.Equal(3, run.Exit);
        Assert.Equal(2, run.Requests.Count(r => r.Method == "POST"));
        Assert.Equal(2, signer.Signatures);
        AssertSays(run, PublishedEnrolWords.ProofNonceStale);
    }

    /// <summary>
    /// EPP16 (F1): the double answers <c>proof-nonce-stale</c> to both <c>POST</c>s. The client prints the errata's <c>Proof, nonce stale</c> span and not the <c>Proof, other</c> words, exits 3, sends two
    /// <c>POST</c>s, and leaves the profile continuable: no <c>enrolled_at</c>, key files unchanged.
    /// </summary>
    [Fact]
    public async Task R11_107_EPP16_AStaleNonceThatSurvivesTheRetryIsNotWordedAsADefectOfTheClient()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(400, "curia/enroll/proof-nonce-stale").Refuse(400, "curia/enroll/proof-nonce-stale");
        host.OnOther = forum.Handle;

        var run = await host.RunAsync(Enrol("epp16f1"));

        Assert.Equal(3, run.Exit);
        Assert.Equal(2, run.Requests.Count(r => r.Method == "POST"));
        AssertSays(run, PublishedEnrolWords.ProofNonceStale);
        Assert.False(PublishedClientWords.Says(run.Text, Words(PublishedEnrolWords.ProofOther, "curia/enroll/proof-nonce-stale")), $"the run blamed the client: {run.Text}");
        Assert.DoesNotContain("defect", run.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("enrolled_at", Identity(host, "epp16f1"), StringComparison.Ordinal);
        var keys = KeyBytes(host, "epp16f1");

        var again = await host.RunAsync(Enrol("epp16f1"));
        Assert.Equal(0, again.Exit);
        Assert.Equal(keys, KeyBytes(host, "epp16f1"));
    }

    /// <summary>EPP16, no other retry: every other refusal of the proof, and a <c>503</c>, sends exactly one <c>POST</c> after one nonce fetch.</summary>
    [Theory]
    [InlineData(400, "curia/enroll/proof-window")]
    [InlineData(400, "curia/enroll/proof-not-possessed")]
    [InlineData(400, "curia/enroll/proof-required")]
    [InlineData(400, "curia/enroll/proof-malformed")]
    [InlineData(400, "curia/enroll/proof-mismatch")]
    [InlineData(400, "curia/enroll/proof-replayed")]
    [InlineData(503, "curia/store/unavailable")]
    public async Task R11_107_EPP16_NoOtherRefusalIsSentAgain(int status, string type)
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(status, type);
        host.OnOther = forum.Handle;

        var run = await host.RunAsync(Enrol("epp16c"));

        Assert.Equal(1, forum.NonceFetches);
        Assert.Equal(1, run.Requests.Count(r => r.Method == "POST"));
        Assert.NotEqual(0, run.Exit);
    }

    /// <summary>EPP17: each refusal's printed line is the span the errata publishes, read from the errata at test time; <c>&lt;type&gt;</c> is the refusal's type as a display literal and nothing else of the response is printed.</summary>
    [Theory]
    [InlineData("proof-window")]
    [InlineData("proof-not-possessed")]
    [InlineData("proof-required")]
    [InlineData("proof-replayed")]
    [InlineData("proof-mismatch")]
    [InlineData("proof-malformed")]
    public async Task R11_107_EPP17_EachRefusalIsWordedAsTheErrataPublishesIt(string slug)
    {
        using var host = new AgentHost();
        host.OnOther = new EnrolForum().Refuse(400, "curia/enroll/" + slug).Handle;

        var run = await host.RunAsync(Enrol("epp17"));

        var words = slug switch
        {
            "proof-window" => PublishedEnrolWords.ProofWindow,
            "proof-not-possessed" => PublishedEnrolWords.ProofNotPossessed,
            "proof-required" => PublishedEnrolWords.ProofRequired,
            _ => Words(PublishedEnrolWords.ProofOther, "curia/enroll/" + slug),
        };
        Assert.Equal(3, run.Exit);
        AssertSays(run, words);
        Assert.False(PublishedClientWords.Says(run.Text, "cannot be enrolled as it is"), "a refusal of the moment was worded as one of the request");
    }

    /// <summary>EPP17, a Forum that predates the proof: the nonce route is a <c>404</c>, the client says so and sends nothing.</summary>
    [Fact]
    public async Task R11_107_EPP17_AForumWithNoNonceRouteIsSaidToPredateTheProofAndNothingIsSent()
    {
        using var host = new AgentHost();
        host.OnOther = new EnrolForum { NoNonceRoute = true }.Handle;

        var run = await host.RunAsync(Enrol("epp17b"));

        AssertSays(run, PublishedEnrolWords.NonceAbsent);
        Assert.DoesNotContain(run.Requests, r => r.Method == "POST");
    }

    private static readonly string[] KeyFiles = ["signing-key.pem", "dpop-key.pem"];

    private static string KeyBytes(AgentHost host, string slug) =>
        string.Concat(KeyFiles.Select(f => Convert.ToHexString(File.ReadAllBytes(Path.Combine(host.Store.DirectoryFor(slug), f)))));

    private static string Identity(AgentHost host, string slug) => File.ReadAllText(Path.Combine(host.Store.DirectoryFor(slug), "identity.json"));

    /// <summary>
    /// EPP20, the continuation: a profile whose enrollment was refused <c>proof-window</c> records no <c>enrolled_at</c>; <c>curia enrol</c> with the same name and identifiers continues it (no key file
    /// changed), sends the proof, succeeds and records <c>enrolled_at</c>. The controls follow: another <c>--agent-id</c>, and a profile that records an enrollment, are refused as today.
    /// </summary>
    [Fact]
    public async Task R11_107_EPP20_AProfileThatWasNeverEnrolledIsContinuedWithItsOwnKeys()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(400, "curia/enroll/proof-window");
        host.OnOther = forum.Handle;

        var first = await host.RunAsync(Enrol("epp20"));
        Assert.Equal(3, first.Exit);
        Assert.True(host.Store.Exists("epp20"), "the refused enrolment left no profile");
        Assert.DoesNotContain("enrolled_at", Identity(host, "epp20"), StringComparison.Ordinal);
        var keys = KeyBytes(host, "epp20");

        var second = await host.RunAsync(Enrol("epp20"));
        Assert.True(second.Exit == 0, $"the same command did not continue the enrollment (exit {second.Exit}): {second.Text}");
        AssertSays(second, PublishedEnrolWords.Continuing);
        Assert.Equal(keys, KeyBytes(host, "epp20"));
        Assert.Contains("enrolled_at", Identity(host, "epp20"), StringComparison.Ordinal);
        Assert.Single(forum.Verifier.Accepted);

        var again = await host.RunAsync(Enrol("epp20"));
        Assert.Equal(2, again.Exit);
        Assert.Contains("An agent by that name is already enrolled", again.Text, StringComparison.Ordinal);
    }

    /// <summary>EPP20's control: a never-enrolled profile named with another <c>--agent-id</c> is refused as today ("An agent by that name is already enrolled", exit 2), and its keys are not touched.</summary>
    [Fact]
    public async Task R11_107_EPP20_AProfileNamedWithAnotherIdentifierIsRefusedAsToday()
    {
        using var host = new AgentHost();
        host.OnOther = new EnrolForum().Refuse(400, "curia/enroll/proof-window").Handle;
        Assert.Equal(3, (await host.RunAsync(Enrol("epp20o"))).Exit);
        var keys = KeyBytes(host, "epp20o");

        var other = await host.RunAsync(Enrol("epp20o", agentId: IdOf("someone-else")));

        Assert.Equal(2, other.Exit);
        Assert.Contains("An agent by that name is already enrolled", other.Text, StringComparison.Ordinal);
        Assert.Equal(keys, KeyBytes(host, "epp20o"));
    }

    /// <summary>
    /// EPP20 (Amendment 2, ruling 23), a refusal of the request itself: a <c>409</c> and a <c>400</c> that is not one of the seven types print the <c>Not continuable</c> words, with the profile's directory;
    /// exit 3; the profile is as it was; and the same command again sends exactly one <c>POST</c> and prints the same.
    /// </summary>
    [Theory]
    [InlineData(409, "curia/enroll/kid-already-registered")]
    [InlineData(409, "curia/enroll/identifier-reserved")]
    [InlineData(400, "curia/enroll/invalid")]
    public async Task R11_107_EPP20_ARefusalOfTheRequestItselfSaysTheProfileCannotBeEnrolled(int status, string type)
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(status, type).Refuse(status, type);
        host.OnOther = forum.Handle;
        var words = Words(PublishedEnrolWords.NotContinuable, type, host.Store.DirectoryFor("epp20nc"));

        var first = await host.RunAsync(Enrol("epp20nc"));
        Assert.Equal(3, first.Exit);
        AssertSays(first, words);
        var fingerprint = host.FingerprintOf("epp20nc");

        var second = await host.RunAsync(Enrol("epp20nc"));
        Assert.Equal(3, second.Exit);
        AssertSays(second, words);
        Assert.Equal(1, second.Requests.Count(r => r.Method == "POST"));
        Assert.Equal(fingerprint, host.FingerprintOf("epp20nc"));
    }
    /// <summary>
    /// G86_EPP21 (R7.33 (rev.) (d)): the edge's <c>403</c> of type <c>curia/edge/enrolment-closed</c> is worded as the errata's <c>Edge closed</c> span says (the operator lists addresses; nothing was registered; the same command enrols once listed),
    /// not by the generic denial line with its empty <c>((none))</c> reason and "Retrying will not change this"; the exit stays 4, and the profile stays continuable (the same command sends one <c>POST</c> and prints the same).
    /// </summary>
    [Fact]
    public async Task G86_EPP21_TheEdgesClosedEnrolmentIsWordedByTheErrata()
    {
        using var host = new AgentHost();
        var forum = new EnrolForum().Refuse(403, "curia/edge/enrolment-closed").Refuse(403, "curia/edge/enrolment-closed");
        host.OnOther = forum.Handle;

        var first = await host.RunAsync(Enrol("epp21"));
        Assert.Equal(4, first.Exit);
        AssertSays(first, PublishedEnrolWords.EdgeClosed);
        Assert.DoesNotContain("((none))", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Retrying will not change this", first.Text, StringComparison.Ordinal);

        var second = await host.RunAsync(Enrol("epp21"));
        Assert.Equal(4, second.Exit);
        AssertSays(second, PublishedEnrolWords.EdgeClosed);
        Assert.Equal(1, second.Requests.Count(r => r.Method == "POST"));
    }
    /// <summary>G86_EPP21b, the control: a <c>403</c> of any other type is not the closure and is not worded as it (the words are for the edge's type alone, D51: a denial does not borrow another refusal's cause).</summary>
    [Fact]
    public async Task G86_EPP21b_AnotherDenialIsNotWordedAsTheEdgesClosure()
    {
        using var host = new AgentHost();
        host.OnOther = new EnrolForum().Refuse(403, "curia/authz/some-other-denial").Handle;

        var run = await host.RunAsync(Enrol("epp21b"));

        Assert.Equal(4, run.Exit);
        Assert.False(PublishedClientWords.Says(run.Text, PublishedEnrolWords.EdgeClosed), $"a 403 of another type was worded as the edge's closure: {run.Text}");
    }
}
