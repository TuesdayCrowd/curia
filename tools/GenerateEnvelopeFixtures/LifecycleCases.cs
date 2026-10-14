using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using NSec.Cryptography;
using static GenerateEnvelopeFixtures.JsonBuilders;
using JsonValue = Curia.Canon.Json.JsonValue;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// conformance/lifecycle/: small whole logs, each with the verdict a reader that folds the author's
/// lifecycle must reach (spec 4.6, R6.54 (add.)). A case holds the entries and proofs a reader fetches, a
/// signed head over the whole log and the log's key set, and the expected verdict and predicate; both
/// runners (Curia.Client.Tests and curia-testis) run every case.
///
/// <para>Everything is deterministic except <c>rotation-es256-then-post</c>: ES256 signing is randomized,
/// so that case is generated once and kept (spec 4.6), and a rerun leaves it as it is. Heads are signed
/// by a fixed Ed25519 log key, which is deterministic, so no other case moves between runs.</para>
///
/// <para>Each case holds one defect, so a verdict of failed cannot come from another cause: the rest of
/// its log is what a case that verifies would hold.</para>
/// </summary>
internal static class LifecycleCases
{
    private const string Agent = "agent://curia.example/tuesdaycrowd/scriptor";
    private const string OtherAgent = "agent://curia.example/tuesdaycrowd/other";
    private const string LogKid = "conformance-log-ed25519";
    private const string RotationHeaderTyp = "curia-key-rotation+jws";
    private const string RetirementHeaderTyp = "curia-key-retirement+jws";
    private const string RotationTyp = "curia.key-rotation/1";
    private const string RetirementTyp = "curia.key-retirement/1";
    private const string FrozenCase = "rotation-es256-then-post";

    private sealed record Key(string Kid, string Alg, byte[] Signing, JsonValue Jwk);

    private sealed class Log
    {
        public List<JsonValue> Entries { get; } = [];

        public int Add(string type, string aggregate, string? actor, JsonValue payload)
        {
            var index = Entries.Count;
            Entries.Add(Obj(
                ("actor_id", actor is null ? Null : Str(actor)),
                ("aggregate_id", Str(aggregate)),
                ("event_id", Str("01K4CQ4A" + new string('0', 14) + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))),
                ("event_type", Str(type)),
                ("payload", payload),
                ("server_ts", Str($"2026-09-05T10:{index:D2}:00.000000Z"))));
            return index;
        }
    }

    private sealed record Case(
        string Name, string Requirement, string Note, Log Log, int Post, int[] Supplied, string Verdict, string Predicate);

    public static int Run(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "conformance", "lifecycle");
        Directory.CreateDirectory(root);

        using var firstDoc = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repoRoot, "conformance", "envelope", "ed25519-minimal", "private-keys.json")));
        var firstJwk = firstDoc.RootElement.GetProperty("keys")[0];
        var k1 = Ed("conformance-ed25519-minimal", Jwk.Base64UrlDecode(firstJwk.GetProperty("d").GetString()!));
        var k2 = Ed("conformance-ed25519-second", Enumerable.Repeat((byte)0x02, 32).ToArray());
        var k3 = Ed("conformance-ed25519-third", Enumerable.Repeat((byte)0x03, 32).ToArray());
        var stranger = Ed("conformance-ed25519-stranger", Enumerable.Repeat((byte)0x09, 32).ToArray());

        var cases = new List<Case>
        {
            RotationThenPost(k1, k2),
            PostAfterRetirement(k1),
            ForgedRotation(k1, k2, stranger),
            RotationAuthorizedByRetiredKey(k1, k2),
            ForeignIdentityEntry(k1),
            StatementDuplicateMember(k1, k2),
            EntryMemberDisagrees(k1, k2, k3),
            SiblingSignedRetirement(k1, k2),
            BrokenLink(k1, k2),
            LinkAfterPostSkips(k1, k2),
            NoLifecycleSupplied(k1),
            RotationSignedAsPost(k1, k2),
            RetirementSignedAsRotation(k1, k2),
            HeaderKidDisagrees(k1, k2),
            RotationJwkAlgMismatch(k1, k2),
            RotationSelfAuthorized(k1, k2),
            StatementExtraMember(k1, k2),
            EntryExtraMember(k1, k2),
            PreG16FirstBindingAfterPost(k1),
            UnknownLifecycleKind(k1),
            UnknownStatementTyp(k1, k2, extraMember: false),
            UnknownStatementTyp(k1, k2, extraMember: true),
            PrevlessBindingAfterRotation(k1, k2, k3),
            PrevlessBindingAfterRetirement(k1, k2),
            SecondPrevlessBinding(k1),
            PrevlessBindingOtherKid(k1, k2),
            PrevlessBindingBeforeEnrolled(k1),
            PrevlessBindingMissingJwk(k1),
            RetirementWithoutAgentId(k1),
        };

        var logSeed = Enumerable.Repeat((byte)0x05, 32).ToArray();
        var logPublic = PublicOf(logSeed);
        var written = 0;
        foreach (var c in cases)
        {
            Write(root, c, logSeed, logPublic);
            written++;
        }

        // The ES256 case is generated once and kept: its signatures are randomized.
        if (File.Exists(Path.Combine(root, FrozenCase, "expected.json")))
        {
            Console.WriteLine($"  kept lifecycle/{FrozenCase} (ES256 signatures are randomized; generated once)");
        }
        else
        {
            Write(root, Es256ThenPost(), logSeed, logPublic);
            written++;
        }

        Console.WriteLine($"lifecycle cases written under conformance/lifecycle/ ({written} written)");
        return 0;
    }

    // ---- cases ---------------------------------------------------------------------------------

    private static Case RotationThenPost(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1));
        var post = AddPost(log, k2, "01");
        AddRetirement(log, new Ret(k1, k1, Prev: 2));
        return new Case("rotation-then-post", "R6.54",
            "The enrolled key authorizes a second; the second signs a post; the enrolled key then retires itself. Every link holds through the first entry after the post.",
            log, post, [0, 1, 2, 4], "verified", "verified");
    }

    private static Case PostAfterRetirement(Key k1)
    {
        var log = EnrolledAndBound(k1);
        AddRetirement(log, new Ret(k1, k1, Prev: 1));
        var post = AddPost(log, k1, "02");
        return new Case("post-after-retirement", "R6.54",
            "The enrolled key retires itself, and signs a post after it: retired at 2, before the post at 3.",
            log, post, [0, 1, 2], "failed", "retired-before-post");
    }

    private static Case ForgedRotation(Key k1, Key k2, Key stranger)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { AuthSigner = stranger });
        var post = AddPost(log, k2, "03");
        return new Case("forged-rotation", "R6.54",
            "The rotation's authorization names the enrolled key in its header but was made by a stranger's key: the authorization does not verify.",
            log, post, [0, 1, 2], "failed", "rotation-unauthorized");
    }

    private static Case RotationAuthorizedByRetiredKey(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRetirement(log, new Ret(k1, k1, Prev: 1));
        AddRotation(log, new Rot(k1, k2, Prev: 2));
        var post = AddPost(log, k2, "04");
        return new Case("rotation-authorized-by-retired-key", "R6.54",
            "Both signatures verify, and the authorizing key was retired at 2, before the rotation at 3.",
            log, post, [0, 1, 2, 3], "failed", "rotation-unauthorized");
    }

    private static Case ForeignIdentityEntry(Key k1)
    {
        var log = EnrolledAndBound(k1);
        log.Add("agent.key-bound", OtherAgent, OtherAgent, Obj(
            ("agent_id", Str(OtherAgent)), ("jwk", k1.Jwk), ("kid", Str(k1.Kid))));
        var post = AddPost(log, k1, "05");
        return new Case("foreign-identity-entry", "R6.54",
            "The listed lifecycle holds another identity's key binding.",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case StatementDuplicateMember(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        var rot = new Rot(k1, k2, Prev: 1);
        var clean = RotationStatement(rot);
        var duplicated = "{\"prev\":1," + Encoding.UTF8.GetString(clean.Span)[1..];
        AddRotation(log, rot with { StatementText = duplicated });
        var post = AddPost(log, k2, "06");
        return new Case("statement-duplicate-member", "R6.54",
            "The rotation's statement repeats the member prev, so it is not JSON a reader can admit.",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case EntryMemberDisagrees(Key k1, Key k2, Key k3)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { EntryKid = k3.Kid });
        var post = AddPost(log, k2, "07");
        return new Case("entry-member-disagrees", "R6.54",
            "The entry states kid as the third key while its statement binds the second.",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case SiblingSignedRetirement(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1));
        AddRetirement(log, new Ret(k1, k1, Prev: 2) { Signer = k2, HeaderKid = k2.Kid });
        var post = AddPost(log, k2, "08");
        return new Case("sibling-signed-retirement", "R6.54",
            "The enrolled key's retirement is signed by its sibling, the second key, whose signature verifies under its own header kid.",
            log, post, [0, 1, 2, 3], "failed", "rotation-unauthorized");
    }

    private static Case BrokenLink(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 0));
        var post = AddPost(log, k2, "09");
        return new Case("broken-link", "R6.54",
            "The rotation names the enrolment at 0 as the entry before it, and the entry before it is the binding at 1.",
            log, post, [0, 1, 2], "failed", "broken-link");
    }

    private static Case LinkAfterPostSkips(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1));
        var post = AddPost(log, k2, "10");
        AddRetirement(log, new Ret(k1, k1, Prev: 2));
        return new Case("link-after-post-skips", "R6.54",
            "The Forum omits the rotation at 2, the entry immediately before the post, from the lifecycle it lists; the retirement after the post links from 2 and the entry before it in the list is 1.",
            log, post, [0, 1, 4], "failed", "broken-link");
    }

    private static Case NoLifecycleSupplied(Key k1)
    {
        var log = EnrolledAndBound(k1);
        var post = AddPost(log, k1, "11");
        return new Case("no-lifecycle-supplied", "R6.54",
            "No lifecycle entry is supplied for the author: a reader cannot say the key was not retired.",
            log, post, [], "could-not-check", "lifecycle-not-supplied");
    }

    private static Case RotationSignedAsPost(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { AuthTyp = DetachedJws.ExpectedTyp, PossTyp = DetachedJws.ExpectedTyp });
        var post = AddPost(log, k2, "12");
        return new Case("rotation-signed-as-post", "R6.54",
            "Both signatures verify, under the header typ of a post (KF1).",
            log, post, [0, 1, 2], "failed", "lifecycle-typ-mismatch");
    }

    private static Case RetirementSignedAsRotation(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1));
        AddRetirement(log, new Ret(k1, k1, Prev: 2) { Typ = RotationHeaderTyp });
        var post = AddPost(log, k2, "13");
        return new Case("retirement-signed-as-rotation", "R6.54",
            "A valid retirement signature whose header typ is a rotation's (KF1).",
            log, post, [0, 1, 2, 3], "failed", "lifecycle-typ-mismatch");
    }

    private static Case HeaderKidDisagrees(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { AuthSigner = k2, AuthHeaderKid = k2.Kid });
        var post = AddPost(log, k2, "14");
        return new Case("header-kid-disagrees", "R6.54",
            "The authorization is made by the second key under its own header kid, though the statement names the enrolled key as authorized_by (KF2).",
            log, post, [0, 1, 2], "failed", "rotation-unauthorized");
    }

    private static Case RotationJwkAlgMismatch(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        var wrongAlg = Obj(
            ("alg", Str("ES256")), ("crv", Str("Ed25519")), ("kid", Str(k2.Kid)), ("kty", Str("OKP")),
            ("x", Str(((JsonValue.String)Member(k2.Jwk, "x")).Value)));
        AddRotation(log, new Rot(k1, k2, Prev: 1) { JwkOverride = wrongAlg });
        var post = AddPost(log, k2, "15");
        return new Case("rotation-jwk-alg-mismatch", "R6.54",
            "The rotation's jwk states alg ES256 for an OKP/Ed25519 key (KF5).",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case RotationSelfAuthorized(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { AuthorizedByOverride = k2.Kid, AuthSigner = k2, AuthHeaderKid = k2.Kid });
        var post = AddPost(log, k2, "16");
        return new Case("rotation-self-authorized", "R6.54",
            "authorized_by equals kid: the new key authorizes its own binding (KF5).",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case StatementExtraMember(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { ExtraStatementMember = true });
        var post = AddPost(log, k2, "17");
        return new Case("statement-extra-member", "R6.54",
            "The rotation's statement carries a member outside its set (KF4).",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case EntryExtraMember(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { ExtraEntryMember = true });
        var post = AddPost(log, k2, "18");
        return new Case("entry-extra-member", "R6.54 (add.)",
            "An otherwise-valid rotation entry carries one undeclared member beside statement (KF4).",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case PreG16FirstBindingAfterPost(Key k1)
    {
        var log = new Log();
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        var post = AddPost(log, k1, "19");
        AddBinding(log, k1);
        return new Case("pre-g16-first-binding-after-post", "R4.38",
            "An identity whose chain is its enrolment alone gains its prev-less first binding after its post: the log holds no key for the post from before it, which is not a broken link.",
            log, post, [0, 2], "could-not-check", "bound-after-post");
    }

    private static Case UnknownLifecycleKind(Key k1)
    {
        var log = EnrolledAndBound(k1);
        log.Add("agent.key-frozen", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        var post = AddPost(log, k1, "20");
        return new Case("unknown-lifecycle-kind", "R6.54",
            "The lifecycle lists an entry of a type none of the four (KF4).",
            log, post, [0, 1, 2], "could-not-check", "unknown-lifecycle-kind");
    }

    private static Case UnknownStatementTyp(Key k1, Key k2, bool extraMember)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1) { StatementTyp = "curia.key-rotation/2", ExtraStatementMember = extraMember });
        var post = AddPost(log, k2, extraMember ? "22" : "21");
        return new Case(
            extraMember ? "unknown-statement-typ-extra-member" : "unknown-statement-typ",
            "R6.54",
            extraMember
                ? "A curia.key-rotation/2 statement carrying a member outside /1's set: the typ is read before the member set (KF4)."
                : "A statement typ of curia.key-rotation/2, a version the reader does not know (KF4).",
            log, post, [0, 1, 2], "could-not-check", "unknown-lifecycle-kind");
    }

    private static Case PrevlessBindingAfterRotation(Key k1, Key k2, Key k3)
    {
        var log = EnrolledAndBound(k1);
        AddRotation(log, new Rot(k1, k2, Prev: 1));
        AddBinding(log, k3);
        var post = AddPost(log, k2, "23");
        return new Case("prevless-binding-after-rotation", "R4.38",
            "A prev-less binding after a rotation is not the enrolment's first binding.",
            log, post, [0, 1, 2, 3], "failed", "broken-link");
    }

    private static Case PrevlessBindingAfterRetirement(Key k1, Key k2)
    {
        var log = EnrolledAndBound(k1);
        AddRetirement(log, new Ret(k1, k1, Prev: 1));
        AddBinding(log, k2);
        var post = AddPost(log, k2, "24");
        return new Case("prevless-binding-after-retirement", "R4.38",
            "A prev-less binding after a retirement is not the enrolment's first binding.",
            log, post, [0, 1, 2, 3], "failed", "broken-link");
    }

    private static Case SecondPrevlessBinding(Key k1)
    {
        var log = EnrolledAndBound(k1);
        AddBinding(log, k1);
        var post = AddPost(log, k1, "25");
        return new Case("second-prevless-binding", "R4.38",
            "A second prev-less binding of the enrolment's own kid: the enrolment has one.",
            log, post, [0, 1, 2], "failed", "broken-link");
    }

    private static Case PrevlessBindingOtherKid(Key k1, Key k2)
    {
        var log = new Log();
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        AddBinding(log, k2);
        var post = AddPost(log, k2, "26");
        return new Case("prevless-binding-other-kid", "R4.38",
            "A prev-less binding right after the enrolment, of a kid other than the one the enrolment names.",
            log, post, [0, 1], "failed", "broken-link");
    }

    private static Case PrevlessBindingBeforeEnrolled(Key k1)
    {
        var log = new Log();
        AddBinding(log, k1);
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        var post = AddPost(log, k1, "27");
        return new Case("prevless-binding-before-enrolled", "R4.38",
            "A prev-less binding that precedes the identity's enrolment.",
            log, post, [0, 1], "failed", "broken-link");
    }

    private static Case PrevlessBindingMissingJwk(Key k1)
    {
        var log = new Log();
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        log.Add("agent.key-bound", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        var post = AddPost(log, k1, "28");
        return new Case("prevless-binding-missing-jwk", "R4.38",
            "The enrolment's binding carries {agent_id, kid} and no jwk: the member set is not exactly {agent_id, jwk, kid}.",
            log, post, [0, 1], "failed", "statement-mismatch");
    }

    private static Case RetirementWithoutAgentId(Key k1)
    {
        var log = EnrolledAndBound(k1);
        AddRetirement(log, new Ret(k1, k1, Prev: 1) { OmitAgentId = true });
        var post = AddPost(log, k1, "29");
        return new Case("retirement-without-agent-id", "R4.38",
            "A retirement entry in the identity's own stream that names no agent is refused, never skipped.",
            log, post, [0, 1, 2], "failed", "statement-mismatch");
    }

    private static Case Es256ThenPost()
    {
        var a = P256Key("conformance-es256-x00", skip: 0);
        var b = P256Key("conformance-es256-x00-second", skip: 1);
        var log = new Log();
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(a.Kid))));
        AddBinding(log, a);
        AddRotation(log, new Rot(a, b, Prev: 1));
        var post = AddPost(log, b, "30");
        return new Case(FrozenCase, "R6.54",
            "P-256 keys whose x coordinates begin with the byte 0x00: the first authorizes the second, which signs the post. The signatures are randomized and were generated once; they are kept.",
            log, post, [0, 1, 2], "verified", "verified");
    }

    // ---- entries -------------------------------------------------------------------------------

    private static Log EnrolledAndBound(Key k1)
    {
        var log = new Log();
        log.Add("agent.enrolled", Agent, Agent, Obj(("agent_id", Str(Agent)), ("kid", Str(k1.Kid))));
        AddBinding(log, k1);
        return log;
    }

    /// <summary>A prev-less binding, as an enrolment appends it: exactly {agent_id, jwk, kid}.</summary>
    private static int AddBinding(Log log, Key key) => log.Add("agent.key-bound", Agent, Agent, Obj(
        ("agent_id", Str(Agent)), ("jwk", key.Jwk), ("kid", Str(key.Kid))));

    private sealed record Rot(Key Authorizer, Key NewKey, long Prev)
    {
        public Key? AuthSigner { get; init; }
        public string? AuthHeaderKid { get; init; }
        public string AuthTyp { get; init; } = RotationHeaderTyp;
        public string PossTyp { get; init; } = RotationHeaderTyp;
        public string StatementTyp { get; init; } = RotationTyp;
        public string? AuthorizedByOverride { get; init; }
        public JsonValue? JwkOverride { get; init; }
        public bool ExtraStatementMember { get; init; }
        public bool ExtraEntryMember { get; init; }
        public string? StatementText { get; init; }
        public string? EntryKid { get; init; }
    }

    private sealed record Ret(Key Key, Key Authorizer, long Prev)
    {
        public Key? Signer { get; init; }
        public string? HeaderKid { get; init; }
        public string Typ { get; init; } = RetirementHeaderTyp;
        public bool OmitAgentId { get; init; }
    }

    private static CanonicalBytes RotationStatement(Rot r)
    {
        var members = new List<(string, JsonValue)>
        {
            ("agent_id", Str(Agent)),
            ("authorized_by", Str(r.AuthorizedByOverride ?? r.Authorizer.Kid)),
            ("jwk", r.JwkOverride ?? r.NewKey.Jwk),
            ("kid", Str(r.NewKey.Kid)),
            ("prev", Num(r.Prev)),
            ("typ", Str(r.StatementTyp)),
        };
        if (r.ExtraStatementMember) members.Add(("note", Str("x")));
        return Canon(Obj([.. members]));
    }

    private static int AddRotation(Log log, Rot r)
    {
        var canonical = RotationStatement(r);
        var text = r.StatementText ?? Encoding.UTF8.GetString(canonical.Span);
        var authSigner = r.AuthSigner ?? r.Authorizer;
        var authorization = Signing.Sign(canonical, authSigner.Alg, r.AuthHeaderKid ?? r.Authorizer.Kid, authSigner.Signing, r.AuthTyp);
        var possession = Signing.Sign(canonical, r.NewKey.Alg, r.NewKey.Kid, r.NewKey.Signing, r.PossTyp);

        var members = new List<(string, JsonValue)>
        {
            ("agent_id", Str(Agent)),
            ("authorization", Str(authorization.Compact)),
            ("authorized_by", Str(r.AuthorizedByOverride ?? r.Authorizer.Kid)),
            ("jwk", r.JwkOverride ?? r.NewKey.Jwk),
            ("kid", Str(r.EntryKid ?? r.NewKey.Kid)),
            ("possession", Str(possession.Compact)),
            ("prev", Num(r.Prev)),
            ("statement", Str(text)),
        };
        if (r.ExtraEntryMember) members.Add(("note", Str("x")));
        return log.Add("agent.key-bound", Agent, Agent, Obj([.. members]));
    }

    private static int AddRetirement(Log log, Ret r)
    {
        var canonical = Canon(Obj(
            ("agent_id", Str(Agent)), ("kid", Str(r.Key.Kid)), ("prev", Num(r.Prev)), ("typ", Str(RetirementTyp))));
        var signer = r.Signer ?? r.Authorizer;
        var authorization = Signing.Sign(canonical, signer.Alg, r.HeaderKid ?? r.Authorizer.Kid, signer.Signing, r.Typ);

        var members = new List<(string, JsonValue)>();
        if (!r.OmitAgentId) members.Add(("agent_id", Str(Agent)));
        members.Add(("authorization", Str(authorization.Compact)));
        members.Add(("kid", Str(r.Key.Kid)));
        members.Add(("prev", Num(r.Prev)));
        members.Add(("statement", Str(Encoding.UTF8.GetString(canonical.Span))));
        return log.Add("agent.key-retired", Agent, Agent, Obj([.. members]));
    }

    /// <summary>A post.accepted of the identity, signed under <paramref name="signer"/> (the post's own typ).</summary>
    private static int AddPost(Log log, Key signer, string nonceLabel)
    {
        var envelope = (JsonValue.Object)Obj(
            ("v", Num(1)),
            ("kind", Str("comment")),
            ("author", Str(Agent)),
            ("board", Str("distributed-systems")),
            ("parent", Null),
            ("prev", Null),
            ("title", Null),
            ("body", Str("A post whose key the log's lifecycle either leaves live or ends.")),
            ("code_blocks", Arr()),
            ("refs", Arr()),
            ("tags", Arr()),
            ("content_type", Str("agent-authored/untrusted")),
            ("created_at", Str("2026-09-05T09:00:00Z")),
            ("nonce", Str("b1b1e6f0a0c94e3a9a7d2f4c8e5a1c" + nonceLabel)),
            ("model_hint", Null));
        var canonical = Signing.Canonicalize(envelope);
        var signature = Signing.Sign(canonical, signer.Alg, signer.Kid, signer.Signing);
        var postId = "01K4CQ5B" + new string('0', 14) + log.Entries.Count.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

        return log.Add("post.accepted", postId, Agent, Obj(
            ("author", Str(Agent)),
            ("board", Str("distributed-systems")),
            ("canonical", Str(Encoding.UTF8.GetString(canonical.Span))),
            ("digest", Str(Digests.Sha256(canonical).ToPrefixed())),
            ("kind", Str("comment")),
            ("parent", Null),
            ("post_id", Str(postId)),
            ("risk_flags", Arr()),
            ("server_ts", Str($"2026-09-05T10:{log.Entries.Count:D2}:00.000000Z")),
            ("signature", Str(signature.Compact))));
    }

    // ---- writing -------------------------------------------------------------------------------

    private static void Write(string root, Case c, byte[] logSeed, byte[] logPublic)
    {
        var dir = Path.Combine(root, c.Name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(Path.Combine(dir, "entries"));
        Directory.CreateDirectory(Path.Combine(dir, "proofs"));

        var canonicals = c.Log.Entries.Select(e => CanonicalJson.Canonicalize(e)
            .Match(b => b, err => throw new InvalidOperationException($"{c.Name}: canonicalize failed: {err.Type}"))).ToList();
        var leaves = canonicals.Select(b => MerkleTree.LeafHash(b.Span)).ToImmutableArray();
        var root32 = MerkleTree.Root(leaves);
        var size = leaves.Length;

        foreach (var index in c.Supplied.Append(c.Post).Distinct().Order())
        {
            var path = MerkleTree.InclusionPath(leaves, index);
            File.WriteAllText(
                Path.Combine(dir, "entries", $"{index}.json"),
                $"{{\"log_index\":{index},\"leaf_hash\":\"{Prefixed(leaves[index])}\",\"entry\":{Encoding.UTF8.GetString(canonicals[index].Span)}}}\n");
            File.WriteAllText(
                Path.Combine(dir, "proofs", $"{index}.json"),
                $"{{\"log_index\":{index},\"tree_size\":{size},\"leaf_hash\":\"{Prefixed(leaves[index])}\",\"audit_path\":[{string.Join(",", path.Select(n => $"\"{Prefixed(n)}\""))}],\"root_hash\":\"{Prefixed(root32)}\",\"head_signed\":true}}\n");
        }

        var headObject = Obj(("root_hash", Str(Prefixed(root32))), ("timestamp", Str("2026-09-05T12:00:00Z")), ("tree_size", Num(size)));
        var headCanonical = Canon(headObject);
        var headSignature = Signing.Sign(headCanonical, "EdDSA", LogKid, logSeed, DetachedJws.HeadTyp);
        File.WriteAllText(
            Path.Combine(dir, "head.json"),
            $"{{\"head\":{Encoding.UTF8.GetString(headCanonical.Span)},\"kid\":\"{LogKid}\",\"signature\":\"{headSignature.Compact}\",\"log_index\":{size},\"signature_valid\":true,\"current_tree_size\":{size}}}\n");

        // The regression guard for R6.49 (spec 4.6, "Outside the lifecycle family"): the same head, signed by the same log
        // key over the same bytes, under each lifecycle typ. A head verifier refuses both, by exact typ; the files sit in
        // the one case so both runners hold the same pinned signatures and neither has to sign.
        if (c.Name == "rotation-then-post")
        {
            foreach (var (suffix, typ) in new[] { ("rotation", RotationHeaderTyp), ("retirement", RetirementHeaderTyp) })
            {
                var asLifecycle = Signing.Sign(headCanonical, "EdDSA", LogKid, logSeed, typ);
                File.WriteAllText(
                    Path.Combine(dir, $"head-as-{suffix}.json"),
                    $"{{\"head\":{Encoding.UTF8.GetString(headCanonical.Span)},\"kid\":\"{LogKid}\",\"signature\":\"{asLifecycle.Compact}\",\"log_index\":{size},\"signature_valid\":true,\"current_tree_size\":{size}}}\n");
            }
        }

        File.WriteAllText(
            Path.Combine(dir, "log-jwks.json"),
            $"{{\"keys\":[{{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"alg\":\"EdDSA\",\"kid\":\"{LogKid}\",\"x\":\"{Jwk.Base64UrlEncode(logPublic)}\",\"valid_from\":\"2026-09-01T00:00:00Z\",\"log_index\":0}}]}}\n");
        File.WriteAllText(
            Path.Combine(dir, "expected.json"),
            $"{{\"verdict\":\"{c.Verdict}\",\"predicate\":\"{c.Predicate}\"}}\n");

        var meta = new JsonObject
        {
            ["profile"] = "lifecycle-log",
            ["requirement"] = c.Requirement,
            ["note"] = c.Note + " Statements are the exact output of CanonicalizeWithNfc, each signature is detached under its kind's header typ with its role's member as the header kid (spec 4.5); the head is signed by a fixed Ed25519 log key. A proposal: it may be regenerated until the owner's lift record pins its digests.",
            ["post_index"] = c.Post,
            ["agent"] = Agent,
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString(Jwk.PrettyOptions) + "\n");
        Console.WriteLine($"  wrote lifecycle/{c.Name}");
    }

    // ---- keys ----------------------------------------------------------------------------------

    private static Key Ed(string kid, byte[] seed) => new(kid, "EdDSA", seed, Obj(
        ("alg", Str("EdDSA")), ("crv", Str("Ed25519")), ("kid", Str(kid)), ("kty", Str("OKP")),
        ("x", Str(Jwk.Base64UrlEncode(PublicOf(seed))))));

    private static byte[] PublicOf(byte[] seed)
    {
        using var key = NSec.Cryptography.Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>The <paramref name="skip"/>th P-256 key, over a counter-derived scalar, whose x coordinate begins with 0x00.</summary>
    private static Key P256Key(string kid, int skip)
    {
        var found = 0;
        for (var counter = 0; counter < 1_000_000; counter++)
        {
            var d = SHA256.HashData(Encoding.UTF8.GetBytes($"curia-conformance-p256-x00-{counter}"));
            ECParameters parameters;
            try
            {
                using var candidate = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
                parameters = candidate.ExportParameters(includePrivateParameters: true);
            }
            catch (CryptographicException)
            {
                continue;
            }

            if (parameters.Q.X![0] != 0x00) continue;
            if (found++ < skip) continue;

            using var ecdsa = ECDsa.Create(parameters);
            return new Key(kid, "ES256", ecdsa.ExportECPrivateKey(), Obj(
                ("alg", Str("ES256")), ("crv", Str("P-256")), ("kid", Str(kid)), ("kty", Str("EC")),
                ("x", Str(Jwk.Base64UrlEncode(parameters.Q.X))), ("y", Str(Jwk.Base64UrlEncode(parameters.Q.Y!)))));
        }

        throw new InvalidOperationException("no P-256 key with a leading-zero x coordinate within the search bound");
    }

    private static CanonicalBytes Canon(JsonValue value) =>
        CanonicalJson.CanonicalizeWithNfc(value)
            .Match(b => b, e => throw new InvalidOperationException($"canonicalize failed: {e.Type}"));

    private static JsonValue Member(JsonValue obj, string name) =>
        ((JsonValue.Object)obj).Members.First(m => m.Key == name).Value;

    private static string Prefixed(ImmutableArray<byte> hash) => "sha256:" + Convert.ToHexStringLower([.. hash]);
}
