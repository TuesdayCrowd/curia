using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Curia.Domain.Content;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// What the request surface is, derived from the host where it can be and listed by hand only where
/// it cannot: the parameters two handlers read off the request rather than binding, the ones a route
/// reads only to refuse, and the headers
/// a transport computes. Shared by the hand sweep (<c>RequestSurfaceTests</c>) and the fuzzer.
/// </summary>
internal static class SurfaceInventory
{
    /// <summary>
    /// Every query parameter a Forum handler reads: those it binds (checked against the handlers by
    /// <c>EveryQueryParameterAHandlerBindsIsProbed</c>) and those search and the batch read from
    /// the request itself. Moved from <c>RequestSurfaceTests</c>, not changed.
    /// </summary>
    internal static readonly string[] QueryParameters =
    [
        "q", "board", "author", "kind", "tags", "cursor", "limit", "why", "min_verification", "marking",
        "agent", "tree_size", "from", "to",
    ];

    /// <summary>
    /// The query parameters a handler reads off the request rather than binding, by route: the hand
    /// list, which no reflection over a handler's parameters can find. A route's exemplar must give
    /// each a value, and the closed pass must see each read at least once, or it is stale.
    /// </summary>
    internal static readonly (string Method, string Pattern, string Name)[] RequestReadQuery =
    [
        ("GET", "/v1/search", "q"),
        ("GET", "/v1/search", "board"),
        ("GET", "/v1/search", "author"),
        ("GET", "/v1/search", "kind"),
        ("GET", "/v1/search", "tags"),
        ("GET", "/v1/search", "cursor"),
        ("GET", "/v1/search", "limit"),
        ("GET", "/v1/search", "why"),
        ("GET", "/v1/search", "min_verification"),
        ("GET", "/v1/search", "marking"),
        ("GET", "/v1/inbox", "board"),
        ("GET", "/v1/inbox", "tags"),
        ("GET", "/v1/inbox", "cursor"),
        ("GET", "/v1/inbox", "limit"),
        ("GET", "/v1/inbox", "marking"),
        ("GET", "/v1/posts/{postId}", "marking"),
        ("POST", "/v1/posts/batch", "marking"),
        ("GET", "/v1/threads/{rootPostId}", "marking"),
        ("GET", "/v1/boards/{board}/posts", "marking"),
    ];

    /// <summary>
    /// The query parameters a route reads only to refuse them, each with the problem type it answers:
    /// R9.26's refusable list as errata G12 item 9 publishes it. A presence is refused before any value
    /// is read, so there is nothing to vary. The closed pass holds this list to the host's reads both
    /// ways, and <c>R14_10_EveryRefusedQueryParameterIsRefusedByItsRoute</c> holds it to the answer, so
    /// a filter the route honours cannot be listed here to escape the fuzzer.
    /// </summary>
    internal static readonly (string Method, string Pattern, string Name, string ProblemType)[] RefusedQuery =
    [
        ("GET", "/v1/search", "verification", "curia/search/unsupported-filter"),
        ("GET", "/v1/search", "environment_version", "curia/search/unsupported-filter"),
    ];

    /// <summary>
    /// The headers no exemplar varies, each with its reason: framing the client computes, which
    /// Kestrel's parser reads before any handler does. <c>Transfer-Encoding</c> and <c>Connection</c>
    /// are not listed: nothing reads either through the request's headers on the test host, so each
    /// was stale on the first pass. Kestrel's parser reads its own copy, before the recorder.
    /// </summary>
    internal static readonly (string Name, string Reason)[] TransportHeaders =
    [
        ("Host", "framing, computed by the client; Kestrel's parser"),
        ("Content-Length", "framing, computed by the client; Kestrel's parser"),
    ];

    /// <summary>Every route the host registers: its method, its pattern and its route parameters. Moved from <c>RequestSurfaceTests</c>.</summary>
    internal static List<(string Method, string Pattern, string[] Parameters)> Routes(ForumFixture forum) =>
        [.. forum.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (m, e.RoutePattern.RawText ?? "", e.RoutePattern.Parameters.Select(p => p.Name).ToArray())))];

    /// <summary>
    /// The query parameters each route's handler binds, as <c>EveryQueryParameterAHandlerBindsIsProbed</c>
    /// finds them: a simple-typed parameter that is not a route parameter, by its query name.
    /// </summary>
    internal static List<(string Method, string Pattern, string Name)> BoundQuery(ForumFixture forum)
    {
        var bound = new List<(string, string, string)>();
        foreach (var endpoint in forum.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler) continue;
            var routeNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                foreach (var parameter in handler.GetParameters())
                {
                    var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                    if (type != typeof(string) && type != typeof(long) && type != typeof(int) && type != typeof(bool)) continue;
                    if (routeNames.Contains(parameter.Name!)) continue;
                    bound.Add((method, endpoint.RoutePattern.RawText ?? "", parameter.GetCustomAttribute<FromQueryAttribute>()?.Name ?? parameter.Name!));
                }
            }
        }

        return bound;
    }

    /// <summary>
    /// Each body DTO's members, by route: the <c>JsonPropertyName</c>s of every handler parameter whose
    /// type declares any.
    /// </summary>
    internal static List<(string Method, string Pattern, string Name)> BodyMembers(ForumFixture forum)
    {
        var members = new List<(string, string, string)>();
        foreach (var endpoint in forum.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler) continue;
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                foreach (var parameter in handler.GetParameters())
                {
                    foreach (var property in parameter.ParameterType.GetProperties())
                    {
                        if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is { } name)
                            members.Add((method, endpoint.RoutePattern.RawText ?? "", name.Name));
                    }
                }
            }
        }

        return members;
    }

    /// <summary>
    /// The envelope parser's known members: <see cref="PostEnvelope"/>'s fields, each the snake_case
    /// wire name the parser reads it from, and, for an array of domain records, each element member
    /// as <c>&lt;member&gt;/*/&lt;field&gt;</c>.
    /// </summary>
    internal static List<string> EnvelopeMembers()
    {
        var members = new List<string>();
        foreach (var p in typeof(PostEnvelope).GetConstructors().Single().GetParameters())
        {
            var name = SnakeCase(p.Name!);
            members.Add(name);
            if (p.ParameterType.IsGenericType
                && p.ParameterType.GetGenericTypeDefinition() == typeof(ImmutableArray<>)
                && p.ParameterType.GetGenericArguments()[0] is { IsClass: true } element
                && element.Namespace is { } ns
                && ns.StartsWith("Curia.Domain", StringComparison.Ordinal))
            {
                foreach (var q in element.GetConstructors().Single(c => c.IsPublic).GetParameters())
                    members.Add($"{name}/*/{SnakeCase(q.Name!)}");
            }
        }

        return members;
    }

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                if (builder.Length > 0) builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
