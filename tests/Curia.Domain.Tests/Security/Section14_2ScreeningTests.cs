using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;
using Xunit;

namespace Curia.Domain.Tests.Security;

/// <summary>
/// §14.2's screening bullet, verbatim, one test per bullet -- the same arrangement
/// <c>Curia.Canon.Tests.Security.Section14_2Tests</c> uses for the canonicalization bullets.
///
/// <para>Kept separate from <c>ContentScreenerTests</c> even though the assertions overlap: those
/// tests are about the screener's design, this one is about a published obligation, and a
/// refactor that changed the design should have to think about whether the obligation still
/// holds rather than deleting the evidence along with the test.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the §14.2 bullet they enforce.")]
public sealed class Section14_2ScreeningTests
{
    /// <summary>
    /// §14.2: <i>"Content containing a synthetic credential → hard-rejected, value not logged"</i>
    ///
    /// <para>Both halves in one test, because the bullet is one obligation: a rejection that
    /// logged the credential would satisfy the first clause and defeat the point. The "not logged"
    /// half is checked the way it would actually fail -- serialize everything the screener hands
    /// back and look for the credential in it.</para>
    ///
    /// <para>Each credential is screened in both forms SCREEN receives content: bare, as a flag's
    /// rationale is, and as the <c>body</c> of a canonical post envelope, as ingest screens it. The
    /// bullet is about submitted content, and a post is the commonest submission.</para>
    /// </summary>
    [Theory]
    [InlineData("ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o", false)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", false)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", false)]
    [InlineData("postgres://curia:hunter2@db.internal:5432/forum", false)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", false)]
    [InlineData("ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o", true)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", true)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", true)]
    [InlineData("postgres://curia:hunter2@db.internal:5432/forum", true)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", true)]
    public void ContentContainingASyntheticCredential_HardRejected_ValueNotLogged(string credential, bool enveloped)
    {
        var content = $"Reporting an incident. The leaked value was {credential} -- please advise.";

        Assert.True(Screen(content, enveloped).TryGetValue(out var result, out _));

        // Hard-rejected.
        Assert.Equal(ScreeningOutcome.Rejected, result!.Outcome);
        Assert.False(result.MayPersist);

        // Value not logged: nothing the screener returns renders the credential, by any route an
        // operator or a structured logger would take.
        var rendered = string.Concat(
            result.ToString(),
            result.Annotations.ToString(),
            string.Join(" ", result.Annotations.Flags),
            JsonSerializer.Serialize(result),
            JsonSerializer.Serialize(result.Annotations));

        Assert.DoesNotContain(credential, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control for the bullet above. Without it, a screener that rejected
    /// <i>everything</i> would pass every case -- and "hard-rejected" would be measuring nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentWithoutACredentialIsNotRejected(bool enveloped)
    {
        const string content = "Reporting an incident. The value has been rotated and the old one is revoked.";

        Assert.True(Screen(content, enveloped).TryGetValue(out var result, out _));

        Assert.NotEqual(ScreeningOutcome.Rejected, result!.Outcome);
        Assert.True(result.MayPersist);
    }

    /// <summary>
    /// SCREEN over <paramref name="content"/>: bare through <see cref="ContentScreener.ScreenText"/>,
    /// or as the <c>body</c> of a canonical post envelope through
    /// <see cref="ContentScreener.ScreenEnvelope"/>.
    /// </summary>
    private static Result<ScreeningResult> Screen(string content, bool enveloped) =>
        enveloped
            ? ContentScreener.ScreenEnvelope(Encoding.UTF8.GetBytes(CanonicalEnvelope(content)))
            : ContentScreener.ScreenText(Encoding.UTF8.GetBytes(content));

    /// <summary>A post envelope around <paramref name="body"/>, canonicalized as production does it.</summary>
    private static string CanonicalEnvelope(string body)
    {
        var envelope = new JsonValue.Object(
        [
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String("question")),
            new("author", new JsonValue.String("https://agents.example/section-14-2")),
            new("board", new JsonValue.String("general")),
            new("title", new JsonValue.String("A leaked credential")),
            new("body", new JsonValue.String(body)),
            new("code_blocks", new JsonValue.Array([])),
            new("refs", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-09-25T00:00:00.0000000+00:00")),
            new("nonce", new JsonValue.String("00000000000000000000000000000000")),
        ]);

        Assert.True(
            CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out var error),
            error?.Type);
        return Encoding.UTF8.GetString(canonical.Span);
    }
}
