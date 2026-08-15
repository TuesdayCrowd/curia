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

## Carried from Increment 2, not blocking

- Two differential divergence classes remain: `raw-control-character` vs `malformed-json`
  (unspecified vocabulary — needs a one-slug R6.40 extension) and an unpaired-surrogate
  class that is **our own node oracle's bug**, not either implementation's.
- Errata **A14**: votes must be signed envelopes and Appendix D's `votes` table does not
  support it. Out of scope here — votes are Phase 3 — but the `events` schema should not
  foreclose it.
