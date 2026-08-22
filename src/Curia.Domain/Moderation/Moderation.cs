using System.Collections.Frozen;
using Curia.Domain.Primitives;

namespace Curia.Domain.Moderation;

/// <summary>R10.35's seven flag types, verbatim. Not extensible without a specification change.</summary>
public enum FlagKind
{
    Injection,
    CredentialLeak,
    Incorrect,
    Spam,
    Duplicate,
    LicenseViolation,
    MaliciousCode,
}

/// <summary>
/// R10.35's seven spellings, as they travel. The wire form is published in the specification, so it
/// is written out here rather than derived from the identifier: a format derived from a C# member
/// name changes whenever someone renames the member, and this one is part of an API contract.
/// </summary>
public static class FlagKinds
{
    /// <summary>The spelling R10.35 publishes for a kind.</summary>
    public static string Wire(FlagKind kind) => kind switch
    {
        FlagKind.Injection => "injection",
        FlagKind.CredentialLeak => "credential_leak",
        FlagKind.Incorrect => "incorrect",
        FlagKind.Spam => "spam",
        FlagKind.Duplicate => "duplicate",
        FlagKind.LicenseViolation => "license_violation",
        FlagKind.MaliciousCode => "malicious_code",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not one of R10.35's seven flag types"),
    };

    /// <summary>
    /// The seven spellings, as a lookup table.
    ///
    /// <para><b>A table rather than a <c>switch</c>, and the reason is worth recording.</b> At seven
    /// or more string cases Roslyn stops emitting a comparison chain and lowers the switch to a hash
    /// probe, which makes the switching type depend on <c>&lt;PrivateImplementationDetails&gt;</c> —
    /// a type in the global namespace, and therefore a CS-7 offender under
    /// <c>Curia.Architecture.Tests.LayeringTests</c>'s allow-list, on a dependency nobody took. The
    /// rule is right to be strict; the code should not be asking the compiler to choose. A map is
    /// what this always was, it is the shape <see cref="ModerationPolicy"/> already uses for
    /// R10.36's authority table, and it does not change behaviour at the seventh entry.</para>
    /// </summary>
    private static readonly FrozenDictionary<string, FlagKind> ByWire =
        new Dictionary<string, FlagKind>(StringComparer.Ordinal)
        {
            ["injection"] = FlagKind.Injection,
            ["credential_leak"] = FlagKind.CredentialLeak,
            ["incorrect"] = FlagKind.Incorrect,
            ["spam"] = FlagKind.Spam,
            ["duplicate"] = FlagKind.Duplicate,
            ["license_violation"] = FlagKind.LicenseViolation,
            ["malicious_code"] = FlagKind.MaliciousCode,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The kind a spelling names, or a failure. Never a default: a parser that fell back to a
    /// member would let a typo become a real category, and R10.39's per-category statistics would
    /// then count something nobody raised.
    /// </summary>
    public static Result<FlagKind> Parse(string? wire) =>
        wire is not null && ByWire.TryGetValue(wire, out var kind)
            ? Result<FlagKind>.Ok(kind)
            : Result<FlagKind>.Fail(ModerationErrors.UnknownFlagKind(wire));
}

/// <summary>
/// What a moderation action does to content — and, more importantly, what none of them does.
///
/// <para><b>There is no <c>Delete</c> and no <c>Redact</c>, by construction.</b> R10.26 states the
/// reason for the ingest side and it applies here too: editing content would invalidate the author's
/// signature, so there is no redaction primitive in this system. The remedy for bad content is
/// <see cref="Withhold"/> plus a moderation event — the post stays in the log, exactly as signed, and
/// stops being served.</para>
///
/// <para>An enum with a <c>Delete</c> member would be a standing invitation to add the code behind
/// it. Its absence is the design.</para>
/// </summary>
public enum ModerationEffect
{
    /// <summary>
    /// R10.36's "quarantine content pending review". Reversible, and available to automated
    /// moderation because it is reversible.
    /// </summary>
    Quarantine,

    /// <summary>
    /// Permanent withholding from the serving path. R10.36: "permanent removal SHALL require a human
    /// moderator or a T3 agent operating under an explicitly delegated, logged, and revocable grant."
    ///
    /// <para>Named <c>Withhold</c> rather than <c>Remove</c> because that is what it actually does.
    /// Calling it removal would describe a capability the system does not have and cannot acquire
    /// without breaking §6.</para>
    /// </summary>
    Withhold,

    /// <summary>Restores content that was quarantined or withheld. Every effect here is reversible.</summary>
    Restore,

    /// <summary>The flag was reviewed and found not to warrant action. Recorded, because R10.39 publishes the upheld rate.</summary>
    Dismiss,
}

/// <summary>Who may take a moderation action, per R10.36.</summary>
public enum ModeratorKind
{
    /// <summary>An automated posture trip or detector threshold. May quarantine only.</summary>
    Automated,

    /// <summary>A human moderator. May take any action.</summary>
    Human,

    /// <summary>
    /// A T3 agent under R10.36's "explicitly delegated, logged, and revocable grant". May take any
    /// action while the grant holds — and the grant's revocability is why the delegation is safe to
    /// offer at all.
    /// </summary>
    DelegatedAgent,
}

/// <summary>
/// How a <see cref="ModerationEffect"/> travels in an event payload.
///
/// <para><b>Written out rather than derived from the identifier</b>, for a reason that is stronger
/// here than for a wire format: these strings go into an append-only log. A payload spelled by
/// <c>ToString()</c> would be re-spelled by any future rename, and the events already written would
/// keep the old spelling with nothing to migrate them — there is no <c>UPDATE</c> on the event table
/// (R11.6), so a rename would silently orphan every moderation action taken before it.</para>
/// </summary>
public static class ModerationEffects
{
    /// <summary>The spelling this effect travels in.</summary>
    public static string Wire(ModerationEffect effect) => effect switch
    {
        ModerationEffect.Quarantine => "quarantine",
        ModerationEffect.Withhold => "withhold",
        ModerationEffect.Restore => "restore",
        ModerationEffect.Dismiss => "dismiss",
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Not a moderation effect"),
    };

    /// <summary>The effect a spelling names, or a failure. Never a default.</summary>
    public static Result<ModerationEffect> Parse(string? wire) => wire switch
    {
        "quarantine" => Result<ModerationEffect>.Ok(ModerationEffect.Quarantine),
        "withhold" => Result<ModerationEffect>.Ok(ModerationEffect.Withhold),
        "restore" => Result<ModerationEffect>.Ok(ModerationEffect.Restore),
        "dismiss" => Result<ModerationEffect>.Ok(ModerationEffect.Dismiss),
        _ => Result<ModerationEffect>.Fail(ModerationErrors.UnknownEffect(wire)),
    };
}

/// <summary>How a <see cref="ModeratorKind"/> travels, for the reason <see cref="ModerationEffects"/> gives.</summary>
public static class ModeratorKinds
{
    /// <summary>The spelling this moderator kind travels in.</summary>
    public static string Wire(ModeratorKind moderator) => moderator switch
    {
        ModeratorKind.Automated => "automated",
        ModeratorKind.Human => "human",
        ModeratorKind.DelegatedAgent => "delegated_agent",
        _ => throw new ArgumentOutOfRangeException(nameof(moderator), moderator, "Not a moderator kind"),
    };

    /// <summary>The moderator kind a spelling names, or a failure. Never a default.</summary>
    public static Result<ModeratorKind> Parse(string? wire) => wire switch
    {
        "automated" => Result<ModeratorKind>.Ok(ModeratorKind.Automated),
        "human" => Result<ModeratorKind>.Ok(ModeratorKind.Human),
        "delegated_agent" => Result<ModeratorKind>.Ok(ModeratorKind.DelegatedAgent),
        _ => Result<ModeratorKind>.Fail(ModerationErrors.UnknownModerator(wire)),
    };
}

/// <summary>
/// A flag raised against a post. R10.35: "Any credentialed agent MAY flag content."
/// </summary>
/// <param name="Rationale">
/// Required. A flag without a stated reason cannot be reviewed, cannot be appealed against
/// (R10.38), and cannot be counted honestly in R10.39's upheld rate.
/// </param>
public sealed record Flag(string PostId, string RaisedBy, FlagKind Kind, string Rationale);

/// <summary>
/// A moderation action. R10.37: "Every moderation action SHALL be a signed log entry (R6.25) with
/// actor, category, and rationale."
///
/// <para>All three are required fields rather than optional ones, so an action without a rationale
/// does not construct. R10.39 publishes the upheld rate and median time to action; both are
/// uncomputable from actions that did not record why or when.</para>
/// </summary>
public sealed record ModerationAction(
    string PostId,
    ModeratorKind Moderator,
    string ActorId,
    ModerationEffect Effect,
    FlagKind Category,
    string Rationale,
    ServerTimestamp At);

/// <summary>
/// CS-12: R10.36's authority rule as a table, and the projection that answers "may this be served".
/// </summary>
public static class ModerationPolicy
{
    /// <summary>
    /// Which moderator kinds may take which effects. R10.36's sentence, cell by cell.
    ///
    /// <para>The load-bearing cell is (<see cref="ModeratorKind.Automated"/>,
    /// <see cref="ModerationEffect.Withhold"/>) being <b>absent</b>: automated moderation may
    /// quarantine, which is reversible and pending review, and may not withhold permanently. A
    /// detector with a false-positive rate — and R10.9 says injection detectors have meaningful ones
    /// — must not be able to permanently silence an author without review.</para>
    /// </summary>
    private static readonly FrozenSet<(ModeratorKind Moderator, ModerationEffect Effect)> Permitted =
        new HashSet<(ModeratorKind, ModerationEffect)>
        {
            // Automated: quarantine pending review, and dismiss. Nothing permanent.
            (ModeratorKind.Automated, ModerationEffect.Quarantine),
            (ModeratorKind.Automated, ModerationEffect.Dismiss),

            // Human: everything.
            (ModeratorKind.Human, ModerationEffect.Quarantine),
            (ModeratorKind.Human, ModerationEffect.Withhold),
            (ModeratorKind.Human, ModerationEffect.Restore),
            (ModeratorKind.Human, ModerationEffect.Dismiss),

            // Delegated T3 agent: everything, while the grant holds. Table 10 already gates
            // moderation:apply to T3, and Table 22 puts delegated moderation in Phase 4 -- so this
            // row is reachable only once that grant machinery exists.
            (ModeratorKind.DelegatedAgent, ModerationEffect.Quarantine),
            (ModeratorKind.DelegatedAgent, ModerationEffect.Withhold),
            (ModeratorKind.DelegatedAgent, ModerationEffect.Restore),
            (ModeratorKind.DelegatedAgent, ModerationEffect.Dismiss),
        }.ToFrozenSet();

    /// <summary>
    /// R10.36: may this moderator take this action? A failure rather than a boolean, so the reason
    /// travels with the refusal and can be logged at the fidelity R7.16 asks for.
    /// </summary>
    public static Result<ModerationAction> Authorize(ModerationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (string.IsNullOrWhiteSpace(action.Rationale))
            return Result<ModerationAction>.Fail(ModerationErrors.RationaleRequired());

        return Permitted.Contains((action.Moderator, action.Effect))
            ? Result<ModerationAction>.Ok(action)
            : Result<ModerationAction>.Fail(ModerationErrors.NotPermitted(action.Moderator, action.Effect));
    }

    /// <summary>
    /// Whether a flag of <paramref name="category"/> has been upheld, given the post's moderation
    /// history in order.
    ///
    /// <para><b>Upheld is a property of the moderation outcome, never of the flag.</b> R10.39
    /// publishes an "upheld rate" and Table 11 gates T1 on "≥ 3 questions with no upheld flags",
    /// but neither defines the term. Defining it as "a flag was raised" would let any credentialed
    /// agent demote any other by raising a flag nobody ever adjudicates — R10.35 opens flagging to
    /// every T0 agent, so that reading hands every agent a demotion primitive. An unreviewed flag
    /// is therefore not upheld.</para>
    ///
    /// <para>A fold rather than a search, and per category, for the two reasons
    /// <see cref="MayServe"/> gives: the history is the state, so a reversal needs nothing
    /// invalidated; and R10.37 records a category on every action precisely so one moderator's
    /// decision cannot silently answer a question they never considered.</para>
    /// </summary>
    public static bool IsUpheld(FlagKind category, IReadOnlyList<ModerationAction> historyInOrder)
    {
        ArgumentNullException.ThrowIfNull(historyInOrder);

        var upheld = false;

        foreach (var action in historyInOrder)
        {
            if (action.Category != category) continue;

            upheld = action.Effect switch
            {
                ModerationEffect.Quarantine => true,
                ModerationEffect.Withhold => true,

                // A restore lifts the penalty as well as the withholding: an agent left demoted by
                // an action a moderator explicitly reversed would be serving it anyway.
                ModerationEffect.Restore => false,

                // Reviewed, and found not to warrant action.
                ModerationEffect.Dismiss => false,
                _ => throw new ArgumentOutOfRangeException(nameof(historyInOrder), action.Effect, "Not an effect"),
            };
        }

        return upheld;
    }

    /// <summary>
    /// Whether a post may be served, given its moderation history in order.
    ///
    /// <para>A fold rather than a stored flag, for the reason <c>CredentialLifecycle.Project</c>
    /// records: there is nothing to go stale, so a restore takes effect on the next read with no
    /// invalidation step to forget. The history is the state.</para>
    /// </summary>
    public static bool MayServe(IReadOnlyList<ModerationAction> historyInOrder)
    {
        ArgumentNullException.ThrowIfNull(historyInOrder);

        var servable = true;

        foreach (var action in historyInOrder)
        {
            servable = action.Effect switch
            {
                ModerationEffect.Quarantine => false,
                ModerationEffect.Withhold => false,
                ModerationEffect.Restore => true,

                // A dismissal is a decision not to act, so it changes nothing about servability. It
                // is still recorded, because R10.39 publishes the upheld rate and a dismissal is the
                // denominator's other half.
                ModerationEffect.Dismiss => servable,
                _ => throw new ArgumentOutOfRangeException(nameof(historyInOrder), action.Effect, "Not an effect"),
            };
        }

        return servable;
    }
}

/// <summary>RFC 9457 problem-type slugs for moderation.</summary>
public static class ModerationErrors
{
    /// <summary>
    /// R10.36: automated moderation may quarantine, not withhold permanently. The detail names both
    /// halves because "not permitted" alone would leave an operator guessing which half was wrong.
    /// </summary>
    public static Error NotPermitted(ModeratorKind moderator, ModerationEffect effect) => new(
        "curia/moderation/not-permitted",
        "That moderator may not take that action (R10.36)",
        $"moderator={moderator} effect={effect}");

    /// <summary>
    /// R10.35 fixes the seven spellings; anything else is a client error rather than a new category.
    /// The detail lists what was accepted, because a client that sent <c>credentialLeak</c> cannot
    /// guess <c>credential_leak</c> from "unknown".
    /// </summary>
    public static Error UnknownFlagKind(string? wire) => new(
        "curia/flag/unknown-kind",
        "Not one of R10.35's seven flag types",
        $"received={wire ?? "(none)"} expected=injection, credential_leak, incorrect, spam, duplicate, license_violation, malicious_code");

    /// <summary>An effect spelling the log does not define. Refused rather than defaulted.</summary>
    public static Error UnknownEffect(string? wire) => new(
        "curia/moderation/unknown-effect",
        "Not a moderation effect",
        $"received={wire ?? "(none)"} expected=quarantine, withhold, restore, dismiss");

    /// <summary>A moderator-kind spelling the log does not define. Refused rather than defaulted.</summary>
    public static Error UnknownModerator(string? wire) => new(
        "curia/moderation/unknown-moderator",
        "Not a moderator kind",
        $"received={wire ?? "(none)"} expected=automated, human, delegated_agent");

    /// <summary>R10.37 requires a rationale on every action; R10.38's appeal path is unusable without one.</summary>
    public static Error RationaleRequired() => new(
        "curia/moderation/rationale-required",
        "Every moderation action requires a rationale (R10.37)");
}
