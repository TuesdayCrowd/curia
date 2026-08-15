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
vector."* **Met: 0 divergence classes across 22,515 compared lines** — re-measured under
T2.3's corrected comparison rules, not the weaker ones the first zero was obtained with.

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

### T2.3 — the zero above was measuring a narrower question than it looked like

`compare.mjs`'s two canonicalize classifiers compared accept-versus-reject and never the
rejection predicate, so **both implementations rejecting one input under different slugs was
reported as agreement.** Only `admit` compared slugs. Fixing the rules alone, changing no
implementation, turned the same seed and count from 0 classes into **7 classes over 3,594
records** — including both divergences errata E13 had already recorded as found-and-unfixed
while the harness stood on top of them printing zero.
**Work**: compare the predicate on `canonicalize` and `canonicalize_nfc` too (R14.8, errata
E14); fix the Rust side R6.43 already governs — `NfcError::Parse(_)` delegates to
`ParseError::predicate()` instead of collapsing every condition onto
`curia/canon/parse-error`, and `json::parse` scans for raw NUL ahead of UTF-8 validation so
R6.40's carve-out holds on the ADMIT-free path as it does at ADMIT.
**Status**: **Complete** — back to **0 classes across 22,515 lines** under the stricter
rules, and all 44 corpus vector inputs under all 3 ops (132 records) agree on every field.
Pinned by in-implementation tests at both canonicalizing entry points, since no published
vector reaches either (R6.43's closing sentence, R14.7).

---

## Tier 3 — finish the event store *(unblocked — in progress)*

**The blocker was stated in terms of a tool, not a requirement.** "Needs Docker" was shorthand
for "needs Testcontainers", which was shorthand for **"needs a real PostgreSQL server"** — and
only the last is true. PostgreSQL 18.4 is installed via Homebrew and already running locally.
Docker is not used and is not needed.

The R11.6 mechanism was proved by hand before any code was written:

```
as the restricted role:
  SELECT count(*) FROM events;       -> 1 row
  UPDATE events SET event_type='x';  -> ERROR: permission denied for table events
  DELETE FROM events;                -> ERROR: permission denied for table events
```

Integration tests take the connection from an environment variable and create a throwaway
database per run, so nothing depends on this machine and CI can point the same variable
anywhere. If no server is reachable the tests **fail loudly naming the variable** — they never
silently skip, because a green suite that quietly ran nothing is the exact failure mode R11.9
exists to prevent.

- **Stage 2 (R11.6)** — `Curia.Infrastructure`, `db/` migrations, grant-refusal test.
- **Stage 3 (R11.9)** — a projection and its rebuild-from-zero, plus determinism.

**Status**: In progress.

## Tier 4 — specification debt *(design revised — the original plan was wrong)*

I reviewed this tier's own design and found four errors in it. They are worth stating, because
three of them would have caused damage rather than merely wasted effort.

**Error 1 — the entry count was wrong.** The plan said "33 entries". The grep behind that
number matched `^## [A-E]<n>`, and **Part A uses `###`**, so every Part A entry was silently
excluded. The real inventory is ~40 entries: A(6 headings, one covering A12/A13), B(7), C(9),
D(9), E(8).

**Error 2, and the serious one — "merge the errata into the white paper" is not a documentation
task, it is a governance decision, and the plan conflated them.** The errata's own preamble
distinguishes its parts by *certainty*, and they are not equivalent:

| part | what it is | mergeable? |
|---|---|---|
| **A** | errata — statements in v1.0 that are wrong | yes, they fix defects |
| **D** | findings from building Increment 1 | yes, evidence-backed |
| **E** | findings from building twice and comparing | yes, evidence-backed |
| **B** | normative gaps, with proposed text | each needs an accept decision |
| **C** | **enhancements — "design ideas that go beyond repair"** | **no, not without acceptance** |

Part C is nine substantial architectural proposals — signed vote envelopes with epoch sealing,
the curation lane, witness cosigning, Sybil bounding, Reader-Contract attestation,
content-addressed dumps, event-driven staleness, differential fuzzing, nomenclature. The errata
says they are *"argued, not asserted, and each states its cost."* Merging them wholesale would
promote nine unaccepted designs into the normative specification by side effect.

**Error 3 — merging Part C would close open decisions silently.** CLAUDE.md is explicit:
*"D1–D10 are open decisions and should stay open unless deliberately closed and recorded."*
Part C entries state their entanglement outright — C1 *"partially informs D5"*, C3 is *"the
same governance shape as D9's seed set"*, C8 *"turns D1's third option into a permanent
asset."* A merge that swept them in would resolve D1, D5 and D9 without anyone deciding to.

**Error 4 — found while checking the above.** Errata A13 says it *"resolves both, and closes
D6"*, but **§16 still lists D6 as open.** The document that closes a decision and the document
that lists it disagree. This is the same cross-reference rot that produced the R4.21 collision,
and it was again found by accident rather than by a check.

### T4.0 — the mechanical checks, first *(new, and it should have been first all along)*

Before merging anything, write the checks that would have caught what has so far been caught by
luck. Every defect below was found by a human noticing, which does not scale and did not
reliably work:

- **No requirement number defined twice.** Would have caught R4.21 in seconds.
- **Every requirement cited anywhere exists**, in one of the two documents.
- **The consolidated index matches the entry bodies** — same IDs, same count, no orphans.
- **No requirement defined in both documents with differing text.**
- **Every §16 decision listed open is not claimed closed elsewhere.** Would have caught D6.

These run in CI over the Markdown. They are cheap, and they convert a class of defect from
"discovered eventually, by chance" into "cannot be committed".
**Status**: **Complete** — `tools/spec-checks/check-spec.py`. Found 4 genuine defects on its
first clean run, one of them new and serious (R5.9–R5.11 cited but never defined). Also found
three false-positive modes **in itself**, each of which would have prompted a spurious edit to
a specification: reading whole index rows instead of the ID cell, not expanding range rows,
and not recognising a decision marked CLOSED in place. Falsified against the historical R4.21
collision.

### T4.1 — triage, then merge only what is settled

Classify all ~40 entries into **corrections** (A, D, E — merge), **gaps** (B — accept
individually, then merge), and **enhancements** (C — do not merge; record each as a proposal
with its cost and its decision status). Produce v1.1 carrying corrections and accepted gaps
only, with the errata retained as the changelog and the rationale record.

**Success**: v1.1 is normative and self-contained for everything settled; no Part C proposal
appears in it without an explicit recorded acceptance; the T4.0 checks pass over the result.
**Status**: Not Started

### T4.2 — close what is already decided but still recorded as open

- ~~**D6** — closed by A13, still listed open in §16.~~ **Done** — §16 now records the closure
  and retains the original fork, since §16 asks that decisions be "closed deliberately and
  recorded" and deleting the entry would destroy the record of what the fork was.
- **Appendix D vs R11.3** — the `events` DDL's `server_ts TIMESTAMPTZ DEFAULT now()` versus
  time entering through the Clock port. Nothing relies on the default now; the DDL should say
  so rather than leave it as an invitation.
- **Depth cap vs the submission wrapper** — the cap is defined over "the document" while the
  wire submission wraps the envelope one level deep. `admit-reject/` pins ADMIT against bare
  documents and contains no oracle for it.
**Status**: Not Started

### T4.3 — apply E8/R4.29 to Table 6

The `expired` state is proposed and still absent from Table 6. Folds into T4.1's corrections
merge (E is a corrections part).
**Status**: Not Started

## Order, and why

1. **Tier 3** — in progress; the last Phase 1 pieces, and no longer blocked.
2. **T4.0** — the mechanical checks. Cheap, and they stop the bleeding: four defects so far
   have been found by someone happening to notice, including two in this plan's own design.
3. **T4.2** — small, and D6 is a live inconsistency between the two authoritative documents.
4. **T4.1** — the merge, once the checks exist to verify it and the triage has separated
   settled corrections from unaccepted proposals.

The original plan put the merge first and treated it as mechanical. It is not: roughly a
quarter of the errata is proposals that a merge would silently adopt, and three open decisions
would have been resolved as a side effect of a documentation task.
