using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R14.11 (errata G27): every registered tool called through <c>tools/call</c> over the real protocol, on a server
/// built by <see cref="ServerComposition.Options"/>, with the arguments the tool needs and then one part varied at
/// a time. Not one fact in this assembly sent a <c>tools/call</c> before this class: they called the method a tool
/// is bound to, which is in front of nothing the SDK does.
///
/// <para><b>What a tool needs is not read from its schema.</b> <see cref="Table"/> is written here with the
/// published sentence beside each row (ruling 8a); the schema is what is checked against it.</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ToolProtocolTests : IDisposable
{
    /// <summary>The fixed phrase a contract refusal begins with (ruling 3), so a tool's own refusal is not mistaken for one.</summary>
    private const string ContractRefusal = "NOT SENT: this call does not match";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-protocol-").FullName;

    /// <summary>
    /// What each tool needs, and what it may omit, with the sentence that says so.
    /// curia_read, curia_resolve: Table 10's pairs take the id and nothing else.
    /// curia_verify: R11.29, "the subject is a post a read has served", so the id suffices and the digest only pins.
    /// curia_search: R9.25, "A request naming no criterion SHALL return everything the corpus matches, paged."
    /// curia_ask: R8.20, the override is sent only when the caller gave a reason; Table 12's question row lists tags
    /// required and the Forum admits a question without (R8.62, register D53), so tags is optional here.
    /// curia_answer, curia_comment: Table 12, Required `body`, `parent`; Optional `refs` (parsed in a fact below).
    /// curia_flag: R10.35, a type and the rationale the description says is required.
    /// </summary>
    private static readonly Dictionary<string, (string[] Needs, string[] Optional)> Table = new(StringComparer.Ordinal)
    {
        ["curia_read"] = (["postId"], []),
        ["curia_verify"] = (["postId"], ["expectedDigest"]),
        ["curia_search"] = ([], ["criteria"]),
        ["curia_ask"] = (["board", "title", "body"], ["tags", "notDuplicateRationale"]),
        ["curia_answer"] = (["parentPostId", "body"], ["refs"]),
        ["curia_comment"] = (["parentPostId", "body"], ["refs"]),
        ["curia_resolve"] = (["answerPostId"], []),
        ["curia_flag"] = (["postId", "kind", "rationale"], []),
    };

    /// <summary>A string no refusal may echo (R9.25), and never a numeric string.</summary>
    private const string Sentinel = "zz-sentinel-value-7731";

    private static readonly string[] ReadOnly = ["curia_read", "curia_verify", "curia_search"];

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> Tools()
    {
        var data = new TheoryData<string>();
        foreach (var name in Table.Keys) data.Add(name);
        return data;
    }

    [Fact]
    public async Task R14_11_EveryRegisteredToolHasARowInTheNeedsTableAndEveryRowATool()
    {
        await using var withIdentity = await Session.StartAsync(_log, _home, identity: true);
        var served = (await withIdentity.ListAsync()).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(served);
        Assert.Equal(Table.Keys.Order(StringComparer.Ordinal), served);

        await using var readOnly = await Session.StartAsync(_log, _home, identity: false);
        Assert.Equal(ReadOnly.Order(StringComparer.Ordinal), (await readOnly.ListAsync()).Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task R11_37_EveryToolsRequiredListIsExactlyWhatItNeeds()
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var tools = await session.ListAsync();
        Assert.NotEmpty(tools);

        var wrong = new List<string>();
        foreach (var tool in tools)
        {
            var (needs, optional) = Table[tool.Name];
            var required = Required(tool.InputSchema);
            var properties = Properties(tool.InputSchema);
            if (!required.SequenceEqual(needs.Order(StringComparer.Ordinal)))
                wrong.Add($"{tool.Name}'s required list is [{string.Join(",", required)}]; it needs [{string.Join(",", needs)}]");
            if (!properties.SequenceEqual(needs.Concat(optional).Order(StringComparer.Ordinal)))
                wrong.Add($"{tool.Name}'s properties are [{string.Join(",", properties)}]; the table has [{string.Join(",", needs.Concat(optional))}]");
        }

        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    /// <summary>Table 12's Optional cells for an answer and a comment, parsed from the white paper, are not required.</summary>
    [Theory]
    [InlineData("curia_answer", "answer")]
    [InlineData("curia_comment", "comment")]
    public async Task R11_37_RefsIsNotRequiredWhereTable12CallsItOptional(string tool, string kind)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var schema = (await session.ListAsync()).Single(t => t.Name == tool).InputSchema;
        var optional = PublishedOptionalMembers(kind);
        Assert.Contains("refs", optional);

        var required = Required(schema);
        var properties = Properties(schema);
        foreach (var member in optional.Where(properties.Contains))
            Assert.False(required.Contains(member), $"{tool} lists '{member}' required, which Table 12 lists as optional for {kind}");
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_37_AToolAnswersACallWithOnlyWhatItNeedsWithoutAnUncausedError(string tool)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var result = await session.CallAsync(tool, Minimal(tool));

        Assert.False(Generic(tool, result), $"{tool} answered the bare invocation error to a call carrying only what it needs:\n{result.Text}");
        Assert.False(result.Text.StartsWith(ContractRefusal, StringComparison.Ordinal), $"{tool} refused a call carrying only what it needs:\n{result.Text}");
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_37_AnArgumentTheSchemaDoesNotDefineIsRefusedByNameAndNothingIsSent(string tool)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var control = await session.CallAsync(tool, Base(tool));
        Assert.False(Generic(tool, control) || control.Text.StartsWith(ContractRefusal, StringComparison.Ordinal), $"the control call (the needs and each optional as null) was refused:\n{control.Text}");

        _log.Requests.Clear();
        var arguments = Base(tool);
        arguments["frobnicate"] = true;
        var result = await session.CallAsync(tool, arguments);

        Assert.True(result.IsError && result.Text.StartsWith(ContractRefusal, StringComparison.Ordinal) && result.Text.Contains("frobnicate", StringComparison.Ordinal),
            $"{tool} accepted an argument its schema does not define:\n{result.Text}");
        Assert.Empty(_log.Requests);
    }

    /// <summary>G26 decision 5: the reply tools take no board; a model that gives one is told, not obeyed.</summary>
    [Theory]
    [InlineData("curia_comment")]
    [InlineData("curia_answer")]
    public async Task R11_37_ABoardIsNotAnArgumentOfAReply(string tool)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var control = await session.CallAsync(tool, Base(tool));
        Assert.False(Generic(tool, control) || control.Text.StartsWith(ContractRefusal, StringComparison.Ordinal), $"the control call (no board) was refused:\n{control.Text}");

        _log.Requests.Clear();
        var arguments = Base(tool);
        arguments["board"] = "elsewhere";
        var result = await session.CallAsync(tool, arguments);

        Assert.True(
            result.IsError && result.Text.StartsWith(ContractRefusal, StringComparison.Ordinal) && result.Text.Contains("board", StringComparison.Ordinal),
            $"{tool} accepted a 'board', which its schema does not define (G26 decision 5):\n{result.Text}");
        Assert.Empty(_log.Requests);
    }

    /// <summary>
    /// R9.25 and R9.26 through the protocol. The three names the specification lists as refusable carry a reason, shown
    /// as the difference from the refusal for a name nobody knows once the name is substituted.
    /// </summary>
    [Theory]
    [InlineData("minVerification")]
    [InlineData("unresolved_only")]
    [InlineData("environment_version")]
    [InlineData("verification")]
    [InlineData("kind")]
    [InlineData("frobnicate")]
    public async Task R11_37_AMemberTheCriteriaRecordDoesNotDefineIsRefusedByName(string member)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        _log.Requests.Clear();
        var result = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { [member] = "V1" } });

        Assert.True(result.IsError && result.Text.StartsWith(ContractRefusal, StringComparison.Ordinal) && result.Text.Contains(member, StringComparison.Ordinal),
            $"curia_search did not refuse '{member}' by name:\n{result.Text}");
        Assert.Contains("tags", result.Text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);

        if (member is "unresolved_only" or "environment_version" or "verification")
        {
            // The refusal for a name nobody knows, with that name replaced by this one: what the refusal would say were the
            // specification silent on it. Substituted in this direction because a member's name can sit inside another
            // token ('verification' inside 'min_verification', in the members list), and replacing the member would
            // alter both texts alike and compare them equal for the wrong reason (FT12 found the other direction vacuous).
            var nobody = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["frobnicate"] = "V1" } });
            var silent = nobody.Text.Replace("frobnicate", member, StringComparison.Ordinal);
            Assert.NotEqual(silent, result.Text);
            if (member != "verification") Assert.Matches(@"R\d+\.\d+", result.Text);

            // R9.26: the third is refused for the spelling, so the refusal points at the member that is honoured. The members
            // list may already hold it, so the reason is the difference in how often the text names it.
            if (member == "verification")
                Assert.True(
                    Regex.Count(result.Text, "min_verification") > Regex.Count(silent, "min_verification"),
                    $"the refusal of 'verification' does not point at min_verification beyond the members list:\n{result.Text}");
        }
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_37_AValueOfATypeTheSchemaDoesNotAdmitIsRefusedByName(string tool)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var schema = (await session.ListAsync()).Single(t => t.Name == tool).InputSchema;
        var tried = 0;
        _log.Requests.Clear();

        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            foreach (var (path, wrong, arguments) in Mismatches(tool, property.Name, property.Value))
            {
                tried++;
                var result = await session.CallAsync(tool, arguments);
                Assert.True(
                    result.IsError && result.Text.StartsWith(ContractRefusal, StringComparison.Ordinal) && result.Text.Contains(path, StringComparison.Ordinal),
                    $"{tool} was given {wrong} at '{path}':\n{result.Text}");
                Assert.DoesNotContain(Sentinel, result.Text, StringComparison.Ordinal);
                Assert.Empty(_log.Requests);
            }
        }

        Assert.True(tried > 0, $"{tool} has no member this fact could mistype; a defect in the fact");
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_37_AnAbsentRequiredMemberIsRefusedByName(string tool)
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var needs = Table[tool].Needs;
        Assert.SkipWhen(needs.Length == 0, $"{tool} needs nothing, so there is nothing to leave out");

        foreach (var left in needs)
        {
            var arguments = Base(tool);
            arguments.Remove(left);
            var result = await session.CallAsync(tool, arguments);
            Assert.True(
                result.IsError && result.Text.StartsWith(ContractRefusal, StringComparison.Ordinal) && result.Text.Contains(left, StringComparison.Ordinal),
                $"{tool} without '{left}':\n{result.Text}");
        }
    }

    /// <summary>The binder reads a numeric string as a number, so the contract is not stricter than the transport.</summary>
    [Fact]
    public async Task R11_37_ANumericStringIsStillAdmittedForAnInteger()
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var result = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["limit"] = "3" } });

        Assert.False(result.IsError, result.Text);
        Assert.False(Generic("curia_search", result), result.Text);
    }

    /// <summary>
    /// R11.39: the record's shared members are spelled as the specification spells them. Appendix E's
    /// <c>GET /v1/search</c> row and G12's note about R9.6 are parsed from the documents; the record's schema is checked against them.
    /// </summary>
    [Fact]
    public async Task R11_39_TheCriteriaRecordIsSpelledAsTheSpecificationSpellsIt()
    {
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var criteria = (await session.ListAsync()).Single(t => t.Name == "curia_search").InputSchema
            .GetProperty("properties").GetProperty("criteria");
        var members = criteria.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        var route = PublishedSearchParameters();
        Assert.Contains("min_verification", route);
        var shared = route.Intersect(["board", "tags", "min_verification", "cursor"]).ToArray();
        Assert.NotEmpty(shared);

        foreach (var name in shared)
            Assert.True(members.Contains(name), $"criteria has no member '{name}', which Appendix E publishes for GET /v1/search; its members are [{string.Join(",", members)}]");

        Assert.DoesNotContain("minVerification", members);
        Assert.DoesNotContain("whyRanked", members);
        Assert.Equal("min_verification", PublishedErrataSpelling());
    }

    /// <summary>
    /// R11.39: a result states the cursor the next page is requested with, in the adapter's block and quoted, or says
    /// there is none. The stub serves a cursor (and, for the second row, none); the Forum's own paging is P19's.
    /// </summary>
    [Theory]
    [InlineData("YzEwOjE=")]
    [InlineData(null)]
    public async Task R11_39_AResultStatesTheNextCursorOrThatThereIsNone(string? cursor)
    {
        _log.SearchNextCursor = cursor;
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var result = await session.CallRawAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?>() });

        var adapters = result.Content.OfType<TextContentBlock>().Select(t => t.Text).ToArray();
        Assert.NotEmpty(adapters);
        var resources = result.Content.OfType<EmbeddedResourceBlock>().Select(r => ((TextResourceContents)r.Resource).Text).ToArray();
        Assert.NotEmpty(resources);

        if (cursor is not null)
        {
            Assert.True(adapters.Any(a => a.Contains("next_cursor=\"" + cursor + "\"", StringComparison.Ordinal)), $"no cursor was stated to the model, though the Forum served one:\n{string.Join("\n", adapters)}");
            Assert.DoesNotContain(resources, r => r.Contains(cursor, StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(adapters, a => a.Contains("next_cursor", StringComparison.Ordinal));
            Assert.True(adapters.Any(a => a.Contains("no_further_pages", StringComparison.Ordinal)), $"the model was not told this is the last page:\n{string.Join("\n", adapters)}");
        }
    }

    /// <summary>
    /// R11.38: the stub serves no thread route, so the read cannot fetch one. The post is still served, and the first
    /// block says the thread was not read; today the result is the post alone, with no word.
    /// </summary>
    [Fact]
    public async Task R11_38_AReadWhoseThreadCannotBeFetchedSaysSo()
    {
        await using var session = await Session.StartAsync(_log, _home, identity: false);
        var result = await session.CallRawAsync("curia_read", new() { ["postId"] = StubLog.PostId });

        var first = result.Content[0] is TextContentBlock text ? text.Text : "(the first block is the post, not a word from the adapter)";
        Assert.True(first.Contains("NOT READ", StringComparison.Ordinal), $"the model was not told the thread could not be read:\n{first}");
        Assert.Contains("The stub serves no", first, StringComparison.Ordinal);
        Assert.Contains("threads", first, StringComparison.Ordinal);

        var served = result.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text).ToArray();
        Assert.Contains(served, text => text.Contains(StubLog.PostId, StringComparison.Ordinal));
    }

    /// <summary>
    /// R11.26: every identifier a description writes in backticks as an argument is one its schema defines (at the top
    /// level or in <c>criteria</c>), so the description and the schema, two artifacts written apart, cannot drift. Result
    /// terms a description may name are a named set, asserted to be exactly that so an exception is not added quietly.
    /// </summary>
    [Fact]
    public async Task R11_26_EveryParameterADescriptionNamesIsOneItsSchemaDefines()
    {
        string[] resultTerms = ["next_cursor"];
        await using var session = await Session.StartAsync(_log, _home, identity: true);
        var tools = await session.ListAsync();
        Assert.NotEmpty(tools);

        var wrong = new List<string>();
        var named = 0;
        foreach (var tool in tools)
        {
            var members = new HashSet<string>(Properties(tool.InputSchema), StringComparer.Ordinal);
            if (tool.InputSchema.GetProperty("properties").TryGetProperty("criteria", out var criteria))
                foreach (var member in criteria.GetProperty("properties").EnumerateObject()) members.Add(member.Name);

            foreach (Match match in Regex.Matches(tool.Description ?? string.Empty, "`([a-z][A-Za-z0-9_]*)`"))
            {
                named++;
                var token = match.Groups[1].Value;
                if (!members.Contains(token) && !resultTerms.Contains(token))
                    wrong.Add($"{tool.Name}'s description names `{token}`; its schema defines [{string.Join(",", members.Order(StringComparer.Ordinal))}]");
            }
        }

        Assert.True(named > 0, "no description names an argument in backticks, so nothing was compared; a defect in the fact");
        Assert.True(wrong.Count == 0, string.Join("; ", wrong.Distinct()));
        Assert.Equal(["next_cursor"], resultTerms);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static Dictionary<string, object?> Minimal(string tool) => tool switch
    {
        "curia_read" => new() { ["postId"] = StubLog.PostId },
        "curia_verify" => new() { ["postId"] = StubLog.PostId },
        "curia_search" => new(),
        "curia_ask" => new() { ["board"] = "b", ["title"] = "A title", ["body"] = "A body." },
        "curia_answer" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "An answer." },
        "curia_comment" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "A reply." },
        "curia_resolve" => new() { ["answerPostId"] = StubLog.AnswerPostId },
        "curia_flag" => new() { ["postId"] = StubLog.PostId, ["kind"] = "incorrect", ["rationale"] = "The premise is wrong." },
        _ => throw new InvalidOperationException($"{tool} has no minimal call: add a row to Table and to this switch"),
    };

    /// <summary>
    /// What a tool needs, plus each optional member sent with a valid non-null value: a valid call on a tree whose schema
    /// lists the optionals required and on one whose schema does not, whatever the schema says about null, so a variation
    /// of it is the only fault in the call. (Omitting them would make the control a defect of R11.37 on today's tree.)
    /// </summary>
    private static Dictionary<string, object?> Base(string tool)
    {
        var arguments = Minimal(tool);
        foreach (var optional in Table[tool].Optional)
            arguments[optional] = optional switch
            {
                "criteria" => new Dictionary<string, object?>(),
                "refs" => Array.Empty<string>(),
                "tags" => new[] { "t" },
                "notDuplicateRationale" => "Not a duplicate: a different question.",
                "expectedDigest" => "sha256:" + new string('0', 64),
                _ => throw new InvalidOperationException($"{optional} has no valid value: add one to Base"),
            };

        return arguments;
    }

    private static bool Generic(string tool, Answer result) =>
        result.IsError && result.Text.Trim() == $"An error occurred invoking '{tool}'.";

    private static string[] Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? [.. required.EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal)]
            : [];

    private static string[] Properties(JsonElement schema) =>
        [.. schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every kind of value the schema's type does not admit (null among them, for a member that is not nullable), for each
    /// top-level member and, in <c>criteria</c>, each of its members; and, for an array member, each kind its items do not admit.
    /// </summary>
    private static IEnumerable<(string Path, string Wrong, Dictionary<string, object?> Arguments)> Mismatches(string tool, string member, JsonElement schema)
    {
        foreach (var wrong in Wrong(schema))
        {
            var arguments = Base(tool);
            arguments[member] = wrong.Value;
            yield return (member, wrong.Name, arguments);
        }

        if (ItemsOf(schema) is { } items)
        {
            foreach (var wrong in Wrong(items))
            {
                var arguments = Base(tool);
                arguments[member] = new object?[] { wrong.Value };
                yield return ($"{member}[0]", $"{wrong.Name} as an element", arguments);
            }
        }

        if (member == "criteria" && schema.TryGetProperty("properties", out var inner))
        {
            foreach (var nested in inner.EnumerateObject())
            {
                foreach (var wrong in Wrong(nested.Value))
                {
                    var arguments = Base(tool);
                    arguments["criteria"] = new Dictionary<string, object?> { [nested.Name] = wrong.Value };
                    yield return ($"criteria.{nested.Name}", wrong.Name, arguments);
                }

                if (ItemsOf(nested.Value) is not { } nestedItems) continue;
                foreach (var wrong in Wrong(nestedItems))
                {
                    var arguments = Base(tool);
                    arguments["criteria"] = new Dictionary<string, object?> { [nested.Name] = new object?[] { wrong.Value } };
                    yield return ($"criteria.{nested.Name}[0]", $"{wrong.Name} as an element", arguments);
                }
            }
        }
    }

    private static JsonElement? ItemsOf(JsonElement schema) =>
        schema.TryGetProperty("items", out var items) && items.ValueKind is JsonValueKind.Object ? items : null;

    private static IEnumerable<(string Name, object? Value)> Wrong(JsonElement schema)
    {
        var admitted = schema.TryGetProperty("type", out var type)
            ? type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToArray() : [type.GetString()!]
            : [];
        if (admitted.Length == 0) yield break;

        // The sentinel is never a numeric string, so it is a mistake for an integer as well.
        (string Kind, string Name, object? Value)[] candidates =
        [
            ("string", "a string", Sentinel), ("number", "a number", 42), ("boolean", "a boolean", true),
            ("array", "an array", new[] { 1 }), ("object", "an object", new Dictionary<string, object?> { ["a"] = 1 }), ("null", "null", null),
        ];
        foreach (var (kind, name, value) in candidates)
        {
            var admits = admitted.Contains(kind) || (kind == "number" && admitted.Contains("integer"));
            if (!admits) yield return (name, value);
        }
    }

    private static string[] PublishedOptionalMembers(string kind)
    {
        var text = File.ReadAllText(Path.Combine(Root("curia-agent-forum-WHITEPAPER.md"), "curia-agent-forum-WHITEPAPER.md"));
        var at = text.IndexOf("**Table 12", StringComparison.Ordinal);
        Assert.True(at >= 0, "the white paper no longer carries Table 12");

        foreach (var raw in text[at..].Split('\n'))
        {
            var cells = raw.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3 || cells[0] != $"`{kind}`") continue;
            return [.. Regex.Matches(cells[2], "`([^`]+)`").Select(m => m.Groups[1].Value)];
        }

        throw new InvalidOperationException($"Table 12 has no row for {kind}");
    }

    /// <summary>Appendix E's <c>GET /v1/search</c> row: `q`, `board`, `tags`, `min_verification`, `format`, cursor.</summary>
    private static string[] PublishedSearchParameters()
    {
        var text = File.ReadAllText(Path.Combine(Root("curia-agent-forum-WHITEPAPER.md"), "curia-agent-forum-WHITEPAPER.md"));
        var row = text.Split('\n').Single(l => l.StartsWith("| GET | `/v1/search` |", StringComparison.Ordinal));
        var cells = row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        return [.. cells[^1].Split(',').Select(p => p.Trim().Trim('`'))];
    }

    /// <summary>G12's table of editorial amendments: "the wire spelling is `min_verification` and R9.6's `verification` is the refused spelling".</summary>
    private static string PublishedErrataSpelling()
    {
        var text = File.ReadAllText(Path.Combine(Root("curia-whitepaper-ERRATA-AND-ADDENDUM.md"), "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var match = Regex.Match(text, "the wire spelling is `([a-z_]+)` and R9\\.6's `verification` is the refused spelling");
        Assert.True(match.Success, "the errata no longer states R9.6's wire spelling");
        return match.Groups[1].Value;
    }

    private static string Root(string marker)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, marker))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>What a tool answered: the text a model reads, and whether the protocol marked it an error.</summary>
    private readonly record struct Answer(bool IsError, string Text);

    /// <summary>A real client and a real server over two pipes, the server built by <see cref="ServerComposition.Options"/>.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly McpClient _client;
        private readonly McpServer _server;
        private readonly StreamServerTransport _transport;
        private readonly EnrolledAgent? _agent;
        private readonly Task _running;

        private Session(McpClient client, McpServer server, StreamServerTransport transport, EnrolledAgent? agent, Task running)
        {
            _client = client;
            _server = server;
            _transport = transport;
            _agent = agent;
            _running = running;
        }

        internal static async Task<Session> StartAsync(StubLog log, string home, bool identity)
        {
            var ct = TestContext.Current.CancellationToken;
            EnrolledAgent? agent = null;
            ForumWriter? writer = null;
            if (identity)
            {
                Assert.True(log.Store.Load("alice").TryGetValue(out agent, out var error), error?.Detail);
                writer = new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System);
            }

            var tools = new ForumTools(log.Client(), MarkingMode.None, new HeadStore(home), writer);
            var options = ServerComposition.Options(tools, writer?.Agent.Profile.AgentId);

            var toServer = new Pipe();
            var toClient = new Pipe();
            var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "curia-mcp-protocol-test", NullLoggerFactory.Instance);
            var server = McpServer.Create(transport, options, NullLoggerFactory.Instance);
            var running = server.RunAsync(ct);

            var client = await McpClient.CreateAsync(
                new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
                new McpClientOptions { ClientInfo = new Implementation { Name = "curia-protocol-gate", Version = "0" } },
                NullLoggerFactory.Instance,
                ct);

            return new Session(client, server, transport, agent, running);
        }

        internal async Task<IReadOnlyList<Tool>> ListAsync() =>
            [.. (await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.ProtocolTool)];

        internal async Task<CallToolResult> CallRawAsync(string tool, Dictionary<string, object?> arguments) =>
            await _client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

        internal async Task<Answer> CallAsync(string tool, Dictionary<string, object?> arguments)
        {
            var result = await _client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
            var text = new StringBuilder();
            foreach (var block in result.Content)
            {
                switch (block)
                {
                    case TextContentBlock t:
                        text.AppendLine(t.Text);
                        break;
                    case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                        text.AppendLine(resource.Text);
                        break;
                    default:
                        break;
                }
            }

            return new Answer(result.IsError == true, text.ToString().TrimEnd());
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DisposeAsync();
            await _server.DisposeAsync();
            await _transport.DisposeAsync();
            try
            {
                await _running;
            }
            catch (OperationCanceledException)
            {
                // the session ended with its client
            }

            _agent?.Dispose();
        }
    }
}
