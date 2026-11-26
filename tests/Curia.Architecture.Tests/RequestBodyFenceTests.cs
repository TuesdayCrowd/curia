using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G84 (R6.68 (rev.)), amendment 1 ruling 5 (fences): the Forum's one bound on a request body is held in the build, not in review. (i) the body is read only from a listed set of methods, each with its
/// reason, so a handler that reads <c>HttpRequest.Body</c> (or the pipe, the form, or a JSON extension) and is not listed fails; (ii) the cap is read from the one definition of R6.39's cap, <c>AdmitLimits.Default.MaxBytes</c>,
/// by <c>RequestBodyBound.Cap</c>, and the middleware reads that and no literal; (iii) the middleware is registered after <c>UseUnreadableRequests</c> (which composes its status into R11.33's answer) and before
/// <c>UseUtf8JsonBodies</c>, routing and every mapped route, read from <c>Program.cs</c>'s order (the precedent is ULG53's, which reads the same file). The binder routes' three body types are
/// <c>JsonReaderFenceTests</c>' <c>BoundBodies</c>; they are not call sites and are bounded by the stream the middleware lays under them (BND2).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RequestBodyFenceTests
{
    private const string Request = "Microsoft.AspNetCore.Http.HttpRequest";

    /// <summary>The members through which a request's body is reached: the stream, the pipe, the form (both ways) and the JSON extension.</summary>
    private static readonly (string Type, string Member)[] Readers =
    [
        (Request, "get_Body"),
        (Request, "set_Body"),
        (Request, "get_BodyReader"),
        (Request, "ReadFormAsync"),
        (Request, "get_Form"),
        ("Microsoft.AspNetCore.Http.HttpRequestJsonExtensions", "ReadFromJsonAsync"),
    ];

    /// <summary>Each method that reaches the body, and why. A row is added only with its reason, in the pull request that adds the read.</summary>
    private static readonly (string Caller, string Member, string Reason)[] Rows =
    [
        ("Curia.Api.BodyBound.UseBodyBound", "get_Body", "R6.68 (rev.): the middleware wraps the body in the counting stream"),
        ("Curia.Api.BodyBound.UseBodyBound", "set_Body", "R6.68 (rev.): the middleware replaces the body with the counting stream, so every reader below reads through it"),
        ("Curia.Api.ForumEndpoints.SubmitAsync", "get_Body", "the post route copies the whole body, read through the counting stream (ADMIT and VERIFY need the whole document)"),
        ("Curia.Api.ForumEndpoints.ReadKeyActBodyAsync", "get_Body", "a key act's loop reads at most its own 32,768 bytes (R11.34's guarded reader), through the counting stream"),
        ("Curia.Api.Issuer.TokenEndpoint.IssueAsync", "ReadFormAsync", "the token endpoint's form reader, which has its own limits; a body past the bound is thrown out of the stream and answered 413 (BND2)"),
    ];

    private static string ApiAssembly() =>
        Shipped.ShippedAssemblies().Single(p => string.Equals(Path.GetFileName(p), "Curia.Api.dll", StringComparison.Ordinal));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>(i) Every method of every shipped assembly that reaches a request's body is a listed row, and every row is a call that exists.</summary>
    [Fact]
    public void R6_68_BND13_NoCodeReadsARequestBodyExceptTheReasonedRows()
    {
        Assert.All(Rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"{row.Caller} has no reason"));
        var calls = Shipped.ShippedAssemblies()
            .SelectMany(path => IlCallSites.CallSites(path))
            .Where(s => Readers.Contains((s.CalleeType, s.CalleeMember)))
            .Select(s => (Caller: $"{s.CallerType}.{s.CallerMethod}", Member: s.CalleeMember))
            .Distinct()
            .ToList();

        var unlisted = calls.Where(c => !Rows.Any(r => r.Caller == c.Caller && r.Member == c.Member)).Select(c => $"{c.Caller} reaches the body through {c.Member}").Order(StringComparer.Ordinal).ToArray();
        var stale = Rows.Where(r => !calls.Contains((r.Caller, r.Member))).Select(r => $"{r.Caller} ({r.Member}) names no call site").ToArray();
        Assert.True(unlisted.Length == 0 && stale.Length == 0, "unlisted: " + string.Join("; ", unlisted) + " | stale: " + string.Join("; ", stale));
    }

    /// <summary>(ii) The bound is read from R6.39's one definition and the middleware reads the bound, not a number.</summary>
    [Fact]
    public void R6_68_BND14_TheCapIsReadFromTheOneDefinitionAndNotTypedASecondTime()
    {
        var calls = IlCallSites.CallSites(ApiAssembly());
        Assert.True(
            calls.Any(c => c.CallerType == "Curia.Api.RequestBodyBound" && c.CalleeType == "Curia.Canon.Json.AdmitLimits" && c.CalleeMember == "get_MaxBytes"),
            "RequestBodyBound.Cap does not read AdmitLimits.MaxBytes: the figure is typed a second time");
        Assert.True(
            calls.Any(c => c.CallerType == "Curia.Api.BodyBound" && c.CallerMethod == "UseBodyBound" && c.CalleeType == "Curia.Api.RequestBodyBound" && c.CalleeMember == "get_Cap"),
            "the middleware does not read RequestBodyBound.Cap");
        Assert.False(
            calls.Any(c => c.CallerType == "Curia.Api.BodyBound" && c.CalleeType == "Curia.Canon.Json.AdmitLimits"),
            "the middleware reads R6.39's cap itself and not through the bound's one definition");
    }

    /// <summary>(iii) The middleware is registered after the status-code pages that compose its answer and before everything that reads or routes a request.</summary>
    [Fact]
    public void R6_68_BND15_TheMiddlewareIsRegisteredAfterTheStatusPagesAndBeforeTheBodiesAreReadOrRouted()
    {
        var program = File.ReadAllText(Path.Combine(Root(), "src", "Curia.Api", "Program.cs"));
        var uses = Use().Matches(program).Select(m => (Name: m.Groups[1].Value, At: m.Index)).ToList();
        var order = uses.Select(u => u.Name).ToList();
        int Position(string name) => order.IndexOf(name);
        var problems = new List<string>();
        foreach (var name in new[] { "UseStoreFaults", "UseUnreadableRequests", "UseBodyBound", "UseUtf8JsonBodies" })
            if (Position(name) < 0) problems.Add($"Program.cs registers no app.{name}()");

        if (problems.Count == 0)
        {
            if (Position("UseBodyBound") != Position("UseUnreadableRequests") + 1) problems.Add($"UseBodyBound is not registered immediately after UseUnreadableRequests: the order is {string.Join(", ", order)}");
            if (Position("UseUtf8JsonBodies") != Position("UseBodyBound") + 1) problems.Add("UseUtf8JsonBodies is not immediately after UseBodyBound: a body read before the bound is unbounded");
            var bound = uses.Single(u => u.Name == "UseBodyBound").At;
            foreach (var mapped in new[] { "TokenEndpoint.Map(app)", "ForumEndpoints.Map(app)", "ActaEndpoints.Map(app)" })
                if (program.IndexOf(mapped, StringComparison.Ordinal) is var at && (at < 0 || at < bound)) problems.Add($"{mapped} is not registered after UseBodyBound");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [GeneratedRegex(@"^\s*app\.(Use\w+)\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Use();
}
