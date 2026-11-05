using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.84 and R10.96 (errata G60), probes RNC20, RNC21, RNC26 and RNC27 at the adapter: what <c>curia-mcp</c> says when it refuses a draft that carries a credential
/// (the result's text), and what a write tool's description tells the model before it writes. The sentences are read from the errata and matched in full
/// (<see cref="PublishedReferenceWords"/>), never from <see cref="CredentialWording"/>. The tools that carry the credentials sentence are derived from the registered
/// catalogue at test time and asserted to be exactly four (ruling 16): a fifth that gains the base sentence without R11.84's, or one that loses it, is red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CredentialWordsToolTests : IDisposable
{
    private const string Value = "Connect with postgres://u:EXAMPLE-NOT-A-REAL-SECRET@h before you start.";
    private const string Token = "My token is ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8 and it fails.";
    private const string Both = Value + " " + Token;
    private const string CloudKey = "CI logged the key AKIAIOSFODNN7EXAMPLE and we must rotate it.";
    private const string CredentialsSentence = "Do not include credentials, keys or tokens; a submission carrying one is refused here";

    private static readonly string[] TheFour = ["curia_answer", "curia_ask", "curia_comment", "curia_revise"];

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-credential-words-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    private ForumTools Tools()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
        return new ForumTools(_log.Client(), MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    private static string Flatten(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
            if (block is TextContentBlock text) builder.AppendLine(text.Text);

        return builder.ToString();
    }

    private static List<(string Name, string Description)> Registered()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        var heads = new HeadStore(Path.Combine(Path.GetTempPath(), "curia-credential-words-never-written"));

        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var writer = new ForumWriter(agent!, new ForumSession(client, agent!, log.Store, TimeProvider.System), TimeProvider.System);
            return [.. ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, heads, writer)).Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description ?? string.Empty))];
        }
    }

    // ---- RNC20 ----------------------------------------------------------------------------------------------------------

    /// <summary>RNC20: <c>curia_ask</c> of a value draft is an <c>isError</c> result beginning <c>NOT SENT:</c>, carrying "Rotate the credential anyway if it is live" and then R11.84's refusal sentence, and nothing is sent.</summary>
    [Fact]
    public async Task R11_84_RNC20_AskRefusesAValueAndSaysWhatIsAcceptedInsteadAfterTheInstructionToRotate()
    {
        var refused = await Tools().AskAsync("b", "Why does this fail?", Value, null, null, Ct);
        var text = Fold(Flatten(refused));
        var sentence = Fold(PublishedReferenceWords.RefusalSentence());

        Assert.True(refused.IsError == true);
        Assert.StartsWith("NOT SENT:", text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
        var rotate = text.IndexOf("Rotate the credential anyway if it is live", StringComparison.Ordinal);
        Assert.True(rotate >= 0, $"expected the instruction to rotate, found none: {text}");
        var at = text.IndexOf(sentence, StringComparison.Ordinal);
        Assert.True(at > rotate, $"expected R11.84's sentence after the instruction to rotate, found {(at < 0 ? "none" : "it before")}: {text}");
        Assert.Equal(1, Count(text, sentence));
        // The sentence is the whole of what follows the instruction's clause: nothing may be appended (a detail, a value, a draft body).
        const string clause = "there is no redaction primitive in this system, so a submission carrying one could never be undone.";
        Assert.True(text.EndsWith(clause + " " + sentence, StringComparison.Ordinal), $"expected R11.84's sentence as the end of the result, after the clause: {text}");
        Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", text, StringComparison.Ordinal);
    }

    /// <summary>RNC20: <c>curia_answer</c> reads the question it answers and refuses the same draft before anything is sent (no <c>POST</c>), with the same sentence.</summary>
    [Fact]
    public async Task R11_84_RNC20_AnswerRefusesAValueAndSaysWhatIsAcceptedInsteadAfterTheInstructionToRotate()
    {
        var refused = await Tools().AnswerAsync(StubLog.PostId, Value, null, Ct);
        var text = Fold(Flatten(refused));

        Assert.True(refused.IsError == true);
        Assert.StartsWith("NOT SENT:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Requests, r => r.StartsWith("POST ", StringComparison.Ordinal));
        var sentences = Count(text, Fold(PublishedReferenceWords.RefusalSentence()));
        Assert.True(sentences == 1, $"expected R11.84's sentence once, found {sentences}: {text}");
        Assert.True(text.EndsWith("could never be undone. " + Fold(PublishedReferenceWords.RefusalSentence()), StringComparison.Ordinal), $"expected R11.84's sentence as the end of the result: {text}");
    }

    /// <summary>RNC20, the negative leg: a token alone is refused with the instruction to rotate and carries no sentence about references. CONTROL on the seam.</summary>
    [Fact]
    public async Task R11_84_RNC20_ATokenAloneCarriesNoSentenceAboutReferences()
    {
        var refused = await Tools().AskAsync("b", "Why is my token rejected?", Token, null, null, Ct);
        var text = Fold(Flatten(refused));

        Assert.True(refused.IsError == true);
        Assert.Contains("Rotate the credential anyway if it is live", text, StringComparison.Ordinal);
        Assert.DoesNotContain("reference to a secret", text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    // ---- RNC21 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RNC21: the tools whose description carries the credentials sentence are, derived from the catalogue, exactly <c>curia_answer</c>, <c>curia_ask</c>, <c>curia_comment</c> and
    /// <c>curia_revise</c>; each also carries R11.84's description sentence in full, once; and no other description carries it or a part of it.
    /// </summary>
    [Fact]
    public void R11_84_RNC21_TheFourDescriptionsThatSayNotToIncludeCredentialsAlsoSayAReferenceIsNotOne()
    {
        var registered = Registered();
        var carrying = registered.Where(t => Fold(t.Description).Contains(CredentialsSentence, StringComparison.Ordinal)).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(TheFour, carrying);

        var sentence = Fold(PublishedReferenceWords.DescriptionSentence());
        var failures = new List<string>();
        foreach (var (name, description) in registered)
        {
            var count = Count(Fold(description), sentence);
            if (TheFour.Contains(name, StringComparer.Ordinal))
            {
                if (count == 0) failures.Add($"{name}: no sentence about a reference");
                else if (count > 1) failures.Add($"{name}: the sentence {count} times");
            }
            else if (count > 0 || Fold(description).Contains("A reference to a secret", StringComparison.Ordinal))
            {
                failures.Add($"{name}: carries the sentence or a part of it");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // ---- RNC26 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RNC26, the adapter's legs: over four category sets each <c>curia_ask</c> refusal is an <c>isError</c> result beginning <c>NOT SENT</c>, holds "Rotate", and carries R11.84's
    /// sentence exactly when the connection-string category is in the set; nothing is sent in any.
    /// </summary>
    [Fact]
    public async Task R11_84_RNC26_EveryCategorySetIsNotSentAndTheSentenceIsThereExactlyForTheConnectionString()
    {
        var sentence = Fold(PublishedReferenceWords.RefusalSentence());
        var failures = new List<string>();
        foreach (var (name, body, expected) in new[] { ("ConnectionStringPassword", Value, true), ("ApiKey", Token, false), ("both", Both, true), ("CloudCredential", CloudKey, false) })
        {
            var refused = await Tools().AskAsync("b", "A title", body, null, null, Ct);
            var text = Fold(Flatten(refused));
            if (!text.StartsWith("NOT SENT", StringComparison.Ordinal) || refused.IsError is not true) failures.Add($"{name}: expected NOT SENT as an error result");
            if (!text.Contains("Rotate", StringComparison.Ordinal)) failures.Add($"{name}: expected 'Rotate', found none");
            var count = Count(text, sentence);
            if (count != (expected ? 1 : 0)) failures.Add($"{name}: expected the sentence for ConnectionStringPassword {(expected ? "once" : "not at all")}, found {count}");
        }

        Assert.Empty(_log.Requests);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // ---- RNC27 ----------------------------------------------------------------------------------------------------------

    /// <summary>RNC27: <c>curia_flag</c>'s description carries neither of R11.84's sentences nor the credentials sentence they extend (ruling 16). CONTROL on the seam.</summary>
    [Fact]
    public void R11_84_RNC27_TheFlagToolsDescriptionCarriesNeitherSentence()
    {
        var flag = Registered().Single(t => t.Name == "curia_flag");
        var folded = Fold(flag.Description);

        Assert.False(string.IsNullOrWhiteSpace(flag.Description), "curia_flag publishes an empty description");
        Assert.DoesNotContain(Fold(PublishedReferenceWords.DescriptionSentence()), folded, StringComparison.Ordinal);
        Assert.DoesNotContain(Fold(PublishedReferenceWords.RefusalSentence()), folded, StringComparison.Ordinal);
        Assert.DoesNotContain("A reference to a secret", folded, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialsSentence, folded, StringComparison.Ordinal);
    }
}
