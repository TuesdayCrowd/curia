using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// NDT9 (D10b, D122; Amendment 1, ruling 4). An annotated post (<c>possible_duplicate</c> set in the log entry) is verified by the real client, over a real Kestrel Forum on the hashed adapter and the real
/// <c>curia</c> command. The scene must carry a cosine that is not integral: a double that is integral serializes as an integer, so an override of a literal duplicate (cosine exactly 1.0) verifies and
/// hides D122. The literal-duplicate override is therefore this probe's control, green on the tree by design; the non-integral scene is the red.
///
/// Predicted red on the tree: <c>curia verify</c> exits 8 for <c>curia/admit/non-integer-number</c>, and the annotation holds no <c>cosine_micro</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AnnotationCosineIntegerTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-ndt9-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^posted\s+""?(?<v>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Posted();

    [GeneratedRegex(@"cosine (?<bp>\d+) bp")]
    private static partial Regex SimilarityBp();

    [GeneratedRegex(@"curia/admit/[a-z-]+")]
    private static partial Regex AdmitType();

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["CURIA_CLIENT_HOME"] = _home;
        start.Environment["CURIA_TESTIS_BIN"] = TestisBinary.Locate();
        using var p = Process.Start(start)!;
        var o = p.StandardOutput.ReadToEndAsync(Ct);
        var e = p.StandardError.ReadToEndAsync(Ct);
        await p.WaitForExitAsync(Ct);
        return (p.ExitCode, await o, await e);
    }

    private async Task SignHeadAsync()
    {
        using var so = new StringWriter(CultureInfo.InvariantCulture);
        using var se = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, await OperatorCommands.RunAsync(["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, so, se, Ct, readLogSigningKeyPem: () => LogKeyPem));
    }

    /// <summary>The <c>possible_duplicate</c> object of a post's log entry, found by walking the entry (the payload may be an object or a string holding JSON).</summary>
    private async Task<JsonElement?> AnnotationOfAsync(string postId)
    {
        var served = JsonDocument.Parse(await forum.Client.GetStringAsync(new Uri($"{Address}/v1/posts/{postId}"), Ct)).RootElement;
        var index = served.GetProperty("log_index").GetInt64();
        var entry = JsonDocument.Parse(await forum.Client.GetStringAsync(new Uri($"{Address}/v1/log/entries/{index.ToString(CultureInfo.InvariantCulture)}"), Ct)).RootElement;
        return Find(entry);

        static JsonElement? Find(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in e.EnumerateObject())
                    {
                        if (member.Name == "possible_duplicate") return member.Value.Clone();
                        if (Find(member.Value) is { } inner) return inner;
                    }

                    return null;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) if (Find(item) is { } inner) return inner;
                    return null;
                case JsonValueKind.String when e.GetString() is { } s && s.StartsWith('{') && s.Contains("possible_duplicate", StringComparison.Ordinal):
                    try { return Find(JsonDocument.Parse(s).RootElement); } catch (JsonException) { return null; }
                default:
                    return null;
            }
        }
    }

    /// <summary>True when the cosine's JSON text carries no fraction or exponent: the class that hides D122.</summary>
    private static bool IsIntegral(JsonElement annotation) =>
        !annotation.TryGetProperty("cosine", out var cosine) || !(cosine.GetRawText().Contains('.', StringComparison.Ordinal) || cosine.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase));

    private sealed record Scene(int RefusedBp, JsonElement Annotation, JsonElement LiteralAnnotation, (int Exit, string Out, string Err) VerifyNonIntegral, (int Exit, string Out, string Err) VerifyLiteral);

    private async Task<Scene> BuildAsync()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var tag = board["board-".Length..];
        string alpha = "alpha" + tag, beta = "beta" + tag, gamma = "gamma" + tag;
        foreach (var agent in new[] { alpha, beta, gamma })
            Assert.Equal(0, (await RunAsync("enrol", "--agent", agent, "--forum", Address)).Exit);

        const string Title = "ECONNRESET from npgsql after the pool idles";
        const string Body = "npgsql throws ECONNRESET after the pooler idles the connection; keepalive is off";
        Assert.Equal(0, (await RunAsync(FirstAsk.Args(alpha, board, Title, Body, Address, true))).Exit);

        // The non-integral scene: the body plus a trailing clause is refused (exit 3, its cosine on the receipt), then re-submitted over the refusal with a rationale, and accepted with possible_duplicate set.
        var variant = Body + " today";
        var refused = await RunAsync(FirstAsk.Args(beta, board, Title, variant, Address, false));
        Assert.Equal(3, refused.Exit);
        var refusedBp = int.Parse(SimilarityBp().Match(refused.Out).Groups["bp"].Value, CultureInfo.InvariantCulture);
        var overridden = await RunAsync(FirstAsk.Args(beta, board, Title, variant, Address, false, "--not-duplicate", "a different incident on another cluster"));
        Assert.Equal(0, overridden.Exit);
        var postId = Posted().Match(overridden.Out).Groups["v"].Value;

        // The control's scene: the literal duplicate, overridden. Its cosine is exactly 1.0 and so it hides D122.
        var literal = await RunAsync(FirstAsk.Args(gamma, board, Title, Body, Address, false, "--not-duplicate", "the same words from a second cluster"));
        Assert.Equal(0, literal.Exit);
        var literalId = Posted().Match(literal.Out).Groups["v"].Value;

        await SignHeadAsync();
        var annotation = await AnnotationOfAsync(postId);
        var literalAnnotation = await AnnotationOfAsync(literalId);
        Assert.True(annotation is not null, "the overridden near-duplicate carries no possible_duplicate in its log entry");
        Assert.True(literalAnnotation is not null, "the overridden literal duplicate carries no possible_duplicate in its log entry");

        var scene = new Scene(refusedBp, annotation!.Value, literalAnnotation!.Value, await RunAsync("verify", postId, "--forum", Address), await RunAsync("verify", literalId, "--forum", Address));
        TestContext.Current.SendDiagnosticMessage(
            $"NDT9 record: non-integral scene cosine integral={IsIntegral(scene.Annotation)} verify exit {scene.VerifyNonIntegral.Exit}; literal-duplicate scene cosine integral={IsIntegral(scene.LiteralAnnotation)} verify exit {scene.VerifyLiteral.Exit}");
        return scene;
    }

    /// <summary>The control, green on the tree by design: the literal duplicate's integral cosine verifies. The scene's other cosine must be non-integral, or the probes below test nothing about D122.</summary>
    [Fact]
    public async Task R8_90_NDT9_ControlAnOverriddenLiteralDuplicateVerifiesAndTheSceneIsNonIntegral()
    {
        var scene = await BuildAsync();
        Assert.True(IsIntegral(scene.LiteralAnnotation), "the control's cosine is exactly 1.0, an integral number, and it is why the control hides D122");
        Assert.Equal(0, scene.VerifyLiteral.Exit);
        Assert.False(IsIntegral(scene.Annotation), "the scene's annotated cosine is integral: it hides D122 and the probe would be vacuous");
    }

    /// <summary>Red on the tree: exit 8 for curia/admit/non-integer-number.</summary>
    [Fact]
    public async Task R8_90_NDT9_AnAnnotatedPostWithANonIntegralCosineVerifies()
    {
        var verify = (await BuildAsync()).VerifyNonIntegral;
        var admit = AdmitType().Match(verify.Out + verify.Err).Value;
        Assert.True(verify.Exit == 0 || (verify.Exit == 8 && admit != "curia/admit/non-integer-number"),
            $"Expected: curia verify exit 0 or 8 for the unsigned head only  Actual: exit {verify.Exit}, {(admit.Length == 0 ? "no curia/admit type" : admit)}");
    }

    /// <summary>Red on the tree: the member is cosine_micro, an integer, and cosine_micro / 100 rounds to the refused attempt's cosine_bp.</summary>
    [Fact]
    public async Task R8_90_NDT9_TheAnnotationStoresItsCosineAsAnIntegerNumberOfMicroUnits()
    {
        var scene = await BuildAsync();
        var members = string.Join(", ", scene.Annotation.EnumerateObject().Select(m => m.Name));
        Assert.True(scene.Annotation.TryGetProperty("cosine_micro", out var micro) && micro.ValueKind == JsonValueKind.Number && micro.TryGetInt64(out _),
            $"Expected: possible_duplicate has an integer member cosine_micro  Actual: members {members}");
        Assert.Equal(scene.RefusedBp, (int)Math.Round(micro.GetInt64() / 100.0, MidpointRounding.AwayFromZero));
    }
}
