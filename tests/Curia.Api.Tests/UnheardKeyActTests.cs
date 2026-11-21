using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using NodeValue = System.Text.Json.Nodes.JsonValue;
using Curia.Client;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;
using CliProgram = Curia.Client.Cli.Program;

namespace Curia.Api.Tests;

/// <summary>
/// UKA3 (errata G74, R11.99, R11.100, register item 322): the scene of item 290's Task 0 (its R3), end to end. The store's reads fail after the first append, so the rotation route answers <c>503</c> with the row
/// appended and honoured; the real client then runs <c>curia keys</c> and <c>curia rotate</c> against the real route. The oracle for "the Forum recorded it" is the store's own log: the row's position is the index of
/// the new event in the whole log, read after the request, and "made no new act" is the log's length unchanged by the settling verb. The in-test control is the same scene unarmed: a <c>201</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class UnheardKeyActTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-uka3-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProfileStore Store => new(_home);

    private ForumClient Client => new(forum.Client, forum.Client.BaseAddress!);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> action)
    {
        var (savedOut, savedError) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await action();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
        }
    }

    private async Task<(string Slug, EnrolledAgent Agent)> EnrolAsync(string stem)
    {
        var slug = "k-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(Store.Create(slug, KeyActRequests.Unique(stem), slug + "-0", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error), error?.Title);
        Assert.True((await Client.EnrolAsync(agent!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        return (slug, agent!);
    }

    private async Task<List<AppendedEvent>> WholeLogAsync()
    {
        var read = await forum.Store.ReadForwardAsync(EventSequence.Zero, null, Ct);
        return [.. read.Match(v => v, e => throw new InvalidOperationException(e.Type))];
    }

    /// <summary>The members of a JSON document and the kinds of its leaves, and nothing else: what two profiles share when they differ only in kids, indices and times.</summary>
    private static string Shape(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Key + ":" + Shape(m.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Shape)) + "]",
        NodeValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => "s",
            JsonValueKind.Number => "n",
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array => "null",
            _ => "null",
        },
        _ => "null",
    };

    private static (string, string) PublicHalf(string privatePem)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(privatePem);
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (B64(q.X!), B64(q.Y!));
    }

    /// <summary>
    /// UKA3: settle after a <c>503</c> whose row was honoured. <c>curia keys</c> prints <c>RECORDED</c> at the index the store holds for the appended event, exits 6, and prints no <c>FOREIGN</c>; <c>curia rotate</c>
    /// exits 0, the profile signs with the kept key (the one whose public half is the <c>jwk</c> of the row's own statement, read from the log), the cached token is deleted, the store's event count is unchanged by the
    /// verb, and <c>curia keys</c> then exits 0; and the settled profile has the shape the unarmed scene's <c>201</c> leaves. Red on the tree: <c>Expected: ... a RECORDED line at 5 ...  Actual: ... no RECORDED line, a FOREIGN line ...</c>.
    /// </summary>
    [Fact]
    public async Task UKA3_ARotationThe503HidAndTheForumRecordedIsSettledAndNotMadeAgain()
    {
        // The control: the same scene, unarmed. Its profile is what a 201 leaves.
        var (controlSlug, control) = await EnrolAsync("uka3c");
        using (control)
        {
            var controlSession = new ForumSession(Client, control, Store, forum.Clock);
            var made = await CaptureAsync(() => CliProgram.RotateKeyAsync(Client, controlSession, control, Store, null, Ct));
            Assert.True(made.Exit == 0, made.Stderr);
        }

        var controlShape = Shape(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Store.DirectoryFor(controlSlug), "identity.json"), Ct)));

        var (slug, agent) = await EnrolAsync("uka3");
        using var armedAgent = agent;
        var before = (await WholeLogAsync()).Count;
        forum.Store.Arm(EventStoreFault.FailReadsAfterFirstAppend);
        (int Exit, string Stdout, string Stderr) armed;
        try
        {
            var session = new ForumSession(Client, armedAgent, Store, forum.Clock);
            armed = await CaptureAsync(() => CliProgram.RotateKeyAsync(Client, session, armedAgent, Store, null, Ct));
        }
        finally
        {
            forum.Store.Disarm();
        }

        // The row's own position and statement, from the store.
        var log = await WholeLogAsync();
        var appended = log.Skip(before).Select((e, i) => (Event: e, At: before + i)).Where(x => x.Event.Event.Type.Value == "agent.key-bound").ToList();
        var row = Assert.Single(appended);
        var payload = Assert.IsType<Curia.Canon.Json.JsonValue.Object>(row.Event.Event.Payload);
        var statement = payload.Members.Single(m => m.Key == "statement").Value is Curia.Canon.Json.JsonValue.String s ? s.Value : throw new InvalidOperationException("the row holds no statement");
        using var parsed = JsonDocument.Parse(statement);
        var rowKid = parsed.RootElement.GetProperty("kid").GetString()!;
        var rowKey = (parsed.RootElement.GetProperty("jwk").GetProperty("x").GetString()!, parsed.RootElement.GetProperty("jwk").GetProperty("y").GetString()!);
        var at = row.At.ToString(CultureInfo.InvariantCulture);

        Assert.True(Store.ReadProfile(slug).TryGetValue(out var profile, out var readError), readError?.Detail);
        var keys = await CaptureAsync(() => CliProgram.ShowKeysAsync(Client, profile!, Ct));

        var eventsBefore = (await WholeLogAsync()).Count;
        Assert.True(Store.Load(slug).TryGetValue(out var again, out var loadError), loadError?.Title);
        (int Exit, string Stdout, string Stderr) settled;
        using (again)
        {
            var session = new ForumSession(Client, again!, Store, forum.Clock);
            settled = await CaptureAsync(() => CliProgram.RotateKeyAsync(Client, session, again!, Store, null, Ct));
        }

        var eventsAfter = (await WholeLogAsync()).Count;
        Assert.True(Store.ReadProfile(slug).TryGetValue(out var settledProfile, out var settledError), settledError?.Detail);
        var signingFile = Path.Combine(Store.DirectoryFor(slug), "signing-key.pem");
        var signsWithKept = settledProfile!.Kid == rowKid && File.Exists(signingFile) && PublicHalf(await File.ReadAllTextAsync(signingFile, Ct)) == rowKey;
        var settledShape = Shape(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Store.DirectoryFor(slug), "identity.json"), Ct)));
        var after = await CaptureAsync(() => CliProgram.ShowKeysAsync(Client, settledProfile, Ct));

        var actual = $"the armed rotate: exit {armed.Exit}, one agent.key-bound row appended; "
            + $"curia keys: exit {keys.Exit}, {(keys.Stdout.Contains("RECORDED: ", StringComparison.Ordinal) && keys.Stdout.Contains($"log index {at}", StringComparison.Ordinal) ? "a RECORDED line at " + at : "no RECORDED line at " + at)}, {(keys.Stdout.Contains("FOREIGN", StringComparison.Ordinal) ? "a FOREIGN line" : "no FOREIGN line")}; "
            + $"curia rotate: exit {settled.Exit}, the store's event count {(eventsAfter == eventsBefore ? "unchanged" : "moved by " + (eventsAfter - eventsBefore).ToString(CultureInfo.InvariantCulture))}, the profile signs with {(signsWithKept ? "the kept key" : "another key")}, "
            + $"token.json {(Store.ReadToken(slug) is null ? "gone" : "kept")}; curia keys after: exit {after.Exit}; the settled profile {(settledShape == controlShape ? "has the shape of the 201's" : "differs in shape from the 201's")}";
        var expected = $"the armed rotate: exit 8, one agent.key-bound row appended; "
            + $"curia keys: exit 6, a RECORDED line at {at}, no FOREIGN line; "
            + "curia rotate: exit 0, the store's event count unchanged, the profile signs with the kept key, token.json gone; curia keys after: exit 0; the settled profile has the shape of the 201's";

        Assert.True(expected == actual, $"Expected: {expected}  Actual: {actual}\n--- armed rotate stderr ---\n{armed.Stderr.Trim()}\n--- curia keys stdout ---\n{keys.Stdout.Trim()}\n--- settling rotate stderr ---\n{settled.Stderr.Trim()}");
    }
}
