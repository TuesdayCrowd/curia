using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// PR N2 (errata G70, R6.67), Task 6, OAS29: the C# fold and the independently written Rust fold, decided over one generated corpus. 500 small logs of up to eight operator acts (owner attestations or moderation
/// records), each act genuine or carrying one deliberate fault drawn from R6.67's eight predicates, are written in the vector shape to a temporary directory; <c>expected.json</c> is written from the C# fold's decisions
/// (never by hand), and <c>cargo test --test operator_acts</c> is run over the directory (<c>CURIA_OPERATOR_ACTS_DIR</c>). The Rust test fails on any row or state it decides differently, and prints the number of cases it
/// decided; this test fails unless that number is the corpus's.
///
/// <para>The rows are signed through <see cref="OperatorActStatements"/>, the writers' own statements; a fault is a deliberate departure from them. The generator also knows what it built, and the C# fold's decision for each row is
/// compared with the predicate the construction intended, so a fold that quietly changes a decision fails here by case and row even before the Rust side reads it.</para>
///
/// <para>Vacuity guards, counted from the files written: the corpus holds 500 cases, at least one row skipped under each of the eight predicates, at least one log whose every row is honoured, and the Rust count line is present
/// and equals the corpus size. <c>cargo</c> absent is a failure, not a skip: the second implementation is the property under test.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CrossFoldCorpusTests
{
    private const string Agent = "agent://curia.example/tuesdaycrowd/scriptor";
    private const string Post = "01K4CQ4B0000000000000000P1";
    private const string Digest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Actor = "operator:ops";
    private const int Cases = 500;
    private const string Seed = "0pj9aDvFX1M7";

    private static readonly string[] Crit = ["b64"];

    private static readonly string[] Owners = ["owner:alice", "owner:bob", "owner:carol", "owner:dave"];
    private static readonly string[] Effects = ["withhold", "quarantine", "restore", "dismiss"];
    private static readonly string[] Categories = ["spam", "injection", "incorrect", "duplicate"];

    /// <summary>The fault drawn for an act: 0 genuine, then one per predicate of R6.67, in the order of <see cref="OperatorActPredicates.All"/> after the first, shifted by one so that 0 is no fault.</summary>
    private static readonly string?[] Intended =
    [
        null,
        OperatorActPredicates.NotSigned,
        OperatorActPredicates.StatementMismatch,
        OperatorActPredicates.UnknownKind,
        OperatorActPredicates.BrokenLink,
        OperatorActPredicates.TypMismatch,
        OperatorActPredicates.KeyUnpublished,
        OperatorActPredicates.KeyNotFirst,
        OperatorActPredicates.SignatureInvalid,
    ];

    private readonly record struct ActSpec(int Fault, int Variant);

    private readonly record struct LogSpec(bool IsPostStream, IReadOnlyList<ActSpec> Acts);

    /// <summary>A log as it is built: the entries, in order, and what the construction intends for each act row.</summary>
    private sealed class BuiltLog(string subject, string stream)
    {
        internal string Subject { get; } = subject;

        internal string Stream { get; } = stream;

        internal List<byte[]> Entries { get; } = [];

        /// <summary>For each act row: its log index and the predicate the construction intends (null when it is meant to be honoured).</summary>
        internal List<(int Index, string? Predicate)> Intent { get; } = [];
    }

    private static T Require<T>(Curia.Domain.Primitives.Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static JsonValue.String S(string value) => new(value);

    private static JsonValue.Object O(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    private static byte[] Canonical(JsonValue value) => Require(CanonicalJson.Canonicalize(value)).Span.ToArray();

    /// <summary>One entry file: <c>log_index</c>, <c>leaf_hash</c> (RFC 9162's leaf over the entry's canonical form) and <c>entry</c>, written as canonical JSON.</summary>
    private static byte[] EntryFile(int index, string aggregate, string type, string actor, JsonValue payload)
    {
        var entry = O(
            ("actor_id", S(actor)),
            ("aggregate_id", S(aggregate)),
            ("event_id", S($"01K4CQ4A{index:D18}")),
            ("event_type", S(type)),
            ("payload", payload),
            ("server_ts", S($"2026-09-05T10:{index / 60:D2}:{index % 60:D2}.000000Z")));
        var leaf = SHA256.HashData([0, .. Canonical(entry)]);
        return Canonical(O(("entry", entry), ("leaf_hash", S("sha256:" + Convert.ToHexStringLower(leaf))), ("log_index", new JsonValue.Number(index))));
    }

    private static JsonValue.Object Replace(JsonValue.Object source, string name, JsonValue value) =>
        new([.. source.Members.Select(m => m.Key == name ? new KeyValuePair<string, JsonValue>(name, value) : m)]);

    /// <summary>An authorization the generator composes itself: header <c>typ</c> as given, <c>kid</c> as claimed, a signature from whichever signer is passed (so a header can name one key and carry another's signature).</summary>
    private static string Authorize(string statementText, string claimedKid, InMemoryOperatorActSigner signer, string headerTyp)
    {
        var header = JsonSerializer.Serialize(new { alg = "ES256", kid = claimedKid, typ = headerTyp, b64 = false, crit = Crit });
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header));
        var signingInput = Encoding.UTF8.GetBytes(encoded + ".").Concat(Encoding.UTF8.GetBytes(statementText)).ToArray();
        return $"{encoded}..{Base64Url.EncodeToString(Require(signer.SignStatement(signingInput)))}";
    }

    private static BuiltLog Build(LogSpec spec, InMemoryOperatorActSigner first, InMemoryOperatorActSigner second, InMemoryOperatorActSigner stranger)
    {
        var log = new BuiltLog(spec.IsPostStream ? Post : Agent, spec.IsPostStream ? "post" : "agent");
        var jwk = O(("alg", S("EdDSA")), ("crv", S("Ed25519")), ("kid", S("conformance-ed25519-minimal")), ("kty", S("OKP")), ("x", S("HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8")));
        void Add(string aggregate, string type, string actor, JsonValue payload) => log.Entries.Add(EntryFile(log.Entries.Count, aggregate, type, actor, payload));

        Add(Agent, "agent.enrolled", Agent, O(("agent_id", S(Agent)), ("kid", S("conformance-ed25519-minimal"))));
        Add(Agent, "agent.key-bound", Agent, O(("agent_id", S(Agent)), ("jwk", jwk), ("kid", S("conformance-ed25519-minimal"))));
        Add("log:operator-keys", "log.operator-key", "operator", O(("jwk", first.PublicJwk()), ("kid", S(first.Kid)), ("valid_from", S("2026-09-01T00:00:00Z"))));
        if (spec.IsPostStream)
            Add(Post, "post.accepted", Agent, O(("digest", S(Digest)), ("post_id", S(Post))));

        // The stream's root is its first event: agent.enrolled at 0, or post.accepted after the log's three key entries.
        long lastHonoured = spec.IsPostStream ? 3 : 0;
        var secondPublished = false;

        foreach (var act in spec.Acts)
        {
            if (act.Fault == 7 && !secondPublished)
            {
                Add("log:operator-keys", "log.operator-key", "operator", O(("jwk", second.PublicJwk()), ("kid", S(second.Kid)), ("valid_from", S("2026-09-02T00:00:00Z"))));
                secondPublished = true;
            }

            var index = log.Entries.Count;
            var prev = act.Fault == 4 ? 999 : lastHonoured;
            var signed = spec.IsPostStream
                ? Require(OperatorActStatements.SignModeration(
                    Actor,
                    act.Variant == 1 ? ["01K4CQ4B0000000000000000F1"] : [],
                    Categories[act.Variant],
                    Digest,
                    Effects[act.Variant],
                    Post,
                    prev,
                    "moderated by the cross-fold corpus",
                    first))
                : Require(OperatorActStatements.SignAttestation(Actor, Agent, "manual", Owners[act.Variant], act.Variant % 2 == 0, prev, "owner verified by the cross-fold corpus", first));

            var statementObject = signed.Members;
            var statementText = signed.Statement;
            var authorization = signed.Authorization;
            var row = statementObject;

            switch (act.Fault)
            {
                case 1: // act-unsigned: the members of a genuine row, no statement and no authorization.
                    log.Entries.Add(EntryFile(index, log.Subject, RowType(spec), Actor, row));
                    log.Intent.Add((index, Intended[1]));
                    continue;
                case 2: // act-statement-mismatch: the row states a member the statement does not.
                    row = Replace(statementObject, spec.IsPostStream ? "rationale" : "owner_id", S("not what the statement says"));
                    break;
                case 3: // act-unknown-kind: a statement version the Forum does not know, signed as it is.
                    (statementObject, statementText) = Restated(statementObject, "typ", S(spec.IsPostStream ? "curia.moderation/9" : "curia.owner-attestation/9"));
                    row = statementObject;
                    authorization = Authorize(statementText, first.Kid, first, "curia-operator-act+jws");
                    break;
                case 4: // act-broken-link: a prev that names no honoured row; the statement is restated and signed as it is.
                    authorization = signed.Authorization;
                    break;
                case 5: // act-typ-mismatch: a signature of the right key under another header typ.
                    authorization = Authorize(statementText, first.Kid, first, "curia-head+jws");
                    break;
                case 6: // act-key-unpublished: a key the log never published.
                    authorization = Authorize(statementText, stranger.Kid, stranger, "curia-operator-act+jws");
                    break;
                case 7: // act-key-not-first: a second published operator key.
                    authorization = Authorize(statementText, second.Kid, second, "curia-operator-act+jws");
                    break;
                case 8: // act-signature-invalid: the right header, another key's signature bytes.
                    authorization = Authorize(statementText, first.Kid, stranger, "curia-operator-act+jws");
                    break;
            }

            var payload = new JsonValue.Object([.. row.Members, new("statement", S(statementText)), new("authorization", S(authorization))]);
            log.Entries.Add(EntryFile(index, log.Subject, RowType(spec), Actor, payload));
            log.Intent.Add((index, Intended[act.Fault]));
            if (act.Fault == 0)
                lastHonoured = index;
        }

        return log;
    }

    private static string RowType(LogSpec spec) => spec.IsPostStream ? "moderation.applied" : "agent.owner-attested";

    private static (JsonValue.Object Members, string Text) Restated(JsonValue.Object members, string name, JsonValue value)
    {
        var changed = Replace(members, name, value);
        return (changed, Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(changed)).Span));
    }

    private static List<Curia.Domain.AppendedEvent> Read(BuiltLog log) =>
        [.. log.Entries.Select((bytes, i) => Curia.Application.Tests.Credentials.ConformanceLog.Event(i, bytes))];

    /// <summary>
    /// The case written to <paramref name="directory"/> in the vector shape, its <c>expected.json</c> from the C# fold's decisions. Returns the construction's disagreements with the fold
    /// (the generator's intent against the fold's verdict, row by row), each naming the case and the row.
    /// </summary>
    private static List<string> Write(string directory, string name, BuiltLog log)
    {
        Directory.CreateDirectory(Path.Combine(directory, "entries"));
        for (var i = 0; i < log.Entries.Count; i++)
            File.WriteAllBytes(Path.Combine(directory, "entries", $"{i}.json"), log.Entries[i]);

        File.WriteAllText(
            Path.Combine(directory, "meta.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["profile"] = "operator-acts-log",
                ["requirement"] = "R6.67",
                ["stream"] = log.Stream,
                ["subject"] = log.Subject,
                ["note"] = $"OAS29 generated case {name}: a generated log decided by both folds.",
            }));

        var events = Read(log);
        var verified = ActVectors.Verified(events);
        var decisions = ActVectors.Decide(events).Where(d => d.Aggregate == log.Subject).ToArray();

        var disagreements = new List<string>();
        if (decisions.Length != log.Intent.Count)
            disagreements.Add($"{name}: the construction made {log.Intent.Count} act rows, the fold decided {decisions.Length}");
        foreach (var ((index, intended), decided) in log.Intent.Zip(decisions))
            if (decided.LogIndex != index || decided.Predicate != intended)
                disagreements.Add($"{name} row {index}: the construction intends {intended ?? "honoured"}, the C# fold decides {decided.Predicate ?? "honoured"} at {decided.LogIndex}");

        var rows = decisions.Select(d => d.Predicate is null
            ? (object)new Dictionary<string, object> { ["log_index"] = d.LogIndex, ["type"] = d.Type, ["verdict"] = "honoured" }
            : new Dictionary<string, object> { ["log_index"] = d.LogIndex, ["type"] = d.Type, ["verdict"] = "skipped", ["predicate"] = d.Predicate }).ToArray();

        var honoured = verified.HonouredEvents.Events;
        object state;
        if (log.Stream == "agent")
        {
            AgentStandingProjector.Fold(honoured).TryGetValue(log.Subject, out var standing);
            state = new Dictionary<string, object?> { ["owner"] = standing?.OwnerId, ["verified"] = standing?.OwnerVerified ?? false };
        }
        else
        {
            var moderation = FlagProjector.Fold(honoured);
            state = new Dictionary<string, object?> { ["served"] = !moderation.TryGetValue(log.Subject, out var post) || post.MayServe };
        }

        File.WriteAllText(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["rows"] = rows, ["state"] = state }));
        return disagreements;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary><c>cargo test --locked --test operator_acts</c> over <paramref name="corpus"/>: its exit code and everything it printed. cargo absent is a failure, never a skip.</summary>
    private static (int Exit, string Output) RunRust(string corpus)
    {
        var start = new ProcessStartInfo("cargo")
        {
            WorkingDirectory = Path.Combine(RepoRoot(), "rust", "curia-testis"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "test", "--locked", "--test", "operator_acts", "--", "--nocapture" }) start.ArgumentList.Add(a);
        start.Environment["CURIA_OPERATOR_ACTS_DIR"] = corpus;

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception e)
        {
            Assert.Fail($"cargo is not on PATH ({e.Message}): OAS29's second implementation is the property under test, so this fails and never skips");
            throw;
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(15)))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("cargo test --test operator_acts did not finish in 15 minutes");
            }

            return (process.ExitCode, stdout.Result + Environment.NewLine + stderr.Result);
        }
    }

    [Fact]
    public void OAS29_TheCSharpAndTheRustFoldsDecideAGeneratedCorpusAlike()
    {
        // One value from a fixed seed, so that the corpus is the same on every run: a Sample over a seed draws a fresh seed per iteration.
        var specs = Gen.Select(Gen.Bool, Gen.Select(Gen.Int[0, 8], Gen.Int[0, 3]).List[1, 8])
            .Select(t => new LogSpec(t.Item1, [.. t.Item2.Select(a => new ActSpec(a.Item1, a.Item2))]))
            .List[Cases, Cases]
            .Single(_ => true, Seed);
        Assert.Equal(Cases, specs.Count);

        var corpus = Path.Combine(Path.GetTempPath(), "curia-oas29-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new InMemoryOperatorActSigner("oas29-operator-key");
            using var second = new InMemoryOperatorActSigner("oas29-second-operator-key");
            using var stranger = new InMemoryOperatorActSigner("oas29-unpublished-key");

            var disagreements = new List<string>();
            for (var i = 0; i < specs.Count; i++)
            {
                var name = $"case-{i:D4}";
                disagreements.AddRange(Write(Path.Combine(corpus, name), name, Build(specs[i], first, second, stranger)));
            }

            Assert.True(disagreements.Count == 0, $"{disagreements.Count} rows the construction and the C# fold decide differently:{Environment.NewLine}{string.Join(Environment.NewLine, disagreements.Take(20))}");

            // The guards, counted from the files written.
            var cases = Directory.EnumerateDirectories(corpus).ToArray();
            Assert.True(cases.Length == Cases, $"Expected: {Cases} cases  Actual: {cases.Length}");
            var predicates = new Dictionary<string, int>(StringComparer.Ordinal);
            var fullyHonoured = 0;
            foreach (var dir in cases)
            {
                using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "expected.json")));
                var rows = expected.RootElement.GetProperty("rows").EnumerateArray().ToArray();
                if (rows.Length > 0 && rows.All(r => r.GetProperty("verdict").GetString() == "honoured")) fullyHonoured++;
                foreach (var r in rows.Where(r => r.TryGetProperty("predicate", out _)))
                {
                    var p = r.GetProperty("predicate").GetString()!;
                    predicates[p] = predicates.GetValueOrDefault(p) + 1;
                }
            }

            var histogram = string.Join(", ", OperatorActPredicates.All.Select(p => $"{p} {predicates.GetValueOrDefault(p)}")) + $", fully honoured logs {fullyHonoured}";
            Assert.True(OperatorActPredicates.All.All(p => predicates.GetValueOrDefault(p) > 0), "a predicate has no row in the corpus: " + histogram);
            Assert.True(fullyHonoured > 0, "no log is fully honoured: " + histogram);
            Assert.Equal(OperatorActPredicates.All.Length, predicates.Count);

            var (exit, output) = RunRust(corpus);
            var tail = string.Join(Environment.NewLine, output.Split('\n').TakeLast(40));
            Assert.True(exit == 0, $"cargo test --test operator_acts exited {exit}; corpus {histogram}{Environment.NewLine}{tail}");

            var count = Regex.Match(output, @"^operator-acts cases decided: (\d+)\s*$", RegexOptions.Multiline);
            Assert.True(count.Success, $"the Rust test printed no count line:{Environment.NewLine}{tail}");
            Assert.Equal(cases.Length, int.Parse(count.Groups[1].Value, CultureInfo.InvariantCulture));
            TestContext.Current.TestOutputHelper?.WriteLine($"OAS29: {cases.Length} cases; Rust decided {count.Groups[1].Value}; {histogram}");
        }
        finally
        {
            if (Directory.Exists(corpus)) Directory.Delete(corpus, recursive: true);
        }
    }
}
