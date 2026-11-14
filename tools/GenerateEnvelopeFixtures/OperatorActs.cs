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
    private sealed record OaCase(string Name, string Note, string Subject, string StreamKind, Log Log, IReadOnlyList<OaRow> Rows, JsonObject State);

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
    }

    /// <summary>A moderation row to build. The defaults are a genuine human withhold of post P by <c>operator:ops</c> signed by the operator key.</summary>
    private sealed record OaModerate(string Effect, long Prev)
    {
        public string Post { get; init; } = OaPost;
        public string Aggregate { get; init; } = OaPost;
        public string Moderator { get; init; } = "human";
        public string StatementTyp { get; init; } = OaModerationTyp;
        public string StatementDigest { get; init; } = OaDigest;
        public string? RowDigest { get; init; }
        public P256LogKey? Signer { get; init; }
        public string? HeaderKid { get; init; }
        public bool NoStatement { get; init; }
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

        static Log Q(Key k0, P256LogKey o)
        {
            var log = R(k0, o);
            log.Add("post.accepted", OaPost, "agent://curia.example/tuesdaycrowd/scriptor", Obj(("digest", Str(OaDigest)), ("post_id", Str(OaPost))));
            return log;
        }

        static JsonObject Alice(bool verified) => new() { ["owner"] = OaAlice, ["verified"] = verified };

        static JsonObject NoOwner() => new() { ["owner"] = null, ["verified"] = false };
    }

    /// <summary>An attestation case: <paramref name="build"/> adds the rows after the base, each signed under the key given, and the rows are the verdicts of the construction.</summary>
    private static OaCase OaA(string name, string note, Log log, Action<Log> build, IReadOnlyList<OaRow> rows, JsonObject state)
    {
        build(log);
        return new OaCase(name, note, Agent, "agent", log, rows, state);
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

    private static JsonValue AttestationStatement(OaAttest a) => a.StatementOverride ?? Obj(
        ("actor_id", Str(a.StatementActor)),
        ("agent_id", Str(a.Agent)),
        ("method", Str("manual")),
        ("owner_id", Str(a.Owner)),
        ("owner_verified", Bool(a.Verified)),
        ("prev", Num(a.Prev)),
        ("reason", Str("owner verified by hand")),
        ("typ", Str(a.StatementTyp)));

    private static JsonValue AttestationPayload(OaAttest a, P256LogKey operatorKey)
    {
        var row = new List<(string, JsonValue)>
        {
            ("agent_id", Str(a.Agent)),
            ("method", Str("manual")),
            ("owner_id", Str(a.RowOwner ?? a.Owner)),
            ("owner_verified", Bool(a.Verified)),
            ("prev", Num(a.Prev)),
            ("reason", Str("owner verified by hand")),
        };
        if (a.ExtraRowMember is { } extra) row.Add((extra, Str("not a member of the statement")));
        if (a.StatementOverride is not null) row.Clear();
        return Signed(row, AttestationStatement(a), a.Signer ?? operatorKey, a.HeaderKid, a.Garbage, a.NoStatement, operatorKey);
    }

    private static JsonValue ModerationStatement(OaModerate m) => Obj(
        ("actor_id", Str(OaActor)),
        ("adjudicates", Arr()),
        ("category", Str("spam")),
        ("digest", Str(m.StatementDigest)),
        ("effect", Str(m.Effect)),
        ("moderator", Str(m.Moderator)),
        ("post_id", Str(m.Post)),
        ("prev", Num(m.Prev)),
        ("rationale", Str("moderated by conformance")),
        ("typ", Str(m.StatementTyp)));

    private static JsonValue ModerationPayload(OaModerate m, P256LogKey operatorKey)
    {
        var row = new List<(string, JsonValue)>
        {
            ("actor_id", Str(OaActor)),
            ("adjudicates", Arr()),
            ("category", Str("spam")),
            ("digest", Str(m.RowDigest ?? m.StatementDigest)),
            ("effect", Str(m.Effect)),
            ("moderator", Str(m.Moderator)),
            ("post_id", Str(m.Post)),
            ("prev", Num(m.Prev)),
            ("rationale", Str("moderated by conformance")),
        };
        return Signed(row, ModerationStatement(m), m.Signer ?? operatorKey, m.HeaderKid, null, m.NoStatement, operatorKey);
    }

    /// <summary>The row's members and, unless the row is unsigned, its statement and its authorization. A kept signature is re-verified; a fresh one is checked against the key it claims, or, for a bad signature, shown not to verify under it.</summary>
    private static JsonValue Signed(List<(string, JsonValue)> row, JsonValue statement, P256LogKey signer, string? headerKid, string? garbage, bool noStatement, P256LogKey operatorKey)
    {
        if (noStatement) return Obj([.. row]);

        var canonical = Canon(statement);
        var text = Encoding.UTF8.GetString(canonical.Span);
        row.Add(("statement", Str(text)));
        var header = headerKid ?? signer.Kid;
        row.Add(("authorization", Str(garbage ?? OaPinnedAuthorization(canonical, text, signer, header, operatorKey))));
        return Obj([.. row]);
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
            ["note"] = c.Note + " Statements are the exact output of CanonicalizeWithNfc, each signature is detached under curia-operator-act+jws with the key's thumbprint as the header kid; the keys the case publishes are its own log.operator-key, log.key and log.declaration-key entries. expected.json is written from this construction and never by running a verifier. A proposal: it may be regenerated until the owner's lift record pins its digests.",
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString(Jwk.PrettyOptions) + "\n");
        Console.WriteLine($"  wrote operator-acts/{c.Name}");
    }
}
