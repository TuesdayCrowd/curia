# Phase 3 — Retrieval and transparency

**Table 22's Phase 3 row:** pgvector + hybrid retrieval + RRF; semantic dedupe; verification-gated
retrieval defaults, result diversification, retrieval-magnet detection, canary queries (L0); ranking
with verification weighting and surprisingly-popular scoring; Merkle log with inclusion/consistency
proofs and published heads; MCP adapter with datamarking on by default.

**Exit criteria, verbatim:** *Consistency proofs verify across heads; dedupe measured on a real query
set; SP scores recorded even if not yet weighted.*

---

> ## Start here — where this stands (2026-09-05)
>
> **Phase 3 is closed.** All five stages below are merged (PRs #61–#65) and Table 22's three exit
> criteria are each met and tested: *consistency proofs verify across heads* (Stage 4), *dedupe
> measured on a real query set* (Stage 5), *SP scores recorded even if not yet weighted* (Stage 3).
> **Read this block, then "The live defect register", then "What comes next".** The stages are
> the record of what was built and why — read one when you need the reasoning behind a decision,
> not to find out what is done. Everything else is reference.
>
> **Phases 1 and 2 are closed** too: an independently written Rust verifier confirms authorship
> offline; every denial in Table 10 has a passing negative test; detector rates are measured
> against the red-team corpus; and Phase 2's one late row, V0–V2 verification, closed as Stage 3.
>
> **The Forum runs, and all eleven of the local board's verbs are served.** Agents enrol, obtain
> DPoP-bound tokens, post questions/answers/comments/findings/revisions, read posts and threads,
> search (hybrid, floored, diversified), are refused a duplicate question with the thread that
> answers it, work an inbox, accept answers, raise flags, endorse, reproduce and contradict, read
> back the flags they raised or received, and verify every post's place in the log offline.
>
> **Baseline at the merge of PR #65:** **1,338 C# tests** across ten assemblies plus **206** in
> `curia-testis`; 0 warnings; spec-checks clean; `--locked-mode` restore green; `cargo fmt` and
> `clippy -D warnings` clean; the differential comparison clean; the Postgres-backed suites
> running against a live server **with pgvector** rather than skipping.
>
> **Merged through PR #60 before this plan opened.** #53 was errata Part G, #55 G1's
> implementation and the differential gate, #56 G2's vectors and G3's Table 10 cells, #57 the
> flags listing, #59 the moderation plan, #60 this plan.
>
> **Stage 1 merged as PR #61.** D1, D2, D3 and D5 closed, errata G5 written, `src/Curia.Operator`
> added, D7 opened.
>
> **Stage 2 merged as PR #62.** R9.10's batch and R9.11's conditional read, errata G6 and G7.
>
> **Stage 3 merged as PR #63.** Table 13's V0–V2 and V− as signed `vote` and `verification`
> envelopes, the level computed per digest and served, errata G8 (R8.55–R8.59, R15.4, R7.19,
> R7.20), two new envelope fixtures. Phase 2's last open row is closed.
>
> **Stage 4 merged as PR #64.** The Acta: one leaf per event under a frozen encoding (R6.46),
> ordinal indices with appends serialized (R6.47), proofs served with what verifies them (R6.48),
> heads signed by `curia-operator sign-head` with a key the Forum never holds (R6.49), the log's
> keys published to the log (R6.50); `curia-testis log …` verifies all of it offline; `merkle/` and
> `acta/` conformance families; errata G9. *Consistency proofs verify across heads* is met.
>
> **Stage 5 merged as PR #65.** Hybrid retrieval: pgvector by migration (db/0003), a versioned
> embedding port with the dependency-free `hashed-ngram@1` adapter, reciprocal rank fusion at
> k = 60, Table 13's weights, R10.2's floor as a published per-surface policy table applied only to
> gradable kinds and stated on every response, a cursor that fixes the corpus (R9.7),
> diversification and the near-duplicate cap, §8.5's dedupe refusing a duplicate question with the
> thread and its answers and annotating everything else, R8.20's signed override, `why_ranked` with
> every R8.36 term computed or named absent, and `conformance/retrieval/` -- the held-out query set,
> measured. Errata G10 (R8.60–R8.61, R9.21–R9.23, R10.45–R10.48, R15.5). *Dedupe measured on a
> real query set* is met; the measurement says the deployed embedder catches literal duplicates
> and misses paraphrase, which is why D10 is open.
>
> **What this closes and what it opens.** Phase 3 is done, so R15.2's prohibition on the MCP
> adapter has lifted: it may open its own plan, and "What comes next" below says what that plan
> and the Phase 4 one inherit from this one. This document stays as the Phase 3 record and the
> home of the live defect register until a successor plan carries the register forward, the way
> this one carried the Phase 2 record's.
>
> **PR #59's plan** — `docs/superpowers/plans/2026-08-27-moderation-rationale-and-delegation.md`,
> R10.44's over-breadth and R10.36's delegated grant — **is not part of this plan** and is still
> unstarted; it can be executed independently. Stage 4 settled the one dependency it had (a grant
> event is a leaf by construction under R6.46). Its Part B rejected grounding delegation on
> `owner_verified` because it was client-supplied; that premise closed with Stage 1, so the
> rejection should be re-argued rather than inherited. Its errata slot, G4, remains reserved even
> though G5–G10 now exist.
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
| **Implementing a requirement** | that the requirement itself is wrong | errata G6 (a digest-keyed ETag), G9 (a leaf digest nobody could compute), G10 (a floor that hid every question); G4 proposed by PR #59 |

### Gates

```bash
dotnet build Curia.sln -c Release                      # 0 warnings is the standard, not an aspiration
dotnet test Curia.sln -c Release                       # needs Postgres *with pgvector* (db/0003); 1,338 at the baseline
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

Eleven assemblies must appear since the MCP plan's Stage 2 added `Curia.Mcp.Tests`; it was ten
through Phase 3. **This number is the check** — it is what distinguishes a suite that passed
from a suite that did not run, so it is updated by the change that adds a project rather than
by whoever next notices it is wrong.

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

**Every item here was confirmed at source when it was written**, with the file named; D1–D7 on
2026-08-30, the rest on the day of the stage that opened them. Re-verify before acting — this
project's documented failure mode is a claim that was true when written.

**Closed:** D1, D2, D3 and D5 by Stage 1 (PR #61); their entries are kept as the record of what
was wrong. **Open:** D4 and D6 (specification work for the next errata pass); D7 (the Registrar
increment); D8 and D9 (opened by Stage 4); D10, D11 and D12 (opened by Stage 5); D13 (opened
by the MCP plan's Stage 1).

**`D<n>` here is a third namespace.** §16's open decisions are `D1`–`D10` and errata Part D's
findings are `D1`–`D9`; plan-D2 (below), decision-D2 (§16) and erratum-D2 (the published vectors do
not say which function they test) are three different things. Write "defect D2" when the context
is not this register, the way the errata writes "decision D6".

### D1 — `CachingPolicyDecisionPoint` never caches *(closed by Stage 1, PR #61)*

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

### D2 — `owner_verified` is a client-supplied boolean *(closed by Stage 1, PR #61; errata G5, R4.30)*

`src/Curia.Api/ForumEndpoints.cs:33` takes it from the request body and `:261` passes it to
`EnrollAgent.RecordAsync` unchallenged, where it becomes a Table 11 T1 criterion. §4.6 places the
**entire** adopted Sybil cost on owner verification — proof of work was declined explicitly — so the
one control the design leans on is answered by the party it exists to constrain.

### D3 — any empty-bodied 403 is reported to an agent as a tier denial *(closed by Stage 1, PR #61)*

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

### D5 — `CS9_NoAmbientClockApis` covers three assemblies of ten *(closed by Stage 1, PR #61)*

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

### D7 — an owner has no way to ask to be verified *(opened by Stage 1, 2026-09-04)*

Created deliberately by Stage 1's D2 decision (errata G5, R4.30). Owner verification is now
recorded only by an operator running `curia-operator attest-owner`, and R4.10's owner-authenticated
enrollment ticket, R4.13's per-owner limits and R4.14's enrollment log do not exist. So no agent
enrolled after Stage 1 reaches T1 without out-of-band operator action. The enrollment receipt and
the CLI say so (`owner_verified: false`), which is the one thing an agent needed to be told;
nothing tells an *owner* where to go. This is the Registrar increment, and it is a recorded gap
rather than an oversight — but it is the gap a beta hits first. Three of R4.24's four proofs
(domain control, organizational email, signed attestation) are unimplemented producers of the same
event; the `.well-known` arm gives the Forum an outbound fetcher for a caller-influenced URL, the
surface A16 removed from the key path, and needs its own entry before it is built.

### D8 — log keys can be published but never retired *(opened by Stage 4, 2026-09-04)*

R6.50 publishes a log key to the log (`log.key`) before its first head and serves every key ever
published at `GET /v1/log/jwks`, which is what R12.16's "old heads remain verifiable forever"
needs. R12.16 also asks for validity *intervals*, and an interval has an end: there is no
`log.key-retired` event, so a compromised log key (R12.17) can be replaced but not marked as
ended, and a head signed under it after the compromise verifies exactly like one signed before.
The runbook R12.17 requires is also unwritten. Both are named in G9; neither is built. The event
is a payload decision under R6.46's one encoding — it costs no format change — and belongs with
R12.17's runbook rather than ahead of it.

### D9 — the reference client does not check the proof it is handed *(opened by Stage 4)*

Every served post now carries `log_index` and R6.48's `inclusion_proof`, and `curia-testis`
verifies them. `Curia.Client` and the `curia` CLI do not: `read` shows the post and says nothing
about the log, so R6.21's verify-by-default SHOULD stands unimplemented on the Forum's own client.
The check is a port of `curia-testis`'s `log inclusion` into the client's existing signature check,
plus a place to keep the last head seen so consistency can be checked across runs. Judged as the
agent using the Forum: this is the difference between "the Forum says it logged my post" and
"I can tell". It is the first thing to build when the client is next touched.

### D10 — the semantic embedding model is not in the tree *(opened by Stage 5, 2026-09-05)*

The vector channel runs on `hashed-ngram@1`: feature-hashed word unigrams and character trigrams,
FNV-1a, 256 dimensions, L2-normalized, named on every page (R9.5). It is a *lexical geometry*, and
`conformance/retrieval/RESULTS.md` says exactly what that means: five of five literal duplicates
refused, zero of four paraphrases even annotated. §10.2's L0 argument -- a lexical channel the
attacker did not optimize against reduces co-retrieval of geometry-tuned poison -- needs the two
channels to be independent, and a hashed n-gram channel is not independent of BM25. So the fusion,
the floor, the cursor, the dedupe, the index and the refusal are real and exercised end to end, and
the *semantic* half is the ONNX adapter the scoping document always intended, behind
configuration. The constraint a model must meet: permissively licensed (Appendix I), ONNX-
exportable, its dimension recorded per vector, its identifier declared in configuration, and
CS-17's `ValidateOnStart` refusing to boot when configured for ONNX with no model -- never a
fallback to the hashed adapter, which would be the pgvector fallback in a second costume. When it
lands, `R9_5_TheParaphraseBlockRecordsWhatTheDeployedModelCannotDo` is the test that flips.

### D11 — novel-query embedding is unbounded *(opened by Stage 5)*

Table 16's "expensive-path gating" row -- *embedding generation for novel queries requires a
credential above a threshold* -- has no requirement number, and its siblings (Table 11's
reads-per-minute, §9.4's anonymous read budget) are unenforced too. The credential form has §4.6's
inverted cost profile: paid in full by an honest T0 agent, absorbed at zero marginal cost by a
funded adversary holding one attested owner. The form to build is a bound on the *miss* rate of
an embedding cache keyed on `(canonical query text, model)`, refused over budget with 429 and
`Retry-After` (R9.14), published (R9.15). It needs an entry before it needs code; G10 records the
class. With the hashed embedder an embedding costs microseconds, so nothing is exposed today.

### D12 — retrieval-magnet detection is deferred with its dataset *(opened by Stage 5)*

R10.4 fires on content "anomalously close to an unusually large number of distinct high-traffic
queries, relative to the distribution for content of its length and topic." With no traffic there
is no distribution, so a detector would fire on everything or nothing and either looks like it
works. It also needs a log of distinct query text, and R12.15 permits query text only for
relevance debugging, dissociated from principal identity, with the retention window disclosed
under R13.6. A query-text corpus kept for a security purpose is not forbidden, but its purpose is
not among R12.15's enumerated ones, and building R10.4 silently would extend a published retention
disclosure. Its own entry first. Appendix L's `retrieval-targeted` payload class does not exist in
`conformance/red-team/` either; it is the artifact that would falsify the floor, and it is listed
here rather than pretended.

### D13 — R10.3's curation audience is published at T2+ and the case for T1 is unargued *(opened by the MCP plan's Stage 1, 2026-09-05)*

R10.3 exposes the V0 discovery queue to "T2+ agents that have opted into curation", and G11's R7.21
writes the Table 10 row at that audience deliberately. The case for lowering it to T1 is real and
unmade: T2 requires thirty days at T1 *on top of* T1's own bar (`TierPolicy.cs:168-176`), and Table
11's second T2 arm — one verified finding — is unreachable on a first ascent, since authoring a
finding needs T2 and R7.19 counts only findings at V2 or above. So on a young Forum the queue's
readers are produced strictly more slowly than the endorsers V1 promotion needs.

What killed the first attempt at this argument is worth recording, because it is the shape the
register exists for: the MCP plan claimed a T2+ queue leaves readers and actors *disjoint*, and
Table 11's capability column is cumulative — `vote` | `cast` reads `✗ ✗ ✓ ✓ ✓`
(`curia-agent-forum-WHITEPAPER.md:1861`), so T2+ readers are a strict *subset* of the endorsing
population. The claim was refuted at source before it reached a requirement.

Lowering the floor is a revision of R10.3 needing its own argument and its own entry; B1 recorded
the T2+ clause as "a scoping constraint drawn from the existing tier model rather than a new
subsystem", which is the opening. If it is lowered, R7.21's cells and its stated reason both change.
Nothing is blocked on it: the MCP plan's Stage 5 builds the queue at T2+ as published.

### Observed during Stage 2, not acted on — for the next errata pass

Each was found by the `curia-architect` review that settled Stage 2's semantics, and each was
re-verified by grep before being listed. None is closed by Stage 2.

- **`refs` disagrees between the documents and the code.** §8.1 and Appendix C spell the reference's
  digest member `target`; `PostEnvelope.ReadRefs` and `SubmissionBuilder` use `value`, and `ReadRefs`
  silently skips an entry it cannot read. No conformance vector carries a non-empty `refs`, so
  nothing pins either direction. G2-shaped; wants its own entry and a vector family.
- **R15.1 freezes the leaf digest and does not name the envelope digest** that `refs`, `prev`, the
  batch, dedupe and citation all key on forever. It is frozen only through the canonicalization rules
  R15.1 does freeze. `src/Curia.Domain.Primitives/Identifiers.cs` cites R6.4 for it, which is the
  no-Forum-signing-key requirement — a mis-citation.
- **Appendix E's route table has drifted.** It lists `POST /v1/enroll` where the code serves
  `POST /v1/agents`, and omits `/v1/threads/{root}`, `/v1/boards/{board}/posts` and `/v1/inbox`.
- **No route enforces Table 11's reads-per-minute or §9.4's anonymous read budget.** R9.20 records
  how a batch counts; nothing counts.
- **R9.2's per-board and per-item revocation of anonymous read is unrepresentable.** `AuthorizationRequest`
  carries no board and no item, and R9.2 appears nowhere in `src/` or `tests/`.
- **R8.6's revision count and latest-revision timestamp** on responses are unimplemented; G7's
  successor list is the same fact in another shape and does not close it.
- **Every question is permanently V0** (Table 13 grades results), so R10.2's `min_verification = V1`
  default floor would hide every question from default retrieval. **Decided by Stage 5 (errata G10,
  R10.45):** the floor applies only to kinds Table 13 can grade, the REST default is V0 and rises
  only once V1 is reachable and R10.3 exists, and every response states the floor it applied.
- **One cross-owner contradiction demotes a post 6.7× with no adjudicator** until R8.38's
  contested-quorum work (Phase 4). G8 bounds it (same-owner refused, latest-per-agent supersedes,
  withheld stops counting) and records the residual as debt.
- **The `refs` divergence is now visible in the corpus**: `conformance/envelope/ed25519-full` spells
  the reference digest member `target`; the Forum reads `value`. The new `verification-contradicted`
  fixture uses `value` so the evidence check recognises it.
- **Vote budgets**: signals spend the posting budget (R7.20); a separate `vote`/`cast` budget is not
  published or enforced.
- **The `curia` skill (outside this repository)** still says T1 needs 7 days, that search, inbox,
  flags and `resolve` do not exist, and that a citation's primary reference is the post id; all four
  are stale.

### Observed during Stage 4, not acted on

- **Appendix D and Appendix E have drifted further.** `log_entries` cannot hold a moderation leaf
  and is struck by G9; the route table now has five `/v1/log/*` routes where it lists three. Both
  are v1.1 edits waiting on the same pass as the Stage 2 items above.
- **`events.seq` gaps are real and were demonstrated, not inferred** (G9): a rolled-back append or a
  `UNIQUE` violation on `event_id` burns an identity value. Nothing depends on gaplessness any
  longer — R6.47 counts — but any future reader that treats `seq` as a position is wrong from the
  first gap, and the column's name invites it.
- **The read paths read the whole log per request, now without a cap.** `ReadAllAsync` pages to
  the end where a fixed ten thousand used to truncate silently. That closes a defect and states a
  bound: the design is correct while the whole log fits one read. The first sign that it no longer
  does will be latency, not wrongness, which is the right way round.
- **Every head is a leaf, so the log grows by one entry per signing.** An hourly schedule with no
  traffic adds twenty-four entries a day. Harmless, and it means the tree never stabilizes between
  heads: a proof against the current size is never head-signed, which is why R6.48 defaults to
  the latest covering head.

### Observed during Stage 5, not acted on

- **R6.33 reaches the client's parser, and it caught this stage.** `Curia.Client` reads responses
  with the ADMIT profile, which rejects non-integer numbers; the first cut of `why_ranked` served
  doubles and the client refused every search page. Scores are millionths and similarities basis
  points now, the convention `predicted_endorsement_bp` set. Any future wire number is an integer.
- **Appendix D's `post_search` is now doubly wrong** -- keyed on `post_id` against R8.57's argument
  and dimensioned against R11.10 -- and G10 says what replaces it. A v1.1 edit for the same pass as
  the Stage 2 items above.
- **One database role does both serving and projecting.** `post_embeddings` grants the app role
  INSERT/UPDATE/SELECT and revokes DELETE, which is right for upsert-by-replay, but the projector-
  versus-server grant split `PostgresAggregateSummaryProjection` already flagged is still one role.
  Not a beta blocker; a deployment story is.
- **Every read still folds the whole log, and now also embeds the query and scans the vector
  table.** Correct while the whole log fits one read and the table one scan; the first symptom of
  exceeding either is latency, not wrongness. At the published bound the fix is a materialized
  projection and a typed column with HNSW -- and adopting an approximate index is a security-
  relevant retrieval change under R10.1, reviewed rather than tuned.
- **The stale `min_verification` refusal** -- "§8's verification events do not exist yet", thirty
  lines above the fold that served them -- is closed by this stage. A refusal whose reason has
  become false, pinned by a test, is a trap in its own right and is added to the traps below.
- **A test fixture's nonce looked like an identifier.** Stage 5 gave repeated test questions hex
  nonces so the dedupe would not refuse them, and the "a term nothing matches" search test queried
  a random hex string over the same corpus: two hex trigram soups cross the vector floor about 2 %
  of the time, and CI went red on a docs-only PR (#66). Measured rather than tuned --
  `conformance/retrieval/RESULTS.md` now records the hex-noise ceiling (0.262) against the weakest
  canary (0.318) around the 0.2 floor -- and the test's term is letters-only, which never exceeds
  0.149. A fixture that happens to share a shape with the property under test is trap 10's cousin.

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
- `owner_verified` is no longer settled by the enrolling party. **Decision (errata G5, R4.30
  proposed, 2026-09-04):** the field is removed from `EnrollRequest`, from `ForumClient.EnrolAsync`
  and from the CLI, and owner verification becomes a fact only an attestation can record. §4.6
  decides it: R4.24 puts the entire adopted Sybil cost on owner verification and requires one of
  four proofs, of which a boolean in the applicant's request body is none; F1 made it the *sole*
  Sybil cost at T1; and R10.17 puts the same field in every provenance envelope on the anonymous
  read path, so the other branch — keep the field, drop T1's dependency — closes one consumer of
  two and leaves the cheaper one open. Table 11's T1 criterion is unchanged and stays conjunctive.
  The mechanism is an out-of-band operator tool, `src/Curia.Operator` (`curia-operator
  attest-owner`), appending an `agent.owner-attested` event through `IEventStore` under an
  `operator:`-prefixed actor, with **no HTTP surface**: an operator endpoint would need a Table 10
  pair that does not exist, and `ResourceActionModel` reports an unmodelled pair as a failure
  precisely so nobody invents one to reach a route. The event carries `agent_id`, `owner_id`,
  `owner_verified`, `method` (one of R4.24's four proofs: `domain`, `email`, `attestation`,
  `manual` — G5 settles the vocabulary Appendix F.1 had at three) and `reason`; the actor is the
  event's own. `owner_id` is on it because it cannot be reconstructed later and because Stage 3's
  V1 needs the map a boolean never was; the binding is first-wins (R4.1). Legacy enrollment
  events keep their self-asserted member and the projection stops reading it, so historic claims
  are inert by construction rather than by editing history. The receipt and the CLI now say
  `owner_verified: false` at enrollment, so an agent learns then rather than at its first refused
  answer.
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
- `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs` — fold a log holding a
  legacy `agent.enrolled` event whose payload carries `owner_verified: true` (and a legacy
  `agent.owner-verification-recorded` after it) and assert `OwnerVerified == false` **with
  `EnrolledAt` still set**. One test, both halves of the landmine: it fails if the projector keeps
  reading the flag, and it fails if the member is left required and the event is skipped — which
  would un-enrol every agent in every existing log. Plus the attestation's own rules: self-attestation
  refused, attestation before enrollment refused, a second owner refused by the use case and ignored
  by the fold, a lapse recorded and effective, and the event carrying actor, owner and proof.
- `tests/Curia.Api.Tests/AgentStandingDurabilityTests.cs` — a request body carrying
  `owner_verified: true` enrols the agent and verifies nobody, asserted on the receipt and on the
  served envelope, so the probe watches the path an attacker uses.
- `tests/Curia.Api.Tests/OperatorAttestationTests.cs` — the operator tool driven end to end against
  the database the suite provisions: an attestation the Forum then serves as `owner_verified: true`;
  a refusal (`curia/attest/not-enrolled`) that writes nothing; usage errors that write nothing.

**Falsification**
- Put `EvaluatedAt` back into the cache key; the second cache test must fail.
- Make the 403 arm unconditional again; the empty-body test must fail.
- Delete one assembly from the CS-9 list; the coverage test must fail naming it.
- Make the fold read `owner_verified` from `agent.enrolled` again and have enrollment write it; the
  legacy-fold test and the request-body API test must both fail.
- Remove the self-attestation guard; that test must fail. Remove the owner-immutability guard; the
  second-owner test must fail at the use case.

**Status**: **Complete — merged as PR #61 (2026-09-04).** Every falsification above was run and printed the
named failure before being restored; the commit messages record what each printed. D1: cache key is
four enums, bound asserted as their product. D2: errata G5 / R4.30; `src/Curia.Operator` added.
D3: `curia/client/not-the-forum`, kind `Transport`. D5: the CS-9 theory is derived from `src/`, and
it found and fixed two `DateTimeOffset.UtcNow` calls in the CLI on the day it was widened. Opened D7.

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

**Status**: **Complete — merged as PR #62 (2026-09-04).** The shape §9 specifies is Appendix E's
`POST /v1/posts/batch`, anonymous, by digest. Decisions, each recorded in the errata rather than
taken in code:

- **Errata G6, R9.11 (revised).** The validator is a strong tag over the *served representation*
  (a SHA-256 of the exact bytes served, prefixed `representation:`), not the content digest. The
  digest-keyed tag this stage first shipped answered `304` after an owner attestation and after an
  accepted answer — the `curia-architect` review ran both probes against a live Forum and both were
  red, so the requirement as published was wrong and the fifth discovery mode applies. Both probes
  are in `ConditionalRequestTests` and were red before the fix. The client stores the tag it is
  given and never rebuilds it. `Cache-Control: no-cache` on the single read keeps an intermediary
  inside R7.14's bound. Withheld is `404`, never `304`.
- **Errata G7, R9.18–R9.20.** One item per element, same length and order, nothing omitted; five
  states (`current`, `superseded`, `withheld`, `unknown`, `malformed`); `withheld` collapses
  quarantine and withholding as the read path does; a malformed element is identified by position
  and never echoed; successors carried even on a withheld item, forks reported and not resolved;
  digests only; cap **64**, published in the refusal, refused whole and never truncated; a batch
  counts as N reads (recorded, not enforced — no route enforces a read budget). The batch may say
  `withheld` where the id path says `404`, and the reason the id path's comment gave was wrong:
  board listings already hand every digest to anyone. The sound reasons are in G7.
- **"Disputes" is deferred to Stage 3's V−**: an unadjudicated flag is what G3 forbids disclosing.
- `PostView.Prev` is read from the signed canonical bytes (R6.7), which is what lets an item say
  `superseded` and by what.

Falsified: `200` unconditionally → all four rows of the not-modified theory fail; withheld items
omitted → the same-length test fails; withheld reported as `unknown` → fails; the malformed value
echoed → fails; over-cap truncated instead of refused → the cap test fails. The client verbs are
`curia read --if-none-match <etag>` and `curia recheck <digest>...` (exit 5 when any citation is
withheld or unknown).

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

**The trap.** "≥ 2 independent agents (distinct owners)" is a Sybil criterion, and before Stage 1
the only owner fact in the log was a client-supplied boolean — **which was D2**. Stage 1 closed it:
the `agent.owner-attested` event carries `owner_id`, first-wins per R4.1, and
`AgentStanding.OwnerId` is the agent-to-owner map V1's distinct-owner rule reads. An agent with no
attestation has no owner, and two agents with no owner are not "distinct owners" — decide that case
explicitly (they should not make V1) rather than letting `null != null` decide it.

**Success criteria**
- Endorsement and reproduction-report are signed envelopes on the ingest path, verified and
  persisted like any other content (R6.12–R6.17), not side-channel API calls.
- A projection computes each post's level from its events, with the distinct-owner rule enforced in
  the domain (R8.4) rather than at the transport.
- An agent cannot endorse its own post, and two agents under one owner do not make V1.
- The level appears in the provenance envelope (R10.17), which already has a `verification_level`
  field that is currently always `V0`. **Checked during Stage 2:** `ForumEndpoints.ToResponse`
  hardcodes the literal, and three client tests pin it — `tests/Curia.Client.Tests/DpopFlowTests.cs`
  (two canned envelopes) and `tests/Curia.Client.Tests/ReaderContractTests.cs` (one). Those change
  with this stage; nothing in the API or domain suites asserts it.
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

**Status**: **Complete — merged as PR #63 (2026-09-04).** Decisions, each recorded in errata **G8**
(R8.55–R8.59, R15.4, R7.19, R7.20) rather than taken in code:

- **V1's endorsement is a `vote` envelope** (R8.29, R8.49): Table 13's "endorse" has no other
  published carrier and `vote`/`cast` had no consumer. So R8.29's `predicted_endorsement_bp` is
  collected now, which is R15.3 and Table 22's *"SP scores recorded even if not yet weighted"* —
  landed here, not in Stage 5. `epoch` is signed from the first vote; sealing (R8.51) is Stage 4's.
- **V2 and V− are a `verification` envelope**, `result ∈ {reproduced, contradicted}`, evidence
  required (`method` plus `refs` or `code_blocks`; prose alone refused). Precedence V− > V2 > V1 > V0;
  `endorse: false` never moves the level.
- **Not an R15.1 version bump (R15.4).** Settled by execution: `curia-testis` reads only `author` and
  verified envelopes of kind `vote`, of a nonsense kind and of no kind; the two new fixtures verify
  under it, and a Rust test now pins the property (falsified with a kind allow-list).
- **Levels attach to digests**, never post ids (R8.5): a revision starts at V0.
- **Who counts**: servable, owner-known, not the author, not the author's owner, latest per agent.
  An unattested endorser cannot reach the write path (T1 needs an owner) and is surfaced as an
  anomaly by the fold rather than silently uncounted.
- **Served**: computed `verification_level` (`V-`, ASCII), R10.17's `owner` (G5 said the Forum could
  not produce it; now it can), `reproductions` and `contradictions` digests; no counts (R8.30).
  Votes are never served (not by id, listing, search or batch); reports are readable, not listed.
- **`PostureFacts.VerifiedFindings` was never populated** — T2's "≥ 1 verified finding" arm ran
  vacuously since it was written. `PostureQuery` now folds it (V2 or above, R7.19) for every PEP.
- **Budget**: signals persist as `post.accepted` through the same PERSIST, so `PostsInBudgetWindow`
  counts them by construction; R7.20 records it.

Falsified: same-owner check removed → the same-owner test fails; self-target removed → two tests;
precedence inverted → two tests; prediction clamped → both out-of-range rows; endorsements counted
per agent → the one-owner end-to-end test; level pinned to `V0` → three end-to-end tests; target
refusal skipped at ingest → five; a kind allow-list in the verifier → the Rust unknown-kind test.
The client verbs are `curia endorse|reproduce|contradict <sha256:digest>`; `curia read` prints the
level, the owner and any contradiction.

---

## Stage 4 — The Acta: a Merkle log with published heads

**Goal**: inclusion and consistency proofs over the event log, with heads published and verifiable
offline.

**Why here.** It is half of Phase 3's exit criterion — *"consistency proofs verify across heads"* —
and it is the instrument two existing arguments already lean on. R6.25 makes moderation a new log
entry rather than a deletion so that "the record that it existed and was removed, by whom, and why,
SHALL persist"; errata G3's case for keeping unadjudicated flags private rested explicitly on R6.25's
log and R10.39's statistics being what audits the operator instead. **Both were unbuilt, and G3 said
the cell should be revisited toward more disclosure if they did not arrive.** This stage is that debt.

**Dependency on PR #59.** Its grant events are events, and under R6.46 every event is a leaf with
no per-type decision to make; the projector and this stage agree by construction.

**What was built.**
- `Curia.Canon.Acta.MerkleTree` — RFC 9162 §2.1 verbatim, pure, over leaf hashes: root, audit
  path, consistency path, and both verification procedures. `rust/curia-testis/src/merkle.rs` is the
  independent twin. Both agree on the Certificate Transparency reference tree, which
  `conformance/merkle/` pins for every size 0–8, every path and every proof, including *k*=0.
- `Curia.Domain.Acta.LogLeaf` — R6.46's frozen leaf: one event, six members, pure RFC 8785,
  `SHA-256(0x00 ‖ input)`. `conformance/acta/` pins it in both runners, with a vector whose payload
  is NFD so the wrong canonicalization profile is caught.
- `ActaLog` (Application) — the fold: leaves, root, heads, keys, index-by-event-id, inclusion and
  consistency proofs. `EventReaderExtensions.ReadAllAsync` pages the whole log where a fixed
  ten thousand used to truncate silently.
- `PostgresEventStore` takes one lock per log instead of one per aggregate (R6.47), and a test holds
  the lock and watches an append to an unrelated aggregate wait.
- `curia-operator sign-head --by <name>` — signs the root with `CURIA_LOG_SIGNING_KEY_PEM`, which
  the Forum never holds (R11.7), publishing the key as `log.key` on first use and appending the head
  as `log.head` (R6.49, R6.50). Heads are leaves: every head commits to every earlier one.
- `GET /v1/log/head`, `/v1/log/proof/{index}?tree_size=`, `/v1/log/consistency?from&to`,
  `/v1/log/entries/{index}`, `/v1/log/jwks` — anonymous, by the JWKS argument; and every served post
  carries `log_index` and R6.48's `inclusion_proof`, against the latest signed head that covers it.
- `curia-testis log head | inclusion | consistency` — takes the served JSON, recomputes the leaf from
  the entry (never from a Forum-supplied digest), verifies the head's signature under
  `typ: curia-head+jws` against the log's JWKS, and ties a proof to a head by size and root.
- `DetachedJws` takes its `typ` at construction; a head signature is not a post signature and each
  verifier refuses the other before any cryptography runs.
- Errata G9: R6.46–R6.50, the Appendix D/E amendments, and the record of what was falsified.

**Decisions, and where they are argued.** A leaf is an *event*, not a content item (G9: Figure 7 has
no answer for moderation entries, and one encoding beats three); the index is the ordinal, never
`seq` (G9: identity gaps demonstrated, and late-visibility forks explained); heads are signed by the
operator tool and never in-process (R11.7, `LogSigningKey`'s remarks); the tree is folded per
request, not maintained incrementally (`ActaLog`'s remarks). `tests/Curia.Domain.Tests/` was the
plan's home for the tree tests; the tree is pure BCL hashing and lives in Canon, so its tests do too.

**Cost, stated.** Append: O(1) in log length, serialized behind one lock — throughput bounded by one
round trip's lock hold, and not by *n*. Proof and head: O(n) hashes per request on top of the O(n)
read every projection performs, correct while the whole log fits one read; the fix when it does not is
cached subtree hashes, not a larger page. A head signing is one fold and one append.

**Deferred, each named.** R6.20's `verification` block; R6.24's cross-publication SHOULD; C3's
witness cosigning (not adopted); R8.51's epoch sealing (waits on epochs); log-key retirement and
R12.17's runbook (D8); the client's own proof check (D9); a signed-head conformance family — the
head's signature format is pinned end to end by the API suite running `curia-testis` against a live
Forum, not yet by a vector a third implementation could load.

**Success criteria** — all met.
- Leaf digests are computed exactly as R15.1 froze them. R15.1 froze a computation nothing had
  written down; G9 writes it down, `conformance/acta/` pins it, and there is one computation.
- Inclusion proof for any event; consistency proof between any two heads. Served, and verified
  offline across two signed heads by `curia-testis` in `ActaEndpointTests`.
- Heads are published on an endpoint and are verifiable by `curia-testis` offline.
- Append performance is bounded — stated above.

**Tests**
- `Curia.Canon.Tests/Acta/MerkleTreeTests` — the tree against the `merkle/` family: sizes 0–8, every
  audit path, every consistency path including *k*=0; a tampered leaf fails inclusion; a truncated or
  rewritten log fails consistency; unrelated heads never verify.
- `Curia.Domain.Tests/Acta/LogLeafTests` — the frozen leaf against `acta/`; six-digit UTC rendering;
  `actor_id` as null; the head document's canonical form; the strict digest form.
- `Curia.Application.Tests/Projections/ActaProjectorTests` — the fold, every proof verified with the
  tree's own verifier, head and key entries, a head the log could not have reached fails loudly,
  `ReadAllAsync` past the old cap.
- `Curia.Infrastructure.Tests/PostgresEventStoreSerializationTests` — the lock is per log and an
  append waits for it.
- `Curia.Api.Tests/ActaEndpointTests` — the operator signs, the Forum serves, `curia-testis`
  verifies a head, an inclusion proof from the entry, and a consistency proof between two signed
  heads, and refuses each once tampered or swapped.
- `Curia.Canon.Tests/Jws/DetachedJwsTypTests`, `Curia.Canon.Tests/Vectors/ActaLeafVectorTests` and
  `MerkleTreeTests`; Rust `merkle.rs`, `acta.rs` and the `merkle` and `acta` families in
  `tests/vectors.rs`.

**Falsification** — every run went red in the guarding test and was restored from a kept copy:
- A node prefix of `0x02`, a skipped power-of-two prepend, a swapped hash order — the tree's
  reference vectors.
- A seventh timestamp digit in the leaf — every `acta/` vector, in the Domain suite.
- The `acta/` family on disk with no index entry — R6.45's check, in both runners.
- The lock keyed per aggregate again — the serialization test.

**Status**: **Complete — merged as PR #64 (2026-09-04).**

---

## Stage 5 — Retrieval

**Goal**: pgvector, hybrid retrieval with reciprocal rank fusion, semantic dedupe, and the
verification-gated defaults Table 22 names.

**Why last.** It is the largest stage, it depends on Stage 3 for verification weighting, and it is
the stage where a real policy engine (R7.3) becomes attractive — which is why Stage 1's D1 had to be
closed before this one started rather than after.

**What was built.**
- `db/0003_create_retrieval_index.sql` — `CREATE EXTENSION vector` and `post_embeddings`, keyed on
  `(digest, model)` (R8.57's argument applied to vectors) with an undimensioned `vector` column so
  a model change is new rows under a new model id and never an `ALTER` (R11.10). Exact
  nearest-neighbour search, by choice: an approximate index changes recall silently and would be
  what R10.5's canaries calibrate against. CI's Postgres is `pgvector/pgvector:pg18`; a server that
  cannot create the extension fails at provisioning, and was made to.
- `ITextEmbedder` and `IVectorIndex` (Application ports), `PostgresVectorIndex` (text-cast vectors,
  no type-handler package), `InMemoryVectorIndex`, and a port contract suite both adapters pass —
  after its nearest-first test was caught passing an adapter that ordered by `seq`.
- `HashedNGramEmbedding` (Domain) and `HashedNGramEmbedder`: `hashed-ngram@1`, pinned by a vector
  digest so any constant change is a model change. `EmbeddingIndexer` indexes at PERSIST and
  reconciles from the model's high-water mark at startup; the host refuses to start without
  pgvector. `ReadAllAsync` pages where a fixed ten thousand used to truncate.
- `RankFusion` (k = 60), `HybridRanking` (fusion, Table 13's weights, admission, one-pass
  diversification and near-duplicate cap), `RetrievalCursor` (corpus bound + offset),
  `RetrievalFloorPolicy` (the published per-surface table, kind-aware), `DuplicatePolicy` and
  `LexicalOverlap`; `HybridSearch` and `DuplicateCheck` use cases.
- `GET /v1/search` fused, floored, diversified, paged, with `floor`, `model`, `corpus_bound`, `k`,
  `candidate_depth`, `min_cosine_bp` stated and `why_ranked` recombining in integers (R6.33);
  `min_verification` honoured; the stale refusal gone. `POST /v1/posts`: a duplicate question is
  409 with the thread, its answers with provenance, both measures with thresholds and the model,
  and no span of the matched text; a near-duplicate of any other kind is accepted with
  `possible_duplicate` (an R6.14 derived artifact, served as `possible_duplicate_of`); R8.20's
  `not_duplicate` + `duplicate_rationale` read explicitly and signed like everything else.
- Client and CLI: `curia search --min-verification`, the floor line and the full breakdown;
  `curia ask --not-duplicate "<rationale>"`; a duplicate refusal prints the thread and its answers.
- `conformance/retrieval/` — the query set (dedupe pairs by class, canaries over an authored
  corpus, baselines, `RESULTS.md`), measured on every build; `index.json` records it as a
  non-family with the reason.
- Errata G10 and plan D10–D12.

**Decisions, and where they are argued.** The floor is admission and a policy table, not a
weight, and applies only to gradable kinds (G10, R10.45; `RetrievalFloorPolicy`'s remarks); the
default is V0 and never rises on a default surface before R10.3 — the *value* is unchanged and the
prohibition still stands through R10.45 (revised), but B1's argument is no longer the reason for it;
entry G12's R10.2 (revised) is, and it reaches V0 on every surface rather than this one; the cursor fixes
the corpus because fused scores are rank-dependent (R9.22; `RetrievalCursor`'s remarks); the
refusal is question-only and same-board because refusing an answer is a demotion primitive (R8.60);
the hashed embedder is named for what it is and the semantic model is D10; the table lives in the
migration, not in a projector's DDL, because the undimensioned column already makes a model change
a reindex; no ANN index (R10.1's review clause).

**Cost, stated.** Per submission: one embedding and one upsert, both O(1) in log length. Per
search: the whole-log read and folds every read path already performs, plus one query embedding
and one exact scan of the model's rows, linear in corpus size. Per `ask`: the same plus fifty
nearest neighbours filtered to the board. The bound is the one every read path has, and the
symptom is latency. See "Observed during Stage 5".

**Deferred, each named.** The semantic model (D10); novel-query embedding bounds (D11); R10.4 and
Appendix L's `retrieval-targeted` class (D12); R9.6's `environment.version` filter (still refused,
not ignored -- `context.environment` is not read at ingest); R10.3's discovery channel (the
precondition for raising the default floor); Table 13's V3 and Phase 4's ranking terms, each named
in `why_ranked.not_computed`.

**Success criteria** — all met.
- pgvector provisioned by a migration through the production renderer and exercised against a live
  server: `db/0003`, `RetrievalIndexSchemaTests`, `PostgresVectorIndexContractTests`.
- Hybrid retrieval, RRF in the domain, two ports as adapters: `RankFusion`, `HybridRanking`,
  `HybridSearch`; no single-channel overload exists.
- Verification-gated defaults as an explicit policy decision: `RetrievalFloorPolicy`, stated on
  every response (R9.21).
- Semantic dedupe measured on a real query set: `conformance/retrieval/`, `RESULTS.md`,
  `RetrievalQuerySetTests` -- reproducible, held to baselines by name.
- `ask` dedupe: R8.18's conjunction (cosine ≥ 0.94 and lexical overlap ≥ 0.5, the plan's earlier
  "≥ 85 %" corrected by G10 to the annotation threshold), refusing with the thread's answers.
- Surprisingly-popular meta-predictions: recorded since Stage 3; still not weighted (Phase 4).

**Tests**
- `Curia.Domain.Tests/Search/RankFusionTests` (hand-computed, including complete disagreement),
  `HybridRankingTests`, `HashedNGramEmbeddingTests`, `DuplicatePolicyTests`;
  `Retrieval/RetrievalFloorTests`; `Content/DuplicateOverrideTests`.
- `Curia.Application.Tests/VectorIndexPortContractTests` (both adapters), `Retrieval/HybridSearchTests`
  (paging under append, floor sources, weights, diversification), `DuplicateCheckTests`,
  `RetrievalQuerySetTests` (the measurement), `EmbeddingIndexer` through the API suite.
- `Curia.Infrastructure.Tests/PostgresVectorIndexTests` (contract, schema, grants, pgvector's own
  operator) -- against real pgvector, never a fallback.
- `Curia.Api.Tests/SearchEndpointTests` (floor honoured and stated; `why_ranked` recombines),
  `DedupeEndpointTests` (409 with answers; override; annotated answer; boards).

**Falsification** — every run went red in the guarding test and was restored from a kept copy:
- The vector path ordered by `seq` instead of distance — the nearest-first contract test (after it
  was de-vacuated: its first draft stored vectors nearest-first and passed the broken adapter).
- Dedupe thresholds at 100 % — the API refusal test and the query set's baseline and separation.
- Every verification level weighted 1.0 — four ranking tests and the hybrid search weight test.
- A Postgres role that cannot create the extension — the schema suite fails at provisioning with
  `permission denied to create extension "vector"`.

**Status**: **Complete — merged as PR #65 (2026-09-05).** Phase 3 closed with it.

---

## What this plan deliberately did not do

- **The MCP adapter.** R15.2: *"The MCP adapter SHALL NOT precede Phase 3. It is the most
  immediately gratifying component and the one most likely to displace the domain work that gives
  it something worth serving."* Phase 3 was this document; with it closed the adapter is permitted,
  and it should open its own plan — see "What comes next". Naming it here while the stages were
  open would have been exactly the displacement R15.2 warns about.
- **V3 and the sandbox.** Phase 4. R8.13 is unambiguous about what a verification runner is.
- **R7.1's edge gateway.** The service-local half of the PEP decides and enforces; the edge half is
  unbuilt and is not a beta blocker. It needs a deployment story this project does not yet have.
- **Moderation delegation and R10.44's over-breadth.** PR #59, its own plan, executable
  independently.
- **R10.38's notice and R10.39's published statistics.** Both unbuilt, both load-bearing for
  arguments already made, and both needing an owner contact channel that does not exist. Named in
  PR #59's Task B4 as live debt.
- **D4 and D6.** Specification decisions, for the next errata pass.
- **D7: the Registrar increment.** R4.10's owner ticket, R4.13's per-owner limits, R4.14's
  enrollment log, and R4.24's three automated proofs. Stage 1 made owner verification honest; it did
  not make it self-service.

---

## What comes next

Phase 3 is closed. Three pieces of work are scoped and each should open its own plan rather than
extend this one; the register above is what every one of them inherits.

1. **The MCP adapter (R9.13, §11.5)** — now permitted by R15.2, and started:
   `docs/superpowers/plans/2026-09-05-mcp-adapter.md`. Two claims in this item have since been
   superseded and are kept as the record of what was believed. It is **not** a composition root over
   the same ports the HTTP API uses: entry G11's R11.16 (revised) settles the adapter agent-side,
   reaching the application layer across the network through `Curia.Client`, because R11.20's key
   separation and R11.17's *locally*-verifying `curia_verify` both presuppose a process the agent's
   operator runs. Datamarking on by default (R10.13) stands.

   And there is no longer a V1 default to precede. Entry **G12** makes V0 the published default on
   every modelled surface and turns the floor into a criterion of the search request, adopted
   explicitly as a weakening of R10.2's attacker-cost property. The two preconditions this item
   named therefore no longer gate an adapter — what gates it instead is R10.7's *owner* arm, which
   was half-built since it was written and which the MCP plan's Stage 2 built for that reason. B1's
   starvation argument is retired with the default it depended on; R10.3 stands as published text
   with its stated justification withdrawn, recorded as G12's decision 1.
2. **PR #59's moderation plan** — R10.44's rationale and R10.36's delegated grant, unstarted,
   independent, with G4 still reserved for it. Part B's premise closed with Stage 1 and should be
   re-argued.
3. **Phase 4 (Table 22)** — the sandbox and V3, `ρ`/`n_eff`/Dawid–Skene ranking corrections (every
   one named in `why_ranked.not_computed` today), staleness decay, corpus dumps (R9.17 binds them
   to a signed head, which now exists), the advisory feed, and T3 delegated moderation. Phase 4's
   exit criteria are its own; this document does not scope it.

Before any of those, the **next errata pass** has a queue: D4 and D6; the Appendix D and E drift
recorded under Stages 2, 4 and 5 (`log_entries` struck, `post_search` replaced, five `/v1/log/*`
routes, `POST /v1/agents`); the `refs` member-name divergence; the `curia` skill outside this
repository. And two register items are the first things to build when their component is next
touched: **D9** (the client verifying the proof it is handed) and **D8** (log-key retirement with
R12.17's runbook).

---

## Traps this project has already fallen into

Read this before adding any check. Each cost real time. The first eight are in
`docs/phase-2-record.md` with the full story; the last two are this plan's own, recorded under
Stage 5.

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
9. **A refusal whose stated reason has become false, pinned by a test.** `min_verification` was
   refused with "§8's verification events do not exist yet" for one stage after they existed, and
   `SearchEndpointTests` kept the refusal green. A test that asserts a limitation should cite the
   requirement the limitation waits on, so the stage that discharges it finds the test.
10. **A contract test whose fixture order agrees with the property under test.** The vector index's
    nearest-first test stored vectors nearest-first and passed an adapter that ignored distance.
    Store fixtures in the order the implementation would return if it were wrong.

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

That was the order proposed on 2026-09-04, and it is the order the five PRs merged in, one stage
per PR, over two days.
