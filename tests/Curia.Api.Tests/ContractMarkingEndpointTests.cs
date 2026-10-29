using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G47, R10.90), probes CHN45 and CHN47 against a real Forum: the served Reader Contract keeps its version and its nine clauses, whose text
/// is §10.7's as the white paper prints it (parsed from the white paper at test time, never read from <see cref="ReaderContract"/>), and carries the
/// non-clause member <c>marking</c> with <c>token_member</c>, <c>escape</c> and <c>rule</c>; and the member the contract names is a member a marked
/// response of the same Forum carries, which the rule's sentence names.
///
/// <para>The member's name and its three members' names and values are R10.90's code spans, read from the errata by <see cref="PublishedChainWords"/>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ContractMarkingEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The nine clauses of §10.7 as the white paper prints them: the block quote's numbered items, emphasis marks removed, lines joined with one space, and the em dash spelled <c>--</c> as the Forum's ASCII text spells it.</summary>
    private static string[] WhitePaperClauses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "curia-agent-forum-WHITEPAPER.md"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var text = File.ReadAllText(Path.Combine(dir!.FullName, "curia-agent-forum-WHITEPAPER.md"));
        var start = text.IndexOf("> **The Cūria Reader Contract**", StringComparison.Ordinal);
        Assert.True(start > 0, "the white paper no longer prints the Reader Contract");
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var block = string.Join('\n', text[start..end].Split('\n').Skip(1).Select(l => l.TrimStart('>', ' ')));
        var clauses = NumberedItem().Split(block).Where(s => s.Trim().Length > 0).Select(s => Regex.Replace(s.Replace("*", string.Empty, StringComparison.Ordinal).Replace(char.ConvertFromUtf32(0x2014), "--", StringComparison.Ordinal), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Trim()).ToArray();
        Assert.Equal(9, clauses.Length);
        return clauses;
    }

    [GeneratedRegex(@"^\d+\.\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedItem();

    private async Task<JsonElement> ContractAsync() => await forum.Client.GetFromJsonAsync<JsonElement>(ReaderContract.WellKnownPath, Ct);

    /// <summary>CHN45. The version stays <c>v1</c>, the nine clauses equal §10.7's text, and the contract carries <c>marking</c> as R10.90 gives it.</summary>
    [Fact]
    public async Task R10_90_CHN45_TheServedContractCarriesMarkingBesideTheUnchangedClauses()
    {
        var names = PublishedChainWords.Marking();
        var contract = await ContractAsync();

        Assert.Equal("v1", contract.GetProperty("version").GetString());
        var served = contract.GetProperty("clauses").EnumerateArray().Select(c => Regex.Replace(c.GetProperty("text").GetString()!, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Trim()).ToArray();
        Assert.Equal(WhitePaperClauses(), served);

        Assert.True(contract.TryGetProperty(names.Member, out var marking), $"Expected: member {names.Member} Actual: absent from [{string.Join(", ", contract.EnumerateObject().Select(p => p.Name))}]");
        Assert.Equal(JsonValueKind.Object, marking.ValueKind);
        Assert.Equal(names.TokenMemberValue, marking.GetProperty(names.TokenMember).GetString());
        Assert.Equal(names.EscapeValue, marking.GetProperty(names.Escape).GetString());
        Assert.False(string.IsNullOrWhiteSpace(marking.GetProperty(names.Rule).GetString()));
    }

    /// <summary>
    /// CHN47. The sentence is about the wire: <c>token_member</c> names a member that a marked response of this Forum carries, with a token in it, and the rule's
    /// sentence names it.
    /// </summary>
    [Fact]
    public async Task R10_90_CHN47_TheRuleIsAboutTheWire()
    {
        var names = PublishedChainWords.Marking();
        var contract = await ContractAsync();
        Assert.True(contract.TryGetProperty(names.Member, out var marking), $"Expected: member {names.Member} Actual: absent");
        var tokenMember = marking.GetProperty(names.TokenMember).GetString()!;

        var agent = ForumAgent.Create("https://agents.example/marking-" + Guid.NewGuid().ToString("N")[..8], "mk-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
        using var posted = await dpop.PostAsync(forum.Client, "http://localhost/v1/posts", token, agent.SignQuestion("board-" + Guid.NewGuid().ToString("N")[..8], "A body about marks.", "Marks", forum.Now), forum.Now, Ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var postId = JsonDocument.Parse(await posted.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("post_id").GetString()!;

        var marked = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}?marking=datamark", Ct);

        Assert.True(marked.GetProperty("provenance").TryGetProperty(tokenMember, out var carried) && carried.ValueKind == JsonValueKind.String, $"token_member names {tokenMember}, which a marked response does not carry");
        Assert.Contains(tokenMember, marking.GetProperty(names.Rule).GetString()!, StringComparison.Ordinal);
    }
}
