using System.Diagnostics.CodeAnalysis;
using System.Net;
using Curia.Infrastructure.Lifecycle;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// GF8 (R4.38 (add.), lock 2): the Forum fails to start, loudly and by name, when
/// <c>Curia:Lifecycle:Enabled</c> names a kind the build does not approve, enables rotation or
/// retirement without compromise, or is not <c>none</c> or a comma list over the three kinds. Each case
/// runs <c>Program.cs</c>'s own composition over the embedded (empty) record. Logging and continuing
/// does not count: the host's start throws.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class LifecycleGuardStartupTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    private const string Setting = "Curia:Lifecycle:Enabled";

    [Fact]
    public void R4_38_StartupRefusesAnUnapprovedKind() =>
        AssertStartRefused("compromise", LifecycleApprovalErrors.KindNotApprovedType);

    [Fact]
    public void R4_38_StartupRefusesRotationWithoutCompromise() =>
        AssertStartRefused("rotation", LifecycleApprovalErrors.CompromiseOrderType);

    [Fact]
    public void R4_38_StartupRefusesAnUnreadableOptIn() =>
        AssertStartRefused("rotation;compromise", LifecycleApprovalErrors.OptInUnreadableType);

    /// <summary>The control: the opt-in <c>none</c> starts, and the started Forum serves.</summary>
    [Fact]
    public async Task R4_38_StartupWithOptInNoneStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = forum.WithWebHostBuilder(builder => builder.UseSetting(Setting, "none"));
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private void AssertStartRefused(string optIn, string errorType)
    {
        using var host = forum.WithWebHostBuilder(builder => builder.UseSetting(Setting, optIn));

        var thrown = Assert.ThrowsAny<Exception>(() => host.CreateClient().Dispose());

        var messages = new List<string>();
        for (Exception? e = thrown; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        Assert.True(
            messages.Any(m => m.Contains(errorType, StringComparison.Ordinal)),
            $"the host's start did not refuse with {errorType}: " + string.Join(" | ", messages));
    }
}
