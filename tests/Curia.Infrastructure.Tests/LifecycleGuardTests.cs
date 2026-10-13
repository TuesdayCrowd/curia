using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Domain.Primitives;
using Curia.Infrastructure.Lifecycle;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R4.38 (add.), spec section 4.13: the lifecycle approval record and the gate's opt-in (lock 1 and lock
/// 2). Every fact touching <see cref="LifecycleApprovalRecord"/> or <see cref="EmbeddedLifecycleAppendGate"/>
/// lives here, where <c>Curia.Infrastructure</c>'s internals are visible (CS-5). GF6 and the public
/// factory's tripwire are in <c>Curia.Architecture.Tests/LifecycleGateFenceTests.cs</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleGuardTests
{
    private const string Decision =
        "\"decision\":{\"words\":\"Approved.\",\"date\":\"2026-10-11\",\"place\":\"the lift PR's review\"}";

    private const string MemberNames =
        "\"member_name_sign_off\":{\"by\":\"owner\",\"date\":\"2026-10-11\",\"confidence\":\"high\",\"statement\":\"the member names and event types\"}";

    private const string Security =
        "\"security_sign_off\":{\"by\":\"cybersecurity\",\"date\":\"2026-10-11\",\"confidence\":\"high\",\"statement\":\"the formats; hopper-doctrine gates the first append\"}";

    private const string SecurityMedium =
        "\"security_sign_off\":{\"by\":\"cybersecurity\",\"date\":\"2026-10-11\",\"confidence\":\"medium\",\"statement\":\"the formats; hopper-doctrine gates the first append\"}";

    private static readonly string Digest = new('a', 64);

    private static Error RequireError<T>(Result<T> result) =>
        result.Match(_ => throw new InvalidOperationException("expected a failure"), e => e);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static string Approval(string kind, string eventType, string directory) =>
        $"{{\"kind\":\"{kind}\",\"event_type\":\"{eventType}\",\"vector_directory\":\"{directory}\",\"canonical_sha256\":\"{Digest}\",\"leaf_sha256\":\"{Digest}\"}}";

    private static readonly string CompromiseApproval = Approval("compromise", "agent.key-compromised", "key-compromised-entry");
    private static readonly string RotationApproval = Approval("rotation", "agent.key-bound", "key-rotated-entry");
    private static readonly string RetirementApproval = Approval("retirement", "agent.key-retired", "key-retired-entry");

    private static Result<LifecycleApprovalRecord> ParseRecord(string approved, params string[] items)
    {
        var members = new List<string> { "\"version\":1", $"\"approved\":[{approved}]" };
        members.AddRange(items);
        return LifecycleApprovalRecord.Parse(Encoding.UTF8.GetBytes("{" + string.Join(",", members) + "}"));
    }

    private static OwnerDecision TestDecision => new("Approved.", "2026-10-11", "the lift PR's review");

    private static SignOff TestSignOff => new("cybersecurity", "2026-10-11", "high", "the formats; hopper-doctrine gates the first append");

    private static LifecycleApprovalRecord TestRecord(params ApprovedKind[] approved) =>
        new(1, [.. approved], TestDecision, TestSignOff, TestSignOff);

    private static string Sha256Of(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Report 2 Lock 1's build-time check (spec section 4.13, "Lifting it"; errata G21, "The guard"): the
    /// embedded record's pinned digests are recomputed from <c>conformance/acta/</c>. Vacuously green while
    /// <c>approved</c> is empty; it binds at the lift, when the record first approves a kind.
    /// </summary>
    [Fact]
    public void R4_38_TheEmbeddedRecordsDigestsMatchConformance() =>
        AssertDigestsMatch(LifecycleApprovalRecord.Embedded(), Path.Combine(RepoRoot(), "conformance", "acta"));

    private static void AssertDigestsMatch(Result<LifecycleApprovalRecord> record, string actaRoot)
    {
        Assert.True(record.IsOk, "the embedded approval record does not read");
        var verified = Require(record).VerifyDigests(actaRoot);
        Assert.True(verified.IsOk, verified.Match(_ => string.Empty, e => $"{e.Type}: {e.Detail}"));
    }

    [Fact]
    public void R4_38_ANonEmptyApprovalWithoutItsDecisionOrSignOffsIsRefused()
    {
        var withoutDecision = RequireError(ParseRecord(CompromiseApproval, MemberNames, Security));
        Assert.Equal(LifecycleApprovalErrors.ItemMissingType, withoutDecision.Type);
        Assert.Equal("decision", withoutDecision.Detail);

        var withoutMemberNames = RequireError(ParseRecord(CompromiseApproval, Decision, Security));
        Assert.Equal(LifecycleApprovalErrors.ItemMissingType, withoutMemberNames.Type);
        Assert.Equal("member_name_sign_off", withoutMemberNames.Detail);

        var withoutSecurity = RequireError(ParseRecord(CompromiseApproval, Decision, MemberNames));
        Assert.Equal(LifecycleApprovalErrors.ItemMissingType, withoutSecurity.Type);
        Assert.Equal("security_sign_off", withoutSecurity.Detail);

        var atMedium = RequireError(ParseRecord(CompromiseApproval, Decision, MemberNames, SecurityMedium));
        Assert.Equal(LifecycleApprovalErrors.ConfidenceNotHighType, atMedium.Type);
        Assert.Equal("security_sign_off.confidence", atMedium.Detail);

        // A present item with an empty member is refused, naming the member.
        var emptyWords = RequireError(ParseRecord(
            CompromiseApproval, Decision.Replace("Approved.", string.Empty, StringComparison.Ordinal), MemberNames, Security));
        Assert.Equal(LifecycleApprovalErrors.ItemMissingType, emptyWords.Type);
        Assert.Equal("decision.words", emptyWords.Detail);

        // Controls: all three present, (e) at high, parses; and the shipped shape parses with all three absent.
        var complete = Require(ParseRecord(CompromiseApproval, Decision, MemberNames, Security));
        Assert.Equal([LifecycleKind.Compromise], complete.Approved.Select(a => a.Kind));

        var empty = Require(LifecycleApprovalRecord.Parse("{\"version\":1,\"approved\":[]}"u8));
        Assert.Empty(empty.Approved);
        Assert.Null(empty.Decision);
        Assert.Null(empty.MemberNameSignOff);
        Assert.Null(empty.SecuritySignOff);
    }

    /// <summary>GF8, the record's half: Cybersecurity Report 2's Lock 2 and its Q3 both state the rule.</summary>
    [Fact]
    public void R4_38_TheApprovalRecordRefusesRotationOrRetirementWithoutCompromise()
    {
        var rotationAlone = RequireError(ParseRecord(RotationApproval, Decision, MemberNames, Security));
        Assert.Equal(LifecycleApprovalErrors.CompromiseOrderType, rotationAlone.Type);

        var retirementAlone = RequireError(ParseRecord(RetirementApproval, Decision, MemberNames, Security));
        Assert.Equal(LifecycleApprovalErrors.CompromiseOrderType, retirementAlone.Type);

        var withCompromise = Require(ParseRecord(CompromiseApproval + "," + RotationApproval, Decision, MemberNames, Security));
        Assert.Equal([LifecycleKind.Compromise, LifecycleKind.Rotation], withCompromise.Approved.Select(a => a.Kind));
    }

    [Fact]
    public void R4_38_AMissingApprovalRecordFailsClosed()
    {
        Assert.False(LifecycleApprovalRecord.Parse([]).IsOk);
        Assert.False(LifecycleApprovalRecord.Parse("{}"u8).IsOk);

        var assembly = typeof(LifecycleApprovalRecord).Assembly;
        var missing = RequireError(LifecycleApprovalRecord.Embedded(assembly, "does-not-exist.json"));
        Assert.Equal(LifecycleApprovalErrors.RecordMissingType, missing.Type);

        var noGate = RequireError(EmbeddedLifecycleAppendGate.FromEmbedded(assembly, "does-not-exist.json", "none"));
        Assert.Equal(LifecycleApprovalErrors.RecordMissingType, noGate.Type);

        // Control: the same seam with the real resource name reads the shipped record.
        Assert.True(LifecycleApprovalRecord.Embedded(assembly, LifecycleApprovalRecord.ResourceName).IsOk);
        Assert.True(EmbeddedLifecycleAppendGate.FromEmbedded(assembly, LifecycleApprovalRecord.ResourceName, "none").IsOk);
    }

    /// <summary>
    /// GF7. Task 1.1's <c>conformance/acta/key-rotated-entry/</c> does not exist yet, so a synthetic
    /// rotation-shaped vector is written to a temporary root: a fixture of this fact only, not a
    /// conformance vector.
    /// </summary>
    [Fact]
    public void R4_38_TheApprovalDigestCheckFailsOnAOneByteChange()
    {
        var root = Directory.CreateTempSubdirectory("curia-lifecycle-gf7-").FullName;
        try
        {
            var rotated = WriteSyntheticRotation(root, "key-rotated-entry", "agent.key-bound");
            var record = TestRecord(new ApprovedKind(
                LifecycleKind.Rotation, "agent.key-bound", "key-rotated-entry",
                Sha256Of(Path.Combine(rotated, "expected.canonical")), Sha256Of(Path.Combine(rotated, "expected.leaf"))));

            Assert.True(record.VerifyDigests(root).IsOk);

            var leafPath = Path.Combine(rotated, "expected.leaf");
            var leaf = File.ReadAllBytes(leafPath);
            leaf[0] ^= 0x01;
            File.WriteAllBytes(leafPath, leaf);

            var changed = RequireError(record.VerifyDigests(root));
            Assert.Equal(LifecycleApprovalErrors.VectorDigestType, changed.Type);
            Assert.StartsWith("rotation", changed.Detail, StringComparison.Ordinal);

            // A directory the root does not hold (FL-24).
            var absent = TestRecord(new ApprovedKind(LifecycleKind.Rotation, "agent.key-bound", "absent-entry", Digest, Digest));
            var absentError = RequireError(absent.VerifyDigests(root));
            Assert.Equal(LifecycleApprovalErrors.VectorMissingType, absentError.Type);
            Assert.StartsWith("rotation", absentError.Detail, StringComparison.Ordinal);

            // An input.json of another event type, its digests matching its own directory (FL-25).
            var other = WriteSyntheticRotation(root, "other-type-entry", "agent.key-retired");
            var otherType = TestRecord(new ApprovedKind(
                LifecycleKind.Rotation, "agent.key-bound", "other-type-entry",
                Sha256Of(Path.Combine(other, "expected.canonical")), Sha256Of(Path.Combine(other, "expected.leaf"))));
            var otherTypeError = RequireError(otherType.VerifyDigests(root));
            Assert.Equal(LifecycleApprovalErrors.VectorEventTypeType, otherTypeError.Type);
            Assert.Equal("rotation", otherTypeError.Detail);

            // Enrolment's pair's shape under another name, its digests matching the copy (FL-26).
            var enrolment = Path.Combine(root, "enrolment-copy");
            Directory.CreateDirectory(enrolment);
            var source = Path.Combine(RepoRoot(), "conformance", "acta", "key-bound-entry");
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(enrolment, Path.GetFileName(file)));
            var enrolmentShape = TestRecord(new ApprovedKind(
                LifecycleKind.Rotation, "agent.key-bound", "enrolment-copy",
                Sha256Of(Path.Combine(enrolment, "expected.canonical")), Sha256Of(Path.Combine(enrolment, "expected.leaf"))));
            var enrolmentError = RequireError(enrolmentShape.VerifyDigests(root));
            Assert.Equal(LifecycleApprovalErrors.VectorEnrolmentShapeType, enrolmentError.Type);
            Assert.Equal("rotation", enrolmentError.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // Over Parse (FL-19): each record otherwise valid, and each approving compromise, so neither is
        // refused by the compromise-order rule instead.
        var enrolmentDirectory = RequireError(ParseRecord(
            CompromiseApproval + "," + Approval("rotation", "agent.key-bound", "key-bound-entry"), Decision, MemberNames, Security));
        Assert.Equal(LifecycleApprovalErrors.VectorDirectoryRefusedType, enrolmentDirectory.Type);
        Assert.Equal("rotation", enrolmentDirectory.Detail);

        var wrongEventType = RequireError(ParseRecord(
            Approval("compromise", "agent.key-bound", "key-compromised-entry"), Decision, MemberNames, Security));
        Assert.Equal(LifecycleApprovalErrors.EventTypeNotTheKindsType, wrongEventType.Type);
        Assert.Equal("compromise", wrongEventType.Detail);
    }

    /// <summary>
    /// An approved kind's vector directory is one lowercase name, an allowlist (FL-30); enrolment's own
    /// directory is refused in any case. Each record is otherwise valid and approves compromise.
    /// </summary>
    [Fact]
    public void R4_38_AVectorDirectoryIsOneLowercaseName()
    {
        string[] refused =
        [
            "Key-Bound-Entry", "KEY-BOUND-ENTRY", ".", "key-bound-entry ", "key-bound-entry.", "x:y", "-a", "a-", string.Empty,
            new string('a', 65),
        ];

        foreach (var directory in refused)
        {
            var result = ParseRecord(
                CompromiseApproval + "," + Approval("rotation", "agent.key-bound", directory), Decision, MemberNames, Security);
            var error = RequireError(result);
            Assert.True(
                LifecycleApprovalErrors.VectorDirectoryRefusedType == error.Type,
                $"vector_directory \"{directory}\" was not refused as a vector directory: {error.Type}");
        }

        var control = Require(ParseRecord(
            CompromiseApproval + "," + Approval("rotation", "agent.key-bound", "key-rotated-entry"), Decision, MemberNames, Security));
        Assert.NotNull(control);
    }

    /// <summary>GF8, lock 2's order with the approval check out of the way (FL-9).</summary>
    [Fact]
    public void R4_38_CreateRefusesRotationWithoutCompromiseOverAnApprovingRecord()
    {
        var record = TestRecord(
            new ApprovedKind(LifecycleKind.Compromise, "agent.key-compromised", "key-compromised-entry", Digest, Digest),
            new ApprovedKind(LifecycleKind.Rotation, "agent.key-bound", "key-rotated-entry", Digest, Digest));

        var rotationAlone = RequireError(EmbeddedLifecycleAppendGate.Create(record, "rotation"));
        Assert.Equal(LifecycleApprovalErrors.CompromiseOrderType, rotationAlone.Type);

        var gate = Require(EmbeddedLifecycleAppendGate.Create(record, "compromise,rotation"));
        Assert.True(gate.Enables(LifecycleKind.Compromise));
        Assert.True(gate.Enables(LifecycleKind.Rotation));
        Assert.False(gate.Enables(LifecycleKind.Retirement));
    }

    /// <summary>
    /// A rotation-shaped vector: an <c>input.json</c> whose payload carries the rotation form's members
    /// (spec section 4.6, <c>statement</c> among them), with an <c>expected.canonical</c> and an
    /// <c>expected.leaf</c> beside it. Its values are this fixture's own, not a computed leaf.
    /// </summary>
    private static string WriteSyntheticRotation(string root, string name, string eventType)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var input =
            "{\"actor_id\":\"agent://curia.example/synthetic\",\"aggregate_id\":\"agent://curia.example/synthetic\"," +
            "\"event_id\":\"01K4CQ1V9P3R5T7W9Y1A3C5E7H\",\"event_type\":\"" + eventType + "\"," +
            "\"payload\":{\"agent_id\":\"agent://curia.example/synthetic\",\"authorization\":\"e30..c2ln\"," +
            "\"authorized_by\":\"old-kid\",\"jwk\":{\"crv\":\"Ed25519\",\"kid\":\"new-kid\",\"kty\":\"OKP\",\"x\":\"AAAA\"}," +
            "\"kid\":\"new-kid\",\"possession\":\"e30..c2ln\",\"prev\":1,\"statement\":\"{}\"}," +
            "\"server_ts\":\"2026-10-11T12:00:00.000000Z\"}";
        File.WriteAllText(Path.Combine(directory, "input.json"), input);
        File.WriteAllText(Path.Combine(directory, "expected.canonical"), input);
        File.WriteAllText(
            Path.Combine(directory, "expected.leaf"),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic leaf " + name))) + "\n");
        return directory;
    }

    /// <summary>The repository root: the first ancestor of the test's output holding a <c>conformance</c> directory.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'conformance' directory) above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// The production gate, <c>EmbeddedLifecycleAppendGate</c> over the shipped (empty) record with the opt-in
/// <c>none</c>: what GF3's and GF4's production-gate runs hand the store.
/// </summary>
internal static class ProductionGate
{
    internal static ILifecycleAppendGate Build() =>
        EmbeddedLifecycleAppendGate.FromEmbedded("none")
            .Match(g => (ILifecycleAppendGate)g, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));
}

/// <summary>GF3 and GF4 against the in-memory store, under the production gate.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovers concrete test classes only when public; the facts are inherited.")]
public sealed class EmbeddedGateInMemoryLifecycleRefusalTests : InMemoryLifecycleRefusalContract
{
    protected override ILifecycleAppendGate RefusingGate() => ProductionGate.Build();
}

/// <summary>GF3 and GF4 against <see cref="PostgresEventStore"/>, under the production gate.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovers concrete test classes only when public; the facts are inherited.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class EmbeddedGatePostgresLifecycleRefusalTests(PostgresDatabaseFixture fixture) : LifecycleRefusalContract
{
    protected override ILifecycleAppendGate RefusingGate() => ProductionGate.Build();

    protected override IEventStore CreateStore(ILifecycleAppendGate gate)
    {
        var schema = fixture.CreateIsolatedSchemaAsync().GetAwaiter().GetResult();
        return new PostgresEventStore(fixture.AppRoleDataSource, TimeProvider.System, gate, schema);
    }
}

/// <summary>The positive control against <see cref="PostgresEventStore"/>: the five appends land under the permissive gate.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovers concrete test classes only when public; the facts are inherited.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PermissiveGatePostgresLifecycleTests(PostgresDatabaseFixture fixture) : LifecyclePermissiveContract
{
    protected override ILifecycleAppendGate PermissiveGate() => fixture.PermissiveLifecycleGate();

    protected override IEventStore CreateStore(ILifecycleAppendGate gate)
    {
        var schema = fixture.CreateIsolatedSchemaAsync().GetAwaiter().GetResult();
        return new PostgresEventStore(fixture.AppRoleDataSource, TimeProvider.System, gate, schema);
    }
}

/// <summary>
/// R4.38 (add.): the permissive gate is handed out only for the database the fixture created in this run.
/// Red when the check is removed.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PermissiveGateDatabaseGuardTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public void R4_38_APermissiveCompositionRefusesADatabaseItDidNotCreate()
    {
        using var retained = Npgsql.NpgsqlDataSource.Create("Host=198.51.100.10;Database=curia_retained;Username=nobody");

        Assert.Throws<InvalidOperationException>(() => fixture.PermissiveLifecycleGateFor(retained));
    }

    [Fact]
    public void R4_38_APermissiveCompositionAcceptsTheDatabaseItCreated() =>
        Assert.NotNull(fixture.PermissiveLifecycleGate());
}
