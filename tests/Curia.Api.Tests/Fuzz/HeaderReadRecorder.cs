using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// Hardin's item 9: every header and every query parameter the host reads across the whole closed
/// pass -- an error path's reads included -- recorded by name and by the route the host matched
/// (<see cref="RouteEndpoint"/>'s raw pattern at the moment of the read, <see cref="NoEndpoint"/> when
/// none matched). An <see cref="IStartupFilter"/> puts a recording <see cref="IHeaderDictionary"/> in
/// <see cref="IHttpRequestFeature.Headers"/>, and a recording <see cref="IQueryCollection"/> behind
/// <see cref="IQueryFeature"/>, before any other middleware runs. The closed pass checks header
/// coverage by name, and query coverage by route, both ways.
/// </summary>
internal sealed class HeaderReadRecorder : IStartupFilter
{
    /// <summary>The route of a read that no endpoint matched.</summary>
    internal const string NoEndpoint = "(no endpoint)";

    private readonly ConcurrentDictionary<string, byte> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Route, string Name), byte> _headerReads = new(RouteAndHeaderName.Instance);
    private readonly ConcurrentDictionary<(string Route, string Name), byte> _queryReads = new();

    internal IReadOnlyCollection<string> Headers => [.. _headers.Keys];

    /// <summary>Every header read, by the route the host matched and the header's name (compared ignoring case).</summary>
    internal IReadOnlyCollection<(string Route, string Name)> HeaderReads => [.. _headerReads.Keys];

    /// <summary>Every query parameter read, by the route the host matched and the parameter's name.</summary>
    internal IReadOnlyCollection<(string Route, string Name)> QueryReads => [.. _queryReads.Keys];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            var request = context.Features.Get<IHttpRequestFeature>()!;
            request.Headers = new RecordingHeaders(context, request.Headers, _headers, _headerReads);
            var query = context.Request.Query;
            context.Features.Set<IQueryFeature>(new RecordingQueryFeature(new RecordingQuery(context, query, _queryReads)));
            await following(context);
        });
        next(app);
    };

    /// <summary>The route the host matched for this request, read when the read happens: the endpoint is set only after routing.</summary>
    private static string RouteOf(HttpContext c) =>
        c.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } raw } ? c.Request.Method + " " + raw : NoEndpoint;

    /// <summary>Route ordinal, header name ignoring case.</summary>
    private sealed class RouteAndHeaderName : IEqualityComparer<(string Route, string Name)>
    {
        internal static readonly RouteAndHeaderName Instance = new();

        public bool Equals((string Route, string Name) x, (string Route, string Name) y) =>
            string.Equals(x.Route, y.Route, StringComparison.Ordinal) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Route, string Name) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Route), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    private sealed class RecordingQueryFeature(IQueryCollection query) : IQueryFeature
    {
        public IQueryCollection Query { get; set; } = query;
    }

    private sealed class RecordingQuery(HttpContext context, IQueryCollection inner, ConcurrentDictionary<(string Route, string Name), byte> reads) : IQueryCollection
    {
        public StringValues this[string key]
        {
            get
            {
                reads.TryAdd((RouteOf(context), key), 0);
                return inner[key];
            }
        }

        public int Count => inner.Count;

        public ICollection<string> Keys => inner.Keys;

        public bool ContainsKey(string key)
        {
            reads.TryAdd((RouteOf(context), key), 0);
            return inner.ContainsKey(key);
        }

        public bool TryGetValue(string key, out StringValues value)
        {
            reads.TryAdd((RouteOf(context), key), 0);
            return inner.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "A recording view of the request's headers, named for what it records.")]
    private sealed class RecordingHeaders(
        HttpContext context,
        IHeaderDictionary inner,
        ConcurrentDictionary<string, byte> reads,
        ConcurrentDictionary<(string Route, string Name), byte> routeReads) : IHeaderDictionary
    {
        private void Record(string key)
        {
            reads.TryAdd(key, 0);
            routeReads.TryAdd((RouteOf(context), key), 0);
        }

        public StringValues this[string key]
        {
            get
            {
                Record(key);
                return inner[key];
            }

            set => inner[key] = value;
        }

        public long? ContentLength
        {
            get
            {
                Record("Content-Length");
                return inner.ContentLength;
            }

            set => inner.ContentLength = value;
        }

        public ICollection<string> Keys => inner.Keys;

        public ICollection<StringValues> Values => inner.Values;

        public int Count => inner.Count;

        public bool IsReadOnly => inner.IsReadOnly;

        StringValues IDictionary<string, StringValues>.this[string key]
        {
            get => this[key];
            set => this[key] = value;
        }

        [SuppressMessage("Usage", "ASP0019:Use IHeaderDictionary.Append or the indexer", Justification = "A pass-through: the wrapper adds exactly as the dictionary it wraps would.")]
        public void Add(string key, StringValues value) => inner.Add(key, value);

        public void Add(KeyValuePair<string, StringValues> item) => inner.Add(item);

        public void Clear() => inner.Clear();

        public bool Contains(KeyValuePair<string, StringValues> item)
        {
            Record(item.Key);
            return inner.Contains(item);
        }

        public bool ContainsKey(string key)
        {
            Record(key);
            return inner.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => inner.GetEnumerator();

        public bool Remove(string key) => inner.Remove(key);

        public bool Remove(KeyValuePair<string, StringValues> item) => inner.Remove(item);

        public bool TryGetValue(string key, out StringValues value)
        {
            Record(key);
            return inner.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
