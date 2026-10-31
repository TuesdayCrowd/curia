using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.AuthN.Ports;
using Curia.Infrastructure;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Curia.Api.Tests;

/// <summary>
/// The Forum hosted in process (Development, the owner's connection) with a decorator on each store the D95 PR's probes fail: the event store, the nonce
/// store, the replay cache and the Registrar's key store. Every decorator forwards to the real adapter and fails only when the test arms it, after the test has
/// seeded (errata G52, ruling 9; plan "FaultableStore"). One host per fixture: the decorators are the host's singletons, kept here for the test to arm.
/// </summary>
public class FaultableForumFixture : ForumFixture
{
    // Resolved from the host, which this builds (and on Kestrel starts) when it is first asked: a decorator is the host's singleton and is never
    // kept in a field that is empty until a request has been made.
    internal FaultableEventStore Store => (FaultableEventStore)Services.GetRequiredService<IEventStore>();

    internal FaultableNonceStore Nonces => (FaultableNonceStore)Services.GetRequiredService<IDpopNonceStore>();

    internal FaultableReplayCache Replay => (FaultableReplayCache)Services.GetRequiredService<IReplayCache>();

    internal FaultableKeyStore Keys => Services.GetRequiredService<FaultableKeyStore>();

    /// <summary>Everything the host logs, with each event's exception.</summary>
    internal LogCapture Log { get; } = new();

    /// <summary>The header a fact puts on a request to learn when the host has finished with it (<see cref="Completion"/>).</summary>
    internal const string ProbeHeader = "X-Completion-Probe";

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _completions = new(StringComparer.Ordinal);

    /// <summary>
    /// Completes when the host has finished with the request that carried <see cref="ProbeHeader"/> <paramref name="probe"/>: every middleware has returned or
    /// thrown, so whatever the Forum was going to log for it is logged. A signal from the host itself, never a delay.
    /// </summary>
    internal Task Completion(string probe) => Completed(probe).Task;

    private TaskCompletionSource Completed(string probe) =>
        _completions.GetOrAdd(probe, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>The outermost middleware of the host: it does nothing but say when a probed request is over.</summary>
    private sealed class CompletionFilter(FaultableForumFixture owner) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, inner) =>
                {
                    try
                    {
                        await inner();
                    }
                    finally
                    {
                        if (context.Request.Headers.TryGetValue(ProbeHeader, out var probe) && probe.ToString() is { Length: > 0 } id)
                            owner.Completed(id).TrySetResult();
                    }
                });
                next(app);
            };
    }

    private static ServiceDescriptor Registered<T>(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(T));
        return registered.ImplementationFactory is null
            ? throw new InvalidOperationException($"{typeof(T).Name} is registered by a factory; this decorator wraps what it builds")
            : registered;
    }

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ILoggerProvider>(Log);
        services.AddSingleton<IStartupFilter>(new CompletionFilter(this));

        var store = Registered<IEventStore>(services);
        services.Remove(store);
        services.AddSingleton<IEventStore>(sp => new FaultableEventStore((IEventStore)store.ImplementationFactory!(sp)));

        var nonces = Registered<IDpopNonceStore>(services);
        services.Remove(nonces);
        services.AddSingleton<IDpopNonceStore>(sp => new FaultableNonceStore((IDpopNonceStore)nonces.ImplementationFactory!(sp)));

        var replay = Registered<IReplayCache>(services);
        services.Remove(replay);
        services.AddSingleton<IReplayCache>(sp => new FaultableReplayCache((IReplayCache)replay.ImplementationFactory!(sp)));

        // One key store behind both of its ports and the log-bound rule that reads it, as Program.Build composes them.
        services.Remove(Registered<IAuthorKeyRegistry>(services));
        services.Remove(Registered<LogBoundKeys>(services));
        services.AddSingleton(sp => new FaultableKeyStore(sp.GetRequiredService<PostgresAgentKeyStore>(), sp.GetRequiredService<PostgresAgentKeyStore>()));
        services.AddSingleton<IAuthorKeyRegistry>(sp => sp.GetRequiredService<FaultableKeyStore>());
        services.AddSingleton(sp => new LogBoundKeys(
            sp.GetRequiredService<FaultableKeyStore>(), sp.GetRequiredService<FaultableKeyStore>(), sp.GetRequiredService<IEventReader>()));
    }
}

/// <summary>
/// What a request was answered with, or what the host threw out of it (an in-process client may see either): the status, the content type, the body and,
/// for a body that is a problem document, its members. <see cref="Says"/> is what a failing assertion prints.
/// </summary>
internal sealed record Reply(int Status, string? ContentType, string Body, string? Threw, IReadOnlyList<string>? HeaderNames = null)
{
    /// <summary>True where the answer carried a header of this name (response and content headers; a reply built without them carries none).</summary>
    internal bool HasHeader(string name) => HeaderNames?.Contains(name, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>The problem type, or the empty string.</summary>
    internal string Type => Member("type") ?? string.Empty;

    /// <summary>The OAuth code, or the empty string.</summary>
    internal string Error => Member("error") ?? string.Empty;

    /// <summary>True where the body is a JSON object with a string <c>type</c>, a string <c>title</c> and a null <c>detail</c> member, served as a problem document.</summary>
    internal bool IsProblemWithNullDetail
    {
        get
        {
            if (ContentType is null || !ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                using var doc = JsonDocument.Parse(Body);
                var root = doc.RootElement;
                return root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    /// <summary>The title, or the empty string.</summary>
    internal string Title => Member("title") ?? string.Empty;

    /// <summary>The words an assertion prints for a reply.</summary>
    internal string Says =>
        Threw is not null
            ? $"threw {Threw}"
            : $"{Status} {ContentType ?? "(no content type)"} {(Body.Length == 0 ? "\"\"" : (Body.Length > 160 ? Body[..160] + "..." : Body).ReplaceLineEndings(" "))}";

    private string? Member(string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends <paramref name="request"/> and never throws for the host's failure: a request the host threw out of is a reply that says what it threw.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An in-process host may throw an unhandled exception to its client where the deployed host answers 500; the reply records which.")]
    internal static async Task<Reply> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return new Reply(
                (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, body, null,
                response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key)).ToList());
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new Reply((int)HttpStatusCode.InternalServerError, null, string.Empty, $"{e.GetType().Name}: {e.Message}");
        }
    }
}

/// <summary>The same decorated Forum on a real socket (Kestrel, Development): for the facts that need a real client, an aborted request or a body on the wire.</summary>
public sealed class FaultableKestrelFixture : FaultableForumFixture
{
    public FaultableKestrelFixture() => UseKestrel(0);
}
