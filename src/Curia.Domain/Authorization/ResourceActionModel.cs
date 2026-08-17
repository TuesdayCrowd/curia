using System.Collections.Frozen;
using Curia.Domain.Primitives;

namespace Curia.Domain.Authorization;

/// <summary>
/// CS-12: Table 10 (whitepaper §7.2, "Resource/action model") as data, meant to be read cell by
/// cell against the published table. <c>Table10ConformanceTests</c> parses the table out of
/// <c>curia-agent-forum-WHITEPAPER.md</c> and asserts this matches it cell for cell.
///
/// <para><b>Why the test parses the white paper instead of reading this table.</b> R7.12 requires
/// every denied cell to have a test asserting the denial. Generating those tests from
/// <see cref="Table"/> would make them vacuous: a mistyped cell would produce a test asserting the
/// mistake, and it would pass. That is the failure this project has now found five separate times
/// -- the absence of a probe is indistinguishable from a probe that passed. So the table's
/// authority stays in the white paper, this is a transcription of it, and a conformance test holds
/// the two against each other. Editing Table 10 without following it here fails the build, and so
/// does the reverse.</para>
///
/// <para>The published table, verbatim:</para>
/// <code>
/// | Resource       | Actions               | Anonymous       | T0           | T1  | T2  | T3            |
/// |----------------|-----------------------|-----------------|--------------|-----|-----|---------------|
/// | `board`        | `list`, `read`        | ✓               | ✓            | ✓   | ✓   | ✓             |
/// | `thread`       | `read`, `search`      | ✓               | ✓            | ✓   | ✓   | ✓             |
/// | `question`     | `create`              | ✗               | rate-limited | ✓   | ✓   | ✓             |
/// | `answer`       | `create`              | ✗               | ✗            | ✓   | ✓   | ✓             |
/// | `finding`      | `create`              | ✗               | ✗            | ✗   | ✓   | ✓             |
/// | `comment`      | `create`              | ✗               | ✓            | ✓   | ✓   | ✓             |
/// | `revision`     | `create` (own)        | ✗               | ✓            | ✓   | ✓   | ✓             |
/// | `vote`         | `cast`                | ✗               | ✗            | ✓   | ✓   | ✓             |
/// | `answer`       | `accept` (own thread) | ✗               | ✓            | ✓   | ✓   | ✓             |
/// | `tag`          | `create`              | ✗               | ✗            | ✗   | ✓   | ✓             |
/// | `flag`         | `raise`               | ✗               | ✓            | ✓   | ✓   | ✓             |
/// | `verification` | `submit`              | ✗               | ✗            | ✓   | ✓   | ✓             |
/// | `moderation`   | `apply`               | ✗               | ✗            | ✗   | ✗   | ✓ (delegated) |
/// | `agent`        | `enroll`              | owner-auth only |              |     |     |               |
/// </code>
/// </summary>
public static class ResourceActionModel
{
    private const Table10Cell Y = Table10Cell.Allowed;
    private const Table10Cell N = Table10Cell.Denied;
    private const Table10Cell Owner = Table10Cell.OwnerAuthOnly;

    /// <summary>
    /// Table 10 itself: every (resource, action) pair the table names, and only those. A lookup
    /// miss is the table saying "this pair is not in the model," which
    /// <see cref="RowFor"/> reports as a <see cref="Result{T}"/> failure (CS-10) rather than as a
    /// denial -- an unmodelled pair is a gap in the specification, and reporting it as "denied"
    /// would let a missing row masquerade as a deliberate one.
    /// </summary>
    private static readonly FrozenDictionary<(ResourceKind Resource, ActionKind Action), ResourceActionRow> Table =
        new Dictionary<(ResourceKind, ActionKind), ResourceActionRow>
        {
            // `board` | `list`, `read` -- one row, two pairs (see ActionKind's remarks).
            [(ResourceKind.Board, ActionKind.List)] = new(Y, Y, Y, Y, Y),
            [(ResourceKind.Board, ActionKind.Read)] = new(Y, Y, Y, Y, Y),

            // `thread` | `read`, `search`
            [(ResourceKind.Thread, ActionKind.Read)] = new(Y, Y, Y, Y, Y),
            [(ResourceKind.Thread, ActionKind.Search)] = new(Y, Y, Y, Y, Y),

            // `question` | `create` -- T0's cell is "rate-limited", the table's only such cell.
            [(ResourceKind.Question, ActionKind.Create)] = new(N, Table10Cell.RateLimited, Y, Y, Y),

            // `answer` | `create`
            [(ResourceKind.Answer, ActionKind.Create)] = new(N, N, Y, Y, Y),

            // `finding` | `create`
            [(ResourceKind.Finding, ActionKind.Create)] = new(N, N, N, Y, Y),

            // `comment` | `create`
            [(ResourceKind.Comment, ActionKind.Create)] = new(N, Y, Y, Y, Y),

            // `revision` | `create` (own)
            [(ResourceKind.Revision, ActionKind.Create)] = new(N, Y, Y, Y, Y, GrantQualifier.OwnResourceOnly),

            // `vote` | `cast`
            [(ResourceKind.Vote, ActionKind.Cast)] = new(N, N, Y, Y, Y),

            // `answer` | `accept` (own thread)
            [(ResourceKind.Answer, ActionKind.Accept)] = new(N, Y, Y, Y, Y, GrantQualifier.OwnThreadOnly),

            // `tag` | `create`
            [(ResourceKind.Tag, ActionKind.Create)] = new(N, N, N, Y, Y),

            // `flag` | `raise`
            [(ResourceKind.Flag, ActionKind.Raise)] = new(N, Y, Y, Y, Y),

            // `verification` | `submit`
            [(ResourceKind.Verification, ActionKind.Submit)] = new(N, N, Y, Y, Y),

            // `moderation` | `apply` -- T3's cell is "✓ (delegated)".
            [(ResourceKind.Moderation, ActionKind.Apply)] = new(N, N, N, N, Y, GrantQualifier.Delegated),

            // `agent` | `enroll` -- "owner-auth only", spanning every tier column.
            [(ResourceKind.Agent, ActionKind.Enroll)] = new(Owner, Owner, Owner, Owner, Owner),
        }.ToFrozenDictionary();

    /// <summary>Every (resource, action) pair Table 10 models, for tests that must cover all of them.</summary>
    public static IReadOnlyCollection<(ResourceKind Resource, ActionKind Action)> ModelledPairs => Table.Keys;

    /// <summary>The published row for a pair, or a failure when Table 10 does not model the pair.</summary>
    public static Result<ResourceActionRow> RowFor(ResourceKind resource, ActionKind action) =>
        Table.TryGetValue((resource, action), out var row)
            ? Result<ResourceActionRow>.Ok(row)
            : Result<ResourceActionRow>.Fail(AuthorizationErrors.UnmodelledResourceAction(resource, action));

    /// <summary>
    /// The single cell lookup this table exists to define. Note what it is not: this is Table 10
    /// alone, with no quarantine rule and no rate-budget evaluation. <see cref="AccessPolicy"/>
    /// composes those; keeping them out of here is what lets the conformance test compare this
    /// against the published table without having to model the rest of §7 first.
    /// </summary>
    public static Result<Table10Cell> CellFor(ResourceKind resource, ActionKind action, PrincipalTier tier) =>
        RowFor(resource, action).Map(row => row[tier]);
}
