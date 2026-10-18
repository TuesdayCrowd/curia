using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.73 (errata G22), the fences on <c>curia browse</c>, read from the shipped assembly's IL rather than from what a type
/// declares: the screen holds no identity, starts no process, writes no file; reaches <c>System.Console</c> only through the
/// members the design lists; is drawn to by one type; and every <c>Vt</c> string is a compile-time constant, so a sequence
/// cannot be built from content.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BrowseFenceTests
{
    private const string Namespace = "Curia.Client.Cli.";

    /// <summary>The types of the screen and its command, by simple name; their nested and generated types are theirs.</summary>
    private static readonly string[] ScreenTypes =
    [
        "Screen", "ScreenRow", "ScreenSize", "CellWidth", "ContentLayout", "PassageHeader", "RowWrap", "IndexRow",
        "Vt", "ScreenWriter", "ScreenFrame", "ScreenBytes", "ScreenDecision", "TerminalFacts",
        "BrowseCommand", "BrowseState", "BrowseView", "BrowseTransition", "BrowseSession", "OutcomeLedger", "LoadResult",
        "ViewSource", "ForumBrowseSource", "ScreenWrite", "OutcomeRule", "ViewKind", "ContinuedRange", "IBrowseSource",
    ];

    /// <summary>
    /// The types of <c>Curia.Client.Cli</c> that are not the screen's, each with the reason it is not (spec 14.5 item 2). The
    /// screen shares an assembly with the profile store and the signer, so a type that is in neither list is a type the fence
    /// does not read, silently; the classification fact below fails the build on it.
    /// </summary>
    private static readonly Dictionary<string, string> NotScreenTypes = new(StringComparer.Ordinal)
    {
        ["ExitCode"] = "the exit codes every verb shares; the screen reads them and defines none",
        ["Args"] = "the command line's parser, shared by every verb",
        ["Output"] = "the line form's writer and the one reader and drawer of the terminal; the fence names who may call it",
        ["Program"] = "the verbs' composition root; it chooses the screen or the line form and holds the profile store",
        ["Hints"] = "the equivalent-command sentences, constants and quoting the screen and the line form share",
        ["Help"] = "the command line's help text",
        ["Testis"] = "the verifier handoff of the verbs; the screen starts no process",
        ["TestisResult"] = "the result of that handoff",
        ["ForeignWatch"] = "the lifecycle watch of an enrolled agent's own profile; an identity the screen must not reach",
    };

    /// <summary>Spec 6: the members of <c>System.Console</c> the screen's one writer and reader may reach.</summary>
    private static readonly string[] ConsoleMembers =
    [
        "get_Out", "get_Error", "ReadKey", "get_KeyAvailable", "get_WindowWidth", "get_WindowHeight",
        "get_IsInputRedirected", "get_IsOutputRedirected", "set_TreatControlCAsInput",
    ];

    private static string CliPath => Path.Combine(AppContext.BaseDirectory, "curia.dll");

    private static Assembly Cli => Assembly.Load(new AssemblyName("curia"));

    private static bool IsClassified(string name) =>
        ScreenTypes.Contains(name, StringComparer.Ordinal) || NotScreenTypes.ContainsKey(name);

    private static bool IsScreenCaller(string callerType) =>
        ScreenTypes.Any(name => callerType == Namespace + name || callerType.StartsWith(Namespace + name + "+", StringComparison.Ordinal));

    [Fact]
    public void R10_73_EveryTypeInTheNamespaceIsClassifiedAsScreenOrNot()
    {
        var types = Cli.GetTypes()
            .Where(t => t.Namespace == Namespace.TrimEnd('.') && !t.IsNested)
            .Select(t => t.Name)
            .ToList();

        // Non-vacuity: the lists name the types the assembly has, and the assembly has a good many of both kinds.
        Assert.True(types.Count > 30, $"only {types.Count} types were read from the namespace");
        Assert.Contains("BrowseSession", types);
        Assert.Contains("Program", types);
        var stale = ScreenTypes.Concat(NotScreenTypes.Keys).Where(name => !types.Contains(name, StringComparer.Ordinal)).ToList();
        Assert.True(stale.Count == 0, "listed but not in the namespace: " + string.Join(", ", stale));

        var unclassified = types.Where(name => !IsClassified(name)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0, "types in Curia.Client.Cli that are neither a screen type nor a named not-screen type: " + string.Join(", ", unclassified));

        var both = ScreenTypes.Where(NotScreenTypes.ContainsKey).ToList();
        Assert.True(both.Count == 0, "classified as both: " + string.Join(", ", both));
        Assert.All(NotScreenTypes, row => Assert.False(string.IsNullOrWhiteSpace(row.Value), row.Key + " has no reason"));
    }

    [Fact]
    public void R10_73_TheScreenHoldsNoIdentityOrCredential()
    {
        var sites = IlCallSites.CallSites(CliPath).Where(s => IsScreenCaller(s.CallerType)).ToList();

        // Non-vacuity: the session and the state are among the callers read, and so is a good share of the screen.
        Assert.True(sites.Count > 200, $"only {sites.Count} call sites were read from the screen's types");
        foreach (var required in new[] { "BrowseSession", "BrowseState", "BrowseCommand", "ScreenWriter" })
            Assert.Contains(sites, s => s.CallerType == Namespace + required);

        string[] identity = ["ProfileStore", "EnrolledAgent", "AgentProfile", "Signer", "ForumSession", "ForeignWatch", "KeyActs", "SubmissionBuilder"];
        var held = sites.Where(s => identity.Any(i => s.CalleeType.Contains(i, StringComparison.Ordinal))).ToList();
        Assert.True(
            held.Count == 0,
            "the screen reaches an identity: " + string.Join(", ", held.Select(s => $"{s.CallerType}.{s.CallerMethod} -> {s.CalleeType}.{s.CalleeMember}")));
    }

    [Fact]
    public void R10_73_TheScreenStartsNoProcessAndWritesNoFile()
    {
        var sites = IlCallSites.CallSites(CliPath).Where(s => IsScreenCaller(s.CallerType)).ToList();
        Assert.True(sites.Count > 200);

        string[] banned =
        [
            "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo", "System.IO.File", "System.IO.FileStream",
            "System.IO.FileInfo", "System.IO.Directory", "System.IO.StreamWriter",
        ];
        var found = sites.Where(s => banned.Contains(s.CalleeType)).ToList();
        Assert.True(
            found.Count == 0,
            "the screen starts a process or touches a file: " + string.Join(", ", found.Select(s => $"{s.CallerType}.{s.CallerMethod} -> {s.CalleeType}.{s.CalleeMember}")));
    }

    [Fact]
    public void R10_73_EveryVtStringIsACompileTimeConstant()
    {
        var vt = Cli.GetType(Namespace + "Vt", throwOnError: true)!;
        var fields = vt.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);

        Assert.True(fields.Length >= 12, $"only {fields.Length} fields were found in Vt; the reflection is wrong");
        var notConstant = fields.Where(f => !f.IsLiteral).Select(f => f.Name).ToList();
        Assert.True(notConstant.Count == 0, "Vt fields that are not const: " + string.Join(", ", notConstant));

        var members = vt.GetMembers(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Method && m is not MethodBase { IsSpecialName: true })
            .Select(m => m.Name)
            .ToList();
        Assert.Equal(["Address"], members);
    }

    [Fact]
    public void R10_73_SystemConsoleIsReachedOnlyThroughTheListedMembers()
    {
        var sites = IlCallSites.CallSites(CliPath).Where(s => s.CalleeType == "System.Console").ToList();
        Assert.Contains(sites, s => s.CalleeMember == "get_Out");
        Assert.Contains(sites, s => s.CalleeMember == "get_WindowWidth");
        Assert.Contains(sites, s => s.CalleeMember == "ReadKey");

        var outside = sites.Where(s => !ConsoleMembers.Contains(s.CalleeMember)).ToList();
        Assert.True(
            outside.Count == 0,
            "System.Console members outside the list: " + string.Join(", ", outside.Select(s => $"{s.CallerType}.{s.CallerMethod} -> {s.CalleeMember}")));
    }

    [Fact]
    public void R10_73_OnlyTheSessionsLoopDrawsAndOnlyItLeaves()
    {
        var sites = IlCallSites.CallSites(CliPath);

        var drawers = sites.Where(s => s.CalleeType == Namespace + "Output" && s.CalleeMember == "Draw").Select(s => s.CallerType).Distinct().ToList();
        Assert.Equal([Namespace + "BrowseSession"], drawers);

        var leavers = sites.Where(s => s.CalleeType == Namespace + "ScreenWriter" && s.CalleeMember == "Leave").Select(s => s.CallerType).Distinct().ToList();
        Assert.Equal([Namespace + "BrowseSession"], leavers);
    }
}
