using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// Hardin's item 9: every header and every query parameter the host reads, by name, across the whole
/// closed pass -- an error path's reads included. An <see cref="IStartupFilter"/> puts a recording
/// <see cref="IHeaderDictionary"/> in <see cref="IHttpRequestFeature.Headers"/>, and a recording
/// <see cref="IQueryCollection"/> behind <see cref="IQueryFeature"/>, before any other middleware runs.
/// </summary>
internal sealed class HeaderReadRecorder : IStartupFilter
{
    private readonly ConcurrentDictionary<string, byte> _headers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _query = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<string> Headers => [.. _headers.Keys];

    internal IReadOnlyCollection<string> QueryNames => [.. _query.Keys];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            var request = context.Features.Get<IHttpRequestFeature>()!;
            request.Headers = new RecordingHeaders(request.Headers, _headers);
            var query = context.Request.Query;
            context.Features.Set<IQueryFeature>(new RecordingQueryFeature(new RecordingQuery(query, _query)));
            await following(context);
        });
        next(app);
    };

    private sealed class RecordingQueryFeature(IQueryCollection query) : IQueryFeature
    {
        public IQueryCollection Query { get; set; } = query;
    }

    private sealed class RecordingQuery(IQueryCollection inner, ConcurrentDictionary<string, byte> reads) : IQueryCollection
    {
        public StringValues this[string key]
        {
            get
            {
                reads.TryAdd(key, 0);
                return inner[key];
            }
        }

        public int Count => inner.Count;

        public ICollection<string> Keys => inner.Keys;

        public bool ContainsKey(string key)
        {
            reads.TryAdd(key, 0);
            return inner.ContainsKey(key);
        }

        public bool TryGetValue(string key, out StringValues value)
        {
            reads.TryAdd(key, 0);
            return inner.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "A recording view of the request's headers, named for what it records.")]
    private sealed class RecordingHeaders(IHeaderDictionary inner, ConcurrentDictionary<string, byte> reads) : IHeaderDictionary
    {
        public StringValues this[string key]
        {
            get
            {
                reads.TryAdd(key, 0);
                return inner[key];
            }

            set => inner[key] = value;
        }

        public long? ContentLength
        {
            get
            {
                reads.TryAdd("Content-Length", 0);
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
            reads.TryAdd(item.Key, 0);
            return inner.Contains(item);
        }

        public bool ContainsKey(string key)
        {
            reads.TryAdd(key, 0);
            return inner.ContainsKey(key);
        }

        public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);

        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => inner.GetEnumerator();

        public bool Remove(string key) => inner.Remove(key);

        public bool Remove(KeyValuePair<string, StringValues> item) => inner.Remove(item);

        public bool TryGetValue(string key, out StringValues value)
        {
            reads.TryAdd(key, 0);
            return inner.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
