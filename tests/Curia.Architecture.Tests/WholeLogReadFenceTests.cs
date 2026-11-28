using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Curia.Application.Ports;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// RC9 (errata G87, R11.113): <i>a route does not read the log whole</i>, held as a build-failing fence by method. Every call in <c>src/</c> that reads the whole log or folds one (a whole-log read, a verifier
/// over a whole list, the standings or moderation of every agent, the Acta's fold) is a reasoned row, keyed by (file, method) and counted, with the class of the site (W, whole by design; B, a serving
/// route's fold, which leaves with the stage that scopes it; C, a corpus-shaped read) and the reason. A read in a method with no row, or more of them than its row allows, fails and names it, so a class A
/// route (authentication, the token, enrolment, key acts, the key set, the budget, the flag routes, a submission's or an acceptance's or a flag's authorization) that reads the log whole is a build failure
/// and not the next measurement's finding. A row naming no call fails too (stale).
///
/// <para>It is a source scan of every <c>*.cs</c> under <c>src/</c> outside <c>bin</c> and <c>obj</c>, comment lines excluded. Its positive control is the rows themselves: the scan must find each one.
/// Falsified (FRC11) by putting one whole-log read back in a class A method.</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class WholeLogReadFenceTests
{
    private const string W = "W (whole by design)";
    private const string B = "B (a serving route, scoped in the stage that serves it)";
    private const string C = "C (corpus-shaped)";
    private const string NotTheLog = "not the event log";

    /// <summary>(file, method, calls, class, reason). Operator verbs are by design; so are the start-up scans, the head signer's fold and the Acta's own routes.</summary>
    private static readonly (string File, string Method, int Calls, string Class, string Reason)[] Rows =
    [
        ("src/Curia.Api/ActaEndpoints.cs", "FoldAsync", 2, W, "the Acta's own routes: a root needs every leaf"),
        ("src/Curia.Api/Adapters/LifecycleEntryScan.cs", "StartAsync", 1, W, "the start-up scan (EventId 7007)"),
        ("src/Curia.Api/Adapters/OperatorActEntryScan.cs", "StartAsync", 1, W, "the start-up scan (EventId 7010)"),
        ("src/Curia.Api/ForumEndpoints.cs", "AcceptAnswerAsync", 1, B, "target resolution: the answer, its correction chain, its thread's root and acceptance fold the corpus; the acceptor's posture and acceptances are read from its own rows (class A) before it"),
        ("src/Curia.Api/ForumEndpoints.cs", "ActaOf", 1, W, "the Acta a serving route folds for an inline proof; leaves with the stored leaf (R6.69) and the serving routes' scoping"),
        ("src/Curia.Api/ForumEndpoints.cs", "BatchAsync", 3, B, "serves posts"),
        ("src/Curia.Api/ForumEndpoints.cs", "DuplicateRefusal", 1, C, "the refusal's thread, from the corpus"),
        ("src/Curia.Api/ForumEndpoints.cs", "GetPostAsync", 3, B, "serves a post"),
        ("src/Curia.Api/ForumEndpoints.cs", "GetThreadAsync", 3, B, "serves a thread"),
        ("src/Curia.Api/ForumEndpoints.cs", "InboxAsync", 3, C, "the inbox is a function of the corpus (R10.101)"),
        ("src/Curia.Api/ForumEndpoints.cs", "ListBoardAsync", 3, B, "serves a board's posts"),
        ("src/Curia.Api/ForumEndpoints.cs", "ListBoardsAsync", 2, C, "the list of boards is a function of the corpus"),
        ("src/Curia.Api/ForumEndpoints.cs", "PersistSubmissionAsync", 1, B, "the parent checks and the facts the append's guard is built from"),
        ("src/Curia.Api/ForumEndpoints.cs", "ReadEventsAsync", 2, W, "the helper the whole-log routes above read through; no class A route calls it"),
        ("src/Curia.Api/ForumEndpoints.cs", "SearchAsync", 3, C, "search is a function of the corpus"),
        ("src/Curia.Api/ForumEndpoints.cs", "Servable", 1, B, "every post's moderation, for the serving routes"),
        ("src/Curia.Api/ForumEndpoints.cs", "SignalRefusal", 1, B, "a vote's or verification's target and pair rules read the corpus"),
        ("src/Curia.Api/ForumEndpoints.cs", "SubmitAttemptAsync", 4, C, "after its authorization (class A, read from the author's own rows): the already-recorded revision, the parent and signal checks and the near-duplicate check fold the corpus"),
        ("src/Curia.Application/Credentials/AttestOwner.cs", "AppendAsync", 2, W, "the operator's out-of-band verb (R4.30)"),
        ("src/Curia.Application/Credentials/AttestOwner.cs", "RecordAsync", 3, W, "the operator's out-of-band verb (R4.30)"),
        ("src/Curia.Application/Credentials/DeclareCompromise.cs", "DeclareAsync", 1, W, "the operator's declaration (R12.11)"),
        ("src/Curia.Application/Credentials/KeyActs.cs", "ReadWholeLifecycleAsync", 4, W, "the operator's declaration reads the whole log; no route calls it"),
        ("src/Curia.Application/Credentials/LifecycleVerifier.cs", "?", 1, W, "the verifier's own whole-log constructor"),
        ("src/Curia.Application/Credentials/LifecycleVerifier.cs", "ExtendTo", 1, W, "the whole-log extension (G71), used by the near-duplicate half of a submission"),
        ("src/Curia.Application/Credentials/OperatorActStatements.cs", "IsFirstOperatorKey", 1, W, "the operator verbs' check that the key is the first published"),
        ("src/Curia.Application/Credentials/PublishDeclarationKey.cs", "PublishAsync", 3, W, "the operator's out-of-band verb (R6.62)"),
        ("src/Curia.Application/Credentials/PublishOperatorKey.cs", "PublishAsync", 3, W, "the operator's out-of-band verb (R6.66)"),
        ("src/Curia.Application/Ingest/LogFacts.cs", "OfHonoured", 1, B, "the corpus facts a submission's checks and guard read"),
        ("src/Curia.Application/Ingest/LogFacts.cs", "ServableAfter", 2, B, "the corpus facts after a delta"),
        ("src/Curia.Application/Moderation/ApplyModeration.cs", "RecordAsync", 6, W, "the operator's out-of-band verb (R10.59)"),
        ("src/Curia.Application/Projections/PostureQuery.cs", "Of", 3, C, "the whole-log posture: the inbox's reader and the oracle the equivalence tests hold the scoped read to"),
        ("src/Curia.Application/Projections/SearchProjection.cs", "FoldHonoured", 2, C, "the corpus fold search and the near-duplicate check read"),
        ("src/Curia.Application/Reads/FlagReads.cs", "ReadAsync", 1, NotTheLog, "the private flag store's own table (bounded by flags raised)"),
        ("src/Curia.Application/Reads/WholeLogPlanReader.cs", "RunAsync", 1, W, "the reference semantics of a plan, and the default of a reader with no better; a fence below holds every shipped reader to its own"),
        ("src/Curia.Application/Retrieval/SearchIndexAudit.cs", "RunAsync", 1, NotTheLog, "the search index's stored rows"),
    ];

    [GeneratedRegex(@"\.ReadAllAsync\(|ReadEventsAsync\(|\.OfLog\(|ActaLog\.Fold\(|\.Standings\(\)|\.Moderation\(\)|ReadWholeLifecycleAsync\(", RegexOptions.CultureInvariant)]
    private static partial Regex WholeRead();

    [GeneratedRegex(@"^ {4}(?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)\s+(?:static\s+)?(?:async\s+)?(?:sealed\s+)?(?:override\s+)?[\w<>\[\],\.\?\(\) ]+?\s+(\w+)\s*(?:<[^>]*>)?\(", RegexOptions.CultureInvariant)]
    private static partial Regex MethodDeclaration();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find a 'src' directory above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void RC9_EveryWholeLogReadIsAReasonedRowAndNoClassARouteReadsTheLogWhole()
    {
        var root = RepositoryRoot();
        var found = new Dictionary<(string File, string Method), int>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            // The operator's own tool is whole by design, as a whole: every verb of it reads the log to sign, to audit or to attest.
            if (relative.StartsWith("src/Curia.Operator/", StringComparison.Ordinal)) continue;
            scanned++;

            var method = "?";
            foreach (var line in File.ReadLines(file))
            {
                if (MethodDeclaration().Match(line) is { Success: true } declared) method = declared.Groups[1].Value;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                var calls = WholeRead().Count(line);
                if (calls > 0) found[(relative, method)] = found.GetValueOrDefault((relative, method)) + calls;
            }
        }

        Assert.True(scanned > 150, $"the scan read {scanned} source files under src/");

        var problems = new List<string>();
        foreach (var ((file, method), count) in found)
        {
            var row = Rows.FirstOrDefault(r => r.File == file && r.Method == method);
            if (row.File is null) problems.Add($"{file} {method} reads the log whole {count} time(s) and has no row (a route that does is not class A; a new reader needs a reasoned row)");
            else if (count > row.Calls) problems.Add($"{file} {method} reads the log whole {count} time(s); its row allows {row.Calls}");
        }

        foreach (var row in Rows.Where(r => !found.ContainsKey((r.File, r.Method))))
            problems.Add($"{row.File} {row.Method} has a row and reads no whole log (a stale row: {row.Reason})");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// The default of <see cref="IPositionedEventReader.ReadAsync"/> answers a plan over the whole log, which is correct and unbounded. Every type built from <c>src/</c> that is an event reader declares
    /// its own, so the unbounded default is a test wrapper's convenience and a shipped adapter never rides on it silently.
    /// </summary>
    [Fact]
    public void RC9_EveryShippedEventReaderAnswersAPlanItself()
    {
        var shipped = new[] { typeof(IEventReader).Assembly, typeof(Curia.Infrastructure.PostgresEventStore).Assembly };
        var readers = shipped
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IEventReader).IsAssignableFrom(t))
            .ToArray();

        Assert.Contains(typeof(Curia.Infrastructure.PostgresEventStore), readers);
        var lacking = readers
            .Where(t => t.GetMethod(nameof(IPositionedEventReader.ReadAsync), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is null)
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(lacking.Length == 0, "an event reader answers a plan by the whole-log default: " + string.Join(", ", lacking));
    }
}
