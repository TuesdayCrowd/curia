using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// UDO9 (errata G75, R11.101, register item 323): <c>curia_keys</c> says what <c>curia keys</c> says when the key this host signs with is not one the Forum serves live, <c>curia-mcp</c> is the version R11.101's
/// paragraph names, and the tool's description carries the sentence the errata publishes. The profile is written by hand: it signs with a kid the stub's key set does not list, and keeps the stub's own key (the one
/// the key set serves <c>active</c>, with the same public half) as a held key. The text is compared with <see cref="KeysReport"/>'s, the one definition the CLI and the tool both call (as <c>KeysToolTests</c> does).
/// Every word is read from the errata at test time; the version is read by this file's own reader (<see cref="PublishedVersion"/>), not <c>PublishedReadersLastWords.Version()</c>, which reads R11.100's paragraph.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SigningKeyNotLiveToolTests : IDisposable
{
    private const string GoneKid = "alice-gone";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-udo9-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Hex(string kid) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kid)));

    private static string Span(string label)
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R11.101 words.**");
        var marker = $"*{label}:*";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || paragraph.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"the errata's R11.101 words do not hold {marker} exactly once");

        var start = at + marker.Length;
        var next = Regex.Match(paragraph[start..], @"\*[A-Z][^*`]*:\*", RegexOptions.CultureInvariant);
        var section = next.Success ? paragraph[start..(start + next.Index)] : paragraph[start..];
        var spans = Regex.Matches(section, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        return spans.Length == 1 ? spans[0] : throw new InvalidOperationException($"{marker}: {spans.Length} spans, expected 1");
    }

    private static string Once(string template, string placeholder, string value)
    {
        var at = template.IndexOf(placeholder, StringComparison.Ordinal);
        if (at < 0 || template.IndexOf(placeholder, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"the published words do not hold {placeholder} exactly once");
        return template.Replace(placeholder, value, StringComparison.Ordinal);
    }

    /// <summary>The version R11.101's paragraph names: <c>`curia-mcp` is x.y.z</c>, read from the paragraph that begins <c>**R11.101**</c>, and nothing else.</summary>
    private static string PublishedVersion()
    {
        var matches = Regex.Matches(PublishedAcceptBudget.Paragraph("**R11.103**"), @"`curia-mcp` is (\d+\.\d+\.\d+)", RegexOptions.CultureInvariant);
        return matches.Count == 1 ? matches[0].Groups[1].Value : throw new InvalidOperationException($"R11.103 yields {matches.Count} readings of the curia-mcp version");
    }

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId, agent);
    }

    /// <summary>
    /// Makes the profile sign with a kid the key set does not list. <paramref name="held"/> keeps the stub's own key (the signing key's bytes, which the key set serves <c>active</c> as <c>alice-1</c>) as a retained key;
    /// <paramref name="notInForce"/> records a rotation to it that is not in force at the index the key set binds it (G73's scene, degenerate as NHR11's is).
    /// </summary>
    private void Seed(bool held, bool notInForce)
    {
        var directory = _log.Store.DirectoryFor("alice");
        var path = Path.Combine(directory, "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        identity["kid"] = GoneKid;
        if (held)
        {
            var file = $"keys/{Hex(StubLog.AgentKid)}.pem";
            Directory.CreateDirectory(Path.Combine(directory, "keys"));
            File.WriteAllText(Path.Combine(directory, file), File.ReadAllText(Path.Combine(directory, "signing-key.pem")));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(directory, "keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(Path.Combine(directory, file), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var retained = new JsonObject { ["alg"] = "ES256", ["kid"] = StubLog.AgentKid, ["file"] = file };
            if (notInForce) retained["rotation_in_force"] = false;
            identity["retained_keys"] = new JsonArray(retained);
        }

        if (notInForce)
            identity["lifecycle_acts"] = new JsonArray(new JsonObject { ["kid"] = StubLog.AgentKid, ["kind"] = "rotation", ["log_index"] = _log.KeyIndex, ["in_force"] = false });

        File.WriteAllText(path, identity.ToJsonString());
    }

    private async Task<string> ReferenceAsync()
    {
        Assert.True(_log.Store.ReadProfile("alice").TryGetValue(out var profile, out var error), error?.Detail);
        var reported = await KeysReport.ReadAsync(_log.Client(), profile!, TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(reported.TryGetValue(out var reading, out var refusal), refusal?.Summary);
        return reading!.Render();
    }

    /// <summary>
    /// UDO9, the lines: <c>curia_keys</c>'s text equals what <c>curia keys</c> reports for the same profile and key set, byte for byte, and holds the errata's words, once. Rows: the held-keys form; the held-keys form
    /// beside the rotation <c>NOW IN FORCE</c> line in its not-live form; the no-held-key form. Red on the tree: <c>Expected: ... holds the errata's words  Actual: ... does not</c>.
    /// </summary>
    [Theory]
    [InlineData("held")]
    [InlineData("held-and-rotation-now-in-force")]
    [InlineData("no-held-key")]
    public async Task UDO9_CuriaKeysIsWhatCuriaKeysReportsWhenTheSigningKeyIsNotLive(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Seed(held: row != "no-held-key", notInForce: row == "held-and-rotation-now-in-force");
        var reference = await ReferenceAsync();

        await using var session = await StartAsync();
        var answer = await session.CallAsync("curia_keys", []);

        var words = new List<string>();
        if (row == "no-held-key")
        {
            var line = Span("No held key served");
            line = Once(line, "<does not list | lists as <status>>", "does not list");
            line = Once(line, "<agent id>", DisplayLiteral.Of(StubLog.Author));
            words.Add(Once(line, "<kid>", DisplayLiteral.Of(GoneKid)));
        }
        else
        {
            var line = Span("Held keys the Forum binds");
            line = Once(line, "<held keys>", $"{DisplayLiteral.Of(StubLog.AgentKid)} (bound at log index {_log.KeyIndex.ToString(CultureInfo.InvariantCulture)})");
            line = Once(line, "<does not list | lists as <status>>", "does not list");
            line = Once(line, "<n>", "1");
            line = Once(line, "<the use-key command for the key bound last>", $"curia use-key --agent 'alice' --kid '{StubLog.AgentKid}'");
            words.Add(Once(line, "<kid>", DisplayLiteral.Of(GoneKid)));
            if (row == "held-and-rotation-now-in-force")
            {
                var rotation = Span("The rotation NOW IN FORCE line while the signing key is not live");
                rotation = Once(rotation, "<i>", _log.KeyIndex.ToString(CultureInfo.InvariantCulture));
                words.Add(Once(rotation, "<new kid>", DisplayLiteral.Of(StubLog.AgentKid)));
            }
        }

        var same = string.Equals(reference, answer.Text.TrimEnd('\n') + "\n", StringComparison.Ordinal);
        var says = words.All(w => PublishedClientWords.Says(answer.Text, w));
        var once = Regex.Count(answer.Text, "SIGNING KEY NOT LIVE:", RegexOptions.CultureInvariant) == 1;   // the line begins "SIGNING KEY NOT LIVE:"; the errata's rotation line names it once more ("see SIGNING KEY NOT LIVE below")
        WholeText.Same(
            "the tool's text is what KeysReport renders; it holds the errata's words, and the line once",
            $"the tool's text {(same ? "is" : "is not")} what KeysReport renders; it {(says ? "holds" : "does not hold")} the errata's words, and the line {(once ? "once" : "not once")}"
            + (says && once ? string.Empty : $"\n--- the errata's words, filled ---\n{string.Join("\n", words)}\n--- the tool's text ---\n{answer.Text}"));
    }

    /// <summary>UDO9, the version: <c>initialize</c> reports the version R11.101's paragraph names. Red on the tree: <c>Expected: 0.12.3  Actual: 0.12.2</c>.</summary>
    [Fact]
    public void UDO9_CuriaMcpReportsTheVersionR11101Names()
    {
        var published = PublishedVersion();
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
            var options = ServerComposition.Options(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId);
            WholeText.Same(published, options.ServerInfo?.Version ?? "none");
        }
    }

    /// <summary>UDO9, the description: <c>curia_keys</c>'s description carries the sentence R11.101's words publish. Red on the tree: <c>Expected: a description with the sentence  Actual: a description without it</c>.</summary>
    [Fact]
    public async Task UDO9_TheDescriptionCarriesTheAddedSentence()
    {
        var sentence = Span("The description sentence of curia_keys");
        await using var session = await StartAsync();
        var description = (await session.ListAsync()).Single(t => t.Name == "curia_keys").Description ?? string.Empty;

        WholeText.Same(
            "a description with the sentence: " + sentence,
            (PublishedClientWords.Says(description, sentence) ? "a description with the sentence: " : "a description without it: ") + sentence);
    }
}
