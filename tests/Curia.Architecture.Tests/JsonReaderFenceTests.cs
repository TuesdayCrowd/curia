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
/// <para><b>What is scanned.</b> Every <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and
/// <c>ldvirtftn</c> in every assembly built from <c>src/</c> except <c>Curia.Client</c>, <c>curia</c>
/// (Client.Cli) and <c>curia-mcp</c>, read by <see cref="IlCallSites.CallSites"/>, so a banned reader
/// taken as a method group (a delegate over JsonDocument.Parse) is a call site like any other. Those three read the Forum's served bytes, not a
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
    /// <summary>
    /// Spec section 4.9's banned callees: every public method of JsonElement, JsonProperty, JsonDocument
    /// and Utf8JsonReader that decodes a string and throws on an unpaired surrogate, the parse entries,
    /// and the four node entries (the three node factories and JsonNode::ReplaceWith).
    /// </summary>
    private static readonly ImmutableHashSet<(string Type, string Member)> BannedCallees =
    [
        ("System.Text.Json.JsonDocument", "Parse"),
        ("System.Text.Json.JsonDocument", "ParseValue"),
        ("System.Text.Json.JsonDocument", "TryParseValue"),
        ("System.Text.Json.JsonElement", "Parse"),
        ("System.Text.Json.JsonElement", "ParseValue"),
        ("System.Text.Json.JsonElement", "TryParseValue"),
        ("System.Text.Json.JsonDocument", "ParseAsync"),
        ("System.Text.Json.Nodes.JsonNode", "Parse"),
        ("System.Text.Json.Nodes.JsonNode", "ParseAsync"),
        ("System.Text.Json.JsonElement", "GetString"),
        ("System.Text.Json.JsonElement", "ToString"),
        ("System.Text.Json.JsonProperty", "get_Name"),
        ("System.Text.Json.JsonSerializer", "Deserialize"),
        ("System.Text.Json.JsonSerializer", "DeserializeAsync"),
        ("System.Text.Json.JsonSerializer", "DeserializeAsyncEnumerable"),
        ("System.Text.Json.Utf8JsonReader", "GetString"),
        ("System.Text.Json.Utf8JsonReader", "CopyString"),
        ("System.Text.Json.JsonElement", "ValueEquals"),
        ("System.Text.Json.JsonElement", "GetGuid"),
        ("System.Text.Json.JsonElement", "TryGetGuid"),
        ("System.Text.Json.JsonElement", "GetBytesFromBase64"),
        ("System.Text.Json.JsonElement", "TryGetBytesFromBase64"),
        ("System.Text.Json.JsonElement", "DeepEquals"),
        ("System.Text.Json.JsonElement", "WriteTo"),
        ("System.Text.Json.JsonDocument", "WriteTo"),
        ("System.Text.Json.JsonProperty", "NameEquals"),
        ("System.Text.Json.JsonProperty", "WriteTo"),
        ("System.Text.Json.Utf8JsonReader", "ValueTextEquals"),
        ("System.Text.Json.Utf8JsonReader", "GetGuid"),
        ("System.Text.Json.Utf8JsonReader", "TryGetGuid"),
        ("System.Text.Json.Utf8JsonReader", "GetBytesFromBase64"),
        ("System.Text.Json.Utf8JsonReader", "TryGetBytesFromBase64"),
        ("System.Text.Json.Nodes.JsonValue", "Create"),
        ("System.Text.Json.Nodes.JsonObject", "Create"),
        ("System.Text.Json.Nodes.JsonArray", "Create"),
        ("System.Text.Json.Nodes.JsonNode", "ReplaceWith"),
        ("Microsoft.AspNetCore.Http.HttpRequestJsonExtensions", "ReadFromJsonAsync"),
        ("System.Net.Http.Json.HttpContentJsonExtensions", "ReadFromJsonAsync"),
    ];

    /// <summary>
    /// Every public method of the four reading types that <see cref="BannedCallees"/> leaves out, each
    /// with the reason it decodes no string (spec section 4.9). Held by
    /// <see cref="R11_34_EveryPublicMethodOfAJsonReadingTypeIsBannedOrClassified"/>.
    /// </summary>
    private static readonly ImmutableArray<(string Type, string Member, string Reason)> NotDecoding =
    [
        ("System.Text.Json.JsonElement", "Clone", Structure),
        ("System.Text.Json.JsonElement", "EnumerateArray", Structure),
        ("System.Text.Json.JsonElement", "EnumerateObject", Structure),
        ("System.Text.Json.JsonElement", "GetArrayLength", Structure),
        ("System.Text.Json.JsonElement", "GetProperty", Structure),
        ("System.Text.Json.JsonElement", "GetPropertyCount", Structure),
        ("System.Text.Json.JsonElement", "GetRawText", Structure),
        ("System.Text.Json.JsonElement", "TryGetProperty", Structure),
        ("System.Text.Json.JsonElement", "get_Item", Structure),
        ("System.Text.Json.JsonElement", "get_ValueKind", Structure),
        ("System.Text.Json.JsonElement", "GetBoolean", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetByte", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetDecimal", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetDouble", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetInt16", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetInt32", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetInt64", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetSByte", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetSingle", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetUInt16", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetUInt32", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetUInt64", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetByte", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetDecimal", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetDouble", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetInt16", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetInt32", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetInt64", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetSByte", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetSingle", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetUInt16", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetUInt32", NumberOrBool),
        ("System.Text.Json.JsonElement", "TryGetUInt64", NumberOrBool),
        ("System.Text.Json.JsonElement", "GetDateTime", Date),
        ("System.Text.Json.JsonElement", "GetDateTimeOffset", Date),
        ("System.Text.Json.JsonElement", "TryGetDateTime", Date),
        ("System.Text.Json.JsonElement", "TryGetDateTimeOffset", Date),
        ("System.Text.Json.JsonProperty", "ToString", Structure),
        ("System.Text.Json.JsonProperty", "get_Value", Structure),
        ("System.Text.Json.JsonDocument", "Dispose", Structure),
        ("System.Text.Json.JsonDocument", "get_RootElement", Structure),
        ("System.Text.Json.Utf8JsonReader", "Read", Structure),
        ("System.Text.Json.Utf8JsonReader", "Skip", Structure),
        ("System.Text.Json.Utf8JsonReader", "TrySkip", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_BytesConsumed", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_CurrentDepth", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_CurrentState", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_HasValueSequence", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_IsFinalBlock", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_Position", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_TokenStartIndex", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_TokenType", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_ValueIsEscaped", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_ValueSequence", Structure),
        ("System.Text.Json.Utf8JsonReader", "get_ValueSpan", Structure),
        ("System.Text.Json.Utf8JsonReader", "GetComment", Comment),
        ("System.Text.Json.Utf8JsonReader", "GetBoolean", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetByte", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetDecimal", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetDouble", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetInt16", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetInt32", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetInt64", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetSByte", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetSingle", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetUInt16", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetUInt32", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetUInt64", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetByte", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetDecimal", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetDouble", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetInt16", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetInt32", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetInt64", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetSByte", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetSingle", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetUInt16", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetUInt32", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "TryGetUInt64", NumberOrBool),
        ("System.Text.Json.Utf8JsonReader", "GetDateTime", Date),
        ("System.Text.Json.Utf8JsonReader", "GetDateTimeOffset", Date),
        ("System.Text.Json.Utf8JsonReader", "TryGetDateTime", Date),
        ("System.Text.Json.Utf8JsonReader", "TryGetDateTimeOffset", Date),
    ];

    private const string Structure = "returns structure or raw bytes; decodes no string";

    private const string NumberOrBool = "reads a number or a bool; decodes no string";

    private const string Date =
        "parses a date from a string and refuses one with an unpaired surrogate as not a date (Try forms return false, Get forms throw FormatException as on any non-date: a wrong-format fault, outside R11.34's class)";

    private const string Comment =
        "a comment's text carries no escape sequence, and every in-scope reader leaves comment handling at Disallow";

    /// <summary>The types whose every public method <see cref="NotDecoding"/> and <see cref="BannedCallees"/> partition.</summary>
    private static readonly Type[] ReadingTypes =
    [
        typeof(System.Text.Json.JsonElement),
        typeof(System.Text.Json.JsonProperty),
        typeof(System.Text.Json.JsonDocument),
        typeof(System.Text.Json.Utf8JsonReader),
    ];

    /// <summary>The assemblies that declare <see cref="BannedCallees"/>' types, where each entry must resolve.</summary>
    private static readonly System.Reflection.Assembly[] BannedCalleeAssemblies =
    [
        typeof(System.Text.Json.JsonDocument).Assembly,
        typeof(Microsoft.AspNetCore.Http.HttpRequestJsonExtensions).Assembly,
        typeof(System.Net.Http.Json.HttpContentJsonExtensions).Assembly,
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
        ("Curia.Api.ActaEndpoints", "ToNode", "System.Text.Json.Nodes.JsonValue::Create",
            "builds a node from the Forum's own Canon value (a string, number or bool), never from a JsonElement"),
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
        // A spelling guard: every entry must name a public method its type declares (DeclaredOnly, or
        // Object.ToString would pass every ToString entry). It does not show that a call can reach the
        // method; JsonElement::ToString is reached only through the constrained mapping, held by
        // R11_34_AConstrainedCallIsReportedAsTheConstrainedTypesMethod.
        var unresolved = BannedCallees
            .Where(b => BannedCalleeAssemblies
                .Select(a => a.GetType(b.Type))
                .OfType<Type>()
                .FirstOrDefault() is not { } type
                || !type.GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                    .Any(m => m.Name == b.Member))
            .Select(b => $"banned callee {b.Type}::{b.Member} names no method; the entry bans nothing")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(unresolved.Count == 0, string.Join(Environment.NewLine, unresolved));

        var shipped = Shipped.ShippedAssemblies();
        Assert.True(shipped.Count >= 12, $"only {shipped.Count} projects were found under src/; the scan is looking in the wrong place");
        var scanned = shipped.Where(p => !OutOfScope.Contains(Path.GetFileName(p), StringComparer.Ordinal)).ToList();
        Assert.Equal(shipped.Count - OutOfScope.Length, scanned.Count);

        var sites = scanned.ToDictionary(p => Path.GetFileName(p), IlCallSites.CallSites, StringComparer.Ordinal);

        // Non-vacuity: a decoder that read nothing would find no banned call and pass.
        Assert.True(sites["Curia.AuthN.dll"].Length > 0, "the scan read no call site in Curia.AuthN; the IL decoder is reading nothing");
        Assert.True(
            sites["Curia.AuthN.dll"].Contains(("Curia.AuthN.AccessTokenValidator", "ValidateRequestAsync", "Curia.AuthN.AccessTokenClaims", "Parse")),
            "the scan did not see AccessTokenValidator's method group over AccessTokenClaims.Parse (AccessTokenValidator.cs, ParsePayload); the IL decoder is not reading ldftn, so a delegate over a banned reader would pass unseen");

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
    /// Spec section 4.9: every public method of JsonElement, JsonProperty, JsonDocument and
    /// Utf8JsonReader is either banned or classified as not decoding a string, with a reason, so a BCL
    /// release that adds an accessor is a red build, not a silent gap.
    /// </summary>
    [Fact]
    public void R11_34_EveryPublicMethodOfAJsonReadingTypeIsBannedOrClassified()
    {
        var failures = new List<string>();
        foreach (var type in ReadingTypes)
        {
            var name = type.FullName!;
            var declared = type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            Assert.True(declared.Count > 0, $"{name} yielded no public method; the partition would hold vacuously");

            foreach (var member in declared.Where(m =>
                         !BannedCallees.Contains((name, m)) && !NotDecoding.Any(n => n.Type == name && n.Member == m)))
                failures.Add($"{name}::{member} is neither banned nor classified; decide whether it decodes a string (R11.34, spec section 4.9)");
            foreach (var entry in NotDecoding.Where(n => n.Type == name && !declared.Contains(n.Member, StringComparer.Ordinal)))
                failures.Add($"not-decoding entry {entry.Type}::{entry.Member} names no method (stale)");
        }

        foreach (var entry in NotDecoding.Where(n => !ReadingTypes.Any(t => t.FullName == n.Type)))
            failures.Add($"not-decoding entry {entry.Type}::{entry.Member} names no reading type (stale)");
        foreach (var entry in NotDecoding.Where(n => BannedCallees.Contains((n.Type, n.Member))))
            failures.Add($"{entry.Type}::{entry.Member} is both banned and classified as not decoding");
        foreach (var entry in NotDecoding.Where(n => string.IsNullOrWhiteSpace(n.Reason)))
            failures.Add($"not-decoding entry {entry.Type}::{entry.Member} has an empty reason");
        foreach (var dup in NotDecoding.GroupBy(n => (n.Type, n.Member)).Where(g => g.Count() > 1))
            failures.Add($"not-decoding entry {dup.Key.Type}::{dup.Key.Member} is listed twice");

        Assert.True(
            failures.Count == 0,
            "R11.34: a public method of a JSON reading type is unclassified:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Order(StringComparer.Ordinal)));
    }

    /// <summary>The constrained-call sentinel; never called.</summary>
    internal static string ConstrainedProbe(System.Text.Json.JsonElement e) => e.ToString();

    /// <summary>
    /// Spec section 4.9: <c>e.ToString()</c> on a <see cref="System.Text.Json.JsonElement"/> compiles to
    /// <c>constrained. JsonElement callvirt System.Object::ToString</c>, so the scan must report it as
    /// <c>JsonElement::ToString</c>, or that ban matches nothing.
    /// </summary>
    [Fact]
    public void R11_34_AConstrainedCallIsReportedAsTheConstrainedTypesMethod()
    {
        var sites = IlCallSites.CallSites(typeof(JsonReaderFenceTests).Assembly.Location);
        Assert.True(
            sites.Contains(("Curia.Architecture.Tests.JsonReaderFenceTests", "ConstrainedProbe", "System.Text.Json.JsonElement", "ToString")),
            "the scan did not report ConstrainedProbe's constrained. JsonElement callvirt Object::ToString as JsonElement::ToString; the IL decoder is not mapping a constrained call, so the JsonElement::ToString ban matches nothing");
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
