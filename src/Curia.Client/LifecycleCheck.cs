using System.Globalization;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>A lifecycle verdict and the short slug of the check that decided it (<c>conformance/lifecycle/</c>'s <c>expected.json</c>).</summary>
internal sealed record LifecycleVerdict(Check Check, string Predicate);

/// <summary>
/// R6.54 (add.): the author's lifecycle, folded (spec sections 4.5, 4.6 and 4.8).
///
/// <para><see cref="ActaCheck.KeyBinding"/> shows that the key which signed a post was bound to its author before
/// it. It cannot show that the key was still the author's when the post was accepted: a key can be retired, and the
/// Forum can omit the entry that says so. This folds every lifecycle entry of the author, in log order, from the
/// author's <c>agent.enrolled</c> through the first entry at or after the post, and answers for the key that signed the
/// post. <c>curia-testis</c> writes the same rules (<c>rust/curia-testis/src/lifecycle.rs</c>); the two are compared on
/// <c>conformance/lifecycle/</c>.</para>
///
/// <list type="bullet">
/// <item>Each entry is read for its type, then its statement's <c>typ</c>, before anything else (KF4): an unknown type
/// or version is <i>could not be checked</i>, whatever it carries and whoever signed it. Never verified, never skipped.</item>
/// <item>Each entry is proven under the one signed head, belongs to the author, and links from the entry before it in the
/// author's own chain. The only entry without a <c>prev</c> is the enrolment's first binding, as the one entry after
/// <c>agent.enrolled</c>, under the enrolment's <c>kid</c>, with exactly <c>{agent_id, jwk, kid}</c> (R4.38); the count is
/// this agent's chain, never the log's, so a binding a pre-G16 identity gains after its posts is that binding and not a
/// broken link.</item>
/// <item>A rotation or retirement is a canonical statement of a closed member set (KF4, KF5) whose signatures carry
/// its kind's header <c>typ</c> (KF1), name their roles' members as header <c>kid</c> (KF2), and verify under a key that
/// was bound and not retired at that point.</item>
/// <item>The post's key is verified when it was bound before the post and not retired before it. The comparison is
/// <c>retiredAt &lt; postIndex</c>, exactly.</item>
/// </list>
///
/// <para>No approval is consulted: a reader folds on the merits (spec section 4.8). A declaration
/// (<c>agent.key-compromised</c>) is a kind this stage cannot fold, and so is an entry of an unknown kind.</para>
/// </summary>
public static class LifecycleCheck
{
    /// <summary>How many lifecycle entries before a post a reader will fold (spec section 4.8).</summary>
    public const int MaxEntriesBeforePost = 1024;

    private const string RetiredType = "agent.key-retired";
    private const string RotationStatementPrefix = "curia.key-rotation/";
    private const string RetirementStatementPrefix = "curia.key-retirement/";

    private static readonly string[] EnrolmentBindingMembers = ["agent_id", "jwk", "kid"];
    private static readonly string[] RotationEntryMembers = ["agent_id", "authorization", "authorized_by", "jwk", "kid", "possession", "prev", "statement"];
    private static readonly string[] RetirementEntryMembers = ["agent_id", "authorization", "kid", "prev", "statement"];

    private static readonly Dictionary<string, IContentSigner> NoSigners = new(StringComparer.Ordinal);

    private static Dictionary<string, IContentVerifier> Verifiers => new(StringComparer.Ordinal)
    {
        ["ES256"] = new Es256Adapter(),
        ["EdDSA"] = new Ed25519Adapter(),
    };

    /// <summary>
    /// Whether the key that signed <paramref name="post"/> was live in the author's lifecycle when the post was accepted.
    /// </summary>
    /// <param name="post">The post the reader holds.</param>
    /// <param name="postEntry">The log's own entry for the post, which an inclusion check has proven.</param>
    /// <param name="lifecycle">
    /// Each lifecycle entry of the author with its proof against the tree <paramref name="head"/> covers, in log order, from
    /// the author's enrolment through the first entry at or after the post.
    /// </param>
    /// <param name="head">A signed head this reader has verified.</param>
    public static Check Verify(
        ProvenancePost post,
        LogEntryDocument postEntry,
        IReadOnlyList<(LogEntryDocument Entry, InclusionProofDocument Proof)> lifecycle,
        SignedHeadDocument head) =>
        Fold(post, postEntry, lifecycle, head).Check;

    internal static LifecycleVerdict Fold(
        ProvenancePost post,
        LogEntryDocument postEntry,
        IReadOnlyList<(LogEntryDocument Entry, InclusionProofDocument Proof)> lifecycle,
        SignedHeadDocument head)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(postEntry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(head);

        if (!ActaCheck.EntryDescribes(postEntry, post))
            return Failed("post-entry-mismatch", "the log entry does not carry this post's canonical bytes (R11.29)");

        var (record, logged) = ActaCheck.ReadLoggedPost(post, postEntry, postEntry.LogIndex);
        if (logged is null) return new LifecycleVerdict(record, "post-entry-mismatch");

        if (lifecycle.Count == 0)
            return CouldNotCheck("lifecycle-not-supplied", "the Forum's key set lists no lifecycle for the author");

        if (lifecycle.Count(l => l.Entry.LogIndex < logged.Index) > MaxEntriesBeforePost)
            return CouldNotCheck("lifecycle-too-long", "the author's lifecycle has more than 1,024 entries before the post");

        var fold = new Folder(logged.Author, head);
        foreach (var (entry, proof) in lifecycle)
        {
            if (fold.Apply(entry, proof) is { } refusal) return refusal;
            if (entry.LogIndex >= logged.Index) break;
        }

        // The post's key. Retired before the post is a failure whatever else holds.
        if (fold.Retired.TryGetValue(logged.Kid, out var retiredAt) && retiredAt < logged.Index)
            return Failed("retired-before-post", Text(
                $"the author's key {Check.Quote(logged.Kid)} was retired at log index {retiredAt}, before the post at {logged.Index}"));

        if (fold.Bound.TryGetValue(logged.Kid, out var bound) && bound.At < logged.Index)
            return new LifecycleVerdict(
                Check.Verified(Text(
                    $"the author's key {Check.Quote(logged.Kid)} was bound at log index {bound.At} and was live at the post's {logged.Index}; {fold.Count} lifecycle entries checked through log index {fold.PrevIndex}")),
                "verified");

        // Bound at or after the post, or by no entry the lifecycle holds: the log holds no key for the post from
        // before it, which is its silence and not a contradiction (R6.54).
        return CouldNotCheck("bound-after-post", Text(
            bound is null
                ? $"the lifecycle binds no key under kid={Check.Quote(logged.Kid)}, so the log holds no key for this post from before it"
                : $"kid={Check.Quote(logged.Kid)} is bound at log index {bound.At}, which is not before this post at {logged.Index}, so the log holds no key for it from before the post"));
    }

    private static LifecycleVerdict Failed(string predicate, string detail) =>
        new(Check.Failed(detail), predicate);

    private static LifecycleVerdict CouldNotCheck(string predicate, string detail) =>
        new(Check.CouldNotCheck(detail), predicate);

    private static string Text(string value) => string.Create(CultureInfo.InvariantCulture, $"{value}");

    private sealed record BoundKey(PublicKeyMaterial Material, long At);

    private sealed class Folder(string author, SignedHeadDocument head)
    {
        private bool _firstWasEnrolment;
        private string? _enrolmentKid;

        public Dictionary<string, BoundKey> Bound { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> Retired { get; } = new(StringComparer.Ordinal);

        public long? PrevIndex { get; private set; }

        public int Count { get; private set; }

        /// <summary>One entry, in KF4's order: its type, then its statement's <c>typ</c>, then everything else.</summary>
        public LifecycleVerdict? Apply(LogEntryDocument entry, InclusionProofDocument proof)
        {
            var type = ClientJson.String(entry.Entry, LogLeaf.EventTypeMember);
            var kind = type switch
            {
                "agent.enrolled" => Kind.Enrolled,
                "agent.key-bound" => Kind.Bound,
                RetiredType => Kind.Retired,
                _ => (Kind?)null,
            };
            if (kind is null)
                return CouldNotCheck("unknown-lifecycle-kind", Text(
                    $"the lifecycle lists an entry at log index {entry.LogIndex} of type {Check.Quote(type)}, none of the four a reader can fold (KF4)"));

            var payload = ClientJson.Object(entry.Entry, LogLeaf.PayloadMember);
            if (payload is null)
                return Failed("statement-mismatch", Text($"the lifecycle entry at log index {entry.LogIndex} carries no payload"));

            var statementText = ClientJson.String(payload, "statement");

            // The statement's typ, read from the still-unverified statement only to choose which rules apply: another
            // version of this kind is not one this reader knows (KF4); a typ of another kind is not this entry's.
            if (statementText is not null && kind is Kind.Bound or Kind.Retired
                && StatementTyp(statementText) is { } typ)
            {
                var (expected, prefix) = kind is Kind.Bound
                    ? (KeyStatements.RotationStatementTyp, RotationStatementPrefix)
                    : (KeyStatements.RetirementStatementTyp, RetirementStatementPrefix);
                if (!string.Equals(typ, expected, StringComparison.Ordinal))
                    return typ.StartsWith(prefix, StringComparison.Ordinal)
                        ? CouldNotCheck("unknown-lifecycle-kind", Text(
                            $"the statement at log index {entry.LogIndex} is of version {Check.Quote(typ)}, which this reader does not know (KF4)"))
                        : Mismatch(entry.LogIndex, "the statement is not this entry's kind");
            }

            if (entry.LogIndex != proof.LogIndex)
                return Failed("inclusion-invalid", Text(
                    $"the lifecycle entry is leaf {entry.LogIndex} and its proof is about leaf {proof.LogIndex}"));

            var proven = ActaCheck.ProofHolds(entry, proof);
            if (proven.Outcome is not CheckOutcome.Verified) return new LifecycleVerdict(proven, "inclusion-invalid");

            var covers = ActaCheck.HeadCovers(head, proof.TreeSize, proof.RootHash);
            if (covers.Outcome is not CheckOutcome.Verified) return new LifecycleVerdict(covers, "inclusion-invalid");

            var aggregate = ClientJson.String(entry.Entry, LogLeaf.AggregateIdMember);
            var agent = ClientJson.String(payload, "agent_id");
            if (!string.Equals(aggregate, author, StringComparison.Ordinal) || !string.Equals(agent, author, StringComparison.Ordinal))
                return Mismatch(entry.LogIndex, "the entry is not the author's");

            if (PrevIndex is { } previous && entry.LogIndex <= previous)
                return BrokenLink(entry.LogIndex, null);

            var refusal = kind switch
            {
                Kind.Enrolled => Enrolled(payload, entry.LogIndex),
                Kind.Bound when statementText is null => EnrolmentBinding(payload, entry.LogIndex),
                Kind.Bound => Rotation(payload, entry.LogIndex),
                Kind.Retired => Retirement(payload, entry.LogIndex),
                _ => Mismatch(entry.LogIndex, "the entry is of no kind a reader can fold"),
            };
            if (refusal is not null) return refusal;

            Count++;
            PrevIndex = entry.LogIndex;
            return null;
        }

        /// <summary><c>agent.enrolled</c> opens the author's chain, and only opens it.</summary>
        private LifecycleVerdict? Enrolled(JsonValue.Object payload, long index)
        {
            if (Count != 0) return BrokenLink(index, null);

            _firstWasEnrolment = true;
            _enrolmentKid = ClientJson.String(payload, "kid");
            return null;
        }

        /// <summary>
        /// R4.38: a binding without a <c>prev</c> is the enrolment's first binding only as the one entry after
        /// <c>agent.enrolled</c>, under its <c>kid</c>, with exactly <c>{agent_id, jwk, kid}</c>; anywhere else it is
        /// a broken link, and a wrong member set is a mismatch.
        /// </summary>
        private LifecycleVerdict? EnrolmentBinding(JsonValue.Object payload, long index)
        {
            var kid = ClientJson.String(payload, "kid");
            if (!(Count == 1 && _firstWasEnrolment) || kid is null || !string.Equals(kid, _enrolmentKid, StringComparison.Ordinal))
                return BrokenLink(index, null);

            if (!HasExactly(payload, EnrolmentBindingMembers))
                return Mismatch(index, "the binding's member set is not {agent_id, jwk, kid}");

            if (ClientJson.Object(payload, "jwk") is not { } jwk
                || ForumDocuments.ReadJwk(jwk) is not { } read
                || !SignatureCheck.Material(read).TryGetValue(out var material, out _))
                return Mismatch(index, "the binding's jwk is not a usable key");

            if (!string.Equals(read.Kid, kid, StringComparison.Ordinal))
                return Mismatch(index, "the binding's jwk names another kid");

            Bound[kid] = new BoundKey(material!, index);
            return null;
        }

        private LifecycleVerdict? Rotation(JsonValue.Object payload, long index)
        {
            if (!HasExactly(payload, RotationEntryMembers))
                return Mismatch(index, "the entry's member set is not a rotation's");

            var text = ClientJson.String(payload, "statement")!;
            if (!KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(text)).TryGetValue(out var statement, out _))
                return Mismatch(index, "the statement is not a rotation statement of the closed form (KF4, KF5)");

            if (!KeyStatements.MaterialOf(statement!).TryGetValue(out var newKey, out _))
                return Mismatch(index, "the statement's jwk is not the key's public rendering under the statement's kid");

            // Every member the entry states beside the statement equals the statement's own.
            if (!string.Equals(ClientJson.String(payload, "agent_id"), statement!.AgentId, StringComparison.Ordinal)
                || !string.Equals(ClientJson.String(payload, "authorized_by"), statement.AuthorizedBy, StringComparison.Ordinal)
                || !string.Equals(ClientJson.String(payload, "kid"), statement.Kid, StringComparison.Ordinal)
                || ClientJson.WholeNumber(payload, "prev") != statement.Prev
                || ClientJson.Object(payload, "jwk") is not { } entryJwk
                || !PublicJwk.SameKey(entryJwk, statement.Jwk))
                return Mismatch(index, "an entry member differs from its statement's");

            if (LinkFailure(statement.Prev, index) is { } broken) return broken;

            var authorization = new JwsSignature(ClientJson.String(payload, "authorization") ?? string.Empty);
            var possession = new JwsSignature(ClientJson.String(payload, "possession") ?? string.Empty);

            // KF1, then KF2: each signature carries a rotation's header typ, and names its role's member as kid.
            if (!HasHeaderTyp(authorization, KeyStatements.RotationHeaderTyp) || !HasHeaderTyp(possession, KeyStatements.RotationHeaderTyp))
                return new LifecycleVerdict(Check.Failed(Text($"the signature of the rotation at log index {index} does not carry a rotation's header typ (KF1)")), "lifecycle-typ-mismatch");
            if (!HasHeaderKid(authorization, statement.AuthorizedBy) || !HasHeaderKid(possession, statement.Kid))
                return Unauthorized(index);

            // The authorizing key was bound and not retired before this entry, and both signatures verify.
            if (!Bound.TryGetValue(statement.AuthorizedBy, out var authorizer) || Retired.ContainsKey(statement.AuthorizedBy))
                return Unauthorized(index);
            if (!CanonicalOf(text).TryGetValue(out var canonical, out _))
                return Mismatch(index, "the statement has no canonical form");
            if (!Signed(authorization, canonical, KeyStatements.RotationHeaderTyp, authorizer.Material)
                || !Signed(possession, canonical, KeyStatements.RotationHeaderTyp, newKey!))
                return Unauthorized(index);

            if (Bound.ContainsKey(statement.Kid))
                return Mismatch(index, "the kid is already bound");

            Bound[statement.Kid] = new BoundKey(newKey!, index);
            return null;
        }

        private LifecycleVerdict? Retirement(JsonValue.Object payload, long index)
        {
            if (!HasExactly(payload, RetirementEntryMembers))
                return Mismatch(index, "the entry's member set is not a retirement's");

            var text = ClientJson.String(payload, "statement")!;
            if (!KeyStatements.ReadRetirement(Encoding.UTF8.GetBytes(text)).TryGetValue(out var statement, out _))
                return Mismatch(index, "the statement is not a retirement statement of the closed form (KF4)");

            if (!string.Equals(ClientJson.String(payload, "agent_id"), statement!.AgentId, StringComparison.Ordinal)
                || !string.Equals(ClientJson.String(payload, "kid"), statement.Kid, StringComparison.Ordinal)
                || ClientJson.WholeNumber(payload, "prev") != statement.Prev)
                return Mismatch(index, "an entry member differs from its statement's");

            if (LinkFailure(statement.Prev, index) is { } broken) return broken;

            // KF1, KF2: the retirement is made by the key it retires, under a retirement's typ.
            var authorization = new JwsSignature(ClientJson.String(payload, "authorization") ?? string.Empty);
            if (!HasHeaderTyp(authorization, KeyStatements.RetirementHeaderTyp))
                return new LifecycleVerdict(Check.Failed(Text($"the signature of the retirement at log index {index} does not carry a retirement's header typ (KF1)")), "lifecycle-typ-mismatch");
            if (!HasHeaderKid(authorization, statement.Kid))
                return Unauthorized(index);

            if (!Bound.TryGetValue(statement.Kid, out var key) || Retired.ContainsKey(statement.Kid))
                return Unauthorized(index);
            if (!CanonicalOf(text).TryGetValue(out var canonical, out _))
                return Mismatch(index, "the statement has no canonical form");
            if (!Signed(authorization, canonical, KeyStatements.RetirementHeaderTyp, key.Material))
                return Unauthorized(index);

            Retired[statement.Kid] = index;
            return null;
        }

        /// <summary>The statement's <c>prev</c> is the index of the entry before it in the author's chain.</summary>
        private LifecycleVerdict? LinkFailure(long stated, long index) =>
            PrevIndex is { } previous && previous == stated ? null : BrokenLink(index, stated);

        private LifecycleVerdict BrokenLink(long index, long? stated) =>
            Failed("broken-link", Text(
                stated is { } named && PrevIndex is { } actual
                    ? $"lifecycle entry {index} names {named} as the entry before it, and the entry before it is {actual}"
                    : $"lifecycle entry {index} has no entry before it to link from, is not after the entry before it, or is a binding without a prev that is not the enrolment's first (R4.38)"));
    }

    private enum Kind
    {
        Enrolled,
        Bound,
        Retired,
    }

    private static LifecycleVerdict Mismatch(long index, string what) =>
        Failed("statement-mismatch", Text($"the lifecycle entry at log index {index} disagrees with its statement or its set: {what}"));

    private static LifecycleVerdict Unauthorized(long index) =>
        Failed("rotation-unauthorized", Text($"the entry at log index {index} is not signed by a key that was live there, in the role its statement names"));

    private static bool HasExactly(JsonValue.Object payload, string[] names) =>
        payload.Members.Length == names.Length
        && payload.Members.Select(m => m.Key).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string? StatementTyp(string statement) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).TryGetValue(out var root, out _)
        && root is JsonValue.Object o
            ? ClientJson.String(o, "typ")
            : null;

    private static bool HasHeaderTyp(JwsSignature signature, string typ) =>
        DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out _)
        && string.Equals(header!.Typ, typ, StringComparison.Ordinal);

    private static bool HasHeaderKid(JwsSignature signature, string kid) =>
        DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out _)
        && string.Equals(header!.Kid, kid, StringComparison.Ordinal);

    private static bool Signed(JwsSignature signature, CanonicalBytes canonical, string headerTyp, PublicKeyMaterial key) =>
        new DetachedJws(NoSigners, Verifiers, headerTyp).Verify(canonical, signature, key).IsOk;

    /// <summary>The canonical bytes of a statement already read as its own canonical form.</summary>
    private static Result<CanonicalBytes> CanonicalOf(string statement) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).TryGetValue(out var root, out var error)
            ? CanonicalJson.CanonicalizeWithNfc(root!)
            : Result<CanonicalBytes>.Fail(error!);
}
