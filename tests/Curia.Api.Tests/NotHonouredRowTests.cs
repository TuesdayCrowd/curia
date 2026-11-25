using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CliProgram = Curia.Client.Cli.Program;

namespace Curia.Api.Tests;

/// <summary>
/// An event store that, while armed, replaces the <c>authorization</c> member of the payload of every entry it appends with <c>x</c> before delegating (the shape of LRV43's <c>SignatureTamperingStore</c>,
/// <c>WritersRoundTripTests</c>): the row lands in the log with a signature no key made, which the fold skips, however well the writer built it. It records every entry appended, in order, by type and id, so a
/// fact reads the row's own position afterwards from the log by the row's event id and not from the answer.
/// </summary>
internal sealed class ArmedTamperingStore(IEventStore inner) : IEventStore
{
    private readonly object _gate = new();

    internal IEventStore Inner => inner;

    internal volatile bool Armed;

    internal List<(string Type, string Id)> Appended { get; } = [];

    internal void Clear()
    {
        lock (_gate) Appended.Clear();
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
        inner.ReadByAggregateAsync(aggregateId, cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
        inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);

    private IReadOnlyList<DomainEvent> Tamper(IReadOnlyList<DomainEvent> events)
    {
        lock (_gate)
            foreach (var e in events) Appended.Add((e.Type.Value, e.Id.Value));

        if (!Armed) return events;

        return
        [
            .. events.Select(e => e.Payload is JsonValue.Object o && o.Members.Any(m => m.Key == "authorization")
                ? e with { Payload = new JsonValue.Object([.. o.Members.Select(m => m.Key == "authorization" ? new KeyValuePair<string, JsonValue>("authorization", new JsonValue.String("x")) : m)]) }
                : e),
        ];
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
        inner.AppendAsync(aggregateId, expectedVersion, Tamper(events), cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
        inner.AppendAsync(aggregateId, expectedVersion, Tamper(events), condition, cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition? condition, AppendGuard guard, CancellationToken cancellationToken = default) =>
        inner.AppendAsync(aggregateId, expectedVersion, Tamper(events), condition, guard, cancellationToken);
}

/// <summary>
/// The Forum with its event store decorated by <see cref="ArmedTamperingStore"/> (register item 290, errata G73): a fixture a fact arms for one request, so that the route appends a row the fold skips and answers
/// <c>500 curia/keys/not-honoured</c>. A sibling of the sealed <c>KeyRouteKestrelFixture</c> and of <c>ForumFixture</c>, which are not edited.
/// </summary>
public class NotHonouredFixture : FaultableForumFixture
{
    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        base.ConfigureStore(services);
        var registered = services.Last(d => d.ServiceType == typeof(IEventStore));
        services.Remove(registered);
        services.AddSingleton<IEventStore>(sp => new ArmedTamperingStore((IEventStore)registered.ImplementationFactory!(sp)));
    }

    internal ArmedTamperingStore Tamper => (ArmedTamperingStore)Services.GetRequiredService<IEventStore>();

    /// <summary>The whole log, read from beneath the decorator.</summary>
    internal async Task<List<AppendedEvent>> WholeLogAsync(CancellationToken ct)
    {
        var read = await Tamper.Inner.ReadForwardAsync(EventSequence.Zero, null, ct);
        return [.. read.Match(v => v, e => throw new InvalidOperationException(e.Type))];
    }
}

/// <summary>The same Forum on a real socket, for the client over the real route (NHR12).</summary>
public sealed class NotHonouredKestrelFixture : NotHonouredFixture
{
    public NotHonouredKestrelFixture() => UseKestrel(0);
}

/// <summary>
/// NHR1 (errata G73, R4.44, register item 290): the route's answer to a row the fold skips names the row's log index. The store appends the row with its authorization replaced, so the Forum skips it and answers
/// <c>500 curia/keys/not-honoured</c>; the body's members are <c>{detail, log_index, title, type}</c>, and <c>log_index</c> is the position the log holds the appended row at, read after the request from the log by
/// the row's own event id (an artifact other than the answer). The in-test control is the same scene unarmed: a <c>201</c> whose <c>log_index</c> is the store's position by the same oracle. Theory: a rotation and a
/// retirement. Red on the tree: <c>Expected: members [detail, log_index, title, type] ...  Actual: members [detail, title, type] ...</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class NotHonouredRowEndpointTests(NotHonouredFixture forum) : IClassFixture<NotHonouredFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> PostAsync(DpopClient dpop, string token, string url, byte[] body) =>
        dpop.PostAsync(forum.Client, url, token, body, forum.Now, Ct, contentType: "application/json");

    private static string Members(JsonDocument body) =>
        "[" + string.Join(", ", body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)) + "]";

    private static string Member(JsonDocument body, string name) =>
        body.RootElement.TryGetProperty(name, out var value) ? value.ToString() : "absent";

    [Theory]
    [InlineData("rotate")]
    [InlineData("retire")]
    public async Task NHR1_ANotHonouredAnswerNamesTheRowsLogIndex(string act)
    {
        var agent = ForumAgent.Create(KeyActRequests.Unique("nhr1"), "nhr1-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, Ct);
        var firstKey = new KeyActRequests.KeyPair(agent.Kid, agent.AssertionKey);

        // The control: the same scene unarmed. A rotation answers 201 and its log_index is the store's position of its row by the oracle used below. (For the retirement it also gives the agent a key to retire.)
        var control = KeyActRequests.KeyPair.New("c-" + Guid.NewGuid().ToString("N")[..8]);
        forum.Tamper.Clear();
        using var created = await PostAsync(dpop, token, KeyActRequests.RotateUrl(agent.AgentId), KeyActRequests.RotationBody(agent, control, await KeyActRequests.LatestLifecycleAsync(forum.Client, agent.AgentId, Ct)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var controlRow = Assert.Single(forum.Tamper.Appended, a => a.Type == "agent.key-bound");
        var controlAt = (await forum.WholeLogAsync(Ct)).FindIndex(e => e.Event.Id.Value == controlRow.Id);
        WholeText.Same($"the unarmed rotation answers 201 with log_index {controlAt}", $"the unarmed rotation answers 201 with log_index {Member(createdBody, "log_index")}");

        var mark = forum.Log.Mark();
        var prev = await KeyActRequests.LatestLifecycleAsync(forum.Client, agent.AgentId, Ct);
        var rowType = act == "rotate" ? "agent.key-bound" : "agent.key-retired";
        Task<HttpResponseMessage> Send() => act == "rotate"
            ? PostAsync(dpop, token, KeyActRequests.RotateUrl(agent.AgentId), KeyActRequests.RotationBody(agent, KeyActRequests.KeyPair.New("n-" + Guid.NewGuid().ToString("N")[..8]), prev))
            : PostAsync(dpop, token, KeyActRequests.RetireUrl(agent.AgentId, firstKey.Kid), KeyActRequests.RetirementBody(agent.AgentId, firstKey, prev));

        forum.Tamper.Clear();
        forum.Tamper.Armed = true;
        HttpResponseMessage response;
        try
        {
            response = await Send();
        }
        finally
        {
            forum.Tamper.Armed = false;
        }

        using var _ = response;
        var text = await response.Content.ReadAsStringAsync(Ct);

        // First, that the tampering happened: a 500 of this type, and a row of the kind in the log.
        using var body = JsonDocument.Parse(text);
        Assert.True(
            response.StatusCode == HttpStatusCode.InternalServerError && Member(body, "type") == "curia/keys/not-honoured",
            $"Expected: 500 curia/keys/not-honoured (the row was tampered)  Actual: {(int)response.StatusCode} {Member(body, "type")}");
        var row = Assert.Single(forum.Tamper.Appended, a => a.Type == rowType);
        var at = (await forum.WholeLogAsync(Ct)).FindIndex(e => e.Event.Id.Value == row.Id);
        var detail = forum.Log.Since(mark).Where(e => e.EventId == 5000).Select(e => e.Message).SingleOrDefault() ?? "no 5000 line";

        var expected = $"members [detail, log_index, title, type]; type curia/keys/not-honoured; detail null; log_index {at}; the 5000 line names log index {at} and lifecycle-typ-mismatch";
        var actual = $"members {Members(body)}; type {Member(body, "type")}; detail {(body.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Null ? "null" : Member(body, "detail"))}; log_index {Member(body, "log_index")}; "
            + $"the 5000 line {(detail.Contains($"log index {at}", StringComparison.Ordinal) && detail.Contains("lifecycle-typ-mismatch", StringComparison.Ordinal) ? $"names log index {at} and lifecycle-typ-mismatch" : "does not: " + detail)}";

        WholeText.Same(expected, actual);
    }
}

/// <summary>
/// NHR2 (errata G73, R11.33 (add.), R4.44): <c>ServerFaultBody</c>, the public pure type <c>ServerFault</c> composes the members of a 5xx body with, so that the set case is driven without an
/// <c>InternalsVisibleTo</c> (CS-5). A 5xx of another type carries <c>type</c>, <c>title</c> and a null <c>detail</c> and nothing else even when its error holds an index; the not-honoured type with an index of at
/// least 0 carries <c>log_index</c> as a fourth member; with no index or a negative one, nothing more. Not the first probe of the unset case (four whole-body tests hold it), the first of the set case. Red on the
/// seam: the not-honoured row with 7 (<c>Expected: ... log_index 7  Actual: ... no log_index</c>); the other rows are green by design.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ServerFaultBodyTests
{
    [Theory]
    [InlineData("curia/store/unavailable", 7L, "type, title, detail; detail null; no log_index")]
    [InlineData("curia/keys/too-many-live-keys", 7L, "type, title, detail; detail null; no log_index")]
    [InlineData("curia/keys/not-honoured", 7L, "type, title, detail, log_index; detail null; log_index 7")]
    [InlineData("curia/keys/not-honoured", null, "type, title, detail; detail null; no log_index")]
    [InlineData("curia/keys/not-honoured", -1L, "type, title, detail; detail null; no log_index")]
    public void NHR2_OnlyTheNotHonouredTypeCarriesTheIndex(string type, long? logIndex, string expected)
    {
        var body = ServerFaultBody.Of(new Error(type, "A title", "a detail that is withheld") { LogIndex = logIndex });

        var actual = $"{string.Join(", ", body.Keys)}; detail {(body.TryGetValue("detail", out var detail) && detail is null ? "null" : "set")}; "
            + (body.TryGetValue("log_index", out var index) ? $"log_index {index}" : "no log_index");

        WholeText.Same(expected, actual);
    }
}

/// <summary>
/// NHR12 (errata G73, R4.44, R11.98, R11.96 (add.)): the real route and the real client agree, over a real socket. <c>curia rotate</c> runs against a Forum whose store skips the row it appends: it exits 6, the
/// act it records is at the position the log holds the row (read by the row's event id), the key it keeps is the public half of the <c>jwk</c> in the row's own statement read from the log, and <c>curia keys</c>
/// then prints the second form of the <c>SKIPPED</c> line at that index. Red on the tree: <c>Expected: exit 6 ...  Actual: exit 8 ...</c>, with stderr naming <c>curia/keys/not-honoured</c> and HTTP 500.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class NotHonouredEndToEndTests(NotHonouredKestrelFixture forum) : IClassFixture<NotHonouredKestrelFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-nhr12-").FullName;

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

    [Fact]
    public async Task NHR12_TheRealClientAndTheRealRouteAgreeOnARowTheForumSkips()
    {
        var agentId = KeyActRequests.Unique("nhr12");
        var slug = "k-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(Store.Create(slug, agentId, slug + "-0", forum.Client.BaseAddress!).TryGetValue(out var created, out var createError), createError?.Title);
        using var agent = created;
        Assert.True((await Client.EnrolAsync(agent!, Ct, forum.Clock)).TryGetValue(out _, out var refusal), refusal?.Summary);
        var session = new ForumSession(Client, agent!, Store, forum.Clock);

        forum.Tamper.Clear();
        forum.Tamper.Armed = true;
        (int Exit, string Stdout, string Stderr) rotated;
        try
        {
            rotated = await CaptureAsync(() => CliProgram.RotateKeyAsync(Client, session, agent!, Store, null, Ct));
        }
        finally
        {
            forum.Tamper.Armed = false;
        }

        // The row's own position and statement, from the log by its event id.
        var row = Assert.Single(forum.Tamper.Appended, a => a.Type == "agent.key-bound");
        var log = await forum.WholeLogAsync(Ct);
        var at = log.FindIndex(e => e.Event.Id.Value == row.Id);
        var payload = Assert.IsType<JsonValue.Object>(log[at].Event.Payload);
        var statement = payload.Members.Single(m => m.Key == "statement").Value is JsonValue.String s ? s.Value : throw new InvalidOperationException("the row holds no statement");
        using var parsed = JsonDocument.Parse(statement);
        var jwk = parsed.RootElement.GetProperty("jwk");
        var rowKid = parsed.RootElement.GetProperty("kid").GetString()!;
        var rowKey = (jwk.GetProperty("x").GetString()!, jwk.GetProperty("y").GetString()!);

        var identity = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Store.DirectoryFor(slug), "identity.json"), Ct))!.AsObject();
        var acts = (identity["lifecycle_acts"]?.AsArray() ?? []).Select(a => $"{a!["kind"]} {a["kid"]} {a["log_index"]} in_force={a["in_force"]?.ToJsonString() ?? "absent"}").ToArray();
        var keptEntry = (identity["retained_keys"]?.AsArray() ?? []).FirstOrDefault(k => (string?)k!["kid"] == rowKid);
        var keptFile = keptEntry is null ? null : Path.Combine(Store.DirectoryFor(slug), (string)keptEntry["file"]!);
        var keptKey = keptFile is not null && File.Exists(keptFile) ? PublicHalf(await File.ReadAllTextAsync(keptFile, Ct)) : default;

        Assert.True(Store.ReadProfile(slug).TryGetValue(out var profile, out var readError), readError?.Detail);
        var keys = await CaptureAsync(() => CliProgram.ShowKeysAsync(Client, profile!, Ct));
        var second = PublishedClientWords.SkippedMade
            .Replace("<type>", "agent.key-bound", StringComparison.Ordinal).Replace("<i>", at.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("<predicate>", "lifecycle-typ-mismatch", StringComparison.Ordinal);

        var expected = $"rotate exit 6; acts [rotation {rowKid} {at} in_force=false]; the kept key's public half is the jwk in the row's statement; curia keys exit 6 and prints the second SKIPPED form at {at}";
        var actual = $"rotate exit {rotated.Exit}; acts [{string.Join("; ", acts)}]; the kept key's public half {(keptKey == rowKey ? "is" : "is not")} the jwk in the row's statement; "
            + $"curia keys exit {keys.Exit} and {(PublishedClientWords.Says(keys.Stdout, second) ? "prints" : "does not print")} the second SKIPPED form at {at}";

        Assert.True(expected == actual, $"Expected: {expected}  Actual: {actual}\n--- rotate stderr ---\n{rotated.Stderr.Trim()}\n--- curia keys stdout ---\n{keys.Stdout.Trim()}");
    }

    private static (string, string) PublicHalf(string privatePem)
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(privatePem);
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (B64(q.X!), B64(q.Y!));
    }
}
