# Increment 3 — the append-only event store

Phase 1 is not finished. The roadmap (§15, closing) is explicit about what "completely"
means: *"signed envelopes, canonicalization, the event store, enrollment, bound tokens —
and verify it with a second, independent verifier before adding anything else."*

Done: signed envelopes, canonicalization, and the independent verifier (`curia-testis`,
48/48 vectors, authorship confirmed offline). Remaining: **the event store**, enrollment,
bound tokens. The event store comes first because the other two write to it.

## What makes this increment hard

Not the SQL. The event table is the system of record, and three of the project's
load-bearing invariants are about *how it may be written*:

- **Append-only is enforced by the grant, not by intent** (R11.6). `REVOKE UPDATE, DELETE`
  on the app role. Code that intends to be append-only and a role that cannot update are
  different guarantees, and only one survives a mistake.
- **Every read model is rebuildable by replay, exercised in CI** (R11.9). Not asserted —
  run. A projection that cannot be rebuilt is a projection that has become a second system
  of record.
- **The write surface is one adapter** (`CS-15`). `Persist` is the only thing that can
  append; the phase-typed ingest chain makes an unverified write fail to compile.

## Stage 1 — Domain and Application, no database

**Goal**: the event model and its ports, with an in-memory adapter, so the whole thing is
testable with no I/O.
**Success**: `Curia.Domain` references nothing outside the BCL and `Curia.Canon`
(R11.1/R11.2); time enters only through a `Clock` port (R11.3, `CS-9`); every port has an
in-memory adapter (R11.4). `Result<T>` for domain fallibility, not exceptions (`CS-10`).
**Tests**: append-then-read round trip; monotonic `seq`; optimistic concurrency on
`aggregate_id`; the in-memory adapter passes the same suite the Postgres one will.
**Status**: **Complete** (commit `vrw`)

## Stage 2 — Infrastructure: Npgsql, and the grant that does the enforcing

**Goal**: the real event store against Postgres, plus `db/` migrations carrying Appendix
D's `events` DDL and its `REVOKE UPDATE, DELETE ON events FROM app_role`.
**Success**: the same port suite from Stage 1 passes against a real database via
Testcontainers; a test connecting *as `app_role`* proves `UPDATE` and `DELETE` are refused
by the server, not by the code.
**Tests**: Testcontainers `pgvector/pgvector`; append batching returns `seq`; the
grant-refusal test is the one that matters.
**Status**: Not Started

## Stage 3 — Replay rebuild, exercised

**Goal**: one projection, and a rebuild-from-zero that reproduces it byte-for-byte.
**Success**: R11.9 satisfied by a test that drops the projection, replays the event table,
and compares — in CI, every run.
**Tests**: rebuild equivalence over a generated event history; replay is deterministic
across two runs.
**Status**: Not Started

## Stage 4 — Architecture tests

**Goal**: make the dependency direction a failing build rather than a convention.
**Success**: `NetArchTest` rules for `CS-6`/`CS-7` (Domain → Canon only; Infrastructure →
Application, never the reverse) and `CS-15` (only `Persist`'s adapter reaches the event
store's write surface). Nothing outside Infrastructure references `Npgsql`.
**Tests**: extend `Curia.Architecture.Tests`.
**Status**: **Complete** — 6 rules to 12; every rule falsified and reverted to prove it can fail

## Environment constraint — Stages 2 and 3 cannot be verified here

**Docker is unavailable in this environment**, so Testcontainers cannot start a Postgres
instance. Stage 2's grant-refusal test and Stage 3's replay-rebuild drill are precisely the
tests that must run against a *real* server — a mocked Postgres would assert that our mock
refuses `UPDATE`, which is worth nothing.

So Stages 1 and 4 are built and verified here; Stages 2 and 3 are written to run in CI and
are marked unverified until they do. Shipping them as "done" on the strength of code that
compiles would misrepresent exactly the guarantee R11.6 and R11.9 exist to provide.

---

# Increment 4 — enrollment and bound tokens

Phase 1's remaining two pieces. Both are substantial *domain* logic and need no database,
so they are fully verifiable in this environment while the event store's Stages 2–3 wait
for Docker. They persist through the in-memory adapter Stage 1 built for exactly this.

## Stage A — the credential lifecycle

**Goal**: Table 6 as a transition table (`CS-12`), with every transition an append-only
event carrying actor, reason and timestamp (whitepaper R4.21 — *not* the errata's
renumbered R4.28).
**Success**: the table *is* Table 6, reviewable cell by cell; an illegal transition is a
`Result` failure; current state is a projection, never a stored field.
**Watch for**: errata **D9.5** — Table 6's `active` row omits `quarantined` as an exit
though `quarantined`'s own row implies it. Decide and record, do not silently pick.
**Status**: **Complete** — 90 tests. Also found a gap the errata did not record: Table 6 names
`expired` as an exit target and defines no row for it. Now errata **E8 / R4.29**.

## Stage B — the agent key store

**Goal**: keys as R4.15 requires (`EdDSA` / `ES256` only), in the JWK shapes **R4.28**
specifies (RFC 8037 `OKP` for Ed25519; RFC 7518 `EC` for P-256), with at least two
simultaneously valid keys (R4.17) so rotation is not an outage.
**Success**: **key validity is evaluated at `server_ts`** — errata **A12**/R6.31, not at
submission time and not at `created_at`. This is the single easiest thing to get subtly
wrong here.
**Watch for**: errata **A16**/R4.16 rev. — the Registrar's key store is authoritative and
the Forum serves JWKS. **No runtime fetch of agent-hosted JWKS**, ever; it is an SSRF and
availability surface. A port that could fetch is already the bug.
**Status**: **Complete** — 42 tests. `ServerTimestamp` is a distinct type with no implicit
conversion, so `created_at` cannot be passed where `server_ts` is required. No fetch-shaped
surface exists anywhere in the key path.

## Stage C — token and DPoP validation

**Goal**: §5.5's algorithm, in its stated order, because several classic vulnerabilities
are ordering bugs.
**Success**: `alg` pinned *before* any signature work (R5.9 — never read `alg` to choose
the routine); `kid` resolved only within the configured issuer JWKS (R5.10 — never fetch a
key named inside the token); a `jti` replay cache with atomic compare-and-set (R5.14,
R5.17); skew ≤ 30s (R5.16).
**Watch for**: errata **A17** — §5.5's published algorithm **omits two checks that must
exist**: the DPoP proof's `typ: "dpop+jwt"`, and `nbf`. An implementation that follows the
printed algorithm exactly is wrong.
**Status**: **Complete** — 64 tests. Both A17 checks present and positioned; verified by
mutation — moving either check makes its test fail, returning the *other* check's error.

## Carried from Increment 4 — two design decisions, deliberately not made unilaterally

**Two incompatible `ServerTimestamp` concepts now coexist in `Curia.Domain`.**
`AppendedEvent.ServerTimestamp` (Increment 3) is a plain `DateTimeOffset` — the store-assigned
`server_ts` of Appendix D's column. `Curia.Domain.ServerTimestamp` (Stage B) is a distinct
wrapper type built so a bare `DateTimeOffset` can never masquerade as the instant governing
key validity. Same namespace, near-identical name, no conversion between them. Nothing wires
them together today, which is why it is a seam rather than a bug — but the first integration
will hit it, and retyping `AppendedEvent.ServerTimestamp` ripples into Increment 3's tests.

**`IJwsKeyResolver` has no channel for `server_ts`, and this is the more serious one.**
Both validators call `ResolveAsync(kid, ct)` in Phase 1, *before* the clock is read in
Phase 3. The client-assertion path is exactly where a future adapter must consult Stage B's
`AgentKeySet.ValidateAt(kid, ServerTimestamp)` — the A12/R6.31 check — but the port carries no
timestamp, so that adapter would read its own clock and reintroduce at the port boundary the
precise ambiguity `ServerTimestamp` was built to remove at the type level. Fixing it changes a
public port signature both validators depend on. Options: pass the instant through the port,
or make key-validity a distinct step after resolution rather than inside it.

## Carried from Increment 2, not blocking

- Two differential divergence classes remain: `raw-control-character` vs `malformed-json`
  (unspecified vocabulary — needs a one-slug R6.40 extension) and an unpaired-surrogate
  class that is **our own node oracle's bug**, not either implementation's.
- Errata **A14**: votes must be signed envelopes and Appendix D's `votes` table does not
  support it. Out of scope here — votes are Phase 3 — but the `events` schema should not
  foreclose it.
