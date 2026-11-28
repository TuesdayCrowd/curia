using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR N2 (errata G70, R4.43, R6.67), OAS12 and OAS17 (the parts that are green on the tree are said so). OAS12: no production code reads an owner standing or a moderation record from a log nobody verified. The
/// scan counts the calls to <c>AgentStandingProjector.Fold</c>, <c>FlagProjector.Fold</c> and <c>FlagProjector.Extend</c> in every shipped assembly's IL, so the real call list is the red text (the plan's "22" was a grep
/// and counted definitions); the allow-list is per type and source method, and each row of it names its reason; OAS12b is the second door, the event-type literals. To be run in Debug and in Release (D16: the IL differs between them).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActsFenceTests
{
    private static readonly (string Callee, string Member)[] RawFolds =
    [
        ("Curia.Application.Projections.AgentStandingProjector", "Fold"),
        ("Curia.Application.Projections.FlagProjector", "Fold"),
        ("Curia.Application.Projections.FlagProjector", "Extend"),

        // Task 3b (Amendment 6, ruling 75 (c)): the unfiltered siblings of the honoured entry points. They stay public for the tests, and a production caller of one is a raw read of a log nobody verified.
        ("Curia.Application.Projections.SearchProjector", "Fold"),
        ("Curia.Application.Ingest.LogFacts", "Of"),
        ("Curia.Application.Ingest.LogFacts", "Extend"),
        ("Curia.Application.Projections.InboxSelector", "Select"),
    ];

    /// <summary>
    /// Callers that may fold a raw log, each by type and source method with its reason (Task 6b, Amendment 8, ruling 97: the allow-list is per method, so a raw call written in a honoured sibling of the same type is not
    /// allowed). The method is the one <see cref="IlCallSites"/> reports: a lambda or a local function maps to the method that wrote it. A <c>*</c> row allows the whole type, and says why a method row cannot.
    /// </summary>
    private static readonly (string Type, string Method, string Reason)[] Allowed =
    [
        ("Curia.Application.Credentials.VerifiedLog", ".ctor", "HonouredEvents: the two lazy views (standings, moderation) fold only the rows the act procedure honoured (R6.67)"),
        ("Curia.Application.Credentials.VerifiedLog", "StandingOf", "folds one agent's own honoured events"),
        ("Curia.Application.Credentials.VerifiedLog", "PostureStandingOf", "folds the honoured events of one agent's read (its stream, posts, their moderation and the acceptances of its threads): the whole-log fold's entry for the agent, over the rows that bear on it (errata G87)"),
        ("Curia.Application.Credentials.VerifiedLog", "ModerationOf", "folds one post's own honoured events (a record is honoured only in the stream of the post it names, R10.60; errata G87)"),
        ("Curia.Application.Projections.AgentStandingProjector", "Fold", "the standing fold reads a demoting moderation record through FlagProjector.Fold over the events it is handed; its production callers hand it honoured events, and the unfiltered call is banned by the AgentStandingProjector.Fold callee row"),
        ("Curia.Application.Projections.FlagProjector", "Fold", "the projector's own overloads call each other: Fold is Extend over an empty map"),
        ("Curia.Application.Projections.SearchProjector", "Fold", "the unfiltered Fold's own body folds the raw moderation and standings; its production callers are banned by the SearchProjector.Fold callee row, and FoldHonoured feeds the same private body from a VerifiedLog"),
        ("Curia.Application.Ingest.LogFacts", "Of", "the unfiltered Of folds the raw moderation in its own body; its production callers are banned by the LogFacts.Of callee row, and OfHonoured decides from a VerifiedLog"),
        ("Curia.Application.Ingest.LogFacts", "Extend", "the unfiltered Extend folds the raw delta in its own body; its production callers are banned by the LogFacts.Extend callee row, and facts of a verified log refuse to be extended"),
        ("Curia.Application.Ingest.LogFacts", "ServableAfter", "the branch for facts that are not of a verified log extends the raw moderation; the verified branch decides the delta's acts over the verified log (the two branches are one source method, which is the finest the IL gives)"),
        ("Curia.Application.Projections.InboxSelector", "Select", "the unfiltered Select's own body folds SearchProjector.Fold; its production callers are banned by the InboxSelector.Select callee row, and SelectHonoured folds SearchProjector.FoldHonoured"),
    ];

    private const string Application = "Curia.Application";

    private static string OwningType(string callerType)
    {
        var plus = callerType.IndexOf("+<", StringComparison.Ordinal);
        return plus < 0 ? callerType : callerType[..plus];
    }

    private static (string Owner, string Method, string Callee, string Member)[] RawSites() =>
    [
        .. Shipped.ShippedAssemblies()
            .SelectMany(p => IlCallSites.CallSites(p))
            .Where(c => RawFolds.Any(f => f.Callee == c.CalleeType && f.Member == c.CalleeMember))
            .Select(c => (Owner: OwningType(c.CallerType), Method: c.CallerMethod, Callee: c.CalleeType, Member: c.CalleeMember)),
    ];

    private static bool IsAllowed((string Owner, string Method, string Callee, string Member) site) =>
        Allowed.Any(a => a.Type == site.Owner && (a.Method == "*" || a.Method == site.Method));

    /// <summary>
    /// OAS12's guard (Task 6b, ruling 98), in three parts so that no row is vacuous behind another's sites. (a) Every row of the callee list resolves by reflection in <c>Curia.Application</c> (the type exists and has a method of
    /// that name), so a renamed callee fails by its row. (b) The four rows that have a caller in <c>src</c> have at least one site each; <c>LogFacts.Of</c>, <c>LogFacts.Extend</c> and <c>InboxSelector.Select</c> have no
    /// caller by design (production uses their honoured siblings; the rows exist to ban a future caller), so they are resolved by reflection only. (c) Every allowed row's type exists and owns at least one site, and a method row
    /// owns a site in that method, so a stale row fails naming itself.
    /// </summary>
    [Fact]
    public void OAS12_TheScanFindsTheCallersOfTheRawFolds()
    {
        var application = System.Reflection.Assembly.LoadFrom(Shipped.ShippedAssemblies().Single(p => Path.GetFileNameWithoutExtension(p) == Application));
        var unresolved = RawFolds
            .Where(f => application.GetType(f.Callee) is not { } type
                || !type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance).Any(m => m.Name == f.Member))
            .Select(f => $"{f.Callee}.{f.Member}")
            .ToArray();
        Assert.True(unresolved.Length == 0, $"Expected: every raw-fold row resolves in {Application}  Actual: {unresolved.Length} that does not: {string.Join(", ", unresolved)}");

        var sites = RawSites();
        (string Callee, string Member)[] withCallers =
        [
            ("Curia.Application.Projections.FlagProjector", "Fold"),
            ("Curia.Application.Projections.FlagProjector", "Extend"),
            ("Curia.Application.Projections.AgentStandingProjector", "Fold"),
            ("Curia.Application.Projections.SearchProjector", "Fold"),
        ];
        var silent = withCallers.Where(r => !sites.Any(s => s.Callee == r.Callee && s.Member == r.Member)).Select(r => $"{r.Callee}.{r.Member}").ToArray();
        Assert.True(silent.Length == 0, $"Expected: a call site for every raw-fold row that has a caller  Actual: {silent.Length} with none: {string.Join(", ", silent)}");

        var stale = Allowed
            .Where(a => application.GetType(a.Type) is null || !sites.Any(s => s.Owner == a.Type && (a.Method == "*" || a.Method == s.Method)))
            .Select(a => $"{a.Type}.{a.Method}")
            .ToArray();
        Assert.True(stale.Length == 0, $"Expected: every allowed row owns a raw-fold site  Actual: {stale.Length} that owns none: {string.Join(", ", stale)}");
    }

    [Fact]
    public void OAS12_NoProductionCodeReadsStandingOrModerationFromAnUnfilteredLog()
    {
        var calls = RawSites()
            .Where(c => !IsAllowed(c))
            .GroupBy(c => $"{c.Owner.Split('.')[^1]}.{c.Method} -> {c.Callee.Split('.')[^1]}.{c.Member}")
            .Select(g => $"{g.Key} ({g.Count()})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.All(Allowed, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason), $"{a.Type}.{a.Method} has no reason"));
        Assert.True(calls.Length == 0, $"Expected: no caller outside the allow-list  Actual: {calls.Length} caller(s): {string.Join(", ", calls)}");
    }

    /// <summary>The event types an operator act is written under: a literal that names one decides something by it, so it is a second door beside the raw callees (Task 6b, ruling 97).</summary>
    private static readonly string[] ActTypeLiterals = ["agent.owner-attested", "moderation.applied"];

    private static readonly (string Type, string Reason)[] AllowedLiteralOwners =
    [
        ("Curia.Application.Credentials.OperatorActFold", "the fold decides an act row by its event type (R6.67)"),
        ("Curia.Application.Projections.AgentStandingProjector", "the projector reads an attestation's members from the events it is handed (honoured ones, from a VerifiedLog)"),
        ("Curia.Application.Projections.FlagProjector", "the projector reads a moderation record from the events it is handed (honoured ones, from a VerifiedLog)"),
        ("Curia.Application.Credentials.AttestOwner", "the writer appends an attestation"),
        ("Curia.Application.Moderation.ApplyModeration", "the writer appends a moderation record"),
        ("Curia.Application.Ingest.LogFacts", "the delta trigger: a moderation row in an appended delta is what makes ServableAfter decide the delta's acts again"),
    ];

    private static (string Assembly, string Type, string Method, string Literal)[] ActLiterals() =>
    [
        .. Shipped.ShippedAssemblies()
            .SelectMany(p => IlCallSites.StringLiteralsOfAssembly(p).Select(l => (Assembly: Path.GetFileNameWithoutExtension(p), l.Type, l.Method, l.Literal)))
            .Where(l => ActTypeLiterals.Any(t => l.Literal.Contains(t, StringComparison.Ordinal))),
    ];

    /// <summary>
    /// OAS12b, the scan's own test (the vacuity guard, ruling 97): each act-type literal is found at least once across the shipped assemblies, and each allowed owner holds at least one, so a renamed constant, a moved use or a
    /// stale row fails naming itself.
    /// </summary>
    [Fact]
    public void OAS12b_TheScanFindsTheActTypeLiteralsAndTheirOwners()
    {
        var literals = ActLiterals();
        var missing = ActTypeLiterals.Where(t => !literals.Any(l => l.Literal.Contains(t, StringComparison.Ordinal))).ToArray();
        Assert.True(missing.Length == 0, $"Expected: every act-type literal found  Actual: none holds {string.Join(", ", missing)}");

        var stale = AllowedLiteralOwners.Where(a => !literals.Any(l => l.Type == a.Type)).Select(a => a.Type).ToArray();
        Assert.True(stale.Length == 0, $"Expected: every allowed owner holds an act-type literal  Actual: {stale.Length} that holds none: {string.Join(", ", stale)}");
    }

    /// <summary>OAS12b: no type but those the allow-list names holds a literal that equals or contains <c>agent.owner-attested</c> or <c>moderation.applied</c>, in any shipped assembly (a <c>const</c> hole is folded into its neighbouring literal, hence "contains").</summary>
    [Fact]
    public void OAS12b_NoTypeButTheActsOwnersNamesAnActEventType()
    {
        Assert.All(AllowedLiteralOwners, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason), $"{a.Type} has no reason"));
        var strays = ActLiterals()
            .Where(l => !AllowedLiteralOwners.Any(a => a.Type == l.Type))
            .Select(l => $"{l.Assembly}: {l.Type}.{l.Method} holds \"{l.Literal}\"")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(strays.Length == 0, $"Expected: no act-type literal outside the allow-list  Actual: {strays.Length}: {string.Join("; ", strays)}");
    }

    /// <summary>
    /// OAS17, the string-scan clause: no shipped assembly but <c>curia-operator</c> contains the name of the operator-acts key's variable. The scan reads every shipped assembly's bytes for the variable's
    /// UTF-16 spelling (the encoding of the string heap), so it finds a literal and not a computed name. The variable exists and the operator tool reads it, so the scan has a positive case in this same test:
    /// the same scan over <c>curia-operator.dll</c> must find the string, or the scan reads the wrong heap or the wrong encoding and passes for the wrong reason. Falsified by FOAS22 (Task 7), which is the row
    /// that shows it red.
    /// </summary>
    [Fact]
    public void OAS17_NoForumAssemblyHoldsTheOperatorActsKeysVariableName()
    {
        var name = System.Text.Encoding.Unicode.GetBytes("CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM");
        var shipped = Shipped.ShippedAssemblies();
        var operatorTool = shipped.Single(p => Path.GetFileNameWithoutExtension(p) == "curia-operator");
        Assert.True(File.ReadAllBytes(operatorTool).AsSpan().IndexOf(name) >= 0, "Expected: the scan finds the variable's name in curia-operator  Actual: it does not, so the scan reads the wrong thing");

        var holders = shipped
            .Where(p => Path.GetFileNameWithoutExtension(p) != "curia-operator")
            .Where(p => File.ReadAllBytes(p).AsSpan().IndexOf(name) >= 0)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToArray();
        Assert.True(holders.Length == 0, $"Expected: no Forum assembly names the operator-acts key's variable  Actual: {string.Join(", ", holders)}");
    }

    /// <summary>
    /// OAS17, the clause that is red today: the implementers of <c>IOperatorActSigner</c>, across every shipped assembly, are exactly <c>Curia.OperatorTool.OperatorActsSigningKey</c>. The interface is found by name
    /// through reflection so that its absence fails by assertion (<c>implementers: []</c>) and not by compilation, which would stop every other test in this assembly.
    /// </summary>
    [Fact]
    public void OAS17_TheOnlyImplementerOfTheOperatorActSignerIsTheOperatorsKey()
    {
        var implementers = new List<string>();
        foreach (var path in Shipped.ShippedAssemblies())
        {
            var assembly = System.Reflection.Assembly.LoadFrom(path);
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = [.. e.Types.Where(t => t is not null)!]; }
            implementers.AddRange(types.Where(t => !t.IsInterface && t.GetInterfaces().Any(i => i.Name == "IOperatorActSigner")).Select(t => t.FullName!));
        }

        WholeText.Same("implementers: [Curia.OperatorTool.OperatorActsSigningKey]", $"implementers: [{string.Join(", ", implementers.Order(StringComparer.Ordinal))}]");
    }
}
