using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Infrastructure;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Curia.Api.Tests;

/// <summary>
/// A clock that reads <paramref name="offset"/> after <paramref name="inner"/>: the store's clock in the fixture of errata G55's probes. On a real store the column
/// <c>server_ts</c> is stamped after the pipeline's own reading by 0.76 to 5.5 ms (Task 0's M6); a fixture whose store and pipeline share one clock cannot tell which
/// instant a receipt carries (trap 90), so the store's reads later by <see cref="ConfirmFixture.StoreAhead"/>.
/// </summary>
internal sealed class OffsetTimeProvider(TimeProvider inner, TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + offset;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => inner.CreateTimer(callback, state, dueTime, period);
}

/// <summary>
/// The host of errata G55's probes (the "A faulted append is confirmed" PR, Task 0c): <see cref="CommittedPostFixture"/> (the production Forum with the decorated event store
/// and vector index, and no catch-up loop) whose event store, and every adapter built beside it, reads a clock <see cref="StoreAhead"/> later than the pipeline's (ruling 19:
/// above the largest gap measured, 5.5 ms, and in the real direction, the pipeline earlier). Each probe takes a host of its own (<see cref="CommittedPostFixture.NewHost"/>).
/// </summary>
public class ConfirmFixture : CommittedPostFixture
{
    /// <summary>The store's clock reads this much after the pipeline's.</summary>
    internal static readonly TimeSpan StoreAhead = TimeSpan.FromMilliseconds(7);

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        base.ConfigureStore(services);

        services.RemoveAll<PostgresAdapters>();
        services.AddSingleton(sp => new PostgresAdapters(
            HostConnectionString,
            new OffsetTimeProvider(sp.GetRequiredService<TimeProvider>(), StoreAhead),
            sp.GetRequiredService<Curia.Application.Ports.ILifecycleAppendGate>()));
    }
}

/// <summary>The same fixture on a real socket (Kestrel): for the probes that run the real <c>curia</c> and for the confirm over a socket.</summary>
public sealed class ConfirmKestrelFixture : ConfirmFixture
{
    public ConfirmKestrelFixture() => UseKestrel(0);
}

/// <summary>One stored <c>post.accepted</c> as the events table holds it: the oracle of every probe (read by SQL on the fixture's own connection string, by digest, never through the Forum).</summary>
internal sealed record StoredPost(long Seq, string PostId, string Kind, string Author, DateTimeOffset PayloadServerTs, DateTimeOffset ColumnServerTs);

internal static partial class Afc
{
    /// <summary>The digest of a signed wire, recomputed from the envelope it carries (<see cref="ReceivedAct"/>), never from what the client or the Forum returned.</summary>
    internal static string DigestOf(byte[] wire) => ReceivedAct.DigestOf(Encoding.UTF8.GetString(wire));

    /// <summary>Every <c>post.accepted</c> whose digest is <paramref name="digest"/>, in log order.</summary>
    internal static async Task<IReadOnlyList<StoredPost>> StoredAsync(ForumFixture forum, string digest, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT seq, payload->>'post_id', payload->>'kind', payload->>'author', payload->>'server_ts', server_ts FROM events " +
            "WHERE event_type = 'post.accepted' AND payload->>'digest' = @digest ORDER BY seq;", admin);
        command.Parameters.AddWithValue("digest", digest);
        var rows = new List<StoredPost>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new StoredPost(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                new DateTimeOffset(reader.GetDateTime(5).ToUniversalTime())));
        }

        return rows;
    }

    /// <summary>The <c>post_embeddings</c> rows whose digest is <paramref name="digest"/>, with the sequence each holds.</summary>
    internal static async Task<IReadOnlyList<long>> EmbeddingSequencesAsync(ForumFixture forum, string digest, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT seq FROM post_embeddings WHERE digest = @digest ORDER BY seq;", admin);
        command.Parameters.AddWithValue("digest", digest);
        var seqs = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) seqs.Add(reader.GetInt64(0));
        return seqs;
    }

    /// <summary>The log events with this id captured since <paramref name="mark"/>.</summary>
    internal static IReadOnlyList<LoggedEvent> Logged(LogCapture log, int mark, int eventId) => [.. log.Since(mark).Where(e => e.EventId == eventId)];

    /// <summary>
    /// The outcome word in an <c>AppendNotConfirmed</c> message, read by turning the entry's published message into a pattern (<c>&lt;Kind&gt;</c> and <c>&lt;FaultType&gt;</c>
    /// anything, <c>&lt;Outcome&gt;</c> a word), or <see langword="null"/> where the message is not the published one.
    /// </summary>
    internal static string? OutcomeOf(LoggedEvent logged)
    {
        var template = PublishedConfirmWords.NotConfirmedEvent().Message;
        var pattern = "^" + Regex.Escape(template).Replace("<Kind>", ".+", StringComparison.Ordinal).Replace("<FaultType>", ".+", StringComparison.Ordinal).Replace("<Outcome>", "(?<outcome>[a-z]+)", StringComparison.Ordinal) + "$";
        var match = Regex.Match(logged.Message, pattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["outcome"].Value : null;
    }

    /// <summary>True where the message is the entry's published message for a confirmed post.</summary>
    internal static bool IsConfirmedMessage(LoggedEvent logged)
    {
        var template = PublishedConfirmWords.ConfirmedEvent().Message;
        var pattern = "^" + Regex.Escape(template).Replace("<Kind>", ".+", StringComparison.Ordinal).Replace("<FaultType>", ".+", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(logged.Message, pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>The names of the top-level members of a JSON object body, in order.</summary>
    internal static string[] MemberNames(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>The text of the top-level member <paramref name="name"/> of a JSON body, or <see langword="null"/>.</summary>
    internal static string? Member(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
