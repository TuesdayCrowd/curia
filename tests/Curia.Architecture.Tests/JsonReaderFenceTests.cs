using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.34 (errata G18): <i>a caller's JSON is read only through a guarded reader</i>, held as an IL
/// call-site scan rather than as review (spec section 4.9).
///
/// <para><b>What is scanned.</b> Every <c>call</c>, <c>callvirt</c> and <c>newobj</c> in every assembly
/// built from <c>src/</c> except <c>Curia.Client</c>, <c>curia</c> (Client.Cli) and <c>curia-mcp</c>,
/// read by <see cref="IlCallSites.CallSites"/>. Those three read the Forum's served bytes, not a
/// caller's; leaving them out is a decision, and this fact states it.</para>
///
/// <para><b>What a row is.</b> A call to one of <see cref="BannedCallees"/> needs a row in
/// <see cref="Rows"/>, keyed by (type, calling method, callee), so a new call in a listed type is a new
/// row for review. A call site belongs to its outermost declaring type; a compiler-generated nested
/// type maps to the type that wrote it, and its method to the method the source wrote. A row naming
/// no call site fails the fact (stale).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class JsonReaderFenceTests
{
    /// <summary>Spec section 4.9's banned callees, the extended ones included.</summary>
    private static readonly ImmutableHashSet<(string Type, string Member)> BannedCallees =
    [
        ("System.Text.Json.JsonDocument", "Parse"),
        ("System.Text.Json.JsonDocument", "ParseValue"),
        ("System.Text.Json.JsonDocument", "ParseAsync"),
        ("System.Text.Json.Nodes.JsonNode", "Parse"),
        ("System.Text.Json.Nodes.JsonNode", "ParseAsync"),
        ("System.Text.Json.JsonElement", "GetString"),
        ("System.Text.Json.JsonSerializer", "Deserialize"),
        ("System.Text.Json.JsonSerializer", "DeserializeAsync"),
        ("System.Text.Json.JsonSerializer", "DeserializeAsyncEnumerable"),
        ("System.Text.Json.Utf8JsonReader", "GetString"),
        ("Microsoft.AspNetCore.Http.HttpRequestJsonExtensions", "ReadFromJsonAsync"),
        ("System.Net.Http.Json.HttpContentJsonExtensions", "ReadFromJsonAsync"),
    ];

    /// <summary>The assemblies that read the Forum's served bytes, not a caller's (spec section 4.9's scope).</summary>
    private static readonly string[] OutOfScope = ["Curia.Client.dll", "curia.dll", "curia-mcp.dll"];

    /// <summary>One row per (type, calling method, callee), each with its reason (spec section 4.9).</summary>
    private static readonly ImmutableArray<(string Type, string Method, string Callee, string Reason)> Rows =
    [
        ("Curia.AuthN.Jwt.CompactJws", "ParseJsonObject", "System.Text.Json.JsonDocument::Parse",
            "the guarded reader: a token segment's JSON is parsed here and refused before any string is read"),
        ("Curia.AuthN.Jwt.CompactJws", "EveryStringDecodes", "System.Text.Json.Utf8JsonReader::GetString",
            "the guarded reader: decodes every string once, so an unpaired surrogate refuses the segment before any reader asks for it"),
        ("Curia.AuthN.Jwt.CompactJws", "ReadString", "System.Text.Json.JsonElement::GetString",
            "the guarded reader: reads a member of a segment CompactJws has already parsed and decoded"),
        ("Curia.Canon.Jws.DetachedJws", "ParseHeader", "System.Text.Json.JsonDocument::Parse",
            "the guarded reader: a post signature's protected header is parsed here and refused before any string is read"),
        ("Curia.Canon.Jws.DetachedJws", "AllStringsDecode", "System.Text.Json.Utf8JsonReader::GetString",
            "the guarded reader: decodes every string once, so an unpaired surrogate refuses the header before any reader asks for it"),
        ("Curia.Canon.Jws.DetachedJws", "ReadString", "System.Text.Json.JsonElement::GetString",
            "the guarded reader: reads a member of a header DetachedJws has already parsed and decoded"),
        ("Curia.Canon.Jws.DetachedJws", "ReadCrit", "System.Text.Json.JsonElement::GetString",
            "the guarded reader: reads crit's entries from a header DetachedJws has already parsed and decoded"),
        ("Curia.Canon.Json.JsonReader", "ReadStringValue", "System.Text.Json.Utf8JsonReader::GetString",
            "ADMIT's reader, which refuses before read"),
        ("Curia.AuthN.AccessTokenClaims", "ReadAudience", "System.Text.Json.JsonElement::GetString",
            "reads after CompactJws's guarded parse"),
        ("Curia.AuthN.Dpop.DpopProofClaims", "Parse", "System.Text.Json.JsonElement::GetString",
            "reads after CompactJws's guarded parse"),
        ("Curia.Api.ActaEndpoints", "PublicKeyOf", "System.Text.Json.JsonDocument::Parse",
            "parses the Forum's own log bytes: a log key's JWK, canonicalized from the Acta's own key set"),
    ];

    /// <summary>The body types the host's binder deserializes, each with its reason (spec section 4.9).</summary>
    private static readonly ImmutableArray<(string Type, string Reason)> BoundBodies =
    [
        ("Curia.Api.EnrollRequest", BinderReason),
        ("Curia.Api.FlagRequest", BinderReason),
        ("Curia.Api.BatchRequest", BinderReason),
    ];

    private const string BinderReason =
        "deserialized by the host's binder into a declared type; an undecodable string refuses the whole body";

    [Fact]
    public void R11_34_ACallersJsonIsReadOnlyThroughAGuardedReader()
    {
        var shipped = Shipped.ShippedAssemblies();
        Assert.True(shipped.Count >= 12, $"only {shipped.Count} projects were found under src/; the scan is looking in the wrong place");
        var scanned = shipped.Where(p => !OutOfScope.Contains(Path.GetFileName(p), StringComparer.Ordinal)).ToList();
        Assert.Equal(shipped.Count - OutOfScope.Length, scanned.Count);

        var sites = scanned.ToDictionary(p => Path.GetFileName(p), IlCallSites.CallSites, StringComparer.Ordinal);

        // Non-vacuity: a decoder that read nothing would find no banned call and pass.
        Assert.True(sites["Curia.AuthN.dll"].Length > 0, "the scan read no call site in Curia.AuthN; the IL decoder is reading nothing");

        var banned = sites.Values
            .SelectMany(s => s)
            .Where(s => BannedCallees.Contains((s.CalleeType, s.CalleeMember)))
            .Select(s => (Type: s.CallerType, Method: s.CallerMethod, Callee: s.CalleeType + "::" + s.CalleeMember))
            .Distinct()
            .ToList();

        var failures = new List<string>();
        foreach (var row in Rows.Where(r => string.IsNullOrWhiteSpace(r.Reason)))
            failures.Add($"row {row.Type}.{row.Method} -> {row.Callee} has an empty reason");
        foreach (var site in banned.Where(s => !Rows.Any(r => r.Type == s.Type && r.Method == s.Method && r.Callee == s.Callee)))
            failures.Add($"{site.Type}.{site.Method} calls {site.Callee} with no row");
        foreach (var row in Rows.Where(r => !banned.Contains((r.Type, r.Method, r.Callee))))
            failures.Add($"row {row.Type}.{row.Method} -> {row.Callee} names no call site (stale)");

        Assert.True(
            failures.Count == 0,
            "R11.34: a caller's JSON is read other than through a guarded reader:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Spec section 4.9: a body the host's binder deserializes has no call site in a <c>src/</c>
    /// assembly, so the call-site scan cannot see it. The <see cref="IAcceptsMetadata.RequestType"/>
    /// of every endpoint in the host's <see cref="EndpointDataSource"/> must equal
    /// <see cref="BoundBodies"/>. The host is the real composition root, built and never run, so no
    /// service it registers is resolved and no database is reached.
    /// </summary>
    [Fact]
    public async Task R11_34_EveryBodyAHostBindsIsAListedType()
    {
        await using var app = Curia.Api.Program.Build(WebApplication.CreateBuilder());
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
        Assert.True(endpoints.Count >= 20, $"the host's endpoint data source yielded only {endpoints.Count} endpoints");

        var bound = endpoints
            .Select(e => e.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType)
            .OfType<Type>()
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        var failures = new List<string>();
        foreach (var row in BoundBodies.Where(r => string.IsNullOrWhiteSpace(r.Reason)))
            failures.Add($"row {row.Type} has an empty reason");
        foreach (var type in bound.Where(t => !BoundBodies.Any(r => r.Type == t)))
            failures.Add($"{type} is deserialized by the host's binder with no row");
        foreach (var row in BoundBodies.Where(r => !bound.Contains(r.Type)))
            failures.Add($"row {row.Type} names no body the host binds (stale)");

        Assert.True(
            failures.Count == 0,
            "R11.34: the host's binder reads a body that is not a listed type:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Order(StringComparer.Ordinal)));
    }
}
