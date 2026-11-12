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
/// conformance/honoured/ (errata G68, R6.64): small whole logs, each with the verdict the Forum's procedure must reach for every lifecycle row
/// of an identity's stream, the status of every key an honoured row names, and the kids the honoured prefix binds. Run by <c>--honoured-cases</c>.
///
/// <para><b>Each case's <c>expected.json</c> is written from the case's construction, below, and never by running a verifier.</b> A row is
/// <c>honoured</c> or <c>skipped</c> with the predicate R6.64's order gives it, and each case holds one defect, so the predicate cannot come from
/// another cause. A skipped row moves nothing (R6.64): every <c>prev</c> in a case names the last row the procedure honours before it, not the
/// last row in the log, so that a defect other than the one named cannot decide the row first.</para>
///
/// <para>A declaration is signed ES256 by a fixed P-256 key, which is randomized, so the first run pins a declaration's signature bytes and every
/// later run keeps them (<see cref="HonouredDeclaration"/>); every other signature is Ed25519 and deterministic. "Garbage" is the string
/// <c>FORGED-NO-VALID-SIGNATURE</c>, which is not a JWS; "bad signature" is a well-formed JWS of the right header whose signature bytes are another
/// key's.</para>
/// </summary>
internal static partial class LifecycleCases
{
    private const string Garbage = "FORGED-NO-VALID-SIGNATURE";
    private const string KeyBound = "agent.key-bound";
    private const string KeyRetired = "agent.key-retired";
    private const string KeyCompromised = "agent.key-compromised";
    private const string MalformedDeclarationKeyKid = "conformance-malformed-declaration-key";

    /// <summary>One row of an agent's stream and the verdict R6.64 gives it; a null predicate is honoured.</summary>
    private sealed record HRow(int Index, string Type, string? Predicate)
    {
        public static HRow Honoured(int index, string type) => new(index, type, null);

        public static HRow Skipped(int index, string type, string predicate) => new(index, type, predicate);
    }

    /// <summary>What the procedure must conclude for one agent: its rows, the status of every key an honoured row names, and the kids bound, in log order.</summary>
    private sealed record HAgent(string Agent, IReadOnlyList<HRow> Rows, IReadOnlyList<(string Kid, string Status)> Keys, IReadOnlyList<string> Bound);

    private sealed record HCase(string Name, string Note, Log Log, HAgent Main, IReadOnlyList<HAgent>? Also = null);

    private sealed record HonouredDeclaration(P256LogKey Signer, string[] Kids, long FromIndex, long Prev)
    {
        public string Reason { get; init; } = "owner-reported";
        public string StatementTyp { get; init; } = CompromiseTyp;

        /// <summary>Signs with this key and states the signer's kid in the header: a well-formed JWS of the right header whose signature is another key's.</summary>
        public P256LogKey? SignWith { get; init; }

        public bool IsGarbage { get; init; }
    }

    public static int RunHonoured(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "conformance", "honoured");
        Directory.CreateDirectory(root);

        using var firstDoc = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repoRoot, "conformance", "envelope", "ed25519-minimal", "private-keys.json")));
        var firstJwk = firstDoc.RootElement.GetProperty("keys")[0];
        var k0 = Ed("conformance-ed25519-minimal", Jwk.Base64UrlDecode(firstJwk.GetProperty("d").GetString()!));
        var k1 = Ed("conformance-ed25519-second", Enumerable.Repeat((byte)0x02, 32).ToArray());
        var k2 = Ed("conformance-ed25519-third", Enumerable.Repeat((byte)0x03, 32).ToArray());
        var kz = Ed("conformance-ed25519-stranger", Enumerable.Repeat((byte)0x09, 32).ToArray());
        var ky = Ed("conformance-ed25519-other-identity", Enumerable.Repeat((byte)0x0A, 32).ToArray());

        var cases = new List<HCase>
        {
            HEnrolmentPairOnly(k0),
            HSecondEnrolmentThenBinding(k0, k1),
            HBindingWithoutStatementAfterHistory(k0, k1, k2),
            HRetirementHonoured(k0, k1),
            HRetirementGarbageAuthorization(k0, k1),
            HRetirementBadSignature(k0, k1, kz),
            HRetirementSignedBySibling(k0, k1),
            HRetirementOfUnboundKid(k0, k1, kz),
            HRetirementWrongPrev(k0, k1),
            HRetirementReplayed(k0, k1),
            HRetirementUnknownStatementTyp(k0, k1),
            HRetirementExtraMember(k0, k1),
            HRetirementPayloadNotAnObject(k0, k1),
            HRetirementNamesAnotherAgent(k0, k1, ky),
            HRotationHonoured(k0, k1),
            HRotationGarbagePossession(k0, k1),
            HRotationPossessionByWrongKey(k0, k1, kz),
            HRotationSelfAuthorized(k0, k1),
            HRotationAuthorizedByRetiredKey(k0, k1, k2),
            HRotationKidAlreadyBound(k0, k1),
            HRotationJwkDiffersFromStatement(k0, k1, k2),
            HRotationWrongPrev(k0, k1),
            HRotationUnknownStatementTyp(k0, k1),
            HForgedRotationThenRotationAuthorizedByIt(k0, k1, k2),
            HSkippedRowDoesNotMoveTheChain(k0, k1),
            HForgedRowThenGenuineRetirement(root, k0, k1),
            HDeclarationHonoured(root, k0),
            HDeclarationSignedByLogKey(root, k0),
            HDeclarationKeyIsAlsoALogKey(root, k0),
            HDeclarationKeyNeverPublished(root, k0),
            HDeclarationKeyPublishedAfterTheDeclaration(root, k0),
            HDeclarationUnderTheSecondDeclarationKey(root, k0),
            HDeclarationBadSignature(root, k0),
            HDeclarationGarbageSignature(root, k0),
            HDeclarationKidsOmitCascade(root, k0, k1),
            HDeclarationKidNotBound(root, k0, kz),
            HDeclarationFromIndexBeforeBinding(root, k0),
            HDeclarationV1Statement(root, k0),
            HDeclarationUnknownReason(root, k0),
            HDeclarationCascadesThroughRotation(root, k0, k1),
            HMalformedDeclarationKeyEntryIsNoKey(root, k0),
            HRowInAnotherAggregateIsIgnored(k0, ky),
            HRetirementOfTheLastLiveKeyIsHonoured(k0),
            HMalformedRowBeforeGenuineDeclaration(root, k0, k1),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in cases)
        {
            if (!seen.Add(c.Name)) throw new InvalidOperationException($"honoured case {c.Name} is built twice");
            WriteHonoured(root, c);
        }

        Console.WriteLine($"honoured cases written under conformance/honoured/ ({cases.Count} written)");
        return 0;
    }

    // ---- cases (vector numbers are the plan's) -----------------------------------------------------

    private static HCase HEnrolmentPairOnly(Key k0)
    {
        var log = EnrolledAndBound(k0);
        return new HCase("enrolment-pair-only",
            "The enrolment's pair alone: the root, honoured as the Forum's own record, and no row beside it.",
            log, Agent_(rows: [], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HSecondEnrolmentThenBinding(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        AddBinding(log, k1);
        return new HCase("second-enrolment-then-binding",
            "A second agent.enrolled at 2 is not a row and binds nothing; the statement-less binding of the second key at 3 is not in the pair's position, so it is skipped.",
            log, Agent_(rows: [HRow.Skipped(3, KeyBound, "broken-link")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HBindingWithoutStatementAfterHistory(Key k0, Key k1, Key k2)
    {
        var log = RotatedOnce(k0, k1);
        AddBinding(log, k2);
        return new HCase("binding-without-statement-after-history",
            "The rotation at 2 is honoured; a statement-less binding of a third key at 3 is not the enrolment's pair, so it is skipped and binds nothing.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyBound, "broken-link")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementHonoured(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2));
        return new HCase("retirement-honoured",
            "A rotation to the second key, then the enrolled key retires itself, signed by itself, prev naming the rotation: both rows are honoured.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Honoured(3, KeyRetired)],
                keys: [(k0.Kid, "retired"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementGarbageAuthorization(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2) { AuthorizationText = Garbage });
        return new HCase("retirement-garbage-authorization",
            "A retirement of the enrolled key whose authorization is the string FORGED-NO-VALID-SIGNATURE, which is no JWS: it carries no retirement header typ.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "lifecycle-typ-mismatch")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementBadSignature(Key k0, Key k1, Key kz)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2) { Signer = kz, HeaderKid = k0.Kid });
        return new HCase("retirement-bad-signature",
            "A retirement of the enrolled key whose authorization has the right header (the retirement typ and the key's kid) and a stranger's signature bytes.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "rotation-unauthorized")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementSignedBySibling(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2) { Signer = k1, HeaderKid = k1.Kid });
        return new HCase("retirement-signed-by-sibling",
            "The enrolled key's retirement signed by its sibling, the second key, under the sibling's own header kid: a key can retire itself, not another.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "rotation-unauthorized")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementOfUnboundKid(Key k0, Key k1, Key kz)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(kz, kz, Prev: 2));
        return new HCase("retirement-of-unbound-kid",
            "A retirement that names a kid the log never bound (the stranger's), signed by a key the attacker holds under that kid: every signature holds and the key is not bound by the honoured prefix.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "rotation-unauthorized")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementWrongPrev(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 1));
        return new HCase("retirement-wrong-prev",
            "A genuine retirement of the enrolled key whose prev names the enrolment's binding at 1; the previous honoured entry is the rotation at 2.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "broken-link")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementReplayed(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        var retirement = new Ret(k0, k0, Prev: 2);
        AddRetirement(log, retirement);
        AddRetirement(log, retirement);
        return new HCase("retirement-replayed",
            "A genuine retirement at 3 and the same statement and signature again at 4: the replay's prev names 2, and the previous honoured entry is 3.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Honoured(3, KeyRetired), HRow.Skipped(4, KeyRetired, "broken-link")],
                keys: [(k0.Kid, "retired"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementUnknownStatementTyp(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2) { StatementTyp = "curia.key-retirement/9" });
        return new HCase("retirement-unknown-statement-typ",
            "A retirement whose statement typ is curia.key-retirement/9, a version the Forum does not know, signed over that statement.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "unknown-lifecycle-kind")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementExtraMember(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2) { ExtraEntryMember = true });
        return new HCase("retirement-extra-member",
            "A genuine retirement whose entry carries one member outside a retirement's closed set (note).",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "statement-mismatch")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementPayloadNotAnObject(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        log.Add(KeyRetired, Agent, Agent, Str("not an object"));
        return new HCase("retirement-payload-not-an-object",
            "An agent.key-retired row in the identity's stream whose payload is a JSON string.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "statement-mismatch")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRetirementNamesAnotherAgent(Key k0, Key k1, Key ky)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(ky, ky, Prev: 2) { AgentOverride = OtherAgent });
        AddPair(log, OtherAgent, ky);
        return new HCase("retirement-names-another-agent",
            "In X's stream at 3 a retirement whose agent_id is Y, the other identity (its pair follows at 4 and 5): the row is not X's act, and Y's status does not move.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyRetired, "statement-mismatch")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]),
            [new HAgent(OtherAgent, [], [(ky.Kid, "active")], [ky.Kid])]);
    }

    private static HCase HRotationHonoured(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        return new HCase("rotation-honoured",
            "The enrolled key authorizes a second key, which signs possession of its own binding, prev naming the enrolment's binding.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound)], keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRotationGarbagePossession(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1) { PossessionText = Garbage });
        return new HCase("rotation-garbage-possession",
            "A rotation whose authorization is genuine and whose possession is the string FORGED-NO-VALID-SIGNATURE: the new key never signed.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "lifecycle-typ-mismatch")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HRotationPossessionByWrongKey(Key k0, Key k1, Key kz)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1) { PossSigner = kz });
        return new HCase("rotation-possession-by-wrong-key",
            "A rotation whose possession has the new key's header kid and the right header typ and a stranger's signature bytes.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "rotation-unauthorized")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HRotationSelfAuthorized(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1) { AuthorizedByOverride = k1.Kid, AuthSigner = k1, AuthHeaderKid = k1.Kid });
        return new HCase("rotation-self-authorized",
            "A rotation to the second key whose authorized_by is the second key itself, signed by it: the authorizing key is not bound by the honoured prefix.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "rotation-unauthorized")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HRotationAuthorizedByRetiredKey(Key k0, Key k1, Key k2)
    {
        var log = RotatedOnce(k0, k1);
        AddRetirement(log, new Ret(k0, k0, Prev: 2));
        AddRotation(log, new Rot(k0, k2, Prev: 3));
        return new HCase("rotation-authorized-by-retired-key",
            "The enrolled key is retired at 3 (honoured); a rotation to a third key at 4, prev naming 3, is authorized by the retired key.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Honoured(3, KeyRetired), HRow.Skipped(4, KeyBound, "rotation-unauthorized")],
                keys: [(k0.Kid, "retired"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRotationKidAlreadyBound(Key k0, Key k1)
    {
        var log = RotatedOnce(k0, k1);
        AddRotation(log, new Rot(k0, k1, Prev: 2));
        return new HCase("rotation-kid-already-bound",
            "A second rotation naming the second key again, authorized by the enrolled key, prev naming 2: every check holds but that the kid is already bound.",
            log, Agent_(rows: [HRow.Honoured(2, KeyBound), HRow.Skipped(3, KeyBound, "statement-mismatch")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HRotationJwkDiffersFromStatement(Key k0, Key k1, Key k2)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1) { EntryJwkOverride = k2.Jwk });
        return new HCase("rotation-jwk-differs-from-statement",
            "A rotation whose entry states the third key's jwk beside a statement whose jwk is the second key's.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "statement-mismatch")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HRotationWrongPrev(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 0));
        return new HCase("rotation-wrong-prev",
            "A rotation whose prev names the enrolment at 0; the previous honoured lifecycle entry is the binding at 1.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "broken-link")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HRotationUnknownStatementTyp(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1) { StatementTyp = "curia.key-rotation/2" });
        return new HCase("rotation-unknown-statement-typ",
            "A rotation whose statement typ is curia.key-rotation/2, a version the Forum does not know, signed over that statement.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "unknown-lifecycle-kind")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    /// <summary>
    /// The plan's table says prev 2 for the second rotation. R6.64 decides a row's <c>prev</c> before its keys, against the last honoured entry, which is 1 here (the forged
    /// row at 2 is not honoured), so a prev of 2 is <c>broken-link</c> and the key is never reached. This case sets prev 1 so that the one defect is the one named: the
    /// authorizing key is bound by no honoured row.
    /// </summary>
    private static HCase HForgedRotationThenRotationAuthorizedByIt(Key k0, Key ka, Key kb)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, ka, Prev: 1) { AuthorizationText = Garbage, PossessionText = Garbage });
        AddRotation(log, new Rot(ka, kb, Prev: 1));
        return new HCase("forged-rotation-then-rotation-authorized-by-it",
            "A forged rotation of the enrolled key to a second key at 2 (garbage signatures), then a rotation from the second key to a third at 3 with valid signatures by both and prev 1, the last honoured entry: a skipped row binds no key, so the second key authorizes nothing.",
            log, Agent_(rows: [HRow.Skipped(2, KeyBound, "lifecycle-typ-mismatch"), HRow.Skipped(3, KeyBound, "rotation-unauthorized")],
                keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HSkippedRowDoesNotMoveTheChain(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddRetirement(log, new Ret(k0, k0, Prev: 1) { AuthorizationText = Garbage });
        AddRotation(log, new Rot(k0, k1, Prev: 1));
        return new HCase("skipped-row-does-not-move-the-chain",
            "A forged retirement of the enrolled key at 2 (garbage authorization), then a genuine rotation at 3 whose prev is 1: the skipped row is not the entry before it, and the enrolled key is not retired.",
            log, Agent_(rows: [HRow.Skipped(2, KeyRetired, "lifecycle-typ-mismatch"), HRow.Honoured(3, KeyBound)],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HForgedRowThenGenuineRetirement(string root, Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1) { IsGarbage = true }, root, "forged-row-then-genuine-retirement");
        AddRotation(log, new Rot(k0, k1, Prev: 1));
        AddRetirement(log, new Ret(k0, k0, Prev: 3));
        return new HCase("forged-row-then-genuine-retirement",
            "A forged declaration at 2 (garbage), a genuine rotation at 3 with prev 1 and a genuine retirement of the enrolled key at 4 with prev 3: the forged row moves nothing, and the retirement ends the key.",
            log, Agent_(rows: [HRow.Skipped(2, KeyCompromised, "lifecycle-typ-mismatch"), HRow.Honoured(3, KeyBound), HRow.Honoured(4, KeyRetired)],
                keys: [(k0.Kid, "retired"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HDeclarationHonoured(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1), root, "declaration-honoured");
        return new HCase("declaration-honoured",
            "The declaration key is published at 2 and a log key at 3; a declaration of the enrolled key from index 1, prev 1, signed by the declaration key is the first key published, before the declaration, and the cascade is the key alone.",
            log, Agent_(rows: [HRow.Honoured(4, KeyCompromised)], keys: [(k0.Kid, "compromised")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationSignedByLogKey(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Log, [k0.Kid], FromIndex: 1, Prev: 1), root, "declaration-signed-by-log-key");
        return new HCase("declaration-signed-by-log-key",
            "A declaration signed by the log key, published at 3 as a log key: the header's kid names no declaration key.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "declaration-key-unpublished")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationKeyIsAlsoALogKey(string root, Key k0)
    {
        var log = EnrolledAndBound(k0);
        PublishDeclarationKey(log, ConformanceLogKeys.Declaration);
        PublishLogKey(log, ConformanceLogKeys.Declaration);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1), root, "declaration-key-is-also-a-log-key");
        return new HCase("declaration-key-is-also-a-log-key",
            "One key published at 2 as a declaration key and at 3 as a log key, and a declaration it signed at 4: a declaration key that is also a log key never declares.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "declaration-key-is-log-key")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationKeyNeverPublished(string root, Key k0)
    {
        var log = EnrolledAndBound(k0);
        PublishLogKey(log, ConformanceLogKeys.Log);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Unpublished, [k0.Kid], FromIndex: 1, Prev: 1), root, "declaration-key-never-published");
        return new HCase("declaration-key-never-published",
            "A declaration signed by a key the log never published (only a log key is), under that key's own kid.",
            log, Agent_(rows: [HRow.Skipped(3, KeyCompromised, "declaration-key-unpublished")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationKeyPublishedAfterTheDeclaration(string root, Key k0)
    {
        var log = EnrolledAndBound(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1), root, "declaration-key-published-after-the-declaration");
        PublishDeclarationKey(log, ConformanceLogKeys.Declaration);
        return new HCase("declaration-key-published-after-the-declaration",
            "A declaration at 2 signed by the declaration key, which the log publishes at 3, after it.",
            log, Agent_(rows: [HRow.Skipped(2, KeyCompromised, "declaration-key-unpublished")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationUnderTheSecondDeclarationKey(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        PublishDeclarationKey(log, ConformanceLogKeys.Unpublished);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Unpublished, [k0.Kid], FromIndex: 1, Prev: 1), root, "declaration-under-the-second-declaration-key");
        return new HCase("declaration-under-the-second-declaration-key",
            "A second declaration key published at 4 and a declaration signed under it at 5: the first published is the one.",
            log, Agent_(rows: [HRow.Skipped(5, KeyCompromised, "declaration-key-not-first")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationBadSignature(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1) { SignWith = ConformanceLogKeys.Unpublished }, root, "declaration-bad-signature");
        return new HCase("declaration-bad-signature",
            "A declaration whose header names the declaration key (the right typ and kid) and whose signature bytes are another key's.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "declaration-signature-invalid")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationGarbageSignature(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1) { IsGarbage = true }, root, "declaration-garbage-signature");
        return new HCase("declaration-garbage-signature",
            "A declaration whose declaration member is the string FORGED-NO-VALID-SIGNATURE, which is no JWS.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "lifecycle-typ-mismatch")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationKidsOmitCascade(string root, Key k0, Key k1)
    {
        var (log, _) = DeclarationBase(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1));
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 4), root, "declaration-kids-omit-cascade");
        return new HCase("declaration-kids-omit-cascade",
            "A rotation at 4 binds the second key under the first; a declaration at 5 from index 1 names the first key alone, and the cascade recomputed from the honoured prefix is both.",
            log, Agent_(rows: [HRow.Honoured(4, KeyBound), HRow.Skipped(5, KeyCompromised, "declaration-mismatch")],
                keys: [(k0.Kid, "active"), (k1.Kid, "active")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HDeclarationKidNotBound(string root, Key k0, Key kz)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [kz.Kid], FromIndex: 1, Prev: 1), root, "declaration-kid-not-bound");
        return new HCase("declaration-kid-not-bound",
            "A declaration naming a kid the honoured prefix never bound.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "kid-not-bound-to-agent")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationFromIndexBeforeBinding(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 0, Prev: 1), root, "declaration-from-index-before-binding");
        return new HCase("declaration-from-index-before-binding",
            "A declaration whose from_index is 0, below the log index 1 of the first kid's binding.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "declaration-mismatch")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationV1Statement(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1) { StatementTyp = "curia.key-compromise/1" }, root, "declaration-v1-statement");
        return new HCase("declaration-v1-statement",
            "A declaration whose statement typ is curia.key-compromise/1, the version R6.62 retired: a typ the Forum does not know.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "unknown-lifecycle-kind")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationUnknownReason(string root, Key k0)
    {
        var (log, _) = DeclarationBase(k0);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1) { Reason = "stolen-laptop" }, root, "declaration-unknown-reason");
        return new HCase("declaration-unknown-reason",
            "A signed declaration whose reason, stolen-laptop, is outside R6.26 (revised)'s closed set.",
            log, Agent_(rows: [HRow.Skipped(4, KeyCompromised, "unknown-reason")], keys: [(k0.Kid, "active")], bound: [k0.Kid]));
    }

    private static HCase HDeclarationCascadesThroughRotation(string root, Key k0, Key k1)
    {
        var (log, _) = DeclarationBase(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1));
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid, k1.Kid], FromIndex: 1, Prev: 4), root, "declaration-cascades-through-rotation");
        return new HCase("declaration-cascades-through-rotation",
            "A rotation at 4 binds the second key under the first; a declaration at 5 from index 1 names both, the cascade recomputed from the honoured prefix: both rows are honoured and both keys are compromised.",
            log, Agent_(rows: [HRow.Honoured(4, KeyBound), HRow.Honoured(5, KeyCompromised)],
                keys: [(k0.Kid, "compromised"), (k1.Kid, "compromised")], bound: [k0.Kid, k1.Kid]));
    }

    private static HCase HMalformedDeclarationKeyEntryIsNoKey(string root, Key k0)
    {
        var log = EnrolledAndBound(k0);
        log.Add("log.declaration-key", "log:declaration-keys", OperatorRole, Obj(("kid", Str(MalformedDeclarationKeyKid)), ("valid_from", Str(LogKeyValidFrom))));
        PublishDeclarationKey(log, ConformanceLogKeys.Declaration);
        PublishLogKey(log, ConformanceLogKeys.Log);
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid], FromIndex: 1, Prev: 1), root, "malformed-declaration-key-entry-is-no-key");
        return new HCase("malformed-declaration-key-entry-is-no-key",
            "A log.declaration-key entry with no jwk at 2 is no key and holds no position, so the real declaration key at 3 is the first published, and the declaration at 5 it signs is honoured.",
            log, Agent_(rows: [HRow.Honoured(5, KeyCompromised)], keys: [(k0.Kid, "compromised")], bound: [k0.Kid]));
    }

    private static HCase HRowInAnotherAggregateIsIgnored(Key k0, Key ky)
    {
        var log = EnrolledAndBound(k0);
        AddPair(log, OtherAgent, ky);
        AddRetirement(log, new Ret(k0, k0, Prev: 1) { AggregateOverride = OtherAgent });
        return new HCase("row-in-another-aggregate-is-ignored",
            "In Y's stream at 4 a retirement whose agent_id is X, naming X's key: X's lifecycle is the events of X's aggregate and does not see it, and for Y it is another agent's act.",
            log, Agent_(rows: [], keys: [(k0.Kid, "active")], bound: [k0.Kid]),
            [new HAgent(OtherAgent, [HRow.Skipped(4, KeyRetired, "statement-mismatch")], [(ky.Kid, "active")], [ky.Kid])]);
    }

    private static HCase HRetirementOfTheLastLiveKeyIsHonoured(Key k0)
    {
        var log = EnrolledAndBound(k0);
        AddRetirement(log, new Ret(k0, k0, Prev: 1));
        return new HCase("retirement-of-the-last-live-key-is-honoured",
            "The enrolled key, the identity's only key, retires itself: the act is honoured, and the identity is left with no live key.",
            log, Agent_(rows: [HRow.Honoured(2, KeyRetired)], keys: [(k0.Kid, "retired")], bound: [k0.Kid]));
    }

    private static HCase HMalformedRowBeforeGenuineDeclaration(string root, Key k0, Key k1)
    {
        var (log, _) = DeclarationBase(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1));
        AddRetirement(log, new Ret(k1, k1, Prev: 4) { OmitAgentId = true });
        AddHonouredDeclaration(log, new HonouredDeclaration(ConformanceLogKeys.Declaration, [k0.Kid, k1.Kid], FromIndex: 1, Prev: 4), root, "malformed-row-before-genuine-declaration");
        return new HCase("malformed-row-before-genuine-declaration",
            "A rotation at 4, a retirement at 5 whose payload has no agent_id (skipped), and a genuine declaration at 6 whose prev names 4: the malformed row hides nothing, and the declaration still compromises both keys (errata G68, ruling 33).",
            log, Agent_(rows: [HRow.Honoured(4, KeyBound), HRow.Skipped(5, KeyRetired, "statement-mismatch"), HRow.Honoured(6, KeyCompromised)],
                keys: [(k0.Kid, "compromised"), (k1.Kid, "compromised")], bound: [k0.Kid, k1.Kid]));
    }

    // ---- construction helpers ----------------------------------------------------------------------

    private static HAgent Agent_(IReadOnlyList<HRow> rows, IReadOnlyList<(string, string)> keys, IReadOnlyList<string> bound) => new(Agent, rows, keys, bound);

    /// <summary>The plan's base R: 0 enrolled, 1 the pair's binding, 2 a rotation from the first key to the second, prev 1.</summary>
    private static Log RotatedOnce(Key k0, Key k1)
    {
        var log = EnrolledAndBound(k0);
        AddRotation(log, new Rot(k0, k1, Prev: 1));
        return log;
    }

    /// <summary>The plan's base D: 0 enrolled, 1 the pair's binding, 2 the declaration key's publication, 3 a log key's publication.</summary>
    private static (Log Log, int Next) DeclarationBase(Key k0)
    {
        var log = EnrolledAndBound(k0);
        PublishDeclarationKey(log, ConformanceLogKeys.Declaration);
        PublishLogKey(log, ConformanceLogKeys.Log);
        return (log, log.Entries.Count);
    }

    /// <summary>An identity's enrolment and its pair, as the enrolment appends them: exactly {agent_id, kid} and {agent_id, jwk, kid}.</summary>
    private static void AddPair(Log log, string agent, Key key)
    {
        log.Add("agent.enrolled", agent, agent, Obj(("agent_id", Str(agent)), ("kid", Str(key.Kid))));
        log.Add(KeyBound, agent, agent, Obj(("agent_id", Str(agent)), ("jwk", key.Jwk), ("kid", Str(key.Kid))));
    }

    /// <summary>
    /// A declaration entry. Garbage is the string; a signature by another key is signed with that key under the declaration key's kid; otherwise it is signed by the
    /// signer, the first run pins the bytes and a later run keeps them and re-verifies them (ES256 signing is randomized).
    /// </summary>
    private static int AddHonouredDeclaration(Log log, HonouredDeclaration d, string root, string caseName)
    {
        var canonical = DeclarationStatement(Agent, d.Kids, d.FromIndex, d.Prev, d.Reason, d.StatementTyp);
        var text = Encoding.UTF8.GetString(canonical.Span);
        string declaration;
        if (d.IsGarbage)
        {
            declaration = Garbage;
        }
        else
        {
            declaration = FindPinned(Path.Combine(root, caseName), "entries", text)
                ?? Signing.Sign(canonical, "ES256", d.Signer.Kid, (d.SignWith ?? d.Signer).EcPrivateKeyDer, CompromiseHeaderTyp).Compact;
            var verifies = Signing.Verify(canonical, new JwsSignature(declaration), "ES256", d.Signer.Kid, d.Signer.Spki, CompromiseHeaderTyp).IsOk;
            if (verifies == (d.SignWith is not null))
            {
                throw new InvalidOperationException($"{caseName}: the declaration {(verifies ? "verifies" : "does not verify")} under its header key, which is not what the case constructs");
            }
        }

        return log.Add(KeyCompromised, Agent, OperatorRole, Obj(
            ("agent_id", Str(Agent)),
            ("declaration", Str(declaration)),
            ("from_index", Num(d.FromIndex)),
            ("kids", new JsonValue.Array([.. d.Kids.Select(k => Str(k))])),
            ("prev", Num(d.Prev)),
            ("reason", Str(d.Reason)),
            ("statement", Str(text))));
    }

    // ---- writing -----------------------------------------------------------------------------------

    private static void WriteHonoured(string root, HCase c)
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

        var expected = ExpectedOf(c.Main);
        if (c.Also is { Count: > 0 } also)
        {
            var others = new JsonArray();
            foreach (var other in also)
            {
                var node = ExpectedOf(other);
                node["agent"] = other.Agent;
                others.Add(node);
            }

            expected["also"] = others;
        }

        File.WriteAllText(Path.Combine(dir, "expected.json"), expected.ToJsonString() + "\n");

        var meta = new JsonObject
        {
            ["profile"] = "honoured-log",
            ["requirement"] = "R6.64",
            ["note"] = c.Note + " Statements are the exact output of CanonicalizeWithNfc, each signature is detached under its kind's header typ with its role's member as the header kid (spec 4.5); the keys the case publishes are its own log.key and log.declaration-key entries. expected.json is written from this construction and never by running a verifier. A proposal: it may be regenerated until the owner's lift record pins its digests.",
            ["agent"] = Agent,
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString(Jwk.PrettyOptions) + "\n");
        Console.WriteLine($"  wrote honoured/{c.Name}");
    }

    private static JsonObject ExpectedOf(HAgent agent)
    {
        var rows = new JsonArray();
        foreach (var row in agent.Rows)
        {
            var node = new JsonObject { ["log_index"] = row.Index, ["type"] = row.Type, ["verdict"] = row.Predicate is null ? "honoured" : "skipped" };
            if (row.Predicate is not null) node["predicate"] = row.Predicate;
            rows.Add(node);
        }

        var keys = new JsonObject();
        foreach (var (kid, status) in agent.Keys) keys[kid] = status;
        var bound = new JsonArray();
        foreach (var kid in agent.Bound) bound.Add(kid);
        return new JsonObject { ["rows"] = rows, ["keys"] = keys, ["bound"] = bound };
    }
}
