using System.Collections.Immutable;
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
/// (<c>agent.key-compromised</c>) is a statement the operator's log key signed (KF1, KF2) whose <c>kids</c>, bindings and
/// <c>from_index</c> the reader recomputes from the lifecycle (KF3); one that holds, and names the post's key from a log
/// index at or below the post's, and follows the post, makes the post <i>disputed</i> (R6.57). Check (1) of KF3 is evaluated
/// over the supplied stream only. A declaration that fails never disputes a post. An entry of an unknown kind is
/// <i>could not be checked</i>.</para>
/// </summary>
public static class LifecycleCheck
{
    /// <summary>How many lifecycle entries before a post a reader will fold (spec section 4.8).</summary>
    public const int MaxEntriesBeforePost = 1024;

    private const string RetiredType = "agent.key-retired";
    private const string CompromisedType = "agent.key-compromised";

    /// <summary>
    /// Every statement <c>typ</c> this reader knows (KF4). A <c>typ</c> outside it is a version or a kind this reader
    /// cannot fold; one inside it, in the other kind's entry, is a statement that is not this entry's.
    /// </summary>
    private static readonly string[] KnownStatementTyps =
        [KeyStatements.RotationStatementTyp, KeyStatements.RetirementStatementTyp, KeyStatements.CompromiseStatementTyp];

    private static readonly string[] EnrolmentBindingMembers = ["agent_id", "jwk", "kid"];
    private static readonly string[] RotationEntryMembers = ["agent_id", "authorization", "authorized_by", "jwk", "kid", "possession", "prev", "statement"];
    private static readonly string[] RetirementEntryMembers = ["agent_id", "authorization", "kid", "prev", "statement"];
    private static readonly string[] CompromiseEntryMembers = ["agent_id", "declaration", "from_index", "kids", "prev", "reason", "statement"];

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
    /// <param name="logKeys">
    /// The log's key set (R6.50), which a declaration is verified under (KF2): its <c>log_index</c> says where each key was
    /// published, and a declaration is valid only under a key published before it.
    /// </param>
    public static Check Verify(
        ProvenancePost post,
        LogEntryDocument postEntry,
        IReadOnlyList<(LogEntryDocument Entry, InclusionProofDocument Proof)> lifecycle,
        SignedHeadDocument head,
        ImmutableArray<LogJwk> logKeys) =>
        Fold(post, postEntry, lifecycle, head, logKeys).Check;

    internal static LifecycleVerdict Fold(
        ProvenancePost post,
        LogEntryDocument postEntry,
        IReadOnlyList<(LogEntryDocument Entry, InclusionProofDocument Proof)> lifecycle,
        SignedHeadDocument head,
        ImmutableArray<LogJwk> logKeys)
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

        // Every supplied entry is folded, in log order: the entries from the first at or after the post to a declaration
        // that names the post's key are supplied so that the declaration's link and cascade are held to the chain (R6.57).
        var fold = new Folder(logged.Author, head, logKeys);
        foreach (var (entry, proof) in lifecycle)
        {
            if (fold.Apply(entry, proof) is { } refusal) return refusal;
        }

        // The post's key. Retired before the post is a failure whatever else holds.
        if (fold.Retired.TryGetValue(logged.Kid, out var retiredAt) && retiredAt < logged.Index)
            return Failed("retired-before-post", Text(
                $"the author's key {Check.Quote(logged.Kid)} was retired at log index {retiredAt}, before the post at {logged.Index}"));

        // A declaration naming the post's key that precedes the post: a compromised key is honoured for nothing at or after
        // the declaration's position (R6.57), so the post could not have been accepted under it as the author's.
        if (fold.Declarations.FirstOrDefault(d => d.Kids.Contains(logged.Kid, StringComparer.Ordinal) && d.Index < logged.Index) is { } before)
            return Failed("compromised-before-post", Text(
                $"the author's key {Check.Quote(logged.Kid)} was declared compromised by the declaration at log index {before.Index}, before the post at {logged.Index}"));

        if (fold.Bound.TryGetValue(logged.Kid, out var bound) && bound.At < logged.Index)
        {
            // R6.57: disputed is by log position, [from_index, the declaration's index): the signature holds and who made it is in question.
            if (fold.Declarations.FirstOrDefault(d =>
                    d.Kids.Contains(logged.Kid, StringComparer.Ordinal) && d.FromIndex <= logged.Index && logged.Index < d.Index) is { } disputing)
                return new LifecycleVerdict(
                    Check.Disputed(Text(
                        $"the signature holds, and the author's key {Check.Quote(logged.Kid)} was declared compromised from log index {disputing.FromIndex} by the declaration at log index {disputing.Index}, which follows this post at {logged.Index} (R6.57); do not cite it as the author's")),
                    "disputed");

            return new LifecycleVerdict(
                Check.Verified(Text(
                    $"the author's key {Check.Quote(logged.Kid)} was bound at log index {bound.At} and was live at the post's {logged.Index}; {fold.Count} lifecycle entries checked through log index {fold.PrevIndex}")),
                "verified");
        }

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

    private sealed record Rotated(long Index, string AuthorizedBy, string Kid);

    /// <summary>A declaration that held under KF1 to KF4 and KF3: where it sits, where it states the compromise from, and whom it names.</summary>
    private sealed record Declared(long Index, long FromIndex, IReadOnlyList<string> Kids);

    private sealed class Folder(string author, SignedHeadDocument head, ImmutableArray<LogJwk> logKeys)
    {
        private bool _firstWasEnrolment;
        private string? _enrolmentKid;

        public Dictionary<string, BoundKey> Bound { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> Retired { get; } = new(StringComparer.Ordinal);

        /// <summary>Every rotation folded so far, in log order: the material R6.57's cascade is recomputed from.</summary>
        private List<Rotated> Rotations { get; } = [];

        public List<Declared> Declarations { get; } = [];

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
                CompromisedType => Kind.Compromised,
                _ => (Kind?)null,
            };
            if (kind is null)
                return CouldNotCheck("unknown-lifecycle-kind", Text(
                    $"the lifecycle lists an entry at log index {entry.LogIndex} of type {Check.Quote(type)}, none of the five a reader can fold (KF4)"));

            var payload = ClientJson.Object(entry.Entry, LogLeaf.PayloadMember);
            if (payload is null)
                return Failed("statement-mismatch", Text($"the lifecycle entry at log index {entry.LogIndex} carries no payload"));

            var statementText = ClientJson.String(payload, "statement");

            // The statement's typ, read from the still-unverified statement only to choose which rules apply: another
            // version of this kind is not one this reader knows (KF4); a typ of another kind is not this entry's.
            if (statementText is not null && ExpectedStatementTyp(kind.Value) is { } expected
                && StatementTyp(statementText) is { } typ)
            {
                if (!string.Equals(typ, expected, StringComparison.Ordinal))
                    return KnownStatementTyps.Contains(typ, StringComparer.Ordinal)
                        ? Mismatch(entry.LogIndex, "the statement is not this entry's kind")
                        : CouldNotCheck("unknown-lifecycle-kind", Text(
                            $"the statement at log index {entry.LogIndex} is of type {Check.Quote(typ)}, which this reader does not know (KF4)"));
            }

            if (entry.LogIndex != proof.LogIndex)
                return Failed("inclusion-invalid", Text(
                    $"the lifecycle entry is leaf {entry.LogIndex} and its proof is about leaf {proof.LogIndex}"));

            // KF4: a reason outside the closed set is a reason this reader does not know, never verified and never skipped.
            if (kind is Kind.Compromised && statementText is not null && UnknownReason(statementText) is { } reason)
                return CouldNotCheck("unknown-reason", Text(
                    $"the declaration at log index {entry.LogIndex} states the reason {Check.Quote(reason)}, outside the closed set this reader knows (KF4)"));

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
                Kind.Compromised => Declaration(payload, entry.LogIndex),
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
            Rotations.Add(new Rotated(index, statement.AuthorizedBy, statement.Kid));
            return null;
        }

        private LifecycleVerdict? Retirement(JsonValue.Object payload, long index)
        {
            if (!HasExactly(payload, RetirementEntryMembers))
                return Mismatch(index, "the entry's member set is not a retirement's");

            var text = ClientJson.String(payload, "statement");
            if (text is null) return Mismatch(index, "the entry carries no statement");
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

        /// <summary>
        /// A declaration (R6.57, spec 4.5, 4.6), in KF4's order after its type, statement <c>typ</c> and reason: the closed member
        /// set, the statement, the link, KF1 (header <c>typ</c>), KF2 (header <c>kid</c> in the log's key set, published before the
        /// declaration), the signature, then KF3: (1) every kid is bound to the author in the supplied stream, (2) binding of
        /// <c>kids[0]</c> at or below <c>from_index</c>, which is below the declaration's index, (3) <c>kids</c> equal, in order, the
        /// cascade recomputed from the lifecycle. A declaration that fails any of them never disputes a post.
        /// </summary>
        private LifecycleVerdict? Declaration(JsonValue.Object payload, long index)
        {
            if (!HasExactly(payload, CompromiseEntryMembers))
                return Mismatch(index, "the entry's member set is not a declaration's");

            var text = ClientJson.String(payload, "statement");
            if (text is null) return Mismatch(index, "the entry carries no statement");
            if (!KeyStatements.ReadCompromise(Encoding.UTF8.GetBytes(text)).TryGetValue(out var statement, out _))
                return Mismatch(index, "the statement is not a compromise statement of the closed form (KF4)");

            if (!string.Equals(ClientJson.String(payload, "agent_id"), statement!.AgentId, StringComparison.Ordinal)
                || ClientJson.WholeNumber(payload, "from_index") != statement.FromIndex
                || ClientJson.WholeNumber(payload, "prev") != statement.Prev
                || !string.Equals(ClientJson.String(payload, "reason"), CompromiseReasons.Wire(statement.Reason), StringComparison.Ordinal)
                || ClientJson.Member(payload, "kids") is not JsonValue.Array kidItems
                || !kidItems.Items.Select(k => (k as JsonValue.String)?.Value).SequenceEqual(statement.Kids, StringComparer.Ordinal))
                return Mismatch(index, "an entry member differs from its statement's");

            if (LinkFailure(statement.Prev, index) is { } broken) return broken;

            // KF1, then KF2: the declaration carries a declaration's header typ, and names a key the log published before it.
            var declaration = new JwsSignature(ClientJson.String(payload, "declaration") ?? string.Empty);
            if (!HasHeaderTyp(declaration, KeyStatements.CompromiseHeaderTyp))
                return new LifecycleVerdict(Check.Failed(Text($"the signature of the declaration at log index {index} does not carry a declaration's header typ (KF1)")), "lifecycle-typ-mismatch");

            if (!DetachedJws.ReadProtectedHeader(declaration).TryGetValue(out var header, out _) || header!.Kid is null)
                return Failed("declaration-key-unpublished", Text($"the declaration at log index {index} names no signing key"));

            var logKey = logKeys.IsDefault ? null : logKeys.LastOrDefault(k => string.Equals(k.Key.Kid, header.Kid, StringComparison.Ordinal));
            if (logKey is null)
                return Failed("declaration-key-unpublished", Text(
                    $"the declaration at log index {index} is signed under kid={Check.Quote(header.Kid)}, which the log's key set does not publish (KF2)"));
            if (logKey.LogIndex < 0)
                return CouldNotCheck("log-key-position-unknown", Text(
                    $"the log's key set gives kid={Check.Quote(header.Kid)} no log index, so this reader cannot say it was published before the declaration at {index} (KF2)"));
            if (logKey.LogIndex >= index)
                return Failed("declaration-key-unpublished", Text(
                    $"the declaration at log index {index} is signed under kid={Check.Quote(header.Kid)}, published at log index {logKey.LogIndex}, which is not before it (KF2)"));

            if (!SignatureCheck.Material(logKey.Key).TryGetValue(out var logMaterial, out _)
                || !CanonicalOf(text).TryGetValue(out var canonical, out _)
                || !Signed(declaration, canonical, KeyStatements.CompromiseHeaderTyp, logMaterial!))
                return Failed("declaration-signature-invalid", Text(
                    $"the declaration at log index {index} does not verify under the log key kid={Check.Quote(header.Kid)}"));

            // KF3 (1): every kid is bound to the author in the stream the reader holds. A kid with no binding there, and a kid
            // whose only binding is not supplied, are alike unbound: the reader is served the author's stream and no other.
            var kids = statement.Kids;
            if (kids.FirstOrDefault(k => !Bound.ContainsKey(k)) is { } unbound)
                return Failed("kid-not-bound-to-agent", Text(
                    $"the declaration at log index {index} names kid={Check.Quote(unbound)}, which no entry of the author's stream binds (KF3)"));

            // KF3 (2): binding(kids[0]) <= from_index < the declaration's own index, strictly.
            if (Bound[kids[0]].At > statement.FromIndex)
                return Failed("declaration-mismatch", Text(
                    $"the declaration at log index {index} states from_index {statement.FromIndex}, below the binding of kid={Check.Quote(kids[0])} at {Bound[kids[0]].At} (KF3)"));
            if (statement.FromIndex >= index)
                return Failed("declaration-mismatch", Text(
                    $"the declaration at log index {index} states from_index {statement.FromIndex}, which is not below its own index (KF3)"));

            // KF3 (3): kids equal, in order, the cascade this reader recomputes. Its root is the earliest-bound key in kids (never
            // the post's key, never kids[0] by position); each rotation at or after from_index authorized by a key already in it
            // adds its key, in log order, and a rotation's authorizer was bound before it, so one pass is the closure.
            var cascade = new List<string> { kids.MinBy(k => Bound[k].At)! };
            foreach (var rotation in Rotations)
            {
                if (rotation.Index >= statement.FromIndex && cascade.Contains(rotation.AuthorizedBy, StringComparer.Ordinal) && !cascade.Contains(rotation.Kid, StringComparer.Ordinal))
                    cascade.Add(rotation.Kid);
            }

            if (!kids.SequenceEqual(cascade, StringComparer.Ordinal))
                return Failed("declaration-mismatch", Text(
                    $"the declaration at log index {index} lists {kids.Count} key(s) and the cascade this reader recomputes from the lifecycle is {cascade.Count}, or the order differs (KF3)"));

            Declarations.Add(new Declared(index, statement.FromIndex, kids));
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
        Compromised,
    }

    /// <summary>The statement <c>typ</c> an entry of this kind carries; null for the enrolment, which carries none.</summary>
    private static string? ExpectedStatementTyp(Kind kind) => kind switch
    {
        Kind.Enrolled => null,
        Kind.Bound => KeyStatements.RotationStatementTyp,
        Kind.Retired => KeyStatements.RetirementStatementTyp,
        Kind.Compromised => KeyStatements.CompromiseStatementTyp,
        _ => null,
    };

    private static LifecycleVerdict Mismatch(long index, string what) =>
        Failed("statement-mismatch", Text($"the lifecycle entry at log index {index} disagrees with its statement or its set: {what}"));

    private static LifecycleVerdict Unauthorized(long index) =>
        Failed("rotation-unauthorized", Text($"the entry at log index {index} is not signed by a key that was live there, in the role its statement names"));

    private static bool HasExactly(JsonValue.Object payload, string[] names) =>
        payload.Members.Length == names.Length
        && payload.Members.Select(m => m.Key).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>The statement's <c>reason</c> when it is a string outside the closed set, else null (KF4).</summary>
    private static string? UnknownReason(string statement) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).TryGetValue(out var root, out _)
        && root is JsonValue.Object o
        && ClientJson.String(o, "reason") is { } reason
        && !CompromiseReasons.Parse(reason).IsOk
            ? reason
            : null;

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
