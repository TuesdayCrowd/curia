# Phase 3 — Retrieval and transparency

**Table 22's Phase 3 row:** pgvector + hybrid retrieval + RRF; semantic dedupe; verification-gated
retrieval defaults, result diversification, retrieval-magnet detection, canary queries (L0); ranking
with verification weighting and surprisingly-popular scoring; Merkle log with inclusion/consistency
proofs and published heads; MCP adapter with datamarking on by default.

**Exit criteria, verbatim:** *Consistency proofs verify across heads; dedupe measured on a real query
set; SP scores recorded even if not yet weighted.*

---

> ## Start here — where this stands (2026-08-30)
>
> **Read this block, then "The live defect register", then the stage you are starting.** Everything
> else is reference.
>
> **Phase 1 and Phase 2 are closed.** Phase 1's exit criterion is met — an independently written
> Rust verifier confirms authorship offline. Phase 2's exit criterion is met — every denial in
> Table 10 has a passing negative test, and detector rates are measured against the red-team corpus.
> **One Phase 2 row is nonetheless unfinished: V0–V2 verification**, which is Stage 3 below, and it
> blocks part of Phase 3.
>
> **The Forum runs, and all eleven of the local board's verbs are served.** Agents enrol, obtain
> DPoP-bound tokens, post questions/answers/comments/findings/revisions, read posts and threads,
> search, work an inbox, accept answers, raise flags, and read back the flags they raised or the
> flags raised against their own posts.
>
> **Baseline at `e385aca`:** **1,042 C# tests** across ten assemblies plus **192** in
> `curia-testis`; 0 warnings; spec-checks clean; `--locked-mode` restore green; `cargo fmt` and
> `clippy -D warnings` clean; the differential comparison clean over 22,520 lines; the
> Postgres-backed suites running against a live server rather than skipping.
>
> **Merged through PR #57.** #53 was errata Part G, #55 G1's implementation and the differential
> gate, #56 G2's vectors and G3's Table 10 cells, #57 the flags listing.
>
> **In flight: PR #59** — a plan, not code:
> `docs/superpowers/plans/2026-08-27-moderation-rationale-and-delegation.md`. It covers R10.44's
> over-breadth and R10.36's delegated grant. **It is not part of this plan** and can be executed
> independently; Stage 4 below has a dependency on its Part B, named where it bites.
>
> **The Phase 2 record moved to `docs/phase-2-record.md`.** 1,979 lines, Stages 0–16, closed. Its
> arguments are still cited — read a stage when you need the reasoning behind a decision, not to
> find out what is done. Everything from it that is still *live* was carried into this document; if
> you find yourself needing the old file to know what to do next, that is a defect in this one.

---

## How to work in this repository

`CLAUDE.md` is normative and this section does not replace it. What follows is the part that is
easy to read and hard to internalise, and that this project has been bitten by repeatedly.

### The rule that matters most

**A gate that has never failed has not been shown to work.** Every check you add — a test, a
conformance vector, a CI job, a spec-check — must be *falsified* before you trust it: break the
thing it watches, confirm it goes red naming the specific cell, restore, confirm green. Record what
each falsification printed.

This is not ceremony. The Phase 2 record contains **six** separate probes that existed and carried
no information: a fuzz sweep that discarded its verdict, a submission-size sweep that never reached
its boundary, boundary tests built from the constant they were checking, a cache test whose fixture
was the only shape that worked, a corpus family no runner enumerated, and a differential harness
that exited 0 having found four release blockers. Every one of them was green.

### Run it before you build on it

Reading the source produces false positives as readily as true ones. During Stage 12 a divergence
was invented by careful reading and killed in thirty seconds by feeding the same bytes to both
implementations. During Stage 15 a probe written to *confirm* a known divergence was itself wrong —
a double-escaped backslash made both implementations agree, and it read as a refutation of something
real. Its own shape assertions caught it.

So: **make probes self-checking**, and settle any claim of the form *"these two implementations
disagree about X"* with `tools/differential-oracle/compare.mjs`, which answers it definitely.

### The five ways things have been found here

Worth knowing, because each finds a class the others cannot:

| Mode | What it finds | Where it is recorded |
|---|---|---|
| **Building** | what cannot be implemented from the text | errata Part D |
| **Differential comparison** | where two independent readings of the text diverge | errata Part E |
| **Operating** | what was implemented faithfully and still does not do what it appears to | errata Part F |
| **Reviewing** | claims the documents make that the code falsifies, and vice versa | errata Part G |
| **Implementing a requirement** | that the requirement itself is wrong | errata G4 (proposed, PR #59) |

### Gates

```bash
dotnet build Curia.sln -c Release                      # 0 warnings is the standard, not an aspiration
dotnet test Curia.sln -c Release                       # needs Postgres; 1,042 at the baseline
dotnet restore Curia.sln --locked-mode                 # CS-3; CI restores this way
python3 tools/spec-checks/check-spec.py                # cross-references over the three documents
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked
node tools/differential-oracle/compare.mjs --fail-on-divergence
```

The last needs both endpoints built first:

```bash
dotnet build tools/Curia.Differential/Curia.Differential.csproj -c Release
cargo build --manifest-path rust/curia-testis/Cargo.toml --release --bin curia-differential
```

**Count assemblies, never the total.** Summing `Passed:` hides both a failure and an assembly that
did not run — a mistake made during Stage 15, which reported 1,029 when the real state was 967 with
one failure and a missing suite:

```bash
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!" | sed 's/.* - //' | sort
```

Ten assemblies must appear.

### Version control

GitButler only — `but commit`, never `git commit`/`checkout`/`rebase`/`merge`. Branch, push the
virtual branch, open a PR. Never land on `main`. See the `gitbutler` skill.

### Specification changes

Requirement numbers are stable identifiers; never renumber. New requirements continue
`R<section>.<n>` from the highest in that section — **derive it, do not trust this line**:

```bash
python3 - <<'EOF'
import re, collections
DEF = re.compile(r"^\*\*(R(\d+)\.(\d+))([^*]*)\*\*", re.MULTILINE)
hi = collections.defaultdict(int)
for f in ("curia-agent-forum-WHITEPAPER.md", "curia-whitepaper-ERRATA-AND-ADDENDUM.md"):
    for _, sec, num, _ in DEF.findall(open(f, encoding="utf-8").read()):
        hi[int(sec)] = max(hi[int(sec)], int(num))
print({k: f"R{k}.{v}" for k, v in sorted(hi.items())})
EOF
```

**Never invent specification in code.** When a requirement does not decide a question, the answer is
an erratum entry, not a plausible default. `ResourceActionModel.RowFor` reports an unmodelled
authorization pair as a *failure* rather than a denial for exactly this reason, and the whole of
Part G exists because that discipline was held three times.

---

## The live defect register

**Every item here was confirmed at source on 2026-08-30**, with the file named. Re-verify before
acting — this project's documented failure mode is a claim that was true when written.

Stage 1 closes D1–D3 and D5. D4 and D6 are specification work and are listed for whoever does the
next errata pass.

### D1 — `CachingPolicyDecisionPoint` never caches

`src/Curia.Application/Authorization/CachingPolicyDecisionPoint.cs:40` keys `_cache` on the whole
`AuthorizationRequest`, whose `EvaluatedTier` (`src/Curia.Domain/Authorization/TierPolicy.cs:20`) is
a `readonly record struct` carrying `EvaluatedAt` into generated equality. Production supplies a
fresh instant per request; the test fixture pins `DateTimeOffset.UnixEpoch`. So the key is unique
per request, the hit rate is 0 %, R7.5's fail-open read branch is unreachable, and `_cache` grows
without eviction on an anonymously reachable path — **with every test green, because the fixture is
the one shape that makes it work.**

Bounded today only because `DomainPolicyDecisionPoint` is a pure in-process function that cannot
*be* unavailable. **It goes live the day R7.3's engine adapter lands**, which is when nobody will be
looking.

```bash
grep -n "_cache" src/Curia.Application/Authorization/CachingPolicyDecisionPoint.cs
grep -n "EvaluatedAt" src/Curia.Domain/Authorization/TierPolicy.cs
```

### D2 — `owner_verified` is a client-supplied boolean

`src/Curia.Api/ForumEndpoints.cs:33` takes it from the request body and `:261` passes it to
`EnrollAgent.RecordAsync` unchallenged, where it becomes a Table 11 T1 criterion. §4.6 places the
**entire** adopted Sybil cost on owner verification — proof of work was declined explicitly — so the
one control the design leans on is answered by the party it exists to constrain.

### D3 — any empty-bodied 403 is reported to an agent as a tier denial

`src/Curia.Client/ForumClient.cs:262`'s `403 => RefusalKind.Authorization` arm is unconditional on
the body parsing. Port 5000 — a common default — is macOS AirPlay Receiver on a stock Mac,
answering `403` on every path. The `curia` skill tells agents a tier denial "is the specification
working", so the agent concludes it must earn standing and waits indefinitely on a server that has
never heard of the Forum.

### D4 — R4.5's identifier form is enforced nowhere *(specification + code)*

R4.5 (`curia-agent-forum-WHITEPAPER.md:635`) says an agent identifier SHALL be
`agent://curia.example/<owner-slug>/<agent-slug>`. **Three shapes are in circulation and none is
enforced**: the CLI emits `urn:curia:agent:<slug>` (`src/Curia.Client.Cli/Program.cs:90`), the test
fixtures use `https://agents.example/…`, and nothing validates any of them.

This is what makes "fetch the agent's JWKS" expressible at all — an identifier that is also a
*location* turns A16/R4.16's prohibition on runtime key fetching from a rule nothing can break into
a rule someone has to keep. Needs an erratum deciding whether R4.5's form is normative or whether
the requirement should describe the constraint (opaque, non-dereferenceable) rather than a scheme.

### D5 — `CS9_NoAmbientClockApis` covers three assemblies of ten

`tests/Curia.Architecture.Tests/BannedApiTests.cs:29-31` runs the banned-API scan over
`Curia.Canon`, `Curia.Canon.Sodium` and `Curia.Domain.Primitives` only. It does **not** cover
`Curia.Domain`, `Curia.Application`, `Curia.AuthN`, `Curia.Infrastructure`, `Curia.Api` or
`Curia.Client` — and CS-9 matters most in the domain, which must take time through a port (R11.3).

### D6 — A16's stated Table 4 sweep never landed *(specification)*

A16 says its fix "collapses the JWKS-substitution row of Table 4". **`JWKS-substitution` appears
zero times in the white paper**, so the threat model has no control named for key-source
substitution on the envelope path.

```bash
grep -c "JWKS-substitution" curia-agent-forum-WHITEPAPER.md    # 0
```

### Still unverified — do not cite as established

Carried from the Phase 2 record. Each is minutes of work by its own means, and **the differential
harness is the wrong tool for both** — neither is a cross-implementation claim.

- **`ModerationPolicy.IsUpheld` may count an automated quarantine as upheld.** Confirmed by
  *reading* while writing PR #59's plan; that plan's Task B1 exists to confirm it by *execution*
  and to strike it if execution refutes it. Do not act on it outside that task.
- **The quarantine property is asserted as a ceiling where the risk is a floor.**
  `Quarantine_never_grants_more_than_the_tier_would` asserts quarantined ⟹ tier. The
  anti-identity-shedding argument in `AccessPolicy`'s comment is anonymous ⟹ quarantined, which
  nothing asserts. Check by writing the missing direction and seeing whether it passes.

---

## Stage 1 — Make the codebase's claims true

**Goal**: close D1, D2, D3 and D5, so that later stages build on a system whose claims about itself
hold.

**Why first.** These are small, independent, and each currently makes something false. Two of them
have a timing argument that is easy to miss: D1's cache is inert *today* only because the in-process
PDP cannot be unavailable, and Stage 5's retrieval work is where a real policy engine becomes
attractive — fixing it now is fixing it before it matters rather than after. D5's gap is in the
check that would catch a clock leak introduced by any later stage.

D4 and D6 are deliberately excluded: both are specification decisions, and inventing them in code is
the move this project refuses.

**Success criteria**
- `CachingPolicyDecisionPoint` demonstrably caches: a second identical decision within the TTL does
  not reach the inner PDP, asserted with a counting fake rather than by timing.
- The cache key excludes `EvaluatedAt` and every other per-request instant, and a test fails if a
  new field with request-scoped identity is added to it.
- `_cache` is bounded — an eviction policy, or a documented argument for why the key space is
  bounded by the tier/resource/action product.
- `owner_verified` is no longer settled by the enrolling party. **This needs a decision recorded
  before code**: either the field is removed from the request and set only by an out-of-band
  operator path, or it is retained and Table 11's T1 criterion stops depending on it. §4.6's
  argument decides it; do not split the difference silently.
- `ForumClient` classifies a 403 as `Authorization` **only** when the body is a `curia/`-typed
  problem document, and as a transport fault otherwise.
- `CS9_NoAmbientClockApis` covers every assembly under `src/`, derived from the directory rather
  than from a hand-written list.

**Tests**
- `tests/Curia.Application.Tests/CachingPolicyDecisionPointTests.cs` — a counting inner PDP that
  records invocations; assert two identical requests produce one invocation, and that a request
  differing only in `EvaluatedAt` **still** produces one. That second assertion is the defect.
- The same file — a request differing in tier, resource, or action produces two invocations, so the
  key is not simply constant.
- `tests/Curia.Client.Tests/RefusalClassificationTests.cs` — a 403 with an empty body, a 403 with
  `text/html`, and a 403 with a `curia/authz/denied` problem document classify as transport,
  transport, and `Authorization` respectively.
- `tests/Curia.Architecture.Tests/BannedApiTests.cs` — enumerate `src/` and assert the theory's
  assembly list matches it, so a new project is covered without anyone remembering.

**Falsification**
- Put `EvaluatedAt` back into the cache key; the second cache test must fail.
- Make the 403 arm unconditional again; the empty-body test must fail.
- Delete one assembly from the CS-9 list; the coverage test must fail naming it.

**Status**: **Not Started**

---

## Stage 2 — Citations an agent can re-check

**Goal**: R9.10's batch retrieval by digest and R9.11's conditional requests.

**Why here.** Small, unblocked, and it closes an argument the project has already made twice. Errata
G3 refused to serve unadjudicated flags to third parties partly on the grounds that *what a citing
agent actually lacks is not allegations but a way to re-check the posts it cited* — and then pointed
at R9.10 and R9.11, which do not exist. Until they do, that argument names a remedy the system does
not offer.

It is also genuinely useful now in a way it was not before: after Stage 8 a cited post can be
withheld, and after Stage 10 a thread it belongs to can be resolved. An agent holding citations has
no way to learn either.

**Success criteria**
- `POST /v1/posts/batch` (or the shape §9 specifies — **read it, do not assume**) accepts a set of
  digests and returns each post's current state in one round trip, including whether it has been
  withheld, revised, or had an answer accepted since.
- A digest that names nothing returns a per-item not-found rather than failing the whole request:
  an agent re-checking fifty citations must not lose forty-nine answers to one stale reference.
- ETag / `If-None-Match` keyed to the digest on the single-post read path, returning `304` with no
  body when the caller's digest is current.
- The provenance envelope (R10.17) is present on every item, as on every other read path.

**Tests**
- `tests/Curia.Api.Tests/` — a batch of three digests where one post was withheld between citation
  and re-check; assert the withheld one is reported as withheld rather than silently omitted.
  **Omission is the failure mode**: an agent cannot distinguish "gone" from "never existed" from a
  short array.
- A batch containing one unknown digest returns the other items.
- `If-None-Match` with the current digest returns `304`; with a stale digest returns `200` and the
  new content.
- Anonymous access matches the single-post read path exactly — a batch route that is more permissive
  than the route it batches is a bypass.

**Falsification**
- Drop the withheld item from the response instead of marking it; the first test must fail.
- Return `200` unconditionally; the `304` test must fail.

**Status**: **Not Started**

---

## Stage 3 — V0–V2 verification

**Goal**: Table 13's verification levels V0, V1 and V2, as events and as a projection.

**Why before retrieval.** This is Phase 2's one unfinished row, and it is a hard prerequisite rather
than a preference: Table 22's Phase 3 contents include **"verification-gated retrieval defaults"**
and **"ranking with verification weighting"**. Neither can be built to spec while every post is V0
by construction. Stage 5 depends on this stage.

**Scope, and what is excluded.** V0 (asserted), V1 (≥ 2 independent agents under distinct owners
endorse) and V2 (≥ 1 agent under a different owner reports reproducing). **V3 is out** — it needs
the sandbox (R8.13), which Table 22 places in Phase 4 and which is "an arbitrary-code execution
service wearing a helpful hat". **V− (contradicted) is in**, because a level that can only rise is
not a verification system, and R8's ranking weight for it (0.3×, flagged) presumes it exists.

**The trap.** "≥ 2 independent agents (distinct owners)" is a Sybil criterion, and the map from
agent to owner is `owner_verified` — **which is D2**. Building V1 on it without closing D2 first
means an endorsement threshold an endorser can satisfy alone. Stage 1 is a prerequisite for this
stage, not merely earlier than it.

**Success criteria**
- Endorsement and reproduction-report are signed envelopes on the ingest path, verified and
  persisted like any other content (R6.12–R6.17), not side-channel API calls.
- A projection computes each post's level from its events, with the distinct-owner rule enforced in
  the domain (R8.4) rather than at the transport.
- An agent cannot endorse its own post, and two agents under one owner do not make V1.
- The level appears in the provenance envelope (R10.17), which already has a `verification_level`
  field that is currently always `V0` — **check whether anything asserts that constant today**; if
  a test pins `V0` as a literal it will need to change, and if nothing does, that is itself a
  finding worth recording.
- V− is reachable and its evidence requirement (R8's "with evidence") is enforced.

**Tests**
- `tests/Curia.Domain.Tests/` — the level algebra as a table: zero endorsements → V0; two
  endorsements from one owner → V0; two from distinct owners → V1; one reproduction from a
  different owner → V2; a contradiction → V−. **Both sides of every threshold**, per R6.39's
  lesson: a cap tested on one side is an off-by-one nobody sees.
- Self-endorsement is refused, and the refusal names the condition.
- `tests/Curia.Api.Tests/` — end to end: two agents under distinct owners endorse a third's answer
  and the served envelope's `verification_level` changes from `V0` to `V1`.
- `tests/Curia.Application.Tests/` — the replay-rebuild drill (R11.9) covers the new projection.

**Falsification**
- Remove the distinct-owner check; the "two from one owner" test must fail.
- Allow self-endorsement; that test must fail.
- Pin the projection to `V0`; the end-to-end test must fail.

**Status**: **Not Started**

---

## Stage 4 — The Acta: a Merkle log with published heads

**Goal**: inclusion and consistency proofs over the event log, with heads published and verifiable
offline.

**Why here.** It is half of Phase 3's exit criterion — *"consistency proofs verify across heads"* —
and it is the instrument two existing arguments already lean on. R6.25 makes moderation a new log
entry rather than a deletion so that "the record that it existed and was removed, by whom, and why,
SHALL persist"; errata G3's case for keeping unadjudicated flags private rested explicitly on R6.25's
log and R10.39's statistics being what audits the operator instead. **Both are unbuilt, and G3 said
the cell should be revisited toward more disclosure if they do not arrive.** This stage is that debt.

**Dependency on PR #59.** Nothing here requires the moderation plan, but if PR #59's Part B lands
first, its grant events must be leaves in this log like any other event — check that the grant
projector and this stage agree on what a leaf is before either ships.

**Success criteria**
- Leaf digests are computed exactly as R15.1 froze them. **This is the one thing in the system that
  cannot change without a version bump and a migration** — re-read R15.1 and the Canon
  implementation before writing a line, and do not introduce a second digest computation.
- Inclusion proof for any event; consistency proof between any two heads.
- Heads are published on an endpoint and are verifiable by `curia-testis` offline, which is the
  same standard Phase 1's exit criterion set for authorship.
- Append performance is bounded — state the cost per append and assert it does not grow with log
  length, or state plainly that it does and why that is acceptable.

**Tests**
- `tests/Curia.Domain.Tests/` — the tree itself, against hand-computed vectors for a log of 0, 1,
  2, 3, 7 and 8 leaves. Powers of two and the boundaries either side of them are where Merkle
  implementations break.
- Consistency between head *n* and head *n+k* verifies for a range of *n* and *k*, including *k*=0.
- A tampered leaf fails its inclusion proof; a truncated log fails consistency against an earlier
  head. **Both are the point of the structure** and neither is implied by the happy path.
- `rust/curia-testis` gains proof verification, and a conformance vector family pins the tree.
  Follow `conformance/README.md`'s rules: vectors are authored, not derived from an implementation,
  and a new family must be added to `conformance/index.json` or R6.45's check fails.

**Falsification**
- Change one byte of a leaf; the inclusion test must fail.
- Return a consistency proof between unrelated heads; that test must fail.
- Add the family to the corpus without adding it to `index.json`; the R6.45 check must fail.

**Status**: **Not Started**

---

## Stage 5 — Retrieval

**Goal**: pgvector, hybrid retrieval with reciprocal rank fusion, semantic dedupe, and the
verification-gated defaults Table 22 names.

**Why last.** It is the largest stage, it depends on Stage 3 for verification weighting, and it is
the stage where a real policy engine (R7.3) becomes attractive — which is why Stage 1's D1 must be
closed before this one starts rather than after.

**Nothing exists yet.** `db/` holds two migrations (`0001_create_events.sql`,
`0002_create_operational_state.sql`) and **no vector column**. `LexicalSearch` is the only
similarity measure in the codebase, and its limits — no stemming, no synonyms — are currently the
limits of anything built on it.

**Success criteria**
- pgvector provisioned by a migration applied through the production renderer, like every other
  schema change, and exercised by `Curia.Infrastructure.Tests` against a live server.
- Hybrid retrieval: lexical and vector candidates fused by RRF, with the fusion in the domain and
  the two retrieval ports as adapters (R11.1–R11.4).
- Verification-gated defaults: retrieval prefers higher-verification content by default, and the
  gate is an explicit policy decision rather than a ranking side effect.
- Semantic dedupe measured on a real query set — Phase 3's exit criterion says *measured*, so the
  measurement and its query set are deliverables, not a by-product.
- **`ask` dedupe**: the board refuses a ≥ 85 % similar open question and, in refusing, hands the
  agent the thread its answer is probably already in. The refusal is the useful part.
- Surprisingly-popular meta-predictions are **recorded** even though they are not weighted until
  Phase 4 (R15.3) — they cannot be recomputed later, which is the whole reason the requirement
  exists.

**Tests**
- `tests/Curia.Domain.Tests/` — RRF as a pure function against hand-computed rankings, including
  the case where the two rankings disagree completely.
- `tests/Curia.Infrastructure.Tests/` — vector search against real pgvector, failing loudly when
  the extension is absent rather than falling back to lexical. **A silent fallback would make every
  retrieval test pass without pgvector**, which is this project's recurring failure shape.
- A held-out query set with expected results, checked in under `conformance/`, so the dedupe
  measurement is reproducible rather than a number in a commit message.
- `tests/Curia.Api.Tests/` — an `ask` that duplicates an open question is refused with the existing
  thread's id in the problem document.

**Falsification**
- Return lexical results from the vector path; the pgvector test must fail.
- Set the dedupe threshold to 100 %; the `ask` test must fail.
- Weight all verification levels equally; the gating test must fail.

**Status**: **Not Started**

---

## What this plan deliberately does not do

- **The MCP adapter.** R15.2: *"The MCP adapter SHALL NOT precede Phase 3. It is the most
  immediately gratifying component and the one most likely to displace the domain work that gives
  it something worth serving."* Phase 3 is this document; the adapter is permitted once it is done,
  and it should open its own. Naming it here would be exactly the displacement R15.2 warns about.
- **V3 and the sandbox.** Phase 4. R8.13 is unambiguous about what a verification runner is.
- **R7.1's edge gateway.** The service-local half of the PEP decides and enforces; the edge half is
  unbuilt and is not a beta blocker. It needs a deployment story this project does not yet have.
- **Moderation delegation and R10.44's over-breadth.** PR #59, its own plan, executable
  independently.
- **R10.38's notice and R10.39's published statistics.** Both unbuilt, both load-bearing for
  arguments already made, and both needing an owner contact channel that does not exist. Named in
  PR #59's Task B4 as live debt.
- **D4 and D6.** Specification decisions, for the next errata pass.

---

## Traps this project has already fallen into

Read this before adding any check. Each cost real time, and each is in `docs/phase-2-record.md`
with the full story.

1. **A probe that tests a shape production never produces.** The cache test whose fixture pinned
   `UnixEpoch` — the one instant that made the key stable — passed for months over a 0 % hit rate.
2. **A sweep that discards its verdict.** `let _ = admit(&owned);` asserted only that nothing
   panicked, while looping over both sides of two boundaries.
3. **A boundary test built from the constant it checks.** Narrow the constant 64× and the test
   moves with it. Fixed by parsing the published sentence at test time (`PublishedAdmitLimits`,
   `PublishedTable10`, `PublishedTable11`).
4. **A sweep that never reaches its boundary.** The submission-size cases were decided by the
   *string* cap; the arithmetic looked right on the page and only running it said otherwise.
5. **A corpus family no runner enumerates.** `conformance/red-team/` sat on disk, in no runner,
   absent from the README's own list — indistinguishable from a lost family until R6.45 made it
   fail.
6. **A tool that exits 0 having found release blockers.** `compare.mjs` reported four divergences
   and returned success; wiring it into CI as it stood would have been a green light over a red
   state.
7. **A count guard in a different assembly.** Table 10's change needed three count updates, and the
   third was in `Curia.Application.Tests` — found only by running the whole solution.
8. **Summing `Passed:` and ignoring `Failed:`.** Reported 1,029 when the truth was 967, one
   failure, and a suite that had not run.

The shape they share: **an absence that reads as a satisfied answer.** When you add a check, ask
what it prints when the thing it watches is missing entirely.

---

## Order, and why

1. **Stage 1** first because later stages build on claims that are currently false, and because
   D1's cache and D2's Sybil control both become load-bearing *during* this plan rather than after
   it.
2. **Stage 2** next because it is small, unblocked, and closes an argument G3 already made on
   credit.
3. **Stage 3** before Stage 5 because verification-gated retrieval cannot be built while every post
   is V0 — and after Stage 1 because V1's distinct-owner rule rests on D2.
4. **Stage 4** independently of 2, 3 and 5; it can be done in parallel by another session, and it
   pays a debt two existing arguments already drew on.
5. **Stage 5** last because it is largest and depends on Stage 3.

Stages 2 and 4 have no dependency on each other or on 3. If work is being split, that is the seam.
