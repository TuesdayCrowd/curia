using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// ORG13 (errata G82, R5.26; amendment 1, #5): nothing in <c>Curia.Api</c> reads the name a request carries, except the reasoned rows. The Forum's name is its one configured origin, and the request's
/// <c>Host</c>, scheme, path base and forwarded headers are inputs to no URL a credential is compared with; this is the shape CS-15 uses for "a new line that does this fails the build": the callers are read
/// from the shipped assembly's IL and held to a list whose every entry has a reason. Banned: <c>HttpRequest.Host</c>, <c>Scheme</c>, <c>PathBase</c> and <c>IsHttps</c>, every <c>HostString</c> member (the
/// framework's parsed <c>Host</c> decodes an internationalized name and is replaced by a forwarded one, so the comparison reads the header as received), and the header names a forwarding proxy writes, as
/// string literals. <b>Red on the tree</b>: three methods build a URL from the request (<c>TokenEndpointUrl</c>, <c>AbsoluteUrl</c>, <c>ReaderContractUrl</c>) and a fourth, <c>KeyRouteValues</c>, reads the path
/// base to strip it from the raw target, which is routing identity and not a name (its one reasoned row).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OriginFenceTests
{
    private const string Request = "Microsoft.AspNetCore.Http.HttpRequest";
    private const string HostString = "Microsoft.AspNetCore.Http.HostString";
    private const string Headers = "Microsoft.AspNetCore.Http.IHeaderDictionary";

    /// <summary>The reads of the request's name that are allowed, and why. A row is added only with its reason, in the pull request that adds the read.</summary>
    private static readonly (string Caller, string CalleeType, string CalleeMember, string Reason)[] Rows =
    [
        ("Curia.Api.ForumEndpoints.KeyRouteValues", Request, "get_PathBase",
            "routing identity, not a name: the key routes' values are decoded from the raw request target once, with the path base stripped from it"),
        ("Curia.Api.HostNotServed.Invoke", Headers, "get_Host",
            "R5.27's one comparison: the Host header as received, never the framework's parsed Host"),
    ];

    /// <summary>The header names a forwarding proxy writes, and <c>Host</c> itself, which no Forum code names as a string.</summary>
    private static readonly string[] BannedLiterals = ["Host", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-For", "Forwarded"];

    private static string ApiAssembly() =>
        Shipped.ShippedAssemblies().Single(p => string.Equals(Path.GetFileName(p), "Curia.Api.dll", StringComparison.Ordinal));

    private static bool IsRead(string calleeType, string calleeMember) =>
        calleeType == HostString
        || (calleeType == Request && calleeMember is "get_Host" or "get_Scheme" or "get_PathBase" or "get_IsHttps")
        || (calleeType == Headers && calleeMember == "get_Host");

    [Fact]
    public void R5_26_ORG13_NoCodeReadsTheNameOfTheRequestExceptTheReasonedRows()
    {
        Assert.All(Rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"{row.Caller} has no reason"));
        var offenders = IlCallSites.CallSites(ApiAssembly())
            .Where(s => s.CallerType.StartsWith("Curia.Api", StringComparison.Ordinal) && IsRead(s.CalleeType, s.CalleeMember))
            .Where(s => !Rows.Any(r => r.Caller == $"{s.CallerType}.{s.CallerMethod}" && r.CalleeType == s.CalleeType && r.CalleeMember == s.CalleeMember))
            .Select(s => $"{s.CallerType}.{s.CallerMethod} calls {s.CalleeType}.{s.CalleeMember}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0, "these methods of Curia.Api read the name a request carries, and no reasoned row allows it: " + string.Join("; ", offenders));
    }

    [Fact]
    public void R5_26_ORG13_NoCodeNamesAForwardingHeaderOrTheHostHeaderAsAString()
    {
        using var stream = File.OpenRead(ApiAssembly());
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        for (var handle = MetadataTokens.UserStringHandle(0); !handle.IsNil; handle = md.GetNextHandle(handle))
        {
            var literal = md.GetUserString(handle);
            if (BannedLiterals.Contains(literal, StringComparer.Ordinal)) found.Add(literal);
        }

        Assert.True(found.Count == 0, "Curia.Api names these headers as string literals, and no reasoned row allows it: " + string.Join(", ", found));
    }
}
