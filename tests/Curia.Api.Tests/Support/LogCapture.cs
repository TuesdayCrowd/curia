using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>One log event a host wrote: its id, its formatted message, and its structured values.</summary>
internal sealed record LoggedEvent(int EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values)
{
    /// <summary>The structured value named <paramref name="name"/>, as text, or null when the event has none.</summary>
    internal string? Value(string name) =>
        Values.Where(v => string.Equals(v.Key, name, StringComparison.Ordinal)).Select(v => v.Value?.ToString()).FirstOrDefault();
}

/// <summary>
/// Every log event a host writes, each with its event id, its formatted message, and the state's
/// structured values (lifted from <c>ServerFaultTests</c>, which kept only the formatted message).
/// A fact that reads what a refusal logged reads the <c>Type</c> value from the structured values, so
/// "no structured value holds the caller's text" checks a list that is not empty.
///
/// <para>A fact takes a <see cref="Mark"/> before its request and reads what was captured after it.
/// A capture belongs to the host it was given to (<see cref="CapturedForum"/>, or the fuzzer's
/// fixture), and that host's requests are sent one at a time, so what it holds after a mark is what
/// the request sent after the mark logged.</para>
/// </summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<LoggedEvent> _events = [];

    /// <summary>A position in the capture: what is logged after it was logged after the mark was taken.</summary>
    internal int Mark()
    {
        lock (_gate) return _events.Count;
    }

    /// <summary>Every event captured since <paramref name="mark"/>.</summary>
    internal IReadOnlyList<LoggedEvent> Since(int mark)
    {
        lock (_gate) return _events.GetRange(mark, _events.Count - mark);
    }

    /// <summary>Every event captured, in order.</summary>
    internal IReadOnlyList<LoggedEvent> All => Since(0);

    /// <summary>
    /// The one event of id <paramref name="eventId"/> captured since <paramref name="mark"/>. Exactly one,
    /// or the fact fails: a request that logged none, or a mutant that answered 500 where it refused,
    /// has no reason to compare.
    /// </summary>
    internal LoggedEvent Single(int mark, int eventId)
    {
        var events = Since(mark).Where(e => e.EventId == eventId).ToList();
        Assert.True(
            events.Count == 1,
            $"{events.Count} events {eventId} were captured for the request, not exactly one: " +
            string.Join(" | ", Since(mark).Where(e => e.EventId is 5000 or 5001).Select(e => $"{e.EventId} {e.Message}")));
        return events[0];
    }

    /// <summary>The <c>Type</c> the one event of id <paramref name="eventId"/> since <paramref name="mark"/> carries.</summary>
    internal string SingleType(int mark, int eventId) => Single(mark, eventId).Value("Type") ?? string.Empty;

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private void Add(LoggedEvent logged)
    {
        lock (_gate) _events.Add(logged);
    }

    private sealed class Logger(LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var values = state is IReadOnlyList<KeyValuePair<string, object?>> list ? [.. list] : (IReadOnlyList<KeyValuePair<string, object?>>)[];
            capture.Add(new LoggedEvent(eventId.Id, formatter(state, exception), values));
        }
    }
}

/// <summary>
/// The fixture's Forum, with a host of its own that logs into a <see cref="LogCapture"/>: the same
/// database, clock and issuer key, so a fact sends the requests whose reasons it reads through
/// <see cref="Client"/> and does everything else through the fixture.
/// </summary>
internal sealed class CapturedForum : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _host;

    internal CapturedForum(ForumFixture forum)
    {
        ArgumentNullException.ThrowIfNull(forum);
        _host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(Log)));
        Client = _host.CreateClient();
    }

    internal LogCapture Log { get; } = new();

    internal HttpClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.DisposeAsync();
        Log.Dispose();
    }
}
