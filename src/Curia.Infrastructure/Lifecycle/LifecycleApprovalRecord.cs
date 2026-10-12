using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Curia.Application.Ports;
using Curia.AuthN.Dpop;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Infrastructure.Lifecycle;

/// <summary>
/// R4.38 (add.): the lifecycle approval record embedded in this assembly
/// (<c>Lifecycle/lifecycle-approval.json</c>). Its contents are spec section 4.13's "Lifting it" and the
/// cybersecurity rulings' Report 2, Lock 1 (a)-(e): (a) <see cref="Decision"/>, (b) the kinds in
/// <see cref="Approved"/>, (c) each <see cref="ApprovedKind"/>'s event type, vector directory and
/// digests, (d) <see cref="MemberNameSignOff"/>, (e) <see cref="SecuritySignOff"/>. The shipped record is
/// <c>{"version":1,"approved":[]}</c>, which approves nothing, until the owner lifts the guard.
///
/// <para>A sealed class, not a record: it needs no value equality and no <c>with</c>, and a record would
/// emit <c>&lt;Clone&gt;$</c>, which calls a compiler-generated copy constructor and would be a second
/// constructor caller the IL fact finds on a correct tree. Its one constructor is the internal one below,
/// and its properties are get-only, so no copy is built either. Within <c>src/</c> and <c>tools/</c>,
/// <see cref="Parse"/> is the constructor's only caller (LifecycleGateFenceTests holds it).</para>
/// </summary>
public sealed class LifecycleApprovalRecord
{
    /// <summary>The embedded resource's manifest name, the only literal in <c>src/</c> naming it.</summary>
    internal const string ResourceName = "Curia.Infrastructure.Lifecycle.lifecycle-approval.json";

    private const string RotationEventType = "agent.key-bound";
    private const string RetirementEventType = "agent.key-retired";
    private const string CompromiseEventType = "agent.key-compromised";

    /// <summary>Enrolment's frozen pair (errata G16) is never a lifecycle kind's evidence.</summary>
    private const string EnrolmentVectorDirectory = "key-bound-entry";

    private static readonly ImmutableHashSet<string> RootMembers =
        ["version", "approved", "decision", "member_name_sign_off", "security_sign_off"];

    private static readonly ImmutableHashSet<string> ApprovedMembers =
        ["kind", "event_type", "vector_directory", "canonical_sha256", "leaf_sha256"];

    private static readonly ImmutableHashSet<string> DecisionMembers = ["words", "date", "place"];

    private static readonly ImmutableHashSet<string> SignOffMembers = ["by", "date", "confidence", "statement"];

    internal LifecycleApprovalRecord(
        int version,
        ImmutableArray<ApprovedKind> approved,
        OwnerDecision? decision,
        SignOff? memberNameSignOff,
        SignOff? securitySignOff)
    {
        Version = version;
        Approved = approved;
        Decision = decision;
        MemberNameSignOff = memberNameSignOff;
        SecuritySignOff = securitySignOff;
    }

    public int Version { get; }

    public ImmutableArray<ApprovedKind> Approved { get; }

    public OwnerDecision? Decision { get; }

    public SignOff? MemberNameSignOff { get; }

    public SignOff? SecuritySignOff { get; }

    /// <summary>The record this build carries. A missing or unparsable resource fails, named; it is never read as empty.</summary>
    public static Result<LifecycleApprovalRecord> Embedded() =>
        Embedded(typeof(LifecycleApprovalRecord).Assembly, ResourceName);

    /// <summary>The seam <see cref="Embedded()"/> calls: a missing or unparsable resource fails, named.</summary>
    internal static Result<LifecycleApprovalRecord> Embedded(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return Result<LifecycleApprovalRecord>.Fail(LifecycleApprovalErrors.RecordMissing(resourceName));

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }

    /// <summary>
    /// Reads a record through the guarded JSON reader (R11.34), never <c>System.Text.Json</c>, and refuses,
    /// by name: a document that is not the record's closed shape; an approved kind whose event type is
    /// not the kind's own, or whose vector directory is empty, holds a path separator or <c>..</c>, or is
    /// enrolment's <c>key-bound-entry</c>; rotation or retirement approved without compromise; a non-empty
    /// approval without (a), (d) or (e), or with any of their members missing or empty; and (e) at any
    /// confidence other than <c>high</c>.
    /// </summary>
    public static Result<LifecycleApprovalRecord> Parse(ReadOnlySpan<byte> json)
    {
        var parsed = JsonReader.Parse(json, AdmitLimits.Default);
        if (!parsed.TryGetValue(out var document, out var parseError))
            return Fail(LifecycleApprovalErrors.RecordUnreadable($"not JSON ({parseError!.Type})"));

        if (document is not JsonValue.Object root)
            return Fail(LifecycleApprovalErrors.RecordUnreadable("the record is not an object"));

        if (Unknown(root, RootMembers) is { } unknownRoot)
            return Fail(LifecycleApprovalErrors.RecordUnreadable($"unknown member {unknownRoot}"));

        if (Member(root, "version") is not JsonValue.Number { Value: 1 })
            return Fail(LifecycleApprovalErrors.RecordUnreadable("version is not 1"));

        if (Member(root, "approved") is not JsonValue.Array approvedArray)
            return Fail(LifecycleApprovalErrors.RecordUnreadable("approved is not an array"));

        var approved = ImmutableArray.CreateBuilder<ApprovedKind>();
        foreach (var item in approvedArray.Items)
        {
            var kind = ReadApprovedKind(item);
            if (!kind.TryGetValue(out var approvedKind, out var kindError))
                return Fail(kindError!);

            if (approved.Any(a => a.Kind == approvedKind.Kind))
                return Fail(LifecycleApprovalErrors.RecordUnreadable($"{KindName(approvedKind.Kind)} is approved twice"));

            approved.Add(approvedKind);
        }

        var kinds = approved.Select(a => a.Kind).ToImmutableHashSet();
        if ((kinds.Contains(LifecycleKind.Rotation) || kinds.Contains(LifecycleKind.Retirement))
            && !kinds.Contains(LifecycleKind.Compromise))
        {
            return Fail(LifecycleApprovalErrors.CompromiseOrder("the approval record"));
        }

        var decision = ReadDecision(root);
        if (!decision.TryGetValue(out var ownerDecision, out var decisionError))
            return Fail(decisionError!);

        var memberNames = ReadSignOff(root, "member_name_sign_off");
        if (!memberNames.TryGetValue(out var memberNameSignOff, out var memberNamesError))
            return Fail(memberNamesError!);

        var security = ReadSignOff(root, "security_sign_off");
        if (!security.TryGetValue(out var securitySignOff, out var securityError))
            return Fail(securityError!);

        if (approved.Count > 0)
        {
            if (ownerDecision is null)
                return Fail(LifecycleApprovalErrors.ItemMissing("decision"));
            if (memberNameSignOff is null)
                return Fail(LifecycleApprovalErrors.ItemMissing("member_name_sign_off"));
            if (securitySignOff is null)
                return Fail(LifecycleApprovalErrors.ItemMissing("security_sign_off"));
        }

        if (securitySignOff is not null && !string.Equals(securitySignOff.Confidence, "high", StringComparison.Ordinal))
            return Fail(LifecycleApprovalErrors.ConfidenceNotHigh());

        return Result<LifecycleApprovalRecord>.Ok(new LifecycleApprovalRecord(
            1, approved.ToImmutable(), ownerDecision, memberNameSignOff, securitySignOff));
    }

    /// <summary>
    /// Recomputes (c): for each approved kind, exactly <c>&lt;actaVectorsRoot&gt;/&lt;VectorDirectory&gt;</c>
    /// and no other directory. Fails, naming the kind, when that directory is missing; when its
    /// <c>input.json</c>'s <c>event_type</c> is not the kind's; when, for rotation, its payload carries no
    /// <c>statement</c> (enrolment's pair's shape); or when the sha256 of its <c>expected.canonical</c> or
    /// <c>expected.leaf</c> differs from the pinned digest.
    /// </summary>
    public Result<Unit> VerifyDigests(string actaVectorsRoot)
    {
        ArgumentNullException.ThrowIfNull(actaVectorsRoot);

        foreach (var approved in Approved)
        {
            var directory = Path.Combine(actaVectorsRoot, approved.VectorDirectory);
            if (!Directory.Exists(directory))
                return Result<Unit>.Fail(LifecycleApprovalErrors.VectorMissing(KindName(approved.Kind), approved.VectorDirectory));

            var inputPath = Path.Combine(directory, "input.json");
            if (!File.Exists(inputPath))
                return Result<Unit>.Fail(LifecycleApprovalErrors.VectorMissing(KindName(approved.Kind), "input.json"));

            if (!JsonReader.Parse(File.ReadAllBytes(inputPath), AdmitLimits.Default).TryGetValue(out var input, out _)
                || input is not JsonValue.Object inputObject)
            {
                return Result<Unit>.Fail(LifecycleApprovalErrors.VectorUnreadable(KindName(approved.Kind)));
            }

            if (Member(inputObject, "event_type") is not JsonValue.String eventType
                || !string.Equals(eventType.Value, approved.EventType, StringComparison.Ordinal))
            {
                return Result<Unit>.Fail(LifecycleApprovalErrors.VectorEventType(KindName(approved.Kind)));
            }

            if (approved.Kind == LifecycleKind.Rotation
                && (Member(inputObject, "payload") is not JsonValue.Object payload || Member(payload, "statement") is null))
            {
                return Result<Unit>.Fail(LifecycleApprovalErrors.VectorEnrolmentShape(KindName(approved.Kind)));
            }

            var canonical = DigestCheck(approved, directory, "expected.canonical", approved.CanonicalSha256);
            if (!canonical.IsOk)
                return canonical;

            var leaf = DigestCheck(approved, directory, "expected.leaf", approved.LeafSha256);
            if (!leaf.IsOk)
                return leaf;
        }

        return Result<Unit>.Ok(Unit.Value);
    }

    /// <summary>The opt-in's and the record's name for a kind.</summary>
    internal static string KindName(LifecycleKind kind) => kind switch
    {
        LifecycleKind.Rotation => "rotation",
        LifecycleKind.Retirement => "retirement",
        LifecycleKind.Compromise => "compromise",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a lifecycle kind"),
    };

    /// <summary>The kind a name names, or null.</summary>
    internal static LifecycleKind? KindNamed(string name) => name switch
    {
        "rotation" => LifecycleKind.Rotation,
        "retirement" => LifecycleKind.Retirement,
        "compromise" => LifecycleKind.Compromise,
        _ => null,
    };

    private static string OwnEventType(LifecycleKind kind) => kind switch
    {
        LifecycleKind.Rotation => RotationEventType,
        LifecycleKind.Retirement => RetirementEventType,
        LifecycleKind.Compromise => CompromiseEventType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a lifecycle kind"),
    };

    private static Result<Unit> DigestCheck(ApprovedKind approved, string directory, string file, string pinned)
    {
        var path = Path.Combine(directory, file);
        if (!File.Exists(path))
            return Result<Unit>.Fail(LifecycleApprovalErrors.VectorMissing(KindName(approved.Kind), file));

        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        return string.Equals(actual, pinned, StringComparison.Ordinal)
            ? Result<Unit>.Ok(Unit.Value)
            : Result<Unit>.Fail(LifecycleApprovalErrors.VectorDigest(KindName(approved.Kind), file));
    }

    private static Result<ApprovedKind> ReadApprovedKind(JsonValue item)
    {
        if (item is not JsonValue.Object entry)
            return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.RecordUnreadable("an approved entry is not an object"));

        if (Unknown(entry, ApprovedMembers) is { } unknown)
            return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.RecordUnreadable($"unknown approved member {unknown}"));

        foreach (var name in ApprovedMembers.Order(StringComparer.Ordinal))
        {
            if (Member(entry, name) is not JsonValue.String)
                return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.ItemMissing($"approved.{name}"));
        }

        var kindName = Text(entry, "kind");
        if (KindNamed(kindName) is not { } kind)
            return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.RecordUnreadable("an approved kind is not rotation, retirement or compromise"));

        var eventType = Text(entry, "event_type");
        if (!string.Equals(eventType, OwnEventType(kind), StringComparison.Ordinal))
            return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.EventTypeNotTheKinds(kindName));

        var directory = Text(entry, "vector_directory");
        if (directory.Length == 0
            || directory.Contains('/', StringComparison.Ordinal)
            || directory.Contains('\\', StringComparison.Ordinal)
            || directory.Contains("..", StringComparison.Ordinal)
            || string.Equals(directory, EnrolmentVectorDirectory, StringComparison.Ordinal))
        {
            return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.VectorDirectoryRefused(kindName));
        }

        var canonical = Text(entry, "canonical_sha256");
        var leaf = Text(entry, "leaf_sha256");
        if (!IsSha256Hex(canonical) || !IsSha256Hex(leaf))
            return Result<ApprovedKind>.Fail(LifecycleApprovalErrors.RecordUnreadable($"{kindName}'s digests are not 64 lowercase hex digits"));

        return Result<ApprovedKind>.Ok(new ApprovedKind(kind, eventType, directory, canonical, leaf));
    }

    private static Result<OwnerDecision?> ReadDecision(JsonValue.Object root)
    {
        if (Member(root, "decision") is not { } value)
            return Result<OwnerDecision?>.Ok(null);

        if (value is not JsonValue.Object decision)
            return Result<OwnerDecision?>.Fail(LifecycleApprovalErrors.RecordUnreadable("decision is not an object"));

        if (Unknown(decision, DecisionMembers) is { } unknown)
            return Result<OwnerDecision?>.Fail(LifecycleApprovalErrors.RecordUnreadable($"unknown decision member {unknown}"));

        foreach (var name in new[] { "words", "date", "place" })
        {
            if (Member(decision, name) is not JsonValue.String { Value.Length: > 0 })
                return Result<OwnerDecision?>.Fail(LifecycleApprovalErrors.ItemMissing($"decision.{name}"));
        }

        if (!IsIsoDate(Text(decision, "date")))
            return Result<OwnerDecision?>.Fail(LifecycleApprovalErrors.RecordUnreadable("decision.date is not an ISO 8601 date"));

        return Result<OwnerDecision?>.Ok(new OwnerDecision(Text(decision, "words"), Text(decision, "date"), Text(decision, "place")));
    }

    private static Result<SignOff?> ReadSignOff(JsonValue.Object root, string item)
    {
        if (Member(root, item) is not { } value)
            return Result<SignOff?>.Ok(null);

        if (value is not JsonValue.Object signOff)
            return Result<SignOff?>.Fail(LifecycleApprovalErrors.RecordUnreadable($"{item} is not an object"));

        if (Unknown(signOff, SignOffMembers) is { } unknown)
            return Result<SignOff?>.Fail(LifecycleApprovalErrors.RecordUnreadable($"unknown {item} member {unknown}"));

        foreach (var name in new[] { "by", "date", "confidence", "statement" })
        {
            if (Member(signOff, name) is not JsonValue.String { Value.Length: > 0 })
                return Result<SignOff?>.Fail(LifecycleApprovalErrors.ItemMissing($"{item}.{name}"));
        }

        if (!IsIsoDate(Text(signOff, "date")))
            return Result<SignOff?>.Fail(LifecycleApprovalErrors.RecordUnreadable($"{item}.date is not an ISO 8601 date"));

        return Result<SignOff?>.Ok(new SignOff(
            Text(signOff, "by"), Text(signOff, "date"), Text(signOff, "confidence"), Text(signOff, "statement")));
    }

    private static Result<LifecycleApprovalRecord> Fail(Error error) => Result<LifecycleApprovalRecord>.Fail(error);

    private static JsonValue? Member(JsonValue.Object value, string name)
    {
        foreach (var member in value.Members)
        {
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
                return member.Value;
        }

        return null;
    }

    private static string Text(JsonValue.Object value, string name) =>
        Member(value, name) is JsonValue.String text ? text.Value : string.Empty;

    private static string? Unknown(JsonValue.Object value, ImmutableHashSet<string> allowed) =>
        value.Members.Select(m => m.Key).FirstOrDefault(k => !allowed.Contains(k));

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsIsoDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>(b) and (c): one approved kind, its own event type, the directory of its pinned vector under <c>conformance/acta/</c>, and the sha256 of that vector's <c>expected.canonical</c> and <c>expected.leaf</c>.</summary>
public sealed record ApprovedKind(LifecycleKind Kind, string EventType, string VectorDirectory, string CanonicalSha256, string LeafSha256);

/// <summary>(a): the owner's words verbatim, the ISO 8601 date, and where the decision was given; all three required, non-empty.</summary>
public sealed record OwnerDecision(string Words, string Date, string Place);

/// <summary>
/// (d), owner question B: the owner's sign-off on the payloads' member names and event types. (e): the
/// cybersecurity agent's sign-off on the lifecycle formats (its report's section 6), at confidence
/// <c>high</c>, and hopper-doctrine's gate on the first append, both named in <see cref="Statement"/>.
/// Every member required and non-empty.
/// </summary>
public sealed record SignOff(string By, string Date, string Confidence, string Statement);
