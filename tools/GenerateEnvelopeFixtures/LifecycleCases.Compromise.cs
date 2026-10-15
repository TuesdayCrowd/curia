using System.Text;
using System.Text.Json;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using static GenerateEnvelopeFixtures.JsonBuilders;
using JsonValue = Curia.Canon.Json.JsonValue;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// PR 2's cases (keys that end, Task 2.0): whole small logs that carry the operator's compromise declaration, and the
/// <c>acta/key-compromised-entry</c> vector. Run by <c>--compromise</c>, and first run in Task 2.3: a declaration is signed
/// ES256 by a fixed P-256 log key, ES256 signing is randomized, so the first run pins each declaration's signature bytes and
/// every later run re-verifies the pinned signature and keeps it. The generator never re-signs a declaration, and refuses
/// a case whose pinned statement changed rather than re-sign it (spec 4.6).
///
/// <para>Every declaration's header <c>typ</c> is <c>curia-key-compromise+jws</c> and its header <c>kid</c> is the log key's
/// (KF1, KF2), except in the cases that exist to break exactly that. Each case holds one defect (KF3, KF4).</para>
/// </summary>
internal static partial class LifecycleCases
{
    private const string CompromiseHeaderTyp = "curia-key-compromise+jws";
    private const string CompromiseTyp = "curia.key-compromise/1";
    private const string OperatorRole = "operator";
    private const string LogKeyValidFrom = "2026-09-01T00:00:00Z";

    private sealed record Decl(P256LogKey Signer, string[] Kids, long FromIndex, long Prev)
    {
        public string Reason { get; init; } = "owner-reported";
        public string StatementTyp { get; init; } = CompromiseTyp;
        public string HeaderTyp { get; init; } = CompromiseHeaderTyp;
    }

    public static int RunCompromise(string repoRoot)
    {
        var status = Lifecycle.RunCompromiseVector(repoRoot);
        if (status != 0)
            return status;

        var root = Path.Combine(repoRoot, "conformance", "lifecycle");
        Directory.CreateDirectory(root);

        using var firstDoc = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(repoRoot, "conformance", "envelope", "ed25519-minimal", "private-keys.json")));
        var firstJwk = firstDoc.RootElement.GetProperty("keys")[0];
        var k1 = Ed("conformance-ed25519-minimal", Jwk.Base64UrlDecode(firstJwk.GetProperty("d").GetString()!));
        var k2 = Ed("conformance-ed25519-second", Enumerable.Repeat((byte)0x02, 32).ToArray());
        var stranger = Ed("conformance-ed25519-stranger", Enumerable.Repeat((byte)0x09, 32).ToArray());

        var cases = new List<Case>
        {
            CompromisedBeforePost(root, k1),
            CompromisedAfterInterveningRotation(root, k1, k2, "compromised-after-intervening-rotation", reorderKids: false, omitCascade: false),
            DeclarationSignedAsHead(root, k1),
            CompromisedAfterInterveningRotation(root, k1, k2, "compromise-kids-omits-cascade", reorderKids: false, omitCascade: true),
            FromIndexAfterDeclaration(root, k1),
            FromIndexAtDeclaration(root, k1),
            KidOfOtherIdentity(root, k1, stranger),
            UnknownReason(root, k1),
            DeclarationKidNotValidAtPosition(root, k1),
            DeclarationKidPositionUnknown(root, k1),
            V2UnderUnknownSigner(root, k1),
            FromIndexBeforeBinding(root, k1),
            CompromisedAfterInterveningRotation(root, k1, k2, "compromise-kids-reordered", reorderKids: true, omitCascade: false),
        };

        var logSeed = Enumerable.Repeat((byte)0x05, 32).ToArray();
        var logPublic = PublicOf(logSeed);
        foreach (var c in cases)
            Write(root, c, logSeed, logPublic);

        Console.WriteLine($"compromise cases written under conformance/lifecycle/ ({cases.Count} written)");
        return 0;
    }

    // ---- cases ---------------------------------------------------------------------------------

    /// <summary>0 enrolled, 1 binding, 2 the post, 3 the log key, 4 the declaration (kids [k1], from_index 1, prev 1).</summary>
    private static (Log Log, int Post, int LogKeyIndex, Decl Decl) PostThenDeclaration(Key k1, string nonce, Func<Decl, Decl>? adjust = null)
    {
        var log = EnrolledAndBound(k1);
        var post = AddPost(log, k1, nonce);
        var logKeyIndex = PublishLogKey(log, ConformanceLogKeys.Log);
        var decl = new Decl(ConformanceLogKeys.Log, [k1.Kid], FromIndex: 1, Prev: 1);
        return (log, post, logKeyIndex, adjust is null ? decl : adjust(decl));
    }

    private static Case CompromisedBeforePost(string root, Key k1)
    {
        var (log, post, logKeyIndex, decl) = PostThenDeclaration(k1, "40");
        AddDeclaration(log, decl, root, "compromised-before-post");
        return new Case("compromised-before-post", "R6.57",
            "The post at 2 is signed by the enrolled key; the operator's declaration at 4 names that key from log index 1, its binding, so the key was declared compromised from before the post. The signature holds and the content is disputed.",
            log, post, [0, 1, 4], "disputed", "disputed")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    /// <summary>
    /// 0 enrolled, 1 binding k1, 2 the post, 3 the rotation k1 to k2 (so k2 is in the cascade), 4 the log key, 5 the declaration.
    /// The disputed variant and the omits-cascade variant use from_index 2, at or below the post (kids[0] is k1, bound at 1). The
    /// reordered variant puts k2 first, bound at 3, so it uses from_index 3 (binding 3 &lt;= 3 &lt; 5; the cascade from 3 is still
    /// [k1, k2], R6.57's membership being at or after from_index): the one defect of both failed variants is in <c>kids</c>.
    /// </summary>
    private static Case CompromisedAfterInterveningRotation(
        string root, Key k1, Key k2, string name, bool reorderKids, bool omitCascade)
    {
        var log = EnrolledAndBound(k1);
        var post = AddPost(log, k1, "41");
        AddRotation(log, new Rot(k1, k2, Prev: 1));
        var logKeyIndex = PublishLogKey(log, ConformanceLogKeys.Log);
        var kids = reorderKids ? new[] { k2.Kid, k1.Kid } : omitCascade ? [k1.Kid] : new[] { k1.Kid, k2.Kid };
        var fromIndex = reorderKids ? 3 : 2;
        AddDeclaration(log, new Decl(ConformanceLogKeys.Log, kids, FromIndex: fromIndex, Prev: 3), root, name);
        var (verdict, predicate, note) = name switch
        {
            "compromise-kids-omits-cascade" => ("failed", "declaration-mismatch",
                "The declaration names the compromised key alone, though the rotation at 3 was authorized by it after from_index: the recomputed cascade is [k1, k2] and kids omits k2 (KF3)."),
            "compromise-kids-reordered" => ("failed", "declaration-mismatch",
                "The correct cascade with its first two kids swapped: kids is [k2, k1] where the compromised key, the post's k1, comes first. from_index is 3, so binding(kids[0]) = binding(k2) = 3 holds the bound, 3 is below the declaration at 5, and the cascade from 3 is still [k1, k2]: the only defect is the order (KF3)."),
            _ => ("disputed", "disputed",
                "The post at 2, a rotation at 3 authorized by the post's key, and the declaration at 5 with from_index 2 at or below the post: the rotation lies between the post and the declaration, and the post is still disputed. The cascade holds two kids."),
        };
        return new Case(name, "R6.57", note, log, post, [0, 1, 3, 5], verdict, predicate)
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    private static Case DeclarationSignedAsHead(string root, Key k1)
    {
        var (log, post, logKeyIndex, decl) = PostThenDeclaration(k1, "42", d => d with { HeaderTyp = DetachedJws.HeadTyp });
        AddDeclaration(log, decl, root, "declaration-signed-as-head");
        return new Case("declaration-signed-as-head", "R6.57",
            "A valid log-key signature over the declaration whose header typ is curia-head+jws, a head's, not curia-key-compromise+jws (KF1).",
            log, post, [0, 1, 4], "failed", "lifecycle-typ-mismatch")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    private static Case FromIndexAfterDeclaration(string root, Key k1)
    {
        var (log, post, logKeyIndex, decl) = PostThenDeclaration(k1, "43", d => d with { FromIndex = 5 });
        AddDeclaration(log, decl, root, "compromise-from-index-after-declaration");
        return new Case("compromise-from-index-after-declaration", "R6.57",
            "from_index is 5 and the declaration is at 4: from_index is not below the declaration's index (KF3).",
            log, post, [0, 1, 4], "failed", "declaration-mismatch")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    private static Case FromIndexAtDeclaration(string root, Key k1)
    {
        var (log, post, logKeyIndex, decl) = PostThenDeclaration(k1, "50", d => d with { FromIndex = 4 });
        AddDeclaration(log, decl, root, "compromise-from-index-at-declaration");
        return new Case("compromise-from-index-at-declaration", "R6.57",
            "from_index is 4 and the declaration is at 4: from_index is not strictly below the declaration's index (KF3).",
            log, post, [0, 1, 4], "failed", "declaration-mismatch")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    /// <summary>
    /// 0 enrolled, 1 binding k1, 2 the stranger's binding (another identity's stream), 3 the post, 4 the log key, 5 the declaration
    /// naming the stranger's key, from_index 2: binding(kids[0]) = 2 &lt;= 2 &lt; 5, so the bound-to-agent_id check is the only one
    /// that can refuse it. The recomputed cascade is [k1], so kids cannot equal it either; that is unavoidable, which is why the
    /// predicate is the distinct <c>kid-not-bound-to-agent</c> and a reader checks it before the cascade.
    /// </summary>
    private static Case KidOfOtherIdentity(string root, Key k1, Key stranger)
    {
        var log = EnrolledAndBound(k1);
        log.Add("agent.key-bound", OtherAgent, OtherAgent, Obj(
            ("agent_id", Str(OtherAgent)), ("jwk", stranger.Jwk), ("kid", Str(stranger.Kid))));
        var post = AddPost(log, k1, "44");
        var logKeyIndex = PublishLogKey(log, ConformanceLogKeys.Log);
        AddDeclaration(log, new Decl(ConformanceLogKeys.Log, [stranger.Kid], FromIndex: 2, Prev: 1), root, "compromise-kid-of-other-identity");
        return new Case("compromise-kid-of-other-identity", "R6.57",
            "The declaration for this identity names a key that another identity's binding at 2 holds. from_index 2 satisfies the binding bound (binding 2 <= 2 < 5), and the only defect is that kids[0] is bound to another identity at 2, not to agent_id in its own stream (KF3).",
            log, post, [0, 1, 2, 5], "failed", "kid-not-bound-to-agent")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    private static Case UnknownReason(string root, Key k1)
    {
        var (log, post, logKeyIndex, decl) = PostThenDeclaration(k1, "45", d => d with { Reason = "key-lost" });
        AddDeclaration(log, decl, root, "compromise-unknown-reason");
        return new Case("compromise-unknown-reason", "R6.57",
            "The declaration's reason is key-lost, outside the closed set: a reader that does not know it cannot check the declaration (KF4).",
            log, post, [0, 1, 4], "could-not-check", "unknown-reason")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    /// <summary>0 enrolled, 1 binding, 2 the post, 3 the declaration, 4 the log key that signed it: published after.</summary>
    private static Case DeclarationKidNotValidAtPosition(string root, Key k1)
    {
        var log = EnrolledAndBound(k1);
        var post = AddPost(log, k1, "46");
        AddDeclaration(log, new Decl(ConformanceLogKeys.Log, [k1.Kid], FromIndex: 1, Prev: 1), root, "compromise-declaration-kid-not-valid-at-position");
        var logKeyIndex = PublishLogKey(log, ConformanceLogKeys.Log);
        return new Case("compromise-declaration-kid-not-valid-at-position", "R6.57",
            "The declaration at 3 verifies under a log key whose log.key entry is at 4, after it: the key was not published before the declaration it signs (KF2). This case is generated by the tool directly; the use case refuses it.",
            log, post, [0, 1, 3], "failed", "declaration-key-unpublished")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    private static Case DeclarationKidPositionUnknown(string root, Key k1)
    {
        var (log, post, _, decl) = PostThenDeclaration(k1, "47");
        AddDeclaration(log, decl, root, "compromise-declaration-kid-position-unknown");
        return new Case("compromise-declaration-kid-position-unknown", "R6.57",
            "The log key set lists the declaring key with no log_index: a reader cannot say it was published before the declaration (KF2, KF4's rule).",
            log, post, [0, 1, 4], "could-not-check", "log-key-position-unknown")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, LogIndex: null)] };
    }

    private static Case V2UnderUnknownSigner(string root, Key k1)
    {
        var log = EnrolledAndBound(k1);
        var post = AddPost(log, k1, "48");
        var logKeyIndex = PublishLogKey(log, ConformanceLogKeys.Log);
        AddDeclaration(
            log,
            new Decl(ConformanceLogKeys.Unpublished, [k1.Kid], FromIndex: 1, Prev: 1) { StatementTyp = "curia.key-compromise/2" },
            root,
            "compromise-v2-under-unknown-signer");
        return new Case("compromise-v2-under-unknown-signer", "R6.57",
            "A curia.key-compromise/2 statement under header typ curia-key-compromise+jws, signed by a P-256 key the log key set does not list: a /1 reader reads the statement typ before the signer, and cannot check it (KF4's order).",
            log, post, [0, 1, 4], "could-not-check", "unknown-lifecycle-kind")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    private static Case FromIndexBeforeBinding(string root, Key k1)
    {
        var (log, post, logKeyIndex, decl) = PostThenDeclaration(k1, "49", d => d with { FromIndex = 0 });
        AddDeclaration(log, decl, root, "compromise-from-index-before-binding");
        return new Case("compromise-from-index-before-binding", "R6.57",
            "from_index is 0 and the first kid's binding is at 1: one below the log index of kids[0]'s binding (KF3).",
            log, post, [0, 1, 4], "failed", "declaration-mismatch")
        { ExtraLogKeys = [new ExtraLogKey(ConformanceLogKeys.Log, LogKeyValidFrom, logKeyIndex)] };
    }

    // ---- entries -------------------------------------------------------------------------------

    /// <summary>A <c>log.key</c> entry, as the operator's tool publishes a log key (R6.50): <c>{ kid, jwk, valid_from }</c>.</summary>
    private static int PublishLogKey(Log log, P256LogKey key) => log.Add("log.key", "log:keys", OperatorRole, Obj(
        ("jwk", key.Jwk), ("kid", Str(key.Kid)), ("valid_from", Str(LogKeyValidFrom))));

    internal static CanonicalBytes DeclarationStatement(string agent, string[] kids, long fromIndex, long prev, string reason, string typ) =>
        Canon(Obj(
            ("agent_id", Str(agent)),
            ("from_index", Num(fromIndex)),
            ("kids", new JsonValue.Array([.. kids.Select(k => Str(k))])),
            ("prev", Num(prev)),
            ("reason", Str(reason)),
            ("typ", Str(typ))));

    private static int AddDeclaration(Log log, Decl d, string root, string caseName)
    {
        var canonical = DeclarationStatement(Agent, d.Kids, d.FromIndex, d.Prev, d.Reason, d.StatementTyp);
        var text = Encoding.UTF8.GetString(canonical.Span);
        var declaration = PinnedOrSigned(
            Path.Combine(root, caseName), Path.Combine("entries"), canonical, text, d.Signer, d.HeaderTyp);
        return log.Add("agent.key-compromised", Agent, OperatorRole, Obj(
            ("agent_id", Str(Agent)),
            ("declaration", Str(declaration)),
            ("from_index", Num(d.FromIndex)),
            ("kids", new JsonValue.Array([.. d.Kids.Select(k => Str(k))])),
            ("prev", Num(d.Prev)),
            ("reason", Str(d.Reason)),
            ("statement", Str(text))));
    }

    /// <summary>
    /// The declaration already pinned in <paramref name="existingDir"/> for exactly this statement, re-verified under the
    /// signer's key and kept; a fresh signature only when the directory does not exist. A directory that exists and holds no
    /// declaration for the statement is refused: ES256 signing is randomized, so re-signing would move bytes a vector pins.
    /// </summary>
    internal static string PinnedOrSigned(
        string existingDir, string entriesSubdirectory, CanonicalBytes canonical, string statementText, P256LogKey signer, string headerTyp)
    {
        var pinned = FindPinned(existingDir, entriesSubdirectory, statementText);
        if (pinned is not null)
        {
            if (!Signing.Verify(canonical, new JwsSignature(pinned), "ES256", signer.Kid, signer.Spki, headerTyp).IsOk)
                throw new InvalidOperationException($"{existingDir}: the pinned declaration does not verify under its log key and typ; it is kept, never re-signed");
            return pinned;
        }

        if (Directory.Exists(existingDir))
        {
            throw new InvalidOperationException(
                $"{existingDir} exists but pins no declaration for this statement; the generator never re-signs. Delete the case deliberately to regenerate it.");
        }

        return Signing.Sign(canonical, "ES256", signer.Kid, signer.EcPrivateKeyDer, headerTyp).Compact;
    }

    private static string? FindPinned(string dir, string entriesSubdirectory, string statementText)
    {
        var searchDir = Directory.Exists(Path.Combine(dir, entriesSubdirectory)) ? Path.Combine(dir, entriesSubdirectory) : dir;
        if (!Directory.Exists(searchDir))
            return null;

        foreach (var file in Directory.EnumerateFiles(searchDir, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = document.RootElement;
            var entry = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entry", out var inner) ? inner : root;
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("event_type", out var type) || type.GetString() != "agent.key-compromised"
                || !entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("statement", out var statement) || statement.GetString() != statementText
                || !payload.TryGetProperty("declaration", out var declaration))
            {
                continue;
            }

            return declaration.GetString();
        }

        return null;
    }

    /// <summary>The key-set members after the head key's, as JSON text starting with a comma per key.</summary>
    private static string ExtraLogKeysJson(Case c)
    {
        var builder = new StringBuilder();
        foreach (var extra in c.ExtraLogKeys)
        {
            var members = new List<(string, JsonValue)>
            {
                ("alg", Str("ES256")),
                ("crv", Str("P-256")),
                ("kid", Str(extra.Key.Kid)),
                ("kty", Str("EC")),
                ("use", Str("sig")),
                ("valid_from", Str(extra.ValidFrom)),
                ("x", Str(Jwk.Base64UrlEncode(extra.Key.X32))),
                ("y", Str(Jwk.Base64UrlEncode(extra.Key.Y32))),
            };
            if (extra.LogIndex is { } index)
                members.Add(("log_index", Num(index)));
            builder.Append(',').Append(Encoding.UTF8.GetString(Canon(Obj([.. members])).Span));
        }

        return builder.ToString();
    }
}
