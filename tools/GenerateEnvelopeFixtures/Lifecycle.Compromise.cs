using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Jws;
using JsonValue = Curia.Canon.Json.JsonValue;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// <c>acta/key-compromised-entry</c> (keys that end, Task 2.0, spec 4.6): the operator's declaration over
/// <c>conformance-ed25519-second</c>. Written by <c>--compromise</c>, first run in Task 2.3. The declaration is signed ES256 by the
/// fixed P-256 conformance log key under <c>curia-key-compromise+jws</c> with the log key's kid (KF1, KF2), never under
/// R6.49's <c>curia-head+jws</c>. Its signature bytes are pinned on first generation and kept afterwards.
/// </summary>
internal static partial class Lifecycle
{
    public static int RunCompromiseVector(string repoRoot)
    {
        var root = Path.Combine(repoRoot, "conformance", "acta");
        var log = ConformanceLogKeys.Log;

        // The compromised key alone: no rotation at an index >= from_index (2) is authorized by SecondKid (R6.57's cascade).
        string[] kids = [SecondKid];
        var canonical = LifecycleCases.DeclarationStatement(Agent, kids, fromIndex: 2, prev: 3, "owner-reported", "curia.key-compromise/1");
        var text = Encoding.UTF8.GetString(canonical.Span);
        var declaration = LifecycleCases.PinnedOrSigned(
            Path.Combine(root, "key-compromised-entry"), "entries", canonical, text, log, "curia-key-compromise+jws");

        var entry = JsonBuilders.Obj(
            ("actor_id", JsonBuilders.Str("operator")),
            ("aggregate_id", JsonBuilders.Str(Agent)),
            ("event_id", JsonBuilders.Str("01K4CQ1V9P3R5T7W9Y1A3C5EBM")),
            ("event_type", JsonBuilders.Str("agent.key-compromised")),
            ("payload", JsonBuilders.Obj(
                ("agent_id", JsonBuilders.Str(Agent)),
                ("declaration", JsonBuilders.Str(declaration)),
                ("from_index", JsonBuilders.Num(2)),
                ("kids", new JsonValue.Array([.. kids.Select(k => JsonBuilders.Str(k))])),
                ("prev", JsonBuilders.Num(3)),
                ("reason", JsonBuilders.Str("owner-reported")),
                ("statement", JsonBuilders.Str(text)))),
            ("server_ts", JsonBuilders.Str("2026-09-04T16:30:00.000000Z")));

        Write(root, "key-compromised-entry", entry, "R6.57",
            "An agent.key-compromised event (R6.57, spec 4.6): the operator declares conformance-ed25519-second compromised from log index 2, "
            + "the rotation that bound it. The implied log: index 0 agent.enrolled, 1 the enrolment's agent.key-bound of conformance-ed25519-minimal, "
            + "2 the rotation binding conformance-ed25519-second under conformance-ed25519-minimal's authorization (key-rotated-entry), "
            + "3 the retirement of conformance-ed25519-minimal (key-retired-entry), 4 the log.key entry publishing the conformance log key "
            + "(a fixed P-256 test key; its kid is the RFC 7638 thumbprint of its public key), 5 this declaration. prev is 3: log.key is not in the "
            + "agent's stream, so the statement is the same bytes with or without entry 4. R6.57's cascade over that log from from_index 2: "
            + "no rotation at an index >= 2 is authorized by conformance-ed25519-second, so kids is that key alone; its binding (2) <= from_index (2) "
            + "< the declaration's index (5), and the key is live at 5. The actor is a role name. The declaration is detached under header typ "
            + "curia-key-compromise+jws with the log key's kid, ES256; ES256 signing is randomized, so its signature bytes were generated once "
            + "and are pinned, and the generator re-verifies and keeps them. A proposal: it may be regenerated until the owner's lift record pins its digests. "
            + "Expected values were computed with python3's json and hashlib, outside both implementations.",
            extra: new JsonObject { ["log_key_kid"] = log.Kid });
        return 0;
    }
}
