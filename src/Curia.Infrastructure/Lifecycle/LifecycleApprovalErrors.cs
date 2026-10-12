using Curia.Domain.Primitives;

namespace Curia.Infrastructure.Lifecycle;

/// <summary>
/// R4.38 (add.): the named refusals of the lifecycle approval record and of the gate's opt-in (spec
/// section 4.13). Each names a field and a rule, never a key, a statement or a signature; an opt-in's
/// value is not echoed.
/// </summary>
public static class LifecycleApprovalErrors
{
    public const string RecordMissingType = "curia/lifecycle/approval-record-missing";
    public const string RecordUnreadableType = "curia/lifecycle/approval-record-unreadable";
    public const string ItemMissingType = "curia/lifecycle/approval-item-missing";
    public const string ConfidenceNotHighType = "curia/lifecycle/approval-confidence-not-high";
    public const string EventTypeNotTheKindsType = "curia/lifecycle/approval-event-type";
    public const string VectorDirectoryRefusedType = "curia/lifecycle/approval-vector-directory";
    public const string CompromiseOrderType = "curia/lifecycle/compromise-order";
    public const string VectorMissingType = "curia/lifecycle/vector-missing";
    public const string VectorUnreadableType = "curia/lifecycle/vector-unreadable";
    public const string VectorEventTypeType = "curia/lifecycle/vector-event-type";
    public const string VectorEnrolmentShapeType = "curia/lifecycle/vector-enrolment-shape";
    public const string VectorDigestType = "curia/lifecycle/vector-digest";
    public const string OptInUnreadableType = "curia/lifecycle/opt-in-unreadable";
    public const string KindNotApprovedType = "curia/lifecycle/kind-not-approved";

    public static Error RecordMissing(string resourceName) => new(
        RecordMissingType,
        "The lifecycle approval record is missing from the build; a missing record is never read as empty",
        resourceName);

    public static Error RecordUnreadable(string reason) => new(
        RecordUnreadableType,
        "The lifecycle approval record is not the record's shape",
        reason);

    public static Error ItemMissing(string item) => new(
        ItemMissingType,
        "A non-empty approval carries the owner's decision (a), the member-name sign-off (d) and the security sign-off (e), every member present and non-empty",
        item);

    public static Error ConfidenceNotHigh() => new(
        ConfidenceNotHighType,
        "The security sign-off (e) is given at confidence high",
        "security_sign_off.confidence");

    public static Error EventTypeNotTheKinds(string kind) => new(
        EventTypeNotTheKindsType,
        "An approved kind's event type is the kind's own",
        kind);

    public static Error VectorDirectoryRefused(string kind) => new(
        VectorDirectoryRefusedType,
        "An approved kind's vector directory is one non-empty name under conformance/acta/, never key-bound-entry",
        kind);

    public static Error CompromiseOrder(string where) => new(
        CompromiseOrderType,
        "Rotation or retirement is never enabled or approved without compromise",
        where);

    public static Error VectorMissing(string kind, string what) => new(
        VectorMissingType,
        "An approved kind's pinned vector is missing",
        $"{kind}: {what}");

    public static Error VectorUnreadable(string kind) => new(
        VectorUnreadableType,
        "An approved kind's vector input is not a JSON object",
        kind);

    public static Error VectorEventType(string kind) => new(
        VectorEventTypeType,
        "An approved kind's vector input carries another event type",
        kind);

    public static Error VectorEnrolmentShape(string kind) => new(
        VectorEnrolmentShapeType,
        "A rotation's vector is enrolment's pair's shape: its payload carries no statement",
        kind);

    public static Error VectorDigest(string kind, string file) => new(
        VectorDigestType,
        "An approved kind's pinned digest differs from its vector",
        $"{kind}: {file}");

    public static Error OptInUnreadable() => new(
        OptInUnreadableType,
        "Curia:Lifecycle:Enabled is none, or a comma list over rotation, retirement and compromise",
        "Curia:Lifecycle:Enabled");

    public static Error KindNotApproved(string kind) => new(
        KindNotApprovedType,
        "Curia:Lifecycle:Enabled names a kind this build's approval record does not approve",
        kind);
}
