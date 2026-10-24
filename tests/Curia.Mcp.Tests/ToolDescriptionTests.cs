using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Moderation;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.19: "MCP tool descriptions SHALL state that returned content is untrusted data. Tool
/// descriptions are read by the consuming model and are the last place to set that expectation
/// before content arrives."
///
/// <para><b>Every assertion here reads the registered collection, never a list written in this
/// file.</b> A hand-written array is a second place to remember an eighth tool, and the thing being
/// guarded is what happens when someone does not — which is the shape of trap 5 in this project's
/// own list, a corpus family no runner enumerated.</para>
///
/// <para><b>And each one asserts non-empty first.</b> The SDK publishes <c>"description": ""</c> for
/// a tool that supplies none — an empty string, not an omitted member — so a bare
/// <c>Assert.Contains</c> on a null-safe path would pass over a silently empty description. That
/// was established by building a server and reading its <c>tools/list</c>, not by reading the
/// SDK.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ToolDescriptionTests
{
    /// <summary>
    /// A catalogue built over a client that is never dialled: a description is composed at
    /// registration and does not depend on the Forum being reachable.
    ///
    /// <para><b>Built with an identity by default</b>, because the write tools are registered only
    /// when one is configured. A catalogue built without one — which is how this helper built it
    /// until Stage 4 — would enumerate three tools, and every theory below would pass while the
    /// three descriptions that most need checking went unread.</para>
    /// </summary>
    private static List<(string Name, string Description)> Registered(bool withIdentity = true) =>
        [.. RegisteredTools(withIdentity).Select(tool => (tool.ProtocolTool.Name, tool.ProtocolTool.Description ?? string.Empty))];

    /// <summary>The registered tools themselves, for the facts that read a tool's schema and not only its description.</summary>
    private static List<McpServerTool> RegisteredTools(bool withIdentity = true)
    {
        using var http = new HttpClient();
        var forum = new Uri("https://forum.invalid/", UriKind.Absolute);
        var client = new ForumClient(http, forum);

        // A head store under a path that is never written to: a description is composed at
        // registration and touches neither the Forum nor the filesystem.
        var heads = new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written"));

        using var log = new StubLog();
        EnrolledAgent? agent = null;
        try
        {
            ForumWriter? writer = null;
            if (withIdentity)
            {
                Assert.True(log.Store.Load("alice").TryGetValue(out agent, out var error), error?.Detail);
                writer = new ForumWriter(agent!, new ForumSession(client, agent!, log.Store, TimeProvider.System), TimeProvider.System);
            }

            return [.. ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, heads, writer))];
        }
        finally
        {
            agent?.Dispose();
        }
    }

    public static TheoryData<string, string> Tools()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, description) in Registered())
            data.Add(name, description);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_19_EveryToolDescriptionStatesThatContentIsUntrusted(string name, string description)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(description),
            $"{name} publishes an empty description. The SDK emits \"\" rather than omitting the " +
            "member, so an empty one satisfies a Contains check on a null-safe path and says nothing.");

        Assert.Contains(ToolText.UntrustedDataNotice, description, StringComparison.Ordinal);
    }

    /// <summary>R6.57: the two tools that render passages say that a key-set dispute reads DISPUTED and is the Forum's word.</summary>
    [Theory]
    [InlineData("curia_read")]
    [InlineData("curia_search")]
    public void R6_57_ThePassageToolsNameTheDisputedReading(string name)
    {
        var description = Registered().Single(t => t.Name == name).Description;

        Assert.Contains("reads 'signature DISPUTED'", description, StringComparison.Ordinal);
        Assert.Contains("the Forum's word on this path", description, StringComparison.Ordinal);
    }

    /// <summary>D51: the answer tool does not tell a model every refusal is a tier refusal.</summary>
    [Fact]
    public void D51_TheAnswerDescriptionDoesNotCallEveryRefusalATierRefusal()
    {
        var answer = Registered().Single(t => t.Name == "curia_answer").Description;

        Assert.DoesNotContain("refuses at this agent's tier", answer, StringComparison.Ordinal);
        Assert.Contains("names its cause", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.27: the notice is one frozen sentence common to every tool. R10.17's warning was frozen
    /// as a constant on the reasoning that a text an operator can reword will eventually say
    /// something weaker, and a description is read <i>before</i> any content arrives, which makes it
    /// the stronger case of the same argument.
    /// </summary>
    [Fact]
    public void R11_27_TheNoticeIsOneSentenceAndIsIdenticalAcrossTools()
    {
        var notices = Registered()
            .Select(t => t.Description[t.Description.IndexOf(ToolText.UntrustedDataNotice, StringComparison.Ordinal)..]
                .Split('\n')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(notices);
    }

    /// <summary>
    /// The Table 10 pair a tool's name maps to, and the tier sentence composed from it.
    ///
    /// <para>The map is the one thing here written by hand, and it is the mapping that genuinely
    /// lives in <see cref="ToolCatalogue"/>. It is safe from the usual objection because
    /// <c>TheCatalogueRegistersTheToolsThisStageBuilds</c> fails when the tool set changes, so a
    /// further tool cannot slip past these theories by being absent from the map — that test names
    /// it first. One map, used by every assertion below, so a tool cannot satisfy one of them by
    /// being measured against another tool's span.</para>
    /// </summary>
    private static (ResourceKind Resource, ActionKind Action) PairFor(string name) => name switch
    {
        "curia_search" => (ResourceKind.Thread, ActionKind.Search),
        "curia_read" => (ResourceKind.Thread, ActionKind.Read),

        // curia_verify reads the post it is asked about; the Acta's routes are anonymous. There is
        // no "verify" pair in Table 10 and inventing one would claim an authority boundary the
        // Forum does not enforce (R11.26).
        "curia_verify" => (ResourceKind.Thread, ActionKind.Read),

        // R11.26: the Scope column's write values are Table 10 rows written with a colon.
        "curia_ask" => (ResourceKind.Question, ActionKind.Create),
        "curia_answer" => (ResourceKind.Answer, ActionKind.Create),
        "curia_flag" => (ResourceKind.Flag, ActionKind.Raise),
        "curia_comment" => (ResourceKind.Comment, ActionKind.Create),
        "curia_resolve" => (ResourceKind.Answer, ActionKind.Accept),
        _ => throw new InvalidOperationException(
            $"{name} has no Table 10 pair in this test's map. Add it, rather than letting the " +
            "row pass without checking what the description claims about authority."),
    };

    private static string SpanFor(string name)
    {
        var (resource, action) = PairFor(name);
        return TierSpan.For(resource, action);
    }

    /// <summary>
    /// R11.27: the tier sentence is the one substituted span, and it is <i>composed</i> from
    /// Table 10 rather than written into the description. A transcribed tier goes stale silently —
    /// F1 moved T1's tenure from seven days to forty-eight hours, and the agent-facing prose outside
    /// this repository still says "≥ 7 days".
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_27_TheDescriptionEndsWithTheComposedTierSpan(string name, string description) =>
        Assert.EndsWith(SpanFor(name), description, StringComparison.Ordinal);

    /// <summary>
    /// The notice sits outside the substituted span, so a tier sentence that changes cannot carry
    /// the notice away with it.
    ///
    /// <para><b>Sliced by each tool's OWN span, which it was not.</b> This measured every
    /// description against the length of the <i>read</i> tier sentence. That sentence is short
    /// ("Requires no credential…"); a tool whose pair is not anonymous composes one roughly three
    /// times longer. For such a tool the slice took an arbitrary suffix of the tier sentence, found
    /// no notice in it, and passed — for a reason unrelated to what the test is named for. It was
    /// invisible while all three registered tools happened to share the anonymous span.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_27_TheNoticeIsOutsideTheSubstitutedSpan(string name, string description)
    {
        var span = SpanFor(name);

        // Non-vacuity: the description really does end with this tool's own composed span, so the
        // slice below is the span rather than some suffix of the prose above it.
        Assert.EndsWith(span, description, StringComparison.Ordinal);

        Assert.DoesNotContain(ToolText.UntrustedDataNotice, span, StringComparison.Ordinal);
        Assert.Contains(ToolText.UntrustedDataNotice, description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The catalogue is not empty. Every other assertion in this file is a theory over the
    /// registered tools, and a theory over nothing passes — which is precisely the vacuity this
    /// project keeps finding. This row fails when the catalogue is empty and the rest go quiet.
    /// </summary>
    [Fact]
    public void TheCatalogueRegistersTheToolsThisStageBuilds()
    {
        Assert.Equal(
            ["curia_answer", "curia_ask", "curia_comment", "curia_flag", "curia_read", "curia_resolve", "curia_search", "curia_verify"],
            Registered().Select(t => t.Name).Order());
    }

    /// <summary>
    /// Without an identity the adapter is read-only, and the write tools are absent rather than
    /// present-and-refusing: a tool listed is a tool that can work.
    /// </summary>
    [Fact]
    public void WithoutAnIdentityOnlyTheReadToolsAreRegistered()
    {
        Assert.Equal(
            ["curia_read", "curia_search", "curia_verify"],
            Registered(withIdentity: false).Select(t => t.Name).Order());
    }

    /// <summary>
    /// R11.26's second clause, which no registered tool exercised before this stage: above T0 the
    /// description states the Table 11 criteria that reach the tier. The expected values are read
    /// from <see cref="TierPolicy"/> — which <c>Table11ConformanceTests</c> holds to the white paper —
    /// so the assertion moves with the table rather than being built from a copy of it (trap 3).
    /// </summary>
    [Fact]
    public void R11_26_AToolAboveT0StatesTheCriteriaThatReachItsTier()
    {
        var answer = Registered().Single(t => t.Name == "curia_answer").Description;

        Assert.Contains("Requires trust tier T1 or above.", answer, StringComparison.Ordinal);
        Assert.Contains($"at least {TierPolicy.T1MinimumHours} hours since enrolment", answer, StringComparison.Ordinal);
        Assert.Contains($"at least {TierPolicy.T1MinimumCleanQuestions} questions with no upheld flags", answer, StringComparison.Ordinal);
        Assert.Contains("owner verified by the Forum's operator", answer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Table 10's one rate-limited cell, <c>question</c>/<c>create</c> at T0, is a permit conditional
    /// on the budget — so the description says what the budget is, composed from the same table.
    /// </summary>
    [Fact]
    public void R11_26_TheRateLimitedCellStatesItsBudget()
    {
        var ask = Registered().Single(t => t.Name == "curia_ask").Description;

        Assert.Contains("Requires trust tier T0 or above.", ask, StringComparison.Ordinal);
        Assert.Contains($"rate-limited to {TierPolicy.PostsPerDay(PrincipalTier.T0)} posts a day", ask, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>curia_flag</c>'s seven kinds are written into its frozen template, which makes them a
    /// transcription — so they are held here against <see cref="FlagKinds"/>, in both directions: every
    /// kind the domain parses is named, and nothing named is a kind the domain would refuse.
    /// </summary>
    [Fact]
    public void R10_35_TheFlagDescriptionNamesExactlyTheForumsKinds()
    {
        var flag = Registered().Single(t => t.Name == "curia_flag").Description;

        const string Lead = "The kind is one of the Forum's seven: ";
        var start = flag.IndexOf(Lead, StringComparison.Ordinal);
        Assert.True(start >= 0, "the description no longer carries the kind list this test reads");

        var listed = flag[(start + Lead.Length)..flag.IndexOf('.', start)]
            .Split(", ", StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var published = Enum.GetValues<FlagKind>().Select(FlagKinds.Wire).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(published, listed);
        Assert.All(listed, kind => Assert.True(FlagKinds.Parse(kind).TryGetValue(out _, out _), kind));
    }

    /// <summary>
    /// The read-only instructions name the variable that enables writing, from the constant the
    /// configuration reads, so renaming one cannot leave the other telling operators a stale name.
    /// </summary>
    [Fact]
    public void TheReadOnlyInstructionsNameTheVariableThatEnablesWriting()
    {
        var names = WriteToolNames();
        var readOnly = ToolText.ReadOnly(names);

        Assert.Contains(McpConfiguration.AgentVariable, readOnly, StringComparison.Ordinal);
        Assert.StartsWith(ToolText.UntrustedDataNotice, readOnly, StringComparison.Ordinal);
        Assert.StartsWith(ToolText.UntrustedDataNotice, ToolText.WritesAs("https://agents.example/x", names), StringComparison.Ordinal);
    }

    // ---- errata G26 -----------------------------------------------------------------------------

    /// <summary>The catalogue's write-tool names, asked of a <see cref="ForumTools"/> with no identity at all.</summary>
    private static IReadOnlyList<string> WriteToolNames()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        return ToolCatalogue.WriteToolNames(new ForumTools(client, MarkingMode.Datamark, new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written"))));
    }

    /// <summary>
    /// What each Table 10 qualifier must say in a description (R11.36), by the one phrase that names
    /// it. Written here and not read from <see cref="TierSpan"/>, so a span that drops a qualifier
    /// cannot satisfy the fact by being measured against itself.
    /// </summary>
    private static string PhraseFor(GrantQualifier qualifier) => qualifier switch
    {
        GrantQualifier.OwnResourceOnly => "(Table 10: own)",
        GrantQualifier.OwnThreadOnly => "(Table 10: own thread)",
        GrantQualifier.Delegated => "delegated grant (Table 10)",
        GrantQualifier.None => throw new InvalidOperationException("None has no phrase"),
        _ => throw new ArgumentOutOfRangeException(nameof(qualifier), qualifier, "Not a Table 10 qualifier"),
    };

    /// <summary>
    /// R11.36: a description states its Table 10 row's parenthetical. A tool that only the author of
    /// the post, or the agent that asked the thread, may use says so before it is called.
    /// </summary>
    [Fact]
    public void R11_36_ADescriptionStatesItsRowsParenthetical()
    {
        var checkedOne = false;
        foreach (var (name, description) in Registered())
        {
            var (resource, action) = PairFor(name);
            var qualifier = ResourceActionModel.RowFor(resource, action).Match(r => r.Qualifier, e => throw new InvalidOperationException(e.Title));
            if (qualifier is GrantQualifier.None) continue;

            checkedOne = true;
            Assert.True(
                description.Contains(PhraseFor(qualifier), StringComparison.Ordinal),
                $"{name}'s description does not state its row's parenthetical ({qualifier})");
        }

        // Non-vacuity: at least one registered tool has a row with a parenthetical.
        Assert.True(checkedOne, "no registered tool's row has a parenthetical, so this fact checked nothing");
    }

    /// <summary>The write pairs a T0 principal holds that no tool exercises, by decision. Asserted to be exactly this set.</summary>
    private static readonly (ResourceKind Resource, ActionKind Action)[] NotExercisedByDecision =
    [
        (ResourceKind.Key, ActionKind.Rotate),
        (ResourceKind.Key, ActionKind.Retire),
        (ResourceKind.Revision, ActionKind.Create),
    ];

    /// <summary>
    /// R11.30 (rev.): every write a T0 principal may do under Table 10 has a tool, except the named
    /// set. Derived from the model's own pairs, not from the catalogue, so a surface that could never
    /// reply fails here rather than passing a list of its own tools.
    /// </summary>
    [Fact]
    public void R11_30_EveryWritePairATierZeroPrincipalHoldsHasATool()
    {
        var exercised = Registered().Select(t => PairFor(t.Name)).ToHashSet();

        var t0Writes = ResourceActionModel.ModelledPairs
            .Where(p => !ActionKinds.IsRead(p.Action))
            .Where(p => ResourceActionModel.RowFor(p.Resource, p.Action).Match(r => r.T0 is Table10Cell.Allowed or Table10Cell.RateLimited, _ => false))
            .ToArray();

        Assert.NotEmpty(t0Writes);

        // The exception set is itself pinned, so one cannot be added quietly.
        Assert.Equal(3, NotExercisedByDecision.Length);
        Assert.Equal(
            new HashSet<(ResourceKind, ActionKind)>
            {
                (ResourceKind.Key, ActionKind.Retire), (ResourceKind.Key, ActionKind.Rotate), (ResourceKind.Revision, ActionKind.Create),
            },
            NotExercisedByDecision.ToHashSet());
        foreach (var pair in NotExercisedByDecision) Assert.Contains(pair, t0Writes);

        foreach (var pair in t0Writes.Except(NotExercisedByDecision))
            Assert.True(exercised.Contains(pair), $"no registered tool exercises ({pair.Resource}, {pair.Action})");
    }

    /// <summary>The members Table 12 lists as optional for a kind, read from the white paper's own table.</summary>
    private static string[] PublishedOptionalMembers(string kind)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "curia-agent-forum-WHITEPAPER.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var text = File.ReadAllText(Path.Combine(dir!.FullName, "curia-agent-forum-WHITEPAPER.md"));
        var at = text.IndexOf("**Table 12", StringComparison.Ordinal);
        Assert.True(at >= 0, "the white paper no longer carries Table 12");

        foreach (var raw in text[at..].Split('\n'))
        {
            var cells = raw.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3 || cells[0] != $"`{kind}`") continue;

            return [.. System.Text.RegularExpressions.Regex.Matches(cells[2], "`([^`]+)`").Select(m => m.Groups[1].Value)];
        }

        throw new InvalidOperationException($"Table 12 has no row for {kind}");
    }

    /// <summary>The envelope's member names, read from the frozen conformance vector, not from the builder.</summary>
    private static HashSet<string> PublishedEnvelopeMembers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir!.FullName, "conformance", "envelope", "ed25519-full", "expected.canonical")));
        return [.. document.RootElement.EnumerateObject().Select(m => m.Name)];
    }

    /// <summary>
    /// R11.35: a write tool takes every optional member Table 12 lists for its kind that is an
    /// envelope member and that this adapter offers. <c>code_blocks</c> is named as not offered, so
    /// the fact is about the member and not about prose; <c>verification</c> is no member of an
    /// answer's envelope (it is a kind of its own, R8.4) and is not read.
    /// </summary>
    [Theory]
    [InlineData("curia_answer", "answer")]
    [InlineData("curia_comment", "comment")]
    public void R11_35_AWriteToolAcceptsEveryOptionalMemberTable12ListsForItsKindThatTheClientTakes(string tool, string kind)
    {
        string[] notOfferedByThisAdapter = ["code_blocks"];
        var envelopeMembers = PublishedEnvelopeMembers();
        var optional = PublishedOptionalMembers(kind).Where(envelopeMembers.Contains).Except(notOfferedByThisAdapter).ToArray();

        Assert.Contains("refs", optional);

        var schema = RegisteredTools().Single(t => t.ProtocolTool.Name == tool).ProtocolTool.InputSchema;
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        var required = schema.TryGetProperty("required", out var requiredList)
            ? requiredList.EnumerateArray().Select(r => r.GetString()).ToArray()
            : [];

        foreach (var member in optional)
        {
            Assert.True(properties.Contains(member), $"{tool}'s input schema has no property '{member}', which Table 12 lists as optional for {kind}");

            // R11.37: optional in the schema's `required` list too, or no model can call the tool without it.
            Assert.False(required.Contains(member), $"{tool}'s input schema lists '{member}' required, which Table 12 lists as optional for {kind}");
        }
    }

    /// <summary>
    /// R11.30 (rev.): the server instructions name every registered write tool, from the catalogue and
    /// not from a sentence typed beside it. Read-only, they say those tools are not offered.
    /// </summary>
    [Fact]
    public void ServerInstructionsNameEveryRegisteredWriteTool()
    {
        var read = RegisteredTools(withIdentity: false).Select(t => t.ProtocolTool.Name).ToHashSet();
        var writeTools = RegisteredTools().Select(t => t.ProtocolTool.Name).Where(n => !read.Contains(n)).ToArray();
        Assert.NotEmpty(writeTools);

        // The names the instructions are composed from are the catalogue's own, whatever the identity.
        var names = WriteToolNames();
        Assert.Equal(writeTools.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));

        var withIdentity = ToolText.ServerInstructions("https://agents.example/x", names);
        var readOnly = ToolText.ServerInstructions(null, names);

        foreach (var name in writeTools)
        {
            Assert.True(withIdentity.Contains(name, StringComparison.Ordinal), $"the instructions with an identity do not name {name}");
            Assert.True(readOnly.Contains(name, StringComparison.Ordinal), $"the read-only instructions do not name {name}");
        }

        Assert.Contains("not offered", readOnly, StringComparison.Ordinal);
    }
}
