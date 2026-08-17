using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
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
    /// </summary>
    [Theory]
    [InlineData("ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("postgres://curia:hunter2@db.internal:5432/forum")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk")]
    public void ContentContainingASyntheticCredential_HardRejected_ValueNotLogged(string credential)
    {
        var content = $"Reporting an incident. The leaked value was {credential} -- please advise.";
        var bytes = Encoding.UTF8.GetBytes(content);

        Assert.True(ContentScreener.Screen(bytes).TryGetValue(out var result, out _));

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
    [Fact]
    public void ContentWithoutACredentialIsNotRejected()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "Reporting an incident. The value has been rotated and the old one is revoked.");

        Assert.True(ContentScreener.Screen(bytes).TryGetValue(out var result, out _));

        Assert.NotEqual(ScreeningOutcome.Rejected, result!.Outcome);
        Assert.True(result.MayPersist);
    }
}
