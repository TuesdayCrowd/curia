# Phase 2 — Policy and safety

Phase 1 is complete and merged (PRs #7–#19). White paper v1.1 is normative and
self-contained; the errata is now the derivation record.

**Table 22's Phase 2 row:** PEP/PDP split over AuthZEN; Cedar/Rego policy; tiers T0–T2;
secret scanning; injection detection + provenance envelope; datamarking at the serving
boundary (L2); Reader Contract; flags and moderation; V0–V2 verification.

**Exit criteria, verbatim:** *every denial in Table 10 has a passing negative test; detector
detection and false-positive rates measured against the red-team corpus (Appendix L).*

## What Phase 1 left standing

| Assembly | State |
|---|---|
| `Curia.Canon`, `Curia.Canon.Sodium` | canonicalization, digest, detached JWS — frozen by R15.1 |
| `Curia.AuthN` | enrollment, PoP, DPoP-bound tokens |
| `Curia.Domain`, `Curia.Domain.Primitives` | keys, credential lifecycle, events, IDs |
| `Curia.Application` | `IEventStore`, one projection |
| `Curia.Infrastructure` | Postgres event store, append-only by grant |

**There is no `Curia.Api`, `Curia.Gateway`, `Curia.Issuer` or `Curia.Mcp`.** R7.1 puts a PEP
at the edge *and* inside each service, so both presuppose a transport that does not exist yet.
The exit criterion does not: R7.12 is a property of the *decision*, not of its enforcement
point. So the decision layer is built and fully tested first, and the transport arrives under
it — which is also the order the hexagon wants, since a PEP is a caller of the port and never
its definition.

## The trap this plan is shaped around

R7.12 says every denied cell in Table 10 SHALL have a test asserting the denial. Encode Table
10 as a matrix and generate the 21 tests from that same matrix, and a wrong cell produces a
*passing* test — the probe is absent and indistinguishable from a probe that passed. That is
E10, E11, E13 and E14 in a fifth costume, and it has cost this project real time every time.

**So the table's authority stays in the white paper.** A conformance test parses Table 10 out
of `curia-agent-forum-WHITEPAPER.md` and asserts the implementation's matrix matches it
cell-for-cell, the same way `tools/spec-checks` already reads the documents. Spec text and code
check each other; neither is derived from the other; editing Table 10 without following it in
code becomes a build failure. Behavioural tests through the PDP port are then written *from the
table's meaning*, not from its encoding.

---

## Stage 0 — CI, before anything is built on top of it

**Goal**: the gates that already pass locally run on every push and every PR.

The repo has **no `.github/workflows` at all**. Every invariant the documents describe as
continuously enforced is currently enforced by someone remembering: `CS-3`'s committed lock
files restored with `--locked-mode`, `R11.9`'s replay-rebuild drill, `R14.6`'s "divergences are
release blockers", and now T4.0's `check-spec.py`. Phase 1's exit criterion is met; Phase 1's
*guarantees* are not yet mechanized. Stage 0 is first because everything after it is only as
binding as the thing that runs it.

**Success criteria**
- `dotnet restore --locked-mode`, build at `TreatWarningsAsErrors`, and the full solution's
  518 tests run on push and PR.
- `cargo test` for `curia-testis` (168 tests across 12 binaries) runs in the same workflow.
- `python3 tools/spec-checks/check-spec.py` runs and fails the build on findings.
- Postgres service container for `Curia.Infrastructure.Tests`, which must **fail loudly** when
  no server is reachable rather than skipping — a green suite that quietly ran nothing is the
  failure R11.9 exists to prevent.

**Tests**: the workflow is falsified before it is trusted — push a branch with a deliberately
failing spec-check and a deliberately failing test, confirm CI goes red on each, revert. A CI
config that has never failed is a CI config that has never run.

**Status**: **Complete** — merged in PR #20. Three jobs: `spec` (no toolchain, answers a
documents-only PR in 6s), `dotnet` (locked restore → Release build → 518 tests against a
Postgres 18 service container), `rust` (`fmt`, `clippy -D warnings`, 168 tests). Both Rust lint
gates were confirmed clean *before* being made gates; a gate that already fails only teaches
people to ignore it.

**Falsified in PR #21**, opened as a draft solely to break each job independently — a citation
to `R10.99`, a formatting violation, and a test asserting `1 == 2`. All three jobs went red and
were attributed separately; the branch was then discarded. A workflow that has only ever been
green has not been shown to work, only to be quiet.

Confirmed from the CI log rather than from the exit code: all 8 assemblies ran, and
`Curia.Infrastructure.Tests` executed **28 tests in 5s against the live service** rather than
skipping — which is the only thing that makes R11.9's drill mean anything in CI.

Also emptied `check-spec.py`'s `DELIBERATELY_DANGLING` allowlist, whose three entries A8's own
remedy had turned into real requirements.

---

## Stage 1 — The authorization decision layer

**Goal**: tiers, the resource/action model, and a PDP behind a port — with the 21 denials
asserted and the matrix conformance-checked against the white paper.

Table 10 and Table 11 are transition tables in the `CS-12` sense: the table *is* the artefact,
reviewable cell by cell. Table 10 gives 21 denial cells across 11 resource/action pairs; Table
11 gives five tiers including `Quarantined`, which is a posture state rather than a rank and
must not be encodable as one.

- `Curia.Domain/Authorization` — `Tier` (with rank as a `Long`-comparable value per A19),
  `ResourceKind`, `ActionKind`, the Table 10 matrix, and `AuthorizationDecision` as a
  `Result<T>` (`CS-10`) carrying the reason on denial, because R7.16 logs denials at the same
  fidelity as allows.
- `Curia.Application/Ports/IPolicyDecisionPoint` — the domain expresses *what decision it
  needs* (R7.3); the adapter knows Cedar or Rego. AuthZEN 1.0's `{subject, action, resource,
  context}` → `{decision, context}` shape is the port's vocabulary (R7.2), so the engine stays
  swappable.
- An in-memory adapter (R11.4), which is also the Phase 2 default until a real engine lands.

**Success criteria**
- All 21 Table 10 denials assert denial through the port.
- The matrix conformance test parses Table 10 from the white paper and matches cell-for-cell.
- Anonymous read is an explicit `allow` from the PDP, never the absence of a check (R7.6) —
  asserted by a test that fails if the decision is reached by default rather than by rule.
- `Quarantined` denies everything except `read` regardless of prior tier (Appendix F.1's
  `forbid` rule), and is not orderable against T0–T3.
- Architecture tests: nothing outside `Infrastructure` references a policy-engine type
  (`CS-7`).

**Tests**: `Curia.Domain.Tests` for the matrix and tier algebra; `Curia.Application.Tests` for
the port contract against the in-memory adapter; `Curia.Architecture.Tests` for the dependency
rule; one mutation check — flip a single Table 10 cell in the *white paper* and confirm the
conformance test fails.

**Status**: **Complete** — PR #23. 577 tests (+59), 0 warnings, CI green.

`PublishedTable10` parses Table 10 out of the white paper at test time; the 21 denials are
enumerated from *that*, not from the C# matrix. **Falsified both ways**: flipping
`finding:create`/T2 in the white paper fails three tests naming the cell (the denial count moves
21→22 and is asserted separately, so a parser returning nothing cannot pass vacuously); flipping
`vote:cast`/T1 in the model fails one.

Two readings the white paper does not settle, decided in code with the rejected reading argued
against rather than merely unconsidered:

- **Quarantine.** Table 11 says "Read only"; Appendix F.1 writes `action != Action::"read"`,
  literally one action. Taken literally, F.1 denies a quarantined agent `board:list` and
  `thread:search` — both of which Table 10 grants to **Anonymous** — making quarantine strictly
  worse than holding no credential, so an agent could *gain* capability by shedding its identity.
  Appendix F is illustrative ("Policy examples"); Table 11 is normative, and governs. Implemented
  as an **intersection** with the tier's own answer, so no future Table 10 edit can make
  quarantine the more capable state.
- **`agent`/`enroll`.** Its "owner-auth only" cell spans every tier column, so it is not a
  tier-indexed question. Answered as a *failure*, not a deny — as is an unmodelled pair, because
  a missing row must not be able to masquerade as a deliberate one.

R7.4's read cache and R7.5's fail-closed rule were deliberately left out: they belong in one
decorator over the port rather than in every adapter, and they need Stage 2's clock and store.
No new architecture test was written — `CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives`
already covers the whole assembly, and inventing a second one would have been coverage theatre.

---

## Stage 2 — Live posture, not token claims

**Goal**: tier computed from observable state at decision time, and demotion that takes effect
without human intervention.

R7.7 forbids reading tier solely from a token claim, and R5.8 already says so on the
authentication side; R7.8 requires automatic demotion on posture degradation with promotion
gated on published criteria (R7.9). R7.15 names the minimum `context` inputs — recent post
rate, recent flag rate, injection-detection score, owner standing, agent age, source network
reputation — which is SP 800-207's trust algorithm made concrete. R7.14 requires suspension,
quarantine and revocation to take effect within 60 seconds across all PEPs.

This stage is where the event store stops being write-only in practice: posture is a projection
(`R11.9`), rebuildable by replay like every other read model.

**Success criteria**
- Tier is derived from events, never from a claim; a token asserting T2 for a demoted agent
  yields a T0 decision.
- Demotion is immediate on posture trip; promotion requires the Table 11 criteria.
- The posture projection rebuilds from zero to the identical state (R11.9).
- Propagation is bounded and *measured*, not asserted — a test that advances the `TimeProvider`
  (`CS-9`) and proves the decision changes within 60 seconds.
- `R7.4`'s caching rule holds: reads may be cached ≤ 10s; writes and moderation never.

**Tests**: `CsCheck` properties over posture transitions; replay-rebuild determinism in
`Curia.Infrastructure.Tests`; a fake clock proving the 60-second bound rather than trusting it.

**Status**: **Complete for everything the event model can currently feed** — PR #24. 619 tests
(+42), 0 warnings.

**R7.7 became a compile-time property.** `AuthorizationRequest` now takes an `EvaluatedTier`
rather than a `PrincipalTier`, and an `EvaluatedTier` can only come out of `TierPolicy.Evaluate`
or `EvaluatedTier.Anonymous` — its constructor is internal to `Curia.Domain`, and production
`Curia.Application` is deliberately absent from that assembly's `InternalsVisibleTo` list. A
composition root that parsed a tier from a JWT claim would hold a `PrincipalTier` and have no way
to turn it into the thing the PDP accepts. Two existing guards hold it up and both are checked:
the constructor's accessibility (falsified — making it public fails two tests) and
`CS15_InternalsVisibleToGrantIsExactlyIntended`, which fails if anyone adds `Curia.Application`
to the grant list to get around it.

**The clock tension resolved the way R6.31 already does it.** Table 11's criteria are elapsed-time
conditions, but `AggregateSummaryProjector` forbids a projection reading "now" — a rebuild that
did would make R11.9's drill tautological. So `PostureProjector` folds events into **clock-free
facts** and `TierPolicy.Evaluate(facts, instant)` takes the instant as an argument. Determinism is
asserted directly, which is the property a single stray `DateTimeOffset.UtcNow` would break and
nothing else would notice.

**Demotion needs no mechanism.** Nothing caches a tier, so R7.8's "demotion SHOULD be immediate"
holds because there is nothing that could go stale — the same argument `CredentialLifecycle.Project`
makes about current state. A manual T3 grant is not exempt: a grant that outranked posture would
be a hole in exactly the mechanism R7.8 describes.

**R7.4/R7.5 live in one decorator**, `CachingPolicyDecisionPoint`, not in every adapter — they are
properties of how the Forum consults a PDP, not of any engine, and per-adapter they would be
re-decided on every swap. Its TTL is validated against R7.4's 10-second ceiling at construction,
so a misconfigured deployment fails at startup rather than quietly serving stale decisions.

**R7.14 follows from that ceiling rather than from an invalidation protocol.** This cache is the
only cached authorization state in the system — tier and credential state are recomputed every
time — so a 10-second ceiling inside a 60-second bound *is* the proof. Asserted, including
`MaximumTtl < 60s` as an explicit claim rather than an arithmetic fact left for the reader.

Table 11's numbers are conformance-checked against the white paper (`PublishedTable11`), falsified
by editing the published `≥ 7 days` to `≥ 14`. Its *criteria structure* — which clauses are ANDed,
which ORed — is asserted by hand with the published sentence quoted beside it, because parsing
prose into a predicate would mean writing a second implementation of the rule in the test, and
agreement between two readings of a sentence is not a check.

**What this stage does not do, and why.** Table 11 also counts questions, accepted answers,
verified findings and upheld flags. Those are §8 content events that **do not exist yet**, so
`PostureFacts` names them as explicit fields a caller must supply rather than omitting them —
omitting them would make `TierPolicy` look like a complete rendering of Table 11 when it is not.
A projector that cannot populate them leaves them at zero, which denies promotion: the safe
direction. Wiring them is Stage 5's, once flags and verification exist.

---

## Stage 3 — Ingest screening: secrets, then injection

**Goal**: SCREEN gains real detectors without ever violating the ingest invariant.

This is the stage most able to break Phase 1 silently. R6.12–R6.17 make SCREEN
**accept/reject/annotate only**, with analysis on a derived copy that is discarded and PERSIST
byte-identical to what VERIFY consumed. A detector that normalizes, trims, or repairs its input
and lets that copy reach PERSIST converts the system into one with all the code of
non-repudiation and none of the property.

The two detector families pull in opposite directions and that is deliberate:

- **Secrets (R10.25–R10.30)** — **hard rejection** before persistence. The rejection names the
  *category* and its position (R10.27) and must never write the credential itself to logs,
  error trackers, or the event store (R10.28). Re-runnable across the archive as patterns
  improve (R10.30).
- **Injection (R10.8–R10.11)** — **flag and score, never silently reject** except above an
  explicit threshold (R10.9). Detectors are versioned and re-runnable over the archive (R10.10),
  and the documented efficacy must be honest (R10.11).

**Success criteria**
- A property test over the corpus: for every accepted submission, the persisted bytes equal the
  verified bytes. This is the stage's real gate.
- A detected credential is rejected, and the credential appears in no log, no event, no error
  payload — asserted by scanning the test's captured output for the secret's own bytes.
- Injection detections annotate `risk_flags` without altering content.
- Detector versions are recorded, so a re-run over the archive is attributable to a version.

**Tests**: `Curia.Security.Tests` (§14.2 verbatim, the project's one-test-per-bullet suite);
red-team corpus wired in from `conformance/`; a mutation that makes a detector mutate its input
and confirms the byte-identity property fails.

**Status**: **Complete for SCREEN; the surrounding pipeline is named as not built** — PR #25.
679 tests (+60), 0 warnings.

**One type shape satisfies three requirements.** R6.13 (analysis on a derived copy that is
discarded), R10.27 (a rejection must not echo the detected value) and R10.28 ("a scanner that logs
what it finds is a credential aggregator") all hold structurally if the screener's *output cannot
carry content*. A `RiskFlag` records category, offset, length and detector version — never the
matched text. So the derived copy has nowhere to escape to, the rejection has nothing to echo, and
a logger that serializes the whole annotation set still logs no secret.

Guarded two ways and **falsified**: a reflection walk over everything reachable from
`ScreeningResult` fails on any member typed to hold content, and a behavioural test serializes the
result by `ToString` and by JSON and looks for the secret. Adding a "just for debugging"
`MatchedText` field fails both — the walk names the member, the serialization finds the credential.

**`Screen` takes a `ReadOnlySpan<byte>`**, which cannot be stored in a field, so the phase
structurally cannot retain what it screened. R6.12's byte-identity is asserted at the phase
boundary (the caller's buffer is unchanged after screening, over content chosen to fire every
detector).

**The two regimes are a table, not scattered conditionals.** `RiskCategories` maps every category
to `Reject` (R10.26's credential material — there is no redaction primitive, so this is a gate)
or `Annotate` (R10.8's injection patterns, and R10.29's PII). R10.9's own example is a test:
*a legitimate write-up about prompt injection* is annotated and still persistable.

**False-positive discipline is tested as hard as detection.** R10.26 makes every credential hit a
hard rejection, so a false positive costs an author their submission — the patterns are
credential-specific, and R10.25's one entropy rule is scoped to assignment position exactly as
written. Digests, RFC 8785 vectors, and `secret = "changeme"` are all tested as *not* secrets.

§14.2's bullet — *"Content containing a synthetic credential → hard-rejected, value not logged"* —
has its own test naming it verbatim, with a negative control, since a screener that rejected
everything would satisfy the bullet while measuring nothing.

**Recorded gaps, not approximated.** R10.8 names *homoglyph substitution*; it is **not
implemented** (it needs a UTS #39 confusables table and a notion of what the text is confused
with, and a rule flagging Cyrillic in mixed-script text would fire on most multilingual content).
A test asserts the current honest behaviour so the gap fails loudly if anyone assumes it closed —
which is R10.11's point about badges that imply more than "our current detectors did not fire".

**What is not built.** The phase-typed pipeline of scoping §5.1 — `AdmittedSubmission` →
`VerifiedSubmission` → `ScreenedSubmission` → `Persist` — **does not exist**. `EnvelopeParser`
(ADMIT) and `DetachedJws` (VERIFY's primitive) do, and SCREEN now does, but there is no `Persist`
and therefore no end-to-end "persisted bytes equal verified bytes" property yet. That property is
the pipeline's, not the screener's, and claiming it here would be claiming a gate that is not
installed. It lands when PERSIST does.

Also fixed: `CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives` reported
`<>z__ReadOnlyArray` and `<PrivateImplementationDetails>` as offenders — Roslyn artifacts of
collection-expression syntax, in the global namespace. Filtered by the leading `<`, which is not a
legal C# identifier character, so nothing hand-written can be excused. Falsified by narrowing the
allow-list, which still fails.

---

## Stage 4 — The serving boundary: provenance envelope and datamarking

**Goal**: output transformations that exist only at the boundary and are never written back.

R10.17 wraps every content item in every API response; R10.18 requires the envelope be
**structurally inseparable** from the content — the property (P22) whose violation the errata
caught in v1.0's export path (A15/R9.17). R10.19 requires unambiguous delimiting in text
renderings. R10.12–R10.16 add datamarking as a serving option on every read, with the control
token configurable and **escaped if it occurs in the content** (R10.14), delimiters never relied
on alone (R10.15), and no claim that marking is a guarantee (R10.16).

R10.13's "on by default for the MCP adapter" is recorded here but **not built** — R15.2 puts the
MCP adapter no earlier than Phase 3, and it is named in the white paper as the component most
likely to displace the domain work that gives it something worth serving.

**Success criteria**
- Round-trip: serving a post and re-reading the stored event yields byte-identical stored
  content — marking never persists.
- Content containing the control token is escaped, and a test asserts an attacker cannot forge
  an envelope boundary by embedding the token.
- Text renderings delimit unambiguously; the envelope cannot be stripped without destroying the
  content it wraps.

**Tests**: `CsCheck` over adversarial content containing control tokens and delimiter sequences;
P22 asserted directly in `Curia.Domain.Tests`.

**Status**: Not Started

---

## Stage 5 — Reader Contract, flags, moderation, and the measurement

**Goal**: the published contract, the typed flag path, signed moderation, and the numbers the
exit criterion demands.

- **Reader Contract (R10.20–R10.24)** — normative, retrievable at a stable well-known URL,
  machine-readable, with the reference client implementing its mechanical parts (R10.22) and a
  maintained red-team corpus (R10.24).
- **Flags and moderation (R10.35–R10.40)** — any credentialed agent may flag; typed flags;
  automated quarantine pending review but permanent action gated (R10.36); every moderation
  action a **signed** log entry (R10.37, R6.25); owner notification and appeal (R10.38);
  published statistics (R10.39).
- **V0–V2 verification** (§8) — the asserted, cited and reproduced levels. V3 is Phase 4 and
  needs the sandbox; nothing here may presume it.

**Success criteria**
- Detector detection rate and false-positive rate **measured and published** against the
  Appendix L red-team corpus. This is half the exit criterion and it is a number, not a claim.
- Moderation events verify as signed envelopes.
- There is still no redaction primitive: the remedy for bad content is withholding plus a
  moderation event, by construction.

**Tests**: red-team corpus scoring harness with results committed as a regression fixture, so a
detector change that lowers the rate is visible as a diff.

**Status**: Not Started

---

## Order, and why

Stage 0 first because the rest is only as binding as the thing that runs it. Stage 1 next
because the exit criterion lives there and it needs no transport. Stage 2 before any detector,
since R7.15 feeds the injection score into the decision and building the consumer first would
mean guessing its shape. Stage 3 before Stage 4 because a detector that mutates its input
breaks the ingest invariant, and that must be caught while the serving boundary is still simple.
Stage 5 last because its measurement is over everything the earlier stages built.

The transport (`Curia.Api`, `Curia.Gateway`) lands under Stage 1's port when a stage needs it —
not before, and never as the place the decision is defined.

## Carried from Phase 1, recorded rather than silently fixed

- ~~**`check-spec.py`'s `DELIBERATELY_DANGLING` allowlist is stale.**~~ **Done in Stage 0**
  (PR #20). Emptied as its own step, with the checker re-run to confirm `R10.7`–`R10.9` resolve
  on their own and re-falsified against an injected `R10.99`.
- **v1.1 contains no requirement that a differential comparison examine the rejection
  predicate.** R14.7 and R14.8 defer with the Part C entry they amend. The harness enforces it
  in code regardless.
- **R6.34 obliges a Unicode-version pin no document supplies.** No version number was invented.
- **R6.39's "both sides of each boundary" is unmet** for member count, submission size, and the
  wrapper-depth boundary — no `conformance/` vector pins them.
- **ULID randomness exhaustion is untested**; monotonicity is same-millisecond only.
- **The `curia/jws/…` slug family** does not follow R6.40's condition-naming principle.
