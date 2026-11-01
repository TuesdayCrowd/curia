using System.Globalization;
using System.Text.Json;
using Curia.Api.Adapters;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
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
    internal CheckHost NewHost() => new(this, WithWebHostBuilder(_ => { }));

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
