using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The test project's verifier of R4.46 (<see cref="EnrollProofVerifier"/>, written from the errata's sentence and sharing nothing with <c>Curia.AuthN</c>) judges every case of
/// <c>conformance/enroll-proof/</c> and reaches the verdict each <c>expected.json</c> was written from its construction to publish. This is the second reading of the family that exists at
/// Task 2, before the Forum's validator: if the generator's verdicts and the errata's sentence disagreed, this is where it would show. It is a control (green on the tree), and it does not
/// stand for the Forum's validator, which is EPP1.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrollProofVectorAgreementTests
{
    private static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance", "enroll-proof"))) dir = dir.Parent;
            return dir is null ? throw new InvalidOperationException("conformance/enroll-proof not found") : Path.Combine(dir.FullName, "conformance", "enroll-proof");
        }
    }

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal)) data.Add(Path.GetFileName(dir));
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void R4_46_EPV_TheIndependentVerifierReachesEveryVectorsVerdict(string name)
    {
        var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name, "input.json"))).RootElement;
        var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name, "expected.json"))).RootElement;
        var request = input.GetProperty("request");
        var context = input.GetProperty("context");
        var nonces = context.GetProperty("current_nonces").EnumerateArray().Select(n => n.GetString()!).ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.FromUnixTimeSeconds(context.GetProperty("now").GetInt64());

        var verifier = new EnrollProofVerifier(context.GetProperty("audience").GetString()!, () => now, nonces.Contains);
        foreach (var jti in context.GetProperty("spent_jtis").EnumerateArray()) verifier.Spend(jti.GetString()!);

        var slug = verifier.Verify(
            request.GetProperty("agent_id").GetString()!,
            request.GetProperty("alg").GetString()!,
            request.GetProperty("kid").GetString()!,
            Convert.FromBase64String(request.GetProperty("public_key").GetString()!),
            request.TryGetProperty("proof", out var proof) ? proof.GetString() : null);

        var want = expected.GetProperty("verdict").GetString() == "ok" ? null : expected.GetProperty("type").GetString()!["curia/enroll/".Length..];
        Assert.Equal(want, slug);
    }
}
