using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Serving;
using Curia.Mcp;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// The adapter's configuration, which is where two requirements land that have nowhere else to go.
///
/// <para><b>R10.13</b> makes datamarking on by default for the MCP adapter, "whose output goes
/// directly into a model's context" — the opposite of the HTTP API's default, and R10.12 says the
/// mechanism is "a per-session setting on the MCP adapter" rather than a query parameter. For a
/// stdio server the session is the process, so the setting is its configuration.</para>
///
/// <para><b>R10.51 / R10.46</b> say an unmodelled value is a failure rather than an implied default.
/// A mis-typed marking that fell through to <c>None</c> would turn R10.13's default silently off and
/// describe it as a deliberate choice — which is exactly the defect this session fixed on the HTTP
/// surface, and it would be worse here, because the whole reason MCP defaults the other way is that
/// nothing downstream will parse the content before a model reads it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class McpConfigurationTests
{
    private static string? Missing => null;

    [Fact]
    public void R10_13_MarkingDefaultsToDatamarkWhenNothingIsConfigured()
    {
        var configured = McpConfiguration.Read("https://forum.example/", Missing);

        Assert.True(configured.TryGetValue(out var config, out var error), error?.Title);
        Assert.Equal(MarkingMode.Datamark, config!.Marking);
    }

    [Theory]
    [InlineData("datamark", MarkingMode.Datamark)]
    [InlineData("delimiters", MarkingMode.DelimitersOnly)]
    [InlineData("none", MarkingMode.None)]
    public void R10_12_TheMarkingVocabularyIsThePublishedOne(string wire, MarkingMode expected)
    {
        var configured = McpConfiguration.Read("https://forum.example/", wire);

        Assert.True(configured.TryGetValue(out var config, out var error), error?.Title);
        Assert.Equal(expected, config!.Marking);
    }

    /// <summary>
    /// The failure R10.13 exists to prevent, one layer up: a value nobody models silently becoming
    /// "no marking at all" on the one surface whose output reaches a model unparsed.
    /// </summary>
    [Theory]
    [InlineData("datamarking")]
    [InlineData("Datamark")]
    [InlineData("off")]
    public void R10_51_AnUnmodelledMarkingIsRefusedRatherThanServedUnmarked(string wire)
    {
        var configured = McpConfiguration.Read("https://forum.example/", wire);

        Assert.False(configured.TryGetValue(out _, out var error));
        Assert.Equal("curia/mcp/unknown-marking", error!.Type);
        Assert.Contains(wire, error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Forum's address has no default. `curia` the CLI falls back to a localhost default because
    /// a developer running it locally is its common case; a server an agent framework launches has
    /// no such case, and a default here would silently point a consuming model at a Forum nobody
    /// chose — which is the repository's own rule for CURIA_EVENTS_POSTGRES and
    /// CURIA_ISSUER_SIGNING_KEY_PEM: fail loudly rather than run as something else.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("/v1/search")]
    public void TheForumAddressIsRequiredAndAbsolute(string? raw)
    {
        var configured = McpConfiguration.Read(raw, Missing);

        Assert.False(configured.TryGetValue(out _, out var error));
        Assert.Equal("curia/mcp/forum-not-configured", error!.Type);
    }

    [Fact]
    public void TheForumAddressIsCarriedThrough()
    {
        var configured = McpConfiguration.Read("https://forum.example/base/", Missing);

        Assert.True(configured.TryGetValue(out var config, out var error), error?.Title);
        Assert.Equal(new Uri("https://forum.example/base/"), config!.Forum);
    }
}
