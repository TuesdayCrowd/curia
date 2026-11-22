using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// UKA12 (errata G74, R11.100, register item 322): <c>curia_keys</c> says what <c>curia keys</c> says for an act this host sent and kept as not yet known, <c>curia-mcp</c> is the version R11.100's paragraph names,
/// and the tool's description carries the sentence the errata publishes. The profile is written by hand (a pending act, a retained key), so the probes are red by assertion on a tree that reads no <c>pending_acts</c>.
/// Every word is read from the errata at test time. The text is compared with <see cref="KeysReport"/>'s, the one definition the CLI and the tool both call (as <c>KeysToolTests</c> does); the retirement the Forum
/// recorded is not a row here, because <see cref="StubLog"/> serves one key and cannot show a retired one: the Client suite's UKA11 holds it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class UnheardKeysToolTests : IDisposable
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-uka12-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A clock a fact sets; the tool and the reference report read the same one.</summary>
    private sealed class SetClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Span(string paragraph, string label)
    {
        var text = PublishedAcceptBudget.Paragraph(paragraph);
        var marker = $"*{label}:*";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException($"the errata's {paragraph} do not hold {marker} exactly once");

        var open = text.IndexOf('`', at + marker.Length);
        var close = text.IndexOf('`', open + 1);
        return open < 0 || close < 0 || text[(at + marker.Length)..open].Trim().Length != 0 ? throw new InvalidOperationException($"no span follows {marker}") : text[(open + 1)..close];
    }

    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Hex(string kid) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kid)));

    private async Task<McpProtocolSession> StartAsync(TimeProvider clock)
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, clock), clock);
        return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer, clock), writer.Agent.Profile.AgentId, agent);
    }

    /// <summary>Writes the profile's record of an act sent and not heard from: the retained key (a file under <c>keys/</c>) and the pending act.</summary>
    private void Seed(string kind, string kid, string privatePem)
    {
        var directory = _log.Store.DirectoryFor("alice");
        var file = $"keys/{Hex(kid)}.pem";
        Directory.CreateDirectory(Path.Combine(directory, "keys"));
        File.WriteAllText(Path.Combine(directory, file), privatePem);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(directory, "keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(directory, file), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var path = Path.Combine(directory, "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var retained = new JsonObject { ["alg"] = "ES256", ["kid"] = kid, ["file"] = file };
        if (kind == "rotation") retained["rotation_in_force"] = false;
        identity["retained_keys"] = new JsonArray(retained);
        identity["pending_acts"] = new JsonArray(new JsonObject { ["kind"] = kind, ["kid"] = kid, ["sent_at"] = Sent.ToString("o", CultureInfo.InvariantCulture) });
        File.WriteAllText(path, identity.ToJsonString());
    }

    private static string NewPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>The report <c>curia keys</c> prints for the profile as it is on disk now, as of <paramref name="clock"/> (errata G74, plan Amendment 2 ruling 4: the report is judged against the window on the clock it is given).</summary>
    private async Task<string> ReferenceAsync(TimeProvider clock)
    {
        Assert.True(_log.Store.ReadProfile("alice").TryGetValue(out var profile, out var error), error?.Detail);
        var reported = await KeysReport.ReadAsync(_log.Client(), profile!, clock, TestContext.Current.CancellationToken);
        Assert.True(reported.TryGetValue(out var reading, out var refusal), refusal?.Summary);
        return reading!.Render();
    }

    /// <summary>
    /// UKA12, the lines: <c>curia_keys</c>'s text equals what <c>curia keys</c> reports for the same profile and key set, byte for byte, and holds the errata's words for the line. Rows: <c>UNKNOWN</c> at 4:59 and
    /// <c>NOT RECORDED</c> at 5:00 for a rotation and a retirement, and <c>RECORDED</c> for a rotation (the one scene <see cref="StubLog"/> can serve: the key set binds the stub's own key at index 1, so the act names that
    /// kid and its key file holds that key, as NHR11's degenerate scene does). Red on the tree: <c>Expected: ... holds the errata's words  Actual: ... does not</c>.
    /// </summary>
    [Theory]
    [InlineData("unknown-rotation")]
    [InlineData("not-recorded-rotation")]
    [InlineData("recorded-rotation")]
    [InlineData("unknown-retirement")]
    [InlineData("not-recorded-retirement")]
    public async Task UKA12_CuriaKeysIsWhatCuriaKeysReportsForAnActNotHeardFrom(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var rotation = row.EndsWith("rotation", StringComparison.Ordinal);
        var which = row[..row.LastIndexOf('-')];
        var now = which == "not-recorded" ? Sent + TimeSpan.FromMinutes(5) : Sent + TimeSpan.FromSeconds(299);
        string signing;
        using (var alice = ECDsa.Create())
        {
            signing = await File.ReadAllTextAsync(Path.Combine(_log.Store.DirectoryFor("alice"), "signing-key.pem"), TestContext.Current.CancellationToken);
            alice.ImportFromPem(signing);
        }

        var kid = rotation ? (which == "recorded" ? StubLog.AgentKid : "alice-feed") : "alice-old";
        Seed(rotation ? "rotation" : "retirement", kid, which == "recorded" ? signing : NewPem());

        var clock = new SetClock(now);
        var reference = await ReferenceAsync(clock);
        await using var session = await StartAsync(clock);
        var answer = await session.CallAsync("curia_keys", []);

        var label = which switch { "recorded" => "Recorded", "unknown" => "Unknown", _ => "Not recorded" };
        var template = Span("**R11.100 words.**", $"{label}, a {(rotation ? "rotation" : "retirement")}");
        var words = template
            .Replace("<the rotate command>", "curia rotate --agent alice", StringComparison.Ordinal)
            .Replace("<the retire command for <kid>>", $"curia retire --agent alice --kid {kid}", StringComparison.Ordinal)
            .Replace("<new kid>", DisplayLiteral.Of(kid), StringComparison.Ordinal)
            .Replace("<kid>", DisplayLiteral.Of(rotation ? StubLog.AgentKid : kid), StringComparison.Ordinal)
            .Replace("<sent>", Instant(Sent), StringComparison.Ordinal)
            .Replace("<after>", Instant(Sent + TimeSpan.FromMinutes(5)), StringComparison.Ordinal)
            .Replace("<i>", _log.KeyIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain('<', words);

        var same = string.Equals(reference, answer.Text.TrimEnd('\n') + "\n", StringComparison.Ordinal);
        var says = PublishedClientWords.Says(answer.Text, words);
        WholeText.Same(
            $"the tool's text is what KeysReport renders; it holds the errata's words: {words}",
            $"the tool's text {(same ? "is" : "is not")} what KeysReport renders; it {(says ? "holds" : "does not hold")} the errata's words: {words}"
            + (same && says ? string.Empty : $"\n--- the tool's text ---\n{answer.Text}"));
    }

    /// <summary>UKA12, the version: <c>curia-mcp</c> reports the version R11.100's paragraph names, read by the one reader (<c>PublishedReadersLastWords.Version()</c>, re-pointed at R11.100's paragraph by errata G74's build). Red on the tree: <c>Expected: 0.12.2  Actual: 0.12.1</c>.</summary>
    [Fact]
    public async Task UKA12_CuriaMcpReportsTheVersionR11100Names()
    {
        var published = PublishedReadersLastWords.Version();

        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
            var options = ServerComposition.Options(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId);
            WholeText.Same(published, options.ServerInfo?.Version ?? "none");
        }
    }

    /// <summary>UKA12, the description: <c>curia_keys</c>'s description carries the sentence R11.100's words publish. Red on the tree: <c>Expected: a description with the sentence  Actual: a description without it</c>.</summary>
    [Fact]
    public async Task UKA12_TheDescriptionCarriesTheNewSentence()
    {
        var sentence = Span("**R11.100 words.**", "The description's added sentence");
        await using var session = await StartAsync(TimeProvider.System);
        var description = (await session.ListAsync()).Single(t => t.Name == "curia_keys").Description ?? string.Empty;

        WholeText.Same(
            "a description with the sentence: " + sentence,
            (PublishedClientWords.Says(description, sentence) ? "a description with the sentence: " : "a description without it: ") + sentence);
    }
}
