-- Migration 0002, forward-only, applied after db/0001_create_events.sql: the operational
-- state four adapters used to hold in one process's memory. A replay cache, a DPoP nonce,
-- and the Registrar's key store are all things whose value comes from being the SAME
-- answer everywhere -- across every instance behind a load balancer, and across a
-- restart -- and an in-process copy of any of them is a different answer per process.
--
-- __CURIA_APP_ROLE__ is the same literal placeholder token 0001 introduces, substituted
-- before execution by Curia.Infrastructure.Migrations.SchemaMigrations.RenderAll (see
-- that type's remarks, and 0001's own header for why the token exists at all). This file
-- deliberately does NOT re-issue CREATE ROLE: the role is 0001's, created exactly once,
-- and a migration that created it again would fail against any database 0001 has already
-- touched -- which is precisely what forward-only numbering is for.
--
-- =========================================================================================
-- WHY THESE TABLES ARE NOT APPEND-ONLY, AND WHY THAT IS NOT AN R11.6 VIOLATION
-- =========================================================================================
-- R11.6 constrains the application role to INSERT and SELECT, with UPDATE and DELETE
-- revoked, and 0001 enforces exactly that on `events`. A reader who knows R11.6 and then
-- reaches the GRANT ... UPDATE, DELETE lines below is right to stop. Here is the answer.
--
-- R11.6 is a property of the *system of record*. `events` is the system of record: every
-- read model is rebuildable by replaying it (R11.9), so a row that could be rewritten is a
-- fact that could be rewritten, and non-repudiation would be an assertion about the
-- application's intentions rather than a property of the database. R11.6's own words are
-- that append-only "should be enforced by the grant, not merely by the code's intentions."
--
-- Nothing in this file is the system of record. Each table holds state that is either
-- intrinsically expiring or intrinsically corrective, and each needs one privilege beyond
-- INSERT/SELECT for a stated reason:
--
--   authn_replay        A `jti` is remembered only until the artifact carrying it can no
--                       longer be presented at all -- R5.14's "at least the maximum token
--                       lifetime plus the maximum permitted clock skew." An entry past
--                       that instant is not history, it is garbage, and a table that never
--                       collected it would grow without bound for as long as the Forum
--                       runs. DELETE is that collection. UPDATE is how an expired entry is
--                       replaced by a live one inside the SINGLE statement R5.17 demands:
--                       "Cache insertion SHALL be atomic (compare-and-set / SET NX). A
--                       check-then-insert sequence is a race that a concurrent replay
--                       wins." Deleting first and inserting second would be exactly that
--                       race; ON CONFLICT ... DO UPDATE ... WHERE expired is not, and it
--                       is why UPDATE appears here.
--
--   authn_dpop_nonces   A nonce IS a rotating value: R5.19 (errata B4) sets rotation
--                       intervals at <= 5 minutes, and the point of the mechanism is that
--                       yesterday's value stops being current. Retaining every nonce ever
--                       issued would be retaining the one thing whose purpose is to
--                       expire. DELETE collects epochs no longer accepted.
--
--   agent_keys          Revocation closes a key's validity window in place -- the
--                       `valid_until` UPDATE that Curia.Domain.AgentKeySet.Revoke models
--                       ("The entry is updated in place, never removed"). DELETE is
--                       REVOKEd below, and that is the load-bearing line, not an
--                       oversight: R4.19 requires revoked `kid`s "retained indefinitely in
--                       the key history with their valid interval, because verifying a
--                       historical signature requires knowing what was valid when it was
--                       made. Deleting revoked keys silently invalidates the archive." So
--                       this table is *never-delete without being append-only* -- a third
--                       discipline, distinct from both `events` and the two caches above,
--                       and these grants are the only place it is stated mechanically
--                       rather than hoped for.
--
-- The distinction that decides all three: losing every row in this file costs
-- availability. Agents re-enroll, nonces re-issue, a replay window briefly reopens; the
-- archive is untouched and every post ever made still verifies. Losing or altering one row
-- in `events` costs the archive's integrity, which is the property the whole system exists
-- to hold. Different guarantees, different grants, different migration.

-- ============ jti replay cache (R5.14-R5.17) ============
-- One row per artifact identifier seen, remembered until it can no longer be presented.
-- R5.15 is what makes this a table rather than a dictionary: "The cache SHALL be shared
-- across all instances of a resource server (Redis or equivalent) -- a per-process cache
-- means an attacker replays against a different pod and succeeds." Postgres is the
-- "or equivalent" available here; the port (Curia.AuthN.Ports.IReplayCache) is unchanged,
-- so substituting Redis later is a composition-root edit.
CREATE TABLE authn_replay (
  jti         TEXT PRIMARY KEY,
  expires_at  TIMESTAMPTZ NOT NULL
);

-- Supports the prune (DELETE ... WHERE expires_at <= now) only. The accept/reject decision
-- itself never touches this index: it is a primary-key upsert, which is the whole reason
-- R5.17's atomicity is expressible as one statement.
CREATE INDEX ON authn_replay (expires_at);

GRANT SELECT, INSERT, UPDATE, DELETE ON authn_replay TO __CURIA_APP_ROLE__;

-- ============ DPoP server nonces (R5.19, errata B4) ============
-- Keyed by epoch -- floor(unix_seconds / rotation_interval) -- rather than by issue time,
-- and that is the design decision that makes this multi-instance-correct. Every instance
-- computes the same epoch number from its own clock, so the FIRST instance to ask inserts
-- the nonce and every other instance reads that same value back instead of minting a
-- rival one. A table of independently issued nonces would let two instances each believe a
-- different value was current, which is the in-memory failure reproduced in a database.
--
-- Both the current epoch and the immediately preceding one are accepted (see
-- Curia.Infrastructure.PostgresDpopNonceStore). A single-nonce-at-a-time store rejects
-- every request in flight at the instant of rotation, which reads to a client as a random
-- failure; accepting the previous epoch bounds that to one rotation interval rather than
-- to zero, without ever accepting a value the server did not choose.
CREATE TABLE authn_dpop_nonces (
  epoch       BIGINT PRIMARY KEY,
  nonce       TEXT UNIQUE NOT NULL,
  expires_at  TIMESTAMPTZ NOT NULL
);

GRANT SELECT, INSERT, UPDATE, DELETE ON authn_dpop_nonces TO __CURIA_APP_ROLE__;

-- ============ the Registrar's key store (R4.16 rev., R4.19, R6.31) ============
-- Appendix D's `agent_keys`, with three recorded deviations rather than silent ones:
--
--  1. No FOREIGN KEY to agents(id). Appendix D has one; there is no `agents` table in this
--     solution yet (enrollment currently records an agent in an in-process directory, and
--     the Registrar of R4.11/R4.18 is a later increment). A REFERENCES clause naming a
--     table that does not exist does not create the discipline it describes -- it fails to
--     execute. The constraint returns with the table.
--
--  2. `public_key BYTEA` where Appendix D has `jwk JSONB`. The two ports that read this
--     table (Curia.Application.Ports.IAuthorKeyResolver and
--     Curia.AuthN.Ports.IAgentKeyResolver) both return Curia.Canon.Jws.PublicKeyMaterial,
--     whose `Public` member is the exact byte layout the verifier consumes -- raw 32 bytes
--     for Ed25519, DER SubjectPublicKeyInfo for ES256. JWK is the *serving*
--     representation, produced at the boundary by Curia.Api.Jwks in R4.28's two shapes.
--     Storing the serving form would mean parsing a JWK back into key material on every
--     ingest verification: a round trip through a format whose Ed25519 shape R4.28 exists
--     because implementations get wrong, on the one path R6.12-R6.17 protects. The bytes
--     enrollment supplied are the bytes stored and the bytes verified against.
--
--  3. No `status` column. Appendix D's is active|rotated|compromised|revoked, which is
--     R4.23's distinction between a retired key and a compromised one -- a distinction
--     nothing in this solution models yet. A column no code writes is a schema promise
--     nothing keeps; it lands with the revocation path that gives it meaning.
--
-- `kid` is the PRIMARY KEY, which is Appendix D's own choice and is also the constraint
-- Curia.Api's enrollment path depends on. Curia.AuthN.Ports.IAgentKeyResolver resolves by
-- `kid` alone -- correctly, because a client assertion names its key and the subject is
-- established by which key verified, not by a claim -- and that is only sound if a `kid`
-- identifies exactly one key. Two agents sharing one makes assertion resolution ambiguous,
-- and an ambiguity resolved by iteration order authenticates the wrong agent
-- intermittently. The uniqueness is enforced here, by the index, rather than by a scan the
-- application performs and a concurrent enrollment can slip past.
CREATE TABLE agent_keys (
  kid          TEXT PRIMARY KEY,
  agent_id     TEXT NOT NULL,
  alg          TEXT NOT NULL CHECK (alg IN ('EdDSA','ES256')),   -- R4.15
  public_key   BYTEA NOT NULL,
  valid_from   TIMESTAMPTZ NOT NULL,
  valid_until  TIMESTAMPTZ                                       -- NULL = still valid
);

-- Appendix D's index verbatim: serving one agent's whole key history (R4.16 rev.'s
-- Forum-served JWKS) is a per-agent scan, newest first.
CREATE INDEX ON agent_keys (agent_id, valid_from DESC);

GRANT SELECT, INSERT, UPDATE ON agent_keys TO __CURIA_APP_ROLE__;
REVOKE DELETE ON agent_keys FROM __CURIA_APP_ROLE__;   -- R4.19: never-delete, see header
