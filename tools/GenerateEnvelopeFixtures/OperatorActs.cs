using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using static GenerateEnvelopeFixtures.JsonBuilders;
using JsonValue = Curia.Canon.Json.JsonValue;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// conformance/operator-acts/ (errata G70, R6.67): small whole logs, each with the verdict the Forum's procedure must reach for every operator-act row
/// (an <c>agent.owner-attested</c> row in an agent's stream, a <c>moderation.applied</c> row in a post's stream), and the state the honoured rows decide: for an
/// attestation case the agent's owner and whether it is verified, for a moderation case whether the post is served. Run by <c>--operator-acts-cases</c>.
///
/// <para><b>Each case's <c>expected.json</c> is written from the case's construction, below, and never by running a fold.</b> There is no fold to run: the family
/// is the specification a second implementation is built from. Each case holds one defect, so the predicate cannot come from another cause, and every
/// <c>prev</c> names the last row the procedure honours before it (the row's own predicate is the only thing that decides it).</para>
///
/// <para>The keys are derived from the text of their label (a SHA-256 over it is the P-256 scalar), so the public keys and their thumbprints are reproducible.
/// ES256 signing is randomized, so a signature's bytes are kept from the existing output when its statement, its header kid and its signer are the same
/// (the pin <see cref="OaPinnedAuthorization"/> reads and verifies), and the generator signs only what no existing row pins: a regeneration over the existing
/// output is byte-identical. A statement is the exact output of <c>CanonicalizeWithNfc</c>; a signature is detached under header <c>typ</c>
/// <c>curia-operator-act+jws</c> with <c>kid</c> the key's thumbprint. "Garbage" is the string <c>FORGED-NO-VALID-SIGNATURE</c>, which is not a JWS; "bad signature" is
/// a well-formed JWS of the right header whose signature bytes are another key's.</para>
/// </summary>
internal static partial class LifecycleCases
{
    private const string OaAttestTyp = "curia.owner-attestation/1";
    private const string OaModerationTyp = "curia.moderation/1";
    private const string OaHeaderTyp = "curia-operator-act+jws";
    private const string OaActor = "operator:ops";
    private const string OaOperatorKeyType = "log.operator-key";
    private const string OaOperatorKeysAggregate = "log:operator-keys";
    private const string OaAttested = "agent.owner-attested";
    private const string OaModerated = "moderation.applied";
    private const string OaPost = "01K4CQ4B0000000000000000P1";
    private const string OaPost2 = "01K4CQ4B0000000000000000P2";
    private const string OaDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string OaDigest2 = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string OaAlice = "owner:alice";
    private const string OaBob = "owner:bob";
    private const string OaSkippedAuthorization = "FORGED-NO-VALID-SIGNATURE";

    private static Dictionary<string, List<string>> _oaPins = new(StringComparer.Ordinal);

    private sealed record OaRow(int Index, string Type, string? Predicate)
    {
        public static OaRow Honoured(int index, string type) => new(index, type, null);

        public static OaRow Skipped(int index, string type, string predicate) => new(index, type, predicate);
    }

    /// <summary>One operator-act case. <paramref name="State"/> is the expected state beside the rows: owner and verified for an agent's stream, served for a post's.</summary>
    private sealed record OaCase(string Name, string Note, string Subject, string StreamKind, Log Log, IReadOnlyList<OaRow> Rows, JsonObject State, int? PrimitiveCalls = null);

    /// <summary>The bytes an authorization's signature is over: the canonical form after the encoded header and a full stop (R6.37, RFC 7797), or its base64url (RFC 7515's attached form).</summary>
    private enum OaInput
    {
        Canonical,
        Encoded,
    }

    /// <summary>
    /// How an authorization departs from the genuine one (the ladder of vectors 39 to 47 and the arms of 48 to 59). <see cref="Auth"/> builds the authorization from a header
    /// the case chooses; <see cref="NoAuthorization"/> leaves the member out; <see cref="TextNotNfc"/> and <see cref="TextTypFirst"/> change the statement's text while the
    /// signature stays over the canonical form of the intended statement; <see cref="StatementMembers"/> replace or add members of the statement only; <see cref="OmitRow"/>
    /// leaves members out of the row, so the member under test is stated by the statement alone.
    /// </summary>
    private sealed record OaForm
    {
        public OaAuth? Auth { get; init; }
        public bool NoAuthorization { get; init; }
        public bool TextNotNfc { get; init; }
        public bool TextTypFirst { get; init; }
        public IReadOnlyDictionary<string, JsonValue>? StatementMembers { get; init; }
        public IReadOnlyList<string>? OmitRow { get; init; }
    }

    /// <summary>A protected header the case chooses. The defaults are the header of a genuine authorization: <c>alg</c> ES256, <c>kid</c> the signer's, <c>typ</c> curia-operator-act+jws, <c>b64</c> false, <c>crit</c> [b64].</summary>
    private sealed record OaAuth
    {
        public string? Typ { get; init; }
        public string? Alg { get; init; }
        public bool OmitKid { get; init; }
        public bool Attached { get; init; }
        public P256LogKey? Signer { get; init; }

        /// <summary>The protected header's text as the case writes it (a repeated member, no <c>b64</c>, no <c>crit</c>); it replaces the composed header.</summary>
        public string? HeaderText { get; init; }

        /// <summary>The signing input the signature is made over.</summary>
        public OaInput Input { get; init; } = OaInput.Canonical;

        /// <summary>When set, the generator asserts the signature verifies under the signer over the signing input of this form (the vector is valid under the other reading: R6.37's header shape is what refuses it).</summary>
        public OaInput? AssertValidUnder { get; init; }

        /// <summary>When set, the signature part is these bytes and nothing is signed (a row whose key cannot verify anything).</summary>
        public byte[]? FixedSignature { get; init; }
    }

    /// <summary>Every member an attestation row states beside <c>statement</c> and <c>authorization</c>: a row that omits all of them states only the two (vector 73).</summary>
    private static readonly string[] AttestationRowMembers = ["agent_id", "method", "owner_id", "owner_verified", "prev", "reason"];

    /// <summary>Every member a moderation row states beside <c>statement</c> and <c>authorization</c> (vector 74).</summary>
    private static readonly string[] ModerationRowMembers = ["actor_id", "adjudicates", "category", "digest", "effect", "moderator", "post_id", "prev", "rationale"];

    private static readonly OaAuth Stranger = new() { Typ = "curia-head+jws", Signer = ConformanceLogKeys.Unpublished };

    /// <summary>An attestation row to build. The defaults are a genuine attestation by <c>operator:ops</c> signed by the operator key.</summary>
    private sealed record OaAttest(string Owner, bool Verified, long Prev)
    {
        public string Agent { get; init; } = LifecycleCases.Agent;
        public string Aggregate { get; init; } = LifecycleCases.Agent;
        public string EventActor { get; init; } = OaActor;
        public string StatementActor { get; init; } = OaActor;
        public string StatementTyp { get; init; } = OaAttestTyp;
        public P256LogKey? Signer { get; init; }
        public string? HeaderKid { get; init; }
        public string? Garbage { get; init; }
        public bool NoStatement { get; init; }
        public string? RowOwner { get; init; }
        public string? ExtraRowMember { get; init; }
        public JsonValue? StatementOverride { get; init; }
        public string Reason { get; init; } = "owner verified by hand";
        public string? RowReason { get; init; }
        public string Method { get; init; } = "manual";
        public OaForm Form { get; init; } = new();
    }

    /// <summary>A moderation row to build. The defaults are a genuine human withhold of post P by <c>operator:ops</c> signed by the operator key.</summary>
    private sealed record OaModerate(string Effect, long Prev)
    {
        public string Post { get; init; } = OaPost;
        public string Aggregate { get; init; } = OaPost;
        public string Moderator { get; init; } = "human";
        public string StatementTyp { get; init; } = OaModerationTyp;
        public string Category { get; init; } = "spam";
        public string StatementDigest { get; init; } = OaDigest;
        public string? RowDigest { get; init; }
        public P256LogKey? Signer { get; init; }
        public string? HeaderKid { get; init; }
        public bool NoStatement { get; init; }
        public OaForm Form { get; init; } = new();
    }

    public static int RunOperatorActs(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "conformance", "operator-acts");
        Directory.CreateDirectory(root);
        _oaPins = LoadOaPins(root);

        var o = OaKey("operator");
        var o2 = OaKey("second-operator");
        var stranger = ConformanceLogKeys.Unpublished;
        var l = ConformanceLogKeys.Log;
        var d = ConformanceLogKeys.Declaration;

        using var firstDoc = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repoRoot, "conformance", "envelope", "ed25519-minimal", "private-keys.json")));
        var k0 = Ed("conformance-ed25519-minimal", Jwk.Base64UrlDecode(firstDoc.RootElement.GetProperty("keys")[0].GetProperty("d").GetString()!));
        var ky = Ed("conformance-ed25519-other-identity", Enumerable.Repeat((byte)0x0A, 32).ToArray());

        OaSelfCheck(o);

        var shared = OaKey("shared-p");
        var keyQ = OaKey("key-q");
        var keyR = OaKey("key-r");
        var offCurve = OaOffCurveKey(o);

        var a1 = new OaAttest(OaAlice, true, 0);
        var w = new OaModerate("withhold", 3);

        var cases = new List<OaCase>
        {
            // 1 to 25: attestations
            OaA("attestation-honoured", "A genuine attestation, signed by the first published operator key: honoured, and the owner is bound and verified.",
                R(k0, o), log => log.Add(a1, o), [OaRow.Honoured(3, OaAttested)], Alice(true)),
            OaA("attestation-then-lapse", "A genuine attestation at 3, then a signed lapse at 4 (verified false, prev 3): both honoured, the owner stays and is not verified.",
                R(k0, o), log => { log.Add(a1, o); log.Add(a1 with { Verified = false, Prev = 3 }, o); }, [OaRow.Honoured(3, OaAttested), OaRow.Honoured(4, OaAttested)], Alice(false)),
            OaA("attestation-replayed-after-lapse", "The attestation of 3 and the lapse of 4, then the first attestation's statement and signature again at 5: its prev is 0 and the previous honoured act is 4, so it is skipped and the owner stays not verified.",
                R(k0, o), log => { log.Add(a1, o); log.Add(a1 with { Verified = false, Prev = 3 }, o); log.Add(a1, o); },
                [OaRow.Honoured(3, OaAttested), OaRow.Honoured(4, OaAttested), OaRow.Skipped(5, OaAttested, "act-broken-link")], Alice(false)),
            OaA("attestation-unsigned", "A row with the members of a genuine attestation and no statement and no authorization, as every row written before this entry: skipped, and nothing is bound.",
                R(k0, o), log => log.Add(a1 with { NoStatement = true }, o), [OaRow.Skipped(3, OaAttested, "act-unsigned")], NoOwner()),
            OaA("attestation-payload-not-an-object", "An agent.owner-attested row whose payload is a JSON string: skipped, not unreadable.",
                R(k0, o), log => log.Add(OaAttested, Agent, OaActor, Str("not an object")), [OaRow.Skipped(3, OaAttested, "act-statement-mismatch")], NoOwner()),
            OaA("attestation-garbage-authorization", "A genuine statement whose authorization is the string FORGED-NO-VALID-SIGNATURE, which is no JWS: it carries no operator-act header typ.",
                R(k0, o), log => log.Add(a1 with { Garbage = OaSkippedAuthorization }, o), [OaRow.Skipped(3, OaAttested, "act-typ-mismatch")], NoOwner()),
            OaA("attestation-signed-by-log-key", "A genuine statement signed by the log key L, whose own log.key entry is at 3: L is no operator key, so the header kid names no operator key published before the row.",
                R(k0, o), log => { log.PublishOaKey("log.key", "log:keys", l); log.Add(a1, l); }, [OaRow.Skipped(4, OaAttested, "act-key-unpublished")], NoOwner()),
            OaA("attestation-signed-by-declaration-key", "A genuine statement signed by the declaration key D, whose own log.declaration-key entry is at 3: D is no operator key.",
                R(k0, o), log => { log.PublishOaKey("log.declaration-key", "log:declaration-keys", d); log.Add(a1, d); }, [OaRow.Skipped(4, OaAttested, "act-key-unpublished")], NoOwner()),
            OaA("operator-key-published-after-the-row", "The attestation at 2 is signed by O, whose log.operator-key entry is at 3: the key was not published at an earlier log index than the row.",
                E(k0), log => { log.Add(a1, o); log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); }, [OaRow.Skipped(2, OaAttested, "act-key-unpublished")], NoOwner()),
            OaA("operator-key-already-a-log-key", "O is published as a log key at 2 and as an operator key at 3: the key's class is the class under which the log first published it, so the operator entry holds no position and the attestation at 4 names no operator key.",
                E(k0), log => { log.PublishOaKey("log.key", "log:keys", o); log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); log.Add(a1, o); },
                [OaRow.Skipped(4, OaAttested, "act-key-unpublished")], NoOwner()),
            OaA("operator-key-republished-as-a-log-key-later", "A genuine attestation at 3, then O's public key published as a log key at 4: a later publication in another class changes nothing an earlier row was decided by.",
                R(k0, o), log => { log.Add(a1, o); log.PublishOaKey("log.key", "log:keys", o); }, [OaRow.Honoured(3, OaAttested)], Alice(true)),
            OaA("second-operator-key-is-not-the-key", "A second log.operator-key (O2) at 3 and an attestation at 4 signed by it: the operator key is the first, so the header kid names one that is not the first.",
                R(k0, o), log => { log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o2); log.Add(a1, o2); }, [OaRow.Skipped(4, OaAttested, "act-key-not-first")], NoOwner()),
            OaA("malformed-operator-key-entry-holds-no-position", "A log.operator-key entry with no jwk at 2 does not read as a key and holds no position: the entry at 3 is the first operator key, and the attestation at 4 signed by it is honoured.",
                E(k0), log => { log.Add(OaOperatorKeyType, OaOperatorKeysAggregate, "operator", Obj(("kid", Str("no-key")), ("valid_from", Str("2026-09-01T00:00:00Z")))); log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); log.Add(a1, o); },
                [OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-bad-signature", "A genuine statement whose authorization has the right header (the operator-act typ and O's kid) and a stranger's signature bytes.",
                R(k0, o), log => log.Add(a1 with { Signer = stranger, HeaderKid = o.Kid }, o), [OaRow.Skipped(3, OaAttested, "act-signature-invalid")], NoOwner()),
            OaA("attestation-wrong-prev", "A genuine attestation whose prev is 1; the stream's root is at 0 and no act precedes it.",
                R(k0, o), log => log.Add(a1 with { Prev = 1 }, o), [OaRow.Skipped(3, OaAttested, "act-broken-link")], NoOwner()),
            OaA("attestation-prev-names-a-skipped-row", "A forged attestation at 3 (garbage authorization) and a genuine one at 4 whose prev is 3: a skipped row moves nothing, so the previous honoured entry is the root at 0.",
                R(k0, o), log => { log.Add(a1 with { Garbage = OaSkippedAuthorization }, o); log.Add(a1 with { Prev = 3 }, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Skipped(4, OaAttested, "act-broken-link")], NoOwner()),
            OaA("skipped-row-does-not-move-the-chain", "A forged attestation naming Bob at 3 (garbage authorization), then the genuine attestation of Alice at 4 whose prev is 0: the forged row moved nothing, and the first honoured row binds the owner.",
                R(k0, o), log => { log.Add(a1 with { Owner = OaBob, Garbage = OaSkippedAuthorization }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-names-another-agent", "Y's enrolment and binding at 3 and 4, then an attestation in X's stream at 5 whose statement names Y: the statement's subject is not the stream's aggregate.",
                R(k0, o), log =>
                {
                    log.Add("agent.enrolled", OtherAgent, OtherAgent, Obj(("agent_id", Str(OtherAgent)), ("kid", Str(ky.Kid))));
                    log.Add("agent.key-bound", OtherAgent, OtherAgent, Obj(("agent_id", Str(OtherAgent)), ("jwk", ky.Jwk), ("kid", Str(ky.Kid))));
                    log.Add(a1 with { Agent = OtherAgent }, o);
                }, [OaRow.Skipped(5, OaAttested, "act-statement-mismatch")], NoOwner()),
            OaA("attestation-payload-differs-from-statement", "A genuine attestation whose row states owner_id Bob and whose statement says Alice.",
                R(k0, o), log => log.Add(a1 with { RowOwner = OaBob }, o), [OaRow.Skipped(3, OaAttested, "act-statement-mismatch")], NoOwner()),
            OaA("attestation-actor-differs-from-statement", "A genuine attestation whose event's actor is operator:other and whose statement says operator:ops.",
                R(k0, o), log => log.Add(a1 with { EventActor = "operator:other" }, o), [OaRow.Skipped(3, OaAttested, "act-statement-mismatch")], NoOwner()),
            OaA("attestation-extra-member", "A genuine attestation whose row carries a member note beside the statement's members: a member that is neither statement, authorization nor a statement member.",
                R(k0, o), log => log.Add(a1 with { ExtraRowMember = "note" }, o), [OaRow.Skipped(3, OaAttested, "act-statement-mismatch")], NoOwner()),
            OaA("attestation-unknown-statement-version", "An attestation whose statement typ is curia.owner-attestation/9, a version the Forum does not know, signed over that statement.",
                R(k0, o), log => log.Add(a1 with { StatementTyp = "curia.owner-attestation/9" }, o), [OaRow.Skipped(3, OaAttested, "act-unknown-kind")], NoOwner()),
            OaA("attestation-carries-a-moderation-statement", "An agent.owner-attested row whose statement is a moderation statement (curia.moderation/1), signed by O: the other type's current typ.",
                R(k0, o), log => log.Add(a1 with { StatementOverride = ModerationStatement(new OaModerate("withhold", 3)) }, o), [OaRow.Skipped(3, OaAttested, "act-statement-mismatch")], NoOwner()),
            OaA("attestation-with-no-root", "An attestation in X's stream when the log holds no agent.enrolled for X: the stream has no root.",
                new Log(), log => { log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); log.Add(a1, o); }, [OaRow.Skipped(1, OaAttested, "act-broken-link")], NoOwner()),
            OaA("second-owner-is-honoured-and-does-not-rebind", "An attestation naming Alice at 3 and one naming Bob at 4 (prev 3): both are honoured as acts, and the second binds nothing (R4.1).",
                R(k0, o), log => { log.Add(a1, o); log.Add(a1 with { Owner = OaBob, Prev = 3 }, o); }, [OaRow.Honoured(3, OaAttested), OaRow.Honoured(4, OaAttested)], Alice(true)),

            // 26 to 37: moderation
            OaM("moderation-honoured", "A genuine human withhold of post P, prev naming the post's root at 3: honoured, and the post is not served.",
                Q(k0, o), log => log.Add(w, o), [OaRow.Honoured(4, OaModerated)], false),
            OaM("moderation-withhold-then-restore", "A genuine withhold at 4 and a genuine restore at 5 (prev 4): both honoured, and the post is served.",
                Q(k0, o), log => { log.Add(w, o); log.Add(new OaModerate("restore", 4), o); }, [OaRow.Honoured(4, OaModerated), OaRow.Honoured(5, OaModerated)], true),
            OaM("moderation-replayed-withhold-after-restore", "As the withhold-then-restore case, then the withhold's statement and signature again at 6: its prev is 3 and the previous honoured act is 5.",
                Q(k0, o), log => { log.Add(w, o); log.Add(new OaModerate("restore", 4), o); log.Add(w, o); },
                [OaRow.Honoured(4, OaModerated), OaRow.Honoured(5, OaModerated), OaRow.Skipped(6, OaModerated, "act-broken-link")], true),
            OaM("moderation-unsigned", "A moderation.applied row with the members of a genuine withhold and no statement and no authorization: skipped, and the post is served.",
                Q(k0, o), log => log.Add(w with { NoStatement = true }, o), [OaRow.Skipped(4, OaModerated, "act-unsigned")], true),
            OaM("moderation-signed-for-another-post", "A withhold in post P's stream whose statement names another post: the statement's subject is not the stream's aggregate.",
                Q(k0, o), log => log.Add(w with { Post = OaPost2 }, o), [OaRow.Skipped(4, OaModerated, "act-statement-mismatch")], true),
            OaM("moderation-unknown-statement-version", "A withhold whose statement typ is curia.moderation/2, a version the Forum does not know, signed over that statement.",
                Q(k0, o), log => log.Add(w with { StatementTyp = "curia.moderation/2" }, o), [OaRow.Skipped(4, OaModerated, "act-unknown-kind")], true),
            OaM("moderation-bad-signature", "A genuine withhold whose authorization has the right header (the operator-act typ and O's kid) and a stranger's signature bytes.",
                Q(k0, o), log => log.Add(w with { Signer = stranger, HeaderKid = o.Kid }, o), [OaRow.Skipped(4, OaModerated, "act-signature-invalid")], true),
            OaM("moderation-digest-differs-from-statement", "A genuine withhold whose row states another post's digest and whose statement states P's.",
                Q(k0, o), log => log.Add(w with { RowDigest = OaDigest2 }, o), [OaRow.Skipped(4, OaModerated, "act-statement-mismatch")], true),
            OaM("moderation-moderator-automated-is-not-honoured", "A withhold whose statement and row say moderator automated, signed by O: curia.moderation/1 honours only human, because no automated actor holds a key.",
                Q(k0, o), log => log.Add(w with { Moderator = "automated" }, o), [OaRow.Skipped(4, OaModerated, "act-statement-mismatch")], true),
            OaM("moderation-moderator-delegated-agent-is-not-honoured", "A withhold whose statement and row say moderator delegated_agent, signed by O: no delegation exists, so only human is honoured.",
                Q(k0, o), log => log.Add(w with { Moderator = "delegated_agent" }, o), [OaRow.Skipped(4, OaModerated, "act-statement-mismatch")], true),
            OaM("moderation-forged-restore-after-a-genuine-withhold", "A genuine withhold at 4, then an unsigned restore at 5 (members only): the forged restore is skipped and the post stays withheld.",
                Q(k0, o), log => { log.Add(w, o); log.Add(new OaModerate("restore", 4) with { NoStatement = true }, o); }, [OaRow.Honoured(4, OaModerated), OaRow.Skipped(5, OaModerated, "act-unsigned")], false),
            OaM("moderation-forged-row-between-genuine-ones", "A genuine withhold at 4, an unsigned restore at 5, then a genuine restore at 6 whose prev is 4: the forged row moved nothing, so the genuine restore links to the withhold.",
                Q(k0, o), log => { log.Add(w, o); log.Add(new OaModerate("restore", 4) with { NoStatement = true }, o); log.Add(new OaModerate("restore", 4), o); },
                [OaRow.Honoured(4, OaModerated), OaRow.Skipped(5, OaModerated, "act-unsigned"), OaRow.Honoured(6, OaModerated)], true),

            // 38
            OaA("attestation-second-enrolment-row-is-not-the-root", "A second agent.enrolled for X at 3 and an attestation at 4 whose prev is 0: the stream's root is its first event, so the second enrolment is not the root and the attestation links to 0.",
                R(k0, o), log => { log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k0.Kid)))); log.Add(a1, o); }, [OaRow.Honoured(4, OaAttested)], Alice(true)),

            // 39 to 47: the ladder. Each row fails the step named and every step after it ("Stranger": header typ curia-head+jws, header kid and signature the unpublished stranger's), and ends with a control, a genuine row
            // whose prev names the stream's root, because a skipped row moves nothing: a fold that skips every row fails the case. Vector 39 has no root to link to and so no control.
            OaA("order-unsigned-before-broken-link", "An attestation in X's stream with no statement and the string FORGED-NO-VALID-SIGNATURE as its authorization, when the log holds no agent.enrolled for X: step 1 (no statement) decides before step 4 (no root).",
                new Log(), log => { log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); log.Add(a1 with { NoStatement = true, Garbage = OaSkippedAuthorization }, o); }, [OaRow.Skipped(1, OaAttested, "act-unsigned")], NoOwner()),
            OaA("order-unknown-kind-before-mismatch", "A statement of typ curia.owner-attestation/9 that also carries a member note, with prev 1 and a stranger's header and signature, then a genuine attestation at 4: step 2 decides before steps 3 to 7.",
                R(k0, o), log => { log.Add(a1 with { StatementTyp = "curia.owner-attestation/9", Prev = 1, Form = new OaForm { Auth = Stranger, StatementMembers = Members(("note", Str("not a member of the closed form"))) } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-unknown-kind"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("order-mismatch-before-broken-link", "A statement that carries a member note (not the closed form) with prev 1 and a stranger's header and signature, then a genuine attestation at 4: step 3 decides before steps 4 to 7.",
                R(k0, o), log => { log.Add(a1 with { Prev = 1, Form = new OaForm { Auth = Stranger, StatementMembers = Members(("note", Str("not a member of the closed form"))) } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("order-broken-link-before-typ-mismatch", "A closed statement whose prev is 1 (the root is at 0) with a stranger's header and signature, then a genuine attestation at 4: step 4 decides before steps 5 to 7.",
                R(k0, o), log => { log.Add(a1 with { Prev = 1, Form = new OaForm { Auth = Stranger } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-broken-link"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("order-typ-mismatch-before-key-unpublished", "A closed statement with the right prev and a stranger's header (typ curia-head+jws, the stranger's kid) and signature, then a genuine attestation at 4: step 5 decides before steps 6 and 7.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { Auth = Stranger } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("order-key-unpublished-before-signature", "An attestation at 2 whose header is right (typ, alg, kid O) and whose signature is a stranger's, O published at 3, then a genuine attestation at 4: step 6 (O is not yet published) decides before step 7.",
                E(k0), log => { log.Add(a1 with { Signer = stranger, HeaderKid = o.Kid }, o); log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); log.Add(a1, o); },
                [OaRow.Skipped(2, OaAttested, "act-key-unpublished"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("order-key-not-first-before-signature", "A second log.operator-key (O2) at 3 and an attestation at 4 whose header is right with kid O2 and whose signature is a stranger's, then a genuine attestation at 5 signed by O: step 6 (O2 is not the first) decides before step 7.",
                R(k0, o), log => { log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o2); log.Add(a1 with { Signer = stranger, HeaderKid = o2.Kid }, o); log.Add(a1, o); },
                [OaRow.Skipped(4, OaAttested, "act-key-not-first"), OaRow.Honoured(5, OaAttested)], Alice(true)),
            OaM("moderation-order-mismatch-before-broken-link", "A withhold whose statement and row say moderator automated, with prev 0 and a stranger's header and signature, then a genuine withhold at 5: step 3 decides before steps 4 to 7, for moderation.",
                Q(k0, o), log => { log.Add(w with { Moderator = "automated", Prev = 0, Form = new OaForm { Auth = Stranger } }, o); log.Add(w, o); },
                [OaRow.Skipped(4, OaModerated, "act-statement-mismatch"), OaRow.Honoured(5, OaModerated)], false),
            OaM("moderation-order-broken-link-before-typ-mismatch", "A human withhold whose prev is 0 (the post's root is at 3) with a stranger's header and signature, then a genuine withhold at 5: step 4 decides before steps 5 to 7, for moderation.",
                Q(k0, o), log => { log.Add(w with { Prev = 0, Form = new OaForm { Auth = Stranger } }, o); log.Add(w, o); },
                [OaRow.Skipped(4, OaModerated, "act-broken-link"), OaRow.Honoured(5, OaModerated)], false),

            // 48 to 59: one arm of step 5, 6 or 3 at a time. A step 5 arm is signed validly by O so that only step 5 can stop the row; a step 3 typing arm omits the member from the row so that "the row differs from the statement" cannot hide it.
            OaA("attestation-header-typ-wrong", "A genuine statement whose authorization header typ is curia-head+jws, signed validly by O over that header, then a genuine attestation at 4: step 5's typ arm alone.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { Typ = "curia-head+jws" } } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-header-alg-wrong", "A genuine statement whose authorization header alg is ES384 and whose signature is ES256 by O over that header (it verifies under ES256), then a genuine attestation at 4: step 5's alg arm alone.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { Alg = "ES384" } } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-header-alg-none", "A genuine statement whose authorization header alg is none and whose signature part is empty, then a genuine attestation at 4: alg none is not ES256.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { Alg = "none" } } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-authorization-attached", "A genuine statement whose authorization is a compact JWS carrying the statement's canonical bytes as its payload (header.payload.signature, the signature O's), then a genuine attestation at 4: not a detached JWS.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { Attached = true } } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-authorization-absent", "A genuine statement and no authorization member, then a genuine attestation at 4: a row with a statement and no detached JWS is act-typ-mismatch, not act-unsigned.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { NoAuthorization = true } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-header-kid-absent", "A genuine statement whose authorization header carries typ and alg and no kid, signed validly by O, then a genuine attestation at 4: a header with no kid names no operator key.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { OmitKid = true } } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-key-unpublished"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-statement-not-canonical", "A genuine attestation whose statement text holds the same members and values with typ first (not the order of RFC 8785), signed by O over the statement's canonical form, then a genuine attestation at 4: the statement is not its own canonical form.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { TextTypFirst = true } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-statement-not-nfc", "A genuine attestation whose reason is e followed by U+0301 (decomposed) in a statement that is otherwise RFC 8785 canonical, signed by O over the NFC canonical form, then a genuine attestation at 4: the statement is not NFC.",
                R(k0, o), log => { log.Add(a1 with { Reason = "e" + (char)0x0301, Form = new OaForm { TextNotNfc = true } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-prev-is-a-string", "A genuine attestation whose statement states prev as the string \"0\" and whose row omits prev, then a genuine attestation at 4: the closed form types prev as a non-negative integer.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { StatementMembers = Members(("prev", Str("0"))), OmitRow = ["prev"] } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-prev-is-negative", "A genuine attestation whose statement states prev as -1 and whose row omits prev, then a genuine attestation at 4: prev is a non-negative integer (so the row is not act-broken-link).",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { StatementMembers = Members(("prev", Num(-1))), OmitRow = ["prev"] } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-owner-verified-is-a-string", "A genuine attestation whose statement states owner_verified as the string \"true\" and whose row omits it, then a genuine attestation at 4: the closed form types owner_verified as a boolean.",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { StatementMembers = Members(("owner_verified", Str("true"))), OmitRow = ["owner_verified"] } }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaM("moderation-adjudicates-not-texts", "A human withhold whose statement states adjudicates as [1] and whose row omits it, then a genuine withhold at 5: the closed form types adjudicates as an array of strings.",
                Q(k0, o), log => { log.Add(w with { Form = new OaForm { StatementMembers = Members(("adjudicates", Arr(Num(1)))), OmitRow = ["adjudicates"] } }, o); log.Add(w, o); },
                [OaRow.Skipped(4, OaModerated, "act-statement-mismatch"), OaRow.Honoured(5, OaModerated)], false),

            // 60
            OaA("second-owner-unverified-changes-nothing", "A genuine attestation naming Alice, verified, at 3 and one naming Bob with owner_verified false at 4 (prev 3): both are honoured as acts, the second binds nothing and sets nothing, so Alice stays verified (R4.1, R6.67).",
                R(k0, o), log => { log.Add(a1, o); log.Add(a1 with { Owner = OaBob, Verified = false, Prev = 3 }, o); },
                [OaRow.Honoured(3, OaAttested), OaRow.Honoured(4, OaAttested)], Alice(true)),

            // 61 to 72 (Task 5b): the places the 60 did not pin and two folds parted (Amendment 5, rulings 65 to 70).
            OaA("attestation-stream-begins-with-a-forged-row", "An attestation in X's stream with no statement is the first event of the stream, then X's enrolment and binding at 2 and 3, then a genuine attestation at 4 whose prev is 2 (the enrolment): the stream's root is its first event, so the stream has no root, and the row at 1 is act-unsigned and the row at 4 is act-broken-link.",
                new Log(), log => { log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o); log.Add(a1 with { NoStatement = true }, o); EnrollX(log, k0); log.Add(a1 with { Prev = 2 }, o); },
                [OaRow.Skipped(1, OaAttested, "act-unsigned"), OaRow.Skipped(4, OaAttested, "act-broken-link")], NoOwner()),
            OaA("operator-key-off-curve-holds-a-position", "A log.operator-key entry whose jwk is in R6.62's closed form and whose point is not on its curve holds a position (on the curve is asked of a key when it must verify), and an attestation whose header kid names it and whose signature is 64 arbitrary bytes is act-signature-invalid, not act-key-unpublished.",
                E(k0), log => { log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, offCurve); log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { Signer = offCurve, FixedSignature = Enumerable.Repeat((byte)0x5A, 64).ToArray() } } }, o); },
                [OaRow.Skipped(3, OaAttested, "act-signature-invalid")], NoOwner(), primitiveCalls: 0),
            OaA("operator-key-kid-listed-again-after-a-clash", "A log key P (kid LK) at 0; a log.operator-key at 1 that lists kid OK with the same public key P (it clashes with the log key and holds no position); a log.operator-key at 2 that lists kid OK again with another key Q (an earlier entry of the class that reads as a key lists OK, so it holds none either). An attestation at 5 signed by Q under kid OK is act-key-unpublished; a log.operator-key at 6 (kid O3, key R) holds the first position and the attestation at 7 signed by it is honoured.",
                new Log(), log =>
                {
                    log.PublishOaKey("log.key", "log:keys", Renamed(shared, "LK"));
                    log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, Renamed(shared, "OK"));
                    log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, Renamed(keyQ, "OK"));
                    EnrollX(log, k0);
                    log.Add(a1 with { Prev = 3, Form = new OaForm { Auth = new OaAuth { Signer = Renamed(keyQ, "OK") } } }, o);
                    log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, Renamed(keyR, "O3"));
                    log.Add(a1 with { Prev = 3, Form = new OaForm { Auth = new OaAuth { Signer = Renamed(keyR, "O3") } } }, o);
                }, [OaRow.Skipped(5, OaAttested, "act-key-unpublished"), OaRow.Honoured(7, OaAttested)], Alice(true)),
            OaA("log-key-repeating-a-kid-publishes-nothing", "A log key P (kid LK) at 0; a second log.key at 1 that lists kid LK again with another key Q (it holds no position and publishes no key); a log.operator-key at 2 (kid OK, key Q), which is therefore no log key and holds a position. An attestation at 5 signed by Q under kid OK is honoured.",
                new Log(), log =>
                {
                    log.PublishOaKey("log.key", "log:keys", Renamed(shared, "LK"));
                    log.PublishOaKey("log.key", "log:keys", Renamed(keyQ, "LK"));
                    log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, Renamed(keyQ, "OK"));
                    EnrollX(log, k0);
                    log.Add(a1 with { Prev = 3, Form = new OaForm { Auth = new OaAuth { Signer = Renamed(keyQ, "OK") } } }, o);
                }, [OaRow.Honoured(5, OaAttested)], Alice(true)),
            OaA("attestation-header-repeats-a-member", "A genuine statement whose authorization header is the text of a header that lists kid twice (a stranger's, then O's), signed validly by O over that text, then a genuine attestation at 4: the ADMIT profile refuses a repeated member, so the header is no object the procedure reads (a reader that takes the last kid honours the row, one that takes the first skips it as act-key-unpublished).",
                R(k0, o), log =>
                {
                    log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { HeaderText = $"{{\"alg\":\"ES256\",\"b64\":false,\"crit\":[\"b64\"],\"kid\":\"{stranger.Kid}\",\"kid\":\"{o.Kid}\",\"typ\":\"{OaHeaderTyp}\"}}" } } }, o);
                    log.Add(a1, o);
                }, [OaRow.Skipped(3, OaAttested, "act-typ-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-header-without-b64", "A genuine statement whose authorization header has typ, alg and kid and no b64, with a signature that verifies under O over RFC 7515's signing input (the base64url of the header, a full stop, the base64url of the statement), then a genuine attestation at 4: R6.37 shapes a detached signature with b64 false, so the row is act-signature-invalid.",
                R(k0, o), log =>
                {
                    log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { HeaderText = $"{{\"alg\":\"ES256\",\"kid\":\"{o.Kid}\",\"typ\":\"{OaHeaderTyp}\"}}", Input = OaInput.Encoded, AssertValidUnder = OaInput.Encoded } } }, o);
                    log.Add(a1, o);
                }, [OaRow.Skipped(3, OaAttested, "act-signature-invalid"), OaRow.Honoured(4, OaAttested)], Alice(true), primitiveCalls: 1),
            OaA("attestation-header-without-crit", "A genuine statement whose authorization header has typ, alg, kid and b64 false and no crit, with a signature that verifies under O over RFC 7797's signing input (the base64url of the header, a full stop, the statement), then a genuine attestation at 4: R6.37 shapes a detached signature with crit exactly [b64], so the row is act-signature-invalid.",
                R(k0, o), log =>
                {
                    log.Add(a1 with { Form = new OaForm { Auth = new OaAuth { HeaderText = $"{{\"alg\":\"ES256\",\"b64\":false,\"kid\":\"{o.Kid}\",\"typ\":\"{OaHeaderTyp}\"}}", Input = OaInput.Canonical, AssertValidUnder = OaInput.Canonical } } }, o);
                    log.Add(a1, o);
                }, [OaRow.Skipped(3, OaAttested, "act-signature-invalid"), OaRow.Honoured(4, OaAttested)], Alice(true), primitiveCalls: 1),
            OaA("attestation-row-differs-from-the-statement-by-normalization", "A genuine attestation whose statement states reason as e-acute (one code point, NFC) and whose row states it as e followed by U+0301, then a genuine attestation at 4: a row member that is not the same JSON value as the statement's differs from it, strings compared code point for code point, never normalized.",
                R(k0, o), log => { log.Add(a1 with { Reason = ((char)0xE9).ToString(), RowReason = "e" + (char)0x0301 }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaA("attestation-method-outside-the-four", "A genuine, validly signed attestation whose statement and row state method carrier-pigeon, then a genuine attestation at 4: the closed form closes method to R4.24's four proofs.",
                R(k0, o), log => { log.Add(a1 with { Method = "carrier-pigeon" }, o); log.Add(a1, o); },
                [OaRow.Skipped(3, OaAttested, "act-statement-mismatch"), OaRow.Honoured(4, OaAttested)], Alice(true)),
            OaM("moderation-effect-outside-the-four", "A human, validly signed record whose statement and row state effect obliterate, then a genuine withhold at 5: the closed form closes effect to quarantine, withhold, restore and dismiss.",
                Q(k0, o), log => { log.Add(new OaModerate("obliterate", 3), o); log.Add(w, o); },
                [OaRow.Skipped(4, OaModerated, "act-statement-mismatch"), OaRow.Honoured(5, OaModerated)], false),
            OaM("moderation-category-outside-the-seven", "A human, validly signed withhold whose statement and row state category vibes, then a genuine withhold at 5: the closed form closes category to R10.35's seven flag types.",
                Q(k0, o), log => { log.Add(w with { Category = "vibes" }, o); log.Add(w, o); },
                [OaRow.Skipped(4, OaModerated, "act-statement-mismatch"), OaRow.Honoured(5, OaModerated)], false),
            OaM("moderation-restore-in-another-category-leaves-the-hold", "A withhold in spam at 4, a quarantine in incorrect at 5 and a restore in spam at 6, each honoured: R10.61 holds a post in the category each record cites until a restore citing it, so incorrect still holds it and the post is not served (the last record is a restore).",
                Q(k0, o), log => { log.Add(new OaModerate("withhold", 3), o); log.Add(new OaModerate("quarantine", 4) with { Category = "incorrect" }, o); log.Add(new OaModerate("restore", 5), o); },
                [OaRow.Honoured(4, OaModerated), OaRow.Honoured(5, OaModerated), OaRow.Honoured(6, OaModerated)], false),

            // 73 and 74 (Task 3b, Amendment 6, ruling 81): an honoured act is read from its statement, and a row need not state any member its statement states (step 3).
            OaA("attestation-row-omits-members-the-statement-states", "The agent's root, then two genuine attestations naming Alice (the first owner_verified true, the second false, prev 3), each row stating only statement and authorization: both are honoured, and the state is the statement's (owner Alice, verified false), because the row is a copy and the statement is the act (R6.67).",
                R(k0, o), log => { log.Add(a1 with { Form = new OaForm { OmitRow = AttestationRowMembers } }, o); log.Add(a1 with { Verified = false, Prev = 3, Form = new OaForm { OmitRow = AttestationRowMembers } }, o); },
                [OaRow.Honoured(3, OaAttested), OaRow.Honoured(4, OaAttested)], Alice(false)),
            OaM("moderation-row-omits-members-the-statement-states", "The post's root, then one genuine human withhold (spam), its row stating only statement and authorization: the row is honoured and the post is not served, because the state is the statement's.",
                Q(k0, o), log => log.Add(w with { Form = new OaForm { OmitRow = ModerationRowMembers } }, o),
                [OaRow.Honoured(4, OaModerated)], false),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (!seen.Add(c.Name)) throw new InvalidOperationException($"operator-acts case {c.Name} is built twice");
        }

        foreach (var c in cases) WriteOa(root, c);
        Console.WriteLine($"operator-acts cases written under conformance/operator-acts/ ({cases.Count} written)");
        return 0;

        static Log R(Key k0, P256LogKey o)
        {
            var log = E(k0);
            log.PublishOaKey(OaOperatorKeyType, OaOperatorKeysAggregate, o);
            return log;
        }

        static Log E(Key k0) => EnrolledAndBound(k0);

        static void EnrollX(Log log, Key k0)
        {
            log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k0.Kid))));
            AddBinding(log, k0);
        }

        static Log Q(Key k0, P256LogKey o)
        {
            var log = R(k0, o);
            log.Add("post.accepted", OaPost, "agent://curia.example/tuesdaycrowd/scriptor", Obj(("digest", Str(OaDigest)), ("post_id", Str(OaPost))));
            return log;
        }

        static JsonObject Alice(bool verified) => new() { ["owner"] = OaAlice, ["verified"] = verified };

        static IReadOnlyDictionary<string, JsonValue> Members(params (string Name, JsonValue Value)[] members) =>
            members.ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal);

        static JsonObject NoOwner() => new() { ["owner"] = null, ["verified"] = false };
    }

    /// <summary>An attestation case: <paramref name="build"/> adds the rows after the base, each signed under the key given, and the rows are the verdicts of the construction.</summary>
    private static OaCase OaA(string name, string note, Log log, Action<Log> build, IReadOnlyList<OaRow> rows, JsonObject state, int? primitiveCalls = null)
    {
        build(log);
        return new OaCase(name, note, Agent, "agent", log, rows, state, primitiveCalls);
    }

    private static OaCase OaM(string name, string note, Log log, Action<Log> build, IReadOnlyList<OaRow> rows, bool served)
    {
        build(log);
        return new OaCase(name, note, OaPost, "post", log, rows, new JsonObject { ["served"] = served });
    }

    // ---- entries --------------------------------------------------------------------------------

    private static int Add(this Log log, OaAttest a, P256LogKey o) => log.Add(OaAttested, a.Aggregate, a.EventActor, AttestationPayload(a, o));

    private static int Add(this Log log, OaModerate m, P256LogKey o) => log.Add(OaModerated, m.Aggregate, OaActor, ModerationPayload(m, o));

    private static int PublishOaKey(this Log log, string type, string aggregate, P256LogKey key) => log.Add(type, aggregate, "operator", Obj(
        ("jwk", key.Jwk), ("kid", Str(key.Kid)), ("valid_from", Str("2026-09-01T00:00:00Z"))));

    private static JsonValue AttestationStatement(OaAttest a) => a.StatementOverride ?? WithMembers(a.Form, [
        ("actor_id", Str(a.StatementActor)),
        ("agent_id", Str(a.Agent)),
        ("method", Str(a.Method)),
        ("owner_id", Str(a.Owner)),
        ("owner_verified", Bool(a.Verified)),
        ("prev", Num(a.Prev)),
        ("reason", Str(a.Reason)),
        ("typ", Str(a.StatementTyp))]);

    /// <summary>The statement's members with the form's replacements (a member present is replaced, an absent one is added).</summary>
    private static JsonValue WithMembers(OaForm form, List<(string, JsonValue)> members)
    {
        if (form.StatementMembers is { } overrides)
        {
            foreach (var (name, value) in overrides)
            {
                var at = members.FindIndex(m => m.Item1 == name);
                if (at >= 0) members[at] = (name, value);
                else members.Add((name, value));
            }
        }

        return Obj([.. members]);
    }

    private static JsonValue AttestationPayload(OaAttest a, P256LogKey operatorKey)
    {
        var row = new List<(string, JsonValue)>
        {
            ("agent_id", Str(a.Agent)),
            ("method", Str(a.Method)),
            ("owner_id", Str(a.RowOwner ?? a.Owner)),
            ("owner_verified", Bool(a.Verified)),
            ("prev", Num(a.Prev)),
            ("reason", Str(a.RowReason ?? a.Reason)),
        };
        if (a.ExtraRowMember is { } extra) row.Add((extra, Str("not a member of the statement")));
        if (a.StatementOverride is not null) row.Clear();
        return Signed(row, AttestationStatement(a), a.Signer ?? operatorKey, a.HeaderKid, a.Garbage, a.NoStatement, operatorKey, a.Form);
    }

    private static JsonValue ModerationStatement(OaModerate m) => WithMembers(m.Form, [
        ("actor_id", Str(OaActor)),
        ("adjudicates", Arr()),
        ("category", Str(m.Category)),
        ("digest", Str(m.StatementDigest)),
        ("effect", Str(m.Effect)),
        ("moderator", Str(m.Moderator)),
        ("post_id", Str(m.Post)),
        ("prev", Num(m.Prev)),
        ("rationale", Str("moderated by conformance")),
        ("typ", Str(m.StatementTyp))]);

    private static JsonValue ModerationPayload(OaModerate m, P256LogKey operatorKey)
    {
        var row = new List<(string, JsonValue)>
        {
            ("actor_id", Str(OaActor)),
            ("adjudicates", Arr()),
            ("category", Str(m.Category)),
            ("digest", Str(m.RowDigest ?? m.StatementDigest)),
            ("effect", Str(m.Effect)),
            ("moderator", Str(m.Moderator)),
            ("post_id", Str(m.Post)),
            ("prev", Num(m.Prev)),
            ("rationale", Str("moderated by conformance")),
        };
        return Signed(row, ModerationStatement(m), m.Signer ?? operatorKey, m.HeaderKid, null, m.NoStatement, operatorKey, m.Form);
    }

    /// <summary>The row's members and, unless the row is unsigned, its statement and its authorization. A kept signature is re-verified; a fresh one is checked against the key it claims, or, for a bad signature, shown not to verify under it.</summary>
    private static JsonValue Signed(List<(string, JsonValue)> row, JsonValue statement, P256LogKey signer, string? headerKid, string? garbage, bool noStatement, P256LogKey operatorKey, OaForm form)
    {
        if (form.OmitRow is { } omitted) row.RemoveAll(m => omitted.Contains(m.Item1));

        if (noStatement)
        {
            if (garbage is not null) row.Add(("authorization", Str(garbage)));
            return Obj([.. row]);
        }

        // The signature is always over the NFC canonical form of the intended statement; the statement's text is that form unless the form changes it.
        var canonical = Canon(statement);
        var text = Encoding.UTF8.GetString(canonical.Span);
        if (form.TextNotNfc) text = Encoding.UTF8.GetString(CanonicalJson.Canonicalize(statement).Match(b => b, e => throw new InvalidOperationException($"canonicalize failed: {e.Type}")).Span);
        if (form.TextTypFirst) text = TypFirst(text);
        row.Add(("statement", Str(text)));
        if (form.NoAuthorization) return Obj([.. row]);

        if (form.Auth is { } auth)
        {
            row.Add(("authorization", Str(OaCustomAuthorization(canonical, text, auth, operatorKey))));
            return Obj([.. row]);
        }

        var header = headerKid ?? signer.Kid;
        row.Add(("authorization", Str(garbage ?? OaPinnedAuthorization(canonical, text, signer, header, operatorKey))));
        return Obj([.. row]);
    }

    /// <summary>The canonical text of a statement with its typ member moved first (typ sorts last in the statements here), so that it holds the same members and values in another order.</summary>
    private static string TypFirst(string canonicalText)
    {
        const string Tail = ",\"typ\":";
        var at = canonicalText.LastIndexOf(Tail, StringComparison.Ordinal);
        if (at < 0 || !canonicalText.StartsWith('{') || !canonicalText.EndsWith('}')) throw new InvalidOperationException("the statement's typ is not its last member");
        var typ = canonicalText[(at + 1)..^1];
        return "{" + typ + "," + canonicalText[1..at] + "}";
    }

    /// <summary>
    /// An authorization whose protected header the case chooses (a typ, an alg, no kid) and whose signature is the signer's (the operator key unless the header says a stranger). The
    /// header is composed as <c>DetachedJws.Sign</c> composes it, member for member and in the same order; the signing input is the encoded header, a full stop and the canonical
    /// bytes; the signature is ES256, IEEE P1363. Alg <c>none</c> carries an empty signature part. An attached form carries the base64url of the canonical bytes as its payload and the
    /// same signature. A kept signature (by statement text and header) is re-verified, so a regeneration over the existing output is byte-identical.
    /// </summary>
    private static string OaCustomAuthorization(CanonicalBytes canonical, string text, OaAuth auth, P256LogKey operatorKey)
    {
        var signer = auth.Signer ?? operatorKey;
        var alg = auth.Alg ?? "ES256";
        string encodedHeader;
        if (auth.HeaderText is { } headerText) encodedHeader = Jwk.Base64UrlEncode(Encoding.UTF8.GetBytes(headerText));
        else
        {
            var header = new Dictionary<string, object> { ["alg"] = alg };
            if (!auth.OmitKid) header["kid"] = signer.Kid;
            header["typ"] = auth.Typ ?? OaHeaderTyp;
            header["b64"] = false;
            header["crit"] = new[] { "b64" };
            encodedHeader = Jwk.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        }

        var payload = auth.Attached ? Jwk.Base64UrlEncode(canonical.Span.ToArray()) : "";

        string signature;
        if (auth.FixedSignature is { } fixedSignature) signature = Jwk.Base64UrlEncode(fixedSignature);
        else if (alg is "none") signature = "";
        else
        {
            var input = OaSigningInput(encodedHeader, canonical, auth.Input);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(signer.Spki, out _);
            signature = "";
            if (_oaPins.TryGetValue(text, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    var parts = candidate.Split('.');
                    if (parts.Length != 3 || parts[0] != encodedHeader || parts[2].Length == 0) continue;
                    if (ecdsa.VerifyData(input, Jwk.Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) { signature = parts[2]; break; }
                }
            }

            if (signature.Length == 0)
            {
                using var privateKey = ECDsa.Create();
                privateKey.ImportECPrivateKey(signer.EcPrivateKeyDer, out _);
                var fresh = privateKey.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (!ecdsa.VerifyData(input, fresh, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new InvalidOperationException("a signature does not verify under its own signer");
                signature = Jwk.Base64UrlEncode(fresh);
            }

            // The vector is valid under the other reading of its signing input: the signature verifies under the signer over the input of the form the case names.
            if (auth.AssertValidUnder is { } reading)
            {
                var other = OaSigningInput(encodedHeader, canonical, reading);
                if (!ecdsa.VerifyData(other, Jwk.Base64UrlDecode(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    throw new InvalidOperationException($"the signature over {text} does not verify under its signer over the {reading} signing input the vector claims");
            }
        }

        return $"{encodedHeader}.{payload}.{signature}";
    }

    /// <summary>The signing input of a detached authorization: the encoded header, a full stop, then the canonical bytes (R6.37, RFC 7797) or their base64url (RFC 7515).</summary>
    private static byte[] OaSigningInput(string encodedHeader, CanonicalBytes canonical, OaInput form) =>
        form is OaInput.Encoded
            ? Encoding.ASCII.GetBytes(encodedHeader + "." + Jwk.Base64UrlEncode(canonical.Span.ToArray()))
            : [.. Encoding.ASCII.GetBytes(encodedHeader + "."), .. canonical.Span.ToArray()];

    /// <summary>Once per run: an authorization built by <see cref="OaCustomAuthorization"/> with the default header verifies under the production verifier, so the composition above is the production one.</summary>
    private static void OaSelfCheck(P256LogKey operatorKey)
    {
        var canonical = Canon(Obj(("typ", Str("self-check"))));
        var compact = OaCustomAuthorization(canonical, "self-check", new OaAuth(), operatorKey);
        var production = Signing.Sign(canonical, "ES256", operatorKey.Kid, operatorKey.EcPrivateKeyDer, OaHeaderTyp).Compact;
        if (compact.Split('.')[0] != production.Split('.')[0]) throw new InvalidOperationException("the generator's protected header is not the production header");
        if (!Signing.Verify(canonical, new JwsSignature(compact), "ES256", operatorKey.Kid, operatorKey.Spki, OaHeaderTyp).IsOk) throw new InvalidOperationException("the generator's signature does not verify under the production verifier");
    }

    private static string OaPinnedAuthorization(CanonicalBytes canonical, string text, P256LogKey signer, string headerKid, P256LogKey operatorKey)
    {
        // The key the header names is the one the signature must verify under; a signer that is another key makes a signature that must not.
        var named = headerKid == signer.Kid ? signer : operatorKey;
        var expectValid = headerKid == signer.Kid;
        if (_oaPins.TryGetValue(text, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                var header = Signing.ReadHeader(new JwsSignature(candidate));
                if (!header.TryGetValue(out var h, out _) || h.Kid != headerKid) continue;
                var ok = Signing.Verify(canonical, new JwsSignature(candidate), "ES256", headerKid, named.Spki, OaHeaderTyp).IsOk;
                if (ok == expectValid && (expectValid || SignedBy(canonical, candidate, signer, headerKid))) return candidate;
            }
        }

        var fresh = Signing.Sign(canonical, "ES256", headerKid, signer.EcPrivateKeyDer, OaHeaderTyp).Compact;
        var verified = Signing.Verify(canonical, new JwsSignature(fresh), "ES256", headerKid, named.Spki, OaHeaderTyp).IsOk;
        if (verified != expectValid)
            throw new InvalidOperationException($"the signature over {text} under header kid {headerKid} {(expectValid ? "does not verify" : "verifies")} under the key it names");
        return fresh;
    }

    /// <summary>A bad signature is a stranger's: it verifies under the stranger's key when the header's kid is read as that key's.</summary>
    private static bool SignedBy(CanonicalBytes canonical, string compact, P256LogKey signer, string headerKid) =>
        Signing.Verify(canonical, new JwsSignature(compact), "ES256", headerKid, signer.Spki, OaHeaderTyp).IsOk;

    /// <summary>Every authorization in the existing output, by the statement it signs, so that a regeneration keeps the bytes of a randomized signature.</summary>
    private static Dictionary<string, List<string>> LoadOaPins(string root)
    {
        var pins = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Where(f => Path.GetFileName(Path.GetDirectoryName(f)) == "entries").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            if (!document.RootElement.TryGetProperty("entry", out var entry) || !entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
            if (!payload.TryGetProperty("statement", out var statement) || statement.ValueKind != JsonValueKind.String
                || !payload.TryGetProperty("authorization", out var authorization) || authorization.ValueKind != JsonValueKind.String) continue;
            if (!pins.TryGetValue(statement.GetString()!, out var list)) pins[statement.GetString()!] = list = [];
            list.Add(authorization.GetString()!);
        }

        return pins;
    }

    private static P256LogKey OaKey(string label)
    {
        var scalar = SHA256.HashData(Encoding.UTF8.GetBytes("curia-conformance-operator-acts-" + label));
        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar });
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        var x = Jwk.Base64UrlEncode(p.Q.X!);
        var y = Jwk.Base64UrlEncode(p.Q.Y!);
        var kid = Jwk.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));
        var jwk = Obj(("alg", Str("ES256")), ("crv", Str("P-256")), ("kid", Str(kid)), ("kty", Str("EC")), ("use", Str("sig")), ("x", Str(x)), ("y", Str(y)));
        return new P256LogKey(kid, p.Q.X!, p.Q.Y!, ecdsa.ExportECPrivateKey(), ecdsa.ExportSubjectPublicKeyInfo(), jwk);
    }

    /// <summary>
    /// A key in R6.62's closed form whose point is not on its curve: the operator key's x and an y whose last character is changed within the set a 32-byte coordinate's last
    /// character may take, until <c>ECDsa.ImportParameters</c> refuses the point. It carries no private key, so nothing can be signed under it.
    /// </summary>
    private static P256LogKey OaOffCurveKey(P256LogKey from)
    {
        var x = Jwk.Base64UrlEncode(from.X32);
        var y = Jwk.Base64UrlEncode(from.Y32);
        foreach (var last in "AEIMQUYcgkosw048")
        {
            var yy = y[..^1] + last;
            if (yy == y) continue;
            var yBytes = Jwk.Base64UrlDecode(yy);
            try
            {
                using var probe = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = from.X32, Y = yBytes } });
            }
            catch (CryptographicException)
            {
                var kid = Jwk.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{yy}}"}""")));
                var jwk = Obj(("alg", Str("ES256")), ("crv", Str("P-256")), ("kid", Str(kid)), ("kty", Str("EC")), ("use", Str("sig")), ("x", Str(x)), ("y", Str(yy)));
                return new P256LogKey(kid, from.X32, yBytes, [], [], jwk);
            }
        }

        throw new InvalidOperationException("no y whose last character is in R6.62's set puts the operator key's x off its curve");
    }

    // ---- output ---------------------------------------------------------------------------------

    private static void WriteOa(string root, OaCase c)
    {
        var dir = Path.Combine(root, c.Name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(Path.Combine(dir, "entries"));

        for (var index = 0; index < c.Log.Entries.Count; index++)
        {
            var canonical = CanonicalJson.Canonicalize(c.Log.Entries[index])
                .Match(b => b, err => throw new InvalidOperationException($"{c.Name}: canonicalize failed: {err.Type}"));
            var leaf = MerkleTree.LeafHash(canonical.Span);
            File.WriteAllText(
                Path.Combine(dir, "entries", $"{index}.json"),
                $"{{\"log_index\":{index},\"leaf_hash\":\"{Prefixed(leaf)}\",\"entry\":{Encoding.UTF8.GetString(canonical.Span)}}}\n");
        }

        var rows = new JsonArray();
        foreach (var row in c.Rows)
        {
            var node = new JsonObject { ["log_index"] = row.Index, ["type"] = row.Type, ["verdict"] = row.Predicate is null ? "honoured" : "skipped" };
            if (row.Predicate is not null) node["predicate"] = row.Predicate;
            rows.Add(node);
        }

        var expected = new JsonObject { ["rows"] = rows, ["state"] = c.State.DeepClone() };
        File.WriteAllText(Path.Combine(dir, "expected.json"), expected.ToJsonString() + "\n");

        var meta = new JsonObject
        {
            ["profile"] = "operator-acts-log",
            ["requirement"] = "R6.67",
            ["stream"] = c.StreamKind,
            ["subject"] = c.Subject,
        };
        if (c.PrimitiveCalls is { } primitiveCalls) meta["primitive_calls"] = primitiveCalls;
        meta["note"] = c.Note + " Statements are the exact output of CanonicalizeWithNfc, each signature is detached under curia-operator-act+jws with the key's thumbprint as the header kid; the keys the case publishes are its own log.operator-key, log.key and log.declaration-key entries. expected.json is written from this construction and never by running a verifier. A proposal: it may be regenerated until the owner's lift record pins its digests.";
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString(Jwk.PrettyOptions) + "\n");
        Console.WriteLine($"  wrote operator-acts/{c.Name}");
    }
}
