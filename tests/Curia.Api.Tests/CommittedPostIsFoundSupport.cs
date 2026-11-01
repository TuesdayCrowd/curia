using System.Globalization;
using System.Text.Json;
using Curia.Api.Adapters;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Curia.Api.Tests;

/// <summary>
/// The host of errata G54's probes (the "A committed post is found" PR, Task 0c): the production Forum with the decorated event store (<see cref="FaultableEventStore"/>)
/// and the decorated vector index (<see cref="FailingVectorIndex"/>) and, per ruling 14, <b>without the catch-up's hosted loop</b>: the fixture's clock has no timers, so
/// the loop's <c>Task.Delay</c> never fires in process and an advance proves nothing by itself. A probe that advances time drives the tick itself, once per step, through
/// <see cref="CheckHost.TickAsync"/>, and <see cref="CheckHost.LoopIsAbsent"/> says the loop is gone so no probe passes vacuously. Each probe takes a fresh host over the
/// fixture's database (<see cref="NewHost"/>), so each starts with its own sweep, its own seams and the start reconcile having run.
/// </summary>
public class CommittedPostFixture : FaultableForumFixture
{
    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        base.ConfigureStore(services);

        var index = services.Last(d => d.ServiceType == typeof(IVectorIndex));
        if (index.ImplementationFactory is null) throw new InvalidOperationException("IVectorIndex is registered by a factory; this decorator wraps what it builds");
        services.Remove(index);
        services.AddSingleton<IVectorIndex>(sp => new FailingVectorIndex((IVectorIndex)index.ImplementationFactory(sp)));

        foreach (var loop in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EmbeddingCatchUpService)).ToList())
            services.Remove(loop);
    }

    /// <summary>A host over this fixture's database with seams of its own; the caller disposes it.</summary>
    internal CheckHost NewHost(Action<IServiceCollection>? configure = null) =>
        new(this, WithWebHostBuilder(builder => { if (configure is not null) builder.ConfigureTestServices(configure); }));

    /// <summary>The fixture's own host (the first one built), for a probe that needs the fixture itself to be the one on a socket.</summary>
    internal CheckHost Itself() => new(this, this);
}

/// <summary>The same decorated Forum on a real socket (Kestrel): for the probe that runs the real <c>curia</c> against it.</summary>
public sealed class CommittedPostKestrelFixture : CommittedPostFixture
{
    public CommittedPostKestrelFixture() => UseKestrel(0);
}

/// <summary>One host of <see cref="CommittedPostFixture"/> and what a probe reaches on it.</summary>
internal sealed class CheckHost(ForumFixture owner, WebApplicationFactory<Program> factory) : IAsyncDisposable
{
    private static readonly string[] DiscussionKinds = ["question", "answer", "finding", "comment", "revision"];

    private HttpClient? _client;

    internal ForumFixture Owner { get; } = owner;

    internal HttpClient Client => _client ??= factory.CreateClient();

    internal IServiceProvider Services => factory.Services;

    internal FaultableEventStore Store => (FaultableEventStore)Services.GetRequiredService<IEventStore>();

    internal FailingVectorIndex Index => (FailingVectorIndex)Services.GetRequiredService<IVectorIndex>();

    internal IndexSweep Sweep => Services.GetRequiredService<IndexSweep>();

    internal IndexBacklog Backlog => Services.GetRequiredService<IndexBacklog>();

    /// <summary>True where the host holds no <see cref="EmbeddingCatchUpService"/>: a time advance on this host runs only the ticks the probe drives.</summary>
    internal bool LoopIsAbsent => !Services.GetServices<IHostedService>().Any(s => s is EmbeddingCatchUpService);

    /// <summary>One tick of the catch-up at <paramref name="now"/>: the call the hosted loop makes every second, made by the probe.</summary>
    internal Task<CatchUpResult> TickAsync(DateTimeOffset now, CancellationToken ct) =>
        Services.GetRequiredService<EmbeddingCatchUp>().RunOnceAsync(Services.GetRequiredService<IEventReader>(), now, ct);

    /// <summary>Moves the fixture's clock forward one second and ticks, <paramref name="seconds"/> times; the clock port is the fixture's, which every host shares.</summary>
    internal async Task TicksAsync(int seconds, CancellationToken ct)
    {
        for (var i = 0; i < seconds; i++)
        {
            ((ManualTimeProvider)Services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromSeconds(1));
            await TickAsync(Owner.Now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The serving process's statement of its own lag, from the raw search response: <c>vector_pending</c>, <c>vector_checked_at</c>, <c>vector_check_due_at</c>.</summary>
    internal async Task<CheckState> StateAsync(CancellationToken ct, string query = "q=quokka")
    {
        using var response = await Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new CheckState((int)response.StatusCode, body, IndexLag.Member(body, "vector_pending"), IndexLag.Member(body, "vector_checked_at"), IndexLag.Member(body, "vector_check_due_at"));
    }

    /// <summary>
    /// The oracle: the <c>post.accepted</c> events of <paramref name="author"/> whose payload kind is a discussion kind (the set is written here, from §9, and is not
    /// <c>PostKinds.IsDiscussion</c>) and whose digest <c>post_embeddings</c> does not hold. SQL on the fixture's own connection string, the kind read as
    /// <c>payload-&gt;&gt;'kind'</c> with no cast, nothing subtracted: the population is named by the author, not corrected by a baseline.
    /// </summary>
    internal async Task<long> AntiJoinAsync(string author, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(Owner.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events e WHERE e.event_type = 'post.accepted' AND e.payload->>'author' = @author " +
            "AND e.payload->>'kind' = ANY(@kinds) AND NOT EXISTS (SELECT 1 FROM post_embeddings p WHERE p.digest = e.payload->>'digest');",
            admin);
        command.Parameters.AddWithValue("author", author);
        command.Parameters.AddWithValue("kinds", DiscussionKinds);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>post.accepted</c> events <paramref name="author"/> has in the table (any kind).</summary>
    internal async Task<long> EventsOfAsync(string author, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(Owner.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author;", admin);
        command.Parameters.AddWithValue("author", author);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>The events table's count and highest sequence, read by SQL on the fixture's own connection string (the log as it is, not as a reader of the Forum serves it).</summary>
    internal async Task<(long Count, long MaxSeq)> LogShapeAsync(CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(Owner.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT count(*), coalesce(max(seq), 0) FROM events;", admin);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (!ReferenceEquals(factory, Owner)) await factory.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>What one search said of the Forum's lag: the status, the body and the three members as raw JSON text (<c>absent</c> where a member is not there).</summary>
internal sealed record CheckState(int Status, string Body, string Pending, string CheckedRaw, string DueRaw)
{
    private static string Unquote(string raw) => raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? JsonSerializer.Deserialize<string>(raw)! : raw;

    /// <summary>The checked instant as served, or the raw text where it is not a string.</summary>
    internal string Checked => Unquote(CheckedRaw);

    /// <summary>The due instant as served, <c>null</c> for JSON null, or <c>absent</c>.</summary>
    internal string Due => Unquote(DueRaw);

    /// <summary>True where the due member is a string that parses as an instant.</summary>
    internal bool Owed => DueRaw.StartsWith('"') && DateTimeOffset.TryParse(Due, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    internal DateTimeOffset? DueAt => Owed ? DateTimeOffset.Parse(Due, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    /// <summary>The words an assertion prints for the state.</summary>
    internal string Says => $"vector_pending={Pending} vector_checked_at={CheckedRaw} vector_check_due_at={DueRaw}";
}

/// <summary>
/// Holds the private lock of an <see cref="IndexSweep"/> or an <see cref="IndexBacklog"/> on a thread of its own, so a probe can stop a search at the one call that waits for
/// it and run what it likes (on that same thread, which owns the lock) before letting it go. The lock is the class's own, found by name.
/// </summary>
internal sealed class HeldLock
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _done;

    private HeldLock(object owner, Action whileHeld)
    {
        var gate = (Lock)(owner.GetType().GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException("the class holds no lock named _gate"));
        _done = Task.Factory.StartNew(
            () =>
            {
                using (gate.EnterScope())
                {
                    _held.SetResult();
                    _go.Task.Wait(TimeSpan.FromSeconds(60));
                    whileHeld();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Takes the lock of <paramref name="owner"/>; <paramref name="whileHeld"/> runs on the holding thread when <see cref="ReleaseAsync"/> is called, before the lock is let go.</summary>
    internal static HeldLock Take(object owner, Action whileHeld) => new(owner, whileHeld);

    /// <summary>Completes when the lock is held.</summary>
    internal Task Held => _held.Task;

    /// <summary>Runs the held action, lets the lock go, and completes when that is done.</summary>
    internal Task ReleaseAsync()
    {
        _go.TrySetResult();
        return _done;
    }
}

/// <summary>A startup filter that puts a middleware outside every other: it records, when a response starts, the status and the sweep's next attempt at that moment.</summary>
internal sealed class ResponseStartRecorder(System.Collections.Concurrent.ConcurrentQueue<(string Method, int Status, DateTimeOffset? DueAt)> seen) : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(async (context, following) =>
            {
                context.Response.OnStarting(() =>
                {
                    seen.Enqueue((context.Request.Method, context.Response.StatusCode, context.RequestServices.GetRequiredService<IndexSweep>().DueAt));
                    return Task.CompletedTask;
                });
                await following(context).ConfigureAwait(false);
            });
            next(app);
        };
    }
}
