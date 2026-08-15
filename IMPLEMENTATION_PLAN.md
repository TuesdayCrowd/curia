# Remediation plan — closing out Phase 1

Everything currently builds and passes: **411 C# tests, 0 warnings**; the Rust verifier green
with the corpus at 48/48. Nothing here is a firefight. These are the known-open items,
ordered by *risk* rather than by how easy they are.

Three of them are live correctness seams, two are blocked on the environment, and the
largest is documentation debt that blocks nothing but grows quietly.

---

## Tier 1 — correctness seams, unblocked, do first

### T1.1 — `IJwsKeyResolver` carries no `server_ts` *(the most serious open item)*

**The problem.** Both validators call `ResolveAsync(kid, ct)` in Phase 1, *before* the clock
is read in Phase 3. The client-assertion path is exactly where a future adapter must consult
`AgentKeySet.ValidateAt(kid, ServerTimestamp)` — the A12/R6.31 check — but the port carries
no timestamp. That adapter would read its own clock, reintroducing **at the port boundary**
the precise ambiguity `ServerTimestamp` was built to eliminate at the type level.

Nothing is broken today because nothing wires the two together. It breaks the first time
someone does, and it will look correct while doing so.

**Two candidate shapes, and this is a real decision:**

1. *Thread the instant through the port* — `ResolveAsync(kid, ServerTimestamp, ct)`. The
   resolver returns only keys valid at that instant. Simple; makes the wrong call
   unwriteable. Costs: every resolver implementation must care about validity, including
   ones for which it is meaningless (the issuer's own JWKS has no per-key validity window).
2. *Separate resolution from validity* — the resolver returns key material; a distinct step
   checks `ValidateAt`. Keeps the port honest about what it does. Costs: the check becomes
   omissible, which is what A12 is about.

**Recommendation: (1), narrowed** — thread the instant, but only on the *agent-key* resolver,
leaving the issuer-JWKS resolver alone. They are different ports pretending to be one, and
the fact that only one of them has a validity notion is the evidence.

**Success**: a test where a key valid at signing and invalid at `server_ts` is rejected
*through the validator*, not merely through `AgentKeySet` directly.
**Status**: **Complete** — port split as recommended. Verified by mutation: feeding the
validator a fixed wrong instant makes exactly one test fail, so the test proves the *path*,
not just the store.

### T1.2 — two incompatible `ServerTimestamp` concepts

**The problem.** `AppendedEvent.ServerTimestamp` (Increment 3) is a plain `DateTimeOffset`;
`Curia.Domain.ServerTimestamp` (Increment 4) is a wrapper type. Same namespace, near-identical
name, no conversion. A reader cannot tell which one a given `server_ts` is.

**Recommendation**: one type, the wrapper, used by both. `AppendedEvent.ServerTimestamp`
becomes `ServerTimestamp`. This ripples into Increment 3's Application-layer tests — that
ripple is the cost of having introduced the second concept, and it is smaller now than later.

**Success**: exactly one type in the solution answers "the Forum's authoritative instant";
architecture test pins that no plain `DateTimeOffset` names itself `server_ts`.
**Status**: **Complete** — `ServerTimestamp` moved to `Curia.Domain.Primitives`, the only
project both `Curia.Domain` and `Curia.AuthN` can see. Ripple followed through with no
compatibility overload left behind. Rule falsified in two assemblies and two member kinds.

### T1.3 — resolve the `Ulid` contradiction *(blocks T3)*

**The problem.** `curia-csharp-scoping.md` §9's package table lists `Ulid | Domain | R8.3`,
planning a third-party package as a `Curia.Domain` dependency, while **R11.1** says Domain
depends on nothing outside the BCL. Two authoritative documents disagree, and the
architecture tests currently enforce R11.1 — so the scoping document's plan would fail the
build.

Increment 3 sidestepped it by leaving IDs as validated opaque strings. Real ID generation
cannot.

**Options**: implement ULID inside `Curia.Domain.Primitives` (BCL-only, ~100 lines, and ULID
is a simple spec); or amend R11.1 to admit a narrow allow-list; or move ID generation to
Application.
**Recommendation**: implement it in `Domain.Primitives`. R11.1 is load-bearing and worth more
than the dependency saves.
**Status**: **Complete** — BCL-only ULID in `Curia.Domain.Primitives`, verified against the
specification independently (max value decodes to exactly 2^128−1; Crockford alphabet checked
algorithmically). Not yet wired into `Curia.Domain`'s identifiers — that is Tier 3's step.
**Supersedes** `curia-csharp-scoping.md` §9's `Ulid | Domain | R8.3` package row, which should
be corrected.

---

## Carried from Tier 1

- **ULID randomness-exhaustion is untested.** The path is real, working code — a verifier
  forced the private counters by reflection and confirmed it fails cleanly rather than
  wrapping — but no shipped test exercises it, because doing so naturally requires 2^80 calls.
  Worth a reflection-based or seam-based test.
- **Monotonicity is same-millisecond only.** A backward clock jump between calls produces
  ULIDs that sort out of order; independently confirmed real. Correctly scoped and disclosed,
  not a hidden defect, but it should be stated wherever ULID ordering is relied upon.

## Tier 2 — close the differential harness, unblocked, small

Definition of Done item 5 is *"the harness runs clean, or every divergence is a committed
vector."* **Met: 0 divergence classes across 22,515 compared lines.**

### T2.1 — fix our own node oracle's unpaired-surrogate bug

667 occurrences, and **not** a divergence between implementations: C# and Rust agree
(reject); the oracle wrongly accepts, because its `\u` decoder builds an unpaired-surrogate
JS string that `Buffer.from` silently mangles to U+FFFD. Ours to fix, in `oracle.mjs`.
**Success**: the class disappears; the oracle still reproduces all six vendored RFC 8785
vectors byte-exactly.
**Status**: **Complete** — root cause was `String.fromCharCode` per-escape with no
surrogate pairing, producing a lone surrogate in a JS string ("WTF-16") that
`Buffer.from(s,'utf8')` then silently replaced with U+FFFD. The oracle's *input* path was
strict (`TextDecoder` with `fatal:true`) and its *output* path was not; only that asymmetry
made the bug reachable. Six vendored vectors still byte-exact, and a valid astral pair
(U+1D11E) still canonicalizes — the control that proves the fix did not overshoot.

### T2.2 — pin `raw-control-character`

C# reports `curia/admit/malformed-json` for a raw C0 control byte where Rust has a dedicated
slug. R6.40 names three slugs and not this one, so the vocabulary is genuinely unspecified.
By R6.40's own condition-naming principle, Rust's is right.
**Work**: extend R6.40 by one slug; align C#; add the pinning vector R6.40 requires.
**Success**: harness at **0 classes**; DoD item 5 met rather than nearly met.
**Status**: **Complete** — R6.40 extended with an explicit precedence clause, since NUL is
itself a C0 control byte and the classes overlap: `curia/admit/nul-byte` wins for `0x00`,
`raw-control-character` covers the other 31. Verified on both endpoints at `0x00`, `0x01`
and `0x1f`.

---

## Tier 3 — finish the event store *(blocked on Docker)*

Carried from Increment 3 unchanged. **Docker is unavailable in this environment**, and these
two are precisely the tests that must hit a real server:

- **Stage 2 (R11.6)** — append-only is enforced *by the grant*. The test connects **as
  `app_role`** and proves the *server* refuses `UPDATE`/`DELETE`. Against a mock it asserts
  that our mock refuses, which is worth nothing.
- **Stage 3 (R11.9)** — replay-rebuild *exercised*, not assumed. A rebuild that only runs
  against an in-memory fake proves the fake is self-consistent.

**Unblock path**: enable Docker locally, or run these in CI and treat CI as the verifier.
Until then they stay Not Started rather than being written blind and marked done.
**Depends on**: T1.3 (real ID generation).
**Status**: Blocked

---

## Tier 4 — specification debt, unblocked, large

### T4.1 — merge the errata into the white paper as v1.1

**40 proposed requirements across 33 entries (A1–E8) live only in the errata.** The errata is
authoritative wherever it touches v1.0, so every reader must currently hold both documents in
their head and diff them mentally. That is exactly the condition that produced the R4.21
collision — and the collision was found by accident, not by a process.

CLAUDE.md already states the shape: *"incorporating the errata into v1.1 is a merge rather
than a renumber"*, with the sole deliberate exception being A8's §10 renumbering.

**This is the highest-leverage item in the plan even though it blocks nothing**, because
every future increment pays a tax for it and the tax is paid in silent misreadings.

**Success**: v1.1 carries every requirement; the errata becomes a changelog rather than a
parallel authority; the consolidated index is checked against the merged text mechanically,
not by eye. Add a check that no requirement number is defined twice — the R4.21 collision
would have been caught in seconds by a script.
**Status**: Not Started

### T4.2 — resolve two recorded contradictions

- **Appendix D vs R11.3**: the `events` DDL uses `server_ts TIMESTAMPTZ DEFAULT now()`
  (Postgres-side) while R11.3/`CS-9` push time through a Clock port. Increment 3 stamps it
  explicitly and recommends Stage 2 do the same; the DDL should say so rather than leaving
  the default as an invitation.
- **Depth cap vs the submission wrapper**: ADMIT's depth cap is defined over "the document",
  but the wire submission wraps the envelope one level deep, so a maximal-depth envelope is
  rejected for being wrapped. `admit-reject/` pins ADMIT against bare documents and contains
  no oracle. Academic for Table 9 envelopes, which are shallow — but unstated.

**Status**: Not Started

### T4.3 — apply E8/R4.29 to Table 6

The `expired` state is proposed in the errata and still absent from Table 6. Folds into T4.1.
**Status**: Not Started

---

## Order, and why

1. **T1.1, T1.2** — live seams in merged code; both get more expensive with every caller.
2. **T2.1, T2.2** — cheap, and they close a Definition of Done item outright.
3. **T1.3** — small, and it unblocks Tier 3.
4. **T4.1** — large, unblocks nothing, and is the one most likely to cause the next defect.
5. **T3** — the moment Docker exists.

T4.1 sits fourth despite being highest-leverage because it is the only item here that is
purely additive: nothing regresses while it waits. Everything above it either degrades or
blocks something.
