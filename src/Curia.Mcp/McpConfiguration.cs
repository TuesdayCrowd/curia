using Curia.Domain.Primitives;
using Curia.Domain.Serving;

namespace Curia.Mcp;

/// <summary>
/// What the adapter is configured with: which Forum, and how its output is marked.
///
/// <para><b>Marking is a per-session setting</b> (R10.12), and for a stdio server the session is the
/// process, so the setting is its configuration. It defaults to datamarking because R10.13 makes
/// that the MCP default — the opposite of the HTTP API's — on the stated ground that this output
/// "goes directly into a model's context". Nothing downstream parses it first, which is both why
/// the default differs and why getting it wrong is worse here.</para>
///
/// <para><b>An unmodelled value is a refusal, never an implied default</b> (R10.46's idiom, R10.51's
/// obligation). A mis-typed marking that fell through to <see cref="MarkingMode.None"/> would turn
/// R10.13's default silently off while the envelope truthfully reported <c>"marking": "None"</c> —
/// a deliberate-looking choice nobody made. That exact defect was live on the HTTP surface until
/// this stage.</para>
///
/// <para><b>The Forum has no default address.</b> The <c>curia</c> CLI falls back to a localhost one
/// because a developer running it by hand is its common case; a server an agent framework launches
/// has no such case, and a default would silently point a consuming model at a Forum nobody chose.
/// This repository already holds that line for <c>CURIA_EVENTS_POSTGRES</c> and
/// <c>CURIA_ISSUER_SIGNING_KEY_PEM</c>: startup fails rather than running as something else.</para>
/// </summary>
internal sealed record McpConfiguration(Uri Forum, MarkingMode Marking)
{
    internal const string ForumVariable = "CURIA_FORUM";
    internal const string MarkingVariable = "CURIA_MCP_MARKING";

    /// <summary>
    /// The published request vocabulary of R10.12, spelled as the HTTP surface spells it. The
    /// absent case is <i>not</i> in this map: omitting the variable means the R10.13 default, while
    /// naming a value the Forum does not model is a refusal, and collapsing the two would make the
    /// default unreachable by mistake and the mistake unreportable.
    /// </summary>
    private static readonly Dictionary<string, MarkingMode> Vocabulary = new(StringComparer.Ordinal)
    {
        ["datamark"] = MarkingMode.Datamark,
        ["delimiters"] = MarkingMode.DelimitersOnly,
        ["none"] = MarkingMode.None,
    };

    /// <summary>Reads the environment. Split from <see cref="Read"/> so the parsing is testable without one.</summary>
    internal static Result<McpConfiguration> FromEnvironment() => Read(
        Environment.GetEnvironmentVariable(ForumVariable),
        Environment.GetEnvironmentVariable(MarkingVariable));

    internal static Result<McpConfiguration> Read(string? forum, string? marking)
    {
        // Absolute is not enough. On Unix, Uri.TryCreate("/v1/search", UriKind.Absolute, …)
        // SUCCEEDS, yielding file:///v1/search -- a leading slash is a valid absolute file path.
        // Without the scheme check a mistyped CURIA_FORUM would point the adapter at the local
        // filesystem, and the first thing it would report is a transport error rather than a
        // configuration one. Found by the test, not by reading.
        if (string.IsNullOrWhiteSpace(forum)
            || !Uri.TryCreate(forum, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return Result<McpConfiguration>.Fail(new Error(
                "curia/mcp/forum-not-configured",
                $"{ForumVariable} must name the absolute URL of a Cūria Forum",
                string.IsNullOrWhiteSpace(forum)
                    ? "It is unset. There is deliberately no default: a server launched by an agent " +
                      "framework has no local Forum to assume, and assuming one would point a " +
                      "consuming model at a corpus nobody chose."
                    : $"received={forum}; it is not an absolute http or https URL."));
        }

        if (string.IsNullOrWhiteSpace(marking))
            return Result<McpConfiguration>.Ok(new McpConfiguration(uri, MarkingMode.Datamark));

        return Vocabulary.TryGetValue(marking, out var mode)
            ? Result<McpConfiguration>.Ok(new McpConfiguration(uri, mode))
            : Result<McpConfiguration>.Fail(new Error(
                "curia/mcp/unknown-marking",
                "That is not a marking this adapter serves, and it is refused rather than " +
                "defaulted — an unrecognised value silently meaning 'unmarked' is the one outcome " +
                "R10.13 exists to prevent",
                $"received={marking}; the published vocabulary is " +
                $"{string.Join(", ", Vocabulary.Keys)}, or the variable unset for R10.13's " +
                "datamarking default."));
    }
}
