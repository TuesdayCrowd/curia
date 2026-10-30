using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Domain.Retrieval;
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

        // R11.49: the batch route is anonymous and a read; the pair curia_read exercises (R11.30 (rev.), annotated by G35).
        "curia_recheck" => (ResourceKind.Thread, ActionKind.Read),

        // R11.26: the Scope column's write values are Table 10 rows written with a colon.
        "curia_ask" => (ResourceKind.Question, ActionKind.Create),
        "curia_answer" => (ResourceKind.Answer, ActionKind.Create),
        "curia_flag" => (ResourceKind.Flag, ActionKind.Raise),

        // Errata G49: the readout of the agent's own budgets, Table 10's own `budget`/`read` (own) row.
        "curia_budget" => (ResourceKind.Budget, ActionKind.Read),
        "curia_comment" => (ResourceKind.Comment, ActionKind.Create),
        "curia_resolve" => (ResourceKind.Answer, ActionKind.Accept),
        "curia_revise" => (ResourceKind.Revision, ActionKind.Create),
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
            ["curia_answer", "curia_ask", "curia_budget", "curia_comment", "curia_flag", "curia_read", "curia_recheck", "curia_resolve", "curia_revise", "curia_search", "curia_verify"],
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
            ["curia_read", "curia_recheck", "curia_search", "curia_verify"],
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
        Assert.Equal(2, NotExercisedByDecision.Length);
        Assert.Equal(
            new HashSet<(ResourceKind, ActionKind)>
            {
                (ResourceKind.Key, ActionKind.Retire), (ResourceKind.Key, ActionKind.Rotate),
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
        // curia_budget needs the session's token and is registered with the write tools under the one gate, but it writes nothing and is not named as a tool that writes (errata G49).
        var writeTools = RegisteredTools().Select(t => t.ProtocolTool.Name).Where(n => !read.Contains(n) && n != "curia_budget").ToArray();
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

    /// <summary>
    /// R10.45, R9.24 (rev.), errata G29 ruling 5: the sentence about <c>min_verification</c> says what the floor applies to.
    /// The kinds are the Forum's own (<c>RetrievalFloorPolicy</c>), not typed here; the words "unendorsed results" claim a
    /// removal of results the floor does not touch.
    /// </summary>
    [Fact]
    public void R10_45_TheFloorSentenceNamesTheKindsItAppliesTo()
    {
        var search = Registered().Single(t => t.Name == "curia_search").Description;
        var paragraph = search.Split("\n\n").Single(p => p.StartsWith("Results are ordered", StringComparison.Ordinal));
        var sentence = paragraph[(paragraph.LastIndexOf(". ", paragraph.Length - 2, StringComparison.Ordinal) + 2)..];
        Assert.Contains("`min_verification`", sentence, StringComparison.Ordinal);

        var gradable = RetrievalFloorPolicy.GradableKinds.Select(PostKinds.Wire).ToArray();
        Assert.NotEmpty(gradable);
        Assert.DoesNotContain("unendorsed results", sentence, StringComparison.OrdinalIgnoreCase);
        foreach (var kind in gradable)
            Assert.True(sentence.Contains(kind, StringComparison.Ordinal), $"the floor sentence does not name the kind '{kind}', which the Forum's floor applies to; the description says: {sentence}");
    }

    /// <summary>
    /// R11.26 (errata G28): every tool name a description or the server instructions mention is a registered tool, so two separately
    /// authored artifacts (the descriptions and the catalogue) cannot drift: a description that sends a model to a tool that is not
    /// there is the defect, and "only superseded by a revision (see curia_revise)" is the sentence that would. Green first (held by FX21).
    /// </summary>
    [Fact]
    public void R11_26_EveryToolNameADescriptionMentionsIsARegisteredTool()
    {
        var registered = Registered().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(registered);

        var texts = Registered().Select(t => (Where: t.Name + "'s description", Text: t.Description))
            .Append(("the server instructions", ToolText.ServerInstructions("https://agents.example/x", WriteToolNames())))
            .Append(("the read-only server instructions", ToolText.ServerInstructions(null, WriteToolNames())))
            .ToList();

        var mentioned = 0;
        var unregistered = new List<string>();
        foreach (var (where, text) in texts)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\bcuria_[a-z]+(?:_[a-z]+)*"))
            {
                mentioned++;
                if (!registered.Contains(match.Value)) unregistered.Add($"{where} names {match.Value}");
            }
        }

        Assert.True(mentioned > 0, "no description or instruction names a tool, so nothing was compared; a defect in this fact");
        Assert.True(unregistered.Count == 0, string.Join("; ", unregistered.Distinct()) + $" ({string.Join(",", registered.Order(StringComparer.Ordinal))} are registered)");
    }

    /// <summary>
    /// R11.46 (errata G33): a refusal of a `kinds` value lists Table 9's kinds, and the list is the type's own: every spelling
    /// <see cref="PostKinds.Wire"/> gives a <see cref="PostKind"/>, read from the enum and not from a copy.
    /// </summary>
    [Fact]
    public void R11_46_TheKindsRefusalListsTable9FromPostKinds()
    {
        var refused = new SearchCriteria { Kinds = ["not-a-kind"] }.ToRequest();

        Assert.False(refused.TryGetValue(out _, out var error), "an unknown kind was accepted");
        var kinds = Enum.GetValues<PostKind>().Select(PostKinds.Wire).ToArray();
        Assert.NotEmpty(kinds);
        var missing = kinds.Where(kind => !(error!.Detail ?? string.Empty).Contains(kind, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"the refusal does not list [{string.Join(", ", missing)}]: {error!.Detail}");
    }

    /// <summary>
    /// R11.46: `curia_search`'s description says what a board no post names answers, and explains every member of
    /// <c>why_ranked</c> that the result prints. The printed names are derived from the renderer: the words
    /// <see cref="SearchScore.WriteWhy"/> writes of its own (a served value is a quoted literal and a number is a number), not
    /// from the description under test.
    /// </summary>
    [Fact]
    public void R11_46_SearchDescriptionStatesAnUnnamedBoardAndEveryWhyRankedMember()
    {
        var why = new WhyRanked(
            new LexicalWhy(1, 2, 3, 4, 5),
            new VectorWhy(1, 5401, "hashed-ngram@1"),
            60, 1, 1, 32787, "V0", 10000, 32787, DeferredByDiversification: true,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("staleness_penalty", "r"));
        var frame = new FrameBuilder();
        SearchScore.WriteWhy(frame, why);
        var rendered = frame.ToString();
        Assert.Contains("why_ranked", rendered, StringComparison.Ordinal);

        var ownWords = System.Text.RegularExpressions.Regex.Replace(rendered, "\"[^\"]*\"", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));
        var names = System.Text.RegularExpressions.Regex.Matches(ownWords, "[A-Za-z_]{3,}", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(names.Length >= 10, $"the renderer's own words were not found: [{string.Join(", ", names)}]");

        var description = Registered().Single(t => t.Name == "curia_search").Description;
        var unexplained = names.Where(n => !description.Contains(n, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(unexplained.Length == 0, $"curia_search's description does not explain [{string.Join(", ", unexplained)}]");

        Assert.Contains("answers like an empty one", description, StringComparison.Ordinal);
        Assert.Contains("case-sensitive", description, StringComparison.Ordinal);
        Assert.Contains("clamped", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// P14 over R11.46's new paragraphs (green first): every identifier a description writes in backticks as an argument is one its
    /// schema defines, at the top level or in <c>criteria</c>, or a result term named here and asserted to be exactly that set.
    /// </summary>
    [Fact]
    public void R11_46_NoDescriptionNamesAnArgumentItsSchemaLacks()
    {
        string[] resultTerms = ["next_cursor", "revision"];
        var wrong = new List<string>();
        var named = 0;
        foreach (var tool in RegisteredTools())
        {
            var schema = tool.ProtocolTool.InputSchema;
            var members = new HashSet<string>(schema.GetProperty("properties").EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            if (schema.GetProperty("properties").TryGetProperty("criteria", out var criteria))
                foreach (var member in criteria.GetProperty("properties").EnumerateObject()) members.Add(member.Name);

            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                tool.ProtocolTool.Description ?? string.Empty, "`([a-z][A-Za-z0-9_]*)`", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                named++;
                var token = match.Groups[1].Value;
                if (!members.Contains(token) && !resultTerms.Contains(token))
                    wrong.Add($"{tool.ProtocolTool.Name} names `{token}`");
            }
        }

        Assert.True(named > 0, "no description names an argument in backticks, so nothing was compared");
        Assert.True(wrong.Count == 0, string.Join("; ", wrong.Distinct()));
        Assert.Equal(["next_cursor", "revision"], resultTerms);
    }

    /// <summary>
    /// R11.47 (errata G34): a description that does not name the refusals teaches them only by being tripped. The words are the ones
    /// the refusals print (the author refusal's <c>was written by</c>, the fork's <c>FORKED</c>, the receipt's <c>NOT CHECKED</c>), and
    /// "reported superseded" stays (R11.44's fact reads it). Weak by construction: it restates the tool's own wording, and
    /// <c>ReviseGuardTests</c>'s errata-derived fact is the one that carries information.
    /// </summary>
    [Fact]
    public void R11_47_TheReviseDescriptionStatesBothRefusalsAndTheReceiptLine()
    {
        var description = Registered().Single(t => t.Name == "curia_revise").Description;
        Assert.NotEmpty(description);

        Assert.Contains("reported superseded", description, StringComparison.Ordinal);
        Assert.Contains("FORKED", description, StringComparison.Ordinal);
        Assert.Contains("was written by", description, StringComparison.Ordinal);
        Assert.Contains("NOT CHECKED", description, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.49: the description says what the tool returns and what it does not check. <b>Weak by construction:</b> it restates the tool's wording
    /// (a description cannot be held against a source other than itself); the closing line's phrases, which the errata names, are held in
    /// <c>RecheckToolTests.R11_49_TheClosingLineSaysNoSignatureAndNoInclusionWasChecked</c>.
    /// </summary>
    [Fact]
    public void R11_49_TheRecheckDescriptionStatesWhatItReturnsAndWhatItDoesNotCheck()
    {
        Assert.NotEmpty(Registered());
        var recheck = Assert.Single(Registered(), t => t.Name == "curia_recheck").Description;

        Assert.Contains(ToolText.UntrustedDataNotice, recheck, StringComparison.Ordinal);
        Assert.Contains("Requires no credential: this Forum serves reads to anonymous callers.", recheck, StringComparison.Ordinal);
        Assert.Contains("ids", recheck, StringComparison.Ordinal);
        Assert.Contains("standings", recheck, StringComparison.Ordinal);
        Assert.Contains("no signature", recheck, StringComparison.Ordinal);
        Assert.Contains("curia_verify", recheck, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fifth exercise's searcher (F6), reproduced live: <c>criteria.author</c> given an agent's short name answers an empty page with no
    /// word saying why. The description says the full identifier is what the author line prints.
    /// </summary>
    [Fact]
    public void R11_49_TheSearchDescriptionSaysAuthorWantsTheFullAgentIdentifier()
    {
        var search = Registered().Single(t => t.Name == "curia_search").Description;

        Assert.Contains("full agent identifier", search, StringComparison.Ordinal);
        Assert.Contains("urn:curia:agent:", search, StringComparison.Ordinal);
        Assert.Contains("not an error", search, StringComparison.Ordinal);
    }

    /// <summary>R11.49: the read-only instructions say the adapter re-checks citations by digest, beside reading and verifying.</summary>
    [Fact]
    public void R11_49_TheReadOnlyInstructionsSayTheAdapterRechecksCitations()
    {
        Assert.Contains("re-checks citations by digest", ToolText.ReadOnly(WriteToolNames()), StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.17, annotated by R8.70 (errata G38): <c>curia_answer</c>'s description says the parent is a question, that a comment's id is not one, and
    /// that the Forum refuses a parent that is not. It adds no backticked token beyond the arguments the description already names
    /// (<c>ToolDescriptionTests</c> and <c>ToolProtocolTests</c> pin the backticked result terms). <b>Weak by construction:</b> it restates the
    /// wording; <c>McpWriteEndToEndTests.R8_70_CuriaAnswerWithACommentAsParentIsRefusedByCause</c> is the behavioural check.
    /// </summary>
    [Fact]
    public void R11_17_CuriaAnswerSaysACommentIsNotAParent()
    {
        var description = Registered().Single(t => t.Name == "curia_answer").Description;
        var outsideBackticks = System.Text.RegularExpressions.Regex.Replace(description, "`[^`]+`", string.Empty);

        Assert.Contains(PublishedParentRule.AnswerParentKind(), outsideBackticks, StringComparison.Ordinal);
        Assert.Contains("comment", outsideBackticks, StringComparison.Ordinal);
        Assert.Contains("refused", outsideBackticks, StringComparison.Ordinal);

        string[] arguments = ["parentPostId", "body", "refs", "sha256:", "pkg:", "http://", "https://"];
        var tokens = System.Text.RegularExpressions.Regex.Matches(description, "`([^`]+)`").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);
        Assert.Empty(tokens.Except(arguments, StringComparer.Ordinal));
    }

    /// <summary>
    /// PJ32, R11.59 (errata G43): the descriptions of <c>curia_ask</c>, <c>curia_answer</c>, <c>curia_comment</c> and <c>curia_revise</c> state what a
    /// tag is: not empty, no comma, no edge white space, no U+0000, compared without regard to case and in NFC by search while storage does not fold
    /// case. The phrases are parsed from the errata at test time (<see cref="PublishedSmallWords.TagPhrases"/>), not written here.
    /// </summary>
    [Theory]
    [InlineData("curia_ask")]
    [InlineData("curia_answer")]
    [InlineData("curia_comment")]
    [InlineData("curia_revise")]
    public void R11_59_TheDescriptionOfAToolThatTakesTagsStatesWhatATagIs(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var description = Assert.Single(Registered(), t => t.Name == name).Description;
        Assert.NotEmpty(description);

        foreach (var phrase in PublishedSmallWords.TagPhrases())
            Assert.Contains(phrase, description, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.56 (errata G42): the description of <c>curia_recheck</c> states R10.83's meaning of <c>current</c>, states <c>fork</c> and that
    /// <c>superseded</c> names the direct revisions only, and does not say what the erratum forbids; the description of <c>curia_answer</c> states
    /// R11.55's pointer and receipt line. <b>Weak by construction</b>: it restates descriptions, so it reads words the errata names and nothing a
    /// call does. The information is carried by the facts that print the lines and call the tools (<c>RecheckStandingWordsTests</c>,
    /// <c>RevisedQuestionPointerTests</c>, <c>QuestionStandingReceiptTests</c> and the two end-to-end doors), and a description that satisfies this
    /// fact and a behaviour that does not is the failure those facts exist to catch. The member name and the forbidden words are parsed from the errata.
    /// </summary>
    [Fact]
    public void R11_56_TheDescriptionsStateWhatTheErratumRequires()
    {
        var registered = Registered();
        string Of(string tool) => registered.Single(t => t.Name == tool).Description;

        var recheck = Of("curia_recheck");
        Assert.NotEmpty(recheck);
        Assert.DoesNotContain(PublishedForkWords.ForbiddenWords(), recheck, StringComparison.Ordinal);
        Assert.Contains(PublishedForkWords.MemberName(), recheck, StringComparison.Ordinal);
        Assert.NotEmpty(Sentence(recheck, "current", "no later revision"));
        Assert.NotEmpty(Sentence(recheck, "superseded", "direct revisions only"));
        Assert.Contains("one call", recheck, StringComparison.OrdinalIgnoreCase);

        var answer = Of("curia_answer");
        Assert.NotEmpty(answer);
        Assert.NotEmpty(Sentence(answer, "revises", "superseded_by"));
        Assert.NotEmpty(Sentence(answer, "receipt", "question", "NOT STATED"));
    }

    /// <summary>
    /// R11.54 (errata G41): the descriptions of <c>curia_read</c>, <c>curia_answer</c> and <c>curia_comment</c> state what the erratum requires of the
    /// tool (R11.26). <b>Weak by construction</b>: it restates descriptions, so it reads words the errata names and nothing a call does. The
    /// information is carried by the facts that call the tools (<c>WithheldToolRefusalTests</c>, <c>WithheldCutLineTests</c> and the end-to-end doors), and a
    /// description that satisfies this fact and a behaviour that does not is the failure those facts exist to catch. The tools are parsed from the errata.
    /// </summary>
    [Fact]
    public void R11_54_ThreeDescriptionsStateWhatTheErratumRequires()
    {
        var named = PublishedWithheldWords.DescribedTools();
        Assert.Contains("curia_read", named);
        Assert.Contains("curia_answer", named);
        Assert.Contains("curia_comment", named);

        var registered = Registered();
        string Of(string tool) => registered.Single(t => t.Name == tool).Description;

        // curia_read: a post the Forum says is withheld is not a post that does not exist, and a cut thread says which.
        Assert.NotEmpty(Sentence(Of("curia_read"), "withheld", "not serving"));

        // curia_answer and curia_comment: a parent the Forum is not serving is NOT SENT, and nothing is signed.
        foreach (var tool in new[] { "curia_answer", "curia_comment" })
            Assert.NotEmpty(Sentence(Of(tool), "withheld", "NOT SENT"));
    }

    private static string Sentence(string description, params string[] words)
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(description, @"(?<=[.!?])\s+|\n+");
        return sentences.FirstOrDefault(s => words.All(w => s.Contains(w, StringComparison.OrdinalIgnoreCase))) ?? string.Empty;
    }

    /// <summary>
    /// R11.53 (errata G40): the five descriptions state what the erratum requires of each. <b>Weak by construction</b>: it restates descriptions, so it
    /// reads words the errata names and nothing a call does. The information is carried by the end-to-end facts (the cap by
    /// <c>RecheckCapEndToEndTests</c>, the pin, the cursor, the receipt and the missing post by their own), and a description that satisfies this
    /// fact and a behaviour that does not is the failure those facts exist to catch. The five tools are parsed from the errata.
    /// </summary>
    [Fact]
    public void R11_53_TheFiveDescriptionsStateWhatTheErratumRequires()
    {
        var obligations = PublishedReadersWords.R1153Obligations();
        Assert.Equal(
            ["curia_read", "curia_recheck", "curia_resolve", "curia_search", "curia_verify"],
            obligations.Keys.Order(StringComparer.Ordinal).ToArray());

        var registered = Registered();
        string Of(string tool) => registered.Single(t => t.Name == tool).Description;

        // curia_recheck: the cap, and that a Forum states the cap it enforces in its refusal.
        var recheck = Of("curia_recheck");
        Assert.Matches(@"\b\d+ digests per call\b", recheck);
        Assert.NotEmpty(Sentence(recheck, "cap", "refusal"));

        // curia_verify: the form of expectedDigest.
        Assert.NotEmpty(Sentence(Of("curia_verify"), "expectedDigest", "sha256:", "lowercase"));

        // curia_search: that an empty cursor is refused.
        Assert.NotEmpty(Sentence(Of("curia_search"), "empty", "cursor", "refused"));

        // curia_resolve: that the receipt names the answer a call replaced.
        Assert.NotEmpty(Sentence(Of("curia_resolve"), "receipt", "replaced"));

        // curia_read: that the accepted line says when the accepted answer has been superseded.
        Assert.NotEmpty(Sentence(Of("curia_read"), "accepted line", "superseded"));
    }

    /// <summary>
    /// R11.50: <c>curia_search</c>'s description says a cursor belongs to the query that produced it, names every member R9.35 binds (the set is parsed from
    /// the errata, never from this file or the description; <c>q</c> is the criterion <c>query</c>, spelled as the tool spells it) and says a mismatch is refused.
    /// </summary>
    [Fact]
    public void R11_50_TheSearchDescriptionNamesEveryMemberTheErratumBinds()
    {
        var description = Registered().Single(t => t.Name == "curia_search").Description;
        Assert.NotEmpty(description);

        var sentences = description.Replace("\n", " ", StringComparison.Ordinal).Split(". ");
        var cursor = sentences.Where(s => s.Contains("belongs to the query", StringComparison.Ordinal)).ToArray();
        Assert.True(cursor.Length > 0, $"curia_search's description has no sentence that says a cursor belongs to the query that produced it:\n{description}");

        var sentence = string.Join(". ", cursor);
        Assert.Contains("refused", sentence, StringComparison.Ordinal);
        foreach (var member in PublishedSearchPage.BoundMembers())
        {
            var spelled = member == "q" ? "query" : member;
            Assert.True(sentence.Contains(spelled, StringComparison.Ordinal), $"the cursor sentence does not name '{spelled}', which R9.35 binds: {sentence}");
        }
    }

    /// <summary>
    /// R11.50: both descriptions name the <c>answers</c> line and its states, in quotes as the existing ones quote <c>'revision NOT STATED'</c>, and add no
    /// backticked token. <b>Weak by construction:</b> it restates the wording; R10.81's tokens are held in <c>AnswerStandingLineTests</c> from the errata.
    /// </summary>
    [Theory]
    [InlineData("curia_search")]
    [InlineData("curia_read")]
    public void R11_50_TheSearchAndReadDescriptionsNameTheAnswersLine(string tool)
    {
        var description = Registered().Single(t => t.Name == tool).Description;
        Assert.NotEmpty(description);

        Assert.Contains("'answers none accepted'", description, StringComparison.Ordinal);
        Assert.Contains("'answers NOT STATED'", description, StringComparison.Ordinal);
        Assert.Contains("'answers accepted'", description, StringComparison.Ordinal);
        Assert.DoesNotContain("`answers", description, StringComparison.Ordinal);
    }
}
