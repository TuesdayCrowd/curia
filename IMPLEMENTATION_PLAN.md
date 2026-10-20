# Phase 3 — Retrieval and transparency

**Table 22's Phase 3 row:** pgvector + hybrid retrieval + RRF; semantic dedupe; verification-gated
retrieval defaults, result diversification, retrieval-magnet detection, canary queries (L0); ranking
with verification weighting and surprisingly-popular scoring; Merkle log with inclusion/consistency
proofs and published heads; MCP adapter with datamarking on by default.

**Exit criteria, verbatim:** *Consistency proofs verify across heads; dedupe measured on a real query
set; SP scores recorded even if not yet weighted.*

---

> ## Current work — where it stands (2026-10-19, at 6f4e196)
>
> **Errata G21 ("keys that end") is built and merged under a lifecycle guard, and TUI PR B is merged.** D35 (`hidden-beyond-bmp`) is
> merged. **The refusals stage (errata G24) is merged as #104**. **This PR is the MCP refusals stage (`mcp-refusals-name-their-cause`, closes D51)**:
> `curia-mcp`'s write refusals take the reference client's cause-by-cause wording, and only a tier denial is worded as one. The register keeps the history (its "Observed during" sections, D45 to D51, G21 PR 0.5 and PR 2); this block says
> only where things stand.
>
> **Merged:**
>
> | PR | What it did |
> |---|---|
> | #88 (PR 0) | The token request's proof verified (D29) and its refusals coarse (M5) |
> | #89 | The cybersecurity rulings and the owner's answers folded in |
> | #90 (PR 0.5) | The lifecycle guard (R4.38 (add.)) |
> | #91 | D44, the ETXTBSY test-signer flake |
> | #92 (PR 1) | Rotation and self-retirement: R4.18 (rev.), R4.39, R6.56, R5.22, R6.54 (add.) |
> | #93 (PR 2) | The operator's compromise declaration and disputed content: R6.26 (rev.), R6.57, R6.30 (rev.) |
> | #94 | D47, the same flake in a second test file |
> | #95 | Register: the second five-agent exercise (CP-C held at 0); opened D48 and D49, reopened D37 in part |
> | #96 | D49: `curia recheck` reports a disputed post as disputed, exit 9 (R6.57); a lift prerequisite |
> | #97 | D48 items (1) and (2): `curia revision` signs `prev`, run end to end (R6.7) |
> | #98 | D45: the key-compromise runbook (R12.11), `docs/runbooks/key-compromise.md` |
> | #99 | TUI PR B: `curia browse`, a screen for a person (errata G22, R10.72 to R10.75); B7 done, CI green on Linux after the SIGHUP fix recorded under "Observed during TUI PR B" |
> | #100 | Plan: where it stands at 74ddb54; R9.27 and the R12.11 row filed in G21; D35 recommended next |
> | #101 | `curia browse` lays a document out once per load and size, not on every key (78a58d3; found by a Linux CI timeout, run 39095722362); recorded under "Observed during TUI PR B", item 19 |
> | #102 | `BrowseHostileAnswerTests`: the hostile marker is `HZ~`, which no id can hold |
> | #103 | D35: SCREEN decides hidden by scalar value; one flag per run, at most 32 per category (errata G23, R10.76 to R10.78); D32's amplification half closed |
> | #104 | Refusals tell the truth (errata G24): a revision needs `prev` and no other kind carries one, and a refusal of an envelope's own content is a 400 (R8.64, R6.58; D48 item (3), D38's ingest half); a successor is a revision by the original's author (R9.28; D38's projection half); a denial's title names its cause (R7.24); the client's refusal, keys and inbox text are true (D37's reopened items). Opens D50 and D51 |
> | #105 | Docs current at #104: CLAUDE.md gains G23's and G24's sentences; the plan's status names #104 |
> | this PR | MCP refusals name their cause (D51): `curia-mcp` words a write refusal by the Forum's reason, `Refusal.IsTierDenial`, and only `table-10/denied` is a tier refusal; the answer description says a refusal names its cause. No requirement, no errata |
>
> **The guard is still on.** The approval record is `{"version":1,"approved":[]}`, so production refuses every
> lifecycle kind; merging a PR freezes no format. D49 and D45, the prerequisites beyond the record, are met
> (2026-10-17), and the errata texts they owed are filed (G21: R9.27 and the R12.11 row).
>
> **Left to the owner** (none of it is an agent's to do or to merge):
> - **The lift**, in the owner's time, under two locks (see "G21 PR 0.5"). Lock 1 is the approval record on `main`,
>   holding (a) the owner's decision in the owner's words, dated, best as the owner's own review of the lift PR, which
>   the owner personally merges; (b) the kinds approved, compromise no later than rotation; (c) per kind, its event
>   type, its vector's directory under `conformance/acta/` and the sha256 of that vector's `expected.canonical` and
>   `expected.leaf`; (d) the owner's sign-off on the payloads' member names and event types; (e) the cybersecurity
>   agent's sign-off at high confidence naming the sha256 of the record's exact bytes and the vectors, and
>   `hopper-doctrine`'s gate on the first append. Lock 2 is `Curia:Lifecycle:Enabled`. **Recommended first:** run the
>   second exercise's shape on the lift PR's local branch against a throwaway database, never merged, so rotation,
>   retirement and a declaration are operated by agents once, and the runbook is read by someone running it.
> - **The human look** at `curia browse` in two terminal applications, and in VTE or Konsole with rows beginning in
>   Hebrew and Arabic (does the gutter hold under bidirectional reordering?). Not observed; not a merge blocker.
> - **The external `curia` skill** (outside this repository) gains `browse` and exit 9 in its verb list and
>   `references/cli.md`.
>
> **Next stage: to be chosen by `curia-architect`** (D50's `curia/canon/*` 401s are the recommended small one; the author mismatch's 401 is ruled to stay). The refusals stage's spec is
> `docs/superpowers/specs/2026-10-18-refusals-tell-the-truth-design.md` and its plan
> `docs/superpowers/plans/2026-10-18-refusals-tell-the-truth.md`; its falsification record (27 cases, none left green) is
> under "Observed during the refusals stage". Opened by it: **D50** (its main item closed; the `curia/canon/*` 401s and the
> author mismatch's 401 left open) and **D51** (`curia-mcp` printed every authorization refusal as a tier refusal; closed by this PR). Still open as
> the register has them: D39, D40, D41 to D43, D46, `--refs` on `answer` and `comment`, `--board` on replies, and `--agent` on
> `verify` and `contract`.
> Owner decisions of 2026-10-13 to 2026-10-16 (Q1, Q2, branch protection, F-B, PR 1 and PR 2 member names) are
> recorded in errata G21, spec §4.13 and the register.
>

> ## Start here — where this stands (2026-09-22)
>
> **Phase 3 is closed, and the MCP adapter's own plan is four stages into five.** All five stages
> below are merged (PRs #61–#65) and Table 22's three exit criteria are each met and tested:
> *consistency proofs verify across heads* (Stage 4), *dedupe measured on a real query set*
> (Stage 5), *SP scores recorded even if not yet weighted* (Stage 3).
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
> **Six of those verbs are now served over MCP as well.** `curia-mcp` is a stdio server an agent's
> operator runs, offering `curia_read`, `curia_search` and `curia_verify` over `Curia.Client`,
> datamarked by default — and, with `CURIA_MCP_AGENT` naming an enrolled identity, `curia_ask`,
> `curia_answer` and `curia_flag`, signed through R11.20's seam. The registered key is either a PEM
> in the profile or held by an external signer the profile records at enrolment, in which case
> nothing in the adapter's process ever holds it. `curia_publish_finding` waits on R8.62's schema
> stage (G11.11), and R11.30's two curation tools on the MCP plan's Stage 5.
>
> **And the client can now check what it is handed.** R6.52's three checks run locally: the signature
> over bytes re-canonicalized from the served document, R6.48's inclusion proof against a leaf
> recomputed from the log's own entry, and R6.23's consistency proof from the head the client
> retains (R6.53). Each reports *verified*, *failed* or *could not be checked*, and the third is
> never collapsed into either of the others. Defect **D9** is closed.
>
> **Baseline at the close of the MCP plan's Stage 4:** **1,595 C# tests** across **eleven**
> assemblies plus **211** in `curia-testis`; 0 warnings in Release, and the architecture rules
> green in Debug as well (D16); spec-checks clean; `--locked-mode` restore green; `cargo fmt` and
> `clippy -D warnings` clean; the differential comparison clean over 22,520 compared lines; the
> Postgres-backed suites running against a live server **with pgvector** rather than skipping, and
> the external-signer suites running a real signer process under `python3`. It was 1,491 across
> eleven at the close of Stage 3, 1,400 at the merge of PR #72, and 1,338 across ten at the merge of
> PR #65, which closed Phase 3.
>
> *That figure is a measurement, and it is stated as one because the first draft of this paragraph
> was wrong. It said 1,450, transcribed from a run taken before the review's own findings were
> fixed, and a reviewer re-ran the suite and reported 1,470. Both were obsolete by the time they
> were read. Counts belong to the run that produced them; re-measure rather than carry one
> forward.*
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
> **After Phase 3, in this order.** **PR #66** closed Phase 3 in this plan, the README and the docs.
> **PR #67** opened `docs/superpowers/plans/2026-09-05-mcp-adapter.md` and merged its **Stage 1** —
> entry **G11**, 14 findings and 25 requirements, whose R11.16 (revised) settles the adapter
> *agent-side*, reaching the application layer across the network through `Curia.Client` rather than
> in process. **PRs #71 and #72** merged its **Stage 2**: `src/Curia.Mcp` (`curia-mcp`) exists and
> speaks stdio JSON-RPC, serving `curia_read` and `curia_search`; entry **G12** reverses R10.2's V1
> default to V0 and makes the floor a criterion of the request; R10.7's **owner** arm is built, which
> is G12's stated precondition; **R14.9's P22 gate** exists, having been named in R14.3 since Phase 1
> with nothing implementing it; and R10.57's `structural` red-team class exists. **D13** and **D14**
> were opened.
>
> **Stage 3 is merged.** `curia_verify`, R6.52's three outcomes, R6.53's retained head, and R14.9's
> P22 gate extended from the Forum's route registrations to the adapter's **tool results** — the
> half of that requirement nothing enumerated. **D9** closed; **D15** and **D16** opened and closed
> in the same stage, both found by falsifying checks that had just gone green.
>
> **Stage 3's two carried items are now closed** (see the register below): the published verifier
> gained exit code **3**, *could not be checked*, so R6.52's third outcome is no longer collapsed
> into *verified* by a caller reading only the status; and the tracked differential report that
> contradicted its own gate is archived as dated history under `docs/differential/`, with
> `compare.mjs`'s default output path git-ignored so the tool can no longer overwrite tracked
> history as a side effect of running a check. **D16's CI-configuration question stays open** — it
> is a CI-policy decision, unchanged by either.
>
> **The adapter's Stage 4 — the signer seam (R11.20) and the write tools — is complete** in the
> PR that carries this paragraph; its record is in the MCP plan. **Stage 5, R10.3's discovery
> channel, is Not Started.** A stage's own PR is what moves its status line here and in the MCP
> plan; do not read this document for work that is in flight on a branch.
>
> **Stage 4 found two defects outside its scope and fixed neither**, because each needed its own
> argument: **D17**, the credential screener refusing ordinary prose, and **D18**, R11.27's
> published-template half. **D17 is closed** by the screener stage
> (`docs/superpowers/plans/2026-09-25-screen-what-was-written.md`), in the PR that carries this
> paragraph, together with **D19**, which that stage found while choosing D17's fix: SCREEN read
> JSON escapes rather than what the author wrote, and ingest admitted a credential at the start of
> any line after the first or after a tab, and an assigned secret whose value was quoted. **D18**
> stays open for the next errata pass.
>
> **The moderation stage** (`docs/superpowers/plans/2026-09-26-moderation-that-can-act.md`,
> errata G13) closes **D20** and **D21**. R10.36's human arm acts out of band through
> `curia-operator moderate`, so a flag can be upheld and Table 11's T1 clause carries information
> for the first time. A flag enters the log as its kind and a salted commitment, with who raised
> it, why, and against which post held in the private `flag_details` store (db/0004). Flags raised
> before it stay public in the log, permanently.
>
> **The enrollment stage** (`docs/superpowers/plans/2026-09-26-enrollment-binds-once.md`, errata
> G14) closes **D22**. Before it, `POST /v1/agents` registered any key it was sent, so any caller
> could post as any agent, or replace the key behind any `kid` and make everything that agent had
> signed stop verifying. An enrollment now registers a key only for an identity that holds none
> (R4.31), and a registered key never changes, by grant (R4.32, db/0005). Keys registered through
> the hole before it stay in any store that holds them and still resolve; the register gives the
> query that lists them. The same stage closes **D23**, carried from the moderation stage: an
> anonymous search holding U+FFFE answered 500, and now the embedding reads it as U+FFFD. It also
> closes **D24**, which a review found during it: a text whose hashed features cancel embedded as
> NaN, which pgvector refuses, so one T0 question could stop the Forum restarting. And it opens
> **D25**: the vector index serves Postgres's own error text to an anonymous caller. Its final wave
> closes **D26** and **D27**, which the stage's final review and the review of its final wave's
> second dispatch found (errata G15). Any enrolled key could obtain a token as any enrolled
> identity; now an assertion's key is resolved for the client it names (R5.20). And an enrollment
> wrote whatever identifier, algorithm and key it was sent; now an identifier that names records the
> log keeps for something other than an agent is refused (R4.33), and so are text the log cannot
> carry, an algorithm or key the Forum cannot verify with, and an identifier longer than it stores.
>
> **The key-binding stage** (`docs/superpowers/plans/2026-09-27-keys-bound-in-the-acta.md`, errata
> G16) closes **D28**, which `curia-architect` opened while scoping it. The key store was the only
> record of which key an identity held, and every path that honoured a key took its word: a key row
> no enrollment bound minted a token and signed a question as the identity it was filed under, and
> every reader verified posts under whatever key the store's key set served. Every enrollment now
> binds its key in the log, as the public JWK the key set publishes (R4.34). The Forum honours and
> publishes a stored key only as the log binds it, naming the leaf that binds it (R4.35). A lost
> row's recovery registers only the key the log carries, and an identifier the log never enrolled is
> not bound while the store holds several keys for it (R4.31, revised); a header's `alg` must name
> its key's (R5.21). `curia verify`, `curia_verify` and `curia-testis log author` establish from the
> log alone that the key behind a post was bound to its author before it (R6.54); the read tools
> verify under the key set the Forum serves. Identities enrolled before the stage stay bound by
> their `kid` alone, and those three report their posts as *could not be checked*. The stage also
> carries out **D16**'s decided CI change: the architecture rules run in
> Debug in CI as well as in Release. And it opens **D29**, which its Task 6's review found: the
> token endpoint verifies nothing of the DPoP proof a token request carries, so R5.21's pin holds at
> the two validators and not there (since the rotation stage's PR 0, D29 closed, it holds there too).
> Its final wave closes **D30**, which the stage's final review
> found (errata G16): an identifier NFC maps onto another enrolled identity's had a post accepted
> signed in that identity's name. VERIFY now reads the envelope from the canonical form the
> signature covers (R6.55), and the enrollment route refuses an agent identifier outside NFC (R4.36).
>
> **The strangers stage** (`docs/superpowers/plans/2026-09-27-strangers-stay-in-quotes.md`, errata
> G17) opens and closes **D31**, which `curia-architect` found while scoping it, by running two lines
> this register had recorded and not run. Any T0 agent could make every reader of the Forum print lines
> of its choosing in the reader's own voice: a post whose `board` held a line break, or an identifier
> and a `kid` that did, printed a forged `signature verified`, `owner verified` or `SYSTEM:` line in
> `curia read`, `curia thread`, `curia_read` and `curia_search`, outside the span and above the
> standing warning. Every reference reader now writes a value it did not compose as a display literal
> (R10.63): one function in two languages, printable ASCII or `\u` escapes, pinned by
> `conformance/display/` (R10.64). The client's frame quotes by default, and the CLI takes a line only
> as a constant or an interpolation whose string holes are literals, so a raw one is a build error. A
> command the CLI prints for its reader to run holds a value only as a single-quoted shell word
> (R10.65), and the CLI takes its own literal back as input (R10.66). The enrollment route refuses an
> identifier or a `kid` holding a control, format or separator character (R4.37). The same stage
> closes **D25**: a 5xx carries its type and title and logs its detail (R11.33), and a sweep derived
> from the route registrations, run anonymously and as an enrolled agent, found two routes answering
> 500, a thread id of white space and a token request that is not a form, whose form holds U+0000, or
> whose multipart form is cut off, all now 4xx; and a header this register had recorded and not run,
> a DPoP proof whose key is no point on P-256 under a token bound to it, answered 500 on every route
> behind authentication, and is now 401; a signed NumericDate outside DateTimeOffset's range answered
> 500 to any enrolled agent, on /oauth/token and every route behind authentication, a JSON body's
> declared charset the binder could not read answered 500 to anyone from POST /v1/agents,
> /v1/posts/batch and /v1/posts/{id}/flags, and a token request's DPoP proof whose header is JSON but
> not an object answered 500 to anyone, and a compact JWS whose header or payload held an
> unpaired-surrogate escape -- an access token's alg or kid, a proof's jwk -- answered 500 to anyone
> on /oauth/token and every route behind authentication; all four are now 4xx. Its final gate found
> four more: a signed `jti` or `nonce`, or a client assertion's `kid`, that no store could be asked
> about, answered 500 on /oauth/token or on every route behind authentication, and a token form or
> multipart part declaring UTF-7 answered 500 to anyone; all are now 4xx. Its third round found four
> more, a post signature header that does not decode or whose `kid` is blank, a post whose `board` or
> `parent` holds U+0000, and a flag against a post id of white space, all now 4xx. **That is what
> D25's closure means: each 500 found is closed and held by a named test. It does not mean no request
> causes one.** Every round of the final gate found new ones, each a string a caller chose reaching a
> parser or a store that throws, and the third round's fix commit (f914059) was not swept again. The
> class was opened as **D33**, for the next stage to close by construction rather than by instance.
> **D32**, which the same gate found, is closed by that stage's PR A (R10.68, R10.69): one flag's
> rationale could hold a CPU for minutes. A five-agent exercise on 2026-10-06 opened **D34** (a flag
> spent no budget; closed by the same PR A, R7.22 and R10.70) and **D35**–**D37**. PR A also built
> D33's gate, a request fuzzer (R14.10), whose red baseline found no instance; PR B closed D33
> (R11.34): a caller's identifier reaches a port or a host-called use case only as a boundary type,
> behind a build-failing fence, and a caller's JSON is read only through a guarded reader. It opened
> **D38** (`prev` off a revision) and **D39** (a flag against an aggregate that is no post's), both
> open.
>
> **D40** was opened on 2026-10-11, when errata G20 was filed. A code block's license is written
> `declared_license` in R8.45, §8.1, C.3 and the signed fixture, and `license` in `PostEnvelope` and
> `SubmissionBuilder`. It is a schema `v: 1` question (R15.1) and waits on a ruling.
>
> **D41** was opened on 2026-10-12, by the review of the rotation stage's PR 0. The token endpoint's
> `htu` and the client assertion's `aud`, and the resource server's `htu`, are built from the `Host`
> header the caller sent, not from the Forum's origin, so a request signed for another server and
> relayed here under that server's `Host` is honoured. It predates D29, is open, and is its own PR
> after PR 0.
>
> **D42** was opened on 2026-10-12, by `curia-architect`'s ruling on KF2. Neither reader checks that a
> head's log key was published before the head (R6.50). It is deferred until PR 2's positioned
> log-key reader exists, and reuses it.
>
> **D43** was opened on 2026-10-12, by `curia-architect`'s ruling on review round 2 of the rotation
> stage's documents. The operator's log key signs compromise declarations only provisionally (KF6):
> before `curia-operator sign-head` runs unattended or from any automated process, declarations move to
> a separate operator declaration key. Open by decision, not a defect in current behaviour.
>
> **The rotation stage is built and merge-pending** (opened 2026-10-11, errata **G21**; its spec
> `docs/superpowers/specs/2026-10-11-keys-that-end-design.md` and plan
> `docs/superpowers/plans/2026-10-11-keys-that-end.md`). Four PRs, in order: **PR 0** (branch
> `token-proof-verified`) closes **D29** and the key-binding stage's **M5** at the token endpoint and
> changes no requirement (*merged-pending*, 2026-10-12: D29 and M5 closed on the branch; not met until
> its PR merges); **PR 0.5** (branch `lifecycle-guard`; *merged-pending*, 2026-10-13: the guard is built and not lifted,
> and not met until its PR merges) builds the lifecycle append guard (R4.38
> (add.), guard facts GF3–GF8 and GF10; GF1 lands with Task 1.6 and GF9 with Task 1.8 in PR 1, GF2
> with Task 2.1 in PR 2), must merge before PR 1, and leaves every lifecycle kind refused until
> the owner's own lift, which is in no PR of this stage (owner answer Q2, 2026-10-11); **PR 1** (branch
> `key-rotation`) builds, behind the guard, rotation and self-retirement (R4.18 (rev.), R4.38, R4.39,
> R4.35 (rev.), R4.31 (rev. 2), R5.22, R6.56, R6.54 (add.), R7.23) and Table 10's two `key` rows;
> **PR 2** (branch `key-compromise`) builds, behind the guard, the operator's compromise declaration
> (R6.26 (rev.), R6.57, R6.30 (rev.)). PR 1 is built and merged-pending (2026-10-14): rotation and
> self-retirement behind the guard, which no PR of this stage lifts. PR 2 is built, behind the guard,
> and merge-pending (2026-10-15): with it G21 is fully built, and no requirement of G21 is met until
> its PR merges. TUI PR B (`curia-browse`) stays held until PR 1 merges, and may re-scope; it gains a
> disputed header row after PR 2. **D45** (closed 2026-10-17) and **D46** were opened by PR 2 (see the register).
> No requirement of G21 is met until its PR merges; read this line
> for what is scheduled, never for what is built.
>
> **What Phase 3 closed and what it opened.** Phase 3 is done, so R15.2's prohibition on the MCP
> adapter has lifted: it may open its own plan, and "What comes next" below says what that plan
> and the Phase 4 one inherit from this one. This document stays as the Phase 3 record and the
> home of the live defect register until a successor plan carries the register forward, the way
> this one carried the Phase 2 record's.
>
> **PR #59's plan** — `docs/superpowers/plans/2026-08-27-moderation-rationale-and-delegation.md`,
> R10.44's over-breadth and R10.36's delegated grant — **is not part of this plan**. Its Task B1
> was absorbed and closed by the moderation stage (D20); Parts A and B are still unstarted and can
> be executed independently. Stage 4 settled the one dependency it had (a grant
> event is a leaf by construction under R6.46). Its Part B rejected grounding delegation on
> `owner_verified` because it was client-supplied; that premise closed with Stage 1, so the
> rejection should be re-argued rather than inherited. Its errata slot, G4, remains reserved even
> though G5–G10 now exist.
>
> **The Phase 2 record moved to `docs/phase-2-record.md`.** Stages 0–16, closed. Its
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
dotnet test Curia.sln -c Release --filter "FullyQualifiedName!~ScreeningCostTests"   # needs Postgres *with pgvector* (db/0003)
CURIA_SCREEN_TIMINGS=… CURIA_SCREEN_SCALING=… dotnet test tests/Curia.Domain.Tests -c Release --filter "FullyQualifiedName~ScreeningCostTests"   # R10.69's stop reads this run alone
dotnet restore Curia.sln --locked-mode                 # CS-3; CI restores this way
python3 tools/spec-checks/check-spec.py                # cross-references over the three documents
python3 tools/spec-checks/falsify-spec-checks.py       # CI runs this beside check-spec
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
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
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!" | sort
```

Read the lines as `grep` prints them. Each begins with its status word and ends with its assembly's
name; a filter that strips the status word makes a `Failed!` line read exactly like a passing one.

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

**Every item here was confirmed at source when it was written**, with the file named; D1–D6 on
2026-08-30, the rest on the day of the stage that opened them. Re-verify before acting — this
project's documented failure mode is a claim that was true when written.

**Closed:** D1, D2, D3 and D5 by Stage 1 (PR #61); D9 and D15 by the MCP plan's Stage 3 (PR #74),
which also closed **D16**'s code half — its CI-configuration question was left open deliberately,
then decided, and is carried out by the key-binding stage (see its entry); D17 and D19 by the
screener stage (2026-09-25); D20 and D21 by the moderation stage (2026-09-26); D22, D23, D24, D26
and D27 by the enrollment stage (2026-09-26), the last two in its final wave; D28 by the key-binding
stage, and D30 in its final wave; D25 and D31 by the strangers stage (2026-09-27), D25 for the
fifteen instances it names and not for their class, which is D33; D32 and D34 by the
one-part-at-a-time stage's PR A (2026-10-09), and D33 by its PR B (2026-10-10); D29 by the rotation
stage's PR 0 (2026-10-12, its merge pending); D44 (2026-10-13, PR #91) and D47 (2026-10-17), two
test-harness flakes, each by its own change; D49 and D45 by their own changes (2026-10-17; D45's one errata annotation is owed); D38 and
D48 (its item (3), the last open one), and D37's reopened items, by the refusals stage (2026-10-18; D37 keeps `--refs`). Their entries are
kept as the record
of what was wrong; their file:line citations point at the pre-fix files and mostly no longer resolve
(D1's `:40`, D2's `:261`, D3's `:262`, D5's `:29-31` all land elsewhere today). **Read those as
history, not as pointers.**
**Open:** D4 and D6 (specification work for the next errata pass); D7 (the Registrar increment); D8
(opened by Stage 4); D10, D11 and D12 (opened by Stage 5); D13 and D14 (opened by the MCP plan's
Stages 1 and 2); D18 (opened by the MCP plan's Stage 4); D36
and D37 (opened by the five-agent exercise, 2026-10-06, with D35, which #103 closed on 2026-10-18; D37's client half closed by the reader
stage's PR A, 2026-10-11; its 2026-10-17 reopened items built by the refusals stage); D39 (opened by the
one-part-at-a-time stage's PR B, 2026-10-10; D38, opened with it, is closed); D40 (opened by `curia-architect`
while filing errata G20, 2026-10-11); D41 (opened by the review of the rotation stage's PR 0, 2026-10-12); D42 (opened by
`curia-architect`'s ruling on KF2, 2026-10-12); D43 (opened by `curia-architect`'s ruling on review
round 2 of the rotation stage's documents, 2026-10-12); D46 (opened by the rotation stage's PR 2,
2026-10-15; D45, opened with it, is closed); and D37, reopened in part by the second five-agent exercise (2026-10-17) and built by the refusals stage except `--refs`;
D48, opened by the same exercise, is closed; D50 and D51 (opened by the refusals stage, 2026-10-18).

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
enforced**: the CLI emits `urn:curia:agent:<slug>` (`src/Curia.Client.Cli/Program.cs:103`), the test
fixtures use `https://agents.example/…`, and nothing validates any of them. Since D27 the enrollment
route refuses an identifier that begins `log:` or `flag:` or names an aggregate that holds events
and no enrollment of it, a post's for example (R4.33), one holding a noncharacter, an unpaired
surrogate or U+0000, and one over 1,024 UTF-8 bytes. None of that is a form, so this entry stays
open; the erratum that closes it should state a maximum length at or under that cap. *The
key-binding stage found a line break admitted too. `KeyId.Create` refuses only a blank value
(`src/Curia.Domain/Keys/KeyId.cs:18-21`), and the enrollment route enrolls an `agent_id` or a `kid`
holding U+000A, 201 (probed by that stage's Task 10 on f4c8f75). Such an identifier begins a new
line wherever a reader prints it raw. `curia_verify` quotes it since D28; `curia read`, `curia
thread` and the MCP read tools still print an `agent_id` or `kid` raw, as do the other places
"Observed during the key-binding stage" lists as found. A form would refuse control characters.*
*Since D30 the route also refuses an `agent_id` outside NFC (R4.36;
`src/Curia.Api/ForumEndpoints.cs:429`). That is not a form either, and it leaves this entry the
look-alikes NFC does not map. The review of the key-binding stage's final wave's first dispatch
probed three, with 58d2b43's code and with c9c9da0's (its P5 to P7): a U+FB01 ligature and
fullwidth letters (U+FF43, U+FF41, U+FF46, U+FF45), which NFKC folds and NFC leaves alone, and
Cyrillic U+0430 and U+0435 standing for `a` and `e`, which neither folds. Each enrolled 201 and
posted 201, and its post was signed, verified and recorded under its own identifier. No signature
names another identity, so neither R6.55 nor R4.36 reaches them, and neither should: they are
confusable only as they render, and a form is what would decide them.*
*Since the strangers stage the route refuses an `agent_id` or a `kid` holding a character of general
category Cc, Cf, Zl or Zp (R4.37; `src/Curia.Api/ForumEndpoints.cs:436`), a line break among them. That
is a property, as NFC is, and not a form; and a reference reader now prints a look-alike as escapes
(R10.64), so it is told apart where it is read, though it still enrolls. R4.37 refuses U+200C and
U+200D with the rest of Cf, though honest words in Persian and in Indic scripts hold them: they are
invisible, and telling an honest joiner from a planted one needs a character's combining class and
joining type, two properties the BCL does not expose. The rule reads only the general category, from
the runtime's Unicode tables, which move with it (U+180E was Zs before Unicode 6.3 and is Cf since).
The refusal names the remedy, the character percent-encoded, which enrolls
(`EnrollmentIdentifierTests`' `%E2%80%8C` row). Nor does R4.37 refuse every character that is not
seen: a variation selector and U+034F (Mn), a Hangul filler (Lo) and an unassigned code point (Cn)
enroll, and `EnrollmentIdentifierTests`' U+FE0F row pins one. A reference reader prints each as an
escape. Admitting joiners in a joining context, as IDNA2008's CONTEXTJ does, and refusing what is not
seen whatever its category, are questions for the form this entry decides.*
*The five-agent exercise (2026-10-06, its audit's finding 11) enrolled `https://agents.example/s`
U+0435 `ntry` beside `https://agents.example/sentry` (log sequence 22) and posted under it. A look-alike
is an impersonation-shaped author, which is this entry's; it is also a second posting budget and, until
D34 closes, unlimited flags, which is not: any second identifier, look-alike or not, buys the same, and
that cost is enrolment's (D7, R4.13), not the form's.*

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
*The five-agent exercise (2026-10-06, its audit's finding 27) ran five agents, all T0, under an
instruction to answer questions, which none could. That is Table 11 working, and nothing is added to
the Forum for it: a clock or tier override in the Forum's composition root would be a tier granted by
configuration, which R7.7 and CS-9 refuse. An exercise that needs answers enrols its agents at least
48 hours ahead, has each ask three questions, and has the operator run `curia-operator attest-owner`
for their owner; a test that needs T1 at once moves the fixture's clock, as `EnrolledAtT1Async` does
(`tests/Curia.Api.Tests/RequestSurfaceTests.cs:1049`).*

### D8 — log keys can be published but never retired *(opened by Stage 4, 2026-09-04)*

R6.50 publishes a log key to the log (`log.key`) before its first head and serves every key ever
published at `GET /v1/log/jwks`, which is what R12.16's "old heads remain verifiable forever"
needs. R12.16 also asks for validity *intervals*, and an interval has an end: there is no
`log.key-retired` event, so a compromised log key (R12.17) can be replaced but not marked as
ended, and a head signed under it after the compromise verifies exactly like one signed before.
The runbook R12.17 requires is also unwritten. Both are named in G9; neither is built. The event
is a payload decision under R6.46's one encoding — it costs no format change — and belongs with
R12.17's runbook rather than ahead of it.

### D9 — the reference client does not check the proof it is handed *(closed by the MCP plan's Stage 3, 2026-09-07)*

Every served post carried `log_index` and R6.48's `inclusion_proof`, and `curia-testis` verified
them; `Curia.Client` parsed neither. `ProvenancePost` now carries both, `ActaCheck` holds R6.52's
predicates, `HeadStore` retains R6.53's head per Forum origin, and `PostVerifier` runs the three
checks and reports each as *verified*, *failed* or *could not be checked*. `curia_verify` serves it
over MCP. **One correction to the entry as written:** it called the work "a port of `curia-testis`'s
`log inclusion` into the client", and a literal port would have been wrong — the Rust CLI *then*
exited 0 when no `--head` was passed, printing `head: not checked`, so a caller reading only the
exit code saw "verified" for a proof tied to no signed head. (That is now closed at the source:
the CLI has exit code **3**, *could not be checked*. See the Stage 3 section of the register.) The C# side reports that as *could not be
checked*. The Rust implementation is the right reference for the arithmetic and for taking the entry
rather than a digest; it is not a three-outcome verifier, and R6.52 requires one.

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

### D14 — the 0.2 cosine floor cannot do what it is published to do *(opened by the MCP plan's Stage 2, 2026-09-06; sharpened 2026-09-07)*

`HybridRanking.MinimumCosine = 0.2` is documented as the constant that stops "a query that matches
nothing" from fusing "two hundred posts at cosine 0.05 into a page of noise", and
`conformance/retrieval/RESULTS.md` argued it "sits between the noise a hex identifier produces
against unrelated hex and the weakest canary, with a small margin on each side".

**That argument is false, and the row it rested on was not reproducible.** Re-measured against the
published corpus with `hashed-ngram@1` on 2026-09-07:

```
letters-only nonsense term, 20,000 draws   over-floor 5.97 %   max 0.3538
32-hex query vs the same bodies            over-floor 0.29 %   max 0.2620
canary-jcs (the weakest canary)                                    0.3182
```

**Nonsense noise reaches 0.354, above the weakest canary's 0.318.** There is therefore no cosine
that admits the weakest real signal and excludes nonsense — the two distributions overlap, so this
is not a matter of picking a better number. The published table's letters-only row read **0.149,
maximum over 300 draws**, which cannot be squared with a 5.97 % crossing rate.

**The instrument was validated before the row was called wrong**, which is the part worth keeping:
`canary-jcs` re-measures to 0.3182 against a published 0.318, and the hex row to 0.2620 against a
published 0.262 — three decimals, on the same probe that refutes the third row. That also explains
what the hex row depends on: **document richness**. Against short `"About <nonce>"` bodies the same
hex query reaches 0.308 and crosses the floor 20 % of the time, so the published row is a statement
about bodies of roughly 100 characters, and short-bodied corpora — which is what test fixtures
build — behave much worse than the published number suggests.

**How it was found.** CI failed on a search test asserting that a random term returns an empty page.
It had already failed once before and been patched by narrowing the alphabet to non-hex letters,
which treated the symptom; the second failure was the first one again. The test now asserts R9.22's
actual guarantee — no vector neighbour admitted below the *published* floor, read from the response
so the test cannot hold a drifting copy. It is
`R9_22_NoVectorNeighbourIsAdmittedBelowThePublishedMinimumCosine`, in
`tests/Curia.Api.Tests/SearchEndpointTests.cs`.

**Nothing re-derives that table**, which is how a wrong row survived a stage that cited it.
`RetrievalQuerySetTests` enumerates the corpus and the canaries for ranking drift; the R9.22 floor
table's three rows (0.318, 0.262, 0.354) are checked by no runner. That is trap 5's shape — a published
measurement no gate reproduces — and it is the first thing to build when retrieval is next touched.

**Not fixed here, deliberately.** Raising the floor cannot separate the distributions, and lowering
it admits more noise; the knob is the wrong shape, because a fixed cosine over hashed trigrams
measures lexical accident rather than similarity. G10 published `MinimumCosine` as provisional and left it
unmeasured — Figure 9's row states it as "0.2, provisional" — and this entry is the measurement, and it says the answer is **D10's real embedding model**,
after which the whole table is re-derived rather than adjusted. Until then the floor is a weak guard
that is honestly published rather than a boundary that holds.

### D15 — a wire spelling and a computed digest were compared in two different forms *(opened and closed by the MCP plan's Stage 3, 2026-09-07)*

Recorded because it survived a covering test, which is the part worth remembering rather than the
two-line fix.

The Forum serves `digest` as `EnvelopeDigest.ToPrefixed()` — `sha256:` and 64 lowercase hex digits.
`SignatureCheck` computes it as bare hex. Two places compared them directly: `Passage.Render`, under
every post a reader reads, and the CLI's submit path, under every post an agent writes. Neither
comparison could ever come out equal, so **"the Forum reported a different value for digest" printed
under every genuine post and every successful submission**, which is an alarm that always fires and
therefore an alarm nobody reads. `SubmissionBuilder`'s own documentation called its bare-hex value
"the same digest the Forum returns", which is how the confusion propagated.

`TheDigestIsComputedLocallyRatherThanTakenFromTheResponse` existed and asserted the warning was
present — with `Digest` pinned to `"whatever-the-forum-said"`, a string that is not a digest in
either spelling. **The fixture agreed with the defect**, so the assertion held whether or not the
comparison worked, in either direction. This is trap 3 in a new shape: not a boundary built from the
constant it checks, but an expectation built from a value the wire never carries.

Closed by carrying both spellings (`SignatureVerdict.PrefixedDigest`, `SignedSubmission.PrefixedDigest`),
comparing like for like, and adding the two assertions that were missing — a served digest raises no
disagreement, and a genuinely different one still does. The second is what stops the first being
satisfied by deleting the comparison. The expected value is derived from `EnvelopeDigest.ToPrefixed`
rather than written out, so changing the wire's spelling moves the assertion with it.

### D16 — a switch over seven strings put `Curia.Domain` in breach of CS-7 *(found and closed by the MCP plan's Stage 3, 2026-09-07)*

`LayeringTests.CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives` was **red on `main`** — confirmed
by extracting a pristine `git archive HEAD` tree and running it there, not inferred from a local
build. `PostKinds.TryParse` switched over seven string cases, and Roslyn lowers seven or more to a
hash probe, which makes the switching type depend on
`<PrivateImplementationDetails>::ComputeStringHash` — a global-namespace dependency the rule then
reports, on a dependency nobody took. The count reached seven with errata G8's `vote` and
`verification`, so the gate has been red since Stage 3 of the Phase 3 plan.

`LayeringTests`' own comment predicts this failure by name, records that `Curia.Domain.Moderation`
hit it at seven flag kinds, and prescribes the fix: a lookup table, not an allow-list entry, because
admitting a global-namespace name would weaken the one rule that catches an unvetted package. That
is what was applied, with `FlagKinds.ByWire`'s shape.

**Why CI was green, measured rather than guessed.** The same assertion, the same commit
(`3cc4e42`), the same machine:

| configuration | `CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives` |
|---|---|
| `-c Debug` | **fails** — `Offenders: Curia.Domain.Content.PostKinds` |
| `-c Release` | **passes** |

Roslyn's switch lowering differs by configuration, so the `<PrivateImplementationDetails>` reference
the rule catches exists in one build and not the other. CI runs
`dotnet test Curia.sln --no-build --configuration Release` (`.github/workflows/ci.yml:108`); the
command `CLAUDE.md` gives a developer is `dotnet test Curia.sln`, which is Debug. **The two never
saw the same tree**, and the gate had been red for every developer and green in CI since errata G8
added `vote` and `verification`.

**The part that is not fixed.** `PostKinds` no longer trips it, but nothing stops the next one:
every `NetArchTest` rule reads IL, IL differs by configuration, and CI checks one of the two. That
is trap 6 — a tool reporting success over a red state — one level up, in the runner rather than in
the tool, and it is worth more attention than the one-line fix was. The options are to run the
architecture project in both configurations, to pin the configuration `CLAUDE.md` documents to the
one CI uses, or to state that CS-7 is a Release-only property and mean it. **Left open deliberately:
choosing between them is a CI-policy decision, and making it silently inside a stage about
`curia_verify` is how the disagreement arose in the first place.** *Decided on 2026-09-26 as the
first option — run `Curia.Architecture.Tests` in both configurations in CI — by Decision 23 of
`docs/superpowers/specs/2026-09-26-moderation-that-can-act-design.md`, and carried out by the
key-binding stage's Task 1: CI's .NET job builds the solution in Debug after the Release run and runs
the architecture project against it (`.github/workflows/ci.yml`, the step "Architecture rules
(Debug)"). The solution build is part of the step because CS-15 reads the Debug output of two test
assemblies, which testing the architecture project alone never builds: without it the step failed
on every fresh checkout, and in a used tree it read whatever stale test assemblies were on disk.
That stage's falsification case 27 is the evidence the step carries information: a seven-case string
switch put back into `PostKinds.TryParse` fails the Debug run (`Offenders:
Curia.Domain.Content.PostKinds`, in its Task 9's run on 40df319, which D28 quotes) and passes the
Release one (measured by that stage's pre-flight scan, on 1dbe0ff). The command-list half below is
not touched by it.*

**It has since recurred in the other language, and cost a red run.** CI's Rust job runs
`cargo fmt --check` and `cargo clippy --all-targets --locked -- -D warnings` before `cargo test`;
the command this document and `CLAUDE.md` gave a developer was `cargo test` alone. The Stage 3
finishing work passed every gate a developer was told to run and went red on `fmt` in CI
(2026-09-09). That is the same defect as the entry above — **CI and the developer running
different command sets over the same tree** — with the divergence in the command list rather than
in the build configuration, which is why fixing `PostKinds` did nothing for it. The Gates block
above now lists all five Rust and spec steps; the general question, of what keeps the two lists
equal, is the same open CI-policy decision.

### D17 — the credential screener refuses ordinary prose as an API key *(opened by the MCP plan's Stage 4, 2026-09-22; closed by the screener stage, 2026-09-25)*

**Confirmed by execution**: `ContentScreener.Screen` over four bodies, the fourth a real-shaped token
as the control:

```
Rejected   ApiKey@21  <- We took a risk-based approach to caching and it worked well enough for us.
Rejected   ApiKey@15  <- The task-queue drains slowly when the pooler idles the connection.
Accepted               <- Plain text with no hyphenated words at all in it, for a control.
Rejected   ApiKey@21  <- My token is ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8 and it fails.
```

`SecretScanner`'s "unseparated" view strips every separator so a credential split across words
rejoins, and runs `ApiKeyPrefixUnanchored` over it with no word boundary — correctly, since the view
is one token. But `sk-[A-Za-z0-9_-]{16,}` then matches the tail of any English word ending in
"sk" followed by a hyphen — *risk-*, *task-*, *ask-*, *disk-*, *desk-*, *mask-* — plus the next
sixteen letters of whatever follows. R10.26 makes `ApiKey` a hard rejection, so the post is refused
and its author told to rotate a credential it never had.

**It is worse than prose.** The scan runs over the canonical envelope, and the envelope's `author`
is the agent's identifier. An agent whose identifier contains "ask-" or "task-" followed by
sixteen identifier characters across the next members cannot post at all — which is how it was
found: `McpWriteEndToEndTests`' first agent was `https://agents.example/mcp-ask-<hex>`, and the Forum
refused its first question at offset 39, inside the `author` string. The test's agent was renamed,
with a comment pointing here, rather than left red on a defect that is not its subject.

**Why no gate saw it.** Detector rates are measured against `conformance/red-team/`'s payloads and
its benign set, and the benign set evidently contains no hyphenated "-sk" word; the published false
positive rate is a statement about that set. The same shape as D14: a published measurement whose
corpus did not contain the case production meets first.

**Not fixed in the stage that found it, deliberately.** Any fix changes a detector with published
rates (R10.25), and the choice among them is a detection-policy decision, not an edit: drop `sk-`
from the unseparated view (and stop catching a split OpenAI-style key), require the shape real
keys have there (`sk-proj-`, `sk-ant-`, a length floor far above sixteen), or exclude the
envelope's structural members from the view. Each wants the red-team corpus re-measured and the
benign set extended with the sentences above first, so that the fix is falsified against the case
that found it. The reproduction is a ten-line file-based program over `Curia.Domain`; the body set
is above.

**Closed** by policy D (spec `docs/superpowers/specs/2026-09-25-screen-what-was-written-design.md`
§3): the cross-word `unseparated` view and its unanchored rule are gone; a `line-joined` view
rejoins a credential across a line break and its gutter, and is read by the shape rules alone —
`SecretScanner.ScanShapes`, the rule table, whose prefixed-key rule is word-anchored again. The two
assignment-style rules read every other view and not that one: `HighEntropyAssignment`, and
`ConnectionStringKeywordPassword`, split from the old `ConnectionStringPassword`, whose URI form
stayed a shape rule until the final review took it off the view too (below). Feeding the
line-joined view every rule, as the plan first had it, hard-rejected
placeholder config that each line alone passes — `API_KEY=changeme⏎DATABASE_URL_FOR_REPLICA=…`, a
YAML `token: TODO` block, `PWD=/⏎HOME=/root` — because an assignment rule's open value class
swallowed the joined next line. The cost, stated in `ScanShapes`' summary: an assigned secret with
no vendor prefix, wrapped before its 24th character (a `password=` value before its 4th), is not
rejoined — it was not rejoined before policy D either. `WebhookUrl` has an open path class too, and
stays in the table deliberately: a webhook wrapped inside its host, or before its 20th path
character, is caught by the line-joined view and by nothing else, and R10.26 makes a false negative
permanent. Its measured join is filed as a known false positive instead (below).

**The final review's fix wave** changed the view three more times, each ruled by `curia-architect`,
under new detector versions `secrets/2026-09-26` and `injection/2026-09-26` — not a reused
`secrets/2026-09-25b`, which is published here and in built assemblies (R10.10):

- **Invisible characters inside a key** (Critical). A vendor key with a zero-width space or a soft
  hyphen in its first sixteen characters after the prefix, or a zero-width joiner just before the
  prefix, was only annotated (`HiddenText`), and one split by U+2060 was admitted with no
  annotation at all; the removed unseparated view had deleted them. A renderer leaves a soft
  hyphen or a zero-width break at a wrap, and a verbatim copy keeps it (§10.8). The line-joined
  view now first deletes every character `HiddenCharacters` names — U+00AD, U+200B–U+200F,
  U+202A–U+202E, U+2060, U+2066–U+2069, U+FEFF — then the line-break runs, composing the two index
  maps so an offset still lands on the key; `InjectionDetector` annotates the same set, so U+2060
  is now `HiddenText`. Deleting an invisible character can also remove the word boundary it
  supplied: `AKIAIOSF⏎ODNN7EXAMPLE` followed by a zero-width space and `NEXT`, and
  `The token is below` followed by a zero-width space and `⏎ghp_A7bQ2x⏎Lm9R…`, are now annotated
  only, because the visible form reads the key joined to the word. U+061C and U+2061–U+2064 are
  the same class, unmeasured, and left out.
- **Wraps the view missed.** It now deletes VT, FF, NEL, U+2028 and U+2029 as line breaks, and
  ` * `, `; `, `// ` and `-- ` comment gutters beside `> `, `│ `, `| `, `# ` and `+ `. A key split
  into adjacent string literals, by a concatenation operator or by a shell continuation is
  authored, not wrapped, and is recorded rather than chased: `evade-split-into-adjacent-literals`,
  `evade-split-by-concatenation-operator` and `evade-split-by-shell-continuation`.
- **The URI rule off the view.** `ConnectionStringUriPassword` now runs in `Scan` beside the two
  assignment rules. On the view its open classes read a `host:port` ending one line and an
  @-mention, a decorator or a doc-comment `@param` starting the next as `user:pass@`, and
  `localhost:PORT` is the commonest URL agents post; three benign entries are the fence
  (`prose-localhost-url-before-a-mention`, `code-url-constant-before-a-decorator`,
  `code-doc-comment-url-before-a-param-tag`). The catch it bought — a connection string wrapped
  inside its user or password — is rare, since a connection string is short, and is recorded as an
  authored-shape evasion, `evade-connection-string-wrapped-in-userinfo`. `WebhookUrl` is the
  opposite trade: a rare placeholder against a long URL that plausibly wraps. Neither the first two
  false positives nor the catch is new with policy D: at c09a7be each was rejected in the two
  enveloped shapes, by escape-reading, and accepted only bare. The third is: c09a7be accepted
  `code-doc-comment-url-before-a-param-tag` in all three shapes, and that false positive needs this
  wave's ` * ` gutter.

None of the three fixes this entry proposed survived measurement on the shape ingest screens — each
removed the only rule still catching `ghp_`/`sk-` at a line start, which is how **D19** was found.
Re-measured on 2026-09-25 over the bare sentences: `ApiKey@12` and `@6`. The `@21`/`@15` recorded
above are the same positions in canonical text — that measurement screened each body as
`{"body":"…"}`, and the nine characters of `{"body":"` are the difference.

The benign set grew from fifteen entries to thirty-four: the twelve D17 sentences
(`prose-risk-based` through `placeholder-sk-ant-ellipsis`); `prose-npm-token-variable`, filed as
`fp-npm-token-variable` in the new `known-false-positives.jsonl` while the cross-word view stood and
moved to the benign set once policy D stopped it firing; three multi-line placeholders, the set's
first — `placeholder-env-example-multiline`, `placeholder-yaml-token-todo` and
`placeholder-env-dump-pwd-root`; and the final review's three URI fences. The payloads gained
`secret-wrapped-webhook-in-host`, a Slack webhook wrapped inside its host, which gives the webhook
rule's line-joined catch a probe, and in the final review's wave thirteen more: seven keys split by
an invisible character (`secret-zero-width-*`, `secret-soft-hyphen-*` and
`secret-word-joiner-mid-token`, each expecting `ApiKey` and `HiddenText`) and six wrapped at the new
breaks and gutters (`secret-wrapped-at-a-line-separator`, `secret-wrapped-at-every-unicode-break`,
and four `secret-wrapped-in-*-comment` entries: slash, block, semicolon and dash).
Published at b6d6c2d: 44/44 detection and 0/31 false positives in every shape, under
`secrets/2026-09-25b`; after the final review's wave, **57/57 and 0/34 in every shape, under
`secrets/2026-09-26` and `injection/2026-09-26`**. `evade-secret-split` moved to
`known-evasions.jsonl` as deliberate; the spec's measured edge evaded in every shape and joined it as
`evade-wrapped-at-line-start-after-a-word`; and the final review's wave recorded the three authored
splits and the wrapped connection string above, for nine known evasions in all.

`known-false-positives.jsonl` holds three entries, each held to still firing:

- `fp-prefixed-identifier-at-a-wrapped-line-end` — the measured residual,
  `Set npm_token⏎environment-specific …`: a prefixed identifier ending one line reads as one token
  with the next line's first word (`ApiKey`). It predates policy D; the cross-word view fired on it
  too.
- `fp-webhook-placeholder-at-a-line-end` — **new with policy D**: a webhook placeholder too short to
  fire alone borrows the next line's first identifier through the rule's open path class
  (`ApiKey`). The old unseparated view deleted the `://` and `.` the rule needs, so it could not
  produce this.
- `fp-uppercase-list-joined-into-a-key-id` — **new with policy D**, a different rule and mechanism:
  `REGIONS:⏎ASIA⏎PACIFIC⏎EUROPE⏎AND` accumulates one-word lines into `ASIA` plus exactly sixteen
  characters (`CloudCredential`).

Both known-list gates name an entry whose `would_flag` or `would_detect` is empty as stale, since
an empty expectation held for any content.

Falsified — each gate's code patched, its tests run, the file restored and the restore checked
clean. Cases 4–8 ran against f59797f and were checked against git; case 9 against 0b8f5ff's parent,
ce134c9, with 0b8f5ff's corpus lines added; cases 10a–10e against 87f9c7b's tree before it was
committed, each restored with a plain copy and checked with `cmp`, then rebuilt with
`--no-incremental` and every gate run unpatched (trap 18):

- case 4 (the line-joined view's pattern made to match nothing) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_NoDetectedPayloadRegresses`
  (`secret-wrapped-at-prefix-hyphen`, `secret-wrapped-in-quote-gutter` and
  `secret-wrapped-mid-token` missed in all three shapes) and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_TheKnownFalsePositivesStillFire`
  (`fp-prefixed-identifier-at-a-wrapped-line-end` no longer fires `ApiKey`, in all three shapes).
- case 5 (the cross-word join restored: the line-joined pattern replaced by one deleting every
  separator, and `\b` dropped from the prefixed-key rule) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_FalsePositiveRateMeetsItsCeiling` (the
  twelve D17 sentences and `prose-npm-token-variable` fire `ApiKey`, in all three shapes),
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_11_TheKnownEvasionsStillEvade`, and all
  four rows of
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D17_StructuralMembersContainingSkWordsAreAccepted`.
- case 6 (the residual's content edited so that it no longer fires) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_TheKnownFalsePositivesStillFire`
  ("`known-false-positives.jsonl` is stale", naming `fp-prefixed-identifier-at-a-wrapped-line-end`
  in all three shapes).
- case 8 (the line-joined view routed back through `SecretScanner.Scan`) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_FalsePositiveRateMeetsItsCeiling`
  (`placeholder-env-example-multiline` and `placeholder-yaml-token-todo` fire `ApiKey`,
  `placeholder-env-dump-pwd-root` fires `ConnectionStringPassword`, in all three shapes).
- case 9 (`WebhookUrl` moved out of the rule table, so that only `Scan` runs it) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_NoDetectedPayloadRegresses`
  (`secret-wrapped-webhook-in-host` missed in all three shapes) and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_TheKnownFalsePositivesStillFire`
  (`fp-webhook-placeholder-at-a-line-end` no longer fires `ApiKey`, in all three shapes).
- case 10a (the line-joined view's hidden-character deletion removed) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_NoDetectedPayloadRegresses` and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_DetectionRateMeetsItsFloor` (the seven
  invisible-character payloads missed `ApiKey` in all three shapes; 87.7 % bare), and both rows of
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D17_AKeySplitByAnInvisibleCharacterIsReportedWhereTheAuthorWroteIt`
  (no `ApiKey` finding).
- case 10b (the old break pattern restored, `[ \t]*[\r\n]+[ \t]*(?:[>│|#+][ \t]*)*`) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_NoDetectedPayloadRegresses` and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_DetectionRateMeetsItsFloor` (the six new
  wraps missed `ApiKey` in all three shapes; 89.5 % bare).
- case 10c (`InjectionDetector` on its old set, U+2060 dropped) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_NoDetectedPayloadRegresses`
  (`secret-word-joiner-mid-token` missed in all three shapes) and the U+2060 row of
  `Curia.Domain.Tests.Screening.DetectorTests.R10_8_HiddenCharactersAreDetected`.
- case 10d (`ConnectionStringUriPassword` back in the rule table) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_FalsePositiveRateMeetsItsCeiling` (the
  three URI fences fire `ConnectionStringPassword`, in all three shapes) and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_11_TheKnownEvasionsStillEvade`
  (`evade-connection-string-wrapped-in-userinfo` now detected, in all three shapes).
- case 10e (the line-joined view's two index maps not composed) → RED: both rows of
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D17_AKeySplitByAnInvisibleCharacterIsReportedWhereTheAuthorWroteIt`
  (the reported span does not end on the key's last characters); every corpus gate stayed green,
  since the corpus compares categories and not offsets.

### D18 — R11.27's published-template half was never built *(opened by the MCP plan's Stage 4, 2026-09-22)*

R11.27: every MCP tool description "SHALL be published in this specification as a template
carrying exactly one substituted span … and a build SHALL fail where the served text differs from
the published template". **No template is published anywhere** — searching both documents for any
sentence of any served description returns nothing, R11.19's notice included — and nothing compares
served text to published text. What exists is the other half: the templates are frozen as
constants in `ToolText`, the span is composed from Tables 10 and 11, and `ToolDescriptionTests`
holds the notice outside the span. R11.27's own words for the difference: "a constant is a freeze
against accident and a parser is a freeze against intent", and only the first was built.

The MCP plan's Stage 2 record cites R11.27 for the composed span without saying the publication half
is missing; Stage 4 added three descriptions to the same state rather than three to a published
set, which is why it is recorded now. **Closing it is specification work**: the six templates —
seven with `curia_publish_finding`, nine with R11.30's two — published as normative text in the
errata, and a `PublishedToolTemplates` parser beside `PublishedTable10` and `PublishedTable11`
holding `ToolText` to it. It belongs to the next errata pass, not to a stage about signing.

### D19 — SCREEN read JSON escapes, not what the author wrote *(opened and closed by the screener stage, 2026-09-25)*

**Confirmed by execution**, found by `curia-architect` while D17's fix was being chosen. Ingest
(`IngestPipeline.cs:119`) and the client's pre-send check (`SubmissionBuilder.cs:163`) screened
the canonical envelope text, in which JCS writes a line break as `\n`, a tab as `\t` and a quote
as `\"`. Every rule anchored on `\b`, and the assignment rule's optional quote, read the escape
instead of the separator:

| Body | Bare (what the corpus measured) | JCS envelope (what ingest screened) |
|---|---|---|
| AWS key on line 2, or after a tab | Rejected | **Accepted** |
| JWT on line 2 | Rejected | **Accepted** |
| `api_key = "…"` — the published payload `secret-assigned-entropy` | Rejected | **Accepted** |
| `token = …` / `password=…` on line 2 | Rejected | **Accepted** |
| `ghp_…` / `sk-proj-…` on line 2 | Rejected | Rejected, only by the unanchored rule D17 would narrow |

"On line 2" in the table means at its start. The blind spot is the character after an escape, not
the line: `Context:⏎The key is AKIAIOSFODNN7EXAMPLE` in an envelope was rejected, since a space
precedes the key. What was admitted is a credential at the start of any line after the first or
after a tab, and an assigned secret whose value was quoted.

**Injection annotations had the same blind spot.** `Context:⏎ignore all previous instructions` in
an envelope was Accepted before this stage and is Annotated now; fifteen of case 2's seventeen
payloads below are injection payloads. Posts persisted under `injection/2026-08-17` with an
injection phrase starting a line after the first are served without that annotation, in
`risk_flags` and in the provenance envelope; R10.10's version bump is what lets an operator find
them and re-screen them.

A false negative here writes a live credential into an append-only log. **Why no gate saw it:**
the corpus runner screened bare strings, the one shape only `RaiseFlag` screens in production —
trap 1, a probe of a shape production never produces, in the component whose published rate is a
release criterion.

**Closed** by `ContentScreener.ScreenEnvelope`: every string token, member names included (an
unknown member is ignored, not rejected, so its name is author-chosen), decoded by
`CanonicalStrings` and screened alone, with offsets mapped back so `risk_flags` keeps its unit.
`ScreenText` serves the one bare path. `CanonicalStrings` has fourteen walker cases, two of them
added in review to fence a finding that ends on an escape. The corpus is now measured bare,
enveloped, and enveloped after a line, with a self-check that the envelope carries the entry.

Falsified — each gate's code patched, its tests run, the file restored and the restore checked
clean against git; every case ran against f59797f:

- case 1 (ingest calling `ScreenText` on the canonical envelope) → RED:
  `Curia.Application.Tests.Ingest.IngestPipelineTests.D19_ACredentialOnASecondLineOfTheBodyIsRejected`.
- case 1b (the client's pre-send check calling `ScreenText`) → RED:
  `Curia.Client.Tests.SubmissionBuilderTests.D19_CredentialMaterialOnASecondLineIsRefusedLocally`.
- case 2 (the walker leaving `\n` undecoded, as `n`) → RED:
  `Curia.Domain.Tests.Screening.CanonicalStringsTests.Decodes_every_escape_JCS_writes`;
  five rows of
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D19_ACredentialTheAuthorWroteOnItsOwnLineIsRejectedInAnEnvelope`,
  the line-break row of
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D19_AFindingsOffsetPointsIntoTheCanonicalText`,
  and `Curia.Domain.Tests.Screening.ContentScreenerTests.D19_ACredentialInACodeBlockIsReached`;
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.Every_enveloped_entry_carries_its_content_as_the_body_token`
  (`secret-pem`); and `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_DetectionRateMeetsItsFloor`,
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_NoDetectedPayloadRegresses` (seventeen
  payloads missed enveloped after a line, `secret-wrapped-in-quote-gutter` enveloped as well) and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.R10_24_FalsePositiveRateMeetsItsCeiling`
  (`placeholder-env-example-multiline` and `placeholder-env-dump-pwd-root`, enveloped).
- case 3 (offsets left in the token's unit, not mapped to the canonical text) → RED: both rows of
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D19_AFindingsOffsetPointsIntoTheCanonicalText`
  — a line break, and a surrogate pair, before the key.
- case 3b (the walker skipping member names) → RED:
  `Curia.Domain.Tests.Screening.CanonicalStringsTests.Yields_every_member_name_and_string_value_in_canonical_order`,
  `Curia.Domain.Tests.Screening.CanonicalStringsTests.An_empty_value_is_an_empty_token`,
  `Curia.Domain.Tests.Screening.ContentScreenerTests.D19_AMemberNameIsScreened` and
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.Every_enveloped_entry_carries_its_content_as_the_body_token`
  (`override-basic`).
- case 7 (the corpus envelope's body replaced by a fixed placeholder) → RED:
  `Curia.Domain.Tests.Screening.RedTeamCorpusTests.Every_enveloped_entry_carries_its_content_as_the_body_token`
  ("`override-basic`: the enveloped shape does not carry the entry as its body token").

### D20 — no flag could be upheld *(opened and closed by the moderation stage, 2026-09-26)*

**Found by reading, confirmed by searching every producer**, by `curia-architect` while
choosing this stage: `moderation.applied` had a fold (`FlagProjection.cs`) and no writer
anywhere in `src/`. The Phase 2 record's Stage 8 deferred the writer because *delegated*
moderation is Phase 4; R10.36's *human* arm is not, and nothing deferred it. So Table 11's T1
"≥ 3 questions with no upheld flags", T2/T3's clean record and R7.8's demotion were vacuous —
implemented, tested, guarding nothing — and R6.17's only remedy could not be exercised. F1 had
written that the clause "becomes real the moment flags are servable"; it did not. F1's defect
one layer up.

**Closed** by errata G13's R10.59–R10.61: `ApplyModeration`, reached by no HTTP route and in
production only by `curia-operator moderate`, writes a record carrying the post's digest and the
flags it adjudicates; *upheld* is decided per flag by the reviewing record that names it,
automated records change no flag's state, and forbidden records are ignored by every fold (PR
#59's Task B1, confirmed by execution first — below). `ModerationLoopTests` drives an author from
T1 to T0 by upholding one flag and back by restoring it.

**Both halves of PR #59's Task B1 were confirmed by execution before they were fixed.** Carried
here from "Still unverified", where they sat until then. Read while writing that plan; run by the
moderation stage (its Task 2, Step 2) against the code as it stood, before anything changed.
Exactly the two tests that ask the question failed —
`Curia.Domain.Tests.Moderation.ModerationTests.An_automated_quarantine_is_not_an_upheld_flag`
(`Assert.False() Failure`, expected `False`, actual `True`: `ModerationPolicy.IsUpheld` counted an
automated quarantine as upheld) and
`Curia.Domain.Tests.Moderation.ModerationTests.An_automated_withholding_does_not_stop_a_post_being_served`
(`Assert.True() Failure`: `MayServe` honoured an automated withholding, which R10.36 forbids) —
printing `Failed!  - Failed:     2, Passed:    27, Skipped:     0, Total:    29` for
`Curia.Domain.Tests.dll`. `IsUpheld` is gone: `ModerationPolicy.UpheldFlags` folds reviewing
records only, and every fold ignores a record R10.61's table refuses.

Falsified by the stage's own runner (its Task 11) — each gate's code patched, its tests run, the
file restored with a plain copy and the restore checked clean against git; every case ran against
50e3955, and after the last one a `--no-incremental` rebuild ran every gate green unpatched (trap
18). Test names and message lines are as the runner printed them, `…` marking where an argument
list is elided. Its filter drops xUnit's
`Expected:`, `Actual:` and `Collection:` lines and exception lines, so the details it could not
print are quoted from a hand re-run of the same patch and marked *hand-run*:

- case 1 (the upheld fold's automated guard removed) → RED:
  `Curia.Domain.Tests.Moderation.ModerationTests.An_automated_quarantine_is_not_an_upheld_flag`,
  `Curia.Domain.Tests.Moderation.ModerationTests.R10_61_AdjudicatedFlagsCountOnlyReviewingRecords`
  and
  `Curia.Domain.Tests.Moderation.ModerationTests.An_automated_dismissal_does_not_release_a_flag_a_human_upheld`
  (`Assert.Empty() Failure: Collection was not empty` once, `Assert.Equal() Failure: Collections differ` twice).
- case 2 (the permitted-cell guard removed from `MayServe`) → RED:
  `Curia.Domain.Tests.Moderation.ModerationTests.An_automated_withholding_does_not_stop_a_post_being_served`
  and
  `Curia.Domain.Tests.Moderation.ModerationTests.An_automated_restore_does_not_serve_what_a_human_withheld`
  (`Assert.True() Failure`, `Assert.False() Failure`). The second is B1's mirror, one test more
  than the plan's table listed; it reads the same guard.
- case 3 (upholding keyed to the category again: `HasUpheldFlag` true while any category's latest
  record quarantines or withholds) → RED:
  `Curia.Application.Tests.Projections.FlagProjectorTests.R10_36_AQuarantineMakesThePostUnservable`,
  `Curia.Application.Tests.Projections.FlagProjectorTests.R10_61_AFlagRaisedAfterAWithholdingIsNotUpheldUntilARecordNamesIt`
  and
  `Curia.Application.Tests.Projections.FlagProjectorTests.R10_61_ARecordWithoutAdjudicatesStillWithholdsAndUpholdsNothing`
  (`Assert.False() Failure` three times); `ARestoreMakesThePostServableAgain` stayed green, as it
  should. **Case 3 is not the fence for the spec's Decision 5** (a flag raised after a withholding
  is not upheld by it): it reds the late-flag test at its first `HasUpheldFlag` exactly as it reds
  the other two. Decision 5 is held by that test's own observation of lateness and by
  `ApplyModerationTests`' late-flag fact, each falsified in the review rounds below.
- case 8 (the writer emitting an empty `adjudicates`) → RED:
  `Curia.Api.Tests.ModerationLoopTests.R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone`
  and
  `Curia.Api.Tests.ModerationLoopTests.R10_61_AnUpheldFlagDemotesItsAuthorAndARestoreReinstatesIt`,
  printing `curia-operator moderate … --effect dismiss … exited 2: error: That record would change nothing, and R10.39 counts records (curia/moderati`
  (cut at the runner's 240 characters) and `Assert.Equal() Failure: Values differ`. *Hand-run:*
  the first fails at its dismissal, refused as `curia/moderation/no-op` because a record naming
  no flag dismisses nothing; the second fails at the refused answer, `Expected: Forbidden`,
  `Actual: Created` — the author still at T1 after the withholding that should have upheld its
  flag. This is the case that shows Table 11's clause now carries information.
- case 9 (the writer recording its moderator as `automated`) → RED: both `ModerationLoopTests`
  above, each at its first withholding, printing
  `curia-operator moderate … --effect withhold … exited 2: error: That moderator may not take that action (R10.36) (curia/moderation/not-permitted): mo`
  twice.
- case 10 (the writer's rationale screen made to refuse nothing) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_60_ACredentialInTheReasonIsRefusedAndNothingIsAppended`
  (`Assert.False() Failure`).
- case 11 (the no-op refusal removed) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_59_ARestoreAfterAProactiveWithholdingIsARecordARestoreOfAServablePostIsNot`,
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_39_ADismissalOfOpenFlagsIsARecordAndADismissalOfNothingIsNot`
  and
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_39_ASecondIdenticalRecordIsRefusedAsANoOp`
  (`Assert.False() Failure` three times).
- case 13 (the writer spelling the effect with `ToString()` instead of its wire name) → RED:
  `Curia.Api.Tests.FlagEndpointTests.R10_36_AWithheldPostStopsBeingServedAndIsNotDeleted` and
  `Curia.Api.Tests.BatchRetrievalTests.R9_18_OneItemPerElementInOrderAndNothingOmitted`
  (`Assert.Equal() Failure: Values differ`, `Assert.Equal() Failure: Collections differ at index 1`).
  Both would have stayed green had `ForumFixture` still hand-built its withholdings (trap 16); it
  now withholds through the writer.

**Gates added in the review rounds carry their own evidence, not a Task 11 case.** Each was shown
red by hand in the round that added it. Most were red under a mutation of the code they guard,
restored with a plain copy and checked with `cmp`. Three were red against the code as it stood
before their fix, and green after it, with nothing to restore: the `init` accessor, the overtaken
record and the writer's blank operator name.

- `Curia.Domain.Tests.Moderation.ModerationTests.A_with_expression_cannot_make_what_a_record_adjudicates_default`
  was red until `Adjudicates` was normalized in its `init` accessor as well as its initializer
  (`System.InvalidOperationException : This operation cannot be performed on a default instance of ImmutableArray<T>.`).
  `An_automated_restore_does_not_serve_what_a_human_withheld`, with `MayServe`'s guard narrowed
  to withholdings, went red alone (`Expected: False`, `Actual: True`) while the other 37
  `ModerationTests` passed.
- `Curia.Application.Tests.Projections.FlagProjectorTests.R10_61_AFlagRaisedAfterAWithholdingIsNotUpheldUntilARecordNamesIt`
  now observes lateness through `FlagDirectory.Join`: with the late raise deleted it went red
  (`Assert.Single() Failure: The collection was empty`), and with the raise moved above the
  withholding it went red with `the flag must be raised after the withholding`.
- `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_ALateFlagIsNotUpheldByAnEarlierWithholdingAndTheNextWithholdingAdjudicatesIt`
  went red with the writer's no-op judged on `MayServe` alone, beside
  `R10_39_ADismissalOfOpenFlagsIsARecordAndADismissalOfNothingIsNot` and the human-only fact.
- `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_60_AFlagOfTheSameCategoryOnAnotherPostIsNeitherNamedNorAdjudicated`
  went red alone, the writer naming another post's flag, with the post filter dropped from its
  `adjudicates` derivation (`ApplyModeration.cs:115`).
- `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_60_EveryRecordTheWriterWritesIsHumanSoNoAutomatedRecordNamesAFlag`
  went red alone with a quarantine's leaf written as `automated`; with the authorized moderator
  set to `DelegatedAgent` it failed on all four records, expecting `human` and reading
  `delegated_agent`, beside `R10_60_ARecordNamesThePostItsDigestAndTheFlagsItAdjudicates`.
- `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_39_ARecordDecidedOnAViewAnotherRecordOvertookIsRefusedAndNotAppended`
  was red against the writer's former second read of the post's stream (`Assert.False() Failure`:
  the overtaken record was appended, two records for one decision), and green once the expected
  version came from the read the decision was made on.
- `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_59_ABlankOperatorNameIsRefusedAndNothingIsAppended`
  was red before the writer's guard: an `operator:` actor with a blank name was recorded.
  `Curia.Api.Tests.OperatorModerationTests.ABlankValueIsAUsageErrorAndWritesNothing`, with the
  verb's `IsNullOrWhiteSpace` check reverted and the writer's guard kept, went red on all three
  rows — `post` on an unhandled `ArgumentException`, `by` and `reason` exiting 2 rather than 1 —
  so the theory pins the verb rather than leaning on the writer.
- `Curia.Api.Tests.ModerationLoopTests.R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone`'s
  reviewing guard on the public fold, four ways: deleted, red at the time-to-action equality, the
  public side timing flag B at `00:30:00` against the private join's `01:30:00`; deleted with both
  equalities, red at the clock-anchored `[90, 120]` minutes; deleted with that too, red at
  `Assert.Single(publicUpheld)` (`Assert.Single() Failure: The collection was empty`); and with
  the private side's rule reverted to the first record naming the flag, red again. With the two
  out-of-rule automated records also removed, the deleted guard stays green — which is why the
  test appends them.

**The final review's wave** changed what this entry closes. The code now reads as follows:

- **A record acts only on the category it cites** (R10.61, amended in place).
  `ModerationPolicy.ServingEffect` folds each category's latest permitted quarantine or withholding
  until a permitted restore citing that category, and `MayServe` is `ServingEffect(h).IsEmpty`: a
  post is served only while no category holds it, and a dismissal holds and releases nothing. Read
  as one servable bit, a restore in `spam` served a post a human had withheld for a credential leak,
  the flag behind that withholding still upheld — the final review's probe, confirmed by execution.
- **Two refusals**, content-free and after the no-op check: `curia/moderation/restore-of-unheld-category`
  and `curia/moderation/dismissal-of-held-category`.
- **The escalation is a record.** The no-op check compares the two maps element by element, so a
  human withholding after a human quarantine in the same category is a record, as is a hold in a
  second category. It had been refused as a no-op, leaving restore-then-withhold as the only way to
  escalate: the post served in between, and a permanent restore nobody meant.
- **The reason guard** (R10.60, R10.62). Every text is compared as a derived copy: hidden characters
  (`HiddenCharacters`, now public) dropped, unpaired surrogates and all 66 noncharacters made
  U+FFFD, then NFKC, invariant lower-casing and whitespace collapse. A reason is refused before
  anything is appended, as `curia/moderation/rationale-discloses-flag` with only `field=raised_by`
  or `field=rationale`, when it repeats either of two things for a flag on the post. The first is
  the flag's raiser, with or without its `scheme://`, in a form of at least 16 characters without
  white space, as a whole token: nothing on either side continues it as an id. An ASCII letter or
  digit continues one, and so does a run of `-._~` that an ASCII letter or digit follows; a full
  stop, a space, a CJK or accented letter is a boundary. The second is any 32 consecutive characters
  of a rationale at least that long. The raiser floor is the architect's ruling on the wave's own
  finding: enrolment accepts any non-blank id (`ForumEndpoints.cs:388` at 3c8f17d, D4), and a short
  id that is itself a word, `e` or `spam`, would otherwise refuse every reason using it. A raiser
  below the floor leaves only itself unprotected. The noncharacter mapping closes the re-review's
  Critical:
  U+FFFE in any flag's rationale or raiser made every `RecordAsync` on its post throw, since .NET's
  ICU-backed NFKC throws on it, and the private store that holds it is append-only.
  Every flag on the post is checked, whatever its category or state. `FlagDirectory.RationalesByFlag`
  is the one source the guard and the listing read, and `curia-operator flags` prints `raised_by`
  only under `--raisers`.
- **`attest-owner`'s blank values**, in `moderate`'s shape: a blank `--agent`, `--by` or `--reason`
  is a usage error, and `AttestOwner` refuses an `operator:` actor with a blank name
  (`curia/attest/blank-operator-name`).

Task 11's record above stands for 50e3955, the tree it ran against. Against this wave's tree, 21 of
its runner's 22 edits still match exactly once. Case 11's does not: `var noOp =
ModerationPolicy.MayServe(before)` is gone, since the no-op check now compares `ServingEffect` maps.
Case 2's now lands in `ServingEffect`, which `MayServe` reads.

Falsified in the final-review wave, against this wave's tree (950a8e5 with the wave's changes, before
they were committed): each fence's code patched, its tests run, the file restored with a plain `cp`
and checked with `cmp`; after the last, one `--no-incremental` Release build and every gate green
unpatched. Messages are as the runner printed them:

- F1 (the fold keyed to one fixed category) and F2 (a restore clearing every hold) → RED, each:
  `Curia.Domain.Tests.Moderation.ModerationTests.R10_61_ARestoreInAnotherCategoryDoesNotServe`,
  `Curia.Domain.Tests.Moderation.ModerationTests.R10_61_TwoHoldsNeedTwoRestores` (`Assert.False() Failure` twice) and
  `Curia.Domain.Tests.Moderation.ModerationTests.R10_61_AServedPostHasNoUpheldFlag`, the CsCheck property finding its own counterexample
  (`CsCheck.CsCheckException : Set seed: …`, 3 shrinks under F1, 1 under F2).
- F3 (the restore guard deleted) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_TheProbeSequenceIsRefused` alone (`Assert.False() Failure`).
- F4 (the dismissal guard deleted) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_ADismissalInAHeldCategoryIsRefused` alone (`Assert.False() Failure`).
- F5 (the map collapsed to the set of effects held, category dropped) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_AHoldInASecondCategoryIsNotANoOp` alone (`curia/moderation/no-op`).
- F6 (the map collapsed to servability, the comparison before this wave) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_AHoldInASecondCategoryIsNotANoOp` and
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_AHumanWithholdingAfterAHumanQuarantineIsARecord` (`curia/moderation/no-op` twice).
- F7 (the maps compared by category alone, the effect dropped) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_61_AHumanWithholdingAfterAHumanQuarantineIsARecord` alone (`curia/moderation/no-op`).
- G1 (the raiser check deleted) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ARaiserInTheReasonIsRefusedAndNothingAppended`,
  all four rows of `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ARaiserMatchesCaseFoldedAndSchemeless`,
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AFlagOfAnotherCategoryIsChecked` and `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_TheRefusalNamesNoFlagOrText`
  (`Assert.False() Failure` seven times); re-run after the raiser floor landed, it also reds the floor's
  three refusing facts, `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AnEchoBeforeAFullStopIsRefused`,
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_SixteenCharactersAreCheckedFifteenAreNot` and `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AShortHostIsCaughtByItsFullForm` (ten).
- G2 (the raiser compared ordinally, unnormalized) → RED: the three rows of
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ARaiserMatchesCaseFoldedAndSchemeless` whose case or form differs; the lower-case
  schemeless row stayed green, as it should.
- G3 (the raiser matched only with its scheme) → RED: the three schemeless rows of the same theory;
  the row with the scheme stayed green.
- G4a (the quote length raised to 33) and G4b (lowered to 31) → RED, each:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ThirtyTwoCharactersOfARationaleAreRefusedThirtyOneAreNot` alone (`Assert.False() Failure`;
  `curia/moderation/rationale-discloses-flag field=rationale`).
- G5 (full containment instead of windows) → RED: both rows of `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_APartialQuoteIsRefused`,
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ThirtyTwoCharactersOfARationaleAreRefusedThirtyOneAreNot` and
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_TheRefusalNamesNoFlagOrText` (`Assert.False() Failure` four times).
- G6 (the check narrowed to the flags the record adjudicates) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AFlagOfAnotherCategoryIsChecked` alone (`Assert.False() Failure`).
- G7a (the refusal echoing the flag's id) → RED: every test that reads the detail, seven (eight on the
  amended tree, with `R10_62_AnEchoBeforeAFullStopIsRefused`), among them
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_TheRefusalNamesNoFlagOrText`
  (`Expected: "field=raised_by"`, `Actual:   "field=raised_by flag=01M3…"`); G7b (echoing the
  matched text) → RED: four, the same test among them (`Actual:   "field=rationale matched=is an advert for a storefr"···`).
- H1 (`attest-owner` checking length, not white space, on all three values) → RED: all three rows of
  `Curia.Api.Tests.OperatorAttestationTests.R4_30_ABlankValueIsAUsageErrorAndWritesNothing` —
  `agent` on `System.ArgumentException : The value cannot be an empty string or composed entirely of whitespace. (Parameter 'agentId')`,
  `by` and `reason` exiting 2 rather than 1.
- H2 (`AttestOwner`'s guard checking emptiness, not white space) → RED:
  `Curia.Application.Tests.Projections.AgentStandingProjectorTests.R4_30_ABlankOperatorNameIsRefusedAndNothingIsAppended`
  alone (`Assert.False() Failure`).
- K1 (the in-memory flag-detail adapter normalizing the rationale to NFC on write) → GREEN against
  the verbatim test as it stood, which held precomposed text only; RED once it carries `e` followed by
  U+0301: `Curia.Application.Tests.InMemoryFlagDetailStoreContractTests.R11_32_AnAppendedDetailReadsBackVerbatim`
  (`Assert.Equal() Failure: Values differ`).
- K2 (`RaiseFlag`'s aggregate made `flag:` plus the event id less its first character) → GREEN
  against the prefix assertion as it stood; RED against the equality:
  `Curia.Application.Tests.Moderation.RaiseFlagTests.R10_62_AFlagEntersTheLogAsItsKindAndACommitmentAlone`
  (`Expected: "flag:01M3ESC9G0YZB9E8240F9YEP25"`, `Actual:   "flag:1M3ESC9G0YZB9E8240F9YEP25"`).

The raiser floor and whole-token match, falsified the same way against the amended tree; G1–G3 and
G7a were re-run there too and went red on the rows above:

- L1 (the raiser floor removed) → RED: the two rows of
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AOneCharacterRaiserCannotShieldItsPost` that use `e` as a word of its own,
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_SixteenCharactersAreCheckedFifteenAreNot` and
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AShortHostIsCaughtByItsFullForm`, each at `curia/moderation/rationale-discloses-flag field=raised_by`.
  The two rows whose reason is "Reviewed: advertising." stayed green: with the floor gone, the
  whole-token match still keeps `e` inside a word from counting.
- L2a (the floor raised to 17) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_SixteenCharactersAreCheckedFifteenAreNot` and
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AShortHostIsCaughtByItsFullForm` (`Assert.False() Failure` twice). L2b (lowered to 15)
  → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_SixteenCharactersAreCheckedFifteenAreNot` alone (`curia/moderation/rationale-discloses-flag field=raised_by`).
- L3 (a plain substring match instead of a whole token) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ARaiserInsideALongerIdIsNotARepeat` alone (`curia/moderation/rationale-discloses-flag field=raised_by`).
- L4 (punctuation counted as a token character) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AnEchoBeforeAFullStopIsRefused`
  alone (`Assert.False() Failure`).
- L5 (the full form not checked, only the schemeless one) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AShortHostIsCaughtByItsFullForm` alone (`Assert.False() Failure`).
- L6 (a form holding white space checked) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ASpacedRaiserCannotRefuseAPhrase` alone
  (`curia/moderation/rationale-discloses-flag field=raised_by`).

The re-review's round — the noncharacter Critical, the id-based boundary, hidden characters — falsified
the same way against its own tree:

- A1 (noncharacters not mapped: the throwing `Normalize`) → RED in two suites:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ANoncharacterInARationaleDoesNotStopTheRecord`,
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ANoncharacterInARaiserDoesNotStopTheRecord` and
  `Curia.Api.Tests.OperatorModerationTests.R10_62_ANoncharacterInAFlagDoesNotStopItsPostBeingModerated`,
  each `System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')`.
- B1 (the boundary from Unicode letters and digits again) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ACjkNeighbourDoesNotHideARaiser` and `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AnAccentedNeighbourDoesNotHideARaiser`.
- B2 (`-._~` never continuing an id) → RED: `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_ARaiserContinuedByAnIdCharacterIsNotARepeat`
  alone (`curia/moderation/rationale-discloses-flag field=raised_by`).
- B3 (`-._~` continuing an id whatever follows them, the ruling read literally) → RED:
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AnEchoBeforeAFullStopIsRefused` alone. This is why a run of `-._~` continues an id only
  when an ASCII letter or digit follows it: read literally, a raiser echoed before a sentence's full
  stop is published.
- C1 (hidden characters not stripped) → RED: all three rows of `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_62_AHiddenCharacterInsideARaiserDoesNotHideIt`.
- K3 (the directory made to believe a rewritten row, the listing test's earlier assertions removed so
  the raiser assertion is reached) → GREEN with the listing run without `--raisers`, where the
  assertion could not fail; RED with it: `Curia.Api.Tests.OperatorModerationTests.R10_62_ARewrittenOrLostPrivateRowIsCountedInTheListing`
  (`Assert.DoesNotContain() Failure: Sub-string found`).

### D21 — the log served every flag's raiser and rationale to anyone *(opened and closed by the moderation stage, 2026-09-26)*

**Confirmed by execution** on 2026-09-25 against a pristine archive of the tree: an anonymous
walk of `GET /v1/log/entries/{i}` returned a `flag.raised` leaf carrying `raised_by`, the
rationale and the post (errata G13, finding 2). R6.46/R6.47 make every event a leaf and R6.51
serves each verbatim, so R10.44, G3's holding and the `curia_flag` description were false. The
R10.44 tests held the two listing routes by name and never reached the log — trap 15, and the
reason for trap 20.

**Closed** by R10.62 and R11.32: a flag enters the log as `flag.committed` — its kind and a
salted commitment, on its own aggregate, with no actor — and its post, raiser, rationale and
salt go first to `flag_details` (db/0004, INSERT/SELECT only). `FlagDirectory` serves R7.18's
views from the join, believes a private row only if it opens its entry's commitment, and counts
every skip. `FlagPrivacyGateTests` walks every registered surface from the endpoint data
source, anonymously and as an uninvolved agent. It was red on the log route before the writer
changed: six lines, each naming `GET /v1/log/entries/{index:long}` — the rationale, the raiser and
the post's id, each served to an anonymous caller and to an uninvolved agent. The new
`conformance/acta/flag-committed-entry` vector pins the entry kind in C# and in Rust, with no leaf
computation changed (R15.1). **Flags raised before this stage stay public forever**; since no
deployment is hosted, the only logs that held any were test and local ones.

Falsified by the same runner, recorded the same way:

- case 4a (`raised_by` written into the `flag.committed` payload) → RED:
  `Curia.Api.Tests.FlagPrivacyGateTests.R10_60_AfterModerationTheRecordIsPublicAndStillNamesNoRaiser`
  and
  `Curia.Api.Tests.FlagPrivacyGateTests.R10_62_NoSurfaceServesAFlagsRaiserRationaleOrUnadjudicatedPost`,
  printing `GET /v1/log/entries/{index:long} served the raiser's identity to an anonymous caller (/v1/log/entries/4)`,
  the same to an uninvolved agent, and both again at `/v1/log/entries/10` — the two facts share
  one fixture, so their flags sit at indices 4 and 10.
- case 4b (the rationale written into the payload) → RED: both facts, the same four lines with
  `served the flag's rationale`.
- case 4c (`post_id` written into the payload) → RED: both facts, the same four lines with
  `served the flagged post's id in the flag's own leaf`.
- case 5 (the gate's driver for `GET /health` removed) → RED: both facts, each printing
  `Registered surfaces this gate cannot drive (R14.9: a surface the enumeration reaches and the gate cannot evaluate is a failure, never an omission): GET /health`.
- case 5b (the gate's log walk made to report an empty log) → RED: both facts, each printing
  `the log walk never met the question's own leaf -- a defect in this gate, not in the Forum`.
- case 6 (`RaiseFlag` writing the event before the detail row) → RED:
  `Curia.Application.Tests.Moderation.RaiseFlagTests.R10_62_AFailedDetailAppendLeavesNoCommitmentForTheJoinToSkip`
  and
  `Curia.Application.Tests.Moderation.RaiseFlagTests.R11_21_ARationaleCarryingANulIsRefusedAndNothingIsWritten`
  (`Assert.Empty() Failure: Collection was not empty` twice). *Hand-run:* the first fails at the
  join's skip count, `Collection: [["flag.committed: no detail"] = 1]`; the second because its
  refused row now leaves a `flag.committed` entry in the log.
- case 7 (the join's commitment check made to pass any row) → RED:
  `Curia.Application.Tests.Projections.FlagDirectoryTests.R10_62_ATamperedDetailIsSkippedAndCounted`
  (`Assert.Empty() Failure: Collection was not empty`).
- case 12a (`adjudicates` dropped from the record) → RED:
  `Curia.Api.Tests.ModerationLoopTests.R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone`
  and
  `Curia.Api.Tests.ModerationLoopTests.R10_61_AnUpheldFlagDemotesItsAuthorAndARestoreReinstatesIt`
  (`Assert.Equal() Failure: Values differ`). *Hand-run:* the first throws
  `System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.`
  at `payload.GetProperty("adjudicates")`; the second fails at the refused answer,
  `Expected: Forbidden`, `Actual: Created`, since a record naming no flag upholds nothing.
- case 12b (`digest` dropped from the record) → RED in two suites:
  `Curia.Api.Tests.ModerationLoopTests.R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone`
  and
  `Curia.Application.Tests.Moderation.ApplyModerationTests.R10_60_ARecordNamesThePostItsDigestAndTheFlagsItAdjudicates`;
  the runner printed only the two names. *Hand-run:*
  `System.Collections.Generic.KeyNotFoundException : The given key was not present in the dictionary.`
  at `payload.GetProperty("digest")`, where the public derivation ties each record to its
  accepted post (R6.25), and
  `System.Collections.Generic.KeyNotFoundException : The given key 'digest' was not present in the dictionary.`
  in memory.
- case 14 (one digit of `conformance/acta/flag-committed-entry/expected.leaf` changed, `66128f1f`
  to `76128f1f`) → RED in all four runners:
  `Curia.Canon.Tests.Vectors.ActaLeafVectorTests.R6_46_EveryVectorCanonicalizesUnderThePureProfileAndHashesToItsLeaf`,
  `Curia.Domain.Tests.Acta.LogLeafTests.R6_46_AnEventRendersToTheConformanceVectorsLeafInputAndLeaf(vector: "flag-committed-entry")`
  and
  `Curia.Client.Tests.ActaLeafRecomputationTests.R6_46_TheClientRecomputesEveryPublishedLeaf(name: "flag-committed-entry")`,
  each printing `Assert.Equal() Failure: Strings differ`; and `curia-testis`, printing
  `test acta ... FAILED` and
  `[FAIL] acta/flag-committed-entry: leaf hash: expected 76128f1fe528857613fef8e66d312b65214bfd0d7b7a7aa82ca5e0ab2cf2219c, got 66128f1fe528857613fef8e66d312b65214bfd0d7b7a7aa82ca5e0ab2cf2219c`.
  *Hand-run*, because the Canon test names no vector:
  `Expected: "76128f1fe528857613fef8e66d312b65214bfd0d7b7a7aa82c"···`,
  `Actual:   "66128f1fe528857613fef8e66d312b65214bfd0d7b7a7aa82c"···`.
- case 15 (`flag_details` granted `UPDATE` and `DELETE`, and its `REVOKE` removed) → RED:
  `Curia.Infrastructure.Tests.FlagDetailGrantTests.R11_32_TheAppRoleCannotUpdateAFlagDetail` and
  `Curia.Infrastructure.Tests.FlagDetailGrantTests.R11_32_TheAppRoleCannotDeleteAFlagDetail`
  (`Assert.Throws() Failure: No exception was thrown` twice).
- case 16 (`FlagCommitment` switched to `CanonicalizeWithNfc`) → RED:
  `Curia.Domain.Tests.Moderation.FlagCommitmentTests.R10_62_TheCommitmentIsOverPureRfc8785WithNoNormalization`
  alone (`Assert.Equal() Failure: Strings differ`); the other six `FlagCommitmentTests` stayed
  green, since every other input is ASCII, where NFC changes nothing. *Hand-run:*
  `Expected: "sha256:be4b171a3c8fd1fa3810e10eb484589bcc6c77bdada"···`,
  `Actual:   "sha256:552d7e392ebd59333804a5afc9d6f3b98d23ed5e02a"···` — the NFC form, which
  composes `cafe` followed by U+0301 into U+00E9.

**Gates added in the review rounds**, falsified by hand as under D20:

- `Curia.Application.Tests.Projections.FlagDirectoryTests.R11_9_TheDirectoryRebuildsFromBothStores`
  is a real rebuild: a fresh read of both stores, the private rows reversed, and one committed
  entry with no row. With `FlagDirectory.Join` pairing entries and rows by position rather than
  by event id it went red (`Assert.True() Failure`); the same mutation left the drill as first
  written green.
- `Curia.Api.Tests.FlagPrivacyGateTests.R10_62_NoSurfaceServesAFlagsRaiserRationaleOrUnadjudicatedPost`
  was hardened to judge every leaf on every route, to look for the salt, to require every flag
  listing to be empty for both callers, and to prove the bystander's credential works. Five
  mutations went red against it, the first four having passed the gate as first written:
  `GET /v1/flags`' `(own)` filter removed (`GET /v1/flags served the flag listing to an uninvolved agent holding entries (1), …`);
  the `(own)` discharge on `GET /v1/posts/{postId}/flags` forced (the same line for that route);
  the salt written into the payload
  (`GET /v1/log/entries/{index:long} served the flag's salt to an anonymous caller`, and to an
  uninvolved agent); the bystander's DPoP `htu` broken
  (`GET /v1/flags never answered the uninvolved agent 200 …`); and the writer as it stood before
  this stage (`no private row holds the flag …`, beside the six log leak lines).
- `Curia.Domain.Tests.Acta.LogLeafTests.R6_46_TheTheoryListsEveryActaVectorOnDisk` went red with
  the theory's `flag-committed-entry` row removed, naming `flag-committed-entry` as a vector on
  disk that the theory does not list.

### D22 — an enrollment could add a key to any identity, or replace the key behind any `kid` *(opened by `curia-architect` on 2026-09-26 and closed by the enrollment stage)*

**Found by reading, confirmed by execution**, by `curia-architect` while choosing the stage after
the moderation stage. The two texts disagreed:
- `PostgresAgentKeyStore`'s own remarks called its last-write-wins key material "a real hazard, and
  one this increment does not close".
- Errata G5 said the opposite about the endpoint above it: "a false enrollment can only impersonate
  an agent whose private key the caller already holds". That rested on "R4.11's proof of
  possession", which was never built.

Run on 2026-09-26 against a pristine archive of `main` at 9829a04, over Postgres: an
unauthenticated `POST /v1/agents` naming an enrolled victim's identifier. Five of the probe's nine
lines, abridged; errata G14's finding quotes the same five, with more of the two enrollment
receipts.

```
attacker enrol (new kid, victim id): 201 {…"kid":"attacker-6e2dda3a",…}
attacker token: obtained
attacker question as victim: 201 {"post_id":"01M0572TG0R4YJJNHM5KWP4SWW", …}
overwrite enrol (victim kid, other bytes): 201 {…"kid":"victim-6e2dda3a",…}
victim token after overwrite: Token request failed (401): … "Signature does not verify"
```

**What that meant.** Every post and JWKS publishes an agent's identifier and `kid`. So anyone could:
- post as any agent, at its tier;
- make every post an agent had signed fail verification, and lock it out. `curia-testis` failed them
  too, since it reads the JWKS the Forum serves.

A second route to the token half survived this entry's close: D26.

The reference client's default identifier, `urn:curia:agent:<slug>`, also merged honest agents that
chose the same local name.

**Why no gate saw it.** Every test enrolled each agent under an identifier of its own, so the one
input that breaks "an identity's key is its own" never ran. That is trap 19's shape, and trap 21's.

**Closed** by errata G14's R4.31 and R4.32:
- `IAuthorKeyRegistry.EnrollAsync` replaces the general register on the port. Both adapters apply
  `KeyEnrollment.Decide` atomically: Postgres under a per-identifier advisory lock, the in-memory
  adapter under one `Lock`.
- `EnrollIdentity` reads the `kid` bound by the log's `agent.enrolled` before it asks the store, and
  refuses any other `kid`, even one the store holds for that identity
  (`EnrollIdentityTests.R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt`, case 19).
  `EnrollAgent` refuses it again when it records.
- A store that has lost an enrolled identity's row re-registers the bound `kid` dated from the
  enrollment, R4.31's one exception. The date is the instant the log recorded the enrollment. It
  equals the lost row's start only under the test fixture's single clock; on a real clock it can be
  slightly later, but never later than an admitted post, since no post is admitted before the log
  holds the enrollment. So R6.31 holds.
- db/0005 leaves the app role UPDATE on the validity window only.
- `RegisterAsync` is internal, and never writes material or relabels an algorithm. Its ownership
  clause, `existing.agent_id = EXCLUDED.agent_id`, refuses another identity presenting the exact key
  a `kid` names, which the JWKS publishes; without it, `LEAST` let that caller close or backdate the
  victim's window
  (`PostgresAgentKeyStoreTests.AnotherAgentPresentingTheExactKeyIsRefusedAndMovesNoWindow`, case 18).

`EnrollmentBindingTests` drives the attack over HTTP and verifies the victim's earlier post under
`curia-testis`. Its negative control shows that the verifier refuses the overwriter's key. It holds
each refusal's served detail, and holds a lost row's recovery to the key set served before the loss.

**What it leaves.**
- **Clocks that disagree.** With several Forum processes whose clocks are skewed, or a wall clock
  that steps backwards, a post's `server_ts` can fall before a recovered key's start, and after a
  lost row is recovered that post stops verifying. This project runs one process, and hosts none.
- **Identities enrolled in the `5a48fcb..5f96f51` window.** On 2026-08-17, for about five hours,
  keys were durable and standing lived in process memory. Such an identity can have posts older than
  its first `agent.enrolled`; if its row is lost, recovery dates its key after them, and they stop
  verifying.
- **db/0005 does not verify its own result.** A `REVOKE` by a role that did not grant the
  table-level UPDATE would leave it in place, and the migration would still succeed. A trailing
  `has_column_privilege` check belongs with a migration runner, which this project does not have
  (`SchemaMigrations.RenderAll`'s remarks). No committed test runs the pre-G14 upsert under the
  grant; case 7 and the plan's note on it cover that.

**Falsified** by the stage's own runner, in its Task 7's run on 30a1527 (`falsify.log`). Each gate's
code was patched, its tests run, and the file restored with a plain copy; each restore was proved
twice, by the kept copy's bytes and by `git diff --quiet`. The runner calls a suite RED only on a
`Failed!` line, and fails the run on anything else. All twenty-one cases went red, in thirty suite
runs, and the log's last line is the runner's own `runner exit: 0`. After the last case, a
`--no-incremental` Release rebuild ran all eleven assemblies green unpatched, and the architecture
project green in Debug too (trap 18, D16). The stage's final wave re-ran the whole runner, by then
thirty-eight cases, on e54ba15: every case went red in every suite it ran, in sixty-three suite
runs, every restore was clean, the log ends `runner exit: 0`, and the same unpatched gates followed
green. D26 and D27 quote that run, and the plan's Task 7 table reads what it printed. The lines
below are the 30a1527 run's, for this entry's cases, as printed; 13 and 14 are D23's, and 20 is
D24's. The plan's Task 7 table says what each case must fail, and why the facts that stay green
should:

```
[1] the rule registers a second kid for an enrolled identity
[1] tests/Curia.Application.Tests RED
    Failed!  - Failed:     6, Passed:    21, Skipped:     0, Total:    27, Duration: 51 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_31_ASecondKidForAnEnrolledIdentifierIsRefused
      System.InvalidOperationException : expected a refusal, got register
  FAILED Curia.Application.Tests.InMemoryAuthorKeyRegistryContractTests.R4_31_ASecondKidForAnEnrolledIdentifierIsRefusedAndRegistersNothing
      System.InvalidOperationException : expected a refusal, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = mallory-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 8/1/2026 12:00:00 AM +00:00, NotAfter =  }
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_31_KidsAndAlgorithmsAreComparedWithTheirCase
      System.InvalidOperationException : expected a refusal, got register
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_31_AHeldKeysMaterialUnderANewKidIsRefused
      System.InvalidOperationException : expected a refusal, got register
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_31_AKeyWhoseWindowHasClosedIsStillHeld
      System.InvalidOperationException : expected a refusal, got register
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_RacingEnrollmentsOfOneFreshIdentityLeaveOneKeyAndOneRecord
      Assert.Single() Failure: The collection contained 8 items
      Collection: [RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = alice-0, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 9/26/2026 12:00:00 PM +00:00, NotAfter =  }, RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, K
[1] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 105 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAuthorKeyRegistryContractTests.R4_31_ASecondKidForAnEnrolledIdentifierIsRefusedAndRegistersNothing
      System.InvalidOperationException : expected a refusal, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = mallory-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 8/1/2026 12:00:00 AM +00:00, NotAfter =  }
[1] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[2] material compared by length, not by bytes
[2] tests/Curia.Application.Tests RED
    Failed!  - Failed:     3, Passed:    14, Skipped:     0, Total:    17, Duration: 25 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_32_MaterialIsComparedByContent
      Assert.False() Failure
      Expected: False
      Actual:   True
  FAILED Curia.Application.Tests.InMemoryAuthorKeyRegistryContractTests.R4_32_ReEnrollingAKidWithOtherBytesIsRefusedAndTheOriginalStands
      System.InvalidOperationException : expected a refusal, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = alice-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 7/1/2026 12:00:00 AM +00:00, NotAfter =  }
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_32_TheSameKidWithOtherBytesIsRefused
      System.InvalidOperationException : expected a refusal, got alice-1
[2] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 111 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAuthorKeyRegistryContractTests.R4_32_ReEnrollingAKidWithOtherBytesIsRefusedAndTheOriginalStands
      System.InvalidOperationException : expected a refusal, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = alice-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 7/1/2026 12:00:00 AM +00:00, NotAfter =  }
[2] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 436 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing
      Assert.Equal() Failure: Values differ
      Expected: Conflict
      Actual:   Created
[2] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[3] material compared by reference, as the record's generated equality would
[3] tests/Curia.Application.Tests RED
    Failed!  - Failed:     4, Passed:    13, Skipped:     0, Total:    17, Duration: 25 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_31_TheSameKeyAgainIsHeldNotRegistered
      Assert.True() Failure
      Expected: True
      Actual:   False
  FAILED Curia.Application.Tests.InMemoryAuthorKeyRegistryContractTests.R4_31_ReEnrollingTheSameKeyWritesNothingAndKeepsItsWindow
      System.InvalidOperationException : curia/keys/material-immutable: That key identifier is already registered with different key material
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_32_MaterialIsComparedByContent
      Assert.True() Failure
      Expected: True
      Actual:   False
  FAILED Curia.Application.Tests.KeyEnrollmentTests.R4_31_AHeldKeyIsFoundByItsKidNotItsPosition
      Assert.True() Failure
      Expected: True
      Actual:   False
[3] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 139 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAuthorKeyRegistryContractTests.R4_31_ReEnrollingTheSameKeyWritesNothingAndKeepsItsWindow
      System.InvalidOperationException : curia/keys/material-immutable: That key identifier is already registered with different key material
[3] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 537 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ReEnrollingTheEnrolledKeyIsAcceptedAndChangesNothing
      Assert.Equal() Failure: Values differ
      Expected: Created
      Actual:   Conflict
[3] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[4] the Postgres enrollment takes no lock
[4] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 76 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresEnrollmentSerializationTests.R4_31_TwoEnrollmentsRacingForOneFreshIdentifierLeaveOneKey
      an enrollment decided while its identifier's lock was held by another transaction
  FAILED Curia.Infrastructure.Tests.PostgresEnrollmentSerializationTests.R4_31_AnEnrollmentWaitsWhileItsIdentifiersLockIsHeld
      an enrollment decided while its identifier's lock was held by another transaction
[4] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[4b] the Postgres lock taken after the read, not before it
[4b] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 4 s - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresEnrollmentSerializationTests.R4_31_TwoEnrollmentsRacingForOneFreshIdentifierLeaveOneKey
      Assert.Single() Failure: The collection contained 2 matching items
      Expected:      (predicate expression)
      Collection:    [Curia.Domain.Primitives.Result`1[Curia.Application.Ports.RegisteredKey], Curia.Domain.Primitives.Result`1[Curia.Application.Ports.RegisteredKey]]
      Match indices: 0, 1
[4b] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[5] db/0005 narrows nothing
[5] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     5, Passed:     3, Skipped:     0, Total:     8, Duration: 51 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheIsolatedKeyStoreSchemasCarryTheSameGrant
      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(Npgsql.PostgresException)
  FAILED Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial(column: "alg")
      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(Npgsql.PostgresException)
  FAILED Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial(column: "agent_id")
      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(Npgsql.PostgresException)
  FAILED Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial(column: "kid")
      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(Npgsql.PostgresException)
  FAILED Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheAppRoleCannotRewriteAKeysIdentityOrMaterial(column: "public_key")
      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(Npgsql.PostgresException)
[5] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[6] the per-test key-store schemas are rendered without db/0005
[6] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:     7, Skipped:     0, Total:     8, Duration: 49 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.AgentKeyMaterialGrantTests.R4_32_TheIsolatedKeyStoreSchemasCarryTheSameGrant
      Assert.Throws() Failure: No exception was thrown
      Expected: typeof(Npgsql.PostgresException)
[6] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[7] the history primitive writes material back, and the grant lets it
[7] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     2, Passed:    14, Skipped:     0, Total:    16, Duration: 163 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands
      System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = EdDSA, Kid = kid-algorithm, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 2/1/2026 12:00:00 AM +00:00, NotAfter =  }
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AKidRegisteredAgainWithOtherBytesIsRefusedAndTheOriginalStands
      System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = kid-material, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 2/1/2026 12:00:00 AM +00:00, NotAfter =  }
[7] restore clean (bytes equal to the kept copy: 2/2; git diff --quiet: yes)
[8] the use case asks the store before the log's binding
[8] tests/Curia.Application.Tests RED
    Failed!  - Failed:     3, Passed:     7, Skipped:     0, Total:    10, Duration: 64 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnEnrollmentThatNamesNoKidBindsNone
      Assert.Empty() Failure: Collection was not empty
      Collection: [RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = alice-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 9/26/2026 12:00:00 PM +00:00, NotAfter =  }]
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnIdentityTheLogBoundIsRefusedAnotherKidEvenWhenTheStoreHoldsNothing
      Assert.Empty() Failure: Collection was not empty
      Collection: [RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = mallory-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 9/26/2026 12:00:00 PM +00:00, NotAfter =  }]
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt
      Assert.Equal() Failure: Values differ
      Expected: 0
      Actual:   1
[8] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 519 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment
      Assert.Null() Failure: Value is not null
      Expected: null
      Actual:   "eyJhbGciOiJFUzI1NiIsImtpZCI6IlJPM1lNN1RGa1ZTbVRzZX"···
[8] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[9] the log's record reports success for any kid
[9] tests/Curia.Application.Tests RED
    Failed!  - Failed:     2, Passed:     8, Skipped:     0, Total:    10, Duration: 63 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnEnrollmentThatNamesNoKidBindsNone
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_TheLogsRecordRefusesAKidItDidNotBind
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
[9] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[10] both of the log's halves off
[10] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 525 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment
      Assert.Equal() Failure: Values differ
      Expected: Conflict
      Actual:   Created
[10] restore clean (bytes equal to the kept copy: 2/2; git diff --quiet: yes)
[11] the log's record written before the key store is asked
[11] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:     9, Skipped:     0, Total:    10, Duration: 64 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded
      Assert.Empty() Failure: Collection was not empty
      Collection: [AppendedEvent { Seq = EventSequence { Value = 2 }, AggregateId = AggregateId { Value = https://agents.example/bob }, ServerTimestamp = 2026-09-26T12:00:00.0000000+00:00, Event = DomainEvent { Id = EventId { Value = 01M3ESC9G0FT
[11] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[12] the verifier's negative control substitutes nothing
[12] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 441 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing
      curia-testis did not refuse the victim's question under the overwriter's key (exit=0) -- the check above cannot fail:
      author: https://agents.example/victim-253fcf32
      kid: victim-253fcf32
      alg: ES256
      digest: sha256:5485d1566c94db86704b05de392e2638811c20ecc6de5b38bc954a9db3215977
[12] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[15] the endpoint serves the bare kid as a refusal's detail, as before this stage
[15] tests/Curia.Api.Tests RED
    Failed!  - Failed:     3, Passed:     2, Skipped:     0, Total:     5, Duration: 470 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing
      Assert.StartsWith() Failure: String start does not match
      String:         "attacker-b3d204be"
      Expected start: "agent=https://agents.example/victim-43302270: noth"···
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "agent=https://agents.example/newcomer-71f04f25 kid"···
      Actual:   "victim-e2980924"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing
      Assert.StartsWith() Failure: String start does not match
      String:         "victim-f73d2088"
      Expected start: "kid=victim-f73d2088: nothing was registered."
[15] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[16] the history primitive no longer compares the algorithm
[16] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:    15, Skipped:     0, Total:    16, Duration: 184 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands
      System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = kid-algorithm, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 2/1/2026 12:00:00 AM +00:00, NotAfter =  }
[16] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[17] a lost row's key re-registered from now, not from the enrollment
[17] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:     9, Skipped:     0, Total:    10, Duration: 63 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound
      Assert.Equal() Failure: Values differ
      Expected: 2026-09-26T12:00:00.0000000+00:00
      Actual:   2026-09-27T12:00:00.0000000+00:00
[17] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 543 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment
      Assert.Equal() Failure: Strings differ
      ↓ (pos 205)
      Expected: ···"not_before":"2026-08-16T12:00:00.0000000+00:00"}]}"
      Actual:   ···"not_before":"2026-08-16T13:00:00.0000000+00:00"}]}"
      ↑ (pos 205)
[17] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[18] the history primitive no longer compares the owner
[18] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:    15, Skipped:     0, Total:    16, Duration: 172 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AnotherAgentPresentingTheExactKeyIsRefusedAndMovesNoWindow
      System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = kid-exact-copy, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 2/1/2026 12:00:00 AM +00:00, NotAfter = 3/1/2026
[18] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[19] the use case exempts a kid the store holds from the log's binding
[19] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:     9, Skipped:     0, Total:    10, Duration: 66 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt
      Assert.Equal() Failure: Values differ
      Expected: 0
      Actual:   1
[19] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

Three of this entry's cases read differently in the final wave's full run. Case 15 also turns the
lost-row fact red, `R4_31_ALostRowsKidTakenByAnotherIdentityRefusesTheRecoveryByName`, at its
detail. Case 18 now shows the backdated window as well, since the ownership fact asserts it. And
case 27 is new: the store reporting a `kid` another identity holds as registered. They printed:

```
[15] the endpoint serves the bare kid as a refusal's detail, as before this stage
[15] tests/Curia.Api.Tests RED
    Failed!  - Failed:     4, Passed:     2, Skipped:     0, Total:     6, Duration: 468 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing
      Assert.StartsWith() Failure: String start does not match
      String:         "attacker-79d5bb29"
      Expected start: "agent=https://agents.example/victim-195e6c9c: noth"···
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "agent=https://agents.example/newcomer-98525feb kid"···
      Actual:   "victim-e597bcf7"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing
      Assert.StartsWith() Failure: String start does not match
      String:         "victim-b85c50d1"
      Expected start: "kid=victim-b85c50d1: nothing was registered."
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidTakenByAnotherIdentityRefusesTheRecoveryByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "agent=https://agents.example/victim-947be888 kid=v"···
      Actual:   "victim-947be888"
      ↑ (pos 0)
[15] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[18] the history primitive no longer compares the owner
[18] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:    15, Skipped:     0, Total:    16, Duration: 174 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AnotherAgentPresentingTheExactKeyIsRefusedAndMovesNoWindow
      System.InvalidOperationException : Expected a failure, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = kid-exact-copy, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 1/25/2026 12:00:00 AM +00:00, NotAfter = 3/1/202
[18] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[27] the store answers a kid another identity holds as registered
[27] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 116 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAuthorKeyRegistryContractTests.R4_31_AKidAnotherIdentifierHoldsIsRefusedAndNeitherIdentifierChanges
      System.InvalidOperationException : expected a refusal, got RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = shared-kid, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 8/1/2026 12:00:00 AM +00:00, NotAfter =  }
[27] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     4, Skipped:     0, Total:     6, Duration: 553 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth
      Assert.Equal() Failure: Values differ
      Expected: Conflict
      Actual:   Created
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidTakenByAnotherIdentityRefusesTheRecoveryByName
      Assert.Equal() Failure: Values differ
      Expected: Conflict
      Actual:   Created
[27] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

### D23 — an anonymous search holding U+FFFE answered 500 *(carried from the moderation stage; opened and closed by the enrollment stage, 2026-09-26)*

**Found by the moderation stage's final review, and confirmed by execution** at ccf200e by the
enrollment stage's pre-flight scan. An anonymous `GET /v1/search?q=%EF%BF%BE` answered 500 with
`System.ArgumentException: String contains invalid Unicode code points.`, and so did
`q=jcs%EF%BF%BEhash`. U+FFFF, U+FDD0 and U+1FFFE answered 200. `HashedNGramEmbedding.Words` called
`string.Normalize(FormKC)`, which .NET refuses for U+FFFE and for an unpaired surrogate, and a query
reaches the vector channel (`HybridSearch.cs:123`) without passing ADMIT. No credential was needed.
The moderation stage had fixed the same throw in the reason guard's own copies
(`FlagDisclosure.Normalize`) and carried this one, which predates it.

**Closed** by the enrollment stage's Task 6. The embedding's derived copy reads an ill-formed sequence,
and every noncharacter, as U+FFFD before NFKC. None of them is a letter or a digit, so the features of
every text the normalizer accepted are unchanged, and `hashed-ngram@1` keeps its version.
`HashedNGramEmbeddingTests.R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged` pins that with a
digest taken before the mapping existed. `SearchEndpointTests.ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks`
holds the route to an answer in which the vector channel still ranks, which a route that caught the
throw would not give. The review of this fix found D24.

**Falsified** by the same runner, in the same run. Under case 13 the pin stays green, as it should:
its text holds U+FFFF, which the normalizer accepts.

```
[13] the embedding normalizes the text it was given, as before D23
[13] tests/Curia.Domain.Tests RED
    Failed!  - Failed:     3, Passed:     8, Skipped:     0, Total:    11, Duration: 29 ms - Curia.Domain.Tests.dll (net10.0)
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.AnUnpairedSurrogateIsReadAsTheReplacementCharacter
      System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes
      System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.ANoncharacterAloneHasNoFeaturesAndDoesNotThrow
      System.ArgumentException : String contains invalid Unicode code points. (Parameter 'strInput')
[13] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 354 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.SearchEndpointTests.ANoncharacterInAQueryIsAnsweredAndTheVectorChannelStillRanks
      Assert.Equal() Failure: Values differ
      Expected: OK
      Actual:   InternalServerError
[13] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[14] the embedding drops a noncharacter rather than reading it as U+FFFD
[14] tests/Curia.Domain.Tests RED
    Failed!  - Failed:     2, Passed:     9, Skipped:     0, Total:    11, Duration: 34 ms - Curia.Domain.Tests.dll (net10.0)
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes
      Assert.Equal() Failure: Collections differ
      ↓ (pos 4)
      Expected: [···, 0, 0, -0.333333343, 0, 0, ···]
      Actual:   [···, 0, 0, -0.353553385, 0, 0, ···]
      ↑ (pos 4)
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "042da00bbfaf5edec954d766767fae622f6f49ccefb31287fe"···
      Actual:   "d53929f11057e7a303954b41b04dac6ce9b8d53848721c59f4"···
      ↑ (pos 0)
[14] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

### D24 — features that cancel embedded as NaN, so one T0 post could stop the Forum restarting *(opened and closed by the enrollment stage, 2026-09-26)*

**Found by the review of D23's fix, and confirmed by execution.** `HashedNGramEmbedding.Embed`
refused only a text with no features (`counts.Count == 0`). Each hashed feature is signed, and two
of equal count that land in one bucket with opposite signs sum to 0.0. When every bucket sums to
0.0 the norm is 0, and dividing by it made every component NaN. `Embed`'s own summary already
stated the rule, "a zero vector has no direction", but the code held it only when there were no
features. The review found readable two-word queries of one form whose features cancel, `dk jà`
among them: two ASCII letters, then an ASCII letter and a Latin-1 one. For one- and two-character
ASCII words FNV-1a's bit 32, which picks the sign, is constant, so two such words never cancel each
other; `jà` is why `dk jà` does. Longer ASCII words' features take both signs, and pure-ASCII text
cancels too: `P33` alone, and `L R17R`, return `no-features` from the built `Curia.Domain` assembly
today, where before D24 each would have embedded as NaN. The defect is as old as `hashed-ngram@1`,
and no test found it.

**What it did.** pgvector refuses NaN (`22000: NaN not allowed in vector`), so:
- an anonymous search answered 503;
- a T0 question answered 500 after PERSIST: it is in the log and absent from the vector index;
- a second such question on the same board answered 503, through §8.5's dedupe;
- on restart, `EmbeddingReconcileService.StartAsync` replays from the vector index's high-water
  mark, and it threw once such a post lay past it.

The review ran the first three and traced the restart. The fix round made the search, the question
and the restart facts, each red without the guard, and the stage's final wave made the dedupe's
(below). One restart was run, as a fact. That every later start threw too, keeping the Forum down
until the code changed, is traced rather than run: each start replays the same post, and the
append-only log cannot drop it.

**Closed** by the enrollment stage's Task 6 fix round: a zero norm returns
`curia/embedding/no-features`, which all three callers already skip (`HybridSearch`,
`DuplicateCheck` and `EmbeddingIndexer`). `hashed-ngram@1` keeps its version: pgvector could never
store a NaN vector, so no stored vector moves. The facts are
`HashedNGramEmbeddingTests.FeaturesThatCancelHaveNoEmbedding` and three in `SearchEndpointTests`:
`AQueryWhoseFeaturesCancelIsAnswered`, `AQuestionWhoseFeaturesCancelIsCreatedAndServed` and
`AHostRestartedOverAPostWhoseFeaturesCancelStarts`. The final wave added a fourth,
`AQuestionWhoseFeaturesCancelIsNotRefusedAsADuplicateOfItself`, which fences the dedupe path and
checks that its text cancels. Its control, `dk` and `ja` with no tags, has features: asked twice on
one board, the second ask is refused `curia/posts/duplicate-question`, so the dedupe engages for
text of this shape. `dk` and `jà`, asked twice on the same board, is created both times, which only
a text with no embedding can be. Without the guard its first ask answered 503 (case 20), because the
control's question is a candidate on the board and the dedupe asks the vector index for the NaN
vector's neighbours. `EmbeddingErrors.NoFeatures`' title, "contains no letters or digits", is now
also returned for features that cancel. Every caller matches its type alone, so the title is never
served.

**Falsified** by the same runner, in the same run. Both digest pins stay green under case 20, as
they should: neither text's features cancel.

```
[20] the embedding divides a zero vector by its zero norm, as before D24
[20] tests/Curia.Domain.Tests RED
    Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: 31 ms - Curia.Domain.Tests.dll (net10.0)
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.FeaturesThatCancelHaveNoEmbedding
      Assert.False() Failure
      Expected: False
      Actual:   True
[20] tests/Curia.Api.Tests RED
    Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3, Duration: 467 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.SearchEndpointTests.AQuestionWhoseFeaturesCancelIsCreatedAndServed
      Assert.Equal() Failure: Values differ
      Expected: Created
      Actual:   InternalServerError
  FAILED Curia.Api.Tests.SearchEndpointTests.AQueryWhoseFeaturesCancelIsAnswered
      Assert.Equal() Failure: Values differ
      Expected: OK
      Actual:   ServiceUnavailable
  FAILED Curia.Api.Tests.SearchEndpointTests.AHostRestartedOverAPostWhoseFeaturesCancelStarts
      System.InvalidOperationException : The vector index could not be reconciled with the log (curia/retrieval/index-unavailable: The vector index could not be queried; 22000: NaN not allowed in vector). The Forum does not start with a retrieval
[20] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

In the final wave's full run, on e54ba15, case 20 also turned the dedupe fact red, at 503:

```
[20] the embedding divides a zero vector by its zero norm, as before D24
[20] tests/Curia.Domain.Tests RED
    Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11, Duration: 31 ms - Curia.Domain.Tests.dll (net10.0)
  FAILED Curia.Domain.Tests.Search.HashedNGramEmbeddingTests.FeaturesThatCancelHaveNoEmbedding
      Assert.False() Failure
      Expected: False
      Actual:   True
[20] tests/Curia.Api.Tests RED
    Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 526 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.SearchEndpointTests.AQuestionWhoseFeaturesCancelIsCreatedAndServed
      Assert.Equal() Failure: Values differ
      Expected: Created
      Actual:   InternalServerError
  FAILED Curia.Api.Tests.SearchEndpointTests.AQueryWhoseFeaturesCancelIsAnswered
      Assert.Equal() Failure: Values differ
      Expected: OK
      Actual:   ServiceUnavailable
  FAILED Curia.Api.Tests.SearchEndpointTests.AHostRestartedOverAPostWhoseFeaturesCancelStarts
      System.InvalidOperationException : The vector index could not be reconciled with the log (curia/retrieval/index-unavailable: The vector index could not be queried; 22000: NaN not allowed in vector). The Forum does not start with a retrieval
  FAILED Curia.Api.Tests.SearchEndpointTests.AQuestionWhoseFeaturesCancelIsNotRefusedAsADuplicateOfItself
      Assert.Equal() Failure: Values differ
      Expected: Created
      Actual:   ServiceUnavailable
[20] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

### D25 — the vector index serves Postgres's own error text to an anonymous caller *(opened by the enrollment stage, 2026-09-26)*

**Found by the review of D23's fix**, beside D24. `PostgresVectorIndex.Translate` folds the SQLSTATE
and Postgres's message text into the `curia/retrieval/index-unavailable` problem's detail
(`RetrievalErrors.IndexUnavailable($"{e.SqlState}: {e.MessageText}")`). Under D24 an anonymous
`GET /v1/search` answered 503 with `"detail":"22000: NaN not allowed in vector"`. No data leaked in
that case, but backend text on an anonymous route is a disclosure surface.
`PostgresFlagDetailStore` folds the SQLSTATE alone.

**Not fixed here.** The fix wants a sweep of every adapter that folds backend exception text into a
served problem detail, logging the text server-side instead. That is a stage's scope, not a line's.
"What comes next" carries it.

*Closed by the strangers stage (errata G17, R11.33), at the boundary rather than the adapter: every 5xx
problem document the Forum composes goes through `ServerFault` (`src/Curia.Api/ServerFault.cs:16`),
which serves the fault's type and title and logs its detail, event 5000; `ForumEndpoints.Problem`
returns one for every 5xx (`src/Curia.Api/ForumEndpoints.cs:2171`) and the Acta's fold for both of its
faults (`src/Curia.Api/ActaEndpoints.cs:233`, `:239`). Two 5xx do not. One is the token endpoint's
`server_error`, which is RFC 6749's shape rather than a problem document; since the rotation stage's
PR 0 (M5) it serves its code and a fixed description and logs its slug and detail as event 5000
(`src/Curia.Api/Issuer/OAuthRefusal.cs`). The other is an exception nothing handles, which a
production host answers with its own empty 500 (the Development host's exception page is a
developer's tool). The
vector index still folds Postgres's text into its error
(`src/Curia.Infrastructure/PostgresVectorIndex.cs:185`); the log is where it now goes. The sweep,
derived from the route registrations, found two routes answering 500 that the register did not know
of, both closed: a thread id of white space alone (`ForumEndpoints.cs:1197`), and a token request that
is not a form, whose form holds U+0000, or whose multipart form is cut off before its closing boundary
(`src/Curia.Api/Issuer/TokenEndpoint.cs:72`, `:80`, `:84`; the last found by the plan's pre-flight). It
found none on `q`, `board` or `author`: every read folds the log in memory. Run as an enrolled agent,
whose requests reach the handlers behind authentication, it found the same two and no other among
well-formed headers. A third was a header, recorded under "Observed during the enrollment stage" and
closed by the same stage: a DPoP proof whose key is no point on P-256, under a token bound to it.
Task 8's review found two more, and the stage closed both. The fourth is a signed claim out of range:
`NumericDate` handed `iat`, `exp` and `nbf` to `FromUnixTimeSeconds` unchecked after the signature
verified, so an assertion with `exp` 1e13 answered 500 from `/oauth/token`, and a proof with `iat`
1e13 or -1e11 answered 500 from every route behind authentication, to any enrolled agent; a value
outside `DateTimeOffset`'s range is `curia/authn/malformed` now (`src/Curia.AuthN/Jwt/NumericDate.cs:16`,
`:19`), held by `NumericDateTests`, the assertion and proof theories, and
`RequestSurfaceTests.R11_33_NoNumericDateAnEnrolledAgentSignsIsAnsweredAsAServerFault` (falsification
cases 61 and 62). The fifth is a JSON body's declared charset, the quoted form included: the
minimal-API binder threw for a charset it cannot read -- `bogus-xyz`, an empty one, and `"utf-8"`,
since it does not unquote -- answering 500 to anyone from `POST /v1/agents`, `/v1/posts/batch` and
`/v1/posts/{id}/flags`; `JsonCharset` refuses anything but the bare token utf-8 with 415 before
binding (`src/Curia.Api/JsonCharset.cs:24`, registered at `src/Curia.Api/Program.cs:295`), held by
`R11_33_AJsonBodyInACharsetOtherThanUtf8IsRefusedBeforeItIsBound` and five sweep bodies, a +json
media type included (cases 63–66). The sixth, found by Task 11's review, is anonymous: a token
request whose DPoP proof's header is JSON but not an object (`[1]`, a number, a string, `null`)
answered 500 before any credential was read, because the sweep sent its hostile proofs to
`/oauth/token` with no form and never reached the parse; the proof is now `invalid_dpop_proof`
(`src/Curia.AuthN/Jwt/CompactJws.cs:126`, which the token endpoint reads its proof through since
Task 11's fix review, `src/Curia.Api/Issuer/TokenEndpoint.cs:190`), held by
`RequestSurfaceTests.R11_33_ATokenRequestsDpopProofWhoseHeaderIsNotAnObjectIsRefusedNotThrown` and
the sweep's form-carrying pass (falsification case 90). The seventh, found by Task 11's fix review,
is anonymous too: `JsonDocument.Parse` accepts an escaped unpaired surrogate and
`JsonElement.GetString()` throws on it, so an access token whose header `alg` or `kid` held one
answered 500 on every route behind authentication before any key was resolved, and a token
request's proof whose `jwk` held one answered 500 at `/oauth/token`, which read the proof through a
parse of its own. `CompactJws` refuses such a segment as malformed
(`src/Curia.AuthN/Jwt/CompactJws.cs:122`), and the token endpoint now reads its proof through
`CompactJws` (`src/Curia.Api/Issuer/TokenEndpoint.cs:190`). It is held by `CompactJwsStringTests`,
`R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown` and the header
sweep's surrogate rows (falsification cases 91 and 92). The sweep had sent `\u0000`, which
decodes, and no string that does not (trap 26).
The eighth, ninth and tenth were found by the stage's final gate, and every one was a string the
enrolled sweep signed but never varied. A DPoP proof's or a client assertion's `jti` that was
absent, not a string, empty, white space, held U+0000, or ran past `authn_replay_pkey`'s 2,704-byte
btree row answered 500 from every route behind authentication and from `/oauth/token`, to any
enrolled agent. `PostgresReplayCache` threw `ArgumentException`, or Postgres threw 22021 or 54000. A
client assertion whose header `kid` was absent, not a string, empty or white space answered 500 from
`/oauth/token` to anyone, before any signature was checked (`PostgresAgentKeyStore.ResolveAsync`). A
write's proof `nonce` holding U+0000 answered 500 from every write route
(`PostgresDpopNonceStore.IsCurrentAsync`, 22021). One reader, `CompactJws.IdentifierRefusal`
(`src/Curia.AuthN/Jwt/CompactJws.cs:159`), now refuses each of them before any store is asked. It is
held by the jti, kid and nonce facts in `AccessTokenValidatorDpopTests` and
`ClientAssertionValidatorTests`, and through the host by
`RequestSurfaceTests.R11_33_NoStringAnEnrolledAgentSignsIsAnsweredAsAServerFault` and, for the
`kid`, `R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown` (falsification cases
97-101). The eleventh is anonymous: a token request whose form, or any of whose multipart parts,
declared charset UTF-7 or an alias answered 500, because the form reader's charset lookup throws
`NotSupportedException` and the endpoint caught only `InvalidDataException` and `IOException`
(`src/Curia.Api/Issuer/TokenEndpoint.cs:88`). It is held by
`R11_33_ATokenRequestInACharsetTheFormReaderCannotDecodeIsInvalidRequest` and four sweep bodies
(falsification case 102).
The twelfth, found by the stage's final gate, third round, is the post signature's own header:
`DetachedJws` parsed it with `JsonDocument.Parse` and read `alg`, `kid`, `typ` and `crit` through
`GetString()`, which throws on an escaped unpaired surrogate or on invalid UTF-8 in a string, so any
enrolled agent's post whose signature header held one answered 500 from `POST /v1/posts`, and a
Forum serving one would have thrown in the reference readers' `SignatureCheck` and `ActaCheck`.
`DetachedJws` now refuses such a header as malformed (`src/Curia.Canon/Jws/DetachedJws.cs:254`), the
twin of `CompactJws`'s guard. Held by
`DetachedJwsTests.R11_33_AProtectedHeaderHoldingAStringThatDoesNotDecodeIsMalformedNotThrown`,
`RequestSurfaceTests.R11_33_ASignedPostWhoseSignatureHeaderItCannotReadIsRefusedNotThrown` and the
Client facts `ReaderContractTests.R11_33_AServedSignatureWhoseHeaderDoesNotDecodeIsAFailedVerdictNotAnException`
and `ActaCheckTests.R11_33_ALoggedSignatureWhoseHeaderDoesNotDecodeIsAFailedCheckNotAnException`
(falsification case 104). Task 11 fixed the compact parser and not the detached one; the same input
class reached both (trap 26).
The thirteenth, found beside it, is that header's `kid`: absent, not a string, empty or white space,
it reached `PostgresAgentKeyStore.ResolveAsync`'s guard and answered 500. `IngestPipeline.VerifyAsync`
now answers it as a kid no key is registered under, `curia/keys/not-registered-to-agent` 401, before
the resolver is asked (`src/Curia.Application/Ingest/IngestPipeline.cs:121`), as the client
assertion's blank `kid` is refused before its resolver since the second round. Held by the Api
theory's kid rows and
`IngestPipelineTests.VerifyAsync_AHeaderKidThatIsBlankIsRefusedAsAnUnregisteredKidBeforeTheResolverIsAsked`
(falsification case 105).
The fourteenth: a signed post's `board`, or a comment's or answer's `parent`, holding U+0000 (ADMIT
accepts the escape `\u0000`) answered 500 from `POST /v1/posts` to any T0 agent, because
`PersistAsync` writes both into the `post.accepted` payload outside the canonical text, and jsonb
refuses U+0000 (22P05). `VerifyAsync` now refuses such a member as `curia/ingest/unstorable-member`
422, naming the member and not its value (`IngestPipeline.cs:100`, `:102`). This is an implementation
limit of the log and not a ruling on R8.63's value space: the body and title, which reach jsonb only
inside the canonical text, still accept U+0000, and whether `board` and `parent` admit control
characters at all remains for the next errata pass (see "Observed during the strangers stage"). Held
by `R11_33_ASignedPostWhoseBoardOrParentTheLogCannotStoreIsRefusedNotThrown` (falsification case 106),
beside `R11_33_ABodyOrTitleHoldingU0000IsStoredInsideTheCanonicalText`, which keeps the refusal
narrow. A flag rationale holding U+0000, raised against a real post, was pre-flighted beside it: traced
to the private store's `text` column and an expected 503, it answered 400
`curia/flag/detail-unstorable`, since `FlagDetailRules.Admit`
(`src/Curia.Application/Ports/IFlagDetailStore.cs:50`) refuses U+0000 in every member before the
store is asked. No rule was added, and no case; `FlagEndpointTests.R11_33_AFlagRationaleTheStoreCannotHoldIsRefusedNotThrown`
keeps it so.
The fifteenth: `POST /v1/posts/{postId}/flags` with a post id of white space alone and a body it
would accept answered 500 to any credentialed agent, because `RaiseFlag.RecordAsync` threw on the id
and the sweep had always sent such ids with a `kind` of "\n", which is refused first (trap 26). It is
`curia/flag/no-such-post` 404 now, as `accept` and `GET …/flags` answer it
(`src/Curia.Application/Moderation/RaiseFlag.cs:89`), and the enrolled sweep sends every hostile path
id with a body its route would accept. Held by
`FlagEndpointTests.R11_33_AFlagAgainstAPostIdOfWhiteSpaceAloneIsNoSuchPost` and the sweep
(falsification case 107). A post id of U+0000 was not run: the test host's client refuses a path
holding it before sending it (see "Observed during the strangers stage").
A 4xx no handler composed is a problem
document now, and at `/oauth/token` RFC 6749's error object; until the stage's final gate every path
under `/oauth` had been left out, so routing's 404 and 405 there were served with no body at all
(cases 95, 96)
(`src/Curia.Api/UnreadableRequests.cs:19`, registered at `src/Curia.Api/Program.cs:294`, with the
binder told not to throw at `:291`), held by
`R11_33_ARequestNoHandlerCanReadIsAnsweredWithAProblemDocument` and the three sweeps (cases 67,
68). A
host running as production serves no framework or backend text on any anonymous request. Held by
`ServerFaultTests`, `RequestSurfaceTests` and
`AccessTokenValidatorDpopTests.R11_33_AProofKeyThatIsNoPointOnTheCurveIsRefusedNotThrown`.*

**What this closure covers, and what it does not** (`curia-architect`'s ruling on the stage's final
gate, 2026-10-06). D25 is closed for what it named, backend text in a served 5xx detail, which
`ServerFault` now withholds at the one boundary that serves a fault, and for the fifteen 500s above,
each closed and each held by a named test and a falsification case. It is **not** a finding that no
request causes a 500, and nothing in this stage may say so. R11.33's first sentence is implemented
instance by instance and discharged by an enumerated sweep. Each of the final gate's three rounds
found instances the previous one had not, and the third round's fix commit, f914059, was not swept
again. The class, a string a caller chose reaching a parser or a store that throws on it, is open as
**D33**. *Amended 2026-10-10 (the one-part-at-a-time stage's Task B7):* the class is closed by R11.34
(D33, closed by that stage's PR B) and gated by R14.10.

### D26 — any enrolled key could obtain a token as any enrolled identity *(pre-existing; found by the enrollment stage's final review, 2026-09-26; opened and closed by that stage's final wave; errata G15, R5.20)*

**Found by probe**, by the enrollment stage's final review, through the real Forum over Postgres at
3c8f17d. A victim enrolled and asked one question. An attacker enrolled its own key under an
identifier of its own, which R4.31 permits, and then asserted the victim's `iss`, `sub` and
`client_id` with that key. Six of the probe's lines, abridged as errata G15 quotes them:

```
control (attacker kid registered nowhere): refused -- Token request failed (401):
  {"error":"invalid_client","error_description":"No key with that identifier is registered to that agent", …}
attacker enrolls its own fresh identity with that key: 201
attack token claims: {… "sub":"https://agents.example/victim-77851479", …,
  "owner":"https://agents.example/victim-77851479","tier":"T0"}
flag raised with the token: 201 {"post_id":"01M0572TG0X22FT8T07WK7819H","kind":"spam", …}
flag_details.raised_by = https://agents.example/victim-77851479
question as victim, attacker's key: 401 {"type":"curia/keys/not-registered-to-agent", …}
```

A second probe needed no enrollment of the attacker at all. An enrollment under an existing post's
identifier registered its key and then could not record the enrollment (D27's first part), and the
key row it left obtained a victim's token. Its two lines, abridged:

```
enrollment under a post id: 500 {"type":"curia/enroll/contended", …; agent_keys rows=1; agent.enrolled naming the kid before/after=0/0
token for the victim: {… "sub":"https://agents.example/victim-9a453076", …, "owner":"https://agents.example/victim-9a453076","tier":"T0"}
```

**Mechanism.** Four texts, each trusting another to hold the rule:
- `TokenEndpoint` handed the validator the key store as its resolver, and the store answered the
  authentication path by `kid` alone (`WHERE kid = @kid`).
- `ClientAssertionValidator` required `sub` to equal `iss` and `client_id`, and compared none of
  them with the agent the verifying key was registered to.
- `ClientAssertionValidationContext`'s remarks said the caller scoped the resolver to one agent's
  keys. The caller passed it unscoped.
- The store's remark said a lookup by `kid` alone was correct. The premise was first written in
  `docs/phase-2-record.md:517-522`: `IAgentKeyResolver` asks by `kid` alone, "correctly, since a
  client assertion names its key and the subject is established by *which key verified*". db/0002's
  header repeated it until the final wave rewrote it.

A signature shows that its signer holds some registered key. Only the store knows whose, and a
lookup by `kid` alone discarded the answer.

**What it did.**
- A DPoP-bound token as the victim, at the victim's tier (probed).
- A flag raised with that token was recorded, privately, as the victim's (probed).
- The same token could accept answers on the victim's threads, which Table 11 counts toward an
  answerer's T2. That is **traced, not run**: the accept route compares the token's `sub` with the
  root's author (`ForumEndpoints.cs:1260`, `:1307`), and no probe tried it.
- A post in the victim's name stayed closed (probed: 401 `curia/keys/not-registered-to-agent`),
  because ingest resolves a key by author and `kid` together (R6.2, `IngestPipeline.cs:103`).

So G14's first finding, impersonation, outlived D22 for every act a token authorizes except
authorship.

**Why no gate saw it.** Every test that asserted "the attacker obtains no token" used a `kid`
registered nowhere, so the lookup failed before the missing comparison could matter. And the rule
was never a requirement. §5 drew it as Figure 5's third step, "resolve agent JWKS by `iss`", while
R6.2 wrote the ingest analogue as a SHALL, and ingest was built to it. It is trap 21 again, one
layer over.

**Closed** by errata G15's R5.20, in the final wave:
- `IAgentKeyResolver.ResolveAsync` takes the agent as well as the `kid` and the instant, as
  `IAuthorKeyResolver` already did, so no caller can ask for an agent's key without saying whose.
  `ClientAssertionValidator` resolves for `ExpectedSubject`, the request's `client_id`
  (`ClientAssertionValidator.cs:77`), and still requires `sub` to equal it (`:102`). The store's
  lookup by `kid` alone is deleted with its remark.
- No agent's key is resolved by `kid` alone anywhere. `RegisterAsync` still reads a `kid`'s owner by
  `kid` (`PostgresAgentKeyStore.cs:261`), to choose which refusal to give; that read resolves no
  key. The issuer's own keys are still resolved by `kid` (`IJwsKeyResolver`): they are the Forum's,
  not an agent's.
- A key registered to another agent is refused exactly as one registered nowhere, byte for byte: 401
  `{"error":"invalid_client","error_description":"No key with that identifier is registered to that
  agent","detail":"curia/keys/not-registered-to-agent"}`, so the refusal does not say whose a `kid`
  is (R5.12). An assertion whose `sub` is not the client is refused 401
  `curia/authn/subject-mismatch`.
- A U+0000 in `client_id` meets the same 401: the store answers it before its query runs
  (`PostgresAgentKeyStore.cs:336`), since no stored row can hold one. One in the assertion header's
  `kid` met it too until the strangers stage's final gate, second round, which refuses a `kid` no
  store can be asked about as `curia/authn/malformed` before any key is resolved (R11.33, D25). The
  review of the fix probed both as Postgres 500s: the fix itself had opened the `client_id` one,
  once the resolver took the agent, and the header `kid` one predated it.
- The facts are `TokenSubjectBindingTests`' four:
  - `R5_20_AKeyEnrolledUnderItsHoldersOwnIdentifierMintsNoTokenForAnother`, which asserts the damage
    first and ends on a positive control;
  - `R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity`, over a row the provisioning role
    writes;
  - `R5_20_AnAssertionNamingAnotherSubjectThanItsClientIsRefused`;
  - `R5_20_AnAssertionNamingANulIdentifierOrKidIsRefusedNotThrown` (named
    `…IsRefusedAsAnUnregisteredKeyIs` until the strangers stage's final gate, second round).

  With them come
  `ClientAssertionValidatorTests.R5_20_AKeyRegisteredToAnotherAgentDoesNotAuthenticateTheAssertedSubject`
  and `PostgresAgentKeyStoreTests.R5_20_TheAssertionPortResolvesAKeyOnlyForTheAgentItIsRegisteredTo`.

**What it leaves.**
- **Rows written before the fix stay** (R4.19). A key row no enrollment recorded authenticates
  nothing: a token needs a key registered to its subject and that subject's `agent.enrolled`, and
  such a row's identifier has none. The second fact above shows it, since asserting the row's own
  identifier is refused "That agent is not enrolled" (`TokenEndpoint.cs:148`).
- **Unfiled, and not ruled:** the token endpoint puts the failing check's slug in `detail`
  (`TokenEndpoint.cs:134`). That predates this stage; see "Observed during the enrollment stage".

**Falsified** by the final wave's full run of the stage's runner, on e54ba15 (D22 describes the
run). Case 21 lets both adapters of the authentication port answer by `kid` alone; case 22 stops the
validator comparing `sub` with the client. Under case 21 the subject fact and the U+0000 fact stay
green, as they should: the subject check refuses the first, and the store refuses a U+0000 before
its query runs. Npgsql accepted the now-unreferenced `@agent` parameter. Under case 22 the other
three `TokenSubjectBindingTests` facts stay green, because the resolver refuses first, and
`SubjectNotMatchingTheResolverScopeIsRejected`, which no earlier record in this repository shows
falsified, goes red.

```
[21] both adapters of the authentication port answer by kid alone
[21] tests/Curia.AuthN.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 40 ms - Curia.AuthN.Tests.dll (net10.0)
  FAILED Curia.AuthN.Tests.ClientAssertionValidatorTests.R5_20_AKeyRegisteredToAnotherAgentDoesNotAuthenticateTheAssertedSubject
      mallory's key authenticated sub=agent://curia.example/tuesdaycrowd/scriptor
[21] tests/Curia.Infrastructure.Tests RED
    Failed!  - Failed:     2, Passed:    14, Skipped:     0, Total:    16, Duration: 182 ms - Curia.Infrastructure.Tests.dll (net10.0)
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.AKidRegisteredToAnotherAgentDoesNotResolveForThisOne
      System.InvalidOperationException : Expected a failure, got PublicKeyMaterial { Alg = ES256, Kid = kid-alices, Public = System.ReadOnlyMemory<Byte>[91] }
  FAILED Curia.Infrastructure.Tests.PostgresAgentKeyStoreTests.R5_20_TheAssertionPortResolvesAKeyOnlyForTheAgentItIsRegisteredTo
      System.InvalidOperationException : Expected a failure, got PublicKeyMaterial { Alg = ES256, Kid = kid-alices-alone, Public = System.ReadOnlyMemory<Byte>[91] }
[21] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 495 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.TokenSubjectBindingTests.R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity
      1 flag(s) recorded as raised by https://agents.example/victim-36d5c25c, with a token another agent's key obtained (the token request answered 200; the flag request answered 201)
  FAILED Curia.Api.Tests.TokenSubjectBindingTests.R5_20_AKeyEnrolledUnderItsHoldersOwnIdentifierMintsNoTokenForAnother
      1 flag(s) recorded as raised by https://agents.example/victim-c1ea6394, with a token another agent's key obtained (the token request answered 200; the flag request answered 201)
[21] restore clean (bytes equal to the kept copy: 2/2; git diff --quiet: yes)
[22] the validator stops comparing sub with the client
[22] tests/Curia.AuthN.Tests RED
    Failed!  - Failed:     1, Passed:    13, Skipped:     0, Total:    14, Duration: 51 ms - Curia.AuthN.Tests.dll (net10.0)
  FAILED Curia.AuthN.Tests.ClientAssertionValidatorTests.SubjectNotMatchingTheResolverScopeIsRejected
      Assert.False() Failure
      Expected: False
      Actual:   True
[22] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 439 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.TokenSubjectBindingTests.R5_20_AnAssertionNamingAnotherSubjectThanItsClientIsRefused
      a token was issued for sub=https://agents.example/victim-9ad303d1 to a client that named https://agents.example/attacker-own-61eab989: {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IkZ6OF9WRk5SNy05RW04c3psQjNUZlVMS1U0ZnUtSnRNOEtad25lLURzQWMi
[22] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

### D27 — an enrollment wrote whatever identifier, algorithm and key it was sent *(pre-existing; found by the enrollment stage's final review and by the review of its final wave's second dispatch, 2026-09-26; closed by that stage's final wave; errata G15, R4.33; R4.5's form stays open as D4)*

`POST /v1/agents` needs no credential. Before the final wave it asked only that `agent_id` and `kid`
were not blank and that `public_key` decoded as base64, and it registered the rest as it was sent.
The final review probed the identifier and the algorithm at 3c8f17d. The review of the final wave's
second dispatch probed the key and the length at a83577d. Four parts:

**(a) The namespace.** An agent's identifier is also the aggregate its credential events are
appended under, and the Forum's own writers mint aggregates too: a post's ULID, `flag:` and a ULID
for each flag, and the Acta's `log:keys` and `log:heads`, which `curia-operator` appends to at the
version its fold of the log expects.
- On a fresh Forum whose log held no event under `log:keys`, `agent_id` `log:keys` answered 201, and
  `sign-head` then exited 2. The control, a fresh Forum holding one ordinary enrollment, exited 0.

  ```
  enroll agent_id=log:keys: 201 {"agent_id":"log:keys","kid":"p7-keys-347cd3cb", …}
  sign-head exit=2
    stderr: error: Append targeted an aggregate at an unexpected version (curia/domain/concurrency-conflict): aggregate=log:keys expected=0 actual=1
  ```

  The event cannot be removed from an append-only log, so one anonymous request stopped that Forum
  from ever signing a head. `log:heads` does the same before the first head (traced).
- `agent_id` set to an existing post's id answered 500 and left one key row and no `agent.enrolled`:
  the store registered, and the log's append at `AggregateVersion.New` could not succeed. A re-send
  gets the same 500 (traced). That row is D26's second probe.

  ```
  agent_id = post id 01M0572TG0C39DCPCK281MKV3V: 500 {"type":"curia/enroll/contended", …,"detail":"agent=01M0572TG0C39DCPCK281MKV3V attempts=2"}
  agent_keys rows for kid: 1; agent.enrolled in post stream: 0
  ```
- **Closed** by errata G15's R4.33: two clauses in `EnrollIdentity`, both before the key store is
  asked. An identifier beginning `log:` or `flag:` is refused before the log is read
  (`ReservedIdentifiers` reserves the prefix, so a later Acta aggregate is reserved without anyone
  remembering). An identifier whose aggregate holds events, none of them its own enrollment, is
  refused after it. Both answer 409 `curia/enroll/identifier-reserved`. The facts are
  `EnrollIdentityTests.R4_33_AnIdentifierWhoseStreamHoldsAnotherWritersEventsIsRefusedBeforeTheStoreIsAsked`,
  `EnrollIdentityTests.R4_33_EveryAggregateNameTheForumMintsIsReserved`, which reflects over the
  writers' own constants and asserts that it found at least three,
  `EnrollmentIdentifierTests.R4_33_AnEnrollmentNamingAPostIsRefusedAndRegistersNothing`, and
  `ActaNamespaceTests.R4_33_AnEnrollmentNamingTheActasStreamsIsRefusedAndAHeadCanStillBeSigned`. The
  last runs on a database of its own, asserts first that neither stream holds an event, and asserts
  the signed head before the refusals.

**(b) The characters.**
- `agent_id` or `kid` holding U+FFFE answered 201. The log served the entry of an `agent_id` holding
  one, and the reference client refused to read it; the control entry read. A `kid`'s entry was not
  read back. A lone surrogate was refused 400 by ASP.NET's JSON binder, before the route ran. Five
  of the final review's lines, abridged:

  ```
  first enroll: 201 {"agent_id":"https://agents.example/probe-a7c99da1\uFFFE","kid":"probe1-a7c99da1", …}
  kid with U+FFFE: 201 {"agent_id":"https://agents.example/probe2-fda4160e","kid":"probe2-fda4160e\uFFFE", …}
  ForumClient entry 0 (noncharacter agent): ok=False The Forum's response could not be parsed: curia/admit/noncharacter
  ForumClient entry 1 (control): ok=True
  lone surrogate in agent_id: 400 Microsoft.AspNetCore.Http.BadHttpRequestException: Failed to read parameter "EnrollRequest request" from the request body as JSON.
  ```
- U+0000, sent as a JSON escape, which ADMIT's string rules accept and Postgres `text` refuses, gave
  a 500 with SQLSTATE 22021 in `agent_id` or `kid`, and so did `/v1/jwks?agent=` naming such an
  identifier. The dispatch that fixed them measured each before its fix, and 0 key rows after the
  `agent_id` one.
- **Closed** by `Curia.Canon.Json.JsonReader.CheckString`, ADMIT's R6.15 rules for a string that did
  not arrive through `Parse`, which the route asks of `agent_id` and then `kid`. It refuses 400 with
  ADMIT's own slugs, `curia/admit/noncharacter` or `curia/admit/unpaired-surrogate`, and the detail
  `field=agent_id` or `field=kid`; the value is never echoed. U+0000 is then refused 400
  `curia/enroll/nul-character`. `/v1/jwks?agent=` naming an identifier that holds U+0000 answers 404
  `curia/keys/unknown-agent`, as for any identifier the store holds nothing for
  (`PostgresAgentKeyStore.cs:288`). The facts are
  `JsonReaderCheckStringTests.CheckStringRefusesANoncharacterAndAnUnpairedSurrogateByName`,
  `JsonReaderCheckStringTests.CheckStringAgreesWithAdmitOnEveryString`, a CsCheck property that
  derives its expectation from `Parse`,
  `EnrollmentIdentifierTests.R6_15_AnEnrollmentHoldingANoncharacterIsRefusedBeforeAnythingIsWritten`,
  `EnrollmentIdentifierTests.AnEnrollmentHoldingUPlus0000IsRefusedByNameBeforeAnythingIsWritten`, and
  `EnrollmentIdentifierTests.AKeySetAskedForAnIdentifierHoldingUPlus0000IsAnsweredAsAnUnknownAgentIs`.
  The last has no case in the runner; the dispatch that wrote it showed it red before the fix
  (`Actual: "500 Npgsql.PostgresException (0x80004005): 22021: "`).

**(c) The algorithm and the key.**
- **The algorithm.** A missing `alg` answered 500 (Npgsql's null parameter), and `RS256` answered
  500 (the `agent_keys` CHECK). Both failed at the key store, before the log was asked to record the
  enrollment, and the dispatch that fixed them counted 0 key rows after `RS256`. Two of the final
  review's lines, abridged:

  ```
  no alg: 500 System.InvalidOperationException: Parameter 'alg' cannot be null, DBNull.Value should be used instead.
  alg RS256: 500 Npgsql.PostgresException (0x80004005): 23514: new row for relation "agent_keys" violates check constraint "agent_keys_alg_check"
  ```

  The second dispatch refused both 400 `curia/enroll/unsupported-algorithm`, against the verifier
  dictionary `DetachedJws` uses as its allow-list. But that theory tested the label, not the key:
  every row sent an honest P-256 key. The theory carried R4.15's number, and the route's remark said
  the Forum "never registers a key it could not check a signature against"
  (`ForumEndpoints.cs:408-409` at a83577d).
- **The key**, probed at a83577d by that dispatch's review.
  - `""`, `AAAA`, 32 raw bytes, an RSA-2048 SubjectPublicKeyInfo and 5 MB of zeros, each as ES256,
    answered 201. The key set of each one probed (the 32 raw bytes, the RSA key, the empty key) then
    answered 500, and so did a token request naming the 32 raw bytes: `Jwks.cs:73` and
    `Es256Adapter.cs:27` at a83577d imported whatever was stored. Neither row could be repaired,
    since R4.19 forbids deleting it and R4.32 changing its material.
  - A P-256 SubjectPublicKeyInfo as EdDSA answered 201, and the key set served its 91 bytes as an
    octet key pair's `x`, which R4.28 forbids.
  - A P-384 SubjectPublicKeyInfo as ES256 answered 201, obtained a token, and had a question signed
    with P-384 and SHA-256 accepted. The key set served it as `crv: "P-256"` with 48-byte
    coordinates. Measured here, `curia-testis` refuses that shape, and with it the whole key set: an
    honest ES256 key beside such an entry verified nothing (`curia/jws/key-malformed`, "`x` did not
    decode to 32 bytes"). So the Forum accepted a post that no independent verifier could confirm.
  - A missing or null `public_key` answered 500 (`System.ArgumentNullException`, from the base64
    decode).
  - An assertion whose header says `EdDSA`, naming any honest ES256 agent and its `kid`, reached
    `Ed25519Adapter.Verify` with that agent's SubjectPublicKeyInfo, which threw: a 500 anyone could
    cause against every honest agent. That was traced, not run, before the fix. Case 32 now runs it,
    as `StoredKeyFormTests`' `eddsa-header-over-an-es256-key` row.
- **Why nothing caught it.** The rule was written at the two trusted entrances,
  `IssuerSigningKey.FromPem` and `LogSigningKey.FromPem`, which each refuse a key that is not P-256.
  It was written neither at the one anonymous entrance nor in the verifier that both trusted keys
  pass through. The only probe that carried R4.15's number varied only the algorithm's name.
- **Closed** by one rule per algorithm, owned by the adapter that verifies with it:
  `Es256Adapter.IsPublicKey`, a DER SubjectPublicKeyInfo read to its last byte on the curve whose
  OID is P-256's, and `Ed25519Adapter.IsPublicKey`, NSec's import of the raw 32 bytes. Each
  adapter's `Verify` uses its rule and answers false rather than throwing (CS-10); `Jwks.ForAgent`
  omits a stored key its rule refuses; and the route asks `Jwks.CanPublish` after base64 and refuses
  400 `curia/enroll/invalid-key`, with a detail that names the form and never the key. A missing
  `public_key` is 400 `invalid-key`, `public_key is missing`. A row written before the fix that is
  not a key of its algorithm now meets the token endpoint as any signature that does not verify
  does, 401 `curia/authn/signature-invalid`, and its key set omits it. The facts are `AdapterTests`'
  three `R4_15_…` facts in `Curia.Canon.Sodium.Tests`,
  `EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten`,
  `EnrollmentIdentifierTests.R4_28_AnEd25519KeyIsRegisteredAndPublishedAsTheOctetKeyPairItIs`,
  `EnrollmentIdentifierTests.AnEnrollmentWithoutAPublicKeyIsRefusedByName`, and
  `StoredKeyFormTests`' two facts.

**(d) The length.** An `agent_id` over 2,684 UTF-8 bytes, or a `kid` of 3,000, answered 500 with
Postgres error 54000, an index row over the btree's 2,704-byte limit. Nothing was written: the
review swept `agent_id` from 2,670 to 2,700 bytes, and every 201 left one key row and one event and
every 500 none. **Closed** by a cap of 1,024 UTF-8 bytes on `agent_id` and on `kid`, counted with
`Encoding.UTF8.GetByteCount` after the text check and refused 400 `curia/enroll/identifier-too-long`
(`field=… bytes=…: at most 1024 UTF-8 bytes`). The cap is an implementation limit, which bounds what
the store can index and what `/v1/jwks?agent=` can carry; it is not R4.5's form, and D4 stays open.
The fact is `EnrollmentIdentifierTests.AnIdentifierLongerThanTheForumStoresIsRefusedByName`, with
rows on both sides of the boundary and one of multi-byte characters.

**What it leaves.**
- **Rows written before the fix stay** (R4.19). A key row no enrollment recorded authenticates
  nothing under R5.20 (D26). A row that is not a key of its algorithm authenticates nothing, and its
  key set omits it. Posts already accepted under a P-384 row stay persisted, and are now verifiable
  by no one, the Forum included. No deployment is hosted.
- **A Forum on which `log:keys` or `log:heads` was enrolled before its first key or head** cannot
  sign a head while that event stands, which is forever. Its operator needs a new database.
- **The two operator writers of the noncharacter class**, the moderation record's reason and
  `AttestOwner`'s: see "Observed during the moderation stage".
- **The JSON binder's own 400 for a lone surrogate** carried the exception's text in the probe. What
  a production host serves there was not checked.
- **EdDSA's form is its length.** NSec imports any 32 bytes. Measured here with the built adapter,
  `Ed25519Adapter.IsPublicKey` answers true for 32 × `0xFF` and for `02 00…00`. `curia-testis`
  accepts the first as a key: a key set holding it beside an honest key still verified the honest
  post. It refuses the second, which is not a curve point, and with it the whole key set: the same
  honest key beside it verified nothing, `curia/jws/key-invalid`. So such a key is registered today,
  and harms the identity that registered it. Once R4.17 and R4.18 let an identity hold two keys, one
  such key would make the other key's posts unverifiable offline. Rotation needs a point check, or
  R4.11's proof of possession, which would refuse every key nobody can sign with.
- **The header's `alg` chooses the verifier** at the token endpoint and for DPoP proofs, without
  being compared with the key's `alg`; see "Observed during the enrollment stage".
- **Two curve checks count coordinate length.** `IssuerSigningKey.cs:116` and `LogSigningKey.cs:51`
  admit any curve with 32-byte coordinates that the platform imports, brainpoolP256r1 and secp256k1
  among them under OpenSSL (traced). The key is operator input, and such a key now signs what the
  Forum's own verifier refuses, so it fails loudly. Converge both on `Es256Adapter.IsPublicKey` the
  next time either file is touched.
- **The OID rule's falsification is traced on Linux, not run.** macOS throws
  `PlatformNotSupportedException` before the OID is read, and Docker needed admin rights. CI on
  Linux runs the brainpool rows expecting `IsPublicKey=False`.
- **U+0000 in other anonymous parameters** is a sweep class of its own, beside D25; see "Observed
  during the enrollment stage".

**Falsified** by the same run as D26. Under case 26 every row now fails at its slug with a 400
`invalid-key`, not a 500: since the key rule, `Jwks.CanPublish` answers false for an algorithm it
has no rule for, so the break no longer reaches the database, and the fact still tells the two
refusals apart. Under case 23 the reflection fact stays green, as it should, since the namespace
clause refuses first; under case 24 the stream fact does, since a post's identifier carries no
prefix. Case 28's brainpool rows stay green on this machine, where macOS cannot import the curve at
all, and case 29 turns them red here instead, as `THROWS PlatformNotSupportedException`. Case 32 is
the one that shows the `EdDSA`-header row carries information.

```
[23] the use case lets an identifier whose stream holds another writer's events reach the store
[23] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 70 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_33_AnIdentifierWhoseStreamHoldsAnotherWritersEventsIsRefusedBeforeTheStoreIsAsked
      Assert.Equal() Failure: Strings differ
      ↓ (pos 13)
      Expected: "curia/enroll/identifier-reserved"
      Actual:   "curia/enroll/contended"
      ↑ (pos 13)
[23] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:    29, Skipped:     0, Total:    30, Duration: 513 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_33_AnEnrollmentNamingAPostIsRefusedAndRegistersNothing
      observed: 500 curia/enroll/contended agent=01M0572TG000Q1K37V4BDA8192 attempts=2; key rows 1; events under the post 1
[23] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[24] the reserved prefixes forget log:
[24] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 70 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_33_EveryAggregateNameTheForumMintsIsReserved
      not reserved: LogEntries.HeadsAggregate (log:heads): enrolled, the store asked 1 time(s); LogEntries.KeysAggregate (log:keys): enrolled, the store asked 1 time(s)
[24] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 329 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.ActaNamespaceTests.R4_33_AnEnrollmentNamingTheActasStreamsIsRefusedAndAHeadCanStillBeSigned
      sign-head exited 2: error: Append targeted an aggregate at an unexpected version (curia/domain/concurrency-conflict): aggregate=log:keys expected=0 actual=1
[24] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[25] CheckString accepts a noncharacter
[25] tests/Curia.Canon.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 34 ms - Curia.Canon.Tests.dll (net10.0)
  FAILED Curia.Canon.Tests.Json.JsonReaderCheckStringTests.CheckStringAgreesWithAdmitOnEveryString
      CsCheck.CsCheckException : Set seed: "7Hbd91rEg5l6" or -e CsCheck_Seed=7Hbd91rEg5l6 to reproduce (8 shrinks, 4,450 skipped, 5,000 total).
      U+FDD2
  FAILED Curia.Canon.Tests.Json.JsonReaderCheckStringTests.CheckStringRefusesANoncharacterAndAnUnpairedSurrogateByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/admit/noncharacter"
      Actual:   "ok"
      ↑ (pos 0)
[25] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 279 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R6_15_AnEnrollmentHoldingANoncharacterIsRefusedBeforeAnythingIsWritten(field: "kid")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/admit/noncharacter field=kid; key rows 0"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R6_15_AnEnrollmentHoldingANoncharacterIsRefusedBeforeAnythingIsWritten(field: "agent_id")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/admit/noncharacter field=agent_id; key r"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
[25] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[25b] the route lets U+0000 through to the database
[25b] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 271 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnEnrollmentHoldingUPlus0000IsRefusedByNameBeforeAnythingIsWritten(field: "agent_id")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/nul-character field=agent_id; key"···
      Actual:   "500 Npgsql.PostgresException (0x80004005): 22021: "···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnEnrollmentHoldingUPlus0000IsRefusedByNameBeforeAnythingIsWritten(field: "kid")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/nul-character field=kid; key rows"···
      Actual:   "500 Npgsql.PostgresException (0x80004005): 22021: "···
      ↑ (pos 0)
[25b] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[26] the route's algorithm check never refuses
[26] tests/Curia.Api.Tests RED
    Failed!  - Failed:     5, Passed:     0, Skipped:     0, Total:     5, Duration: 268 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWithoutAnAlgorithmTheForumVerifiesIsRefusedByName(alg: "es256")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 17)
      Expected: "400 curia/enroll/unsupported-algorithm alg=es256: "···
      Actual:   "400 curia/enroll/invalid-key alg=es256: public_key"···
      ↑ (pos 17)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWithoutAnAlgorithmTheForumVerifiesIsRefusedByName(alg: "")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 17)
      Expected: "400 curia/enroll/unsupported-algorithm alg=(none):"···
      Actual:   "400 curia/enroll/invalid-key alg=: public_key is n"···
      ↑ (pos 17)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWithoutAnAlgorithmTheForumVerifiesIsRefusedByName(alg: "RS256")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 17)
      Expected: "400 curia/enroll/unsupported-algorithm alg=RS256: "···
      Actual:   "400 curia/enroll/invalid-key alg=RS256: public_key"···
      ↑ (pos 17)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWithoutAnAlgorithmTheForumVerifiesIsRefusedByName(alg: "HS256")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 17)
      Expected: "400 curia/enroll/unsupported-algorithm alg=HS256: "···
      Actual:   "400 curia/enroll/invalid-key alg=HS256: public_key"···
      ↑ (pos 17)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWithoutAnAlgorithmTheForumVerifiesIsRefusedByName(alg: null)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 17)
      Expected: "400 curia/enroll/unsupported-algorithm alg=(none):"···
      Actual:   "400 curia/enroll/invalid-key alg=: public_key is n"···
      ↑ (pos 17)
[26] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[28] the ES256 form is whatever the platform imports
[28] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     2, Passed:     5, Skipped:     0, Total:     7, Duration: 75 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "p384-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=True; Verify=True"
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "p256-spki-and-a-trailing-byte")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=True; Verify=True"
      ↑ (pos 12)
[28] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     8, Skipped:     0, Total:    10, Duration: 408 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "p384-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "p256-spki-and-a-trailing-byte")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
[28] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 389 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_15_AStoredKeyThatIsNotAKeyOfItsAlgorithmMintsNoTokenAndIsAnsweredAsABadSignatureIs(row: "es256-p384-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "401 {"error":"invalid_client","error_description":"···
      Actual:   "200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6I"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish
      Assert.Equal() Failure: Strings differ
      ↓ (pos 30)
      Expected: ···"0 kids=[stored-x-6b72c27c] x=32 y=32; Y: 200 {"key"···
      Actual:   ···"0 kids=[stored-x-6b72c27c,x-p384-6b72c27c] x=32,48"···
      ↑ (pos 30)
[28] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[29] the ES256 form throws
[29] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     5, Passed:     2, Skipped:     0, Total:     7, Duration: 74 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "empty")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS CryptographicException; Verify="···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "32-raw-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS CryptographicException; Verify="···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "brainpoolP256r1-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS PlatformNotSupportedException; "···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "three-zero-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS CryptographicException; Verify="···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(material: "rsa-2048-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS CryptographicException; Verify="···
      ↑ (pos 12)
[29] tests/Curia.Api.Tests RED
    Failed!  - Failed:     5, Passed:     5, Skipped:     0, Total:    10, Duration: 355 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "32-raw-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "500 System.Security.Cryptography.CryptographicExce"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "rsa-2048-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "500 System.Security.Cryptography.CryptographicExce"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "brainpoolP256r1-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "500 System.PlatformNotSupportedException: The spec"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "empty")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "500 System.Security.Cryptography.CryptographicExce"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "ES256", material: "three-zero-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=ES256: public_key"···
      Actual:   "500 System.Security.Cryptography.CryptographicExce"···
      ↑ (pos 0)
[29] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 357 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_15_AStoredKeyThatIsNotAKeyOfItsAlgorithmMintsNoTokenAndIsAnsweredAsABadSignatureIs(row: "es256-32-raw-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "401 {"error":"invalid_client","error_description":"···
      Actual:   "500 System.Security.Cryptography.CryptographicExce"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish
      Assert.Equal() Failure: Strings differ
      ↓ (pos 3)
      Expected: "X: 200 kids=[stored-x-bdf588f8] x=32 y=32; Y: 200 "···
      Actual:   "X: 500 System.Security.Cryptography.CryptographicE"···
      ↑ (pos 3)
[29] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[30] the EdDSA form accepts anything
[30] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 28 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "p256-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=True; Verify=False"
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "33-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=True; Verify=False"
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "empty")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=True; Verify=False"
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "31-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=True; Verify=False"
      ↑ (pos 12)
[30] tests/Curia.Api.Tests RED
    Failed!  - Failed:     3, Passed:     7, Skipped:     0, Total:    10, Duration: 382 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "EdDSA", material: "31-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "EdDSA", material: "33-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "EdDSA", material: "p256-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
[30] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 365 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish
      Assert.Equal() Failure: Strings differ
      ↓ (pos 30)
      Expected: ···"0 kids=[stored-x-a58cca61] x=32 y=32; Y: 200 {"key"···
      Actual:   ···"0 kids=[stored-x-a58cca61,x-spki-as-eddsa-a58cca61"···
      ↑ (pos 30)
[30] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[31] the EdDSA form refuses every key
[31] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 25 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_AnHonestKeyOfEachAlgorithmIsAPublicKeyAndVerifies
      Assert.Equal() Failure: Strings differ
      ↓ (pos 57)
      Expected: ···"erify=True. EdDSA: IsPublicKey=True; Verify=True"
      Actual:   ···"erify=True. EdDSA: IsPublicKey=False; Verify=False"
      ↑ (pos 57)
[31] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 248 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_28_AnEd25519KeyIsRegisteredAndPublishedAsTheOctetKeyPairItIs
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "201 enrolled; OKP Ed25519 EdDSA ed25519-dff75f10 x"···
      Actual:   "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      ↑ (pos 0)
[31] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[32] the EdDSA read throws
[32] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 27 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "p256-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS FormatException; Verify=THROWS "···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "33-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS FormatException; Verify=THROWS "···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "empty")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS FormatException; Verify=THROWS "···
      ↑ (pos 12)
  FAILED Curia.Canon.Sodium.Tests.AdapterTests.R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(material: "31-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "IsPublicKey=False; Verify=False"
      Actual:   "IsPublicKey=THROWS FormatException; Verify=THROWS "···
      ↑ (pos 12)
[32] tests/Curia.Api.Tests RED
    Failed!  - Failed:     3, Passed:     7, Skipped:     0, Total:    10, Duration: 349 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "EdDSA", material: "31-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      Actual:   "500 System.FormatException: The key BLOB is not in"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "EdDSA", material: "33-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      Actual:   "500 System.FormatException: The key BLOB is not in"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(alg: "EdDSA", material: "p256-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key alg=EdDSA: public_key"···
      Actual:   "500 System.FormatException: The key BLOB is not in"···
      ↑ (pos 0)
[32] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 334 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_15_AStoredKeyThatIsNotAKeyOfItsAlgorithmMintsNoTokenAndIsAnsweredAsABadSignatureIs(row: "eddsa-header-over-an-es256-key")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "401 {"error":"invalid_client","error_description":"···
      Actual:   "500 System.FormatException: The key BLOB is not in"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish
      Assert.Equal() Failure: Strings differ
      ↓ (pos 3)
      Expected: "X: 200 kids=[stored-x-58cdbf5a] x=32 y=32; Y: 200 "···
      Actual:   "X: 500 System.FormatException: The key BLOB is not"···
      ↑ (pos 3)
[32] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[33] the key set stops omitting
[33] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 367 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish
      Assert.Equal() Failure: Strings differ
      ↓ (pos 3)
      Expected: "X: 200 kids=[stored-x-74ae8288] x=32 y=32; Y: 200 "···
      Actual:   "X: 500 System.Security.Cryptography.CryptographicE"···
      ↑ (pos 3)
[33] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[34] a missing public_key reaches base64
[34] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 276 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnEnrollmentWithoutAPublicKeyIsRefusedByName(publicKey: "null")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key public_key is missing"···
      Actual:   "500 System.ArgumentNullException: Value cannot be "···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnEnrollmentWithoutAPublicKeyIsRefusedByName(publicKey: "omitted")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/invalid-key public_key is missing"···
      Actual:   "500 System.ArgumentNullException: Value cannot be "···
      ↑ (pos 0)
[34] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[35] the cap never refuses
[35] tests/Curia.Api.Tests RED
    Failed!  - Failed:     4, Passed:     2, Skipped:     0, Total:     6, Duration: 322 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnIdentifierLongerThanTheForumStoresIsRefusedByName(field: "agent_id", asciiBytes: 40, multiByteChars: 600, expected: "400")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/identifier-too-long field=agent_i"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnIdentifierLongerThanTheForumStoresIsRefusedByName(field: "agent_id", asciiBytes: 1025, multiByteChars: 0, expected: "400")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/identifier-too-long field=agent_i"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnIdentifierLongerThanTheForumStoresIsRefusedByName(field: "agent_id", asciiBytes: 2685, multiByteChars: 0, expected: "400")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/identifier-too-long field=agent_i"···
      Actual:   "500 Npgsql.PostgresException (0x80004005): 54000: "···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnIdentifierLongerThanTheForumStoresIsRefusedByName(field: "kid", asciiBytes: 1025, multiByteChars: 0, expected: "400")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/identifier-too-long field=kid byt"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
[35] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[36] the cap counts UTF-16 code units
[36] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     5, Skipped:     0, Total:     6, Duration: 304 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.AnIdentifierLongerThanTheForumStoresIsRefusedByName(field: "agent_id", asciiBytes: 40, multiByteChars: 600, expected: "400")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/identifier-too-long field=agent_i"···
      Actual:   "201 enrolled; key rows 1, events 1"
      ↑ (pos 0)
[36] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

### D28 — the key store alone decided which key an identity held, and every verifier took its word *(opened by `curia-architect` on 2026-09-26 and closed by the key-binding stage; errata G16)*

The event log recorded which `kid` an identity enrolled with (`agent.enrolled`, R4.31) and never
which key. The key store held the key. At 1dbe0ff every path that honoured a key read the store and
nothing else: ingest's `IAuthorKeyResolver` and the token endpoint's `IAgentKeyResolver` were both
the store (`Program.cs:112`, `:114`), and the key set every reader verifies with listed whatever the
store held (`ForumEndpoints.cs:1209`). R6.52's first check verifies a post under a key that key set
serves. So a row counted as the identity's key because the store held it.

Two kinds of row the store can hold under an enrolled identity were never bound by any enrollment.
The key-binding stage's facts printed each, on the unchanged code, through the real Forum over
Postgres (the build-check's run, recorded in the stage's spec, §1.2; the suffixes are random per
run, and cases 5 and 1 below print the same lines again under a patch that restores the old
behaviour):

- **A second key**, as D22's hole added them and as any store written before the enrollment stage
  may still hold them. R4.19 forbids deleting such a row and R4.32 changing it, so every one in any
  store stays. The fact writes one as the provisioning role:

  ```
  a key the log never bound acted as https://agents.example/bound-16404ed9: token request 200, question 201 {"post_id":"01M0572TG068D3GQRVKBD6MSCG", …
  ```
- **Other bytes under the identity's own `kid`**, registered by a lost row's recovery (errata G14's
  fourth cost; "R4.31's one exception cannot check bytes", under "Observed during the enrollment
  stage"):

  ```
  other bytes under victim-f4f953ce were answered 201, and their holder obtained the victim's token
  ```

A reader holding the key set could not tell either from the identity's own key: the key set served
it, and the signature verified under it. **The log's record constrained nothing**, and every test
that asserted an identity's key was its own had enrolled that identity through the one route that
writes both records, so the two never disagreed where a test could see it (trap 22).

Two smaller findings rode with it. **The seam the enrollment stage left** (its spec's Decision 8,
and "What comes next" as it stood): `EnrollmentBinding` read the identity's first `agent.enrolled`
and nothing else, so a key bound any other way, as R4.18's rotation will bind one, would be refused
`curia/enroll/already-enrolled` when re-announced, while re-announcing the enrolled key succeeds and
the API test helper does it on every authentication. And **one of D27's two
leftovers**: both validators chose the verifier by the header's `alg` and never compared it with the
key's, so a header naming the other allowed algorithm read as a bad signature
(`Actual: "curia/authn/signature-invalid "`, both AuthN facts on the unchanged validators).

**Closed** by errata G16:
- **R4.34.** `EnrollAgent` appends `agent.key-bound`, `{ agent_id, kid, jwk }`, in the same append as
  `agent.enrolled`, which `EnrollIdentityTests.R4_34_AnEnrollmentAndItsBindingAreOneAppend` holds
  against a store that takes one append and refuses the next (case 36). The JWK is exactly what
  `PublicJwk.Of` renders for the key set, so the key published and the key bound are one
  computation; `EnrollIdentity` renders it before the key store is asked, so a key the log cannot
  carry leaves no row. `conformance/acta/key-bound-entry` pins the entry kind in both runners, and
  `EnrollIdentityTests.R4_34_AnEnrollmentWritesTheConformanceVectorsPayload` holds the writer to it.
- **R4.35.** `LogBoundKeys` asks the store first and then holds its answer to the log's bindings.
  Ingest, the token endpoint (through `LogBoundAgentKeyResolver`) and the key set all read through
  it, and each published key names `curia_log_index`, the leaf that binds it. A key the log does not
  bind is refused `curia/keys/not-bound-by-the-log` and is not published. Facts:
  `KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing`, which
  asserts the damage first; `LogBoundKeysTests`' facts, among them
  `R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid`, which holds the key set to the binding's bytes
  and not its `kid` alone (case 38); both rows of
  `StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken`.
- **R4.31 (revised).** `EnrollmentBinding` reads every binding an identity holds. A bound `kid`
  whose binding carries a key admits only that key, in `EnrollIdentity` before the store is asked
  and again in `EnrollAgent` at the log's record. Facts:
  `EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName`, and three in
  `EnrollIdentityTests`: `R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused`,
  `R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound` and, for the seam,
  `R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs`. An identifier the log never enrolled is
  refused `curia/enroll/keys-ambiguous` while the store holds more than one key for it, whichever it
  presents: binding the one presented would let anyone holding a stored key's public half make that
  key the identity's, and turn every post signed under its own key into a failure of the signature
  check (the entry's review found it). Facts:
  `EnrollIdentityTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys`
  and, over HTTP,
  `EnrollmentBindingTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented`.
- **R5.21.** Both validators refuse `curia/authn/alg-key-mismatch` before a verifier is chosen
  (`ClientAssertionValidatorTests.R5_21_AHeaderNamingAnotherAlgorithmThanTheKeysIsRefusedByName`,
  `AccessTokenValidatorDpopTests.R5_21_AProofWhoseHeaderNamesAnotherAlgorithmThanItsJwkIsRefusedByName`,
  and over HTTP
  `StoredKeyFormTests.R5_21_AnAssertionWhoseHeaderNamesAnotherAlgorithmThanItsKeyIsRefusedByName`).
  At the key-binding stage the pin held at those two alone: a client assertion's at the token
  endpoint, and a DPoP proof's at the resource server. Since the rotation stage's PR 0 (D29 closed) it
  holds at a third: the token request's DPoP proof passes `DpopProofValidator.Validate`, which refuses
  `curia/authn/alg-key-mismatch` before a verifier is chosen
  (`src/Curia.AuthN/Dpop/DpopProofValidator.cs:93-94`).
- **R6.54.** `PostVerifier`'s fourth check, `key`, and `curia-testis log author`, which reads no agent
  key set. The overall verdict needs the fourth check verified. Each reader compares the binding with
  the post's author and `kid` before it trusts the binding's key
  (`PostVerifierTests.R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey`), and reads the
  author's binding after the post as *could not be checked*, never *failed*
  (`PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished`). The client holds the binding's
  proof to the head it verified (`PostVerifierTests.R6_54_AKeyBindingProvenUnderAnotherRootFails`),
  and `curia_verify` reports the check on a line of its own
  (`PropertyP22ToolResultTests.R6_54_TheVerifyToolReportsTheKeyCheckSeparately`), as errata G16's
  cross-reference of R11.29 requires. Both readers read the post's author, `kid` and signature from its
  own `post.accepted` entry and not from the read
  (`PostVerifierTests.R6_54_ThePostsKidIsTheOneItsLogEntryHolds`,
  `PostVerifierTests.R6_54_AnEntryOfAnotherTypeCarryingThePostsBytesIsNotItsAcceptance`), and the
  client also fails a post the Forum served as another author's
  (`PostVerifierTests.R6_54_APostServedAsAnotherAuthorsFailsWhateverTheLogBinds`). Where the two
  differ, R6.54 says so: a key document the client could not parse, and a missing head, are *could
  not be checked* for the client, which fetches, and a failure where `curia-testis` can see one, since
  it is handed files (`PostVerifierTests.R6_54_AKeyDocumentThatDoesNotParseIsNotChecked`,
  `PostVerifierTests.R6_54_WithNoSignedHeadTheKeyIsNotCheckedAndNoLogMaterialIsFetched`). A proof the
  head does not commit to fails even where its entry is withheld
  (`PostVerifierTests.R6_54_AKeyProofOffTheHeadFailsThoughItsEntryIsWithheld`,
  `PostVerifierTests.R6_54_APostProofOffTheHeadFailsThoughThePostsEntryIsWithheld`), and every value
  `curia_verify`'s result names that the client did not write is quoted as a JSON string, so none can
  begin a line (`PostVerifierTests.R11_29_AValueTheLogRecordedCannotBeginALineOfTheResult`).

**What it does not close.** An identity enrolled before G16 is bound by its `kid` alone. A key under
that `kid` is still honoured whatever its bytes, because nothing recorded the original, and a reader
reports that identity's posts as *could not be checked*, never *verified*. A key under any other
`kid` is no longer honoured for it
(`LogBoundKeysTests.R4_35_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone`). Nothing appends a
binding for such an identity; whether an operator should is the owner's question (the stage's spec,
§2.1). An identity the log never enrolled at all, one enrolled before `agent.enrolled` existed, is
bound by the first request that re-presents the one key the store holds for it, after its whole
history; one the store holds several keys for is refused, and has no path back until R4.18's
recovery exists (errata G16's fifth cost; see below). No identity can rotate, revoke or recover a
key yet: see "What comes next". Here and below "a reader" means the three that run
R6.54: `curia verify`, `curia_verify` and `curia-testis log author`. `curia read`, `curia thread`,
`curia_read` and `curia_search` verify a post's signature under the key set the Forum serves and
print it "verified locally against kid=…" (`src/Curia.Client/SignatureCheck.cs:68`) with no binding
check. Against a writer of the key store alone R4.35 covers them too, since the Forum publishes no
key the log does not bind; against a Forum process that serves a key set of its choosing, D28's
reader half stands on those paths.

**Falsified:** the stage's Task 9, sixty-one cases in seventy-seven suite runs, each red by name, every
restore proved by bytes and by `git diff`, and the gates re-run unpatched after a
`--no-incremental` rebuild. Cases 10 and 11 each turn off one half of R4.31 (revised)'s material
check and run the application facts alone: by design the other half keeps the HTTP fact green
(traced, not run in this pass). Case 12, both halves off at once, is the one the surface sees, and
R4.35 still refuses the impostor's token. Cases 28 and 29 came from the pre-flight scan's finding
B1: each reader reading the author's binding after the post as a failure (28, `curia-testis`; 29,
the client). Case 30 came from the amendments that answered the scan: the client not comparing the
binding with the post (30). Cases 31 to 33 came from errata G16's review: the
refusal of several stored keys counting another identifier's (31), the client holding the key's
proof to its own root rather than the signed head's (32), and `curia_verify` dropping the key
check's line (33). Cases 34 and 35 came from Task 3's review: the renderer stripping a P-256
coordinate's leading zeros (34), and rendering an Ed25519 key in `EC`'s form, errata D4's trap (35).
Case 36 came from Task 4's review: the enrollment and its binding written in two appends, which
only a store that takes one append and refuses the next can tell from one (36). Cases 37 to 40
came from Task 5's review: the key set rendering every key it lists and throwing on one it cannot,
which only a pre-G16 identity's row still reaches (37); the key set matching a stored key to its
binding by `kid` alone, which publishes another holder's key under a bound `kid` (38); the
resolver handing on the log reader's refusal as its own, a 401 for a server fault (39); and the key
set serving its keys without positions when the log cannot be read (40). Cases 41 to 48 came from
Task 7's review: `curia-testis` holding the key's proof to its own root rather than the signed
head's (41); dropping, one at a time, its comparison of the binding's `kid` (42), its stream (43)
and its type (44); `log author` reporting a missing head before it checks anything (45); taking
the two proofs from two trees when there is no head (46); and reading the post's author and `kid`
from an envelope ADMIT never saw (47) and a header no parser refused (48). Cases 49 to 57 came
from Task 8's agreement probe: the client verifying a post the Forum served as another author's
(49), reading an entry of another type carrying the post's bytes as its acceptance (50), and
taking the read's `kid` and signature in place of the log's (51); holding the key's proof to the
head only after its entry's order and type (52); ignoring the index the key's entry route states,
in the client (53) and in `curia-testis` (56), and the leaf hash it states in `curia-testis` (55);
reading a key entry the client could not parse as failed (54); and reporting a key set naming no
leaf before the post's own record (57). Cases 58 to 61 came from Task 8's review: the client
reporting a key proof the signed head does not commit to as *could not be checked* when the key's
entry is withheld (58), the same of the post's own proof (59) and of the key line that inherits the
post's failure (60), and the one helper that quotes what `curia_verify` did not write leaving a
control character raw (61). Cases 62 to 65 came from the stage's final review: VERIFY reading the
envelope from the submission as it arrived (62) and the enrollment route asking the `kid` in place
of the agent identifier (63), which are D30's; the client reporting an honest post whose own entry
cannot be fetched as failed (64); and `curia-testis`, with no head, comparing the two proofs' tree
sizes and not their roots (65). With them the stage has sixty-five cases in eighty-two suite runs.

The lines below are that run's, as printed in `falsify.log` (Task 9's run on 40df319): every case,
case 27's included, which is D16's evidence. The plan's Task 9 table says what each case must fail,
and why the facts that stay green should.

```
[1] the enrollment appends its binding under a type no reader reads
[1] tests/Curia.Application.Tests RED
    Failed!  - Failed:     9, Passed:    14, Skipped:     0, Total:    23, Duration: 99 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys
      Assert.Equal() Failure: Collections differ at index 1
      Expected: "agent.key-bound"
      Actual:   "agent.key-bound-unread"
      ↑ (pos 15)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_ReEnrollingTheBoundKeyWritesNothing
      Assert.Equal() Failure: Collections differ at index 1
      Expected: "agent.key-bound"
      Actual:   "agent.key-bound-unread"
      ↑ (pos 15)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs
      Assert.Equal() Failure: Collections differ at index 1
      Expected: "agent.key-bound"
      Actual:   "agent.key-bound-unread"
      ↑ (pos 15)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_34_AnEnrollmentAndItsBindingAreOneAppend
      Assert.Equal() Failure: Strings differ
      Expected: "enrolled; agent.enrolled, agent.key-bound"
      Actual:   "enrolled; agent.enrolled, agent.key-bound-unread"
      ↑ (pos 41)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_34_AnEnrollmentBindsItsKeyInTheLogBesideItsRecord
      Assert.Equal() Failure: Collections differ at index 1
      Expected: "agent.key-bound"
      Actual:   "agent.key-bound-unread"
      ↑ (pos 15)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound
      Assert.Equal() Failure: Collections differ at index 1
      Expected: "agent.key-bound"
      Actual:   "agent.key-bound-unread"
      ↑ (pos 15)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_34_AnEnrollmentWritesTheConformanceVectorsPayload
      Assert.Single() Failure: The collection did not contain any matching items
      Expected:   (predicate expression)
      Collection: [AppendedEvent { Seq = EventSequence { Value = 1 }, AggregateId = AggregateId { Value = agent://curia.example/tuesdaycrowd/scriptor }, ServerTimestamp = 2026-09-26T12:00:00.0000000+00:00, Event = DomainEvent { Id = EventId { Val
[1] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:    18, Skipped:     0, Total:    20, Duration: 1 s - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName
      other bytes under victim-1639354a were answered 201, and their holder obtained the victim's token
  FAILED Curia.Api.Tests.KeyBindingTests.R6_54_EachPublishedKeyNamesTheLeafThatBindsIt
      Assert.Equal() Failure: Strings differ
      ↓ (pos 6)
      Expected: "agent.key-bound https://agents.example/positioned-"···
      Actual:   "agent.enrolled https://agents.example/positioned-7"···
      ↑ (pos 6)
[1] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[2] a binding that carries the key answers on the kid alone
[2] tests/Curia.Application.Tests RED
    Failed!  - Failed:     5, Passed:    25, Skipped:     0, Total:    30, Duration: 80 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid
      Assert.Equal() Failure: Strings differ
      ↓ (pos 16)
      Expected: "stored=1 bound=[]"
      Actual:   "stored=1 bound=[alice-1@01M3ESC9G0M4Y41EJNA4AKNT6T"···
      ↑ (pos 16)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_OtherBytesUnderTheBoundKidAreRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log"
      Actual:   "resolved"
      ↑ (pos 0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKeyBindingThatCarriesNoKeyBindsNoBytesUnderItsKid
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
[2] tests/Curia.Api.Tests RED
    Failed!  - Failed:     4, Passed:    11, Skipped:     0, Total:    15, Duration: 1 s - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName
      other bytes under victim-31b317af were answered 201, and their holder obtained the victim's token
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_AnotherHoldersKeyUnderABoundKidSignsNothingMintsNothingAndIsNotPublished
      another holder's key under stored-victim-fed567e6 acted as https://agents.example/stored-victim-fed567e6: token request 200, question 201 {"post_id":"01M0572TG053GWGN50P2JPT1N4","digest":"sha256:dfdaffb3808996e0da59bf9819e0b3f4233d915e5e247
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(material: "32-raw-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The event log binds no su"···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(material: "p384-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The event log binds no su"···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
[2] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[3] the kid-only binding stands beside a binding that carries the key
[3] tests/Curia.Application.Tests RED
    Failed!  - Failed:     7, Passed:    23, Skipped:     0, Total:    30, Duration: 84 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid
      Assert.Equal() Failure: Strings differ
      ↓ (pos 16)
      Expected: "stored=1 bound=[]"
      Actual:   "stored=1 bound=[alice-1@01M3ESC9G0502SZ6XXAH3DTJ0N"···
      ↑ (pos 16)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_OtherBytesUnderTheBoundKidAreRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log"
      Actual:   "resolved"
      ↑ (pos 0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetListsOnlyTheKeysTheLogBinds
      Assert.Equal() Failure: Strings differ
      ↓ (pos 49)
      Expected: ···"tored=2 bound=[alice-1@01M3ESC9G0MPRH8FWVDJM8ETDC]"
      Actual:   ···"tored=2 bound=[alice-1@01M3ESC9G0MPRH8FWVDJM8ETDB]"
      ↑ (pos 49)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKeyBindingThatCarriesNoKeyBindsNoBytesUnderItsKid
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_34_OnlyTheIdentitysOwnStreamBindsItsKeys
      Assert.Equal() Failure: Strings differ
      Expected: "alice-1"
      Actual:   "alice-1,alice-1"
      ↑ (pos 7)
[3] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     7, Skipped:     0, Total:     8, Duration: 607 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName
      other bytes under victim-8504ca2f were answered 201, and their holder obtained the victim's token
[3] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[4] the binding set is its first binding alone
[4] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    22, Skipped:     0, Total:    23, Duration: 91 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs
      System.InvalidOperationException : curia/enroll/already-enrolled: That agent identifier is already enrolled with a different key
[4] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[5] the resolver honours whatever the store resolves
[5] tests/Curia.Application.Tests RED
    Failed!  - Failed:     3, Passed:     4, Skipped:     0, Total:     7, Duration: 46 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone
      Assert.Equal() Failure: Strings differ
      ↓ (pos 27)
      Expected: ···"ice-1: resolved; hole-1: curia/keys/not-bound-by-t"···
      Actual:   ···"ice-1: resolved; hole-1: resolved"
      ↑ (pos 27)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_OtherBytesUnderTheBoundKidAreRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log"
      Actual:   "resolved"
      ↑ (pos 0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindIsRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log agent=https://agen"···
      Actual:   "resolved"
      ↑ (pos 0)
[5] tests/Curia.Api.Tests RED
    Failed!  - Failed:     5, Passed:    18, Skipped:     0, Total:    23, Duration: 1 s - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.TokenSubjectBindingTests.R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log"
      Actual:   "That agent is not enrolled"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_AnotherHoldersKeyUnderABoundKidSignsNothingMintsNothingAndIsNotPublished
      another holder's key under stored-victim-7b482d8f acted as https://agents.example/stored-victim-7b482d8f: token request 200, question 201 {"post_id":"01M0572TG04YHK3WSNMND9J8WG","digest":"sha256:cd809ade8a1498b6156f22ce8f545ec1a5b6e6abeb50d
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(material: "32-raw-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The event log binds no su"···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(material: "p384-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The event log binds no su"···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing
      a key the log never bound acted as https://agents.example/bound-120ab148: token request 200, question 201 {"post_id":"01M0572TG09YSHS6CSNWGAVVS2","digest":"sha256:c57fc02ef79f96a205fb2bef146e938a880c9ec7427740c7d6058d0acfbe78ec","server_ts"
[5] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[6] the key set lists every key the store holds
[6] tests/Curia.Application.Tests RED
    Failed!  - Failed:     2, Passed:     5, Skipped:     0, Total:     7, Duration: 51 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid
      Assert.Equal() Failure: Strings differ
      ↓ (pos 16)
      Expected: "stored=1 bound=[]"
      Actual:   "stored=1 bound=[alice-1@unbound]"
      ↑ (pos 16)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetListsOnlyTheKeysTheLogBinds
      Assert.Equal() Failure: Strings differ
      ↓ (pos 50)
      Expected: ···"alice-1@01M3ESC9G0ZKXZAXS9GE8F7B6R]"
      Actual:   ···"alice-1@01M3ESC9G0ZKXZAXS9GE8F7B6R,hole-1@unbound]"
      ↑ (pos 50)
[6] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 810 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing
      a key the log never bound acted as https://agents.example/bound-46ddc5d5: token request 401, question 401 {"type":"curia/keys/not-bound-by-the-log","title":"The event log binds no such key to that agent","detail":"agent=https://agents.examp
[6] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[7] the key set reads the log before the store
[7] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5, Duration: 250 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_TheKeySetAnswersEveryAgentWithoutA500(encoded: "https%3A%2F%2Fagents.example%2Fnul%00")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "404 curia/keys/unknown-agent"
      Actual:   "500 Npgsql.PostgresException (0x80004005): 22021: "···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_TheKeySetAnswersEveryAgentWithoutA500(encoded: "%00")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "404 curia/keys/unknown-agent"
      Actual:   "500 Npgsql.PostgresException (0x80004005): 22021: "···
      ↑ (pos 0)
[7] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[8] the token endpoint resolves through the store alone
[8] tests/Curia.Api.Tests RED
    Failed!  - Failed:     6, Passed:    17, Skipped:     0, Total:    23, Duration: 1 s - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.TokenSubjectBindingTests.R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log"
      Actual:   "That agent is not enrolled"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_AnotherHoldersKeyUnderABoundKidSignsNothingMintsNothingAndIsNotPublished
      another holder's key under stored-victim-81d92b6c acted as https://agents.example/stored-victim-81d92b6c: token request 200, question 401 {"type":"curia/keys/not-bound-by-the-log","title":"The event log binds no such key to that agent","det
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(material: "32-raw-bytes")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The event log binds no su"···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(material: "p384-spki")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The event log binds no su"···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing
      a key the log never bound acted as https://agents.example/bound-93af7a8b: token request 200, question 401 {"type":"curia/keys/not-bound-by-the-log","title":"The event log binds no such key to that agent","detail":"agent=https://agents.examp
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal(path: "token", refuses: "stream", expected: "500 {\"error\":\"server_error\",\"error_descriptio"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "500 {"error":"server_error","error_description":"T"···
      Actual:   "200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6I"···
      ↑ (pos 0)
[8] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[9] ingest resolves through the store alone
[9] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12, Duration: 834 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing
      a key the log never bound acted as https://agents.example/bound-358e8863: token request 401, question 201 {"post_id":"01M0572TG0GK40F7FQF1M1KA4C","digest":"sha256:c9b72eac33b8e63b0aefa0b9ac241ec1d5e98ea8ef704561573db39cf6e7e379","server_ts"
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal(path: "question", refuses: "stream", expected: "503 {\"type\":\"curia/log/unreadable\",\"title\":\"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "503 {"type":"curia/log/unreadable","title":"The ev"···
      Actual:   "201 {"post_id":"01M0572TG09QS6TRJYTN84EBF6","diges"···
      ↑ (pos 0)
[9] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[10] the use case's material check off
[10] tests/Curia.Application.Tests RED
    Failed!  - Failed:     2, Passed:    21, Skipped:     0, Total:    23, Duration: 99 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AKeyBindingThatCarriesNoKeyBindsNoBytesUnderItsKid
      Assert.Empty() Failure: Collection was not empty
      Collection: [RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = alice-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 9/26/2026 12:00:00 PM +00:00, NotAfter =  }]
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused
      Assert.Empty() Failure: Collection was not empty
      Collection: [RegisteredKey { Key = PublicKeyMaterial { Alg = ES256, Kid = alice-1, Public = System.ReadOnlyMemory<Byte>[91] }, NotBefore = 9/26/2026 12:00:00 PM +00:00, NotAfter =  }]
[10] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[11] the log's record material check off
[11] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    22, Skipped:     0, Total:    23, Duration: 94 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
[11] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[12] both material checks off
[12] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     7, Skipped:     0, Total:     8, Duration: 611 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName
      other bytes under victim-12a22e77 were answered 201, and their holder obtained no token
[12] restore clean (bytes equal to the kept copy: 2/2; git diff --quiet: yes)
[13] a client assertion's alg is not pinned to its key's
[13] tests/Curia.AuthN.Tests RED
    Failed!  - Failed:     1, Passed:    14, Skipped:     0, Total:    15, Duration: 157 ms - Curia.AuthN.Tests.dll (net10.0)
  FAILED Curia.AuthN.Tests.ClientAssertionValidatorTests.R5_21_AHeaderNamingAnotherAlgorithmThanTheKeysIsRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "curia/authn/alg-key-mismatch header=ES256 key=EdDS"···
      Actual:   "curia/authn/signature-invalid "
      ↑ (pos 12)
[13] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 291 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R5_21_AnAssertionWhoseHeaderNamesAnotherAlgorithmThanItsKeyIsRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 51)
      Expected: ···"nt","error_description":"The header names another "···
      Actual:   ···"nt","error_description":"Signature does not verify"···
      ↑ (pos 51)
[13] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[14] a DPoP proof's alg is not pinned to its jwk's
[14] tests/Curia.AuthN.Tests RED
    Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 56 ms - Curia.AuthN.Tests.dll (net10.0)
  FAILED Curia.AuthN.Tests.AccessTokenValidatorDpopTests.R5_21_AProofWhoseHeaderNamesAnotherAlgorithmThanItsJwkIsRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 12)
      Expected: "curia/authn/alg-key-mismatch header=ES256 key=EdDS"···
      Actual:   "curia/authn/signature-invalid "
      ↑ (pos 12)
[14] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[15] the renderer publishes an ES256 key on any named curve
[15] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 188 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(alg: "ES256", material: "p384-spki", isKey: False)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 23)
      Expected: "adapter=False rendered=False"
      Actual:   "adapter=False rendered=True"
      ↑ (pos 23)
[15] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[16] the renderer publishes an EdDSA key of any length
[16] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     3, Passed:    14, Skipped:     0, Total:    17, Duration: 181 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(alg: "EdDSA", material: "33-bytes", isKey: False)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 23)
      Expected: "adapter=False rendered=False"
      Actual:   "adapter=False rendered=True"
      ↑ (pos 23)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(alg: "EdDSA", material: "p256-spki", isKey: False)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 23)
      Expected: "adapter=False rendered=False"
      Actual:   "adapter=False rendered=True"
      ↑ (pos 23)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(alg: "EdDSA", material: "31-bytes", isKey: False)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 23)
      Expected: "adapter=False rendered=False"
      Actual:   "adapter=False rendered=True"
      ↑ (pos 23)
[16] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[17] two public JWKs compared by length
[17] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 115 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_34_SameKeyComparesTheKeyNotTheReference
      Assert.Equal() Failure: Strings differ
      ↓ (pos 20)
      Expected: "same=True different=False"
      Actual:   "same=True different=True"
      ↑ (pos 20)
[17] tests/Curia.Application.Tests RED
    Failed!  - Failed:     4, Passed:    26, Skipped:     0, Total:    30, Duration: 87 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid
      Assert.Equal() Failure: Strings differ
      ↓ (pos 16)
      Expected: "stored=1 bound=[]"
      Actual:   "stored=1 bound=[alice-1@01M3ESC9G0E04TCP5CH01AD3JT"···
      ↑ (pos 16)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_OtherBytesUnderTheBoundKidAreRefusedByName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "curia/keys/not-bound-by-the-log"
      Actual:   "resolved"
      ↑ (pos 0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = True }
[17] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     7, Skipped:     0, Total:     8, Duration: 620 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName
      other bytes under victim-70cbf136 were answered 201, and their holder obtained the victim's token
[17] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[18] the renderer swaps a P-256 key's coordinates
[18] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     2, Passed:    15, Skipped:     0, Total:    17, Duration: 108 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_28_AP256CoordinateThatBeginsWithZeroIsRenderedAtFullWidth
      Assert.Equal() Failure: Strings differ
      ↓ (pos 69)
      Expected: ···"zeros","kty":"EC","x":"AAB-5fiMEJIpXm_c9khweWztuCm"···
      Actual:   ···"zeros","kty":"EC","x":"AAC7ecfw4dPbjRBKTllZ2QksN8o"···
      ↑ (pos 69)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_28_AP256KeyIsRenderedAsRfc7518sEcForm
      Assert.Equal() Failure: Strings differ
      ↓ (pos 64)
      Expected: ···"7515-a3","kty":"EC","x":"f83OJ3D2xF1Bg8vub9tLe1gHM"···
      Actual:   ···"7515-a3","kty":"EC","x":"x_FEzRu9m36HLN_tue659LNpX"···
      ↑ (pos 64)
[18] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    22, Skipped:     0, Total:    23, Duration: 98 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_34_AnEnrollmentBindsItsKeyInTheLogBesideItsRecord
      Assert.Equal() Failure: Strings differ
      ↓ (pos 95)
      Expected: ···"256,kid:alice-1,kty:EC,x:7g1n5OfeL8_UE88SYuLM8L4RV"···
      Actual:   ···"256,kid:alice-1,kty:EC,x:v14P9KDkPE-i0vRbBtb0Nb_O5"···
      ↑ (pos 95)
[18] tests/Curia.Api.Tests RED
    Failed!  - Failed:     5, Passed:     3, Skipped:     0, Total:     8, Duration: 513 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "xANgzBrpcN-ZweIIkyf4rJZVLOzv6yX0RXnco7t2wiw"
      Actual:   "U0wyZr1qg0a55M_UvSi17oF3VZMr5UDH5TzZmYUtfOk"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "RCN0dxAmTa5_02GzYW6Pz3Bi97cTpbpDUF1SSxEyib8"
      Actual:   "ng9mcUjBS12OaRZM6KYdfjx0OouCAy0zYgNA6aHtNAg"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_ReEnrollingTheEnrolledKeyIsAcceptedAndChangesNothing
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "llBTcCEQj3J8KDeaKT0FhzWqoONQGcGzUuyG2ANx7jk"
      Actual:   "fakz1wpWmv4XdUu0Sac5lNepyPCH9KHkxdymQlAYygk"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "mI9kW0WCe15QnnM_4Uz4wcWRCruFmnpqaFj0yn08XzM"
      Actual:   "uWA5h3jR0ky5dM-bCLsRfpVmW8J3cybObS3itOIF7S4"
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "y0W-ApngPha7cDeTH8VPHNPyuul_8rsJNjb8Y3oVsbM"
      Actual:   "s-taiy22uBkhskP2_gw_o8tkkEmvAFZzll5nJpjpU7w"
      ↑ (pos 0)
[18] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[19] the client's binding check verifies nothing under the bound key
[19] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 349 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_ALogThatBoundAnotherKeyFailsTheBindingThoughTheSignatureVerifies
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
[19] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[20] the client's binding check ignores the order of the leaves
[20] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 353 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
[20] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[21] the client reads a kid-only enrollment as a verified binding
[21] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 360 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyTheLogNamesByKidAloneIsNotEstablished
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
[21] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[22] the overall verdict ignores the binding
[22] tests/Curia.Client.Tests RED
    Failed!  - Failed:     5, Passed:    39, Skipped:     0, Total:    44, Duration: 352 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeySetNamingNoLeafCannotBeChecked
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyTheLogNamesByKidAloneIsNotEstablished
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyDocumentThatDoesNotParseIsNotChecked(route: "entry")
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyDocumentThatDoesNotParseIsNotChecked(route: "proof")
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
[22] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[23] a key set naming no leaf is read as a verified binding
[23] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 362 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeySetNamingNoLeafCannotBeChecked
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Verified
[23] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[24] curia-testis ignores the order of the leaves
[24] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.13s
  FAILED r6_54_a_key_bound_after_the_post_is_not_established
      thread 'r6_54_a_key_bound_after_the_post_is_not_established' (4953299) panicked at tests/log_author.rs:155:34:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 2, post_index: 0 }
[24] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[25] curia-testis does not compare the binding with the post
[25] cargo log_author RED
    test result: FAILED. 16 passed; 4 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.12s
  FAILED r6_54_another_identitys_enrollment_fails
      thread 'r6_54_another_identitys_enrollment_fails' (4954024) panicked at tests/log_author.rs:216:5:
      assertion `left == right` failed: the log names kid `conformance-ed25519-minimal` only in the author's enrollment, which carries no key [curia/acta/key-not-carried]
      left: ("curia/acta/key-not-carried", true)
      right: ("curia/acta/binding-mismatch", false)
  FAILED r6_54_a_binding_for_another_identity_fails
      thread 'r6_54_a_binding_for_another_identity_fails' (4954011) panicked at tests/log_author.rs:188:34:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
  FAILED r6_54_a_binding_in_another_identitys_stream_fails
      thread 'r6_54_a_binding_in_another_identitys_stream_fails' (4954012) panicked at tests/log_author.rs:357:34:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
  FAILED r6_54_the_authors_enrollment_of_another_kid_fails
      thread 'r6_54_the_authors_enrollment_of_another_kid_fails' (4954028) panicked at tests/log_author.rs:336:5:
      assertion `left == right` failed: the log names kid `another-kid` only in the author's enrollment, which carries no key [curia/acta/key-not-carried]
      left: ("curia/acta/key-not-carried", true)
      right: ("curia/acta/binding-mismatch", false)
[25] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[26] curia-testis reads a kid-only enrollment as not a binding at all
[26] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.12s
  FAILED r6_54_an_enrollment_that_names_the_kid_alone_is_not_checked
      thread 'r6_54_an_enrollment_that_names_the_kid_alone_is_not_checked' (4954724) panicked at tests/log_author.rs:199:5:
      assertion `left == right` failed: key entry has no usable `jwk` [curia/acta/missing-field]
      left: ("curia/acta/missing-field", false)
      right: ("curia/acta/key-not-carried", true)
[26] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 582 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.ActaEndpointTests.R6_54_TestisEstablishesAuthorshipFromTheLogAlone
      Assert.StartsWith() Failure: String start does not match
      String:         "exit 1: error: key entry has no usable `jwk` [curi"···
      Expected start: "exit 3:"
[26] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[27] Curia.Domain regains a seven-case string switch (D16)
[27] tests/Curia.Architecture.Tests RED
    Failed!  - Failed:     1, Passed:     7, Skipped:     0, Total:     8, Duration: 136 ms - Curia.Architecture.Tests.dll (net10.0)
  FAILED Curia.Architecture.Tests.LayeringTests.CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives
      Curia.Domain must depend on nothing beyond the BCL, Curia.Canon, and Curia.Domain.Primitives (CS-7, R11.1-R11.2). Offenders: Curia.Domain.Content.PostKinds
[27] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[28] curia-testis reports the author's binding after the post as a failure
[28] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.13s
  FAILED r6_54_a_key_bound_after_the_post_is_not_established
      thread 'r6_54_a_key_bound_after_the_post_is_not_established' (4956897) panicked at tests/log_author.rs:156:5:
      assertion `left == right` failed: the key is bound at leaf 2, which is not before the post at leaf 0 [curia/acta/bound-after-post]
      left: ("curia/acta/bound-after-post", false)
      right: ("curia/acta/bound-after-post", true)
[28] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 628 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.ActaEndpointTests.R6_54_TestisEstablishesAuthorshipFromTheLogAlone
      Assert.StartsWith() Failure: String start does not match
      String:         "exit 1: error: the key is bound at leaf 10, which "···
      Expected start: "exit 3:"
[28] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[29] the client reports the author's binding after the post as a failure
[29] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 354 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Failed
[29] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[30] the client does not compare the binding with the post's author and kid
[30] tests/Curia.Client.Tests RED
    Failed!  - Failed:     2, Passed:    42, Skipped:     0, Total:    44, Duration: 357 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_ThePostsKidIsTheOneItsLogEntryHolds
      Assert.Contains() Failure: Sub-string not found
      String:    "the post, as the log holds it, does not verify und"···
      Not found: "under kid="alice-2""
[30] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[31] an identifier the log never enrolled is bound whatever the store holds for it
[31] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    22, Skipped:     0, Total:    23, Duration: 96 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys
      System.InvalidOperationException : expected a refusal, got AgentEnrollment { EnrolledAt = 9/26/2026 12:00:00 PM +00:00, OwnerVerified = False, WasAlreadyEnrolled = False }
[31] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     7, Skipped:     0, Total:     8, Duration: 636 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentBindingTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented
      presenting the second stored key of https://agents.example/never-enrolled-5c9df2b2 was answered 201, and the identity's own key then 409
[31] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[32] the client holds the key's proof to its own root, not the signed head's
[32] tests/Curia.Client.Tests RED
    Failed!  - Failed:     3, Passed:    41, Skipped:     0, Total:    44, Duration: 349 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyBindingProvenUnderAnotherRootFails
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AnUnestablishedBindingUnderAnotherRootFailsRatherThanGoingUnchecked(enrolledBeforeR4_34: False)
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AnUnestablishedBindingUnderAnotherRootFailsRatherThanGoingUnchecked(enrolledBeforeR4_34: True)
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
[32] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[33] curia_verify's result drops the key check's line
[33] tests/Curia.Mcp.Tests RED
    Failed!  - Failed:     1, Passed:    14, Skipped:     0, Total:    15, Duration: 240 ms - Curia.Mcp.Tests.dll (net10.0)
  FAILED Curia.Mcp.Tests.PropertyP22ToolResultTests.R6_54_TheVerifyToolReportsTheKeyCheckSeparately
      Assert.Contains() Failure: Sub-string not found
      String:    "subject     WARNING: no read in this session serve"···
      Not found: "key         verified: "
[33] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[34] the renderer strips a P-256 coordinate's leading zeros
[34] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 85 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_28_AP256CoordinateThatBeginsWithZeroIsRenderedAtFullWidth
      Assert.Equal() Failure: Strings differ
      ↓ (pos 67)
      Expected: ···"g-zeros","kty":"EC","x":"AAB-5fiMEJIpXm_c9khweWztu"···
      Actual:   ···"g-zeros","kty":"EC","x":"fuX4jBCSKV5v3PZIcHls7bgpv"···
      ↑ (pos 67)
[34] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[35] the renderer publishes an Ed25519 key in EC's form (errata D4's trap)
[35] tests/Curia.Canon.Sodium.Tests RED
    Failed!  - Failed:     1, Passed:    16, Skipped:     0, Total:    17, Duration: 120 ms - Curia.Canon.Sodium.Tests.dll (net10.0)
  FAILED Curia.Canon.Sodium.Tests.PublicJwkTests.R4_28_AnEd25519KeyIsRenderedAsRfc8037sOctetKeyPair
      Assert.Equal() Failure: Strings differ
      ↓ (pos 56)
      Expected: ···""kid":"rfc8037-a","kty":"OKP","x":"11qYAYKxCrfVS_7"···
      Actual:   ···""kid":"rfc8037-a","kty":"EC","x":"11qYAYKxCrfVS_7T"···
      ↑ (pos 56)
[35] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[36] the enrollment and its binding written in two appends
[36] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:    22, Skipped:     0, Total:    23, Duration: 99 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.EnrollIdentityTests.R4_34_AnEnrollmentAndItsBindingAreOneAppend
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "enrolled; agent.enrolled, agent.key-bound"
      Actual:   "test/append-failed; agent.enrolled"
      ↑ (pos 0)
[36] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[37] the key set renders every key it lists, and throws on one it cannot
[37] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 433 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish
      Assert.Equal() Failure: Strings differ
      ↓ (pos 58)
      Expected: ···"3795321] x=32 y=32; raw: 200 kids=[] x= y=; p384: "···
      Actual:   ···"3795321] x=32 y=32; raw: 500 System.InvalidOperati"···
      ↑ (pos 58)
[37] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[38] the key set matches a stored key to its binding by kid alone
[38] tests/Curia.Application.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 50 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Credentials.LogBoundKeysTests.R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid
      Assert.Equal() Failure: Strings differ
      ↓ (pos 16)
      Expected: "stored=1 bound=[]"
      Actual:   "stored=1 bound=[alice-1@01M3ESC9G04VBQSHG7A3D64S92"···
      ↑ (pos 16)
[38] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     6, Skipped:     0, Total:     7, Duration: 414 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.StoredKeyFormTests.R4_35_AnotherHoldersKeyUnderABoundKidSignsNothingMintsNothingAndIsNotPublished
      another holder's key under stored-victim-b2b2aed9 acted as https://agents.example/stored-victim-b2b2aed9: token request 401, question 401 {"type":"curia/keys/not-bound-by-the-log","title":"The event log binds no such key to that agent","det
[38] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[39] the resolver hands on the log reader's refusal as its own
[39] tests/Curia.Api.Tests RED
    Failed!  - Failed:     3, Passed:     1, Skipped:     0, Total:     4, Duration: 725 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal(path: "question", refuses: "stream", expected: "503 {\"type\":\"curia/log/unreadable\",\"title\":\"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "503 {"type":"curia/log/unreadable","title":"The ev"···
      Actual:   "401 {"type":"test/log-unreadable","title":"The tes"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal(path: "token", refuses: "stream", expected: "500 {\"error\":\"server_error\",\"error_descriptio"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "500 {"error":"server_error","error_description":"T"···
      Actual:   "401 {"error":"invalid_client","error_description":"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal(path: "key set", refuses: "stream", expected: "503 {\"type\":\"curia/log/unreadable\",\"title\":\"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 13)
      Expected: "503 {"type":"curia/log/unreadable","title":"The ev"···
      Actual:   "503 {"type":"test/log-unreadable","title":"The tes"···
      ↑ (pos 13)
[39] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[40] the key set serves its keys without positions when the log cannot be read
[40] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 730 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal(path: "key set", refuses: "whole", expected: "503 {\"type\":\"curia/log/unreadable\",\"title\":\"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "503 {"type":"curia/log/unreadable","title":"The ev"···
      Actual:   "200 {"keys":[{"kty":"EC","crv":"P-256","alg":"ES25"···
      ↑ (pos 0)
[40] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[41] curia-testis holds the key's proof to its own root, not the signed head's
[41] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.13s
  FAILED r6_54_a_binding_proven_under_a_tree_the_head_does_not_cover_fails
      thread 'r6_54_a_binding_proven_under_a_tree_the_head_does_not_cover_fails' (4965967) panicked at tests/log_author.rs:322:6:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
[41] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[42] curia-testis does not compare the binding's kid with the post's
[42] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.11s
  FAILED r6_54_the_authors_enrollment_of_another_kid_fails
      thread 'r6_54_the_authors_enrollment_of_another_kid_fails' (4966704) panicked at tests/log_author.rs:336:5:
      assertion `left == right` failed: the log names kid `another-kid` only in the author's enrollment, which carries no key [curia/acta/key-not-carried]
      left: ("curia/acta/key-not-carried", true)
      right: ("curia/acta/binding-mismatch", false)
[42] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[43] curia-testis does not hold the binding to the author's own stream
[43] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.13s
  FAILED r6_54_a_binding_in_another_identitys_stream_fails
      thread 'r6_54_a_binding_in_another_identitys_stream_fails' (4967466) panicked at tests/log_author.rs:357:34:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
[43] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[44] curia-testis reads an entry of any type as the key's binding
[44] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.14s
  FAILED r6_54_an_entry_of_another_type_fails_though_it_carries_the_key
      thread 'r6_54_an_entry_of_another_type_fails_though_it_carries_the_key' (4968168) panicked at tests/log_author.rs:375:34:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
[44] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[45] log author reports a missing head before it checks anything
[45] cargo log_author RED
    test result: FAILED. 18 passed; 2 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.11s
  FAILED r6_54_log_author_without_a_head_fails_what_is_not_json
      thread 'r6_54_log_author_without_a_head_fails_what_is_not_json' (4968883) panicked at tests/log_author.rs:386:5:
      assertion `left == right` failed: stdout:
      head: not checked
      stderr:
      not checked: every check that needs no head held, and no signed head was given, so neither proof is tied to a root the log's key signed, and the order of the two leaves is the Forum's word. Pass --head and --log-jwks.
      left: (Some(3), false)
      right: (Some(1), true)
  FAILED r6_54_log_author_without_a_head_fails_a_forged_entry
      thread 'r6_54_log_author_without_a_head_fails_a_forged_entry' (4968882) panicked at tests/log_author.rs:416:5:
      assertion `left == right` failed: stdout:
      head: not checked
      stderr:
      not checked: every check that needs no head held, and no signed head was given, so neither proof is tied to a root the log's key signed, and the order of the two leaves is the Forum's word. Pass --head and --log-jwks.
      left: (Some(3), false)
      right: (Some(1), true)
[45] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[46] with no head, curia-testis takes the two proofs from any two trees
[46] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.12s
  FAILED r6_54_without_a_head_proofs_against_two_trees_fail
      thread 'r6_54_without_a_head_proofs_against_two_trees_fail' (4969676) panicked at tests/log_author.rs:441:6:
      called `Result::unwrap_err()` on an `Ok` value: ()
[46] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[47] curia-testis reads the post's author from an envelope ADMIT never saw
[47] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.12s
  FAILED r6_54_an_envelope_admit_refuses_fails_though_its_key_is_bound_after_it
      thread 'r6_54_an_envelope_admit_refuses_fails_though_its_key_is_bound_after_it' (4970397) panicked at tests/log_author.rs:461:5:
      assertion `left == right` failed: the key is bound at leaf 2, which is not before the post at leaf 0 [curia/acta/bound-after-post]
      left: ("curia/acta/bound-after-post", true)
      right: ("curia/acta/malformed", false)
[47] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[48] curia-testis reads the post's kid from a header no parser refused
[48] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.13s
  FAILED r6_54_a_signature_header_naming_a_member_twice_fails_though_its_key_is_bound_after_it
      thread 'r6_54_a_signature_header_naming_a_member_twice_fails_though_its_key_is_bound_after_it' (4971175) panicked at tests/log_author.rs:488:5:
      assertion `left == right` failed: the key is bound at leaf 2, which is not before the post at leaf 0 [curia/acta/bound-after-post]
      left: ("curia/acta/bound-after-post", true)
      right: ("curia/acta/malformed", false)
[48] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[49] the client does not compare the Forum's attribution with the signed author
[49] tests/Curia.Client.Tests RED
    Failed!  - Failed:     3, Passed:    41, Skipped:     0, Total:    44, Duration: 359 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostServedWithNoAuthorFailsAndSaysSo
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostServedAsAnotherAuthorsFailsWhateverTheLogBinds(logBindsTheKeyToMallory: False)
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostServedAsAnotherAuthorsFailsWhateverTheLogBinds(logBindsTheKeyToMallory: True)
      Assert.Contains() Failure: Sub-string not found
      String:    "the entry at leaf 0 ("agent.key-bound") is an entr"···
      Not found: "served this post as written by "https://agents.exa"···
[49] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[50] the client reads an entry of any type as the post's acceptance
[50] tests/Curia.Client.Tests RED
    Failed!  - Failed:     3, Passed:    41, Skipped:     0, Total:    44, Duration: 356 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostsOwnRecordFailsEvenWhereTheKeySetNamesNoLeaf
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AnEntryOfAnotherTypeCarryingThePostsBytesIsNotItsAcceptance
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
  FAILED Curia.Client.Tests.PostVerifierTests.R11_29_AValueTheLogRecordedCannotBeginALineOfTheResult
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
[50] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[51] the client takes the post's kid and signature from the read, not the log
[51] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 359 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_ThePostsKidIsTheOneItsLogEntryHolds
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
[51] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[52] the client holds the key's proof to the head only after its order and type
[52] tests/Curia.Client.Tests RED
    Failed!  - Failed:     2, Passed:    42, Skipped:     0, Total:    44, Duration: 355 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AnUnestablishedBindingUnderAnotherRootFailsRatherThanGoingUnchecked(enrolledBeforeR4_34: False)
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AnUnestablishedBindingUnderAnotherRootFailsRatherThanGoingUnchecked(enrolledBeforeR4_34: True)
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
[52] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[53] the client ignores the index the key's entry route states
[53] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 354 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_TheKeyEntryRoutesOwnStatementsAreComparedNotTaken(member: "log_index")
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   Verified
[53] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[54] the client reports a key entry it could not parse as failed
[54] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 356 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyDocumentThatDoesNotParseIsNotChecked(route: "entry")
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Failed
[54] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[55] curia-testis ignores the leaf hash the entry route states
[55] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.11s
  FAILED r6_54_a_key_entry_route_stating_another_leaf_hash_fails
      thread 'r6_54_a_key_entry_route_stating_another_leaf_hash_fails' (4975022) panicked at tests/log_author.rs:522:6:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
[55] cargo log_outcomes RED
    test result: FAILED. 5 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.08s
  FAILED r6_52_an_entry_route_misstating_its_leaf_or_index_fails
      thread 'r6_52_an_entry_route_misstating_its_leaf_or_index_fails' (4975206) panicked at tests/log_outcomes.rs:226:9:
      assertion `left == right` failed: leaf: not checked: the audit path verifies, and no signed head was given to tie its root to. Pass --head and --log-jwks. A root nobody signed is a root the Forum can have invented.
      left: (3, false)
      right: (1, true)
[55] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[56] curia-testis ignores the index the entry route states
[56] cargo log_author RED
    test result: FAILED. 19 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.13s
  FAILED r6_54_a_key_entry_route_naming_another_index_fails
      thread 'r6_54_a_key_entry_route_naming_another_index_fails' (4975947) panicked at tests/log_author.rs:545:6:
      called `Result::unwrap_err()` on an `Ok` value: VerifiedAuthor { author: "agent://curia.example/tuesdaycrowd/scriptor", kid: "conformance-ed25519-minimal", alg: "EdDSA", key_index: 0, post_index: 2 }
[56] cargo log_outcomes RED
    test result: FAILED. 5 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.09s
  FAILED r6_52_an_entry_route_misstating_its_leaf_or_index_fails
      thread 'r6_52_an_entry_route_misstating_its_leaf_or_index_fails' (4976170) panicked at tests/log_outcomes.rs:226:9:
      assertion `left == right` failed: index: not checked: the audit path verifies, and no signed head was given to tie its root to. Pass --head and --log-jwks. A root nobody signed is a root the Forum can have invented.
      left: (3, false)
      right: (1, true)
[56] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[57] the client reports a key set naming no leaf before it checks the post's own record
[57] tests/Curia.Client.Tests RED
    Failed!  - Failed:     2, Passed:    42, Skipped:     0, Total:    44, Duration: 348 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostServedWithNoAuthorFailsAndSaysSo
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostsOwnRecordFailsEvenWhereTheKeySetNamesNoLeaf
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
[57] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[58] the client reports a key proof off the head as not checked when the key's entry is withheld
[58] tests/Curia.Client.Tests RED
    Failed!  - Failed:     2, Passed:    42, Skipped:     0, Total:    44, Duration: 358 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyProofOffTheHeadFailsThoughItsEntryIsWithheld(withheld: "garbage")
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_AKeyProofOffTheHeadFailsThoughItsEntryIsWithheld(withheld: "unavailable")
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
[58] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[59] the client reports the post's proof off the head as not checked when the post's entry is withheld
[59] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 350 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostProofOffTheHeadFailsThoughThePostsEntryIsWithheld
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
[59] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[60] the key line reports the post's entry as not in hand where the inclusion line failed
[60] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 357 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_54_APostProofOffTheHeadFailsThoughThePostsEntryIsWithheld
      Assert.Equal() Failure: Values differ
      Expected: Failed
      Actual:   CouldNotCheck
[60] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[61] the helper that quotes what curia_verify did not write leaves a control character raw
[61] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    43, Skipped:     0, Total:    44, Duration: 352 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R11_29_AValueTheLogRecordedCannotBeginALineOfTheResult
      Assert.StartsWith() Failure: String start does not match
      String:         "key         verified: FORGED LINE"
      Expected start: "key         FAILED: "
[61] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
runner exit: 0
```

**The final wave's run.** After the final review's fixes (errata G16's R6.55 and R4.36, register
D30), the final wave ran every case on bb19cad, with the workspace's `global.json` (SDK 10.0.401),
using Task 9's runner extracted from the plan as committed and given no case ids: sixty-five cases
in eighty-two suite runs, each red by its own failure line, every restore clean by both proofs, and
`runner exit: 0`. `curia-testis` was then rebuilt from the restored `acta.rs`, and Step 3's gates
ran unpatched and green: the Release build with `--no-incremental`, 0 warnings; eleven `Passed!`
lines and no `Failed!` (Primitives 39, AuthN 68, Sodium 32, Architecture 30, Application 299,
Domain 609, Mcp 74, Client 230, Infrastructure 106, Canon 262, Api 237); the Debug build, 0
warnings, and the architecture rules in Debug, 30; `cargo fmt --check`, `clippy -D warnings`, and
`cargo test`, 233 in 18 binaries; the differential, 0 divergence classes over 22,520 lines; and
`check-spec` clean, with its falsifier red on 4 of 4. Compared mechanically, case by case, the set
of `FAILED` names each of cases 1 to 61 printed is the set the run above printed for it in 60 of
the 61, and no name the run above printed is missing from the new run. The one difference is case
46's: deleting the whole check that, with no head, holds the two proofs to one tree also turns red
`r6_54_without_a_head_proofs_against_two_trees_of_one_size_fail`, the fact the final wave added,
which case 65 falsifies by narrowing that check to the tree sizes. The comparison is of names, not
of lines: the status lines' counts and durations, and the messages' thread ids and line numbers,
which change with the suites, the files and the run, were not compared. Cases 62 to 65, as the run
printed them:

```
[62] VERIFY reads the envelope from the submission as it arrived, not from the form the signature covers
[62] tests/Curia.Application.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 64 ms - Curia.Application.Tests.dll (net10.0)
  FAILED Curia.Application.Tests.Ingest.IngestPipelineTests.R6_55_AnAuthorSentOutsideNfcIsTheAuthorItsSignatureCovers
      curia/content/author-principal-mismatch
  FAILED Curia.Application.Tests.Ingest.IngestPipelineTests.R6_55_AnAuthorWhoseSignedFormIsAnotherIdentifierIsNotThePrincipal
      Assert.False() Failure
      Expected: False
      Actual:   True
[62] tests/Curia.Api.Tests RED
    Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 418 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.SignedAuthorTests.R6_55_AnIdentifierNfcMapsOntoAnothersCannotPostSignedInTheOthersName
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "401 curia/content/author-principal-mismatch; posts"···
      Actual:   "201 ; posts on the board 1"
      ↑ (pos 0)
[62] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[63] the enrollment route asks the kid, not the agent identifier, whether it is in NFC
[63] tests/Curia.Api.Tests RED
    Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 309 ms - Curia.Api.Tests.dll (net10.0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_36_AnAgentIdentifierNfcWouldChangeIsRefusedBeforeAnythingIsWritten(field: "kid", expected: "201 enrolled; key rows 1, events 2")
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "201 enrolled; key rows 1, events 2"
      Actual:   "400 curia/enroll/identifier-not-nfc field=agent_id"···
      ↑ (pos 0)
  FAILED Curia.Api.Tests.EnrollmentIdentifierTests.R4_36_AnAgentIdentifierNfcWouldChangeIsRefusedBeforeAnythingIsWritten(field: "agent_id", expected: "400 curia/enroll/identifier-not-nfc field=agent_id"···)
      Assert.Equal() Failure: Strings differ
      ↓ (pos 0)
      Expected: "400 curia/enroll/identifier-not-nfc field=agent_id"···
      Actual:   "201 enrolled; key rows 1, events 2"
      ↑ (pos 0)
[63] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[64] the client reports an honest post whose own entry cannot be fetched as failed
[64] tests/Curia.Client.Tests RED
    Failed!  - Failed:     1, Passed:    44, Skipped:     0, Total:    45, Duration: 361 ms - Curia.Client.Tests.dll (net10.0)
  FAILED Curia.Client.Tests.PostVerifierTests.R6_52_AnHonestPostWhoseOwnEntryCannotBeFetchedIsNotCheckedNeverFailed
      Assert.Equal() Failure: Values differ
      Expected: CouldNotCheck
      Actual:   Failed
[64] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
[65] with no head, curia-testis compares the two proofs' tree sizes and not their roots
[65] cargo log_author RED
    test result: FAILED. 20 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.11s
  FAILED r6_54_without_a_head_proofs_against_two_trees_of_one_size_fail
      thread 'r6_54_without_a_head_proofs_against_two_trees_of_one_size_fail' (5363048) panicked at tests/log_author.rs:467:6:
      called `Result::unwrap_err()` on an `Ok` value: ()
[65] restore clean (bytes equal to the kept copy: 1/1; git diff --quiet: yes)
```

### D29 — the token endpoint verifies nothing of the DPoP proof a token request carries *(pre-existing; found by the key-binding stage's Task 6 review, 2026-09-27; opened by that stage; closed by the rotation stage's PR 0, 2026-10-12, merge pending; errata G21)*

**Found by the review of R5.21's task, and confirmed by execution.**
`src/Curia.Api/Issuer/TokenEndpoint.cs:111` takes the proof's thumbprint through `DpopThumbprintOf`
(`:189`), which reads the header's `jwk` and nothing else, as its summary says: "read without
verifying the proof" (`:181`). The token is bound to that thumbprint (`:164-166`). No `typ`,
algorithm, signature, `htm`, `htu`, `iat` or `jti` of the proof is checked. The review probed it on
1543986. The stage's Task 10 ran it again on f4c8f75, with the workspace's SDK pin, through the real
Forum over Postgres, each request carrying a valid client assertion. Its lines, the test runner's
indent removed, each body cut at 60 characters by the probe:

```
control (a valid proof): 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IlZtV2lfRFVIMW1
header EdDSA over a P-256 jwk: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IlZtV2lfRFVIMW1
typ not-dpop, htm GET, htu another host, iat 30 days old, 64 zero bytes: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IlZtV2lfRFVIMW1
one proof sent twice: 200, then 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IlZtV2lfRFVIMW1
```

**What it leaves unmet.**
- RFC 9449 §5 asks for a valid proof on a token request, and §4.3's checks are those a server
  receiving a proof makes.
- R5.14: "A `jti` replay cache SHALL be maintained for both client assertions and DPoP proofs". The
  replayed proof above was issued a second token.
- R5.16's skew window, as the 30-day `iat` shows.
- R5.21's DPoP clause, which names no exception. The stage's pin runs in the resource server's proof
  checks (`AccessTokenValidator.cs:143-147`), and nothing runs at the token endpoint for it to join
  (D28).

**Why it is low.** The client assertion still authenticates the agent (R5.20), and a token bound to a
key its caller does not hold is useless to that caller: every resource request verifies its proof,
under R5.21's pin. The endpoint's remarks make that argument (`TokenEndpoint.cs:183-187`). It is a gap against the
text, not an escalation.

**Not fixed here.** R5.21 is not scoped to exclude the token endpoint: once the endpoint verifies its
proof, the requirement holds there without a word changed, and errata G16 says so. The fix routes
the token request's proof through the one proof validator (R5.13): the resource path's proof checks,
with the endpoint's ~~own~~ `htu` *(the one it builds from the request; corrected at the review of
91fe326, D41)* and without `ath` or `cnf.jkt`, answering `invalid_dpop_proof`, with
the replay cache. That is a behaviour change to the token endpoint, with a fact for each row above.
"What comes next" carries it. Since Task A5 (406a5ed), the fuzzer's `perturbed-first` class exercises this
on every closed pass: the 8 POST /oauth/token rows for `jws:proof:header/jwk/x` and `/y`, re-signed and
unsigned, answer 200 for an off-curve key.
*Scheduled 2026-10-11: the rotation stage's PR 0 (errata G21), with M5.*

**Closed by the rotation stage's PR 0** (branch `token-proof-verified`, Tasks 0.1 and 0.3, 2026-10-12;
recorded by Task 0.4; its plan, `docs/superpowers/plans/2026-10-11-keys-that-end.md`, and spec §4.10;
merge pending). The token request's DPoP proof passes the one proof validator
(`src/Curia.AuthN/Dpop/DpopProofValidator.cs`): `Validate` holds the checks `AccessTokenValidator` ran,
in the same order and with the same errors, and `ConsumeAsync` the replay insert. The token endpoint
validates the proof against `POST` and the `htu` it builds from the request (scheme, `Host`, path base
and path; `TokenEndpoint.cs:199-200`). That binds a proof to the URL the caller addressed, not to this
Forum's origin: a caller who sends another `Host` chooses the expected `htu`, and the client
assertion's `aud`, through the same helper (D41). It does so with no `ath`, no `cnf.jkt` binding and
no nonce (R5.19), before it reads the client assertion; it consumes the proof only after the assertion
authenticates, and binds the token to `ValidatedProof.Thumbprint`. `DpopThumbprintOf` and `ProofKey` are
deleted. The resource path calls the same two methods, with its nonce block between them. No word of
R5.13, R5.14, R5.16 or R5.21 changed (errata G21's §5.5, §5.6 row).
- *The facts* (`tests/Curia.Api.Tests/TokenEndpointProofTests.cs`). Each sends a valid client
  assertion with one proof part changed and asserts 400 `invalid_dpop_proof`, exactly one captured
  event 5001 for the request, and its Type equal to the slug of the check the part targets (since M5
  the slug is logged, not served): `D29_AProofWhoseIatIsThirtyDaysOldIsRefused`
  (`curia/authn/proof-window-exceeded`), `D29_AProofWhoseHtuNamesAnotherHostIsRefused`
  (`curia/authn/url-mismatch`), `D29_AProofWhoseHtmIsGetIsRefused` (`curia/authn/method-mismatch`),
  `D29_AProofWhoseTypIsNotDpopJwtIsRefused` (`curia/authn/typ-mismatch`),
  `D29_AProofSignedOverAnotherPayloadIsRefused` (`curia/authn/signature-invalid`),
  `D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused` (`curia/authn/alg-key-mismatch`) and
  `D29_AProofOverAnOffCurveKeyIsRefused` (`curia/authn/malformed-jwk`).
  `D29_AProofSentTwiceIsRefusedTheSecondTime` sends one proof with two client assertions, each its own
  jti: 200, then 400 `invalid_dpop_proof` with `curia/authn/replay`.
  `D29_AProofRefusedForItsAssertionIsNotConsumed`: a proof is consumed only after the assertion
  authenticates. One proof is sent first with an assertion whose signature is 64 zero bytes (401
  `invalid_client`, logged `curia/authn/signature-invalid`), then with a valid assertion (200), so a
  caller with no credential writes no replay row and a request refused for its assertion does not burn
  its proof. It is green on the base and after the fix alike; its information is in F0-14.
  `D29_TheReferenceClientsTokenRequestStillSucceeds`: `ForumSession`'s own token flow answers 200.
- *The five proof-ordering facts* (`tests/Curia.AuthN.Tests/AccessTokenValidatorOrderingTests.cs`,
  0d1a9c6), green on the base before the extraction and after it, each a proof with two faults
  asserting the earlier check's error: `R5_13_AProofsBindingIsCheckedBeforeItsAlgKeyPin`,
  `R5_13_AProofsAlgKeyPinIsCheckedBeforeItsSignature`, `R5_13_AProofsSignatureIsCheckedBeforeItsClaims`,
  `R5_13_AProofsAthIsCheckedBeforeTheNonce` and `R5_13_AProofsNonceIsCheckedBeforeTheReplayInsert`
  (whose `jti` is not consumed).
- *The base's red lines*, run on 0d1a9c6 with the facts written and before any source change, each body cut at 60 characters:
  ```
  D29_AProofOverAnOffCurveKeyIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofWhoseHtuNamesAnotherHostIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofWhoseIatIsThirtyDaysOldIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofSignedOverAnotherPayloadIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofSentTwiceIsRefusedTheSecondTime: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofWhoseHtmIsGetIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  D29_AProofWhoseTypIsNotDpopJwtIsRefused: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6ImtrSkhHWlZxWlc
  R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IjBzMnI3SXd2ZGJ
  R5_14_AProofDatedAheadIsNotAcceptedAgainOnceTheOldRetentionLapses: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6IjBzMnI3SXd2ZGJ
  Failed!  - Failed:    10, Passed:     9, Skipped:     0, Total:    19 - Curia.Api.Tests.dll (net10.0)
  R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew: the proof's jti is held until 2026-08-12T12:00:30.0000000+00:00, not beyond iat + MaxSkew (2026-08-12T12:00:50.0000000+00:00)
  Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1 - Curia.AuthN.Tests.dll (net10.0)
  ```
  `D29_AProofSentTwiceIsRefusedTheSecondTime`'s line is its second send: 200 on the base.
- *The R11.34 fence's red lines* once `TokenEndpoint` reached §4.10's types, verbatim (Release):
  ```
  Curia.AuthN.Dpop.DpopExpectation..ctor(AccessToken): System.String has no row
  Curia.AuthN.Dpop.DpopExpectation..ctor(ExpectedThumbprint): System.String has no row
  Curia.AuthN.Dpop.DpopExpectation..ctor(HttpMethod): System.String has no row
  Curia.AuthN.Dpop.DpopExpectation..ctor(Url): System.String has no row
  Curia.AuthN.Dpop.DpopProofClaims..ctor(Ath): System.String has no row
  Curia.AuthN.Dpop.DpopProofClaims..ctor(Htm): System.String has no row
  Curia.AuthN.Dpop.DpopProofClaims..ctor(Htu): System.String has no row
  Curia.AuthN.Dpop.DpopProofClaims..ctor(Nonce): System.String has no row
  Curia.AuthN.Dpop.DpopProofValidator.Validate(proof): System.String has no row
  Curia.AuthN.Dpop.DpopProofValidator.Validate(verifiers): System.Collections.Generic.IReadOnlyDictionary`2[System.String,Curia.Canon.Jws.IContentVerifier] has no row
  Curia.AuthN.Dpop.ValidatedProof..ctor(Thumbprint): System.String has no row
  row Curia.AuthN.AuthNErrors.*(*) names no string-like parameter in scope (stale)
  row Curia.AuthN.Jwt.CompactJws.Split(compact) names no string-like parameter in scope (stale)
  row Curia.AuthN.Jwt.CompactJwsParts..ctor(HeaderSegment) names no string-like parameter in scope (stale)
  row Curia.AuthN.Jwt.CompactJwsParts..ctor(PayloadSegment) names no string-like parameter in scope (stale)
  row Curia.AuthN.Jwt.CompactJwsParts..ctor(SignatureSegment) names no string-like parameter in scope (stale)
  ```
  The five stale rows were removed and eleven rows added under spec §4.8's rule (the plan's Task 0.1
  ruling, b877e77); none of the eleven parameters is a caller's identifier.
- *The two `RequestSurfaceTests` changes.* Run on the post-D29 tree with `OffCurveProof` still in
  place, six tests went red: the five `inAssertion` rows of
  `R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown`, each
  `400 {"error":"invalid_dpop_proof","error_description":"The DPoP proof was not accepted","detail":"curia/authn/malformed-jwk"}`,
  and `R11_33_NoHeaderARouteCannotReadIsAnsweredAsAServerFault` ("the token endpoint did not issue a
  token bound to a key off the curve (400 …malformed-jwk…)"). Neither was re-pinned to the new status.
  (1) The off-curve proof is refused at the token endpoint (400 `invalid_dpop_proof`), and the token
  bound to the off-curve key, which the resource path's key build still refuses as defence in depth, is
  minted by the test: the issued token's payload with `cnf.jkt` set to the off-curve jwk's RFC 7638
  thumbprint, re-signed ES256 with the issuer key. At least one route's 401 is asserted to carry
  `curia/authn/malformed-jwk`, and a control re-signing with `cnf.jkt` as issued gets a non-401 answer.
  (2) The `inAssertion` rows send an on-curve proof with a fresh jti, so the assertion is read and, for
  the last four rows, its kid check (`Kid.Parse`, before any key is resolved) is reached; the store's
  blank-kid throw is the history they guard against. They keep 401 `invalid_client`. The `detail` the
  base served for each of the five rows, read from one base run and the write removed before commit,
  was `curia/authn/malformed`; since M5 it is their captured Type.
- *`OracleTests`' `sendProof: true` row* went red on the post-D29 tree before its re-pin, which is the
  row's falsification (it sees a detail added):
  `Assert.Equal() Failure: Strings differ` / `Expected: "invalid_dpop_proof"` /
  `Actual:   "invalid_dpop_proof curia/authn/malformed"`. It is re-pinned to that slug, a member of
  `Oracle.ParserRefusals`, and still not past the first parser.
- *Observed, not run as a row.* With the off-curve key-build refusal made unreachable (`if
  (!jwk.ToPublicKeyMaterial(kid: "").TryGetValue(out var proofKey, out var proofKeyError) &&
  expected.HttpMethod == "NEVER")`, which compiles), `D29_AProofOverAnOffCurveKeyIsRefused` went red
  by a 500, an `ArgumentNullException` (parameter `key`) from the signature check given a null key,
  not by a different slug.
- *R5.14's retention, corrected.* The resource path held a proof's jti until `now + MaxSkew`, but
  `Validate` accepts a proof until `iat + MaxSkew`, so a proof dated ahead was acceptable again in
  between. `ConsumeAsync` holds it until max(now, iat) + MaxSkew + 1 s, the second because
  `PostgresReplayCache` readmits a jti whose expiry has come. Pinned by
  `R5_14_AProofDatedAheadIsNotAcceptedAgainOnceTheOldRetentionLapses` and
  `R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain` (`TokenEndpointProofRetentionTests.cs`, a class
  and fixture of their own), and `R5_14_ConsumeHoldsAProofUntilItsIatPlusSkew` and
  `R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew` (`tests/Curia.AuthN.Tests/DpopProofRetentionTests.cs`).
  Their red lines against the verbatim move (`now + AuthNConstants.MaxSkew`), before the expiry line
  was written:
  ```
  R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6Ilcxajg1OXNGUEl
  R5_14_AProofDatedAheadIsNotAcceptedAgainOnceTheOldRetentionLapses: 200 {"access_token":"eyJhbGciOiJFUzI1NiIsImtpZCI6Ilcxajg1OXNGUEl
  Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2 - Curia.Api.Tests.dll (net10.0)
  R5_14_ConsumeHoldsAProofUntilItsIatPlusSkew: the proof's jti is held until 2026-08-12T12:00:30.0000000+00:00, before iat + MaxSkew (2026-08-12T12:00:50.0000000+00:00)
  R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew: the proof's jti is held until 2026-08-12T12:00:30.0000000+00:00, not beyond iat + MaxSkew (2026-08-12T12:00:50.0000000+00:00)
  Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2 - Curia.AuthN.Tests.dll (net10.0)
  ```
- *The random pass's floor* (seed `0000001diyh1`). The runner's output line after D29 (Task 0.1),
  verbatim:
  ```
  seed 0000001diyh1: 1936 sent, 64 unsent; 629 of 1217 JSON and JWS draws past the first parser; 103 of 132 POST /v1/posts envelope draws past ADMIT; answers: 200 x189, 201 x141, 400 x477, 401 x878, 404 x197, 415 x51, 422 x3
  ```
  and after M5's oracle change (Task 0.2), verbatim:
  ```
  seed 0000001diyh1: 1936 sent, 64 unsent; 629 of 1217 JSON and JWS draws past the first parser; 103 of 132 POST /v1/posts envelope draws past ADMIT; answers: 200 x189, 201 x141, 400 x477, 401 x878, 404 x197, 415 x51, 422 x3
  ```
  The two are equal: the pass reads `Oracle.PastFirstParser` alone, which M5's rule for a refusal
  logged with no reason does not change. `Divisor` stays 4 (0.517, half of it 0.258), its comment
  recording 629 of 1,217 (`tests/Curia.Api.Tests/Fuzz/RequestFuzzRandomTests.cs`).
- *Falsified* (Task 0.3; `falsify: 17 cases, 0 not RED` for the PR, each case restored by plain copy,
  rebuilt `--no-incremental` and run once unpatched). D29's rows, the runner's lines with their timings removed:
  ```
  F0-1: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofWhoseHtuNamesAnotherHostIsRefused
  F0-2: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofWhoseIatIsThirtyDaysOldIsRefused
  F0-3: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofSentTwiceIsRefusedTheSecondTime
  F0-4: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofWhoseTypIsNotDpopJwtIsRefused
  F0-5: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofSignedOverAnotherPayloadIsRefused
  F0-8: RED
      Failed Curia.AuthN.Tests.AccessTokenValidatorOrderingTests.R5_13_AProofsBindingIsCheckedBeforeItsAlgKeyPin
  F0-10: RED
      Failed Curia.Api.Tests.RequestSurfaceTests.R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown(inAssertion: True, header: "{\"alg\":\"ES256\",\"typ\":\"JWT\"}")
      Failed Curia.Api.Tests.RequestSurfaceTests.R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown(inAssertion: True, header: "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"\"}")
      Failed Curia.Api.Tests.RequestSurfaceTests.R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown(inAssertion: True, header: "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\" \"}")
      Failed Curia.Api.Tests.RequestSurfaceTests.R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown(inAssertion: True, header: "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":1}")
  F0-11: RED
      Failed Curia.Api.Tests.TokenEndpointProofRetentionTests.R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain
      Failed Curia.Api.Tests.TokenEndpointProofRetentionTests.R5_14_AProofDatedAheadIsNotAcceptedAgainOnceTheOldRetentionLapses
      Failed Curia.AuthN.Tests.DpopProofRetentionTests.R5_14_ConsumeHoldsAProofUntilItsIatPlusSkew
      Failed Curia.AuthN.Tests.DpopProofRetentionTests.R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew
  F0-11b: RED
      Failed Curia.Api.Tests.TokenEndpointProofRetentionTests.R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain
      Failed Curia.AuthN.Tests.DpopProofRetentionTests.R5_14_ConsumeHoldsAProofUntilItsIatPlusSkew
      Failed Curia.AuthN.Tests.DpopProofRetentionTests.R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew
  F0-13: RED
      Failed Curia.AuthN.Tests.DpopProofRetentionTests.R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew
  F0-14: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofRefusedForItsAssertionIsNotConsumed
  F0-15: RED
      Failed Curia.Api.Tests.TokenEndpointProofTests.D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused
  ```
  F0-14 went red on its second send ("the proof was consumed by a request refused before
  authentication: 400 {"error":"invalid_dpop_proof",…}"); F0-15 by a different captured Type,
  `curia/authn/signature-invalid` in place of `curia/authn/alg-key-mismatch`, not by a 500; F0-13's
  mutant compiled, and went red with the base's line ("held until …12:00:30…, not beyond iat + MaxSkew
  (…12:00:50…)"). F0-11b, the bound without its second, is red only on the edge fact and both AuthN
  facts' strict clause, as planned.
- *Observed, not acted on.* `DpopProofClaims` and `ValidatedProof` are public positional records, so
  Parse-only and Validate-only construction is a convention, not a type fence, and the public
  `DpopProofValidator.ConsumeAsync` accepts a `ValidatedProof` that never passed `Validate`. The
  PortStringAllowlist rows for `DpopProofClaims..ctor` (Ath, Htm, Htu, Nonce) justify their raw strings
  by the same convention. Today the only construction sites are the `new DpopProofClaims(` in
  `DpopProofClaims.Parse` and the `new ValidatedProof(` in `DpopProofValidator.Validate`. Fencing them
  (internal constructors, with the allowlist rows and `Curia.Api`'s access adjusted) is a change to the
  R11.34 boundary-types work (D33), not to D29, so PR 0 leaves it and the two summaries now say it is a
  convention.

### D30 — an identifier NFC maps onto another's got a post accepted signed in the other identity's name *(pre-existing; found by the key-binding stage's final review, 2026-09-27; opened and closed by that stage's final wave; errata G16, R6.55, R4.36)*

**Found by the stage's final review, and confirmed by execution.** VERIFY verified and persisted the
canonical form, NFC throughout (`src/Curia.Application/Ingest/IngestPipeline.cs:77` at 58d2b43),
read the envelope from the submission as it arrived (`:81`), compared its `author` with the
principal (`:89`), and resolved the signing key for that `author` (`:103`). The review probed it
through the real Forum over Postgres on 58d2b43, and the final wave's architect ran the same probe
again on a `git archive` of 58d2b43 with the workspace's SDK pin, with the same outcome under another
suffix. The review's lines, each identifier's accented letter written as a JSON escape and each body
cut:

```
victim NFC? True  attacker NFC? False  attacker NFC form == victim: True
victim enroll: 201 {"agent_id":"https://agents.example/caf\u00e9-4b086f55","kid":"victim-4b086f55",…}
attacker enroll: 201 {"agent_id":"https://agents.example/cafe\u0301-4b086f55","kid":"attacker-4b086f55",…}
attacker token: issued
wire carries NFD author: True
post: 201 {"post_id":"01M0572TG0MTVRNNQZM29F67CZ",…}
served provenance author == attacker: True
signed canonical author == victim: True; == attacker: False
```

**What it reached.** The log held a `post.accepted` whose signed author was the victim, under a key
the log binds only to the look-alike: D28's concern, reached through a seam rather than through the
key store. The Forum served the post as the look-alike's. R6.54's readers fail it, the reference
client on the attribution and `curia-testis log author` with `curia/acta/binding-mismatch`; but
`curia read`, `curia thread`, `curia_read` and `curia_search` print the look-alike as its author,
"verified locally" under the look-alike's key set, and the envelope a reader's model receives names
the victim. The observation this entry answers ("A non-NFC identifier has not been exercised",
below) said a reader would fail such a post, and never that the NFC form could be another enrolled
identity.

**Why nothing caught it.** No test sent an author outside NFC: `ForumAgent`, `IngestPipelineTests`
and the reference client all render their submissions with `CanonicalizeWithNfc`, so the author as
sent and the author as signed were one string wherever a test could look. The remarks on
`PostEnvelope` and `VerifiedSubmission` said the envelope VERIFY hands on is a reading of the
canonical bytes; the code read it from the arrival. Trap 22's shape, found again.

**Closed** by errata G16's R6.55 and R4.36:
- **R6.55.** VERIFY parses the envelope from the canonical bytes it verifies
  (`IngestPipeline.cs:87-91`), so the `author` it compares with the principal (`:110`) and resolves
  the key for (`:131`) is the one the signature covers, and so is every member the phases after it
  read. An identifier NFC would change never equals the author of anything it signs. A principal
  whose identifier is in NFC may send its own `author` outside NFC and is accepted, as every other
  member already was (R6.10). Held by
  `IngestPipelineTests.R6_55_AnAuthorWhoseSignedFormIsAnotherIdentifierIsNotThePrincipal`,
  `IngestPipelineTests.R6_55_AnAuthorSentOutsideNfcIsTheAuthorItsSignatureCovers`, and, through the
  Forum with the look-alike enrolled as the route enrolled it before R4.36,
  `SignedAuthorTests.R6_55_AnIdentifierNfcMapsOntoAnothersCannotPostSignedInTheOthersName`.
- **R4.36.** The enrollment route refuses an `agent_id` outside NFC, 400
  `curia/enroll/identifier-not-nfc`, naming the field and never echoing the value, before anything
  is read or written (`src/Curia.Api/ForumEndpoints.cs:429`). A `kid` is not asked: it travels in a
  protected header signed as its bytes, and is never canonicalized. Held by
  `EnrollmentIdentifierTests.R4_36_AnAgentIdentifierNfcWouldChangeIsRefusedBeforeAnythingIsWritten`,
  one row for each field.

The same probe on the fixed tree: the look-alike's enrollment answers
`400 {"type":"curia/enroll/identifier-not-nfc",…}`, and its token request then fails, since nothing
was enrolled. Enrolled past the route, as a Forum that enrolled it before R4.36 still holds it, it
obtains its token, and its post answers `401 {"type":"curia/content/author-principal-mismatch",…}`.

**Falsified:** case 62 reads the envelope from the arrival, and the two application facts and the
HTTP fact go red; case 63 asks the `kid` in place of the agent identifier, and both rows of the
enrollment fact go red. D28 quotes their lines from the final wave's run.

**Other places an identifier is compared, and why this does not reach them.** The attack needs one
side of a comparison canonicalized. Only ingest canonicalizes with NFC and binds the result to an
identity. The event store's NFC decides admission only, and its bytes are discarded
(`src/Curia.Infrastructure/PostgresEventStore.cs:373-375`); leaves, heads, flag commitments and the
Acta's key set are pure RFC 8785 (`src/Curia.Domain/Acta/LogLeaf.cs:75`, `:141`;
`src/Curia.Domain/Moderation/FlagCommitment.cs:49`; `src/Curia.Api/ActaEndpoints.cs:257`). The token
endpoint resolves the assertion's key by `client_id` and `kid`, compares `iss`, `sub` and `client_id`
ordinally (`src/Curia.AuthN/ClientAssertionValidator.cs:77`, `:97`, `:102`), and mints `sub` as it
came (`src/Curia.Api/Issuer/TokenIssuer.cs:105`). A flag records the subject its token names
(`ForumEndpoints.cs:826`); accepting an answer compares the thread root's recorded author with the
subject (`:1411`); `attest-owner` and the key set read the stream of the identifier named, exactly
(`src/Curia.Application/Credentials/AttestOwner.cs:85-110`, `ForumEndpoints.cs:1300`). Each compares
one string with itself.

**What it does not close.** An identity enrolled outside NFC before R4.36 keeps its rows (R4.19,
R4.32) and its tokens; it may flag and read, authors nothing, and cannot re-announce its key. No
deployment is hosted. An identifier that only looks like another, a letter from another script for
instance, is a different string under NFC and is D4's. `curia-operator attest-owner` names an
identity by the string an operator types, so two identities that render alike are told apart only
by their bytes. R4.36 keeps new pairs that NFC maps onto one identifier from being enrolled;
look-alikes NFC leaves distinct, compatibility forms and another script's letters among them, still
enroll (D4).

Nor does it rewrite a post accepted before R6.55. PERSIST recorded beside the canonical bytes the
`author` VERIFY had matched against the submission as it arrived, and the `board` and `parent` read
from that arrival (`IngestPipeline.cs:171-174` at 58d2b43); since R6.55 all three are the signed
ones (`:199-202`). The log is append-only, so such an event keeps them, and replay reproduces them:
`PostProjector` serves the recorded fields
(`src/Curia.Application/Projections/PostProjection.cs:160-164`) to every read that folds the log,
the provenance's `author` (`ForumEndpoints.cs:1982`), search's results (`:1585`, `:1618-1621`) and
accepting an answer (`:1407-1411`) among them. Search's index alone re-derives each post from its
canonical bytes (`src/Curia.Application/Projections/SearchProjection.cs:115`) and matches on the
author that form names (`:136`). In a log holding the probe's post, every view of the post, search
results included, names the look-alike, while search matches it under the victim's name (an
`author=` query, `src/Curia.Domain/Search/LexicalSearch.cs:293`), and the look-alike may accept
answers on a thread whose root's signature names the victim. R6.54's readers fail the author half:
the reference client compares the author the Forum served with the one the signed envelope names
(`src/Curia.Client/ActaCheck.cs:316`), and `curia-testis log author` fails the key's binding as
another identity's, `curia/acta/binding-mismatch` (`rust/curia-testis/src/acta.rs:517-518`). No
deployment is hosted, so no such event exists outside a test's throwaway database.

R6.55 is held where VERIFY reads the envelope, not by a type. `AdmittedSubmission` is a public
positional record, so the tree as it arrived is public as its `Document`
(`src/Curia.Application/Ingest/IngestPhases.cs:29`), and the submit route keeps the admitted
submission in scope after VERIFY (`src/Curia.Api/ForumEndpoints.cs:622-632`). A later edit that
reads a member from `Document.Root` rather than from the verified envelope reopens this seam for
that member, compiles, and leaves every fact green: case 62 fences the one argument at
`IngestPipeline.cs:91`. Nothing in `src/` reads the arrival tree after VERIFY today; its one reader
is the canonicalizer's input (`:77`). A fence of CS-15's shape would catch it: a compile error,
with `Document` visible only inside `Curia.Application`, whose `InternalsVisibleTo` names
`Curia.Application.Tests` alone; or a red architecture fact, that nothing outside `IngestPipeline`
reads it. The review of the final wave's first dispatch raised it (Mi1); it is recorded, not ruled.

### D31 — a stranger's words printed as the reader's own *(opened by `curia-architect` on 2026-09-27 and closed by the strangers stage; errata G17)*

**Found by running two lines this register recorded and had not run.** "Observed during the
key-binding stage" listed the places the reference client and `curia-mcp` print a value as it was
served, and said "a value holding a newline begins a line at each (traced, not run)"; D4 recorded that
the enrollment route enrolls an `agent_id` or a `kid` holding U+000A. `curia-architect` ran both on
2026-09-27, on a `git archive` of b4bfe31 with the workspace's SDK pin, through the real Forum over
Postgres, reading each post back with `Passage.Render`, which `curia read`, `curia thread`,
`curia board`, `curia_read` and `curia_search` all print. Errata G17 quotes both outputs. The worse of the two needed no enrollment trick: a post's `board` is any non-empty string
(`src/Curia.Domain/Content/PostEnvelope.cs:100`), and an ordinary T0 agent's question whose board held
a line break printed a `SYSTEM:` line between the post's kind and its author, in every reader.

**What it reached.** `Passage.Render` printed the post's id, kind, board, parent, author, owner and
`server_ts` as they came (`src/Curia.Client/Passage.cs:56-62` at b4bfe31), and
`SignatureVerdict.Describe` the signature's `kid` (`src/Curia.Client/SignatureCheck.cs:63` at b4bfe31). The
lines sit above the standing warning and outside the span, where a reader's model is told the client
speaks. `curia-testis` printed `author:` and `kid:` as they came
(`rust/curia-testis/src/bin/curia-testis.rs:324-325`, `:507-508`, at b4bfe31); `curia verify` joined its lines
with spaces, so only a direct run of the verifier began a line. And `Check.Quote`, which G16 gave
`curia_verify`, walked UTF-16 code units, so a tag character from U+E0000's block, category Cf and a
valid surrogate pair, passed through unescaped: invisible text a model reads. Run on b4bfe31's
`Check.Quote`, `a`, U+E0041, U+E0042, `b` came back as `0022 0061 DB40 DC41 DB40 DC42 0062 0022`.

**Why nothing caught it.** Every test of R10.22's data-position wrapping held the span to its
delimiters, and no fixture served a value outside the span that an ordinary Forum would not. G16 found
the shape in `curia_verify` and quoted there, value by value, and listed the rest as found; a list is
what a sweep finds, not a rule that finds the next site. Trap 23.

**Closed** by errata G17's R10.63, R10.64, R10.67 and R4.37:
- **R10.64.** `Curia.Canon.Json.DisplayLiteral.Of` (`src/Curia.Canon/Json/DisplayLiteral.cs:36`) and
  `curia_testis::display::literal` (`rust/curia-testis/src/display.rs:20`): a JSON string literal in
  which printable ASCII stands for itself and every other UTF-16 code unit is a `\u` escape. Sixteen
  vectors in `conformance/display/`, written by script from code points, run by both runners and
  counted in the index; `printable-ascii` holds every printable character but the quote and the
  backslash, which the Task 2 review found it did not (21 of 95: a reader that escaped `<` or `$`
  passed both runners). A Rust fact walks every scalar value, as the C# property walks generated
  strings. `Check.Quote` is now `DisplayLiteral.Of`.
- **R10.63, the client.** `FrameText` (`src/Curia.Client/Frame.cs:117`), an interpolated-string handler
  whose `string` and character holes are literals and whose other holes compile only if they format
  themselves; `OwnText` (`:21`) for the client's own words; `FrameBuilder` (`:235`), which takes a line
  only as a `FrameText`, a constant, a passage or a span, and writes a span raw only once
  `IsDelimitedSpan` (`:323`) has checked its delimiters. `Passage.Render` (`src/Curia.Client/Passage.cs:57`), `Reading`,
  `SignatureVerdict.Describe` (`SignatureCheck.cs:66`) and `Refusal.Summary`
  (`src/Curia.Client/ForumResult.cs:82`) are built on them. The standing warning is written as the
  client's own only when it is the published text (`Passage.cs:138`), and the marking caveat is the one
  the client holds for the marking served. `ConstantArgumentTests` fails on a delegate over a method
  whose parameter must be a constant, the one way past CA1857, and on an `OwnText` made from a
  parameter that is not one.
- **R10.63, the CLI.** `Output` (`src/Curia.Client.Cli/Cli.cs:290`) takes a line only as a constant
  (`[ConstantExpected]`, so a variable is CA1857, a build error), a `FrameText`, a `FrameBuilder` or a
  `Reading`. When it changed, the compiler named the CLI's sites itself. `OutputFenceTests` holds the
  fence: no other CLI type touches `System.Console`, and no line's `string` parameter loses the
  attribute.
- **R10.63, the adapter.** `ForumTools` and `WriteTools` compose every result and refusal through
  `FrameBuilder`, except `curia_verify`, whose result is `PostVerification.Render`: lines the client
  composes over `Check` details, in which every value the client did not compose goes through
  `Check.Quote`. That covers a served value, and also the origin of the caller's own `--forum` or
  `CURIA_FORUM`, and its `CURIA_CLIENT_HOME`, which `ConsistencyAsync` had echoed raw until the
  stage's final gate (`src/Curia.Client/PostVerifier.cs`, falsification cases 93 and 94).
  The authority `ConsistencyAsync` names, and the key `HeadStore.OriginKey` retains a head under,
  kept the Forum URL's userinfo, so a `--forum` or `CURIA_FORUM` password was printed into
  `curia verify`'s and `curia_verify`'s consistency line, and one Forum reached with two credentials kept
  two heads, against the comment that said it could not. Both now read `HeadStore.Origin`, which
  drops it; the CLI's fallback Reader Contract, used when a Forum serves a `reader_contract` that
  is not an absolute URI, had been built from the configured URL and printed its userinfo in
  `curia read`, `thread`, `board` and `search` until the final gate's third round; it is
  `ReaderContractLocation.For` now (`src/Curia.Client/ReaderContractLocation.cs:22`), and the
  transport refusals, `ForumWriter`'s refusal and a scan for the two expressions that keep userinfo
  are held by facts, where before only `HeadStoreTests` and `PostVerifierTests` were (falsification
  case 108); an origin without userinfo keys exactly as before; one with userinfo does not: a head
  retained under it before the second round is never looked up again, `ConsistencyAsync` reports
  that no earlier head was retained, which is false, and anchors anew, skipping one consistency check
  against the old head, and the old directory, whose readable name carries the credential, stays on
  disk (see "Observed during the strangers stage") (`HeadStoreTests`,
  `PostVerifierTests.R6_53_AConsistencyDetailNamesTheForumWithoutItsUserinfo`; falsification case
  103).
  `curia verify` prints the
  same details through `OwnText`; a receipt had printed the board an answer copies from its question, and a write
  refusal the Forum's title and detail, as they came. A passage's resource URI carries the post id
  percent-encoded (`src/Curia.Mcp/ForumTools.cs:285`), and the gate reads each URI: it had carried the
  id as served, where no gate looked (the Task 1 review's M4).
- **R10.63, the verifier.** `curia-testis` prints `author`, `kid` and `alg`, a head's `kid`, `alg` and
  timestamp, and every value a refusal names, a member's name among them, as literals; serde_json's
  own words, which quote a document in Rust's debug form, it does not write at all (Task 3's review,
  I1); and so the arguments, paths and platform reasons its usage refusals name
  (`rust/curia-testis/src/bin/curia-testis.rs:515`), which it had echoed as given.
- **R10.65.** A command the CLI prints for its reader to run -- the entity-tag re-check, the next
  page's cursor, a thread to read -- is written in one place, `Hints`
  (`src/Curia.Client.Cli/Hints.cs:18`), and holds a value only as a `ShellWord`
  (`src/Curia.Client/Frame.cs:46`, `TryOf` at `:60`): single-quoted, printable ASCII other than `'`,
  `\` and `!`, not beginning with `-`. At b4bfe31 the hints printed the entity tag between single quotes
  as it came and a cursor and a post id bare, so a `'` or a `;` in one ended the word; the plan's
  first form printed them as display literals, which a shell reads as double-quoted words and in which
  it runs `$(…)`. A hostile value's command ran under both, in sh, dash, bash, zsh and fish. `!` is
  refused because csh and tcsh expand it as history even between single quotation marks; the design
  probe ran the word's alphabet through all seven shells, and the committed facts run `/bin/sh`. A
  value that is not a word leaves the command unprinted.
- **R10.66.** `DisplayLiteral.TryRead` (`DisplayLiteral.cs:70`) reads a literal back only when it is
  exactly the one `Of` writes and spells no surrogate without its pair, which a URL's encoding would
  have sent as U+FFFD; and the CLI's `Args` (`src/Curia.Client.Cli/Cli.cs:94`) reads a
  command's arguments, `--board`, `--author`, `--parent` and each tag and ref through it, so a board
  printed as escapes can be passed back as printed; one that begins with a quotation mark and is not
  a literal is refused before anything is sent (`Program.cs:37`).
- **R10.67.** The span was the one thing a reader wrote unquoted that it did not compose, and R10.63
  let it through once its delimiters were checked; a terminal does not read delimiters.
  `curia-architect`, scoping a terminal reader on 2026-10-05, ran it through the real Forum on a
  `git archive` of 3b145fc: a question whose body held sixteen control, format and separator
  characters was accepted, and `curia read`'s renderer, `curia_read` and `curia_search` each wrote
  thirteen of them as they came -- U+007F, the C1 controls U+0085, U+009B, U+009C and U+009D, U+00AD,
  U+200B, U+200D, U+202E, U+2028, U+2029, U+FEFF and the tag character U+E0041. ESC, CR and BEL did
  not reach them only because the span holds the canonical form, which RFC 8785 escapes below
  U+0020; a Forum that serves a span holding them inside valid delimiters was written as it came
  (`src/Curia.Client/Frame.cs:298` at 3b145fc, which also turned CR, U+0085 and U+2028 into new
  indented lines on the duplicate refusal's path). `Curia.Canon.Json.SpanText`
  (`src/Curia.Canon/Json/SpanText.cs:31`) writes every character of general category Cc but
  U+000A and U+0009, Cf, Zl or Zp, and every surrogate without its pair, as `\u` and four lowercase
  hex digits per code unit, walking scalar values; `FrameBuilder.Span` (`Frame.cs:303`) checks the
  delimiters on the span as served and writes it through `SpanText.Block`, in `curia read`,
  `curia thread`, `curia board`, the duplicate refusal's answers, `curia_read`, `curia_search` and
  `curia_ask`. `curia-operator`'s `TerminalText` is `SpanText` now, so its uppercase escapes over C0,
  C1, DEL and the bidi controls became R4.37's set in R10.64's form. `curia-testis` prints no content
  and is not reached. Red first, on Task 9b's facts over 3b145fc's readers (run again for this entry on a `git archive`
  of 0a2d5b3 with `SpanText`, its facts, and Task 9b's changes to `Frame.cs`, `TerminalText.cs` and
  `RedTeamCorpusTests.cs` taken out): `Curia.Api.Tests` `Failed: 2`, `curia read (Datamark) wrote
  U+009B as itself (R10.67)` and the operator listing's `Not found: "\\u001b"`;
  `Curia.Client.Tests` `Failed: 2`, `the passage wrote U+001B as itself (R10.67)` and an indented span
  that printed a line feed and an indent where `a\u000dsignature verified` was expected;
  `Curia.Mcp.Tests` `Failed: 3`, `curia_ask`, `curia_read` and `curia_search` each `wrote U+001B as
  itself`; and `Curia.Domain.Tests` `Failed: 1`, `R10_57_EveryDeclaredOutcomeKindHasAnEvaluator`
  naming the twelve `escaped-by-reader` entries.
- **R4.37.** The enrollment route refuses an identifier or a `kid` holding a character of general
  category Cc, Cf, Zl or Zp, walked by scalar value, 400 `curia/enroll/identifier-control-character`,
  naming the field, the code point and its category (`src/Curia.Api/ForumEndpoints.cs:436`, `:545`).

Held by `Curia.Client.Tests.ReaderFrameTests` (a served post whose every string member is hostile,
built by reflection), `Curia.Mcp.Tests.ReaderFrameToolTests` (every registered tool, each served member
hostile in turn, its resource URIs read with its text, and every refusal kind the client classifies, the 403 typed outside curia/ among them), `OutputFenceTests`,
`CommandHintTests` (every word run through `/bin/sh`), `ArgsTests`' R10.66 facts,
`DisplayLiteralTests`, the Rust `display` vectors and `display.rs`' walk of every scalar value,
`display_output.rs` (the refusals, and the binary run with a hostile argument),
`EnrollmentIdentifierTests.R4_37_…`, `SpanTextTests` (twenty-two characters of the set with
hand-written escapes, thirteen kept, and a property that reads every escape back), both `R10_67_` facts
of `Curia.Client.Tests.ReaderFrameTests` and `ReaderFrameToolTests.R10_67_AHostileSpanReachesNoToolResultWithAControlAsItself`
(a span a hostile Forum serves, ESC and CR in it), `RedTeamCorpusTests.R10_67_ReaderPayloadsReachNoReaderAsThemselves`
over twelve `escaped-by-reader` payloads, `OperatorModerationTests.R10_62_TheListingMarksTheRationaleAndEscapesControlCharacters`,
and, through the real Forum, `Curia.Api.Tests.ReaderFrameTests`, which reads a hostile board and a
hostile identifier through `curia read`'s renderer, `curia_read`, `curia_search`, `curia_verify` and
`curia-testis verify`, and in `R10_67_ABodyWrittenToDriveATerminalReachesNoReaderAsItself` a body
written to drive a terminal through the first three.

**Falsified:** the strangers stage's Task 10 holds ninety-six cases in one hundred and twenty-four suite
runs, its review rounds' cases included. Task 11 ran the first eighty-nine in one run of its runner
from the repository root, on 1045f08's source: every one of the 116 commands printed `RED`, every restore
printed `restore clean` with both proofs and, after each of the eleven cases that patch a file under
`rust/` (3, 4, 18-20, 31, 42, 45, 48, 70 and 71), `curia-testis rebuilt: yes`, and the run's last
lines were `runner exit: 0` and `falsify.py exit 0`. Case 90 came with Task 11's review, which ran it
alone: `RED`, `restore clean` with both proofs, and `runner exit: 0`. Task 11's fix review
retargeted case 90 at `CompactJws`'s object check and added cases 91 and 92, and ran the three with
its changes uncommitted: `RED` on every command, each restore byte-identical to its kept copy, and
`runner exit: 0`. How each round first ran the cases it added is in Task 10's narrative. The
stage's final gate added cases 93-96 and ran the four from the repository root with its two code
files staged in the index: `RED` on every command (93 and 94 at `Failed: 1, Passed: 1` in Client,
naming the first-read and the unreadable-head facts in turn; 95 at `Failed: 5, Passed: 4` and 96 at
`Failed: 3, Passed: 6` in Api), `restore clean` with both proofs, and `runner exit: 0`.

**What it does not close.** A reference reader quotes; a third-party reader that prints served values
raw is as exposed as the reference client was, which is what R4.37 narrows for identifiers and nothing
narrows for a `board`, `parent` or tag. An identity enrolled before R4.37 keeps its rows, and cannot re-announce its enrollment or have a lost key row registered again. An `OwnText`
wrapped around a served value is the defect the compiler cannot see; the plan's Task 5 lists every one
the library, the CLI and the adapter hold, and each is the client's own words. `curia-operator` escapes
what it reads from the database, through `SpanText`, and does not quote it. R10.64's decoding of bytes -- another program's
output, a path -- is each reader's own rather than the platform's. `ProgramOutput`
(`src/Curia.Client/ProgramOutput.cs`) reads a child's stdout and stderr as raw bytes and decodes them
as UTF-8 with U+FFFD for each maximal ill-formed subpart, because the redirected `StreamReader` it
replaced detected a byte order mark, and on macOS read a stream beginning FF FE as UTF-16LE.
`curia-testis` uses Rust's lossy conversion, which substitutes the same way. Each side is pinned:
`ProgramOutputTests` feeds ill-formed rows (`FFFE4100`, `C328`, `F09F98`) through `Decode` and
through a real child process, and `rust/curia-testis/tests/display_output.rs` runs the binary with a
non-UTF-8 argument. No fact feeds the same ill-formed bytes to both readers, so that they agree is
still shown only by the run this paragraph used to rest on. R10.67's set is the runtime's
Unicode tables', so a code point a later runtime places in Cf is escaped from that upgrade on; the
enrollment route keeps its own walk of the same four categories (`ForumEndpoints.cs`,
`ControlCharacter`) rather than calling `SpanText`; which terminals act on which C1 control was not
run; and the span a reader prints is still not compared with the canonical form it verified (below).

### D32 — screening is quadratic in the length of what it screens, and a flag's rationale has no cap *(opened by the strangers stage's final gate, 2026-10-06; closed by the one-part-at-a-time stage's PR A; errata G18, R10.68 and R10.69)*

**Found by the stage's final review.** `RaiseFlag.RecordAsync` (`src/Curia.Application/Moderation/RaiseFlag.cs:97`) runs `ContentScreener.ScreenText` over the whole rationale before it checks that the post exists, and nothing caps the rationale: a flag is not a signed envelope, so R6.39's caps never apply, and Kestrel's 30 MB body limit is the only bound. `ScreenText` grows with the square of its input (timed in Release on `r` repeated N times: 25k 0.23 s, 100k 1.15 s, 200k 4.57 s, 400k 18.56 s). Over HTTP, an enrolled agent's flag against a post that does not exist took 106.7 s at N = 1,000,000 before it answered 404, and ran past a 100 s client timeout at 3,000,000. Enrollment costs nothing, so this is anyone's. The errata's argument that decode work is 'already bounded at 1 MiB' (around line 2294) holds for an admitted submission and not for this path. `ApplyModeration.cs:93` makes the same call on the operator's out-of-band path. Reproduced independently by the final gate's third round (Release, timing `ContentScreener.ScreenText` directly: 100k 1.09 s, 200k 5.03 s, 400k 19.50 s on `r` repeated; varied words of the same lengths 0.01–0.08 s), which also confirmed that nothing in `FlagRequest` or `src/Curia.Api` bounds the body below Kestrel's default. Since that round a post id of white space alone is refused before screening (D25's fifteenth); the cost to any other id is unchanged.

**Probably wider, and not measured.** `ScreenEnvelope` runs the same detectors over every canonical string, so a T0 post carrying one string at R6.39's 256 KiB cap is likely to cost several seconds of CPU at SCREEN. The probe that would carry information: time `IngestPipeline` on a submission whose body is one 256 KiB string of a single repeated character, and on one of 256 KiB of ordinary prose.

**Measured wider** (`curia-architect`, 2026-10-06; a Release build of the tree at 0059010, whose `src/`
is `main`'s at 9c3dcb1, on an Apple M3 Max). `ContentScreener.ScreenEnvelope` over an envelope with one
string member of `"ai"` followed by spaces took 0.18 s at 4 KiB, 0.70 s at 8 KiB, 2.74 s at 16 KiB,
10.76 s at 32 KiB and 43.00 s at 64 KiB: quadratic, and some hundred times the flag path's cost on `r`
at the same length. At R6.39's 256 KiB cap that is about eleven minutes of one core per string
(extrapolated, not run), and a submission can carry several. The rule is
`InjectionDetector.SecondPersonImperative` (`src/Curia.Domain/Screening/InjectionDetector.cs:106`),
whose `\s*[,:]?\s*` gives a backtracking engine two loops over one class. Timed alone at 64 KiB on the
same copy of each pattern, `SecretScanner`'s connection-string URI rule is quadratic on `r` repeated,
the credential-URL rule on `http://` repeated and the HTML-comment rule on `<!--` repeated; the rest
were under 20 ms. Under `RegexOptions.NonBacktracking` every rule but the high-entropy assignment, whose
keyword is a lookbehind that engine refuses, finished each input under 20 ms, and the second-person
rule 256 KiB in 26 ms with the matches of the backtracking engine on a sample; `[GeneratedRegex]` takes
the option and builds clean under `AnalysisLevel latest-all` with warnings as errors (scratch project,
not this tree's analyzers). Not run: the HTTP path, and an `IngestPipeline` timing. The probe that
settles the post path end to end: one T0 question whose body is `"ai"` and 65,534 spaces, timed from
send to answer, expecting about 43 s.

**Beside it, not a cost of time.** The same run screened one 256 KiB string of U+200B and returned 87,381
hidden-text flags, every one of which `PersistAsync` writes into the post's `risk_flags`
(`src/Curia.Application/Ingest/IngestPipeline.cs:248`), so the log entry carries some 85 bytes per flag,
about 7 MB for that one string (computed, not measured as stored). Every read folds it, and every leaf
hash covers it. Not a 5xx, so R14.10's fuzzer cannot find it. The probe: post that body as a T0 agent
and read `pg_column_size(payload)` of its `post.accepted` event. Whether flags of one category over a
contiguous run coalesce into one, which changes what an annotation means and moves a detector version,
is a decision for the next errata pass (G18, "What this deliberately does not change").
*The serving half (the five-agent exercise, 2026-10-06, its audit's finding 6).* The projection keeps
each stored flag's category and drops its offset, length and detector
(`src/Curia.Application/Projections/PostProjection.cs:170`–`:178`), and every read serves the list as
one category string per flag (`src/Curia.Api/ForumEndpoints.cs:1999`): the exercise's post
`01M48BRMQR4SC39SG60XEDSN94` served `"HiddenText"` four times, so the post above would serve it
87,381 times on every read. Categories alone are R10.32's and R10.50's shape and the white paper's own
example, and are not a defect; the repetition is this amplification reaching the wire. The coalescing
decision settles what is stored and what is served together, and D35's new hidden characters move the
same annotation.

**Not fixed here.** Two parts. (1) Make the screener's cost linear, or bounded per string, with the red-team corpus (`conformance/red-team/`) as the regression set and a timing fact that fails above a stated budget at R6.39's cap. (2) A published cap on a flag rationale (and on a moderation rationale), checked before screening, which wants an errata entry because it is a new normative number. 'What comes next' carries both.

**Why it did not hold the strangers stage's merge** (`curia-architect`'s ruling, 2026-10-06). The
cost is on `origin/main` already (`RaiseFlag.cs:91` there), so merging the stage neither opens it nor
widens it, and holding the merge would not close it. No Forum is hosted. A cap alone would close the
flag path and leave the post path, which is the same detector on the same input. And the fix is
coupled to D33's: the request fuzzer's per-request time budget is the gate that would have found this
cost, and it cannot go green until the screener is linear. So D32 is the first task of D33's stage.
A cap is still not chosen in this register: it is a new normative number, and it goes in that stage's
errata entry.

**A3's stop fired on CI, and R10.69 holds** (`curia-architect`'s ruling, 2026-10-06, on 95b38b8 and CI
run 37566876224). CI's screen-timings artifact (X64, 4 processors) read 1,508 ms for zero-width-space
ScreenEnvelope and 1,499 ms for tab ScreenEnvelope against the 2,000 ms budget, past A3 Step 5's 500 ms
stop. A scratch console against the Release `Curia.Domain.dll` at 95b38b8 (Apple M3 Max, not the CI
runner) timed every slow row from 32 KiB to 512 KiB: each doubles at about 2.0, zero-width-space at an
exponent of about 1.03 over a sixteenfold range, so no row is superlinear and R10.69 holds. Three
constants, none a pattern, make up CI's number: the first screening call in a process builds the fifteen
non-backtracking automata at run time (`[GeneratedRegex]` emits no code for that engine) and JITs the
path, about 100 ms locally, charged to whichever row runs first (tab, on CI); the solution's eleven test
assemblies run at once on CI's four cores; and zero-width-space at the cap raises 436,905 hidden-text
flags across five views before de-duplication, about 107 MB allocated per call, which is the flag volume
recorded above under "Beside it", now with a CPU cost as well as a storage cost. The attribution to
contention is inferred from the artifact's run order, not measured on a four-core machine.
The stop did its job: it guards the margin of the gate CI enforces, and a budget at 75% on the runner
that enforces it is a gate that goes red without a defect, whose predictable remedy is a larger budget.
The budget stays 2,000 ms and is not raised, because raising it answers the runner, not the screener.
What happens instead, as one commit on A3 before A4: one untimed screening call per process before any
row is timed (the fuzzer's precedent, which excludes each row's first send); a scaling fact that holds
R10.69's own claim, linear, on any machine; and a CI step that reruns the screening cost tests alone,
whose timings alone are the stop's reading. The proposal to run hidden-text detection on the original
view alone, said to give an identical result, does not: the base64-decoded view maps every decoded
character to its run's start (`DerivedViews.cs:265`), so a zero-width character carried in base64 is
flagged only there, and that change would drop it. The flag volume is the coalescing decision's, with
D35, and is not cured inside A3. CI run 37575114798 (X64, 4 processors, e166e59) measured the contention
A3 inferred: zero-width-space ScreenEnvelope at the cap took 309 ms alone and at least 1,010 ms (fastest
of three) inside the full solution, a 24.8× ratio to 32 KiB; the ~107 MB of flags per call is a cost that
grows with host load. A caller choosing that input costs about 1 s of CPU and 107 MB per call on a
contended 4-core host, an input to D35's coalescing decision, not cured in A3.

**The zero-width-space ratio's cause, measured and fixed (review of c31ce24, 2026-10-07).** A3's
zero-width-space ratio was caused by per-view `RiskFlag`s kept live until `Conclude`: one flag per hidden
character in each of about five derived views, about 437,000 objects at the cap, which the collector
re-traced as the live set grew with the input (gen0/1/2 10/5/1 at the cap), so wall time was mildly
superlinear while allocation stayed linear. Fixed by keep-first dedup before allocation: `ContentScreener`
applies `Conclude`'s keep-first (category, offset) rule as flags are found, and the hidden-text step yields
offsets, so a flag is allocated only for an offset not yet seen. Output-identical (the red-team corpus
console byte-identical before and after, the request fuzzer's answers byte-identical to A1's, and a
reference screener compared on the corpus, 2,000 generated inputs and a base64-wrapped U+200B), no
detector-version change, no errata entry; 106.6 → 34.8 MB at the cap, no collections. Every one of
the 20 scaling rows asserts its wall ratio again (ceiling 24, reading stop 16, allocation ratio 9);
the exemption the previous ruling proposed for this row was withdrawn with its premise fact, which
would have stayed green after its cause was gone. CI runs 37596130961, 37601395004 and 37606333636 (X64,
4 processors, the class alone, head 5d7a00a) read zero-width-space at 8.79/8.86, 9.14/9.05 and
9.40/8.81 (ScreenEnvelope/ScreenText), every row at most 9.40, every allocation ratio at most 8.39,
no collections on any row at the cap, 20 rows in each artifact, and slowest timing rows of 111, 117
and 94 ms.

**Closed by the stage's PR A** (Tasks A3 and A4, 2026-10-07; recorded by Task A7, 2026-10-09; the
stage's plan, `docs/superpowers/plans/2026-10-06-one-part-at-a-time.md`). Part (1) of "Not fixed
here": every screening pattern runs on the non-backtracking engine (R10.69), and
`HighEntropyAssignment` was rewritten for it, its keyword boundary consumed rather than looked behind
and its flag reporting the value's span, not the keyword's (`src/Curia.Domain/Screening/SecretScanner.cs`).
Part (2): a flag's and a moderation record's rationale is at most 4,096 UTF-8 bytes (R10.68,
`RationaleLimit.MaxUtf8Bytes`, `src/Curia.Domain/Moderation/RationaleLimit.cs`), refused before
screening and before the post is looked up, on both paths: over HTTP with 422
`curia/flag/rationale-too-long`, after authentication, and after authorization, which reads the
whole log and the private flag store under the raiser's hold; and by `curia-operator moderate`,
which has no HTTP route, with `curia/moderation/rationale-too-long` as a refusal on stderr, after
its operator-name and rationale-required checks and before any read of the log.
- *The A3 timings (A3 Step 5).* The local reading, Release, the class alone (Arm64, 16 processors):
  slowest row zero-width-space ScreenEnvelope, 173 ms at A3 and 55 ms after the keep-first dedup
  (review of c31ce24). The CI reading, Step 5's three dispatches at head 5d7a00a (X64, 4 processors;
  runs 37596130961, 37601395004 and 37606333636): `screen-timings` 20 rows each, slowest 111, 117 and
  94 ms, under the 500 ms stop and the 2,000 ms budget; `screen-scaling` 20 rows each, largest wall
  ratio 8.86, 9.14 and 9.40, under the stop of 16 and the ceiling of 24.
- *The corpus diff: empty.* Step 1's console over `conformance/red-team/` (121 entries, 363 rows)
  before and after A3 differs in its first line alone, the versions; before and after the keep-first
  dedup it is byte-identical, first line included. The request fuzzer's answers file after A3
  (`/tmp/answers-A3.json`, 34,838 keys) and after the dedup (`/tmp/answers-A3b.json`) is
  byte-identical to A1's.
- *The versions.* `SecretScanner.Version` `secrets/2026-09-26` to `secrets/2026-10-06`, and
  `InjectionDetector.Version` `injection/2026-09-26` to `injection/2026-10-06`, each with a history
  line; `conformance/red-team/RESULTS.md`'s detector line regenerated, rates unchanged.
- *The facts.* R10.69: `ScreeningCostTests.R10_69_EveryScreeningPatternRunsOnTheLinearEngine`
  (15 patterns, by reflection), `R10_69_ScreeningAtTheStringCapStaysWithinItsBudget` (ten rows at
  262,144 bytes, 2,000 ms) and `R10_69_ScreeningCostScalesLinearlyToTheStringCap` (wall ratio at
  most 24, allocation ratio at most 9); the IL-wide engine fact,
  `RegexEngineTests.R10_69_EveryRegexInShippedCodeIsAGeneratedPatternOnTheLinearEngine` in
  `Curia.Architecture.Tests`, over every assembly built from `src/` (review of 1b0d423);
  `ScreenerReferenceTests` (the screener against a reference built from public surface, on the
  corpus, 2,000 generated inputs and a U+200B inside base64; review of c31ce24); and
  `DetectorTests.R10_25_TheHighEntropyRuleReportsTheValueNotTheKeyword`. R10.68:
  `FlagEndpointTests.R10_68_ARationaleAtTheCapIsAcceptedAndOneByteOverIsRefused`,
  `R10_68_TheCapCountsUtf8BytesNotCharacters` and
  `R10_68_AnOverlongRationaleIsRefusedBeforeScreeningOrNoSuchPost`;
  `OperatorModerationTests.R10_68_AModerationReasonOverTheCapIsRefused` and the moderation order fact,
  `R10_68_AnOverlongModerationReasonIsRefusedBeforeScreeningOrNoSuchPost` (review of a403a3c);
  `RationaleLimitTests` (five); the two Application read-counting facts,
  `ApplyModerationTests.R10_68_AnOverlongRationaleIsRefusedBeforeTheLogOrAnyFlagRowIsRead` and
  `RaiseFlagTests.R10_68_AnOverlongRationaleIsRefusedBeforeThePostsStreamIsRead` (review of the A4
  round-1 fix); and the five "after" facts (review of the A7 round-1 fix), the moderation path's
  `ApplyModerationTests.R10_68_AnOverlongReasonFromANonOperatorIsRefusedAsNotAnOperator`,
  `R10_68_AnOverlongReasonUnderABlankOperatorNameIsRefusedAsBlank` and
  `R10_68_AnOverlongBlankReasonIsRefusedAsRequired`, and the flag path's
  `FlagEndpointTests.R10_68_AnUnauthenticatedOverlongFlagIsRefusedByAuthenticationFirst` and
  `R10_68_AnOverlongFlagPastTheFlagBudgetIsRefusedByAuthorizationFirst`.
- *The falsification cases* (the plan's Task A6, run 3, `falsify: 56 cases, 0 not RED`): F1, F2, F3
  and F13 (the engine, the budget row, the value's span); F31-F33, the IL-wide engine fact, each green
  on the Domain reflection fact (review of 1b0d423); F4-F7 (the cap, its unit, and the order on each
  path); F34 (review of a403a3c) and, beside it, F48 (review of A6), the screening half of R10.68's
  order on the moderation path and on the flag path; F35, its lookup half; F36 and F37, the two
  read-counting facts (review of the A4 round-1 fix); and F54, the moderation half of R10.68's
  "after" order (the operator-name and rationale-required checks), and F55 and F56, its flag half
  (authentication, then authorization) (review of the A7 round-1 fix). Run 3 ran all 56 on the
  committed fix (`4740d34`) and supersedes run 2.
- *Observed and acted on (review of A6).*
  `RaiseFlagTests.R10_68_AnOverlongRationaleIsRefusedBeforeThePostsStreamIsRead`'s summary said
  RaiseFlag reads no flag rows on any path; since R10.70 it reads `IFlagDetailStore.ReadAllAsync`
  after the post's stream (`RaiseFlag.cs:126`). The order makes zero post reads imply zero detail
  reads, so the fact held, but the sentence was false; it now reads "RaiseFlag reads the
  flag-detail store only after the post's stream, so zero post reads implies zero detail reads."
  (`tests/Curia.Application.Tests/Moderation/RaiseFlagTests.cs:224`–`:225`).
- *Observed and acted on (review of c6a49b9).* This closure first said both rationale slugs were
  refused with 422 after authentication and authorization. That holds on the flag path only. The
  moderation path is `curia-operator moderate`, out of band, with no HTTP status and no token step,
  and its cap follows the operator-name and rationale-required checks
  (`ApplyModeration.cs:79`–`:93`). The sentence now separates the two paths. Separately,
  `FlagEndpointTests.R10_68_AnOverlongRationaleIsRefusedBeforeItIsScreenedOrAPostIsRead`'s summary
  said an overlong flag "costs no read of the log". On the HTTP route the endpoint takes the
  raiser's hold, reads the whole log and the private flag store, and runs the PDP before
  `RaiseFlag.RecordAsync` reaches the cap (`ForumEndpoints.cs:801`, `:809`, `:820`, `:824`, `:843`;
  `RaiseFlag.cs:96`). R10.68 permits those reads, so the code was correct and the sentence was
  false. The summary now says what the fact sees and names
  `RaiseFlagTests.R10_68_AnOverlongRationaleIsRefusedBeforeThePostsStreamIsRead` as the fact that
  holds the no-read property, at the Application layer.
- *Observed and acted on (review of the A7 round-1 fix).* The review of c6a49b9 said the corrected
  closure's ordering claims were already falsified by F34-F37 and F48. That held for the "before"
  halves only: each of those cases moves the cap later. A cap hoisted above `ApplyModeration`'s
  operator checks left `Curia.Application.Tests` at `Passed: 310`, and no fact sent an
  unauthenticated or denied flag with an overlong rationale: under F55 and under F56, applied before
  the new facts existed, the whole of `Curia.Api.Tests` was run and no fact went red on the order
  (every failure, seven under F55 and one under F56, was an absent or null rationale reaching the
  patched cap and answering 500, or the closed pass's `Accept` coverage line the patched early
  return adds; the plan's ruling bullet lists them). Five facts now pin the order:
  `ApplyModerationTests`' (a) `R10_68_AnOverlongReasonFromANonOperatorIsRefusedAsNotAnOperator`, (b)
  `R10_68_AnOverlongReasonUnderABlankOperatorNameIsRefusedAsBlank` and (c)
  `R10_68_AnOverlongBlankReasonIsRefusedAsRequired`, and `FlagEndpointTests`' (d)
  `R10_68_AnUnauthenticatedOverlongFlagIsRefusedByAuthenticationFirst` and (e)
  `R10_68_AnOverlongFlagPastTheFlagBudgetIsRefusedByAuthorizationFirst`, falsified by F54 ((a)-(c)),
  F55 ((d) and (e)) and F56 ((e) alone, (d) green). Both R10.68 order facts were renamed from ...BeforeItIsScreenedOrAPostIsRead to ...BeforeScreeningOrNoSuchPost: each sees the cap precede screening and the no-such-post answer, and neither can see a log or stream read, which the two Application read-counting facts hold (F36, F37).

**Left open: the amplification paragraph** ("Beside it, not a cost of time", with its serving half).
R10.69 bounds the screener's work, and the keep-first dedup its allocation; neither changes how many
flags a run of hidden characters raises, stores and serves. That is the coalescing decision, with D35,
for the next errata pass.

**Closed, the amplification half, by the hidden-beyond-bmp stage** (D35's "Closed by" paragraph; errata G23,
R10.77; PR pending). A run of adjacent hidden characters is one flag, and a post stores at most 32 flags of an
annotating category, so 87,381 flags at the cap become at most 32 per category; a category that rejects is never capped.

### D33 — a string a caller chose reaches a parser or a store that throws on it *(opened by `curia-architect` on the strangers stage's final gate, 2026-10-06; gated by the one-part-at-a-time stage's PR A, R14.10; closed by its PR B, 2026-10-10; errata G18, R11.34)*

**Found by not converging.** The strangers stage closed fifteen 500s under D25, and each round of its
final gate found instances the round before had not: in the second round a `jti`, a `nonce` and an
assertion's `kid` (`PostgresReplayCache`, `PostgresDpopNonceStore`, `PostgresAgentKeyStore`) and a
form's charset; in the third, a post signature header that does not decode, its blank `kid`, a
`board` or `parent` holding U+0000, and a flag against a post id of white space. Every instance had the
same shape. A value the caller chose reached code that cannot take it and signals so by throwing. Five
mechanisms produced them:

- **A port takes a caller's identifier as a bare `string`, and its adapter throws or Postgres refuses.**
  `IReplayCache.TryInsertAsync(string jti, …)` (`src/Curia.AuthN/Ports/IReplayCache.cs:26`),
  `IDpopNonceStore.IsCurrentAsync(string nonce, …)` (`src/Curia.AuthN/Ports/IDpopNonceStore.cs:21`),
  `IAgentKeyResolver.ResolveAsync(string agentId, string kid, …)`
  (`src/Curia.AuthN/Ports/IAgentKeyResolver.cs:47`) and `IAuthorKeyResolver`
  (`src/Curia.Application/Ports/IAuthorKeyResolver.cs:50`, `:51`). Behind them,
  `PostgresReplayCache.cs:118` and `PostgresAgentKeyStore.cs:330` guard with
  `ArgumentException.ThrowIfNullOrWhiteSpace`, and Postgres refuses U+0000 in `text` (22021) or `jsonb`
  (22P05) and a btree row past 2,704 bytes (54000). The second and third rounds' Postgres-backed 500s
  were all here.
- **A use case's precondition is written as a throw on a value the route passed through unrefused.**
  `RaiseFlag.cs:84` throws on `raisedBy`, and it threw on `postId` until the third round (`:89`);
  `AcceptAnswer.cs:56`–`:58`, `PostProjection.cs:111`, `ApplyModeration.cs:77` and the other
  `ArgumentException.ThrowIf*` guards in `src/Curia.Application` are the same shape. Each is safe today
  only because a route or a validator happens to refuse first. CS-10 reserves exceptions for "bugs and
  infrastructure faults" (`curia-csharp-scoping.md:175`), and these treat a caller's value as a
  precondition.
- **A JSON reader accepts a value that a later accessor throws on.** `JsonDocument.Parse` accepts an
  escaped unpaired surrogate, and `JsonElement.GetString()` throws on it. The guard was written twice
  (`CompactJws.EveryStringDecodes`, `src/Curia.AuthN/Jwt/CompactJws.cs:133`, and its twin in
  `src/Curia.Canon/Jws/DetachedJws.cs`), one round apart, because the first fix did not reach the
  second parser. Nothing stops a third parser.
- **A framework reader throws an exception the endpoint does not catch.** The form reader's charset
  lookup threw `NotSupportedException` for UTF-7 (`TokenEndpoint.cs:88`). The minimal-API binder threw
  for a charset it could not read, which `JsonCharset` now refuses first. No type can fence this; only a
  fuzzer finds it.
- **A refusal written at one reader is a rule the next reader does not know about.** R11.33's own
  reason says so of 5xx details, and the stage fixed that at the boundary (`ServerFault`). It fixed
  every input instance at a reader, though: `CompactJws.IdentifierRefusal` (`CompactJws.cs:159`),
  `IngestPipeline.cs:100`, `:102` and `:121`, `RaiseFlag.cs:89`, and the enrollment route's
  `RefusedText`. Five places now define what a storable caller string is, and they do not agree.
  `IdentifierRefusal` caps length and the ingest checks do not. None of them checks UTF-16
  well-formedness, which they leave to the parser before them.

**Why the sweep did not converge.** `RequestSurfaceTests` (`tests/Curia.Api.Tests/RequestSurfaceTests.cs`)
holds a hand-written list of hostile values (`:74`) and hostile bodies (`Requests`, `:1167`). Trap 26
is that a sweep reaches only what it sends, and all five of its instances were *positions* the sweep
never varied, not values it lacked. The hand list still does this. The two hostile JSON bodies
(`:1179`, `:1183`) put every hostile member into one object, so the first member a route refuses hides
the rest. Hostile `agent_id` and `rationale` are never sent beside a valid `kid` and `kind`. The sweep
is a list of the positions someone thought of, and the gate found the rest by thinking of more.

**What is not known.** Whether f914059 answers 500 to any request. It was not swept after the third
round's fix. Instances not yet found are expected, but none is recorded. CS-8 has never been built
(`curia-csharp-scoping.md:151`: "every identifier is a strongly typed wrapper, never a bare `string`";
no `AgentId`, `PostId` or `Kid` type exists in `src/`), and nothing enforces it. That is the gap this
class grew in.

**The fix, scoped for the next stage** (errata entry and requirement numbers to be allocated when the
entry is written):
1. One definition of a string the Forum will store or index: well-formed UTF-16, no U+0000, not blank
   where it is an identifier, and at most a per-type number of UTF-8 bytes. It lives in
   `Curia.Domain.Primitives` and is used by readonly record structs that a caller's identifiers become
   at the boundary (`AgentId`, `PostId`, `Kid`, `Jti`, `DpopNonce`, and `BoardName` for what
   `PersistAsync` writes outside the canonical text). Each has `static Result<T> Parse(string?)` and no
   public constructor that skips it, which is CS-8 as written. NFC belongs only to the types a
   requirement normalizes (R4.36's `agent_id`). It is never part of the general rule, because content
   is never normalized (erratum D1).
2. The fence, in `Curia.Architecture.Tests` beside CS-15's: no method of a port interface in
   `Curia.Application` or `Curia.AuthN`, and no public method of a use case the Api calls, takes a
   `string` parameter unless an allowlist row names it and gives a reason (free text that is screened
   or never stored, the schema name a constructor takes). A new `string` parameter with no row fails
   the build. The five existing refusals are deleted in favour of `Parse`, and the
   `ArgumentException.ThrowIf*` guards on caller-derived values go with them.
3. A banned-API rule (`BannedApiTests`): `JsonDocument.Parse`, `JsonNode.Parse` and
   `JsonElement.GetString` are permitted only in the readers that guard decoding (`CompactJws`,
   `DetachedJws`, Canon's `JsonReader`), plus a reasoned allowlist for parses of the Forum's own bytes
   (`ActaEndpoints.cs:259`) and for reads after a guarded parse (`AccessTokenClaims.cs:60`, `:69`).
4. A request fuzzer that replaces the hand list as the gate, and keeps the hand list as its regression
   rows. For every registered route it takes one valid exemplar. A route with no exemplar fails the run
   unless a row says why. It splits the exemplar into parts: path segments, query values, headers, form
   fields, every JSON leaf at every depth including nested JWKs, and every JWS header member and claim.
   The proof, the client assertion and the post signature header are re-signed with the agent's key so
   that a mutation reaches past verification; a second, unre-signed copy reaches the parse. It varies
   **one part at a time** and holds every other part at the exemplar's value. That is trap 26's lesson
   applied by the fuzzer, not by its author. The variations are a closed set: removed, retyped (null,
   number, boolean, array, object), and for a string empty, white space, U+0000, an escaped lone high
   and low surrogate, raw invalid UTF-8, U+2028, U+FFFE, a line break, not NFC, and long at 1 KiB,
   64 KiB and the route's admitted maximum. A number varies to 0, −1, 1e13, −1e11, 2^53+1, 1.5 and
   1e400. A CsCheck pass draws random strings from a generator weighted toward those categories at a
   fixed seed and iteration count, printing the seed on failure. The oracle checks four things:
   - no response is 5xx;
   - every 4xx is a problem document, or RFC 6749's error at `/oauth/token`;
   - every request finishes inside a stated per-request budget (D32's gate);
   - the unmutated exemplar answers 2xx both before and after its mutations, or every mutation
     tested a request that was already refused.
   A fresh agent, or an advanced clock, keeps the rate budget from turning later mutations into tests
   of the budget. The fixture must be one of its own, because every read folds the log in memory and
   accepted mutations grow it.
5. **Acceptance, derived from a different artifact than the fuzzer.** Revert each of D25's fifteen
   fixes in turn, with the hand sweep's rows for that instance removed. The fuzzer alone must go red on
   every one. If it misses any, it is a list of positions again.

**Red facts first.** Before any type exists, run the fuzzer against the tree at the stage's base and
record every 500 it finds here as this entry's instances. They are the stage's baseline, and the
evidence that the class was open at the merge.

- **The red baseline (Task A1, 2026-10-06; regenerated after the review of 7bf1160): no instance.**
  The closed pass at the base (2c26477), with the fuzzer as the review rounds left it, 30 rows over
  23 routes and 35,314 planned sends, each sent, counted unsent, or counted superseded, answered no
  5xx and no 4xx that is no problem document. So the ledger
  (`tests/Curia.Api.Tests/Fuzz/ExpectedFaults.cs`) holds no row, and D33 has no `D33-<n>` yet. A
  superseded copy is a re-signed copy of an envelope variation with no canonical form: 520 in the
  pass, across nine variation ids (nul-raw, lone-high, lone-high-raw, lone-low, lone-low-raw,
  bad-utf8 and overlong, 72 each; 1e400, 9; `removed` of the `json:/envelope` root, 7), bounded by
  `R14_10_OnlyAVariationWithNoCanonicalFormIsSuperseded`. It failed only on budget rows, all D32's,
  left failing for Task A3: 101 in the first run and 103 in the second. The two extra rows are
  comment and verification `json:/envelope/nonce long-self-262144 re-signed`, a Fresh part. They are
  every `long-r-262144` and `long-comment-262144` variation, and some `long-self-262144` ones, of a
  string the screener reads: envelope strings across the seven `POST /v1/posts` rows, re-signed, and
  the flag's `json:/rationale` three times (about 8 s for `r` and `self` and 3 s for `<!--`, against
  2 s). The plan's Task A1 Step 6 holds the second run's lines verbatim. Watch item for A3 Step 7: a
  difference in status or problem type on a Fresh-part `long-*` variation between
  `/tmp/answers-A3.json` and `/tmp/answers-A1.json` is a finding (content-dependent screening,
  possibly a secret-scanner false positive on random hex), never a flap. The coverage of reads, now
  per route and both ways with `RefusedQuery`, the reach clause and the restart check passed. The
  restart check on the `vote` row asks only `GET /health`, because a vote is never served (R8.55).
  D25's fifteen fixes held on the base, which is why A5 must revert them to see the fuzzer go red.
  The first baseline, at 7bf1160 (35,004 sends, 91 and 92 budget rows), is withdrawn, not merely
  superseded. It sent 15,991 re-signed copies of raw-byte envelope variations carrying the original
  signature and credited them with reach. Its query coverage compared names only, so `/v1/search`'s
  reads made nine `RequestReadQuery` entries unfalsifiable, and `/v1/search`'s `verification` and
  `environment_version` reads went unlisted. Its "coverage passed" was a probe that could not fail.
  Observed beside it, and not a fault the oracle names: a flag is accepted against any aggregate that has events, so the enrollment
  row's accepted `agent_id` variations could be flagged as though they were posts
  (`RaiseFlag.cs:109`–`:114` reads the stream and never asks that it is a post's).
- **The red baseline, continued (Task A2): no instance.** The seeded random pass
  (`RequestFuzzRandomTests`, seed `0000001diyh1`, printed by `new CsCheck.PCG(1, 20261006)`, 2,000
  draws on one thread) and the Kestrel pass (`RequestFuzzKestrelTests`, raw path bytes over a real
  socket on every route with a path parameter) answered no 5xx and no 4xx that is no problem
  document, and nothing was ledgered. The random pass's last recorded run (review of 6cbfa9f): 1,936
  draws sent and 64 unsent; 643 of 1,217 JSON and JWS draws past the first parser (0.528, `Divisor`
  4); 103 of 132 `POST /v1/posts` `json:/envelope/*` draws past ADMIT (0.780, `EnvelopeDivisor` 3).
  The Kestrel pass got past Kestrel's parser on every route with a path parameter; `%00`, `a%00b`,
  raw `0xFF` and raw `0x00` are answered 400 on every route (the plan's Task A2). So, with A1's
  closed pass, D33 has no `D33-<n>`: **the ledger holds 0 rows; PR B empties it**, which at 0 rows
  means PR B leaves it empty. The ledger is a ratchet and never holds a budget row
  (`tests/Curia.Api.Tests/Fuzz/ExpectedFaults.cs`). Its stale rule is partitioned: a `random-N` row
  is judged by the random pass, stale included, and every other row by the closed pass
  (`ExpectedFaults.IsRandom`; review of 9411deb), falsified by F12 (closed) and F28 (random).

- **Acceptance (Task A5), case 3: a miss behind the jwk parser, remedied by `perturbed-first` and by rebinding.**
  Run 1 at 7b2afce left case 3 (777db55's catch in `JwkPublicKey.cs:68`) GREEN, with 0 failure rows.
  `AccessTokenValidator` refuses a `cnf.jkt` that is not the proof jwk's thumbprint
  (`src/Curia.AuthN/AccessTokenValidator.cs:139`–`:141`, `curia/authn/binding-mismatch`) before it builds
  the key (`:152`), and the fuzzer's token was always bound to the agent's real key
  (`tests/Curia.Api.Tests/Fuzz/FuzzContext.cs:182`), so no re-signed variation of a proof's jwk built a
  key: at 7b2afce, counted from its answer map, every one on an authenticated route answered
  `curia/authn/malformed` (336 rows) or `curia/authn/malformed-jwk` (1,428), and every one at
  `POST /oauth/token` answered `invalid_dpop_proof` (294). `perturbed` set only a padding bit, and of the Long
  variations `long-r-*` and `long-self-*` decoded to other lengths and `long-space-*` and `long-comment-*`
  are not base64url, so the JWS parse, the jwk parser or `JwkParser.TryDecode` refused every one on an
  authenticated route before the binding check, and clause 5 credited those parts with reach on that
  parser refusal. The request is reachable from the wire: the token endpoint binds a token to any jwk it
  can read without building the key (D29; since the rotation stage's PR 0, D29 closed, it verifies the
  proof first, and an off-curve key is refused), the two-request path recorded under D25 and closed by
  777db55. Two remedies, by curia-architect's ruling (spec §4.10 Signing, §4.11):
  1. *Rebinding.* The re-signed copy of a variation of `jws:proof:header/jwk/…` rebinds the token's
     `cnf.jkt` to the RFC 7638 thumbprint the fuzzer computes from the rendered jwk's four strings
     (`RequestModel.ReboundTokenClaims`, `RequestModel.JktOf`), as `htu` and `ath` are derived. Alone,
     it left case 3 GREEN (probe 1): `perturbed` changes the last character of a 43-character base64url
     coordinate, which holds two padding bits a canonical encoding leaves zero, so every such value set
     only a padding bit and `Base64Url.IsValid` refused it. No variation produced a different 32-byte
     coordinate: `long-r-*` and `long-self-*` decode to other lengths and `long-space-*` and
     `long-comment-*` are not base64url, and `JwkParser.TryDecode` refuses both.
  2. *A variation class, `perturbed-first`:* the first character, by `perturbed`'s rule, on every string
     part. With it, case 3 is RED (probe 2): `3: RED  e.g. {"route": "GET /v1/inbox", "variant":
     "plain", "part": "jws:proof:header/jwk/x", "variation": "perturbed-first", "copy": "re-signed",
     "status": 500, …}`, 24 rows of 500 across every authenticated route, and the answer map at the new
     fuzzer differs from 7b2afce's only by 919 new `perturbed-first` rows. Unpatched, the suite stays
     green with the class.
  Falsified both ways by records that exist: GREEN without them at 7b2afce (run 1, case 3, 0 failure
  rows) and RED with them. Facts: `TokenRebindingTests` (three), falsified by hand once; runner rows F43
  (rebinding) and F44 (perturbed-first), Task A6. Two rounds of
  §4.11's three.
- **Acceptance (Task A5, R14.10): the fuzzer alone goes red on every one of D25's fifteen fixes.**
  Run 2, at 406a5ed (2026-10-08), unfiltered, is the record; run 1 at 7b2afce is history (plan, Task
  A5), because the fuzzer changed after it. The log is the session scratchpad's
  `a5r2/accept/accept.log`; no `DIRTY RESTORE`, no `FINAL STATE NOT CLEAN`. Its 20 lines, verbatim
  (each `e.g.` row cut at 200 characters by the runner):

  ```
  1: RED  e.g. {"route": "GET /v1/threads/{rootPostId}", "variant": "plain", "part": "path:rootPostId", "variation": "space", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 5, "ledgered": false}
  2a: RED  e.g. {"route": "POST /oauth/token", "variant": "multipart", "part": "header:Content-Type", "variation": "random-269", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 6, "ledgered": false}
  2b: RED  e.g. {"route": "POST /oauth/token", "variant": "urlencoded", "part": "form:client_assertion_type", "variation": "random-33", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 6, "ledgered": f
  2c: RED  e.g. {"route": "POST /oauth/token", "variant": "multipart", "part": "form:", "variation": "random-63", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 6, "ledgered": false}
  3: RED  e.g. {"route": "GET /v1/inbox", "variant": "plain", "part": "jws:proof:header/jwk/x", "variation": "perturbed-first", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 8, "ledgered": fals
  4: RED  e.g. {"route": "POST /oauth/token", "variant": "urlencoded", "part": "jws:assertion:claims/iat", "variation": "2^53+1", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 1, "ledgered": fa
  5: RED  e.g. {"route": "POST /v1/agents", "variant": "plain", "part": "header:Content-Type;charset", "variation": "empty", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 0, "ledgered": false}
  6: RED  e.g. {"route": "POST /oauth/token", "variant": "urlencoded", "part": "jws:proof:header", "variation": "null", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 0, "ledgered": false}
  7: RED  e.g. {"route": "POST /oauth/token", "variant": "urlencoded", "part": "jws:assertion:header/alg", "variation": "lone-high", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 0, "ledgered":
  8a: RED  e.g. {"route": "GET /v1/inbox", "variant": "plain", "part": "jws:proof:claims/jti", "variation": "removed", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 1, "ledgered": false}
  8b: RED  e.g. {"route": "POST /oauth/token", "variant": "multipart", "part": "jws:assertion:claims/jti", "variation": "random-1645", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 9, "ledgered"
  9: RED  e.g. {"route": "POST /oauth/token", "variant": "urlencoded", "part": "jws:assertion:header/kid", "variation": "removed", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 9, "ledgered": f
  10: RED  e.g. {"route": "POST /v1/posts", "variant": "question", "part": "jws:proof:claims/nonce", "variation": "nul", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 1, "ledgered": false}
  11: RED  e.g. {"route": "POST /oauth/token", "variant": "urlencoded", "part": "header:Content-Type;charset", "variation": "disabled-encoding", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 6, "led
  12: RED  e.g. {"route": "POST /v1/posts", "variant": "finding", "part": "jws:post:header/typ", "variation": "random-233", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 7, "ledgered": false}
  13: RED  e.g. {"route": "POST /v1/posts", "variant": "question", "part": "jws:post:header/kid", "variation": "removed", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 4, "ledgered": false}
  14a: RED  e.g. {"route": "POST /v1/posts", "variant": "finding", "part": "json:/envelope/board", "variation": "random-508", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 41, "ledgered": false}
  14b: RED  e.g. {"route": "POST /v1/posts", "variant": "answer", "part": "json:/envelope/parent", "variation": "nul", "copy": "re-signed", "status": 500, "problemType": "", "elapsedMs": 42, "ledgered": false}
  15: RED  e.g. {"route": "POST /v1/posts/{postId}/flags", "variant": "plain", "part": "path:postId", "variation": "space", "copy": "plain", "status": 500, "problemType": "", "elapsedMs": 7, "ledgered": false}
  16: RED  e.g. {"route": "POST /v1/agents", "variant": "plain", "part": "json:", "variation": "removed", "copy": "plain", "status": 400, "problemType": "", "elapsedMs": 0, "ledgered": false}
  ```
  `acceptance: 20 cases, 0 not RED`. The closed set's one addition is `perturbed-first` (case 3,
  above); the ledger is unchanged.
- **Observed, not acted on (Task A5): the reach clause counts a parser refusal as reach.**
  `Oracle.Reached` (`tests/Curia.Api.Tests/Fuzz/Oracle.cs`) excludes only the
  `AuthenticationAndSignatureRefusals` slugs (`Oracle.cs:23`–`:29`), so any `ParserRefusals` slug,
  `curia/authn/malformed-jwk` included, counts as reach, though that refusal comes before signature
  verification. At 7b2afce every re-signed `jws:proof:header/jwk/*` variation was credited that way
  (case 3, above). That is why the fuzzer did not report case 3's miss itself, and why adding
  `curia/authn/binding-mismatch` to the list would not have exposed it: no such row answered it.
  Whether a parser refusal should count as reach is a design decision: excluding `ParserRefusals` would
  turn the baseline red on every part whose only reader is a parser. Adding the slug alone would turn the
  baseline red on `jws:token:claims/cnf/jkt`, whose only reader is the binding check. The rebinding is
  pinned by the closed pass's rebinding rule (every re-signed perturbed-first variation of a proof jwk's
  x or y on a token-carrying route answers the key build's curia/authn/malformed-jwk, and every such row
  sends at least one) and by TokenRebindingTests, falsified by F43, F45 and F46; the rule's harness
  precondition by F47; perturbed-first's place in the closed set by F44.
- **Observed, not acted on (Task A5): two rows of the answer map flap between runs.** `GET
  /v1/posts/{postId}` and `GET /v1/threads/{rootPostId}` `path:… perturbed plain` answer `200` or
  `404 …/not-found` by run, with no change to the code. The fixture's clock is fixed, so
  `UlidGenerator`'s monotonic rule (`src/Curia.Domain.Primitives/UlidGenerator.cs`) makes the three seed
  questions consecutive ULIDs, and `perturbed` of the first one's last character names the second when
  that character's ASCII successor is its Crockford successor, which the run's random suffix decides.
  A before-and-after comparison of answer maps (A3, A4, A4b, PR B) that finds these two rows changed has
  found this, not a change on the wire.
- **Observed, not acted on (review of the rotation stage's Task 0.2, 2026-10-12): the random pass's
  floor counts the form checks' bare `invalid_request` as past the first parser,** although nothing
  past the form reader ran. That is the same as main, where the form refusals served `detail` as JSON
  null and their problem type was the same bare code, and it was ruled untouched by M5. It is a known
  inflation of the floor (vacuity question 2), to be settled when the floor's definition is next
  opened.
- **The Forum accepts a line break in an envelope's identifier-like members, and a `parent` that is
  no ULID.** A `board`, a `parent` and a tag may hold any character a JSON string may
  (`PostEnvelope.cs:100` requires only a non-empty `board`, and `:128` only that an answer names a
  parent). The reference readers quote them (R10.63). For `parent` this is a divergence, not a
  silence: Table 9 types it `ULID?` (traced, by reading). Whether the Forum should refuse a control,
  format or separator character there is a decision about each member's value space (R8.63); for
  `parent` it belongs beside the queued question of whether an answer's parent must exist and share
  its board, in the next errata pass (the strangers stage's spec, §7). U+0000 alone is refused since
  the final gate's third round, because the log cannot store it (D25's fourteenth).
  *The five-agent exercise (2026-10-06, its audit's finding 10) posted to boards
  `"general"` followed by ESC `[2J`, `"gen"` U+0435 `"ral"` (a Cyrillic `e`) and a tag holding U+202E,
  each accepted (posts `01M48BS42S4YW74B3GYEX077F8` and `01M48BS3YXQDBN2W99N83E9ZVT`). No route lists
  boards, so such a board is a shadow nobody can enumerate; a reference reader prints its escapes, and a
  consumer that decodes JSON and prints it sees `general`. The value-space decision above is what
  closes it; R11.34's rule refuses only what a store cannot hold, and the one-part-at-a-time stage
  leaves it alone on purpose.*
- **A bare `GET /v1/jwks` answers `curia/request/unreadable`, not `curia/keys/agent-required`**
  (the exercise's audit, finding 5). `GetJwks` binds `string agent`, non-nullable
  (`src/Curia.Api/ForumEndpoints.cs:1292`–`:1293`), so the minimal-API binder refuses a request with
  no `agent` before the handler's own check (`:1295`–`:1297`) is reached, and that check answers only
  an empty or white-space value. A 4xx problem either way, so the fuzzer's oracle passes both; the
  answer names the wrong cause. Closed by the stage's PR B, Task B3, which takes `?agent=` as an
  `AgentId` parsed from `string?` and maps `absent` and `blank` to `agent-required`.
- **A 4xx detail can echo a stranger's text back.** `GET /v1/posts/{id}` names the id it did not find,
  and `GET /v1/jwks?agent=` the agent; an agent that copied an id out of a post reads the post's author
  back in the Forum's detail. The reference readers quote it; R11.33 does not change a 4xx.
- **ADMIT's malformed-JSON detail is System.Text.Json's message** (`src/Curia.Canon/Json/JsonReader.cs:161`),
  which can echo a character of the submission, against R6.40's "echoes no content". Reached only by an
  authenticated submitter, about its own bytes (traced, not run).
- **A tag holding a comma, or beginning or ending with white space, cannot be named as a filter on
  the wire.** The Forum's `?tags=` filter (`ForumEndpoints.cs:1577`, `:1745`) splits on `,` and trims,
  so it would read such a tag as other tags. The reference client refuses such a filter
  (`curia/client/tag-not-filterable`) rather than send one the Forum would misread. Whether R8.63's
  tag value space excludes these or R9.26's filter grammar changes is for the next errata pass (Task
  5's review).
- ~~**The token endpoint's `detail` still names the failing check's slug** (the key-binding stage's M5),
  and its DPoP proof is still unverified (D29). Its `server_error` 500 is RFC 6749's shape, not a
  problem document, so R11.33's second sentence does not reach it: it carries the fault's title as
  `error_description` and its type as `detail`, and nothing logs its reason, against R5.12's "log the
  specific reason internally" (`TokenEndpoint.cs:221`). All three are at one endpoint and ride with
  rotation, which changes that endpoint's key handling.~~
  *Closed by the rotation stage's PR 0* (**M5**, Task 0.2, 2026-10-12, merge pending; D29 beside it).
  `OAuthRefusal` (`src/Curia.Api/Issuer/OAuthRefusal.cs`, spec §4.11) replaces `OAuthError`: a refusal
  from `/oauth/token` serves RFC 6749's `{"error","error_description"}` and no `detail`;
  `invalid_client`, `invalid_dpop_proof` and `server_error` take fixed descriptions ("The client could
  not be authenticated", "The DPoP proof was not accepted", "The Forum could not issue a token"), and
  `invalid_request` keeps its form-check text. A 4xx logs event 5001 with its code and its slug only,
  never the reason's `detail`, which can hold a header value the caller chose; a `server_error` logs
  event 5000 with its reason. **A 4xx logs its slug only.** The facts
  (`tests/Curia.Api.Tests/TokenEndpointRefusalTests.cs`), red on the base (no 5001 logged, a
  `detail` served, or the fault's title as the description) and green after: `M5_AKidRegisteredToAnotherAgentAndASignatureThatFailsReadTheSame`
  (the two bodies byte-equal), `M5_NoTokenRefusalCarriesADetail` (on the base "22 of 30 token answers
  carry a detail"), `M5_AServerErrorIsLoggedWithItsReason` and
  `M5_ARefusalLogsItsSlugAndNoneOfTheCallersText` (a proof `typ` holding CR, LF, ESC, U+202E,
  `forged` and 2,000 `a`: one 5001, Type `curia/authn/typ-mismatch`, and none of the caller's text in
  the message or any structured value). Every earlier token-refusal assertion
  (`KeyBindingTests`, `StoredKeyFormTests`, `TokenSubjectBindingTests`, `RequestSurfaceTests`,
  `TokenEndpointProofTests`, `OracleTests`) is re-pinned to the coarse body and the captured event's
  Type, through the capturing logger lifted to `tests/Curia.Api.Tests/Support/`. The fuzz oracle
  classifies a token refusal by the slug it logged (`Oracle.ProblemType`'s captured-reason term).
  Falsified (Task 0.3), the runner's lines with their timings removed:
  ```
  F0-6: RED
      Failed Curia.Api.Tests.TokenEndpointRefusalTests.M5_AKidRegisteredToAnotherAgentAndASignatureThatFailsReadTheSame
  F0-7: RED
      Failed Curia.Api.Tests.TokenEndpointRefusalTests.M5_NoTokenRefusalCarriesADetail
  F0-9: RED
      Failed Curia.Api.Tests.Fuzz.OracleTests.M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason
      Failed Curia.Api.Tests.Fuzz.OracleTests.R14_10_TheTokenEndpointsProofRefusalsStopAtTheFirstParser(sendProof: True)
  F0-12: RED
      Failed Curia.Api.Tests.TokenEndpointRefusalTests.M5_ARefusalLogsItsSlugAndNoneOfTheCallersText
  F0-16: RED
      Failed Curia.Api.Tests.Fuzz.OracleTests.M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason
      Failed Curia.Api.Tests.Fuzz.OracleTests.R14_10_TheTokenEndpointsFormRefusalIsLoggedWithNoReasonAndIsReach
  ```
  F0-6 served the reason's title (`"No key with that identifi…"`) in place of the fixed description;
  F0-7 served `"detail":"curia/keys/not…"`; F0-9 classified by body alone (`"invalid_client"` for
  `"invalid_client curia/authn/signature-invalid"`); F0-12 logged the caller's `typ` after the slug.
  - *Ruled* (curia-architect, 2026-10-12, on the implementer's stop): the oracle's no-reason rule
    exempts a bare `invalid_request` (`Oracle.TokenEndpointFormRefusal`, the form checks). Exact
    equality on the bare code, so bare `invalid_client`, `invalid_dpop_proof` and `server_error` stay
    not-reach. The closed pass before the exemption is that exemption's falsification on the full pass,
    `RequestFuzzClosedTests.R14_10_NoVariationOfAnyPartIsAServerFaultAProblemlessRefusalOrOverBudget`
    red with `5 failures:`, verbatim:
    ```
    reach: POST /oauth/token [urlencoded] form:client_assertion_type: no re-signed or plain variation was answered 2xx or refused past the credential and the signature
    reach: POST /oauth/token [multipart] header:Content-Type: no re-signed or plain variation was answered 2xx or refused past the credential and the signature
    reach: POST /oauth/token [multipart] header:Content-Type;boundary: no re-signed or plain variation was answered 2xx or refused past the credential and the signature
    reach: POST /oauth/token [multipart] form:: no re-signed or plain variation was answered 2xx or refused past the credential and the signature
    reach: POST /oauth/token [multipart] form:client_assertion_type: no re-signed or plain variation was answered 2xx or refused past the credential and the signature
    ```
    F0-16 is its standing falsification. Also observed: a bare `invalid_client` ('not enrolled',
    TokenEndpoint.cs:154,167, reason null) was reach on the base and is not reach after M5. No part
    depended on it. Whether a form-check refusal should carry a reason is D33's existing 'parser
    refusal counts as reach' observation, not a new entry. If it is acted on, that is option (a) with
    its own floor re-measure.
  - *Accepted as built* (curia-architect, 2026-10-12): the capture is per-host, not fixture-wide. The
    plan requires a per-row factory for OracleTests and does not require a fixture-wide capture for
    the re-pins. Task 0.5's File Structure check would refuse ForumFixture.cs. Each re-pin asserts
    exactly one 5001 per request on its own host, which is the information the plan wanted.
  - *Accepted as built* (curia-architect, 2026-10-12): the missing-proof refusal now serves 'The DPoP
    proof was not accepted'. The plan says invalid_dpop_proof takes the fixed description, and an
    invalid_dpop_proof that is not missing already serves that text, so the two being
    indistinguishable is M5's purpose. The reason stays null, as Task 0.2 requires.
  - *Ruling correction* (curia-architect, 2026-10-12):
    `R14_10_TheTokenEndpointsFormRefusalIsLoggedWithNoReasonAndIsReach` asserts
    `Oracle.PastFirstParser(400, "invalid_request")` is true, not false. `PastFirstParser` is unchanged
    from main, where the form refusals served `detail` as JSON null, which main's `ProblemType` did not
    append, so this refusal's problem type was already the bare `invalid_request` and already counted
    past the first parser. The test pins that value, and a `PastFirstParser` that excluded it turns the
    test red, which is what "the floor is untouched" means; making the floor exclude the form refusal
    would change it, and is out of M5's scope.
  - *Observed, not acted on:* the missing-proof refusal still logs no reason. Giving it
    `AuthNErrors.MissingDpopProof()` would make the fuzz oracle count `curia/authn/missing-dpop-proof`,
    which is not in `ParserRefusals`, as past the first parser and move the floor, so PR 0 leaves it.
  - *Observed, not acted on* (review of Task 0.2, 2026-10-12): `invalid_client`'s two null-reason
    refusals ('not enrolled', `TokenEndpoint`'s two `new OAuthRefusal("invalid_client",
    OAuthRefusal.ClientNotAuthenticated, null)` sites) log no slug, which departs from the letter of
    spec §4.11 ("R5.12's specific reason is the slug"). Giving them slugs needs new error slugs in the
    source, and would make two bare `invalid_client` refusals count as reach and as past the first
    parser, which changes the oracle and possibly the floor, so PR 0 leaves it, as it leaves the
    missing-proof refusal above.
  - *Observed, not acted on* (review of Task 0.2, 2026-10-12; one entry): the coarse body makes a `kid`
    registered to another agent and a failing signature equal in bytes, not in timing
    (`LogBoundKeys`' read, by reading the code, not measured); that leaks nothing, because the Forum
    serves the kid-to-agent mapping. That reason does not reach a store row the log does not bind,
    which the Forum does not serve (R4.35, errata G16): a store miss returns before `BindingAsync` and
    an unbound row does not (`LogBoundKeys.cs:70-79`), so whether such a row exists is still told by one
    event-log read's time, read from the code and not measured (review of 91fe326). Closing it means a
    log read on a miss as well, or a resolver that consults the log first. A measured probe should
    precede either: time not-registered against not-bound-by-the-log over enough requests to separate
    one indexed read from none. An anonymous caller can force one event-log read per
    `/oauth/token` request with a public agent id and kid, which predates D29. `/oauth/token` has no
    rate limiter, and the register has no entry for one. Event 5001 carries no correlation id.
  - *Deferred* (review of Task 0.2, 2026-10-12): tightening `ProblemShape.NotAProblem` to the exact
    member set `{error, error_description}` for `/oauth/token`. It changes clause 2 of the closed fuzz
    pass and needs its own adequacy argument; the review checked neither whether any middleware under
    `/oauth/token` answers without `OAuthRefusal`. `M5_NoTokenRefusalCarriesADetail` holds the exact
    member set and each fixed description over the bodies its facts record.
  - *Fixed in the review of Task 0.2:* the fuzzer keeps a logged reason only by event id and logger
    category (5001 from `OAuthRefusal`; 5000 from it or `ServerFault`), falsified with a stray 5001
    under another category; `M5_NoTokenRefusalCarriesADetail` pins every refusal's members and each
    fixed description, falsified with main's `detail = null` shape and with a slug appended to
    `invalid_dpop_proof`'s description; and `b51896f` pinned a problem type, `invalid_request` with
    the form refusal's text appended, that main never produced (main served the form refusals'
    `detail` as JSON null, so its problem type was the bare code), so that assertion was removed.
  - *Fixed in the review of Task 0.3* (curia-architect, 2026-10-12): ClientAssertionValidator.cs's
    comment beside subjectText said TokenEndpoint serves an error's Title and Type. That was true when
    e083d6d (#84) wrote it and false after M5, where the token endpoint serves only the code and a
    constant description (fixed per code, except invalid_request's form-check texts;
    UnreadableRequests' bodies (400, 405, 413, 415) carry the status title) and OAuthRefusal logs a
    4xx's code and slug only. Rewritten, comment only, and corrected in the review of 2882b63, which
    had called every description fixed, contrary to OAuthRefusal's summary. No fact or falsification
    row, because a comment has no behaviour to pin; F0-6 and F0-7 are what hold the served body, and
    F0-12 what holds the logged slug.
- **`curia-operator` escapes what it reads and does not quote it.** It is the operator's tool over the
  database and not a reference reader. Its `TerminalText` is R10.67's `SpanText` since Task 9b, so an
  identifier enrolled before R4.37 holding a control, format or separator character reaches its
  output as escapes, unquoted; and `attest-owner` echoes its own arguments as given
  (`src/Curia.Operator/Program.cs:215`, `:219`).
- **R10.65's word was run in sh, dash, bash, zsh, fish, csh and tcsh, not in PowerShell or cmd.exe.**
  PowerShell documents a single-quoted string as verbatim but for `''`, which the word's alphabet
  excludes; no PowerShell was available to run it in, and none runs in CI. cmd.exe does not quote with
  single quotation marks at all, so no word is safe there. The committed facts run `/bin/sh` only; the
  other six were the design probe's, and none of them runs in CI.
- **The header sweep varies the two headers every route reads.** `Authorization` and `DPoP`, hostile
  without a credential and as a token bound to a key off the curve (`RequestSurfaceTests`); since Task
  8's review, a JSON body's declared charset and the NumericDates in a JWT an agent signs, and, since
  Task 11's fix review, a header or proof jwk member holding an unpaired-surrogate escape, and, since
  the stage's final gate, a signed `jti` and `nonce`, an assertion's and an access token's `kid`,
  and a token form's declared charset, are swept too (D25); and, since the stage's final gate,
  third round, a post signature's protected header: strings that do not decode, and a blank or
  non-string `kid`. A header one handler reads -- a conditional read's `If-None-Match` -- is not swept.
- **A display literal can hold a delimiter in the middle of a line.** `Of("<<<CURIA-UNTRUSTED-END>>>")`
  is that text between quotation marks (the Task 2 review ran it). A literal never begins a line, and
  `IsDelimitedSpan` (`src/Curia.Client/Frame.cs:323`) runs only on the Forum's served `rendered`
  member, never on a frame's output, where a literal would sit; it also requires each outer delimiter
  on a line of its own and refuses a span whose inner text holds either delimiter anywhere. A consumer
  that found a span in a frame's output by searching for the delimiter's text anywhere would be
  deceived; no reader here does.
- **`curia-mcp` names tools with a post id in them** (`Read the thread with curia_read "…"`). They are
  tool calls, whose arguments are JSON, not commands a shell runs, so R10.65 does not reach them (its
  text says "in a shell" since the Task 1 review); the id is a display literal, which a JSON argument
  reads as its value (R10.66).
- **A cleanup keyed on a directory's name deletes source.** `find . -name bin -exec rm -rf` removed
  `rust/curia-testis/src/bin` during the stage's build-check. Build output lives under `src/*/`,
  `tests/*/` and `tools/*/`; clean those, or nothing.
- **The not-the-Forum refusal echoes the type a stranger served.** ClientErrors.NotTheForum puts
  "problem type " + the served type into its detail (ClientErrors.cs:124), and ReadProblem takes it
  unchecked from "type" or OAuth's "error". The reference readers quote it (R10.63, gated since Task
  6's second review). Whether a refusal the client attributes to something that is not the Forum
  should echo that thing's words at all, or name only that it was not curia/-typed, is for the next
  errata pass.
- **The span a reader prints is not compared with the canonical form it verified.** `Passage.Render`
  writes `rendered` beside a verdict on `canonical`, and nothing checks that the one renders the
  other: a hostile Forum can serve a signed post that verifies with a span of other words, and the
  reader prints them under its verdict. R10.67 keeps such a span from driving a terminal; it does not
  make it the author's. Whether a reader derives the span from the canonical form itself, or compares
  the two and refuses a mismatch, is a decision about R10.18's two representations (traced, by
  reading).
  **Closed by the reader stage's PR A** (errata G19, R10.71; Task A1 and its two reviews,
  2026-10-11): the reader compares, and refuses a mismatch. The served form must be byte-identical to
  the bytes the signature covers (parsed under ADMIT's limits and re-canonicalized with NFC,
  `SpanCheck.CoveredForm`); the span must pass R10.63's delimiter check; its token must be one
  private-use character; and its inner text with that token stripped must equal the inner text of
  `Datamarking.Delimit` of the covered form (`src/Curia.Client/SpanCheck.cs`). On a mismatch over a
  delimited span, `Passage` writes a line beginning `content   FAILED:` and this client's rendering
  of the covered bytes, or, when there are no covered bytes, that line and no span. Over a span
  with no delimiters it writes `NOT A DELIMITED SPAN:` and the Forum's string as a display literal,
  never as content (R10.63's path), and no rendering of its own. Either way the passage fails, and
  the reading verbs (`curia read`, `thread`, `board` with or without `--titles`, `search --titles`)
  exit 6. A posting refusal keeps its refusal's code: a duplicate refusal exits 3, although an
  answer it frames can still carry the `content   FAILED:` line. Whether a posting refusal whose
  framed answer failed should exit 6 rather than 3 is undecided; the exit code says what happened
  to the post, and the FAILED line what happened to the answer. The CLI's duplicate refusal frames
  only answers it verified, and only on the posting path (`Program.RefusedAsync`). Facts:
  `SpanCheckTests`, `MarkingParseTests`, `RefusalOutputTests` and the `R10_71_` facts of
  `ReaderFrameTests` and `HelpTextTests` (`tests/Curia.Client.Tests/`);
  `ReaderFrameToolTests.R10_71_AMismatchedSpanReachesNoToolResult` (`curia_read`, `curia_search`,
  `curia_ask`); `RefusalPathTests` in `Curia.Architecture.Tests`, in Debug and Release; and
  `ClientVerificationTests.R10_71_TheForumsOwnRenderingPassesUnderEveryMarking` against the real
  Forum. Falsified by FA1-FA6, FA16 and FA21-FA32 (the stage's plan,
  `docs/superpowers/plans/2026-10-06-the-readers-claims-are-true.md`, Task A5).
- **Two verifications can move the retained head backwards (R6.53).** `PostVerifier.ConsistencyAsync`
  reads the retained head (`src/Curia.Client/PostVerifier.cs:415`), fetches a consistency proof over
  the network (`:434`) and writes the newer head (`:444`), and nothing serializes the three:
  `PrivateFiles.Write` makes each write atomic, not the read-compare-write. Two verifications
  interleaved there -- `curia-mcp`, which is long-lived, beside a `curia verify`, or two tool calls
  if the MCP SDK runs them concurrently (not checked) -- can leave the smaller of two verified heads
  retained: one reads 10, the other advances it to 20, the first verifies 10 to 15 and writes 15, and
  a log that forks after 15 is then consistent with what the client retains. The first-read branch
  (`:418`) has the same shape. Traced by reading, not run. The probe that would carry information:
  two `PostVerifier`s over one `HeadStore` directory and a stub log serving heads at 15 and 20, the
  first held by a barrier between its read and its write while the second completes, asserting the
  retained `tree_size` is 20; it must go red before a lock is written. The requirement comes first
  (R6.53, an addition: replace only with a head larger than the one retained at the moment of
  replacement, compared again under an exclusive lock held across the comparison and the write), and
  rides with the terminal-reader entry, whose reader verifies more than one post at a time.
  **Closed by the reader stage's PR A** (errata G19, R6.53 (add.); Task A2 and its two reviews,
  2026-10-11). `HeadStore.LockAsync` takes one exclusive lock on `logs/<origin>/head.lock`, shared
  by every process that retains that head, and `PostVerifier.ConsistencyAsync` holds it across the
  read, the fetch, the comparison and the replacement. A wait past its bound (30 s by default, on
  the given `TimeProvider`), a lock held elsewhere, file locking switched off, a lock the runtime
  does not enforce, or any other I/O failure is could-not-check and touches nothing. Facts:
  `HeadStoreLockTests` (`tests/Curia.Client.Tests/`), with `tests/Shared/flock-hold.py` holding
  the lock from another process and two child facts run under
  `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1`. Falsified by FA7-FA10, FA17-FA19 and FA33-FA40. The
  probe above, built as `R6_53_TwoVerificationsInterleavedLeaveTheLargerHead`, printed red at the
  base on macOS (`expected 20, retained 15`, the red commit of Task A2, under its first oracle; the
  review made B's refusal naming "held elsewhere" the oracle). On Linux CI it had not run when this
  was written: the cross-process facts' Linux run is Task A7's merge condition, and A7 records it.
- **What a signer is sent goes through the process's default writer encoding.** `ExternalSigner.Run`
  writes a signer's stdin through `process.StandardInput`, whose encoding the platform chooses, not a
  rule. R10.64 governs what a reader is shown, which is what comes back, so Task 10's review left it;
  what is sent today is base64url text (traced, by reading).
- **Task 10's enumeration covered `[Fact]` and `[Theory]`.** Four Rust `#[test]`s this stage added are
  named by no case: `display.rs`' `a_line_break_is_an_escape_not_a_line` and
  `a_character_outside_the_bmp_is_its_surrogate_pair`, and `display_output.rs`'
  `r10_63_two_names_nfc_makes_one_are_named_as_a_literal` and
  `r10_63_a_document_of_the_wrong_shape_is_refused_naming_no_value`. Case 4's patch reaches the second
  by its text, but runs only `vectors.rs`; whether any case turns them red was not run.
- **An external signer whose output begins with a UTF-8 byte order mark is refused, where it was
  accepted.** `ExternalSigner` now reads stdout through `ProgramOutput` (R10.64), which keeps
  `EF BB BF` as U+FEFF. `JsonDocument.Parse` refuses it on describe, and `String.Trim` does not strip
  it before base64url on sign. A signer that writes a BOM (Windows PowerShell's default, Python's
  `utf-8-sig`) stops working. Refusing is kept, because the protocol is non-normative and
  machine-parsed and RFC 8259 §8.1 forbids the mark. The describe side is pinned by
  `R10_64_ASignerWhoseOutputBeginsWithAByteOrderMarkIsRefused`. The sign side was observed by the fix
  review's scratch run and is not pinned. A signer's stdin is outside R10.64, as recorded before.
- **No gate reads a `Check` detail's holes.** The two facts added at the final gate pin the three
  sites in `ConsistencyAsync` that echoed the caller's own configuration. Nothing fails if a later
  `Check.Verified`, `Failed` or `CouldNotCheck` interpolates a served or caller-given value without
  `Check.Quote`. `OwnText(acta.….Describe)` in `curia verify` and `PostVerification.Render` in
  `curia_verify` both trust that every hole is quoted. A syntactic gate over the factories'
  interpolation holes, with an allowlist of numbers, computed digests and nested `Check` details,
  would hold it.
- **A head retained under a Forum URL with userinfo is orphaned by the second round's key change.**
  `HeadStore.OriginKey` hashed `GetLeftPart(UriPartial.Authority)`, which keeps userinfo, until the
  final gate's second round, and now hashes `HeadStore.Origin`, which drops it; the two agree on every
  URL without userinfo (pinned by `HeadStoreTests.R6_53_AnOriginKeyWithoutUserinfoIsUnchanged`) and
  differ on every URL with it. A client that verified against such a Forum before the change finds no
  head, reports 'this client had retained no earlier head' (`src/Curia.Client/PostVerifier.cs:420`),
  which is false, and anchors anew: R6.53's check against the old head is skipped once. The old
  directory's readable half names the credential, and nothing removes it. Not migrated: a migration
  would read the old key, which is the expression the residue grep and the userinfo scan forbid, for
  a configuration nothing documents. The remedy for an affected operator is to delete
  `<CURIA_CLIENT_HOME>/logs/<name containing the credential>` by hand, which costs the same one
  re-anchor. If rotation or a client release note ever needs a migration story, this belongs in it.
- **A post id of U+0000 on a route that reads one is not run.** The test host's client refuses a path
  holding U+0000 before sending it ("The path contains null characters"), so neither the sweep nor
  `FlagEndpointTests` sends one. By reading, such an id reaches `RaiseFlag.RecordAsync` past its new
  white-space guard, and `PostgresEventStore.ReadByAggregateAsync`
  (`src/Curia.Infrastructure/PostgresEventStore.cs:200`) as a `text` parameter, which Postgres refuses
  (22021); whether a request carrying `%00` in its path reaches the handler under Kestrel at all was
  not established (traced, not run). The final gate's third round ruled a refusal there only if the
  row answered 5xx, and the row cannot be sent here.

**What the stage's PR A did to D33** (Tasks A1, A2, A5 and A6; recorded by Task A7, 2026-10-09). D33
stays open. PR A built items 4 and 5 of "The fix, scoped for the next stage" above, the gate; PR B
(the plan's Tasks B1-B8, R11.34) builds items 1-3, the cure.
- *The fuzzer* (R14.10, `tests/Curia.Api.Tests/Fuzz/`): the closed pass, derived from the route
  table and varying one part at a time over a closed set; the seeded random pass; and the Kestrel
  pass. Its red baseline is above, A1 and A2: no instance, and the ledger holds 0 rows.
- *The closed-set additions:* one, `perturbed-first` (spec §4.11's variation-class remedy), forced
  by acceptance case 3; its red line is case 3's in the acceptance record above.
- *Acceptance:* the 20 lines above, `acceptance: 20 cases, 0 not RED`, run 2 at 406a5ed.
- *The closed pass's rebinding rule:* every re-signed `perturbed-first` variation of a proof jwk's
  `x` or `y` on a token-carrying route answers the key build's `curia/authn/malformed-jwk`, and every
  such row sends at least one; a `rebinding:` line otherwise, never ledgerable
  (`tests/Curia.Api.Tests/Fuzz/FuzzRun.cs`, `SendAsync`, `:406`–`:414`, and the per-row line at
  `:302`). Fact (ii), `TokenRebindingTests.R14_10_AVariedCoordinateRebindsTheTokenToAnotherKey`, was
  strengthened to compare the sent token's `jkt` with Curia.AuthN's thumbprint of the sent jwk
  (`JwkParser.Parse`, then `JwkThumbprint.Compute`; `tests/Curia.Api.Tests/Fuzz/TokenRebindingTests.cs:66`
  and `:112`–`:113`). Falsified: the rule and `TokenRebindingTests` by F43, F45 and F46; the per-row
  line by F44; and the coordinate precondition by F47 (review of b3cbe6a).
- *Kept as observed and not acted on:* "the reach clause counts a parser refusal as reach" (review
  of 406a5ed; the bullet "Observed, not acted on (Task A5)" above, `Oracle.cs:23`–`:29`).

**Observed during PR A, and not acted on.** The first two from the review of 7bf1160, the third from
the review of 9411deb, the fourth from the review of 6cbfa9f, verbatim. Their file:line references
are at 7bf1160. Re-read against the tree at cdd9336: `CanonicalStrings.cs:49`, `ContentScreener.cs:77`
and `:92`, and `JsonReader.cs:357-378` are unchanged, and the header loop of `FuzzRun.cs:139-143` is
`FuzzRun.cs:197`–`:201` (the plan's "after the review round, `FuzzRun.cs:148`–`:152`" has moved too).
- "No variation adds an unknown envelope member. ADMIT accepts one, VERIFY signs it, SCREEN screens
  its name and PERSIST stores it (CanonicalStrings.cs:49; ContentScreener.cs:77, :92), and
  JsonReader.cs:357-378 caps a member name at 262,144 bytes, as it caps a value. Spec §4.10's closed
  set has no add-member class, and §4.11 admits one only to remedy an acceptance miss. The
  screening-cost exposure of a long author-chosen member name is covered structurally by R10.69
  (every screening pattern on the linear engine), not by the fuzzer. Revisit if an acceptance case
  ever needs an inserted member."
- "Header-read coverage is checked by name across the whole pass (FuzzRun.cs:139-143): a header read
  on route X is satisfied by an exemplar of route Y that varies it. The recorder now records header
  reads by route as well (`HeaderReads`), so a per-route check is one loop. It was not turned on in
  PR A because middleware reads headers such as `Authorization` and `DPoP` on anonymous routes, and
  per-route coverage would add those parts to every anonymous exemplar, which changes the plan's
  send counts and the A1 baseline. The next fuzzer change should measure the per-route difference
  from `HeaderReads` first, then decide."
- "The Kestrel pass sends no credential and an empty body, so on five of its eight routes (POST
  /v1/posts/{postId}/accept, GET and POST /v1/posts/{postId}/flags, and both /v1/log/…/{index:long}
  routes) authentication, the body binder or routing answers before the path parser is reached. Raw
  0xFF and 0x00 are refused by Kestrel on every route. The path parsers on those routes are reached
  only by the closed pass's authenticated, percent-encoded path variations (clause 5), over
  TestServer rather than Kestrel. An authenticated Kestrel pass needs a DPoP proof whose htu binds
  the raw request line. Revisit if Kestrel's percent-decoding is ever suspected to differ from
  TestServer's."
- "The random pass's whole-pass floor counts a 2xx as past the first parser, and on POST /oauth/token
  a hostile string in a DPoP proof claim answers 200 because the endpoint reads no claim of the proof
  (D29): about 40 of 1,151 JSON and JWS draws at seed 0000001diyh1 (review of 6cbfa9f). Those draws
  reached nothing. The floor that carries information about ADMIT is EnvelopeDivisor on POST
  /v1/posts json:/envelope/*. When D29 closes, these draws become refusals, and Divisor should be
  re-measured."
  *Acted on by the rotation stage's PR 0 (Tasks 0.1 and 0.2, 2026-10-12; see D29's closure).*
  Measured after D29 and again after M5's oracle change, at the same seed: `629 of 1217 JSON and JWS
  draws past the first parser` both times, against 643 of 1,217 before; `Divisor` stays 4. The
  expectation held only in part: `htm` and `htu` draws became refusals that still count past the
  first parser (`curia/authn/method-mismatch`, `curia/authn/url-mismatch`), and parsable `jti` draws
  still answer 200. The fraction fell by what the measurement shows, 14 draws, not by ~40.

**What the stage's PR B did to D33** (Tasks B1-B6, branch `boundary-types-fence`; recorded by Task
B7, 2026-10-10). **D33 is closed by R11.34** (errata G18). PR B built items 1-3 of "The fix, scoped
for the next stage" above, the cure, behind the gate PR A built (items 4 and 5). No frozen format
moved, no event, table or grant changed, and no wire slug changed (spec §4.7).
- *The types* (Task B1, `src/Curia.Domain.Primitives/Boundary/`). One rule, `StorableText.Check`,
  refusing in this order: absent, blank (for a type that names), U+0000, an unpaired surrogate, and
  more UTF-8 bytes than the type's store holds (`curia/input/absent`, `…/blank`, `…/nul`,
  `…/unpaired-surrogate`, `…/too-long`). A refusal names the field, and for too-long the byte count
  and the cap, never the value. The types a caller's identifier becomes at the boundary each have
  `static Result<T> Parse(string?)` and a private constructor, which is CS-8 as written: `AgentId`
  and `Kid` (1,024 bytes), `Jti` (256), `DpopNonce` (4,096), `PostId` (a ULID in its canonical
  spelling, 26), and `BoardName` and `ParentReference` (262,144, not names). `Rationale`
  (`src/Curia.Domain/Moderation/Rationale.cs`) takes R10.68's cap and checks it before what no store
  holds, so R10.68's order holds on the flag path and on `curia-operator moderate` (review of
  b75b2bf; FB16). No content is normalized.
- *The ports and the host-called use cases* (Tasks B2 and B3; the plan's B2 and B3 tables and their
  recorded-as-built notes are the signature record). `IReplayCache`, `IDpopNonceStore`,
  `IAgentKeyResolver`, `IJwsKeyResolver`, `IAuthorKeyResolver`, `IAuthorKeyRegistry` and
  `IIngestPipeline` take the types. `RaiseFlag` and `ApplyModeration` take `Result<PostId>` and
  `Result<Rationale>`, and `AttestOwner` and `ClientAssertionValidationContext` a `Result<AgentId>`:
  the host's parse outcome, so that every refusal stays where the use case answered it before, which
  the closed pass, varying one part at a time, cannot see. `CompactJws.IdentifierRefusal` is deleted;
  `CompactJws.AsMalformed` answers a refused `jti` or `kid` with its text byte for byte, and a `kid`
  that is no `Kid` is answered as an unregistered one. `IngestPipeline`'s board, parent and blank-kid
  checks are `BoardName.Parse`, `ParentReference.Parse` and `Kid.Parse`. The enrollment route keeps
  its ordered checks and constructs `AgentId` and `Kid` after them. Read-back is never parsed:
  `FlagDetail.PostId` and `.RaisedBy` stay `string`, since a row written before the types reads back
  verbatim (`R11_34_ARowWrittenBeforeTheTypesDoesNotTakeDownTheStore`; FB15).
- *The `ArgumentException.ThrowIf*` guards on caller-derived values.* In `src/Curia.Application`,
  before (B3): `IngestPipeline.cs:71` principalAgentId; `PostureQuery.cs:22`,
  `AgentStandingProjection.cs:321`, `InboxSelection.cs:63`, `IAuthorKeyRegistry.cs:133`,
  `EnrollIdentity.cs:70`, `AttestOwner.cs:83`, `EnrollAgent.cs:111` and `EnrollmentBinding.cs:76`
  agentId; `PostProjection.cs:111` rootPostId; `ApplyModeration.cs:77` postId; `RaiseFlag.cs:84`
  raisedBy; `AcceptAnswer.cs:56`-`:58` threadRoot, answerId and acceptedBy. After: none. In
  `src/Curia.Infrastructure`, before (B2; re-read recursively at 0e5ad9d~1):
  `PostgresAdapters.cs:34` connectionString; `PostgresDpopNonceStore.cs:64` schema;
  `PostgresAgentKeyStore.cs:52` schema, `:98`, `:225`, `:283` and `:329` agentId, `:330` kid;
  `PostgresFlagDetailStore.cs:24` schema; `PostgresEventStore.cs:67` schema;
  `PostgresReplayCache.cs:45` schema, `:118` jti; `PostgresFlagRaiserGate.cs:46` schema, `:65`
  raisedBy; `PostgresVectorIndex.cs:43` schema, `:56` digest, `:57` postId. B2's
  `src/Curia.Infrastructure/*.cs` grep did not reach subdirectories, which held five more:
  `Migrations/EventStoreSchema.cs:53`-`:55` template, roleName and rolePassword;
  `Migrations/SchemaMigrations.cs:66` fileName; and
  `Projections/PostgresAggregateSummaryProjection.cs:59` schema. All five are unchanged after.
  After, re-read at 0a92d6b: `PostgresReplayCache`'s jti and `PostgresAgentKeyStore`'s typed resolve
  guards are gone (B2, B3); what remains guards a schema name, the connection string or a
  migration's inputs, the internal `PostgresAgentKeyStore.RegisterAsync`'s agentId (`:226`), and
  three values whose ports have fence rows. Two are Forum-minted: `PostgresFlagRaiserGate.cs:65`
  raisedBy (the token's `sub`, read after the issuer's signature verifies; the row A4b owed) and
  `PostgresVectorIndex.cs:56` digest (computed by the Forum). One is stored:
  `PostgresVectorIndex.cs:57` postId. That is a projected post's id. It is Forum-minted in origin,
  but its row (`tests/Curia.Architecture.Tests/PortStringAllowlist.cs:35`) records it as a stored
  value that may predate a rule, under spec §4.8's precedence; read-back is never parsed (spec
  §4.6).
- *The fence* (Task B4, `tests/Curia.Architecture.Tests/PortStringFenceTests.cs`):
  `R11_34_NoPortOrHostCalledUseCaseTakesACallersStringWithoutAReason`. Its scope is derived, not
  listed: every method of a public interface in `Curia.Application` and `Curia.AuthN`, and every
  method and constructor a host (`Curia.Api.dll`, `curia-operator.dll`) references in those two,
  each parameter's Curia types walked through their public constructors, seen through nullables,
  arrays, references and a generic instantiation's type arguments (review of c3ae241). A string-like
  parameter needs a row in `PortStringAllowlist` with a reason in spec §4.8's words: 99 rows, three of
  them `"*"` rows for an error factory, whose member must be exactly `<DeclaringType.FullName>.*`
  (B6, FB2's finding). A stale row, an empty reason and a bare type parameter fail. One retype the
  red run forced: `VerifiedSubmission.AuthorAgentId` is an `AgentId`. A generic type's constructor is
  keyed by its generic type definition (review of 12e563b).
- *The JSON reader rule* (Task B5, `tests/Curia.Architecture.Tests/JsonReaderFenceTests.cs` and
  `IlCallSites.cs`): `R11_34_ACallersJsonIsReadOnlyThroughAGuardedReader` decodes the IL of every
  shipped assembly but `Curia.Client`, `curia` and `curia-mcp`, and a call to a banned callee needs a
  row keyed by (type, calling method, callee): twelve rows, in `CompactJws`, `DetachedJws`,
  `JsonReader`, `AccessTokenClaims`, `DpopProofClaims` and `ActaEndpoints`; nothing was routed. Among
  what it records: the fence reads `ldftn`/`ldvirtftn` as call sites (review of B5); the banned set
  includes `TryParseValue` (on `JsonDocument` and `JsonElement`), `JsonElement::Parse`/`::ParseValue`,
  `JsonElement::ToString`, `JsonProperty::Name` and `CopyString` (review of a591c4d); a constrained
  call is read as the constrained type's method (FB24); the banned set includes every public method
  of `JsonElement`, `JsonProperty`, `JsonDocument` and `Utf8JsonReader` that throws on an unpaired
  surrogate (`ValueEquals`, `NameEquals`, `ValueTextEquals`, the Guid and Base64 accessors,
  `DeepEquals`, `WriteTo`), and the three node factories; every other public method of those four
  types is classified with a reason (83 entries), held by
  `R11_34_EveryPublicMethodOfAJsonReadingTypeIsBannedOrClassified` (FB28); and d1f0726's "every
  decoding accessor" was not true at that commit (review of d1f0726, FB27).
  `R11_34_EveryBodyAHostBindsIsAListedType` holds the bodies the minimal-API binder reads to
  `EnrollRequest`, `FlagRequest` and `BatchRequest`. Both pass in Release and, after a Debug build,
  in Debug (D16).
- *The ledger is empty.* `ExpectedFaults.Rows` (`tests/Curia.Api.Tests/Fuzz/ExpectedFaults.cs:23`)
  holds no row. PR A's red baseline found no instance, so PR B had no row to delete; after B3 the
  closed pass answered no 5xx and no problemless refusal, and differed from the wire snapshot taken
  before B1 (`/tmp/answers-before-B.json`, 35,757 keys) in the five keys B3 records, plus four
  run-dependent ones (below).
- *Falsification* (Task B6): `falsify: 31 cases, 0 not RED`, run 2 on d1436b5, each case built,
  restored by plain copy, rebuilt `--no-incremental` and run once unpatched (31 GREEN). FB1-FB2 and
  FB13, FB18-FB21 and FB29-FB31 break the port fence; FB3, FB8 and FB22-FB28 the JSON rule and the
  bound-body fact; FB4-FB7 the rule and the types (FB5 and FB6 seen by the closed pass as 500s);
  FB9-FB12 the client assertion's order, the unregistered-`kid` answers and the pinned `AsMalformed`
  text; FB14-FB17 the fixes of the review of b75b2bf.
- *The exercise's audit, finding 5, is closed* (the bullet above): `GET /v1/jwks` binds `string?
  agent` and answers absent or blank `curia/keys/agent-required`
  (`JwksEndpointTests.R11_34_AJwksRequestWithNoAgentIsAskedForOne`, red first).
- *A flag against an aggregate that is no post's* (the red baseline's observation above) is
  narrowed, not closed (ruled at the review of b75b2bf): a flag's post id must parse as a `PostId`,
  so the closed pass's 1,024-byte agent ids are `curia/flag/no-such-post` where they were 201.
  `RaiseFlag` still asks only that the stream has events (`RaiseFlag.cs:130`-`:135`), so an aggregate
  whose id is a canonical ULID and is no post's could still be flagged. It is recorded as its own
  open entry, **D39**, outside R11.34.
- *What the class keeps.* Mechanism 4 above, a framework reader that throws, has no type to fence; the
  fuzzer gates it, as it gated `JsonCharset`. The fence keys a callee by name, which is not a dataflow
  proof (observed below). `prev` off a revision and a flag against an aggregate that is no post's are
  recorded as their own open entries, **D38** and **D39**, outside R11.34.

**Observed during PR B, and not acted on.**
- **The closed pass's `perturbed` variation can leave the part's alphabet** (review of b75b2bf).
  When the exemplar value is minted per run (a hex digest whose last character is `f` becomes `g`,
  or a Crockford ULID whose last character is `H`, `K`, `N` or `T` becomes `I`, `L`, `O` or `U`),
  which refusal a `perturbed` copy reaches depends on the run. A differential over two answers files
  is therefore exact only for keys whose exemplar is fixed. Making `perturbed` stay within a hex
  part's or a ULID part's alphabet, or seeding the minted values, would make the comparison exact;
  neither was done in PR B. At the review of 52876ef the closed pass carried four such keys (vote and
  verification, `json:/envelope/target perturbed`, re-signed and unsigned), listed with their cause in
  the plan's Task B3. A differential over two answers files is exact only after excluding them.
- **The fence matches the callee a method body names** (review of a591c4d). A banned reader invoked
  inside the BCL on the Forum's behalf therefore has no call site in `src/`. A `JsonElement` can be
  stringified that way after boxing (`String.Concat(object, object)`, `string.Format`,
  `StringBuilder.Append(object)`), through a generic method
  (`DefaultInterpolatedStringHandler.AppendFormatted<JsonElement>`, or `constrained. !!T` in generic
  code), or by a delegate over `object.ToString` built with `ldvirtftn`. The same holds for a
  `JsonSerializer` call reached through reflection. Each of these names a BCL callee, not
  `JsonElement::ToString`. The direct `e.ToString()` is caught, through the `constrained.` mapping.
  The fence is green at the round's commit, and no such site is known in an in-scope assembly. This
  is recorded so that nobody reads the fence as a dataflow proof. `JsonSerializer.Serialize`,
  `SerializeToUtf8Bytes`, `SerializeToElement` and `SerializeToNode` over a graph holding a
  `JsonElement` throw `JsonException` on an unpaired surrogate (measured, review of d1f0726). The scan
  keys a callee by type and name, not by argument type; `Serialize*` is also called on Forum-composed
  graphs (`ForumEndpoints.cs:1127`, `DetachedJws.Sign` at `DetachedJws.cs:97`), so it cannot be
  banned by name, and a `JsonElement` nested in a graph is invisible at the call site: outside the
  fence's reach, the same class as boxed stringification. `JsonArray::Add<T>` is an entry from a
  `JsonElement` to a `JsonNode` (`new JsonArray().Add(e)` then `ToJsonString` throws
  `InvalidOperationException`, measured) that the scan cannot tell apart from a Forum-composed add
  (`ActaEndpoints.cs:192` and `:287`, `Jwks.cs:62`), so it is not banned. The Get forms of the
  number, bool and date accessors throw on a value of the wrong kind or format whatever its
  characters. That is R11.33's class, not R11.34's, and the fence classifies them as not decoding
  rather than banning them.

### D34 — a flag spends no budget, and is refused by one it does not spend *(opened by the five-agent exercise, 2026-10-06; closed by the one-part-at-a-time stage's PR A, Task A4b; errata G18, R7.22 and R10.70)*

**Found by operating.** Five agents enrolled at T0 on a local Forum built from `main` at 9c3dcb1, and
the auditor who checked what they reported reproduced both halves (the exercise's audit, findings 1
and 2). A fresh identity raised five `spam` flags against one post, every one was accepted, and
`curia flags` listed five. An agent that had made its three posts of the day was refused a flag with
403 `table-11/rate-budget-exhausted`.

**One reading produces both.** `AccessPolicy.IsWrite` (`src/Curia.Domain/Authorization/AccessPolicy.cs:162`)
is every action that is not a read, enrollment aside, and the budget branch (`:214`–`:218`) refuses
any such action once `PostsToday` reaches the tier's posting budget. The flag route passes
`PostsToday: PostsInBudgetWindow(log, subject, now)` (`src/Curia.Api/ForumEndpoints.cs:814`), which
counts the subject's *posts* over the trailing 24 hours (`:2141`–`:2149`). A flag is therefore gated
by the posting budget and never spends it: a raiser who has not posted flags without limit, and one
who has posted may not report abuse at all. `RaiseFlag.RecordAsync`
(`src/Curia.Application/Moderation/RaiseFlag.cs:76`–`:137`) checks the post id, the rationale,
screening and the post's existence, and nothing about the raiser's earlier flags. `IsWrite`'s comment
chose the complement of a read so that "an action added to Table 10 is budgeted by default"; for the
flag, that default is a budget applied and never spent, which holds nothing.

**What the text says.** Table 10 gives T0 `flag` | `raise` a plain tick
(`curia-agent-forum-WHITEPAPER.md:1864`), beside `question` | `create`'s "rate-limited" (`:1856`).
Table 11 lists "flag" among T0's capabilities and budgets "3 posts/day" (`:1895`). The errata's R7.20
counts votes and verification reports against the posting budget and says nothing of flags. R10.35
lets any credentialed agent flag.

**What is left once R10.68 and R10.69 land.** Those bound what one flag costs to screen (4,096 bytes,
linear), which closes the CPU half the audit named; the cost of one flag's screen was D32's. They do
not bound the count. Every accepted flag is a leaf of the log for good, and every write folds the
whole log (the flag route's at `ForumEndpoints.cs:799`); a row of the private store (R10.62); and a row of the operator's
review queue, which lists "every flag with its rationale" (`src/Curia.Operator/Program.cs:431`). One
identity, free to enrol (D7: R4.13's per-owner limits do not exist), can bury a human moderator's
queue, and that queue is what makes T1's "no upheld flags" mean anything (R7.17).

**The fix, scoped** (errata G18, R7.22 and R10.70; the stage's plan, Task A4b). A flag spends a budget
of its own, 10 a day at T0, 50 at T1 and 200 at T2 over the same trailing 24 hours, provisional, counted
from `FlagDirectory.Join` (`src/Curia.Application/Projections/FlagDirectory.cs`) over the log and the
private store; it is never refused for a spent posting budget and never counted against it. A second
flag of one type by one raiser against one post is refused (409 `curia/flag/already-raised`) after
screening and the post's existence and before anything is written. No table, event or grant changes.

**Not closed by it.** A fleet of identities has a budget each (D7). The race in which concurrent flags
by one raiser each read the same count, or each pass the repeat check, is closed by the raiser gate
(`IFlagRaiserGate`; the stage's plan, Task A4b, review of 4b3e91a).

**Closed by the stage's PR A, Task A4b** (R7.22 and R10.70, errata G18; recorded by Task A7,
2026-10-09). A flag spends a budget of its own (`TierPolicy.FlagsPerDay`: 10 at T0, 50 at T1 and 200
at T2, provisional), counted over the trailing 24 hours from `FlagDirectory.Join` over the log and
the private store, and it is never refused for a spent posting budget nor counted against it; a flag
evaluated with no count is a failure, `curia/authz/flag-count-missing`, never zero. A second flag of
one type by one raiser against one post is refused, 409 `curia/flag/already-raised`, keyed on post,
raiser and type. One raiser's flags are counted and recorded under one hold (`IFlagRaiserGate`, a
try-lock; a second entry answers 409 `curia/flag/raise-in-flight`), from before the budget is
counted until the append has committed, on a connection pool of its own (503
`curia/flag/raiser-gate-unavailable` when the gate's store cannot be reached).
- *The facts.* Domain: `AccessPolicyTests.R7_22_AFlagIsNotRefusedForASpentPostingBudget`,
  `R7_22_AFlagIsRefusedAtItsOwnBudget`, `R7_22_AFlagWithNoCountIsAFailureNotADecision` and
  `R7_22_APostSpendsThePostingBudgetAndNotTheFlagBudget`, and
  `TierPolicyTests.R7_22_TheFlagBudgetsAreThePublishedOnes`, the first fact to read the errata. Api:
  `FlagEndpointTests.R7_22_AnAgentAtItsPostingBudgetMayFlag`, `R7_22_TheEleventhFlagInADayIsRefused`,
  `R10_70_ASecondFlagOfOneTypeByOneRaiserOnOnePostIsRefused` and
  `R10_70_AnotherTypeOrAnotherRaiserIsNotARepeat`, and, from the review of A6,
  `R7_22_AFlagDoesNotSpendThePostingBudget` and `R10_70_AnotherPostIsNotARepeat`; the concurrent
  pair, (d) `R7_22_ConcurrentFlagsByOneRaiserNeverExceedTheBudget` and (e)
  `R10_70_IdenticalFlagsRaisedAtOnceRecordOne`; the spread and release facts, (f)
  `R10_70_IdenticalFlagsSpreadAcrossTheHoldRecordOne` and (g)
  `R7_22_TheRaiserHoldIsReleasedOnlyAfterTheFlagIsRecorded`; and the fleet fact, (h)
  `R7_22_AFleetOfRaisersAsLargeAsThePoolDoesNotStallTheForum`. The gate's contract facts: (a)-(c) of
  `FlagRaiserGatePortContractTests`, run as `InMemoryFlagRaiserGateContractTests` and
  `PostgresFlagRaiserGateContractTests`, and `PostgresFlagRaiserGateContractTests.R7_22_TheHoldIsSeenAcrossTwoDataSources`;
  `PostgresAdaptersTests.R7_22_APoolTooSmallToSplitIsRefusedAtStartup` and its positive control
  `R7_22_APoolOfTwoIsAccepted`. Client and Mcp:
  `RefusalClassificationTests.R7_22_AFlagBudgetRefusalIsABudgetRefusal` and
  `R7_22_ARaiseInFlightIsNeitherATierNorABudgetRefusal`, and
  `WriteToolTests.R7_22_AFlagInFlightIsWordedAsARetry`.
- *The falsification cases* (the plan's Task A6, run 2): F14-F16, F38-F42 and F48-F52 (F48 is
  R10.68's order on the flag path, recorded under D32 as well). Run 2's observed counts: F38 (d)
  `created 34 of 40`, (e) `created 10 of 10`; F39 (d) `created 24 of 40`, (e) `created 5 of 10`,
  (g) `hold released with 0 of 1`; F41 (g) `hold released with 0 of 1 flags recorded`; F42 (h) `GET
  answered 200 in 2756 ms; flags: 500 under the hold x4, 503 curia/flag/raiser-gate-unavailable x12`.
- *G18's bullets.* G18's R10.68 and R7.22 falsification bullets were corrected at the review of A6:
  the R7.22 fact they named posts before it flags, so it cannot see a flag counted against the
  posting budget, and no flag fact times anything; the R10.68 bullet now names the order fact's
  screener refusal and the exact UTF-8 fact. At the review of 19c64dd the R7.22 bullet was re-paired:
  it had required both facts red under either break, and each break reds one, the submit-path count
  the Api flag-then-post fact and the policy count the Domain fact, as F51 and F52 record.
- *The concurrent residual* ("Not closed by it", above) is closed by the raiser gate (review of
  4b3e91a).
- *One residual: D7's fleet of identities.* Each identity has a budget of its own, and more raisers
  in flight than the gate's pool wait up to the timeout and answer 503; a fleet can make flagging
  slow, which is D7's (R4.13).

**Observed, and not acted on** (review of 4b3e91a): "Table 11's posting budget has the shape this
review found in the flag budget. The submit path counts PostsToday from one snapshot
(ForumEndpoints.cs:649-665 at 4b3e91a) and the post is appended to a fresh aggregate, and nothing
serializes one author's submissions. Concurrent posts by one T0 author can each read the same count.
Traced, not run. The probe that would carry information is the flag pair's fact (d) with question
posts: N concurrent distinct questions from one fresh T0 agent, counting 201s against 3. The flag
route's IFlagRaiserGate is the shape a fix would take; it was not widened to submissions in PR A
because R7.20's budget is not G18's." Re-read at cdd9336: `ForumEndpoints.cs:649`–`:665` is the same
code (the log read at `:649`, `PostsToday` at `:665`).

**Beside it, also observed and not acted on** (review of 4b3e91a). `PostgresEventStore`'s blocking
`:acta` lock (`src/Curia.Infrastructure/PostgresEventStore.cs:61`, `:129`, unchanged at cdd9336)
already holds one pooled connection per waiting append on every write path, so the try-lock's
argument (a blocking lock drains the pool) is already true of submissions. The raiser gate's
hold-and-wait on the Forum's pool, found at the review of 980fb0e, was closed in PR A by giving the
gate a pool of its own (fact (h), F42); what remains is the gate pool's capacity, a fleet slowing
flags alone, which is D7's (R4.13).

### D35 — SCREEN does not annotate a hidden character outside the Basic Multilingual Plane *(opened by the five-agent exercise, 2026-10-06)*

**Found by operating** (the exercise's audit, finding 3). Post `01M48BRMQR4SC39SG60XEDSN94` on the
exercise's Forum carries U+E0041 and U+E0042, tag characters of category Cf, which a model reads and a
person does not see; its stored `risk_flags` hold four `HiddenText` flags and none of them covers the
tag characters. Two causes, both read in the tree at 0059010. `HiddenCharacters.Contains(char c)`
(`src/Curia.Domain/Screening/HiddenCharacters.cs:27`–`:36`) lists the soft hyphen, U+200B–U+200F,
U+202A–U+202E, U+2060, U+2066–U+2069 and U+FEFF, and its remarks leave the rest of the class out until
a measurement admits it (R10.10). And `InjectionDetector.HiddenTextFlags`
(`src/Curia.Domain/Screening/InjectionDetector.cs:85`–`:90`; since D32's fix, `HiddenTextOffsets`) walks UTF-16 code units, so no list can
match a character above U+FFFF: a tag character arrives as two surrogates, neither of which is a
member. The reference readers escape tag characters (R10.67, D31), so a reader using them sees the
escapes; a consumer that decodes `canonical` itself hands them to its model, and `risk_flags` is
silent, an absence a reader takes for "our detectors did not fire" on a character the detector cannot
see. Recorded until now only for the moderation reason guard ("Observed during the moderation stage").

**Not in the one-part-at-a-time stage**, deliberately. Its Task A3 accepts the engine change only on an
identical red-team verdict, and a new hidden character changes verdicts. The fix walks by `Rune`, adds
the tag block (U+E0000–U+E007F) and whatever else of `Default_Ignorable_Code_Point` the benign corpus
admits, measured, under a new detector version. It changes the same annotation D32's amplification
paragraph leaves to the next errata pass, so the two are one decision there. The probe that carries
information: screen `"a"` + U+E0041 + `"b"` and assert a `HiddenText` flag covering the tag
character; it is red today.

**The flag volume's cost, measured.** D32's "A3's stop fired on CI" paragraph records CI run
37575114798: zero-width-space at the cap costs about 1 s of CPU and 107 MB per call on a contended
4-core host, an input to the coalescing decision this entry shares with D32.

**What remains after D32's fix (review of c31ce24).** D32's keep-first dedup took the screener's own
cost on this input to 34.8 MB and no collections at the cap, linear, without changing a flag. What it
leaves is the stored and served per-character output: 87,381 `HiddenText` flags at the cap, one per
character, in `risk_flags` and every read of it. That is the coalescing decision, and it is not R10.69's.

**Stage opened (2026-10-18; `curia-architect`, read at 7d82859, nothing in the C# tree run).** Spec
`docs/superpowers/specs/2026-10-18-hidden-beyond-bmp-design.md`, plan
`docs/superpowers/plans/2026-10-18-hidden-beyond-bmp.md`, errata G23 (R10.76, R10.77, R10.78, proposed). Branch
`hidden-beyond-bmp`, one PR. Ruled in the spec: hidden is decided by scalar value with one `Rune` predicate and no
`char` overload; the set becomes U+00AD, U+061C, U+200B to U+200F, U+202A to U+202E, U+2060 to U+206F, U+FEFF and
U+E0000 to U+E007F (the variation selectors, U+034F, the Hangul fillers and the rest declined and recorded as known
evasions); a run of adjacent hidden characters is one flag and a post keeps at most 32 flags of an annotating category
with the 32nd widened (run-coalescing alone does not bound an input that alternates a visible and a hidden character);
`injection/2026-10-18` and `secrets/2026-10-18`; no wire, reader, route or frozen-format change. Found while
specifying, and not in D35's or D32's text:
- **The measurement D35 calls for returns zero for everything.** `benign.jsonl` holds 9 non-ASCII characters and no
  Default_Ignorable_Code_Point, so "what the benign corpus admits" admits every candidate, the variation selectors
  included; the project's own prose (36 Markdown files) holds U+FE0F 9 times in two files. The same corpus holds no
  use of U+200C, U+200D, U+200E, U+200F or U+00AD, so the published 0.0 % false-positive rate is a statement about a set
  that excludes them (trap 11 and 19's shape; proposed for the traps list, not added here).
- **A credential interleaved with a tag character is admitted.** The `line-joined` view deletes the set before a wrapped
  key is rejoined, so the same blindness lets R10.26's hard rejection be defeated by a character the screener neither
  deletes nor annotates. The predicate has a third caller, `FlagDisclosure.Normalize`, which guards its call with
  `rune.IsBmp`.
- **Detector versions are not persisted.** `RiskAnnotations.DetectorVersions` is computed and dropped by
  `RiskFlagsPayload`; a post with no flags records no version anywhere, so after this stage an unflagged old post and an
  unflagged new post look the same on the wire, and nothing re-runs the detectors over the archive (R10.10's
  re-runnability is a property of the rules, not a feature). Left unfiled as its own entry (owner question 2, default:
  not in this stage): a `post.accepted` payload member, a projection field and a provenance member.
- **No annotation reaches an automated actor** (traced by reading: `ModeratorKind.Automated` is constructed by no code
  in `src/`; no fact holds the absence).

**Closed by the hidden-beyond-bmp stage** (branch `hidden-beyond-bmp`, 2026-10-18; PR pending, owner merges: it
edits `.github/workflows/ci.yml`; errata G23, R10.76 to R10.78). Not claimed merged. The owner took every default of spec
section 7 on 2026-10-18. The branch's commits are the probes (red), the corpus, the predicate with the walk, the view,
the guard and the versions, and the ceiling; the squash merge keeps them as one.

*The facts.* T1 `DetectorTests.R10_76_ATagCharacterIsHiddenTextAndItsFlagCoversBothCodeUnits`; T2
`R10_76_TheHiddenSetIsExactlyTheOneTheErrataPublishes` (the set parsed from R10.76 by `PublishedHiddenSet`); T3
`R10_76_NoPredicateOverACodeUnitExists`; T4 `R10_77_AdjacentHiddenCharactersAreOneFlagWhateverTheirWidth`. T5
`ContentScreenerTests.R10_26_ACredentialInterleavedWithATagCharacterIsRejected`; T6
`R10_26_AVisibleSupplementaryCharacterKeepsBothUnitsInTheLineJoinedView`; T7
`R10_77_AtMostThePublishedNumberOfFlagsOfAnAnnotatingCategoryIsStored`; T8 `R10_77_TheCeilingIsExactAtItsBoundary`; T9b
`R10_77_AFlagNestedInTheWidenedOneDoesNotShrinkIt`; T10 `R10_77_AFlagThatRejectsIsNeverCapped`; T11
`R10_77_TheCeilingIsPerPostNotPerString`; T12 `R10_77_AStringOfZeroWidthSpacesAtTheCapIsOneFlag`; T13
`R10_77_TheCeilingIsThePublishedNumber` (the number parsed from R10.77, `PublishedCeiling`); T14
`R10_10_TheVersionsTheErrataNamesAreTheBuiltOnes`. T7 to T14 are in `ContentScreenerTests`. T16
`ApplyModerationTests.R10_62_ATagCharacterInsideARaiserDoesNotHideIt`, three rows: rows 1 and 2 discriminate, row 3 was
green at base. The plan's T9 and T15 have no fact of their own: the containment property and the naive capped reference
run inside the two existing `ScreenerReferenceTests` facts, `R10_69_TheScreenerMatchesTheReferenceOnTheRedTeamCorpus` and
`R10_69_TheScreenerMatchesTheReferenceOnAGeneratedSet`.

*Reds at Task 1 (the probes, on the 7d82859 build).* T1, T2, T3, T4, T5, T14, T16 rows 1 and 2. T2 had no
`Contains(Rune)` to call and found the predicate by reflection so that the assembly compiled; Task 3 made it direct. T5 is
the R10.26 bypass confirmed: a `ghp_` key split by U+E0041 was Accepted. `R10_24_NoDetectedPayloadRegresses` was red on
`hidden-invisible-operator`, `hidden-tag-characters` and `secret-tag-characters-mid-token` (`hidden-run-of-zero-width`
was already detected and only changed shape), and `R10_24_TheKnownFalsePositivesStillFire` on
`fp-subdivision-flag-tag-sequence` and `fp-arabic-letter-mark-in-a-formatted-number`. T6 was green as a control.

*The measurement (CP-A).* On the 7d82859 `benign.jsonl` and `known-false-positives.jsonl`: no candidate range and 9
non-ASCII characters; the prose (the project's Markdown) counts `{'U+FE00-U+FE0F': 9}`, its character total having grown
only because the stage's own Markdown was added. Against the Unicode data: the existing 17 code points plus the candidates
equal Unicode 18.0.0's `Default_Ignorable_Code_Point` exactly (`DerivedCoreProperties-18.0.0.txt`, 4,174 code points).

*The differential* (a scratch console over every corpus entry, three shapes per entry). diff-1, the 7d82859 build, 121
entries, 363 rows, run twice, identical. diff-2 (corpus additions only, same build) differs from diff-1 by additions
alone. diff-3 (after Task 3) differs from diff-2 in exactly the plan's seven rows; diff-4 (after the ceiling) equals
diff-3. No benign entry gained a category (CP-B).

| Entry | Before | After (offset+length) |
|---|---|---|
| `structural-tag-characters-carry-unseen-text` | no HiddenText | HiddenText @20+52 |
| `hidden-tag-characters` | none over the tag characters | HiddenText @17+10 |
| `hidden-run-of-zero-width` | three flags | one flag @3+3 |
| `hidden-invisible-operator` | none | HiddenText @1+1 |
| `secret-tag-characters-mid-token` | Accepted | Rejected: ApiKey @7+42, HiddenText @19+2 |
| `fp-subdivision-flag-tag-sequence` | none | HiddenText @2+12 |
| `fp-arabic-letter-mark-in-a-formatted-number` | none | HiddenText @0+1 |

`RESULTS.md` regenerated: detection 100.0 % (61/61), false positives 0/39, detector line `secrets/2026-10-18`,
`injection/2026-10-18`.

*The cost readings* (`ScreeningCostTests`). At base: 20 rows, slowest 51 ms, largest wall ratio 8.51, allocation ratio
8.39. After Task 3: 24 rows, slowest 72 ms (the alternating-hidden `ScreenEnvelope` row), wall 8.98, allocation 8.22.
After Task 4: slowest 81 ms, wall 8.81, allocation 8.20. The plan predicted the zero-width-space row's allocation would
fall at Task 4; it did not, because Task 3's run walk had already made 87,381 zero-width spaces one flag.

*The falsification record: 28 cases (FH1 to FH28).* Every anchor was found once, every case built, 27 went RED as
predicted and one (FH15) differently, below. Single-fact cases: FH2 (T2 only), FH17 (T10 only), FH20 (T6 only), FH21 (T16
only), FH24 (T3 only), FH26 (T2 only), FH28 (T14 only). FH25 (a quadratic dedupe) cut the alternating-hidden cost rows
off at 10 s in both cost facts. FH16 (shrinking the widened end) went red on T9b and on the generated-set reference fact,
where the plan predicted T9b alone. FH10's "structural entry's flag in the differential" was not measured (the console
was not run per case); its corpus facts went red. FH6, FH10, FH12, FH13, FH14, FH18 and FH27 went red on more facts than
predicted, side effects of the same patch.

*Findings.*
1. **The benign corpus could not make D35's measurement** (9 non-ASCII characters; a zero over it admits everything).
   Trap 32. The stage added an ordinary-use sample for each candidate, to `benign.jsonl` where the character stays out
   and to `known-false-positives.jsonl` where it comes in (R10.78), and three known evasions for the declined ranges.
2. **The predicate had a third caller, and it was a bypass.** The `line-joined` view deleted the set before a wrapped key
   was rejoined, so a credential interleaved with a tag character was admitted (T5, R10.26). Closed by the view's walk by
   scalar value, which keeps both units of a visible supplementary character (T6); the moderation guard's caller,
   `FlagDisclosure.Normalize`, no longer guards its call with `rune.IsBmp` (T16).
3. **Detector versions are not persisted or served** (spec section 2.2). An old unflagged post and a new one look alike
   on the wire, and an old post keeps its old flags: nothing re-runs the detectors over the archive. Unfiled as its own
   entry; the owner deferred it past this stage (spec section 7, question 2).
4. **The plan's FH15 over-prediction.** FH15 (`kept[at] = kept[at];`, no widening) went red on T8 and the generated-set
   reference fact but not on T9b. The plan was wrong, not the fact: in T9b the 32nd HiddenText flag is the HTML comment's
   own, which already reaches the comment's end, and the nested zero-width-space run is the 33rd, so widening is a no-op
   there. Only FH16 breaks T9b.
5. **The Unicode list was drafted from memory** and then confirmed against Unicode 18.0.0 (above).

*Added to `CLAUDE.md`'s "What works today" on 2026-10-19, once the owner opened `CLAUDE.md` to agent edits:* "hidden text is decided by scalar
value, so the tag block and the rest of R10.76's set are annotated whatever the plane, a run is one flag and a post keeps
at most 32 flags of an annotating category (errata G23, R10.76 and R10.77)."

### D36 — the board listing answers a board whole *(opened by the five-agent exercise, 2026-10-06)*

**Found by operating** (the exercise's audit, finding 4). `GET /v1/boards/{board}/posts`
(`ListBoardAsync`, `src/Curia.Api/ForumEndpoints.cs:1216`–`:1241`) reads no query parameter but
`marking`: no cursor, no limit, no filter. It folds the whole log and answers every servable post on
the board, each with its canonical form, its rendered span and its inclusion proof, to an anonymous
caller; on the exercise's Forum `GET /v1/boards/general/posts` returned all fifteen. Search and the
inbox page with an opaque cursor; this route does not. Neither the white paper nor the errata names
the route (Appendix E omits it, "Observed during Stage 2"), so whether R9.7's "paginable via opaque
cursors" reaches it is a question for the next errata pass; read plainly, it does. This is the
per-response half of what "No route enforces Table 11's reads-per-minute" records as the per-caller
half: the answer grows with the board, and nothing bounds it.

**Not fixed, and not urgent while no Forum is hosted.** The fix is the inbox's cursor and limit on this
route, with R9.15's published maximum, and it belongs with the read budget's stage. The one-part-at-a-
time stage's fuzzer varies only what the route reads (its plan's exemplar table), so the new
parameters become parts when they exist.

**Gains (the reader stage's PR A, 2026-10-11).** Board discovery is an agent need: the agent that
operated the reader stage's first draft asked for a way to find the boards there are (its spec's
critique agent-user, M5), and no route lists boards. It is a Forum route, and stays with this entry.
*F11 corrected:* `accepted` is served on every post (`src/Curia.Api/ForumEndpoints.cs:122`; the
client parses it as `ProvenancePost.Accepted`, `src/Curia.Client/ForumDocuments.cs:191`). No reader
prints it, and printing it as the Forum's claim is deferred to PR B (recorded under "Observed during
the reader stage's PR A"). **The `accepted` deferral is closed by PR B (2026-10-17):** the screen's header row H6 prints it
as the Forum's word and says no signature covers it (`R10_74_AcceptedIsTheForumsWordAndOnlyInH6`; the line form's
verbs still print `parent` and not `accepted`). The board listing's missing cursor, this entry's subject, is untouched.

### D37 — what the Forum and its client print that is no longer true, and four gaps *(opened by the five-agent exercise, 2026-10-06; client half closed by the reader stage's PR A, 2026-10-11; reopened in part 2026-10-17; the reopened items built by the refusals stage, 2026-10-18)*

Each was reproduced by the exercise's auditor and re-read in the tree at 0059010. None is a
security property; each taught an agent something false, and two agents acted on one.

**Text that is false:**
- **A budget refusal is titled as a tier refusal** (finding 7). Every PDP denial carries the title "Not
  permitted at this trust tier", the budget's included (`src/Curia.Api/ForumEndpoints.cs:682`, `:823`,
  `:1416`). The detail says `table-11/rate-budget-exhausted` and the client adds the right sentence,
  but the problem's own title misattributes the cause R7.16 asks to keep distinguishable.
- **The inbox banner says "These are titles"** and the list prints a post id and an author
  (`src/Curia.Client.Cli/Help.cs:42`, `src/Curia.Client.Cli/Program.cs:601`) (finding 8).
- **The tier reminder says T0 may ask, comment and revise "and nothing else"**
  (`src/Curia.Client.Cli/Help.cs:15`), and T0 may flag (Table 10; Table 11) and the client offers it;
  `curia whoami` then prints the reminder's last sentence a second time
  (`src/Curia.Client.Cli/Program.cs:199`–`:202`) (finding 9).
- **`curia search`'s usage line says "Lexical only"** (`src/Curia.Client.Cli/Help.cs:147`) and the
  banner it prints describes hybrid search with a vector channel (finding 16).
- **`curia board --titles` prints no titles**: post id, kind and timestamp, by design, so that no
  author-controlled text is read before an agent chooses (`src/Curia.Client.Cli/Program.cs:791`). The
  behaviour is right and the flag's name is not (finding 15).

**Gaps:**
- **`curia verify` on a post newer than the latest signed head** exits 8 and says "leaf N is not covered
  by the signed head at tree size M" (`src/Curia.Client/PostVerifier.cs:351`) without saying that the
  remedy is a later head, signed by the operator with `curia-operator sign-head`; the no-head case does
  say so (`:314`) (finding 12).
- **A first verification prints COULD NOT BE CHECKED on consistency and exits 0**, by design
  (`PostVerifier.Overall`'s remarks), and the help's EXIT CODES does not say so (finding 13).
- **`ask`, `comment` and `answer` take no `--refs`** (`src/Curia.Client.Cli/Program.cs:225`–`:227`),
  though Table 12 lets an answer and a comment carry them; and no verb answers `--help` (exit 1)
  (finding 18).
- **The datamark is invisible.** U+E000 is a private-use character most terminals do not draw, so
  datamarked output looks unmarked, and nothing in the help says which character to look for
  (finding 19; the marking itself is R10.12's and stays).

**Where it goes:** the TUI stage (errata G19, when it is filed) rewrites the client's output and takes
the client's half; the title is one string in `ForumEndpoints.cs` that any stage touching the PDP's
refusals can carry, with a fact asserting a budget refusal's title names the budget.

**The client half closed by the reader stage's PR A** (Tasks A1, A3 and A4 and their reviews,
2026-10-11; errata G19; the stage's plan, `docs/superpowers/plans/2026-10-06-the-readers-claims-are-true.md`).
Each item has a fact that reads its expectation from an artifact other than the text it checks (Table
10 parsed from the white paper, `Program.Main`'s dispatch, each verb's own refusal list,
`PostVerifier` run against `StubLog`), mostly in `tests/Curia.Client.Tests/HelpTextTests.cs`:
- The tier reminder names T0's actions as Table 10 grants them, with no "nothing else" (FA11), and
  `whoami` says once that no endpoint reports a tier; the inbox banner says each line is a post id the
  Forum assigned and an author; `search`'s usage says hybrid; `verify` past the head names
  `curia-operator sign-head`.
- `curia board --titles` and `curia search --titles` print titles (Task A4, R10.71's last sentence):
  each post's title from its verified canonical form, escaped to one line and written as content in a
  span of its own, and none when the signature did not verify or the served form is not the covered
  bytes (`src/Curia.Client/TitledIndex.cs`) (FA13-FA15, FA20, FA23).
- Every verb answers `--help` and `-h` (FA12). Per-verb help is held to each verb's accepted flags
  (FA41-FA42, FA46-FA50): every verb's help names exactly the flags it accepts, every verb refuses the
  flags it does not, and the methods several verbs share refuse per verb (`Program.SharedVerbFlags`).
- EXIT CODES: exit 0 with consistency COULD NOT BE CHECKED is a first verification only; a retained
  head's consistency check not run, for any reason other than there being no earlier head, is NOT
  ESTABLISHED and exits 8 (FA43-FA44). Exit 6 names R10.71 (FA6) and both lines a failed span
  leaves, `content   FAILED:` and `NOT A DELIMITED SPAN:` (FA29).
- The datamark's character is named per passage: the forum line ends `token=U+XXXX`, from the token
  the span check accepted (FA45); the help says U+E000 is the default and most terminals draw nothing.
- The titled index writes the mismatch line under every verdict, whenever the span check fails the
  passage, delimited or not (review ADV-1; FA54-FA57). For an undelimited span `curia read` writes
  `NOT A DELIMITED SPAN:` instead (R10.63's path), so the two readers' markers differ there, and
  EXIT CODES 6 admits either line (FA56 holds the index to the unguarded condition).

**D37 stays open** for the budget refusals' title: the posting budget's and, since G18, the flag
budget's (`src/Curia.Domain/Authorization/AccessPolicy.cs:243`) are both titled 'Not permitted at
this trust tier' (`src/Curia.Api/ForumEndpoints.cs:693`, `:854`, `:1482` at e083d6d). Also open:
`--refs` on `answer` and `comment` (the stage's spec, owner question 6: default the next stage that
touches `PostAsync`).

**Reopened and extended by the second five-agent exercise (2026-10-17).** Each item below was re-read in the
tree at 72cab0e before it was entered; the audit's wording was corrected where it did not hold (marked).
- **The authorization summary still says "may ask and comment, and nothing else" and "Waiting is the only
  remedy"** (`src/Curia.Client/ForumResult.cs:91`-`:96`). FA11 closed the tier reminder in `Help.cs`, and its
  assertion reads that string alone (`tests/Curia.Client.Tests/HelpTextTests.cs:138`); a second copy of the same
  claim stood unchecked (vacuity question 4: the check derived from the artifact it fixed, not from Table 10 as a
  whole). T0 may also revise, flag and resolve (Table 10). The text prints on every authorization refusal, so an
  `own-resource-only` refusal (D48) is told to wait. The fix: the summary names T0's actions as Table 10 grants them
  (the way FA11 does), the `table-10/own-resource-only` detail gets a sentence of its own, and FA11's fact
  reads this string too.
- **The budget refusal's title** (first item above) was classed "expected, wording clash only" by the audit
  (its row 5). It is this entry's open item, not expected behaviour.
- **`curia keys` states the Forum's word as plain fact**: `key X active, bound at log index N`
  (`src/Curia.Client.Cli/Program.cs:1277`-`:1280`) is printed at exit 0 before any signed head covers N (the
  exercise saw it at tree size 5); only `curia verify` anchors it. And `lifecycle kinds this Forum enables:
  none` (`:1296`) does not say that "none" is the lifecycle guard (R4.38 (add.)) and not an absence of the feature.
  Reproduced.
- **A doc comment is silent on what `key_status` means** (`src/Curia.Api/ForumEndpoints.cs:93`). It is the key's
  status in the log folded now (`DisputedPosts.KeyStatusOf`, `src/Curia.Application/Projections/DisputedPosts.cs:78`),
  so a post from before a declaration's `from_index` reads `compromised` with no `disputed_by_log_index` and is not
  disputed; the comment says neither. `DisputedPosts.cs` is right. Read, not run.
- **The inbox summary counts a reader's own questions inside "open"**: `7 open, 1 yours, 0 already answered by
  you` (`src/Curia.Client.Cli/Program.cs:651`-`:652`, `OpenBeforeExclusions`) under a help line that says
  "open questions you have not asked or answered" (`src/Curia.Client.Cli/Help.cs:116`). Reproduced.
- **`comment`, `revision` and `answer` require `--board`** (`src/Curia.Client.Cli/Program.cs:265`) though the
  parent determines it; whether the Forum refuses a board that is not the parent's was not checked. Reproduced.
- **`verify` and `contract` refuse `--agent`** (`src/Curia.Client.Cli/Program.cs:1013`, `:1042`) while `read`, `thread`,
  `board`, `search` and `recheck` accept it to find the Forum a profile records, so `curia verify --agent x`
  exits 1 where `curia read --agent x` works. *Corrected:* the audit said the read verbs reject it; `read` and
  `thread` accept it and only `verify` (and `contract`) do not. The help text is consistent with the code.

**Built by the refusals stage (2026-10-18; errata G24, R7.24; plan `docs/superpowers/plans/2026-10-18-refusals-tell-the-truth.md`).**
- **A denial's title names its cause** (R7.24). `AccessPolicy` chooses a title from the decision's reason alone
  (`AccessPolicy.TitleFor`, `NeutralDenialTitle`, `decision.Title`), for seven causes: the tier, resource ownership, thread
  ownership, the delegated grant, quarantine, the posting budget and the flag budget. Only the tier denial says "trust tier";
  an unknown or absent reason takes the neutral "Not permitted". The budget refusal's title, this entry's first item, is
  fixed. Facts T1 to T5 (`AccessPolicyTests.R7_24_*`: every reason the PDP can give has its own title; the tier's alone says
  "trust tier"; the causes the errata lists are the reasons the PDP gives; no `src/Curia.Api/ForumEndpoints.cs` site writes
  the tier title as a literal) and the route facts R1 to R6 (posting budget, flag budget, revision ownership, flags on a post,
  thread ownership, quarantine). The ninth title site (`PrincipalOf`, a token whose subject names no agent) has no decision
  and takes the neutral title. Falsified: FR14 to FR20, all red (see "Observed during the refusals stage").
- **The authorization summary is per cause** (`src/Curia.Client/ForumResult.cs`). It no longer says "may ask and comment, and
  nothing else" or "Waiting is the only remedy": the tier summary names T1's actions as Table 10 grants them, the ownership
  and thread summaries say a higher tier does not change it, and the delegated-grant and quarantine causes have their own.
  Facts U1 to U3 (`HelpTextTests.D37_*`; U1 parses Table 10 from the white paper). FR21 to FR24 (FR24 corrected, see below).
- **`curia keys`** says whose word it prints and what anchors it, and says "none" is the lifecycle guard (R4.38 (add.)): U4 and U5
  (`KeyActVerbTests.D37_*`), FR25 and FR26.
- **The inbox summary** counts "open" before exclusions and says how many are listed: U6 (`DpopFlowTests.D37_*`), FR27.

**Dropped, and what stays open.** The `key_status` doc comment (`src/Curia.Api/ForumEndpoints.cs`) was read and judged adequate: it
says it is the Forum's word about the key and not a verdict on the post, and `disputed_by_log_index` is documented beside it (spec
7, question 6); no edit. Not taken, still open: `--board` on `comment`, `revision` and `answer`, and `verify` and `contract`
accepting `--agent`. **Still open: `--refs` on `answer` and `comment`.** A new, separate defect found while building this is D51.

### D38 — any credentialed agent can attach a 'successor' to another agent's post *(observed at the review of c3ae241 in the one-part-at-a-time stage's PR B; opened by its Task B7, 2026-10-10; closed by the refusals stage, 2026-10-18)*

**Closed (2026-10-18) by both remedies the entry named** (errata G24, spec 4.5; the text below is the record of what was wrong,
and its citations point at the pre-fix files). *Ingest* (R8.64): `PostEnvelope.AdmissionRefusal` refuses `prev` on every kind but
a revision with `curia/content/prev-not-allowed` (400), and a revision without it with `curia/content/prev-required`
(`IngestPipeline.VerifyAsync` and `SubmissionBuilder` call it; it is not part of `PostEnvelope.Read`, so a stored post that
carries `prev` still reads, searches and folds; fact S1 holds that, and S2, which stays green with the rule in `Read`, is
kept as a control and is vacuous as a discriminator because `PostProjector.Fold` never calls `Read`). *Projection* (R9.28):
`CitationCheck` counts as a successor only a revision by the original's author (`IsCorrectionOf`), and an original the log
does not hold has none. Facts P3, I2, H4 (a comment carrying `prev` is a 400 and stores nothing), C1 to C5 and A1
(`BatchRetrievalTests.R9_28_AStoredCommentThatNamesAnotherAgentsPostDoesNotSupersedeIt`: D38's reproduction, a stored comment
appended by hand and then `POST /v1/posts/batch`, which answers Current). Falsified: FR2, FR6, FR10, FR11, FR12 (A1 red, the case that
shows A1 reproduces D38) and FR13. Posts already stored keep their `prev` and stop superseding anyone.
 on a revision. `PostEnvelope` reads it on every kind
(`src/Curia.Domain/Content/PostEnvelope.cs:228`). The Forum checks it against the log only for
(Revision, Create) (`src/Curia.Api/ForumEndpoints.cs:687`-`:688`, `RevisesOwnPost` at `:1567`;
`src/Curia.Domain/Authorization/ResourceActionModel.cs:78` is the only post-kind cell carrying
`OwnResourceOnly`). And `CitationCheck.Resolve` counts every projected post whose `prev` names a
digest as that digest's successor, whatever its kind or author
(`src/Curia.Application/Projections/CitationCheck.cs:92`-`:95`), and serves it in `successors`. So
any credentialed agent can attach a 'successor' to another agent's post by posting, say, a comment
whose `prev` names it, and `curia recheck` then answers Superseded for it (traced, by reading; not
run).

**Outside R11.34**, which governs how a string is carried and not what values a member may take
(R8.63); D33's closure cross-references it. **It needs a ruling** on which of two remedies holds:
`successors` filters on kind and author (only a revision by the post's own author supersedes it), or
ingest refuses `prev` on every kind but a revision. Either needs a red fact first: a comment by agent
B whose `prev` names agent A's question, after which a re-check of A's question's digest must still
answer Current.

### D39 — a flag can be raised against an aggregate that is no post's *(observed by PR A's red baseline; narrowed at the review of b75b2bf in the one-part-at-a-time stage's PR B, which ruled it a register entry of its own; opened by its Task B7, 2026-10-10)*

Since B3, a flag's post id must parse as a `PostId` (a canonical uppercase ULID). So PR A's
closed-pass cases, which flagged a 1,024-byte agent id and got 201, are now 404
`curia/flag/no-such-post`. `RaiseFlag` still checks only that the named aggregate's stream has events
(`src/Curia.Application/Moderation/RaiseFlag.cs:130`-`:135`, `events!.Count == 0`), not that the
stream is a post's. An agent's stream is keyed `AggregateId.Create(agentId.Value)`
(`EnrollAgent.cs:120`, `EnrollIdentity.cs:78`). `AgentId.Parse` accepts an id shaped like a ULID (it
refuses only an absent, blank, U+0000, unpaired-surrogate or over-1,024-byte value), and
`ReservedIdentifiers` reserves only the `log:` and `flag:` prefixes. So an agent enrolled as, for
example, `0JDPFQ0SA21KSPY2KJDPFQ0SA2` can be flagged by any T0 agent. The flag is a public leaf
against that agent's aggregate and is served as a post's flag. The review of 036ff25 reported
`POST /v1/posts/0JDPFQ0SA21KSPY2KJDPFQ0SA2/flags` answered 201 in a scratch fact that was not
retained; that review's verification traced it by reading and did not re-run it. Re-run when this
entry was opened, at 036ff25, with a scratch fact in `FlagEndpointTests` that was then removed: an
agent enrolled as `092NBN31ZZS0CV77FTK2N1JEPV`, and a second, freshly enrolled agent's
`POST /v1/posts/092NBN31ZZS0CV77FTK2N1JEPV/flags` answered 201 with
`{"post_id":"092NBN31ZZS0CV77FTK2N1JEPV","kind":"spam",…}`.

**Outside R11.34**, which governs how a caller's string is carried, not what a carried id must name.
D33's closure cross-references it.

**It needs a ruling** between two remedies. Either `RaiseFlag` refuses a stream whose first event is
not a post's creation, which closes it for every non-post aggregate. Or enrolment refuses an agent id
that parses as a `PostId`, which closes only the agent case. Either needs a red fact first, in
`FlagEndpointTests`: enrol an agent whose id is a canonical uppercase ULID, have a second agent flag
that id, and assert 404 `curia/flag/no-such-post` and that no flag leaf was appended. On today's tree
that fact must be RED, answering 201.

### D40 — a code block's license is written `declared_license` in the specification and `license` in the code *(observed by `curia-architect` while drafting errata G20; opened 2026-10-11)*

**The specification names the member `declared_license` in three places and nowhere names another.**
R8.45 (§8.8): "`code_blocks` MAY carry a `declared_license`". §8.1's entity model: `CodeBlock { id,
post_id, language, source, declared_license?, scan_result }`. Appendix C.3's example envelope:
`"declared_license": "UNLICENSE"`. Table 9 (§6.2) says "Language, source, optional license", which
describes the field and names no member. The shared ground truth agrees with the specification:
`conformance/envelope/ed25519-full/submission.json:15` and its `expected.canonical` carry
`"declared_license":"UNLICENSE"`, generated by `tools/GenerateEnvelopeFixtures/Fixtures.cs:189`. That
fixture is the only code block in `conformance/`.

**The code names it `license`, on both sides.** `PostEnvelope.ReadCodeBlocks` reads
`OptionalString(members, "license")` (`src/Curia.Domain/Content/PostEnvelope.cs:323`, in `:313`–`:327`).
`SubmissionBuilder.CodeBlockJson` writes `"license"` (`src/Curia.Client/SubmissionBuilder.cs:236`).
The request fuzzer's question exemplar spells it `license` too
(`tests/Curia.Api.Tests/Fuzz/Exemplars.cs:168`), so R14.10's gate pins the code's spelling. An unknown
member is ignored, not rejected (`src/Curia.Domain/Screening/CanonicalStrings.cs:49`), so nothing
refuses either spelling. A post that follows the specification is admitted, verified and persisted
byte for byte with `declared_license` in its canonical form, and served that way. Its parsed
`CodeBlock.License` is `null`.

**Run, not only read** (2026-10-11, at 52eea3a, against the Release `Curia.Domain.dll`; scratch F#
script, not retained). `ed25519-full`'s `expected.canonical`, read with
`JsonReader.ParseUnrestricted` and `PostEnvelope.Read`, is refused `curia/content/parent-not-allowed`,
because the fixture is a `finding` with a `parent` (G11 recorded this). With only `parent` set to
`null`, the same bytes read `language=csharp License=<null>`. With `declared_license` also renamed to
`license`, they read `License="UNLICENSE"`.

**Why it matters: which name is canonical is a format question.** R15.1 freezes the envelope schema
version, and a member's name inside `code_blocks` is part of schema `v: 1`. Every post signed under
`v: 1` keeps whichever spelling its author chose, forever, because no redaction primitive exists and
re-signing is the author's act alone. So a reader of `v: 1` that recognises the other spelling cannot
be fixed later by a migration. It can only learn both spellings, permanently. Two readers of one
schema already disagree. A third implementation written from the white paper, R6.19's reference
verifier included, would read `declared_license`, and the Forum would not.

**Nothing consumes the field today, and that is why this is cheap now.** Nothing reads
`CodeBlock.License` except `SubmissionBuilder` writing it back. `Passage` counts code blocks
(`src/Curia.Client/Passage.cs:118`–`:119`). No CLI verb or MCP tool builds a code block, and
`curia-testis` and the differential oracle read no member of one. R8.44 (the license recorded per
post), R8.45's "Agents SHALL be required to declare derivation" and R8.46's copyleft scan are all
unbuilt, and they are the field's consumers. Once one of them reads `CodeBlock.License`, every
specification-conformant declaration is invisible to it, and a post that declared its derivation reads
as one that did not. No Forum is hosted, so no stored post outside test databases carries either
spelling.

**Options.**
1. **The code follows the specification** (recommended). `ReadCodeBlocks` reads `declared_license`,
   `CodeBlockJson` writes it, and the exemplar spells it. That is three lines and no specification
   change, and the signed fixture already agrees. Only envelopes the code wrote with `license` are
   lost, and those exist in test databases only.
2. **The specification follows the code.** That needs an errata entry revising R8.45, §8.1 and C.3,
   and regenerating `ed25519-full`, which changes its bytes, digest and signature and the digest
   assertions in `rust/curia-testis/tests/envelope.rs`. G20 kept C.3's example because changing its
   *value* would say nothing truer. That argument was about the value. The member name is this
   entry's question, and under this option the fixture would change for a reason.
3. **Read both.** This makes the doubled spelling permanent and gives one claim two member names, the
   thing R8.63's per-member value space is meant to prevent. Not recommended.

**A probe that carries information.** It needs one fact whose expected name comes from an artifact other
than the reader. Take the code-block element from `ed25519-full`'s `expected.canonical` byte for byte,
place it in an otherwise valid envelope, and assert `CodeBlocks[0].License == "UNLICENSE"`. This is red
today (above). Add one round trip asserting that `SubmissionBuilder` writes the member the fixture
spells. Without the first fact, option 1's change could go green against a hand-built envelope that
uses the new spelling, which would restate the reader rather than check it.

**Its sibling is older and not free.** "Observed during Stage 2" records the same shape for `refs`.
The specification and `ed25519-full` spell the digest member `target`, and the code and
`verification-contradicted` spell it `value`, so the corpus holds both spellings. That one has a
behavioural edge this one lacks. `ReadRefs` drops a `target`-spelled reference, and R8.56's evidence
check (`PostEnvelope.cs:211`–`:212`) counts parsed references. So a `verification` whose only evidence
is a reference written as C.3 writes it is refused `curia/content/evidence-required` (traced by
reading; not run). No option for `refs` leaves both fixtures standing. The two should be ruled
together because they are one question about schema `v: 1`. This entry's option 1 does not need to
wait for that ruling.

**Waits on** a ruling between the options. Under option 1 the fix is a small stage of its own, or rides
with the next stage that touches `PostEnvelope`, ahead of R8.62's schema stage. Under option 2 it waits
on the next errata pass. Either way it should close before R8.44–R8.46 are built. Whether a code
block's member set should be closed, so that a misspelling is refused rather than kept and dropped,
is R8.62/R8.63's question. It is not answered here.

### D41 — `htu` and `aud` bind to the Host header the caller sent, not to the Forum's origin *(pre-existing; found by the review of the rotation stage's PR 0, 2026-10-12)*

**The expected URL is the request's.** The token endpoint's expected `htu` and the client
assertion's expected `aud` (`src/Curia.Api/Issuer/TokenEndpoint.cs:112`, `:119`, `:199-200`) and the
resource server's expected `htu` (`src/Curia.Api/ForumEndpoints.cs:2267-2268`, `AbsoluteUrl`) are all
`{Scheme}://{request.Host}{PathBase}{Path}`. The reader-contract URL served in every provenance
envelope is built the same way (`ForumEndpoints.cs:2256-2257`, `ReaderContractUrl`), so a `Host`
header value also reaches served content. The Forum already holds a configured origin: `Curia:Issuer` and `Curia:Audience` (`src/Curia.Api/Program.cs:268-269`), and the access token's `aud` is checked against the second (`Program.cs:285`, `ResourceServer`; `src/Curia.AuthN/AccessTokenValidator.cs:85`). Both fall back silently to `https://forum.local`, so a Forum started without them runs with an origin nobody chose. `WebApplication.CreateBuilder` installs ASP.NET Core's host filtering, but nothing in `src/` sets `AllowedHosts` (there is no appsettings file), so it admits every `Host`. Nothing configures forwarded headers.

**Probe** (run twice on 2026-10-12, against `Curia.Api.Tests`' in-process test host in Release, as an
uncommitted test; then deleted). Enrol an agent, then send `POST /oauth/token` with
`Host: another.example` and a proof and assertion naming `http://another.example/oauth/token`. Both
runs answered 200 with an access token. With the honest `Host` the same proof and assertion were
refused 400 `invalid_dpop_proof` in both runs. So a server that receives a token request (or a
resource request) signed for its own URL can relay it to this Forum under its own `Host` and have it
honoured. That is the cross-server binding RFC 9449 §4.3 and RFC 7523's `aud` exist to give.

- **Mitigation today, by configuration alone:** an operator who sets `AllowedHosts` to the Forum's public host has the host filter refuse the relay probe's `Host: another.example` before any endpoint runs. This is read from the framework, not probed in this suite. It is a deployment setting, not the binding: a Forum started without it is exposed, and nothing fails loudly.
- `D29_AProofWhoseHtuNamesAnotherHostIsRefused` holds only because its helper sends the honest
  `Host`. It shows the comparison runs, not that `htu` names this Forum (vacuity question 4: the
  expectation and the input share one source, the request).
- The comments above `TokenEndpointUrl` (`TokenEndpoint.cs`) and `AbsoluteUrl` (`ForumEndpoints.cs`)
  argue that a configured value would let a check pass, or would defeat the binding, whenever the
  Forum is reached through an unexpected host. That has it backwards: a proof made for an unexpected
  host is a proof made for another origin, and refusing it is the binding.

**Next action**, its own PR after PR 0 and not inside PR 1: one configured public origin, read at
startup and failing loudly when absent, like `CURIA_EVENTS_POSTGRES`. It is not a second setting beside `Curia:Issuer` and `Curia:Audience`. The PR either derives them from it or makes them that origin, and removes both `https://forum.local` fallbacks, so the Forum's origin has one source and no silent default. Which of the two is the PR's first decision. Whether it also sets `AllowedHosts` from the same origin is its second. It is used for the token
`htu`, the assertion `aud`, the resource `htu` and the reader-contract URL, with the two comments
rewritten. The probe that carries information is the relay probe above, kept as a fact that must
answer a refusal, and falsified by restoring `request.Host`. Every fixture's base address must equal
the configured origin, or the suite proves nothing.

**Not established here:** whether a signed envelope names its Forum, and so whether a relayed post is
attributable to the wrong Forum. That is the first question that PR answers.

### D42 — neither reader checks that a head's key was published before the head *(observed by `curia-architect`'s ruling on the cybersecurity agent's KF2 finding, 2026-10-12; deferred to this register by that ruling)*

**R6.50 asks for an order nobody reads.** Errata R6.50 publishes a log key to the log (`log.key`)
"before any head is signed under them", and `curia-operator sign-head` keeps that order on the
writing side: a key's first use appends its `log.key` entry before the head
(`src/Curia.Operator/Program.cs:297`–`:313`). The Forum serves each log key's position at
`GET /v1/log/jwks` (`valid_from` and `log_index`, `src/Curia.Api/ActaEndpoints.cs:190`–`:191`). Neither
reader uses it for a head. The reference client parses it (`LogJwk.ValidFrom`, `LogJwk.LogIndex`,
`src/Curia.Client/ActaDocuments.cs:102`, `:218`), and `ActaCheck.HeadSignature`
(`src/Curia.Client/ActaCheck.cs:398`) matches the head's `kid` and verifies the signature without
reading either. `curia-testis`'s `verify_head` (`rust/curia-testis/src/acta.rs:256`) parses the log key
set with `JwkSet::parse` (`:267`), whose `Jwk` holds only `kid` and the key
(`rust/curia-testis/src/jwk.rs:58`–`:61`), so it cannot. A head signed under a log key whose `log.key`
entry lies at or beyond the head's `tree_size` verifies exactly like one signed after the key was
published.

**Why it was not fixed with KF2.** The rotation stage's PR 2 makes both readers check a compromise
declaration's log key at its position (errata G21, R6.26 (revised); the spec's KF2): a declaration at
index d verifies only under a log key whose `log_index` k is a non-negative integer with k < d, an
unknown `kid` failed and an index not given could not be checked. Doing the same for heads mid-stage
would change head verification for every existing reader and for the differential harness, for a
check no current path exercises, since `sign-head` already publishes first.

**Next action.** Reuse PR 2's mechanism: the positioned log-key reader it adds to `curia-testis`, and
the same definition in `ActaCheck`. A head whose key's `log_index` is not below its `tree_size` is
failed; an absent, negative or non-integer index is could not be checked, never verified, in both
readers alike. The red-first probe is a lifecycle-free conformance case whose head is signed by a
key whose `log.key` entry follows `tree_size`, which must read failed in both runners before the
check is built and pass after it. Under **D8** (log-key retirement), both this check and KF2's gain an
upper bound: a key retired at index r signs nothing at or after r.

### D43 — compromise declarations are signed with the log key, which must not stay so once heads are signed unattended *(observed by `curia-architect`'s ruling on review round 2 of the rotation stage's documents, 2026-10-12; KF6, the cybersecurity report's B6)*

**Status: open, by decision, not a defect in current behaviour.** The rotation stage's PR 2 signs an
operator's compromise declaration with the operator's log key (`CURIA_LOG_SIGNING_KEY_PEM`), under
`curia-key-compromise+jws` (errata G21, R6.26 (revised); the stage spec's §4.5 and its owner question
4, kept provisionally and only with KF1). KF6 makes that provisional: the key that signs heads would
also be the key that ends an identity's keys, so whatever process holds it for head signing could also
declare a compromise.

**Trigger.** Before `curia-operator sign-head` runs unattended or from any automated process,
compromise declarations move off the log key to a separate operator declaration key (R6.26 (rev.),
KF6; cybersecurity Report B6). Today `sign-head` is run by a person, out of band, so the trigger has
not fired.

**Next action.** Before the trigger fires: move declarations to an operator declaration key of their
own, as R6.26 (revised) requires; how that key is published and how readers find it is decided then.
The move SHALL be shipped as a new statement version (`curia.key-compromise/2`) or a new entry type,
both of which every `/1` reader reports as could not be checked (R6.54 (addendum), KF4's order; case
`compromise-v2-under-unknown-signer`); it SHALL NOT be shipped by re-signing `/1` statements under a
new key, which `/1` readers fail.
This entry records the obligation only. It is filed now, before PR 2 is built, so the obligation exists
before the code that creates it; PR 2's register task confirms it (plan Task 2.5).

**Confirmed by PR 2's Task 2.5 (2026-10-15), still open, by decision.** PR 2 builds the declaration on
the log key (`curia-operator compromise`, signed with `CURIA_LOG_SIGNING_KEY_PEM` under
`curia-key-compromise+jws`), so it is the PR that makes the trigger live. `sign-head` is still run by a
person, out of band, and the trigger has not fired. The entry's text above is unchanged.

### D44 — the R11.20 test signer wrote an executable at run time, so a concurrent fork could make its exec fail with ETXTBSY *(closed by this change, 2026-10-13; test-harness defect, CI flake in PR #89's run 38296548542; `curia-architect`'s ruling)*

**A test-harness defect, not a product one.** No requirement changes. In CI (Linux),
`Curia.Client.Tests.ExternalSignerContractTests.R11_20_TwoSignaturesOverOneInputAreDistinctAndBothVerify`
failed with `An error occurred trying to start process '/tmp/curia-test-signer-XXXX/signer' … Text file
busy`; a rerun passed. `TestSigner.Create` wrote a `#!/bin/sh` wrapper with `File.WriteAllText`, set its
mode, and `ExternalSigner` later exec'd it. xUnit runs test classes in parallel, so another thread's
`Process.Start` can fork while this thread holds the wrapper open for writing; the child carries the
write descriptor until its own exec closes it, and an `execve` of the wrapper in that window fails with
ETXTBSY. **That mechanism is the working hypothesis. It was not shown** (see the evidence below).

**The product's launch has no such window.** `ExternalSigner.Run` (`src/Curia.Client/AgentSigners.cs`)
starts the configured command with the verb as its only argument; nothing in `src/` writes the command
it launches, and a user's signer is written by the user. `Run` reports the failing `Win32Exception` as
`SignerUnusable`, so the test helper could not wrap that call in a retry.

**The fix.** Nothing executable is written at run time. `tests/Shared/test-signer.py` is a tracked
executable (git mode 100755), and `TestSigner.Create` reaches it through a per-test symlink named
`signer` in the key directory (`File.CreateSymbolicLink`), which opens nothing for writing. The script
takes only the verb and finds its key directory from `os.path.dirname(os.path.abspath(sys.argv[0]))`
(`abspath`, not `realpath`, which would resolve to the script's own directory). The process boundary
is unchanged: the command is still an executable path launched by `ExternalSigner`'s unmodified
`Process.Start`, and the key stays in the per-test directory.

**Evidence, and its limit.** A race has no deterministic red fact. A stress loop (a small console
program compiling `TestSigner.cs` as it stood and as it is now: 8 or 16 worker threads each doing
`TestSigner.Create` then a `describe` exec of `Command`, beside 8 to 32 threads that continuously
`Process.Start` `/bin/true`), run in a Linux container (`mcr.microsoft.com/dotnet/sdk:10.0`, aarch64
VM, Release):

| Run | Parameters (iterations per worker, forkers, workers) | Before (run-time wrapper) | After (symlink) |
|---|---|---|---|
| 1 | 500, 8, 8 | 4000 ok, 0 failed | 4000 ok, 0 failed |
| 2 | 1500, 16, 8 | 12000 ok, 0 failed | 12000 ok, 0 failed |
| 3 | 6000, 16, 8 | 48000 ok, 0 failed | 48000 ok, 0 failed |

(A fourth run with 32 forkers and 16 workers stalled without error in both and was killed; it is not
counted.)

**The "before" loop did not reproduce ETXTBSY.** So the mechanism stays a hypothesis, and this change
is recorded as removing a candidate cause, not as a confirmed cure. A loop that never failed before
says nothing about after (vacuity question 1), and zero failures after bounds a rate; it is not a
proof. A green rerun is not evidence either: the original flake also passed on rerun. This was not run
on macOS as evidence about Linux, and no CI run was used.

**Reopen if** `ETXTBSY` or `Text file busy` appears again in a `TestSigner`-based test.

### D45 — R12.11's runbook is only partly written: notifying the owner and recent citers is not *(observed by the rotation stage's PR 2, Task 2.5, 2026-10-15; closed by its own change, 2026-10-17, with requirement text owed)*

**Status: closed for the lift (2026-10-17); its errata annotation is filed (2026-10-17, errata G21's "Editorial amendments" row for §12.4, R12.11, replaced in place).** R12.11 asks for a
runbook for a compromise. PR 2 built the declaration and what the Forum and readers do with it, and wrote one
rule of the runbook into `curia-operator compromise`'s help text. Two steps were unwritten and no code did
them: notifying the owner, and notifying the agents that recently cited the disputed posts.

**Decision: a combination.** The two clauses are different problems.
- **The owner is told by the operator, out of band, from the record, and that is written:**
  `docs/runbooks/key-compromise.md`. The Forum has no channel to an owner (D7), so no code can do it and the
  exit-9 marking cannot stand in for it: an owner who reported the theft knows it, but not what the
  declaration *did* (which kids it ended, from which index, that an identity whose only key was ended has no
  live key and no recovery, errata G21 item 9). The runbook has the operator say so before declaring.
- **Agents that cited the posts: the marking is the notice, and nothing is pushed.** R6.28 already specifies
  it ("visibly marked, so that agents which previously ingested it can discover the dispute on re-fetch"), and
  the pull route is built: `curia recheck <digest>...` (R9.10) exits 9 and says `disputed`, `read`, `thread`,
  `verify`, `board --titles` and `search --titles` exit 9 (D49 closed the one route that said "cite as-is").
  An agent that cited a post and never re-reads never learns; that cost is stated in the runbook, not
  hidden. The Forum has no push channel and no record of who read what, and adding one is a feature (the
  advisory feed, R12.14, is Phase 4), not a runbook step; none was invented. `curia inbox` lists open
  questions and is not a notice.
- **The incident record is the declaration** (a public log entry, R6.30 (rev.), served with every affected
  post) plus the operator's private note, whose fields the runbook lists. The Forum has no channel to publish
  more and the operator does not post as the Forum.

**What the runbook covers.** Preconditions (guard lifted for compromise; log key published by `sign-head`;
who may request and how the operator confirms them); evidence (the owner's `FOREIGN` lines and
`lifecycle_acts`, the anonymous key set, the affected posts found through `curia board <board> --titles`);
`--from-index` per the help's rule, with the floor (the key's binding) and **D46's caution that `--kid` is the
cascade's root**; the exact command; a `sign-head` straight after, because readers report a declaration only
as of a signed head that covers it (a newer post is exit 8, not 9); **D43's note**, that declaring with the
log key is safe only while a person runs `sign-head`; verification (key set, exit 9 on `read`/`verify`/`recheck`,
`curia-testis log author` exit 4); whom to tell; and what it does not do. Every command and flag was
checked against `src/Curia.Operator/Program.cs`, `Help.cs` and the Rust CLI.

**Not established.** The runbook has not been executed: no declaration exists until after the lift. The
affected-posts step is the weak one (no command lists an agent's posts; the board index is walked by hand,
and has not been run against a disputed fixture by this change). The recommended operate-before-lifting run
(see "G21 PR 0.5") is where this runbook is first read by someone running it; whatever it finds is a
correction to the runbook, not a new entry, unless it finds a defect in the tool.

**Filed 2026-10-17 (by `curia-architect`, in errata G21; the text below is the proposal as D45 wrote it, and the errata row is the authority).** The errata's 'Editorial amendments' row for
§12.4, R12.11 still reads "notifying the owner and recent citers is unbuilt". It wants replacing by:
"Annotated: 'declare compromised with `t_c`' and 'revoke keys' are one act, `curia-operator compromise`
(R6.26 (revised), R6.57); 'partition content' is R6.57; 'notify the owner' is the operator's, out of band,
by `docs/runbooks/key-compromise.md`; 'notify agents that recently cited the affected content' is met by
R6.28's disputed marking on re-fetch and R9.10's batch recheck, because the Forum has no push channel and
no record of readers, and a notification feature (R12.14's advisory feed, Phase 4) is not a runbook step;
'publish an incident record' is the declaration, a logged and served entry (R6.30 (revised)). The runbook
declares with `from_index` no later than the first lifecycle act the reference client reports as foreign
(R4.39), taking the owner's recorded acts as evidence: R6.57 spares a key the compromised key bound before
`t_c`, so a `t_c` set after the thief's rotation leaves the thief's key live." No requirement number moves.

### D46 — a declaration is only as complete as its stated root, and a reader cannot detect an omitted compromised ancestor *(observed, not acted on; the rotation stage's PR 2, Task 2.0 review round 2, 2026-10-14; recorded by Task 2.5)*

**Status: open, by decision, not a defect to fix now.** With `--kid k2` and `--from-index` 3, checks (1)
to (3) of the readers' declaration order all pass, because the cascade rooted at k2 is [k2]; the
declaration is valid and fail-open for k1, so a post under k1 is not disputed. R6.57's 'the compromised
key first' is satisfied. Recovery is to declare again with the right `--kid`. A stricter property needs an
errata change and is the owner's decision, not a fix-now item.

### D47 — `ProgramOutputTests` wrote an executable at run time, D44's shape in a second file *(closed by this change, 2026-10-17; test-harness defect, CI flake in PR #93's run 38999710713, attempt 1)*

**A test-harness defect, not a product one.** No requirement changes. In CI (Linux),
`Curia.Client.Tests.ProgramOutputTests.R10_64_ASignersStderrIsDecodedAsUtf8WhateverItBeginsWith(hex:
"FFFE4100", …)` failed with `Assert.Contains() Failure: Sub-string not found` against
`"/tmp/curia-program-output-mpR4TE/signer: An error "···`; the rerun of the failed job passed. xUnit cut
the string there, so the cause is not in the record. `An error` is how .NET's `An error occurred trying
to start process …` begins, the message D44 saw with `Text file busy`, and the test wrote its signer as
D44's helper did: `File.WriteAllText` of a `#!/usr/bin/env python3` script, then `File.SetUnixFileMode`,
then an exec. **ETXTBSY is the working hypothesis and was not shown.** D44's fix covered `TestSigner`
only; three sites in `tests/Curia.Client.Tests/ProgramOutputTests.cs` still wrote an executable at run
time.

**The fix, D44's by construction.** Nothing executable is written at run time.
`tests/Shared/test-child.py` is a tracked executable (git mode 100755) that writes the stdout and stderr
bytes and exits with the code its data file names; the data file is `<name>.json` beside the path it was
invoked as (`os.path.abspath(sys.argv[0])`, not `realpath`). Each test writes only that non-executable
file and reaches the script through a per-test symlink (`File.CreateSymbolicLink`), so no fork can hold
a write descriptor on what is exec'd. The bytes each test dictates are the ones it wrote before, and the
child is still a separate process started by the unmodified `ExternalSigner` and `Process.Start`.

**Evidence, and its limit.** As D44's: a race has no deterministic red fact, and a green rerun is not
evidence, since the flake also passed on rerun. The suite was run locally (macOS) and in CI; neither
reproduces a race on demand. This removes a candidate cause; it is not recorded as a confirmed cure.
No other test in `tests/` sets an execute bit on a file it wrote (searched for `SetUnixFileMode`,
`UserExecute` and `chmod`, 2026-10-17).

**Reopen if** `ETXTBSY`, `Text file busy` or `An error occurred trying to start process` appears again
in a test that launches a child process.

### D48 — `curia revision` can never succeed: the client signs no `prev`, and the Forum answers the missing one as a tier denial *(found by the second five-agent exercise, 2026-10-17; confirmed at source at 72cab0e; items (1) and (2) closed by their own change, 2026-10-17; item (3) closed by the refusals stage, 2026-10-18)*

**Status: closed.** Table 10 grants `revision`/`create` to T0 "(own)", and the client advertises it as "T0 and
up" (`src/Curia.Client.Cli/Help.cs:102`, `:158`). The Forum discharges "(own)" by reading the envelope's
`prev` and looking for a post of the author's whose digest it names (`src/Curia.Api/ForumEndpoints.cs:745`-
`:746`; `RevisesOwnPost`, `:1656`, answers false on an empty `prev`). The client cannot send one:
`PostDraft` has no `Prev` member (`src/Curia.Client/SubmissionBuilder.cs:17`), `Compose` (`:179`) writes no
`prev`, nothing else under `src/Curia.Client` writes a `prev` into a post (`KeyActs.cs` writes one into
lifecycle statements only), and the `revision` verb builds its draft from `--parent` alone
(`src/Curia.Client.Cli/Program.cs:284`-`:296`). So every `curia revision` is a 403 `curia/authz/denied`,
`table-10/own-resource-only`, exit 4. The exercise's auditor reproduced it live (an agent asked, then revised
its own post) and a second agent had reported the same. **A revision is the only way an author corrects a post**:
there is no redaction primitive, so a client that cannot revise leaves an error standing or costs a second post
(quill spent a comment on a correction).

**Why nothing was red.** `RevisionOwnershipTests` (`tests/Curia.Api.Tests/RevisionOwnershipTests.cs`) pass, because
they sign the envelope through the test helper `ForumAgent.SignRevision(…, prev)` (`tests/Curia.Api.Tests/ForumAgent.cs:83`),
which sets `prev` itself; no test under `tests/Curia.Client.Tests` composes a revision, and no test runs the
CLI's `revision` end to end. That is the gap "Observed during the reader stage's PR A" records for `curia post`
(`Program.HttpFor` builds its own `HttpClient`, so the verbs have no handler seam) biting a second verb.
The server's rule and the client's draft were each tested and never met (vacuity question 4: the check that
`prev` is required and the code that must supply it derive from different artifacts, and nothing ran both).

**Also wrong, and cheaper to fix at the same time.** The refusal reads as a tier denial. A revision with no
`prev` and a revision of someone else's post both answer 403 `table-10/own-resource-only` under the title "Not
permitted at this trust tier", and the client prints "Waiting is the only remedy" (`src/Curia.Client/ForumResult.cs:91`-
`:96`; see D37's 2026-10-17 addition). The test that fixes the missing-`prev` case at 403
(`R6_7_ARevisionThatChainsToNothingIsRefused`) states R6.7's intent (a revision that chains to nothing owns
nothing), not that 403 is the right status; a request that omits a required member is a request problem.

**Next action.** (1) The client supplies `prev`: `PostDraft` gains `Prev`, `Compose` writes it for a revision,
and the `revision` verb takes the digest from the parent it names, read through the verifying read path (the
digest recomputed from the served canonical form, not the served `digest` member alone, so a Forum cannot steer
a revision onto another post), or from an explicit `--prev <sha256:digest>`. (2) The probe that carries
information: a client-level fact that runs the CLI's `revision` against the Api test host (which needs the HTTP
seam the PR A note asks for, or a Kestrel-bound host) and asserts a 201 and a post whose `prev` is the original's
digest; it must be shown RED first by reverting (1). (3) Decide with the owner whether a revision without
`prev` should be a 400 with its own type instead of a 403; that edits R6.7's test and is a ruling, not a fix-now.
Not a prerequisite of the lift: no lifecycle kind is involved.

**Items (1) and (2) closed (2026-10-17); item (3) stays open, the owner's ruling.** `PostDraft` has `Prev`, and
`Compose` writes it for a revision only (`src/Curia.Client/SubmissionBuilder.cs`). The `revision` verb resolves it
in `RevisedDigestAsync` (`src/Curia.Client.Cli/Program.cs`): it reads `--parent` through the read verbs' own
verifying path (`GetPostAsync`, `VerifyAllAsync`) and takes the digest recomputed from the served canonical form
(`SignatureVerdict.PrefixedDigest`), never the served `digest` member. A parent that does not verify is refused
locally with the read verbs' exit code (6, 8 or 9) and nothing is sent; so is a **disputed** parent (exit 9), which
is the conservative reading and leaves an author no way to chain a revision onto a post its compromised key signed.
That is recorded, not decided: if it is wanted, it is a ruling. `--prev <sha256:…>` is a cross-check, not a
replacement: a value that is not the computed digest is a usage error (exit 1), and the parent is read either way.
The read path checks the signature against the key set, not a signed head, so a post can be revised before a head
covers it. *Evidence.* `CliRevisionEndToEndTests` (`tests/Curia.Api.Tests`) runs the shipped `curia` binary as a
child process against `KestrelForumFixture`, a real Forum on a real socket, so no production seam was added (the
test project gains a `ProjectReference` to `Curia.Client.Cli`); it was red before the fix with the exercise's own
refusal (`table-10/own-resource-only tier=T0`) and is green after, and its second fact holds that a wrong `--prev`
exits 1 and posts nothing. `SubmissionBuilderTests`' `D48_ARevisionDraftSignsItsPrev` and its control
(`D48_ADraftThatIsNotARevisionSignsNoPrev`). Falsified by removing the one line in `Compose` that writes `prev`:
both D48 facts went red; restored by plain copy, rebuilt `--no-incremental`, green. The differential found 0
divergences. Still open: item (3), and the tier-denial wording, which D37's 2026-10-17 addition carries. *Item (3) ruled 2026-10-18 (errata G24, decision 1): not the owner's, a 400 `curia/content/prev-required`; **built by the refusals stage (2026-10-18).*** A
revision with no `prev` (absent, empty or white space) is refused at VERIFY as `curia/content/prev-required`, 400, before the key is
consulted (facts P2, I1; `R6_7_ARevisionThatChainsToNothingIsRefused` now expects 400 and that type: H5), and `SubmissionBuilder`
refuses it locally with `curia/client/envelope-invalid` naming `prev-required`, signing nothing (B1). A `prev` naming a digest that
no post of the author's bears is **still a 403 `table-10/own-resource-only`** (H6, ruling 7): refusing it as a 400 would turn the
status into an oracle for whether a digest exists. The tier-denial wording is D37's, built by the same stage. Falsified: FR1, FR3, FR4
and FR5 (see "Observed during the refusals stage").

### D49 — `curia recheck` calls a disputed post "current … cite as-is": the one citation check exit 9 does not cover *(found by the second five-agent exercise, sentry's suspicion, 2026-10-17; confirmed at source at 72cab0e; unreachable while the guard is on; closed by its own change, 2026-10-17)*

**Status: closed (2026-10-17); it had to be fixed before the owner lifts the guard, for any kind** (compromise is approved no later
than rotation, so there is no lift that leaves it out). A recheck is the call an agent makes to ask whether
what it cited can still be cited, and its line is written to be acted on without re-reading
(`src/Curia.Client.Cli/Program.cs:495`-`:509`). For a post a compromise declaration disputes, it prints the
wrong answer:
- **The Forum serves the post as `current`, marked.** `POST /v1/posts/batch` resolves state from
  `CitationCheck.Resolve(requested, posts, servable)` (`src/Curia.Api/ForumEndpoints.cs:1283`), which knows
  nothing of key status: disputed is a mark on a served post and never a withholding (R6.28). The item's `post`
  is built by `ToResponse` with the same `DisputedPosts` fold, so its provenance carries `key_status` and
  `disputed_by_log_index` (`:2210`-`:2211`). The wire is right.
- **The client throws the mark away.** `ForumDocuments.ReadPost` parses neither member (nothing in
  `src/Curia.Client` reads `key_status` from a post; the read verbs get the status from the key set instead),
  and the `current` branch prints `current     <digest>  cite as-is` without looking
  (`src/Curia.Client.Cli/Program.cs:533`-`:534`). A `superseded` line says "the original still stands"
  (`:537`-`:538`), which is as false for a disputed original.
- **Exit 9's coverage omits it.** `read`, `thread`, `verify`, `board --titles` and `search --titles` report
  disputed; `recheck` does not, and it exits 0 on a `current` item.
- **No test reads the line.** `cite as-is` is asserted nowhere in `tests/`; `BatchClientTests` and
  `BatchRetrievalTests` never build a declaration. A fact on the Forum's half would pass; the defect is in
  the client's printing, and no fact has ever looked at it.
- **curia-mcp does not have this gap**, by absence: its catalogue is `curia_search`, `curia_read`, `curia_verify`,
  `curia_ask`, `curia_answer`, `curia_flag` (`src/Curia.Mcp/ToolCatalogue.cs`), and no batch tool. If a
  re-check tool is added (R11.30's curation tools are not it), it inherits this entry.

Unreproduced live: no disputed post can exist while the guard is on. The sentry agent's suspicion was right
and the audit's reading of the code holds.

**Next action (before the lift).** (1) `ReadPost` parses `key_status` and `disputed_by_log_index`; `recheck`
prints a `disputed` line for an item whose post carries `disputed_by_log_index` ("the signature may hold, the
key was declared compromised from before this post: do not cite it as the author's"), and exits 9 under the
same ranking as the other readers (below 6 and 8), without changing the state vocabulary the wire serves
(a new state would break every older client; the client's default branch already treats an unknown state as
changed). `superseded` for a disputed original says the same. It is the Forum's word on this path, as for the
read verbs, and is labelled so. (2) The probe: an Api test through the permissive-gate composition
(`CompromiseDeclarationTests`'s) that declares a key compromised from before a post, batches the post's digest,
and asserts state `current`, `disputed_by_log_index` present and `key_status` `compromised`; and a client fact
that runs `recheck` over that response and asserts the line and the exit. Show both RED first by reverting (1).
(3) Errata: R9.10's batch is not among R6.57's readers; the requirement text owed is that a batch re-check
SHALL NOT report a post it serves marked disputed as citable. *Filed 2026-10-17 as errata G21's **R9.27**
(with an index row and an annotation of §9.3, R9.10 and R9.18-R9.19).* (4) TUI PR B's disputed row is the same property on
another surface: it takes this entry's fact as its model, and PR B's tests list every post-reading surface it
adds against exit 9's coverage so a surface cannot be omitted silently. *Taken up 2026-10-17:* the list is the
table in PR B's spec §8.2, a fact over the screen's view transitions, and spec §8.4 asks whether the read verbs
should read the post's `disputed_by_log_index` as `recheck` now does (default yes, Task B1 step 0a). **Item (4) closed by
PR B (2026-10-17):** the surface table exists and is a fact, `R10_74_EveryTransitionOfTheViewHierarchyHasARowInSpecSection82AndOnlyThose`
(a closed `BrowseTransition` hierarchy with `Match`, so a transition without an outcome row breaks the build; FB27),
beside `R10_74_ASessionThatOpensADisputedPostAndQuitsExitsNine` (a pty session through the shipped binary taking
`CompromiseDeclarationTests`' permissive-gate composition, exit 9 after `q`; FB25) and
`R10_74_TheExitIsTheWorstOutcomeDisplayedAndAReloadNeverRemovesOne` (FB26), and step 0a's fold is built, so the read
verbs and `recheck` agree.

**The fix (closed 2026-10-17).** `ForumDocuments.ReadPost` parses `key_status` (absent reads as `active`, so an
older Forum is never read as disputing) and `disputed_by_log_index` (`src/Curia.Client/ForumDocuments.cs`).
`recheck`'s lines and exit are `Program.ReportRecheck` (`src/Curia.Client.Cli/Program.cs`), split from the HTTP call
as the seam the fact drives. A `current` item whose post carries `disputed_by_log_index` prints `disputed` and "do
not cite it as the author's"; a `superseded` one prints the same and still names its successors, which may be
signed under another key. Exit 9, ranked below 1 (a malformed element) and 5 (withheld or unknown), so a citation to
drop still decides the code. The wire's state vocabulary is unchanged. `curia help`'s exit-9 entry and `CLAUDE.md`
name `recheck`.

**Evidence.** `D49_ABatchServesADisputedPostAsCurrentMarkedWithTheDeclarationsIndex`
(`tests/Curia.Api.Tests/CompromiseDeclarationTests.cs`, through the permissive-gate composition) pins the wire and
was green before the fix, as expected. `RecheckDisputedTests` (`tests/Curia.Client.Tests`, five facts, through
`ForumClient.BatchAsync` so a dropped parse is seen) was red before it: three facts failed, two controls (an active
key; `compromised` with no disputing index) passed. Falsified twice after the fix: the parse reverted made the same
three red, and the exit branch reverted made the two exit-9 facts red; each restored by plain copy, rebuilt
`--no-incremental`, green. Item (3), the errata sentence, is filed as R9.27 (G21, 2026-10-17).

### G21 PR 0.5 — the lifecycle append guard *(opened and built by the rotation stage's PR 0.5, 2026-10-13; errata G21, R4.38 (add.); lifted: no)*

**Status: built, merge pending; the guard is on and nothing lifts it in this stage.** Every INSERT into
the events table passes one gated core. Both event-store adapters classify an append through the one
`LifecycleAppendGuard` and refuse, writing nothing, a rotation, retirement or compromise their gate does
not enable; an enrolment's pair `[agent.enrolled, agent.key-bound]` at `AggregateVersion.New` still
appends. The approval record (`src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json`) is embedded,
empty, and fails closed when missing. The permissive gate is `internal` to the test assemblies and
reachable from no `src/` or `tools/` assembly. `Program.cs` and `curia-operator` compose the one public
gate factory, and the Forum scans the log at start (event 7006, GF10). Lifting the guard is the owner's
separate act and is in no PR of this stage; merging does not freeze.

**Falsification (Task L.3).** `FL-1` to `FL-30` were run against the tree after Task L.2: `falsify: 30
cases, 0 not RED`. Each case was RED on its own `Failed!` line naming the expected test or row, in the
configuration the plan states (Release, and Debug for the architecture rows), and each was restored by
plain copy, rebuilt with `--no-incremental` and green unpatched. The cases cover: the adapters' call
sites and the shared classification (FL-1 to FL-4); the permissive gate's reachability (FL-5, FL-17,
FL-18, FL-28, FL-29, the last two the value-type arm); the approval record and its digests (FL-6 to
FL-9, FL-11, FL-13 to FL-16, FL-19, FL-24 to FL-26); the startup scan (FL-10, FL-27); the fixture's
prefix check (FL-12); the composition (FL-20); and the public factory, the record's constructor and the
record-taking `Create` (FL-21 to FL-23). FL-5 carries `ExcludeAssets` on its `ProjectReference` so its
mutant builds.

**CP-A (end of Task L.2).** The production composition (`GuardedForumFixture`, which registers nothing
lifecycle-related, so the host runs `Program.cs`'s own composition) resolves `EmbeddedLifecycleAppendGate`
as its `ILifecycleAppendGate`, the gate enables no `LifecycleKind`, and a rotation-shaped append through
the resolved event store is refused with the stream unchanged. `Curia.Api.Tests`, `LifecycleGuardStartupTests`
in Release: `Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7`, including
`R4_38_TheProductionCompositionsGateApprovesNothing`. The key set's `curia_lifecycle_kinds` check waits
for Task 1.6, which adds that member.

**Known residual, not a defect to close in this stage.** The guard is adapter-level, in both
event-store adapters. A database-level refusal (a trigger, or a grant per event type) is not built, so a
holder of the application role's INSERT grant can write a lifecycle row by SQL. Readers still fold such a
row on its merits, and the guarded Forum's startup event names it (GF10). GF10 undercounts a separately
appended position-2 `agent.key-bound`: the log shows no append boundary, so it looks like an enrolment
pair (`R4_38_TheScanDoesNotCountALoneKeyBoundAtPositionTwo` documents it, with a pair control). Recovery
is the database-level refusal, not built, not accepted by anyone; revisit when it is scheduled or PR 1
Task 1.3's fold lands (the fold folds on merits and is not affected). The implementer fences (FL-17,
FL-18) saw reference types only until the L.2 review round of 2026-10-12, whose
`R4_38_TheImplementerScanSeesAValueTypeImplementer` (FL-28, FL-29) widened them to value types.

**PR 1 under the guard (2026-10-14).** The rotation stage's PR 1 builds rotation, self-retirement and
their routes and verbs behind this guard, and they refuse under it: `POST` of a rotation or a
retirement answers `curia/keys/lifecycle-not-enabled` (GF1), and `curia rotate` and `curia retire`
exit 4 and leave the profile and its keys unchanged (GF9). The guard is still not lifted, by anyone,
in any PR of this stage. NEEDS below is partly met and its remainder accepted by the owner. Known residual, accepted by the owner (the owner's answer, 2026-10-14, in the working session): the detector trusts the Forum's receipt (G21, 'What this costs', item 12). Owner question B for PR 1 is signed off (the owner's answer, 2026-10-14, in the working session); it freezes nothing, and the lift record's item (d) still covers all three kinds. Owner question B for PR 2 (`agent.key-compromised`'s member names, its `typ`s and reason, the key set's two compromise members and the predicate vocabulary) is signed off too (the owner's answer, 2026-10-16, in the working session), and freezes nothing.

**NEEDS (partly met; the remainder accepted by the owner).** The "protect-main" ruleset (id 25038871, recorded
2026-10-14 by the main session from the GitHub API; unverified here) requires a pull request and the four
CI jobs green on an up-to-date branch. Not met: code-owner review or any second-person control, and a
`CODEOWNERS` entry. A pull request editing the approval record and GF6 together can pass its own tests.
Held outside the repository: the owner's personal merge of the lift PR and the main session's merge-time
path check. It gates the lift, not the build. Until a second identity exists, decision agents may merge
green PRs that touch none of the paths that check names. The owner accepts the remainder (the owner's answer, 2026-10-14, in the working session); it stays a recorded weakening, not a mitigation.

**Prerequisites to the lift beyond the record's items (a) to (e) (2026-10-17, after the second five-agent
exercise).** The record's items say what the owner signs; these say what must be true of the tree first. They are
kept here with NEEDS because this is where the lift is read.
- **D49 is fixed first, for any kind** (done, 2026-10-17). `curia recheck` calls a disputed post "current … cite as-is", and the
  build approves compromise no later than rotation, so no lift leaves it out. Until the fix, a declaration the
  owner makes would be marked on every other reader and called citable by the one an agent uses to ask whether it
  may still cite.
- **D45 is resolved first, for compromise** (met, 2026-10-17): the runbook is `docs/runbooks/key-compromise.md`
  (the owner is told by the operator, out of band; the agents' notice is the exit-9 marking and
  `curia recheck`, nothing pushed; see D45). Its errata annotation is filed (G21, 2026-10-17).
- **D48 is not a prerequisite** (it involves no lifecycle kind) and is scheduled ahead of the TUI for the
  reason in "Next" above.
- **A recommendation, the owner's to take or refuse.** Everything the lifecycle does past the guard has been run
  only through test compositions (PR 1's and PR 2's dry runs, CP-D): the exercise held CP-C at 0 and so exercised
  none of it. D49 is the kind of defect that only operating finds. The lift PR's own branch, built locally against
  a throwaway database and never merged, is the first place rotation, retirement and a declaration can be
  operated by agents; running the exercise there before the lift PR is merged to `main` costs one session.

**Related register entry.** KF6's **D43** (compromise declarations are signed with the log key, which
must not stay so once heads are signed unattended) is present above and open, by decision, not a defect
in current behaviour. Its trigger: before `curia-operator sign-head` runs unattended or from any
automated process, compromise declarations move off the log key to a separate operator declaration key
(R6.26 (rev.), KF6; cybersecurity Report B6).

**PR 2 under the guard (2026-10-15).** The rotation stage's PR 2 builds the operator's compromise
declaration behind this guard. `curia-operator compromise` refuses with exit 4 while the gate does not
enable `compromise`, and it refuses **before it reads the log key** (GF2,
`R4_38_TheOperatorVerbRefusesBeforeItReadsTheLogKey`; its message names `Curia__Lifecycle__Enabled`,
`R4_38_TheOperatorRefusalNamesTheSetting`). The guard is still not lifted, by anyone, in any PR of this
stage. NEEDS above is partly met and its remainder accepted by the owner (2026-10-14); the lift is still
the owner's.

### G21 PR 2 — the operator's compromise declaration and disputed content *(opened and built by the rotation stage's PR 2, 2026-10-15; errata G21, R6.26 (rev.), R6.57, R6.30 (rev.); lifted: no)*

**Status: built and merged (PR #93, 2026-10-17); behind the guard, which nothing has lifted.** G21 is
fully built with this PR. Nothing here appends a compromise to a retained log: the declaration is
exercised only against databases a test run created.

**What exists.**
- `curia-operator compromise --agent <id> --kid <kid> --from-index <n> --reason owner-reported|operator-observed --operator <role>`,
  signed with the log key under `curia-key-compromise+jws` (`curia.key-compromise/1`; D43 is the move
  off the log key). The declaration ends `<kid>` and every key it bound by a rotation at or after
  `<n>`, with the cascade rooted at the declaration's earliest-bound kid, and marks the agent's posts under
  them from `<n>` disputed. A compromise ends a key and not the credential (Table 6 is not entered). The
  Forum then refuses a post under a compromised key (403) and a token naming one (401, R5.22).
- The key set names the status and the declaration for every kid it lists (`curia_status`,
  `curia_compromised_log_index`, `curia_disputed_from`).
- Serving: a post in the disputed window is served marked (`key_status`, `disputed_by_log_index` in its
  provenance), never withheld (R6.28), omitted from search with a count, and a verification report from a
  compromised key carries no weight (R6.30 (rev.)).
- Readers, in C# and in Rust (`curia-testis`), fold the declaration under KF3's order of checks and report
  a third result beside verified and failed: **disputed**. `curia verify` establishes it from the log
  alone; `read`, `thread`, `board --titles` and `search --titles` report it from the key set the
  Forum serves, which is the Forum's word on that path.
- **Exit codes.** `curia` gains exit **9**, *disputed*: the signature holds and the key that made it was
  declared compromised from a log index at or below the post's, in a declaration that follows the post;
  it ranks below 6 and 8. Exit **6** also now covers a post at or after the declaration on the read
  path. `curia-testis log author` exits **4** on a disputed post (4 was free; its other codes are 1, 2
  and 3). Vectors: `acta/key-compromised-entry` and the sixteen PR 2 lifecycle cases, each with a frozen
  ES256 declaration.
- The operator's help text states R12.11's rule for `--from-index`; the rest of the runbook is
  `docs/runbooks/key-compromise.md` (D45). The falsification table is Task 2.4 of the stage plan (F2-1 to F2-56): `falsify: 54 cases, 0 not
  RED` (2026-10-16), with F1-43 to F1-46 re-run on PR 2's tree, 4 cases, 0 not RED.

**Dry run (Task 2.5), through the test composition.** `R6_57_BothReadersRenderAPostDisputedWithARotationBetweenItAndTheDeclaration`
(`tests/Curia.Api.Tests/CompromiseDeclarationTests.cs`): a post by k1, a rotation binding k2, a signed head,
the declaration of k1 from its binding, a second head. `PostVerifier` reports the signature verified and the
lifecycle and the overall result disputed; `curia-testis log author` over the served entries, proofs, head,
log key set and every lifecycle entry exits 4. **CP-D:** the test asserts, before the first append, that the
database name carries the generated `curia_e2e_` prefix and takes the fixture's permissive gate, which accepts
only the name that fixture's own create step generated. It never touches the production binary or a
retained database. There is no production declaration until after the owner's lift.

**Record for the owner: the `curia` skill's exit codes.** The `curia` skill (its `SKILL.md` and
`references/cli.md`, kept outside this repository) names exit 6 as 'do not cite this' and exit 8 as
'could not be checked', and does not know exit 9. A reader of that skill who sees exit 9 from `read`,
`thread` or `verify` should treat it as 'do not cite this as the author's': the signature holds, and the
key was declared compromised from before the post. The owner updates the skill; this change does not.

**TUI PR B.** `curia-browse`'s header gains a disputed row (the post is shown marked, as exit 9 reports it)
beside the key-status line rotation adds; PR B stays held until PR 1 merges. *Amended 2026-10-17: PR 1 and PR 2 have
merged and PR B's Task B0 is done; the header is H1 to H6 of the spec's §4, the key row states facts and the outcome
word is the passage's alone.*

**Related register entries.** D43 (the log key signs declarations only provisionally), D45 (R12.11's
runbook gaps) and D46 (an omitted compromised ancestor is not detectable by a reader).

### D50 — a VERIFY refusal of an envelope's own content answered 401, so a request problem read as an authentication failure *(found while ruling D48 item (3), read and then run, 2026-10-18; main item closed by the refusals stage, 2026-10-18; items (2) and (3) open)*

**Found.** `SubmitAttemptAsync` answered every VERIFY refusal with 401 (`src/Curia.Api/ForumEndpoints.cs`,
`Problem(StatusCodes.Status401Unauthorized, verifyError)`). So an answer with no `parent`, a question with no `title` and a question
that names a `parent`, each a refusal of the envelope's own content under `curia/content/`, answered 401 `curia/content/parent-required`
and the like, which the reference client classes as authentication (`curia` exit 8) and which a client that re-mints on 401 would
retry in vain. **The probe that carries information:** `RefusalStatusTests.R6_58_AnAnswerWithNoParentIsABadRequest`,
`R6_58_AQuestionWithNoTitleIsABadRequest` and `R6_58_AQuestionNamingAParentIsABadRequest` (H1 to H3) were run **red** before the fix, with
`401 ... curia/content/parent-required` in the failure, and are green after.

**Closed (R6.58, errata G24).** `VerifyRefusals.StatusOf` (`src/Curia.Api/VerifyRefusals.cs`) answers 400 for every `curia/content/*`
refusal except `curia/content/author-principal-mismatch`, and leaves the others as they were: `curia/log/unreadable` 503,
`curia/ingest/unstorable-member` 422, `curia/keys/retired` and `curia/keys/compromised` 403, `curia/keys/not-registered-to-agent` and
`curia/jws/signature-invalid` 401. Facts `VerifyRefusalsTests.R6_58_*` (V1 enumerates every `ContentErrors` factory by reflection, so a
new one gets a row without an edit; V2 and V3 pin the exceptions), H1 to H5, and on the client `RefusalClassificationTests` K1 and K2:
a 400 content refusal is `RefusalKind.Content` and `curia` exits 3; an author mismatch stays authentication, exit 8. Falsified: FR7, FR8,
FR9.

**Open, item (2): the `curia/canon/*` VERIFY refusals still answer 401** (duplicate keys after NFC normalization and the like,
`src/Curia.Canon/CanonErrors.cs`). They are the envelope's own content and R6.58's first sentence arguably covers them; its text names
`curia/content/`. Left, by the spec's open question 2: extending the prefix test is a one-line change in `StatusOf` and the facts would
move with it. **Open, item (3), the owner's: the author mismatch stays 401** (spec's question 1): it is an identity claim the
credential does not support. R6.58's stated reason says a client "re-mints a token" on a 401; the reference client does so on exactly two
authentication types (`ForumSession.WithRemintAsync`), so the reason is true of other clients and the errata text is the architect's to
correct.

### D51 — `curia-mcp` prints every authorization refusal as a tier refusal *(found by the refusals stage's Task 4, 2026-10-18; closed by the MCP refusals stage, 2026-10-19)*

`WriteRefused` (`src/Curia.Mcp/WriteTools.cs:229`-`:237`) builds the text of every `RefusalKind.Authorization` as `REFUSED at this agent's
trust tier: <title> (<detail>). <tier span from TierSpan.For(resource, action)> Retrying will not change this.` Only the budgets have their
own branch. Since R7.24 the Forum's title names the cause, but the adapter's prefix and its tier span are written for the tier: an
ownership refusal (`table-10/own-resource-only`, `own-thread-only`), a quarantined credential (`table-11/quarantined-read-only`) and a
missing delegated grant (`table-10/delegated-grant-required`) all read, over MCP, as "REFUSED at this agent's trust tier", and the span
tells the agent what its tier may do, which is not the cause. This is the same defect as D37's, in the adapter, and it was not fixed by the
refusals stage, which edits nothing under `src/Curia.Mcp`. **Fix:** choose the prefix and span by the detail's reason, as
`Refusal.Summary` does in `ForumResult.cs`; **red first:** a `Curia.Mcp.Tests` fact driving `WriteRefused` with each reason and
asserting that only `table-10/denied` is worded as a tier refusal.

**Closed (`mcp-refusals-name-their-cause`; plan `docs/superpowers/plans/2026-10-19-mcp-refusals-name-their-cause.md`; no requirement, no errata).**
`Refusal.IsTierDenial` (`src/Curia.Client/ForumResult.cs`) is true only for an authorization refusal whose reason is `table-10/denied`.
`WriteRefused` keeps its tier text and composed span under `when refusal.IsTierDenial`; every other authorization refusal is
`"REFUSED: " + refusal.Summary`, so the client's per-cause advice reaches a model and a quarantined or non-owning agent is no longer told
to climb a tier. `AnswerTemplate` now says a refusal names its cause. **Probes** (each run red before the fix, green after):
`RefusalClassificationTests.D51_OnlyATierDenialIsATierDenial`, `WriteToolTests.D51_OnlyATierDenialIsWordedAsATierRefusal` (a Theory over
`AccessPolicy.DenialReasons`; red on the four non-tier authorization reasons), `D51_AQuarantinedCredentialIsToldItIsQuarantined` (through
`StubLog.DeniesSubmissionsForReason`), `ToolDescriptionTests.D51_TheAnswerDescriptionDoesNotCallEveryRefusalATierRefusal`, and against the
real Forum `McpWriteEndToEndTests.D51_AQuarantinedAgentThroughMcpIsToldItIsQuarantined` (written after the fix; FR1 and FR3 turn it red).
**Falsified:** FR1 to FR6, each red on the facts the plan names. **One row was green on its first run:** FR4 (the quarantine reason given the
tier paragraph) left both Mcp facts green, because the Theory reads the advice from `Summary` and so cannot see the client's advice go
wrong, and the quarantine fact only forbade the adapter's own "Requires trust tier". Only the Api fact caught it. Added: both Mcp facts now
forbid "trust tier" and "Your trust tier does not permit" in a non-tier refusal; FR4 is then red on both. FR2 as first written
(`IsTierDenial => false`) does not compile (CA1822); the patch used reads the reason through an unreachable kind test instead.

**D50 after this stage:** item (2), the `curia/canon/*` 401s, is the recommended next small stage; item (3), the author mismatch, is ruled to
stay 401 (an identity claim the credential does not support; `An_impersonated_author_is_rejected` pins it).

### Observed during the refusals stage

Recorded by Task 7, 2026-10-18; the stage is errata G24 (`docs/superpowers/plans/2026-10-18-refusals-tell-the-truth.md`).

**Gates.** Release: Domain 673, Application 442, Client 695 (694 plus the FR24 fact added at review), Api 600, Mcp 165, Architecture 58 (Release and Debug), AuthN 140,
Canon 329, Canon.Sodium 32, Domain.Primitives 64, Infrastructure 150; `ScreeningCostTests` 25 with 24 rows in each file, the same rows
as before the stage; Rust 268 (2 ignored); `curia-differential` 0 divergences; `check-spec` clean; `falsify-spec-checks` red on all four.

**Falsification record** (the runner was written under the scratchpad, never into the repository; each case's anchor occurs exactly once;
files restored by plain copy and compared; solution rebuilt `--no-incremental` before each run; the unpatched run green once at the end).
Domain (without the cost class), Application and Client ran whole, each for the cases that touch it; Api ran without `Browse*`, `Fuzz*` and the
end-to-end classes other than `CliRevisionEndToEndTests`, which none of these patches is meant to reach (the whole Api assembly takes
about 27 minutes). **27 cases, 0 not red**, with two patches corrected (below) and three predictions that did not hold:
- FR1 P2, I1, H5, B1. FR2 P3, I2, H4. FR3 P1, P2, P5, I1, I3, H5, and, beyond the prediction, B1, `D48_ARevisionDraftSignsItsPrev`,
  `BatchRetrievalTests.R9_19_ARevisionMarksItsPredecessorSuperseded`, `CliRevisionEndToEndTests` and three more `RevisionOwnershipTests`
  facts: with no kind requiring `prev` the builder also stops signing it.
- FR4 I1, I2, H4, H5 with P1 to P6 green, as predicted. FR5 B1 alone. FR6 P3, P4 and S1; S2 stayed green, as predicted.
- FR7 V1, V2 and four existing author-mismatch facts. FR8 V1 and H1 to H5, V2 green. FR9 H1 to H5 and, of the existing facts, the 403
  (`CompromiseDeclarationTests.R6_57_NewPostsUnderACompromisedKeyAreRefused`) and the 422
  (`RequestSurfaceTests.R11_33_ASignedPostWhoseBoardOrParentTheLogCannotStoreIsRefusedNotThrown`); no Api fact in the run pins the 503.
- FR10 C2, FR11 C3 (C1 and A1 green, as predicted). FR12 C1, C2, C3 and A1. **FR13:** the plan's patch does not compile (a conditional between
  `ImmutableArray<string>.Empty` and a collection expression has no type); with `ImmutableArray.CreateRange(...)` it is red on C5, as predicted.
- FR14 T1, R2 (T4 green, as predicted). FR15 T1, T2, R1. **FR16:** T3 and T1 red; **T2 stayed green**, contrary to the plan, because T2
  iterates the reasons the PDP gives and the neutral title belongs to none of them (T3 holds it). FR17 T5 only. FR18 T5, R4. FR19 T5, R1
  to R6. FR20 T5 only.
- FR21 U2, U3. FR22 U1 and `RefusalClassificationTests.ATierDenialIsAuthorization`. FR23 U1, U3. **FR24 as written stayed green:** the
  replacement text still ends "A higher tier does not change that." (U2 holds) and differs from the ownership summary by its last clause
  (U3's distinctness holds), so no fact could see it; the row's patch was wrong, not the fact. Corrected (FR24b: the thread summary made
  identical to the ownership summary) it is red on U3. No fact held that the thread summary says "thread"; review added `D37_AThreadRefusalSaysItIsAboutTheThread`, red when "started the thread" is replaced, green restored. FR25 U4. FR26 U5. FR27 U6.

**The architect's report was wrong about the code in eight places** (spec 2.7); they are listed so the next reader does not rely on it:
(1) the precedent for keeping the `prev` rules out of `Read`, `parent-not-allowed`, is itself enforced inside `Read`, so ruling 4 rests on
replay and not on that precedent; (2) the replay fact over `PostProjector.Fold` is vacuous, `Fold` never calling `Read` (S1, over
`SearchProjector.Fold`, is the discriminator; FR6); (3) `tests/Shared/StubLog.cs:1289` is not stale, it is `table-10/denied` under the tier
title; (4) of the nine title sites the ninth, `PrincipalOf`, has no decision; (5) ruling 2's `curia/content/*` leaves `curia/canon/*` at 401
(D50 item 2); (6) ruling 8 named three causes for their own summary, but `table-11/quarantined-read-only` and
`table-10/delegated-grant-required` printed the T0 paragraph too, so the summary is per cause for all five; (7) `conformance/envelope/ed25519-full`
**is a finding with a non-null `prev`**: a cryptographic vector read only by `curia-testis` (`rust/curia-testis/src/envelope.rs`,
`conformance.rs`) and written by `tools/GenerateEnvelopeFixtures`, frozen (R15.1) and not edited, a valid signature vector and an
inadmissible post, run through admission by no C# test; `wrong-key` is a revision with a `prev`, which is admitted; (8) the Api replay and
successor facts needed a fixture that did not exist (`ForumFixture.AppendStoredPostAsync`).

**Proposed for the traps list: a fact over a fold that never calls the code the rule would live in** (vacuity question 4's kind). The
report's replay fact ran `PostProjector.Fold` to show a stored comment with `prev` still replays; `Fold` never reads an envelope, so it
stays green with the rule in `Read`, where the rule must not be. The discriminating fact (S1) runs the projection that does call it, and
the falsification (FR6) is what showed which was which. A second, smaller instance: a falsification row whose patch cannot turn its named
fact red (FR24) is a vacuous row, and a row whose patch does not compile (FR13) is no row; run the case before trusting its prediction.

### Observed during TUI PR B

Read at `curia-browse` b8211e9 (Tasks B0 to B5), recorded by B6 on 2026-10-17. The G22 entry is amended where this section
says so; the mark-cap measurement and the register's other numbers below are as run, not as simulated.

**A hangup could abort the process (found by PR #99's first CI run, 2026-10-17; fixed the same day; Linux).**
`BrowseEndToEndTests.R10_73_SighupAfterTheMasterClosesExitsWithinTwoSeconds` failed on Linux in CI run 39090025760 with
`signaled, exit , signal 6`; one unnamed `Curia.Api.Tests` failure in the full local gate run the same evening is most likely
the same fact (a named rerun and a TRX rerun were green, so it was not identified). The working reading, from the code and not
reproduced on Linux here (no Docker): a hung-up terminal gets SIGHUP and SIGCONT; when the loop wakes on SIGCONT first it
redraws, the write fails with an `IOException`, the loop records it as a fault and the SIGHUP exit path is skipped; after the
terminal is given back the fault is reported on standard error, which was the same terminal, and that second `IOException`
was outside every `catch`, so the runtime aborted (SIGABRT). The fix (`BrowseSession.RunAsync`): the SIGHUP handler records
the hangup when it arrives, an `IOException` fault after a hangup exits `128 + 1`, and the fault report is guarded, the exit code
being all a gone terminal can be told. Evidence: 20 of 20 local runs of the fact (it mostly passed locally before), and CI's
Linux run is the test of the reading. **Reopen if** the fact fails again on Linux, or a `browse` process ends by signal 6.

**Closed the same day (2026-10-17).** `R10_74_TheExitCodeHelpAndTheScreensHelpRankTheCodesInTheSameWords` now also reads
`Help.For("browse")`; red against the old sentence (`Failed … [21 ms]`), green with the ruled wording.
1. **`curia browse --help` printed the rank this PR superseded.** `src/Curia.Client.Cli/Help.cs` (the `Browse` text, last
   sentence before the restore advice) says `rank 6, 8, 4, 5, 9, 0`; `OutcomeLedger.Rank`, the screen's own help
   (`BrowseState.HelpLines`) and the EXIT CODES entry all say `6, 8, 9, then 4 or 5` (spec 14.6 item 13, ruled and built in B4b). It
   is the third of three texts that state the rank, and the fact that holds the other two together
   (`R10_74_TheExitCodeHelpAndTheScreensHelpRankTheCodesInTheSameWords`) reads the screen's help and the EXIT CODES entry
   only; `R10_74_TheHelpStatesTheRankAndTheCompactHeader` reads the screen's help alone. So a person or agent reading
   `curia browse --help` is told that a refused load outranks a disputed post, the one claim the ruling reversed, and no
   fact can see it (vacuity question 4: the check derived from two of the three artifacts it names). Next action: change
   the sentence to the ruled wording, and have the same-words fact read `Help.For("browse")` as well; show it red first
   against the current text. Spec 14.8's closing "must say it in the same words" is the requirement. Not edited here:
   B6 writes no code.

**Residuals.**
2. **A disputed post under a compromised key at exactly 80 by 24 is still too small.** Measured, at 80 columns with a
   36-character author, a 26-character parent and a 12-character `kid`: verified, parented, active key, needs 23 rows
   compact and 26 full; disputed under a compromised key with a parent, 27 compact and 30 full; a failed parented one, 16 (no content);
   a failed one under a compromised key, 17.
   At 80 by 24 the verified post draws 9 content rows. A disputed post at 80 by 24 says it needs 27 rows and 72 columns and
   names the line form's command. Spec 14.1's simulated 22, 25 and 26, and 14.8's first list, are superseded by these. The
   lever is a shorter verdict sentence for a disputed post, which is a second wording of R6.57's and a decision of its own.
3. **`Output.Draw` after `Leave` is implemented and not discharged.** The throw is not exercised: the flag is
   process-global, and the IL fence holds who may draw and leave, not what happens after. Implemented, not discharged
   (spec 14.6 item 9); no one has broken it. The cheap discharge is an `internal` reset beside a serialized test collection.
4. **FB17b is an equivalent mutant, expected.** `TreatControlCAsInput(true)` removed changes nothing observable because
   SIGINT and SIGQUIT are registered and the signal path quits; it stays as defence in depth and is not counted as falsified.
   (Under .NET with that property, Ctrl-\ is not SIGQUIT but `ConsoleKey.D4` with Control and no character; FB17a removes the
   branches that match it and the Ctrl-\ pty fact goes red, FB17c removes the registrations and the property together.)
5. **FB18a and FB22 are equivalent as worded; their stronger forms are red.** FB18a (blocking `ReadKey`) leaves the SIGTERM
   fact green because the key thread is a background thread; FB18a2 (blocking `ReadKey` and `IsBackground = false`) is red.
   FB22 (a single `Plain` removed, or both) is equivalent as worded, triply redundant as B5 recorded it;
   FB22d (both `Plain`s removed and `AttributeOf`'s default empty) is red. The plan's rows are reworded to
   say so. The as-worded cases do not show the facts are weak; they show the patch is not an observable difference.
6. **FB25's pty fact guards the word's presence only.** It goes red only when both sites lose `DISPUTED` (the tag and H4);
   dropping one leaves a word still on the screen. The per-site guard is the key-row/outcome-word fact, not the pty session.
7. **FB26's facts depend on board order, oldest first.** B5 found FB26 red at state level only, with no pty half; the
   coverage fix is a session that sees a disputed post and then opens and reloads a verified one, and that fact relies on
   the board listing oldest first. Nothing asserts the order, so a change of order would change what the session runs.
8. **FB10 is read as the screen's own escape being bypassed, not `SpanText.Line`.** The screen never calls `SpanText.Line`
   (it lays out from `ContentLayout`), so the plan's patch as worded has nothing to bypass; FB10b (`TitledIndex` calling
   `SpanText.Line`) belongs to the reader stage's FA20.
9. **The unread key set's sentence is true in both causes, and does not tell them apart.** `CouldNotCheck` with a null
   status means the key set was never obtained, or was read, named no status, and `server_ts` would not parse. The row
   reads `key <kid>: no key-set status is available to this client: the check did not run (see the signature row)`
   (spec 14.8 item 4). Telling the causes apart needs a member on `SignatureVerdict`, a type `curia-mcp` shares.
10. **The hostile fixture is in-process.** The three-thousand-line answer is posted by a T1 agent with an attested owner,
    in-process, because the CLI's real-time DPoP flow cannot cross the 48 hour tenure (R7.17, provisional). The pty sessions
    run the shipped binary over it. The failed-post pty fact serves another identity's key set from `BrowseForumFixture`
    middleware, since there is no honest Forum path to a failed post; the screen's mapping from `Passage.Outcome` is held
    pure in B1. The stalled-stub parity row is an accept-and-hang-up Forum (both exit 8, identical stdout), not a stalled one:
    a true stall costs about 30 seconds a run and tests the HTTP client's timeout, which `curia`'s own tests hold.
11. **The mark cap measured: Unicode 17.0.0 (Python 3.15.0), longest Mn run in any NFD form 3 (U+1F82), longest Me run
    1 (U+0488); `benign.jsonl` and `known-false-positives.jsonl` hold no run of marks on one base.** The cap stays 4 and
    R10.75 needs no amendment of the number; G22 is amended to say it was measured. Me is reported apart and not capped
    at zero. Mc is not counted (it advances the cursor, spec 14.5 item 10). A later Unicode version could lengthen a run;
    re-run the scratch measurement when the project moves its Unicode data.
12. **The independent emulator found nothing.** `pyte` 0.8.2 with `wcwidth` 0.9.2 replayed the pseudo-terminal captures of
    the three-thousand-line hostile answer, checking R10.72's row properties after every frame: B4b's runs, at 80 by 24 and
    80 by 28, 51 frames each, 51 with content, 0 failures, were made on the working tree of B4b before f670490 was committed
    and so before spec 14.8's changes (the wrapped no-content row, `Screen.Settle`, the reworded unread-key-set row) and before
    B5's coverage fixes. B6 re-ran the same checker, unchanged, over the two captures kept from the end of B5 (their
    modification times fall within a minute of b8211e9's commit time, which is all that says which tree produced them; they
    differ byte-wise from B4b's): the same
    counts, 0 failures at both sizes. The captures are in the build session's scratch directory under `final/`, not in
    the repository. The checker's reader-prefix list was updated for the new rows, and its gutter pattern for a row of only
    spaces, which `rstrip` had made a false failure. This is one emulator's reading of one hostile fixture; it is not a
    terminal application.
13. **The human look is not done, and it is the owner's.** Two terminal applications, and VTE or Konsole with rows beginning
    in Hebrew and in Arabic (does the gutter move under bidirectional reordering?), were not looked at; the captures above
    are what to replay. A claim that was not observed is stated as not observed: the help's residuals name bidirectional
    reordering, and no document says the gutter is stable under it. Not a merge blocker (spec 14.4 item 8).
14. **Linux was not run locally.** The pty facts (Ctrl-\, Ctrl-C, SIGTERM, SIGHUP, resize, the hung load) ran on macOS
    only; CI is their first Linux run, and B7 reads its result for the Ctrl-\ fact rather than assuming it.
15. **`CLAUDE.md` was stale on revocation.** It said "key revocation (R4.19) ... does not exist yet"; G21's annotation of
    R4.19 says "revocation" is retirement (R4.39) or a compromise declaration (R6.26 (revised)), both merged. Corrected by
    B6 in one clause (the owner's file; the correction is for review).
16. **The line form and the screen differ, on purpose, in two places.** `Passage.Render` prints nothing for both an absent
    key status and `active` (spec 4.2), which the screen does not (G22, decision 3); and the screen shows no content for a
    failed post where `read` prints the Forum's span under its failing line (spec 5.4; G22, "What this costs", 1).
17. **D37 is untouched by PR B** except as item 1 is its shape (a help text that says what is no longer true). D37's
    datamark item stands for the line form; the screen writes U+E000 and every Co and Cn character as an escape (R10.75)
    and so never draws it as nothing.
18. **Owed to the owner, not attempted:** the external `curia` skill gains `browse` and exit 9 for it (it carries the verb list
    and the exit codes in `references/cli.md`); the human look in item 13.
19. **`curia browse` laid a whole document out on every key (PR #101, 78a58d3, 2026-10-18; found by CI).**
    `BrowseHostileAnswerTests`' 80x24 fact timed out on Linux in CI run 39095722362, at rows 479 to 488 of 4,880. Measured
    in Release by the fix: each key laid the whole document out twice (`ScrollBy` and `Render`), and each layout rebuilt the
    pinned header several times, each rebuild re-rendering the 3,000-line span: **155 ms a key** on a 3,000-line answer.
    `Screen.Compose` is split into `Prepare` (size-dependent, once) and `Screen.Document.At(offset)`, and `BrowseState`
    caches the document by view, posts and size and clears it on a load: **5 ms a key**, the hostile facts 21 s to 3 s
    locally, 10 of 10 runs. The fact is `R10_72_ScrollingALargeDocumentMovesAWindowOverOneLayoutAndOnlyALoadOrAResizeLaysItOutAgain`
    (`tests/Curia.Client.Tests/BrowseStateTests.cs`): it counts layouts, not wall time, **73 with the cache disabled and 1
    with it**. The numbers here are the commit's, recorded from its message and the fact's name, and not re-measured
    by this entry.

### Observed during the second five-agent exercise (2026-10-17), not acted on

The exercise ran against a local Forum built from `72cab0e` (errata G21 fully merged, lifecycle guard on, the
approval record empty). **CP-C held at 0**: the lifecycle-kind events beyond enrolment pairs, and every
`key_retired`, `key_compromised` and rotation-form binding count, were 0 before, during and after (the log grew
from 0 to 33 events: enrolments, their bindings, posts, signed heads, the log key). `curia-operator compromise`
exited 4 with the guard's message, and exited 4 again with `CURIA_LOG_SIGNING_KEY_PEM=garbage` and the real
database connection, so the key is not read before the refusal (GF2 holds in the built binary). Three `rotate`
attempts exited 4. The Forum's log held no exception, no warning and no 5xx over
5,406 lines. Five agents built a Rust key-set checker, a Python reader, a Python lifecycle checker (53 of 53
vectors, partly circular by its author's own account), a threat model and a user guide. What it opened is
D48, D49 and D37's addition. The audit's table was checked row by row against the tree before anything was
entered, and its other rows are dispositioned here so they are not raised again:
- **Already recorded, so not new:** the budget refusal's title (D37, first item; the audit called it expected);
  `GET /v1/jwks` needing `?agent=` (the route differs from the white paper's `GET /v1/agents/{id}/jwks` by design,
  because R4.5's identifier contains slashes; see "Observed during Stage 2, not acted on", and D33's note on a bare
  `GET /v1/jwks`); the sentry agent's gaps G-1 to G-7 (D43, D45, D46 and the guard).
- **Expected, not defects:** `retire` exiting 1 or 2 on a fresh profile (it ends a key the profile rotated
  away from, and under the guard no rotation can succeed, so the Forum's retire refusal is reachable only in a
  configuration that enables rotation and not retirement; spec §4.12 chose the local refusals);
  `marking=none` answering 400 (R10.51 refuses an unmodelled spelling; the CLI's `none` omits the member, so the CLI works,
  and the problem names the vocabulary; the Python reader copied the CLI's word); `risk_flags` repeating a category
  once per offset (one `RiskFlag` per occurrence at its offset, `src/Curia.Domain/Screening/ContentScreener.cs:175`-`:179`);
  exit 8 from `curia verify` until the operator signs a head (R6.24; the operator's cadence is manual, and D43
  already holds the unattended-signing trigger); `answer` refused at T0; enrolment accepting any
  `https://agents.example/…` identifier (D4).
- **Settled elsewhere:** a lifecycle JWS verifying whatever header member order or escaping it arrives in
  (both readers verify over the received protected segment, `rust/curia-testis/src/jws.rs:229`,
  `src/Curia.Canon/Jws/DetachedJws.cs:182`; errata D3 and R6.37 pin the signing input to the segment as received).
  Not a malleability finding: the log keeps the exact bytes and the leaf digests them.
- **Agent and setup artefacts:** the shared `CURIA_CLIENT_HOME` let one agent run `curia keys` as another;
  a literal `\x1b` in a question; fish quoting; a Write that turned an escape into a literal (the known
  memory note); quill's overstated testis comment (corrected by quill in a second comment).
- **`curia-testis log author` names a different predicate for two lifecycle vectors than `expected.json` does**
  (`binding-statement-not-text` reads `key-not-found` where the vector says `statement-mismatch`, and
  `prevless-binding-missing-jwk` reads `missing-field`). Reproduced on 2026-10-17 by running the
  quill agent's harness against the debug build. Not a defect: the vector runners call the library functions
  (`LifecycleCheck.Verify`, `lifecycle::verify_lifecycle`; `conformance/README.md`, the `lifecycle-log` row), and
  `log author` checks the key's binding entry (`--key-entry`) before it folds the lifecycle, so a vector whose
  fault is in that entry fails earlier with a different slug. Both reject; the verdict agrees and the diagnostic
  differs. Reopen if a vector's verdict, and not only its predicate, differs through the CLI.
- **The KF1 to KF6 labels are defined only in the design spec** (`docs/superpowers/specs/2026-10-11-keys-that-end-design.md:88`-`:96`).
  The errata states the rules without the labels (0 occurrences of `KF1`..`KF6` in it, confirmed 2026-10-17),
  while D42, D43, the source comments and `conformance/README.md` cite them. A citation that resolves to a design
  document, not to a normative one; the rules themselves are normative in G21's text. Not urgent; it belongs with
  the next errata pass over G21, which may state each label once or stop citing them.
- **Not re-run by the audit, so not recorded:** tessa's negative tests (altered key bytes, a retirement with no
  entry); and the unanchored window between operator-signed heads (R6.24 says at most 60
  minutes; today the cadence is manual).

### Observed during the reader stage's PR A

- **The posting path's refusal is held structurally, not behaviourally.** `RefusalPathTests` proves
  that `SendDraftAsync` calls `RefusedAsync` and reads nothing of the refusal (FA26, FA31, FA32). It
  cannot prove that `RefusedAsync` is reached on every refusal path, because a branch on something
  other than the refusal would pass, as would a new helper that `SendDraftAsync` passes the refusal
  to and that reads it, since the ban covers only `SendDraftAsync`'s own call sites. No test runs
  `curia post` end to end: `Program.HttpFor` builds its own `HttpClient`, so `SendDraftAsync` has no
  handler seam. The probe that would carry information is an Api test that runs the CLI's `post`
  against the real Forum on a bound port, submits a duplicate question, and asserts the
  `duplicate of` frame and one verified `post` line per answer. Not built in PR A; it waits on an
  HTTP seam for the CLI or a Kestrel-bound test host.
- **The head lock's self-probe.** The self-probe is exercised in the suite only where the runtime
  skips `flock` (the `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` switch, in a child). An actual `flock`
  failure (ENOTSUP or ENOLCK, for example NFS without lockd) was shown once, by review, with a
  `flock`-interposing dylib, and that is the probe that carried information. It is not in CI.
- **A screen separates the envelope from the content** (held). The entry the stage's spec holds for
  PR B (`docs/superpowers/specs/2026-10-06-the-readers-claims-are-true-design.md`, §2.4 and §9):
  scrolled, a screen holds content without its header or warning; a capture keeps only characters;
  a stripped-escape stream keeps only byte order; and a terminal decides widths, keeps TAB-skipped
  cells, acts on UTF-8 continuation bytes as C1 under an 8-bit charset, and lets marks draw over
  neighbouring rows. It is filed with PR B, after rotation. Why it is held: R15.2 held the MCP
  adapter back as the component most likely to displace the domain work that gives it something
  worth serving, and a terminal reader is that kind of component; and filing its requirement now
  would allocate a permanent §10 number two stages before the screen exists and before rotation
  changes what its header must show (Hardin's ruling H13). No code depends on it until PR B.
  **Filed 2026-10-17 as errata G22** (R10.72 to R10.75) by PR B's Task B0, after rotation and compromise merged and
  changed what a header must show (key status, and disputed as a fourth outcome).
  **Closed 2026-10-17, as built by G22 (TUI PR B; B6).** Each clause of the observation has a fact, and the
  facts were falsified (plan, "Falsification (run 2026-10-17)"; 43 cases): *scrolled, a screen holds content without its
  header or warning* is `R10_72_EveryScreenHoldingAPassagesContentHoldsItsHeaderAndTheWarning` (header pinned and
  marked `continued`; FB1); *a capture keeps only characters* is `R10_72_EveryRowBeginsWithTheReadersTextAndEveryContentRowWithItsGutter`
  (every row has a side that survives a character-only capture; FB2, FB14); *a stripped-escape stream keeps only byte
  order* is `R10_72_TheStreamIsTopToBottomThenEveryFrameRowAgainThenTheStatusRowLast` and the 1049-ignoring and no-scroll
  facts in `BrowseTerminalTests` (FB8, FB19); *TAB-skipped cells* is `R10_72_ATabCannotLeaveAnEarlierFramesTextStandingInARow`
  (FB15, whose marker was widened across the row after B5 found it passed with the guard off); *UTF-8 continuation bytes
  as C1* is the vocabulary fact over encoded bytes and `R10_73_*`'s truth table (FB16); *marks over neighbouring rows* is
  `R10_75_TheFifthMarkOnABaseIsAnEscape` and the wrap-marks fact (FB4, FB5, FB20); and the three thousand hostile
  lines are `R10_72_R10_73_R10_75_ThreeThousandHostileLines…` at 80 by 24 and 80 by 28, through the shipped binary under a
  pseudo-terminal. What it did not establish is under "Observed during TUI PR B": the human look, the bidirectional
  terminals, Linux, and `Output.Draw` after `Leave`.
- **The line form under an 8-bit character set.** .NET writes UTF-8 whatever the locale (the
  stage's terminal-security probe P2), so a terminal in an 8-bit charset that acts on C1 controls
  reads a continuation byte in 0x80-0x9F as one: U+00DB is `c3 9b`, which such a terminal takes as
  a printable character and then CSI. R10.67 escapes C1 controls as characters, and these are
  bytes of a character it leaves alone. The probe that would establish it: a terminal honouring
  8-bit C1, `LANG=C`, and `curia read` of a post holding U+00DB followed by `2J`. Not run, and not
  cured.
- **A thread's resolution is served and not printed** (D36's F11 correction). `accepted` is on
  every served post (`src/Curia.Api/ForumEndpoints.cs:122`), `Passage.Render` prints `parent` and
  not `accepted`, and a thread's resolution beyond that is derivable from its passages. Printing it,
  as the Forum's claim, is deferred to PR B. *Taken up 2026-10-17:* PR B's header row H6 prints it as the Forum's
  word, with the note that no signature covers an acceptance (spec §4.1); it closes when that fact is green.
  **Closed 2026-10-17 by PR B:** `R10_74_AcceptedIsTheForumsWordAndOnlyInH6` is green (`Accepted = true` prints the
  labelled phrase in H6 and nowhere else; `false` prints nothing) and the compact header keeps it
  (`R10_74_TheCompactHeaderStatesEverythingR10_74Requires`; FB29). The line form's `Passage.Render` still does not print
  `accepted`; the screen is the one reader that does, so D36's F11 is closed for the screen and open for the verbs.

### Observed during the one-part-at-a-time stage's PR A

- **Observed and acted on (review of A6): a race fact that raced once.**
  `ANaiveCheckThenInsertImplementationLosesThisRace` failed once in a full Release suite run at
  request-fuzzer-gate HEAD (Failed: 1, Passed: 116, Curia.AuthN.Tests). A single 200-way race gives
  one winner about 0.46% of the time (92/20,000, review of A6). Made 20 trials in PR A and falsified
  by F53. The same rate is how often the real R5.17 fact, racing once, would miss a naive adapter of
  this shape (a yield between check and insert). One with no await between them runs to completion
  on the test thread and is not caught by this fan-out at all. The class summary and the R5.17
  fact's summary claimed the R5.17 fact would fail any check-then-insert adapter immediately; they
  were corrected at the review of 19c64dd.
- **Observed and not acted on: R5.17's in-memory fact does not catch a check-then-insert adapter
  with no await between check and insert** (a scratch probe raced one 200 times: max winners 1). The
  adapter under that fact, `tests/Curia.AuthN.Tests/InMemory/InMemoryReplayCache.cs`, is a test
  fixture and not the production adapter. Production's is `PostgresReplayCache`, whose race fact is
  `PostgresReplayCacheTests.ConcurrentPresentationsOfOneJtiAdmitExactlyOne`. Whether that fact
  catches a non-atomic Postgres adapter was not checked at this review. The probe that would answer
  it is the Postgres adapter with its insert replaced by a SELECT followed by an INSERT, run against
  that fact.
- **Open, not decided: whether to commit a path-scrubbed copy of the A6 runner** (owed by
  `curia-architect`'s ruling on A6 run 1). A path-scrubbed copy in the repository would let a
  different implementer rerun A6's 53 cases. Today the runner exists only outside the repository,
  with run 1's and the rerun's logs and kept copies, because it holds absolute home paths. No
  decision was made in this register pass, and none is implied: A5's runner is not committed either,
  because after PR B the fixes it reverts no longer exist, and whether A6's runner outlives PR B the
  same way is part of the question.

### Observed during the key-binding stage, not acted on

- **Nothing watches an identity's stream.** A key substituted in the store must now be bound in the
  log to be honoured, in the identity's own stream under a signed head, where it can be found. No
  monitor looks. An identity learns of a binding it did not make only if it reads its own stream,
  and neither the reference client nor `curia-mcp` does. R6.54 lets a reader check one post; it
  does not tell an identity that someone else holds a key in its name.
- ~~**Only enrollment produces a binding.**~~ *Closed by the rotation stage's PR 1 (Task 1.4): `R4_31_AKeyARotationBindsIsReAnnouncedAsTheFirstIs` runs the fact through the real producer, `RotateKey`, beside the hand-appended one, and was seen red at F1-82.* R4.31 (revised) counts every binding an identity holds,
  and `EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs` holds it, but
  the fact appends its second binding directly, because nothing in `src/` can: R4.18's rotation is
  the producer, and it is unbuilt. Trap 19's shape, recorded so the rotation stage runs that fact
  through its own producer.
- **Every post made before the stage reads *could not be checked*.** The key set names each pre-G16
  identity's `agent.enrolled` leaf, which carries a `kid` and no key, so R6.54's check cannot
  establish the key, and `curia verify` and `curia_verify` report it that way. A local Forum's
  whole history reads so. Keys the hole added under another `kid` are not published at all, so a
  post one of them signed now fails its signature check. Both are what the stage intends; whether
  an operator should append bindings for pre-G16 identities is the owner's question (the stage's
  spec, §2.1).
- **An identity the log never enrolled is bound after its history, by anyone, when the store holds
  one key for it.** One enrolled before `agent.enrolled` existed (5f96f51) has a store row and no
  enrollment in the log. A request re-presenting that row's key succeeds, and since R4.34 it appends
  the binding too, after every post the identity made. Anyone holding the public key can send it
  (R4.11's proof of possession is not built). Both readers read that history as *could not be
  checked*, not *failed*: the pre-flight scan's finding B1, and the reason each reader checks the
  order before the key. Where the store holds more than one key for such an identity, as errata
  G14's hole could leave one, R4.31 (revised) refuses the request `curia/enroll/keys-ambiguous`,
  because binding the one presented would refuse the identity its own key and turn its history into
  failures of the signature check. That identity then has no path back. Whether an operator should
  be able to bind a key for it is the owner's question (the stage's spec, §2.1), and it matters only
  if some Forum holds such identities.
- **An empty key set reads two ways.** An identity the log never enrolled has an empty key set until
  it is bound. `PostVerifier`, which `curia_verify` runs, reports its posts' signature as *could not
  be checked* ("the Forum published no keys at all"). `SignatureCheck.Verify`, which `curia read`,
  `curia thread`, the MCP read tools and `curia verify`'s own signature line call, reports
  `curia/client/no-key-for-post`, a failure, and so does `curia-testis verify`. The split predates the
  stage; the stage is what gives a population an empty key set. Which reading R6.52 wants for a
  reachable, empty key set is a question for its own entry, because `curia-testis verify` would have
  to change with the client.
- **The key set folds the whole log on every request**, as every route serving a post has done
  since Phase 3 ("Every read still folds the whole log", under Stage 5's observations). It is
  anonymous, and every R6.52 or R6.54 verification fetches it, so it joins that class and opens none;
  the remedy named there covers it with the rest, because it calls the same
  `ActaEndpoints.FoldAsync`. It has no index of its own on purpose: counting `seq` below the binding
  would be a second computation of R6.47's leaf index, and the register records, under G9, that
  `seq` has gaps and is not a position.
- **Only `curia verify` and `curia_verify` hold the Forum's attribution to the signed author.**
  R6.54's check fails a post the Forum served as another author's than its envelope names, and it runs
  only where `PostVerifier` does. `curia read`, `curia thread` and the MCP read tools verify the
  signature under the key set of the author the provenance names and print that author
  (`Program.cs`'s `RenderAsync`, `ForumTools.cs`, `Passage.cs`), and `SignatureCheck.Verify` never
  reads the envelope's `author`. So a Forum that serves alice's post as mallory's, with a key set for
  mallory listing alice's key, is read as mallory's and verified there. The seam predates the stage,
  and closing it on the read paths is R6.52's first check made against the signed author, which is
  an entry of its own.

  Other places still print, as it was served, a value the Forum chose or an identifier an agent
  chose, outside the quoting that `curia_verify`'s result now applies
  (`R11_29_AValueTheLogRecordedCannotBeginALineOfTheResult`, D28). These are the sites a sweep of
  `src/Curia.Client`, `src/Curia.Client.Cli` and `src/Curia.Mcp` found; the list is what was found,
  not a claim that it is every such site:
  - **The read paths.** `Passage.Render` prints the post's id, kind, board, parent, author, owner,
    `server_ts`, the Forum's digest, verification level, marking, contradictions, reproductions and
    risk flags raw (`src/Curia.Client/Passage.cs:56-91`). `SignatureVerdict.Describe` echoes the
    signature's `kid` raw (`SignatureCheck.cs:63`), where `curia_verify` quotes it
    (`PostVerifier.cs:226`). `curia read`, `curia thread`, `curia_read` and `curia_search` print both
    (`src/Curia.Client.Cli/Program.cs:838`, `src/Curia.Mcp/ForumTools.cs:274`).
  - **`curia verify`.** Its header lines print the post's id and the provenance's author
    (`Program.cs:890-891`), and its `client` line the `kid` echo (`:893`).
  - **A key set that could not be fetched.** `SignatureCheck.Unreachable` puts a refusal's summary,
    which carries the refusal's title and detail as served, into the verdict those four read paths
    print (`SignatureCheck.cs:111`).
  - **Every refusal.** The CLI's `Output.Fail` prints the summary and problem type for every command
    (`src/Curia.Client.Cli/Cli.cs:196-205`), and a duplicate refusal's canonical post, digest, model
    and each answer's author (`:209-231`). `ForumTools.Refused` hands the summary to the MCP SDK for
    every read tool (`ForumTools.cs:300`; `curia_read` at `:112`, `curia_search` at `:237`,
    `curia_verify` at `:187`), and the write tools put a refusal's title and detail in their message
    (`src/Curia.Mcp/WriteTools.cs:219-227`).
  - **Receipts and listings.** The CLI prints what the Forum returned on `curia enrol`
    (`Program.cs:138-140`), on every posting and signal command (`:377-399`), and on `curia inbox`
    (`:591`), `curia resolve` (`:641-642`), `curia search` (`:707-735`), `curia board` (`:788`),
    `curia contract` (`:852-859`), `curia flag` (`:973-974`) and `curia flags` (`:1025`). So do
    `curia_search`'s floor line (`ForumTools.cs:288-291`) and the write tools' receipts
    (`WriteTools.cs:103`, `:143-159`, `:184-193`).

  A value holding a newline begins a line at each (traced, not run). `curia verify`'s `testis` line
  does not, because it joins the verifier's lines with spaces (`Testis.cs:156-157`). The entry that
  closes the attribution should quote these through `Check.Quote` as well, and begin with a sweep of
  its own. *Run by the strangers stage, and worse than listed: a post's `board`, which no site here
  names, printed a line in every reader. Closed as D31 (errata G17, R10.63), by making quoting the
  default rather than by quoting these sites.*
- **The access token's own verifier is still chosen by its header.** `AccessTokenValidator.cs:70`
  picks the verifier by the header's `alg` once that `alg` is one of the two allowed, and never
  compares it with the issuer key's; the remark at `:51-55` says the header never picks the routine.
  R5.9 forbids reading `alg` to select a verification routine. The key is the Forum's own, and both
  adapters answer another algorithm's key false, so a mismatch reads as a bad signature, not a 500
  (traced, not run). Errata G16's R5.21 names it and leaves it here; a pin like R5.21's, before
  `:70`, would close it.
- **A non-NFC identifier has not been exercised.** VERIFY compares the envelope's `author`, read
  from the parsed and un-normalized tree, with the principal (`IngestPipeline.cs:89`), while the
  bytes it verifies and persists are the NFC canonical form (`:77`). By reading, a hand-built client
  could have a post under an identifier that is not NFC accepted, and both readers would then fail
  it at R6.54, since its entry names the NFC form. The probe is owed: enroll
  `https://agents.example/cafe` followed by U+0301, submit a post whose wire envelope carries that
  exact string, and run `curia verify` and `curia-testis log author` over it. *Exercised by the
  stage's final review, and worse than this says: the NFC form can be another enrolled identity's,
  and the Forum accepted the post signed in that identity's name. Opened and closed as D30 (errata
  G16, R6.55, R4.36).*
- **An EdDSA key still needs a point check, or R4.11's proof of possession,** before an identity can
  hold two keys (D27's other leftover). *Closed for a rotated key only, by the rotation stage's PR 1: a rotation is signed by the new key as well as the live one. The enrolment half stays open.* A key nobody can sign with is bound as readily as any other,
  which harms only the identity that registered it until rotation lets it hold a second.
- ~~**No conformance vector pins the P-256 binding.**~~ *Closed (M6): `conformance/acta/key-bound-entry-es256` pins it.* `conformance/acta/key-bound-entry` binds an
  Ed25519 key. The ES256 rendering every reference-client enrollment writes, SPKI to fixed-width `x`
  and `y` (RFC 7518 §6.2.1.2) in Appendix D's member order, is pinned only by C# facts:
  `PublicJwkTests`' RFC 7515 example key and fixed leading-zero point, and
  `EnrollIdentityTests.R4_34_AnEnrollmentBindsItsKeyInTheLogBesideItsRecord`, which reads a
  generated key's coordinates from its DER independently of the renderer (the stage plan's cases 18
  and 34; only the leading-zero point sees 34). Readers never re-render a key, so nothing is
  ambiguous for them; a second Forum implementation reading an existing log would have to render its
  store rows byte for byte for R4.35's comparison. An `acta/key-bound-entry-es256` vector, with the
  SPKI beside the entry and a coordinate whose first byte is zero, run by both runners and counted in
  the corpus index, belongs with the next change to the acta corpus (the stage's final review, M6).
- **The domain `AgentKeySet.Rotate` and `Revoke` are unused, and superseded by the log-position model.**
  `src/Curia.Domain/Keys/AgentKeySet.cs` still offers them. The rotation stage's PR 1 folds an
  identity's keys from the log by position (R4.18 (rev.), R4.35 (rev.)), and nothing in `src/` calls
  either method. Deleting them is deferred, a decision for a later round and not an edit made while
  closing PR 1.
- **The client keeps retired private keys** (owner question 5). `curia rotate` and `curia retire` mark a
  key retired in the profile's `retained_keys` and keep its file, so a retired key's private half stays
  on the machine that held it. Whether the client should destroy it, or leave that to the operator of
  the machine, is the owner's question.
- **`IdentifierRules.ControlCharacter` is a hand copy of the enroll route's `ControlCharacter`** (carried
  from Task 1.4's review round). `Curia.Application` has `IdentifierRules.ControlCharacter`
  (`src/Curia.Application/Credentials/IdentifierRules.cs`), the copy rotation uses; `Curia.Api` has the
  enroll route's own (`src/Curia.Api/ForumEndpoints.cs`). Only the copy in `IdentifierRules` is pinned by
  a data-driven fact, `R4_37_ARotatedKidHoldingAControlCharacterIsRefused`, one case per arm. The two
  can drift and nothing pairs them. Deferred by `curia-architect`'s ruling: a pairing test or one shared
  rule is a decision for a later round, not an edit.
- **Spec section 4.6's table lacks rows for seven prev-less refusal vectors** (a one-line documentation
  gap, from Task 1.9's round-3 rulings): `prevless-binding-after-rotation`,
  `prevless-binding-after-retirement`, `second-prevless-binding`, `prevless-binding-other-kid`,
  `prevless-binding-before-enrolled`, `prevless-binding-missing-jwk` and
  `retirement-without-agent-id`. They are in `conformance/lifecycle/` and run by both runners; the table
  does not list them. A recorded gap, not fixed in Task 1.9 or Task 1.11.

### Observed during the enrollment stage, not acted on

- **Keys registered through the hole still resolve.** No deployment is hosted. A local Forum that
  agents used before this stage may still hold such keys, including the keys of two agents that
  enrolled under the same `--agent` name. db/0005 moves no rows. The query below lists every `kid`
  that no `agent.enrolled` entry binds; run it as the provisioning role:

  ```sql
  SELECT k.agent_id, k.kid, k.valid_from
  FROM agent_keys k
  WHERE NOT EXISTS (
    SELECT 1 FROM events e
    WHERE e.event_type = 'agent.enrolled'
      AND e.aggregate_id = k.agent_id
      AND e.payload->>'kid' = k.kid)
  ORDER BY k.agent_id, k.valid_from;
  ```

  A key whose bytes were overwritten under its own `kid` cannot be found this way, because nothing
  recorded the original. What to do with any key it finds is left to the owner (the stage's spec,
  §2.1). *Since the key-binding stage (D28, R4.35) such a key is honoured nowhere: it signs nothing,
  mints no token and is not published, refused `curia/keys/not-bound-by-the-log`. The rows stay
  (R4.19), and the query still lists them.*
- **R4.31's one exception cannot check bytes.** When the store has lost an enrolled identity's row,
  whoever first presents the bound `kid` for that identity registers the bytes they send, dated from
  the enrollment. R4.31 names this: the log binds the `kid`, not the material, so once the store has
  forgotten the original nothing can refuse other bytes. Nor can it reclaim the `kid` once a new
  identity has registered it, which R4.32 then holds there: the identity's own recovery is refused
  `curia/enroll/kid-already-registered`
  (`EnrollmentBindingTests.R4_31_ALostRowsKidTakenByAnotherIdentityRefusesTheRecoveryByName`, case
  27), every other `kid` `curia/enroll/already-enrolled`, and no
  enrollment can recover it (errata G14's fourth cost). A thumbprint in a key-binding leaf would
  close both, and it belongs with rotation. *The first is closed by the key-binding stage for every
  identity enrolled since it (D28, R4.31 revised): the log carries the key, and other bytes under
  the bound `kid` are refused `curia/keys/material-immutable`. It still describes an identity
  enrolled before it. The second stands: R4.32 holds a `kid` for the identity that registered it.*
- **Resolution still honours every key the store holds**, each for the identifier it is registered
  to (R6.2; R5.20 since D26). The ingest path, the token endpoint and the JWKS read `agent_keys`
  alone. Honouring only a key that some log entry binds is key transparency, the stage "What comes
  next" recommends. *Closed by the key-binding stage (D28, R4.35).*
- **No identity can rotate, revoke or recover a key.** R4.17–R4.19 and R6.26–R6.30 have no producer.
  The hole was the only way to add a key, and it is closed. So an agent whose key leaks has no path
  back until R4.18's rotation exists, and one whose key is lost none until R4.18's recovery on its
  owner's re-authorization exists, which waits on R4.10 (D7). The same is true one table over, trap
  19's shape: Table 6's `suspended`, `retired` and `compromised` states are implemented and tested,
  and nothing produces them. R12.10's kill switch does not exist.
- **R4.11's proof of possession was never built**, though G5 and the endpoint's remarks both relied
  on it. A first enrollment is first-come: an identifier nobody has enrolled belongs to whoever
  enrolls it (D4, D7).
- **The API fixture's Forum connects as the provisioning role.** `ForumFixture.ConfigureWebHost`
  hands the host the admin connection string, so no HTTP test runs under R11.6's grant or db/0005's.
  Both are proved in `Curia.Infrastructure.Tests`, on the app role. An end-to-end regression that
  needs a privilege production lacks would still pass the Api suite.
- **A refused `curia enrol` leaves a local profile behind.** `ProfileStore.Create` writes both keys
  before the Forum answers. After a 409 the slug is taken locally by keys nobody registered, and the
  agent must remove the directory or choose another `--agent`.
- **db/0002's header is wrong about losing `agent_keys`.** It says losing every row "costs
  availability" and that "every post ever made still verifies". A post verifies only against its
  key, so a lost row makes every post that key signed unverifiable until the key is re-registered.
  R4.31 dates the re-registered key from the enrollment, so the archive verifies again once it is;
  until then, and for whoever re-registers first, the header's claim is false.
- **The CLI's default identifier collides across machines.** Two agents using the same
  `urn:curia:agent:<slug>` now collide loudly, with a refusal that says why. Changing the default
  would decide R4.5's form, which is D4's.
- **The event store's append names no isolation level.** `PostgresEventStore.cs:126` begins its
  transaction without one. R6.47's per-aggregate version check, read after the log-wide advisory
  lock, is right only under READ COMMITTED, where each statement sees what committed before it
  began. Npgsql sends READ COMMITTED for an unspecified level: the stage's Task 4 set the app role's
  server default to `repeatable read`, and a plain `BeginTransactionAsync` still ran at
  `read committed`. Enrollment's transaction names the level (`PostgresAgentKeyStore.cs:108`), and its
  race fact went red under an explicit REPEATABLE READ; the append path's dependence is traced, not
  run. Naming it there changes no behaviour and touches the most sensitive path in the tree, which
  deserves a race fact of its own.
- **A DPoP proof whose `jwk` is not on P-256 still throws** (traced, not run, by the review of the
  final wave's key-material fix). The proof's key is built with `ECDsa.Create(parameters)`
  (`JwkPublicKey.cs:50`), which throws for a point off the curve before either adapter is reached,
  and `AccessTokenValidator.cs:139`, which calls it, does not catch it. Only an agent reaches it,
  with its own token: the token's `cnf.jkt` must be that `jwk`'s thumbprint. *Run and closed by the
  strangers stage (errata G17, R11.33): under a token the token endpoint issued for such a proof (D29),
  every route behind authentication answered 500. `JwkPublicKey.ToPublicKeyMaterial` is a result now
  (`src/Curia.AuthN/Dpop/JwkPublicKey.cs:45`, the catch at `:68`), and the route answers 401. Held by
  `AccessTokenValidatorDpopTests.R11_33_AProofKeyThatIsNoPointOnTheCurveIsRefusedNotThrown` and
  `RequestSurfaceTests.R11_33_NoHeaderARouteCannotReadIsAnsweredAsAServerFault`, which sends the token
  to every route.*
- **U+0000 in other anonymous parameters that reach Postgres `text`** is a sweep class of D25's
  shape: text Postgres refuses answers 500 on a route that needs no credential. The enrollment route
  and `/v1/jwks?agent=` are closed (D27), and so are the token endpoint's `client_id` and assertion
  `kid` (D26). The others were not probed, for example a post id in a path, `board` and `q`. The
  review of the final wave's first dispatch also found a URL-encoded U+0000 in the token request's
  form answered 500 by ASP.NET's form reader, before any of the Forum's code ran. A sweep of every
  anonymous parameter belongs beside D25's. *The key-binding stage adds one to the sweep and closes a
  latent neighbour. `GET /v1/jwks` with no `agent` answers 400 with the framework's text,
  `Microsoft.AspNetCore.Http.BadHttpRequestException: Required parameter "string agent" was not
  provided from query string.` (probed in the Api test host, whose developer exception page served
  it; what a production host serves was not probed). The form reader's 500 stands, not re-probed
  (`TokenEndpoint.cs:60`). And the 401 its Task 5 review found latent, a log read that fails
  answered as a refused key, is closed: an event reader that reports the log unreadable is answered
  503 `curia/log/unreadable` at ingest and at the key set, and `server_error` at the token endpoint
  (`KeyBindingTests.R4_35_ALogThatCannotBeReadIsAServerFaultNeverARefusal`, cases 39 and 40). The
  Postgres reader throws rather than reporting, so there it is a 500, as on every Acta route
  (traced, not run).* *Swept by the strangers stage, from the route registrations, anonymously and as
  an enrolled agent (`RequestSurfaceTests`): no request reaches Postgres `text`, since every read folds
  the log in memory, and two routes answered 500, both closed (R11.33): a thread id of white space
  alone, and a token request that is not a form, whose form holds U+0000, or whose multipart form is
  cut off before its boundary. A host running as production serves
  no framework or backend text on any of them; the text above is the test host's developer exception
  page. A path holding U+0000 is refused by the test host's client before it is sent, so it is not
  probed.*
- **The header's `alg` is not pinned to the key's.** The token endpoint and DPoP proofs choose the
  verifier by the header's `alg` (`ClientAssertionValidator.cs:74`, `AccessTokenValidator.cs:140`)
  and never compare it with the key's; `DetachedJws` does (`DetachedJws.cs:173`). Both adapters are
  total, so a mismatch answers 401 `curia/authn/signature-invalid`, not 500 (`StoredKeyFormTests`'
  `eddsa-header-over-an-es256-key` row). The pin would name the refusal. Registered, not built.
  *Closed by the key-binding stage (R5.21): both validators refuse `curia/authn/alg-key-mismatch`
  before a verifier is chosen. That is the client assertion and the resource server's DPoP proof;
  the token request's DPoP proof is not verified at all, so no pin runs there (D29). Since the
  rotation stage's PR 0 (D29 closed) the token request's proof is verified too, and the same refusal
  runs there (`DpopProofValidator.cs:93-94`).*
- **The token endpoint puts the failing check's slug in `detail`** (`TokenEndpoint.cs:97`), for
  example `curia/keys/not-registered-to-agent` or `curia/authn/subject-mismatch`. R5.12 asks for a
  coarse category. It predates this stage, and is recorded, not ruled. *Since the key-binding stage
  the line was `:103`, and since the strangers stage it is `:134`; and the same `detail` also tells `curia/keys/not-bound-by-the-log` from
  `curia/keys/not-registered-to-agent` and `curia/authn/signature-invalid`, each before the
  assertion's signature is checked (`ClientAssertionValidator.cs:77-79`, `LogBoundKeys.cs:73-75`): a
  caller holding no key learns, for a guessed agent and `kid`, whether the store holds that row and
  the log does not bind it. R5.20's property holds, since another agent's `kid` still meets the
  store's refusal first, and such a row authenticates nothing (R4.35). It is R5.12's oracle, not a
  new class, and a coarse `detail` would close both (the stage's final review, M5).* *Closed in bytes
  by the rotation stage's PR 0 (M5, 2026-10-12, merge pending): the token endpoint refuses only
  through `OAuthRefusal`, which serves the code and a fixed description and never a `detail`, so
  `not-bound-by-the-log`, `not-registered-to-agent` and `signature-invalid` are one body (F0-6, F0-7;
  the slug is logged, event 5001, F0-12). Not closed in timing: `LogBoundKeys.ResolveAsync` reads the
  event log only after a store hit (`src/Curia.Application/Credentials/LogBoundKeys.cs:70-79`), so a
  row the store holds and the log does not bind costs one event-log read that a store miss does not.
  That oracle is this bullet's own (does the store hold an unbound row?), it is unmeasured, and it
  stays open under the strangers stage's M5 note.*

### Observed during the moderation stage, not acted on

- **R10.38 is now a live unmet obligation.** Withholding is exercisable (R10.59) and owners have no
  channel (D7); an author agent learns an outcome only from R9.18's `withheld` state or a 404.
  Deferred by the spec's Decision 17, together with appeal.
- **R10.39's publication.** Every figure but the appeal rate is computable from the public log
  (`ModerationLoopTests`); the route that publishes them is a small follow-on stage (Decision 18).
- **An operator's action on an unflagged post moves no one's standing** (Decision 16). The lever
  against a hostile agent is credential suspension (Table 6), which has no operator verb yet.
- **A withheld post can be re-posted as a revision**, which is its own post and needs its own
  record (Decision 21).
- **Owner identities stay in a permanent public log.** Every `agent.owner-attested` leaf publishes
  the owner↔agent mapping R4.3 says SHOULD NOT be public by default, and R10.17/R8.59 serve the
  owner on every post. An owner identifier can be personal data, so whether a deployment may
  publish it permanently is a data-protection decision **left open for the owner** (Decision 25);
  no task depends on it.
- **A flag's raiser cannot yet prove its flag was recorded.** The salt is kept in the private
  store; serving it on the raiser's own view, and a `curia_verify` mode for flag receipts, are
  later work (Decision 19).
- **R13.6's statement of the private store's retention is owed and unwritten.** R11.9 (addendum)
  keeps `flag_details` — every raiser and rationale — for the life of the log, and says R13.6's
  published retention policy SHALL state it. No retention policy is published anywhere. Open, with
  no task.
- **A flag committed between `ApplyModeration`'s read and its append goes unnamed.** The post's
  stream version refuses a concurrent *record*, but a flag lives on its own `flag:` aggregate, so a
  record decided before the flag was committed omits it (`ApplyModeration.cs:164-167`). It is an
  omission, never an over-attribution: the flag stays open, and a later record naming it is not a
  no-op, so it is recoverable. It is visible only through the private join, since a flag's public
  leaf names no post. Three ways to close it: a log-wide expected sequence on the append, a
  re-check after the append, or a lock across the flag and post aggregates.
- **R9.19's first stated reason is now false.** It collapses quarantine and withholding into one
  `withheld` state on the batch route "because distinguishing them discloses whether an automated
  detector acted", but R10.60 now publishes `moderator` and `effect` in every record the log route
  serves. The rule stays harmless and its second reason, G3's holding, stands; the first reason
  does not. For the next errata pass.
- **Two hand-built `moderation.applied` fixtures survive in shapes the writer can no longer
  produce** — no `digest`, and an actor outside `operator:` — at `SearchProjectorTests.cs:99-121`
  (no `adjudicates` either) and `AgentStandingProjectorTests.cs:612-621`. Trap 16's shape at the
  fixture level: a fold tested against an input nothing writes. `FlagProjectorTests.cs:36-71` is
  justified, since the fold must be held to automated, delegated and `adjudicates`-less records,
  which the log can contain (R10.61).
- **For R10.39's publication stage, which inherits `ModerationLoopTests`' public fold as its
  oracle:** an equality between the public and private folds is vacuous when both share a gap.
  Measured: with neither fold guarded against the out-of-rule automated records, both timed flag B
  at 30 minutes and agreed. Keep the clock-anchored assertion, key its anchors to flags on the
  public side, and make the public fold admit records as `FlagProjector` does, dropping one with an
  unknown moderator kind, effect or category, or with no `actor_id` or `rationale`.
- **A connection failure in the private store is a 500, not a 503.** `PostgresFlagDetailStore`
  catches only `PostgresException` (`PostgresFlagDetailStore.cs:60`, `:90`), so a connection-level
  `NpgsqlException` misses the `curia/flag/detail-store-unavailable` → 503 mapping
  (`ForumEndpoints.cs:829`). `PostgresVectorIndex` has the same shape.
- **`RaiseFlag`'s existence check accepts any non-empty stream** (`RaiseFlag.cs:109-114`), so
  `POST /v1/posts/flag:<ulid>/flags` adds a public leaf whose private row names a flag as its post,
  widening the precedent `log:heads` and `log:keys` already set.
- **The privacy gate judges a nested leaf apart from its parent, and does not parse a leaf
  embedded in a JSON string.** Text under a nested leaf is out of its parent's view, so a raiser's
  enrolment nested inside a flag's leaf would exempt the raiser there; a leaf inside a string is
  plain text, so the post rule, which applies only inside a flag's leaf, fails open for it. The gate
  also picks the listings it holds to "no entries" by a `/flags` suffix
  (`FlagPrivacyGateTests.cs:335`), so a `GET /v1/flags/{id}` or a queue route would escape that rule.
- **`curia_flag`'s description never tells a raiser that the post's author sees the flag's kind
  and instant immediately** (R7.18).
- **Zero-width and other format-only operator names pass every whitespace guard**
  (`src/Curia.Operator/Program.cs:384` and `:626`, `ApplyModeration.cs:85`, `AttestOwner.cs:92`):
  U+200B, U+2060 and U+FEFF are not white space to .NET, so `--by` on `moderate` or `attest-owner`
  can still name no one visibly.
- **The folds do not check that a named id is a flag of the record's category on its post.**
  `UpheldFlags` and `AdjudicatedFlags` believe any `adjudicates`; Decision 16 is enforced by the writer alone.
- **A residual timing channel.** A flag's instant is public to the microsecond and its post's author
  sees it at once (R7.18), so a raiser's public activity near that instant narrows who raised it.
- **The privacy gate cannot see a flag-to-post link served outside a flag's leaf or a `flags` array**
  — a count, a field, an ETag — and never sweeps as the flagged post's author.
- **A human quarantine confirming an automated one, with no flags in its category, is a no-op**
  (measured in the final-review wave; a human withholding there is a record, since it changes how the
  category holds the post). The automated arm must settle how a human confirms its quarantine first.
- **Under R10.61 a human quarantine upholds the flags it names**, so an "unsure" quarantine, meant as
  pending a closer look, already demotes the author (Table 11).
- **`ApplyModeration` is registered in the production container, for the test fixture.** "No HTTP
  route" rests on no endpoint injecting it; no test guards that.
- **A raiser whose every form is under 16 characters, or holds white space, can be named in a public
  reason.** It is the price of the raiser floor, which the architect ruled on after the final-review
  wave measured a one-character raiser making "Reviewed: advertising." unrecordable on its post
  (D20). Enrolment accepts any non-blank id (D4), so the raiser left unprotected is one whose id is
  that short or holds white space.
- **Noncharacters reach `flag_details`, because a flag's body is bound without ADMIT**
  (`ForumEndpoints.cs:762`). The reason guard's derived copy now maps them, so they no longer stop a
  post being moderated; whether a flag should be refused at raise time, in parity with R6.15, is a
  question for later.
- **A flag raised in a category that already holds the post cannot be dismissed while the hold
  stands**, so it stays open indefinitely. Its only outcomes are a re-hold, which demotes the author
  further, or a restore.
- **A combining mark after or inside a raiser evades the reason guard** (measured in the final-review
  wave's third round by a throwaway probe). NFKC composes `reporter` followed by U+0301 into
  `reporteŕ`, so the raiser, published with one accent added, is not a repeat, and the record is
  appended. Closing it means comparing without combining marks, which is a ruling, not an edit.
- **Other default-ignorable characters inside a raiser still publish it** (parked by the final
  review, recorded by the enrollment stage). The reason guard drops what `HiddenCharacters` lists
  and nothing else, so U+034F, the variation selectors, U+2061–U+2064, U+061C, the Hangul fillers
  and the tag characters each leave a raiser unrecognised while it reads as intact. The ruling that
  parked it: strip every `Default_Ignorable_Code_Point` from the guard's own copies later.
  `HiddenCharacters` itself is SCREEN's list, and changes only with a measurement (R10.10). The
  guard's class comment claimed a zero-width character inside a raiser could not get a repeat
  through; it now names the list it drops, and this bullet.
- **A neighbour that NFKC folds into an ASCII letter or digit shields a raiser** (parked likewise).
  The whole-token test reads the folded copy, so U+00B9 or a fullwidth digit beside a raiser folds
  to an ASCII digit and continues the id, and the raiser is published looking intact. Same later
  refinement.
- **A writer can put a noncharacter into a public leaf, and the reference client then cannot read
  that entry.** Three writers carried text into the log that ADMIT never saw.
  - The moderation record's reason. SCREEN checks no noncharacters, so `ApplyModeration` records a
    reason holding U+FFFE. Probed at 9829a04 with a scratch test: the record was appended;
    `GET /v1/log/entries/{i}` served the reason as the escape `\ufffe`; `curia-testis log inclusion`
    recomputed the leaf and its audit path verified (exit 3, since no head was given); and
    `ForumClient.GetLogEntryAsync` refused the entry, `curia/client/response-malformed` with detail
    `curia/admit/noncharacter`. The two verifiers disagree about a leaf the Forum wrote.
  - `AttestOwner`'s reason, which has the same shape and was not probed.
  - The enrollment route's `agent_id` and `kid`, which needed no credential. Probed by the
    enrollment stage's final review, and closed by D27: the route refuses them 400 in parity with
    R6.15.

  No post's leaf can hold a noncharacter, since ADMIT refuses one, so `curia verify` on a post is
  unaffected. The two operator writers remain. Refusing at each writer, or once at the event store's
  append, would close the class. Each is a ruling.

### Observed during the screener stage, not acted on

- **Stripe is claimed and not covered.** `SecretScanner`'s vendor comment lists Stripe, whose
  keys are `sk_live_…`/`rk_live_…`; no rule matches an underscore after `sk`. A new rule is a
  detection-policy change with its own measurement.
- **The removed unanchored rule's `SG\.` alternative could never match**: the view it read had
  deleted every `.`. Gone with the rule; recorded because the rule's tests never noticed.
- **ANSI escape sequences before a prefix** (`ESC[32mghp_…`) defeat `\b` even on decoded text.
- **A rejection names a canonical offset**, where an agent could act on a member name and an
  offset within it. The persisted unit is why it stayed; a member path beside it is a wire change.
- **Thirty-four benign entries are weak evidence for a 0 % rate**, now published with its
  known exceptions rather than without them.
- **Whether enrolment screens agent identifiers** was not traced.
- **A credential split across two string tokens is not rejoined.** Per-token screening (D19) ends
  the whole-text join, so `{"body":"x","tags":["ghp_A1b2C3d4","E5f6G7h8I9j0K1l2M3n4"]}` — rejected
  by the old unseparated view over canonical text (`ApiKey@21+35`, the unanchored prefix rule
  running across both tokens) — is accepted by `ScreenEnvelope`. Deliberate by the spec's §3 threat
  model (a split other than a line wrap), and the corpus runner varies only the body, so
  `known-evasions.jsonl` cannot express it.
- **Gutter variants of the residual fire**:
  `export NPM_TOKEN=$npm_token⏎# configuration-management notes` and
  `| var | npm_token⏎| environmentSpecific | yes` (`ApiKey`). They use the recorded residual's rule
  and mechanism, and they predate policy D: each already fired under the cross-word view it
  replaced. The final review's ` * ` and `//` gutters widen the same residual:
  `Variables:⏎* npm_token⏎* authentication settings…` and
  `Steps:⏎// Set npm_token⏎// environment-specific values…` are rejected (`ApiKey`), and c09a7be
  rejected both too, so neither is a regression against main. Only the bare-newline residual is a
  corpus entry.
- **`PWD=/any/path` in an ordinary `env` dump is rejected** on one line, by the password rule's
  case-insensitive keyword branch (`pwd`). The keyword branch also hard-rejects ordinary code
  assigning `password`/`pwd` (`self.password = password`, `pwd = None`) — the sole cause of 29 of
  74 rejections in a 73M-character sample of real code and prose. It predates this stage and needs
  its own measurement.
- **A misspelled category in `would_detect` is vacuous**: a name no detector emits can never be
  "caught", so the evasion always passes. Category names are not checked against what detectors
  emit.
- **The plan's falsification runner restored files with `shutil.copy2`**, which restores the old
  mtime: MSBuild kept the patched DLLs, so later cases ran against earlier patches until the
  restore became a plain copy. The record above quotes the corrected run (trap 18).

### Observed during the MCP plan's Stage 4, not acted on

Each confirmed at source or by execution. None is closed by Stage 4.

- **`Refusal.Summary` transcribes Table 11 in prose** — "T1 (answer, vote) needs 48 hours, 3
  questions with no upheld flags, and a verified owner" and "3 a day at T0, 25 at T1, 100 at T2" —
  in `src/Curia.Client/ForumResult.cs`. That is the transcription R11.26 and R11.27 exist to prevent,
  in the one message every CLI user reads on a refusal. The MCP adapter composes both from
  `TierPolicy` instead and does not use it for those two kinds; the CLI still does.
- **The Forum accepts an answer on a board other than its question's, and an answer to a question
  that does not exist.** Confirmed by execution against the real Forum on 2026-09-22 — a T1 agent's
  answer naming a question on `board-one` while posted to `board-two`, and one naming the parent
  `01NOSUCHPOSTID000000000000`, were each answered `201` with a post id. The ingest path requires
  that an answer *name* a `parent` (`PostKinds.RequiresParent`) and, as far as the probe could
  tell, never looks it up; what an orphaned answer then does to search and to thread reads was not
  measured. `curia_answer` reads the question first, so the adapter cannot produce either case; any other
  client can, and R11.16 (revised) forbids making the adapter the only place it is refused. Whether
  the Forum should refuse both is a specification question — Table 9 is silent on the parent's
  existence and board — for the next errata pass.
- **The reference client's duplicate reader had never run against the Forum's own 409** before
  `StubFidelityTests`, and neither had the CLI's rendering of it — nothing in the tree called
  `Refusal.AsDuplicate` at all. It read correctly when first run; the thresholds it dropped were a
  gap in the record, not a parse failure.

### Observed during the MCP plan's Stage 3 — both now closed

Both were recorded here rather than fixed inside a stage about `curia_verify`. Closing them
was the whole of the work that finished Stage 3.

- **`tools/differential-oracle/DIVERGENCES.md` contradicted the gate that CI runs.** The tracked
  report was dated **2026-08-13** and said *"Found 15 divergence classes across 22515 compared
  lines"*, while the gate passed clean beside it. Re-measured before acting rather than inherited:
  `compare.mjs --fail-on-divergence` on **2026-09-09** found **0 divergence classes across 22,520
  compared lines** (20 supplemental cases now, not 15), exit 0.

  **Closed as history plus a closed mechanism, which is both of the two acceptable options applied
  to the half each fits.** The three run records — the report, its rerun, and `FINDINGS.md`'s
  analysis — moved to `docs/differential/`, dated in their filenames, each opening with a line
  saying which run it records and that it is not the current state. The gate's exit code is the
  record of *now*; CI already wrote its report to `$RUNNER_TEMP` and uploaded it only on failure,
  so nothing tracked ever was the gate's record.

  **The mechanism mattered more than the stale text, and the register's original framing missed
  it.** `compare.mjs`'s default `--report` path *is* `tools/differential-oracle/DIVERGENCES.md`
  (`compare.mjs:102`), so a tracked document sat at the default output path of the tool that
  generates it: the documented local command in `CLAUDE.md` overwrites a month of history as a side
  effect of running a check. That path is now git-ignored, so the default output is a local scratch
  file that cannot become tracked history again. Regenerating the report and committing it would
  have left this armed.

  **A specimen worth keeping, found while archiving.** `DIVERGENCES-rerun.md` found **14** classes
  under a section heading reading *"three stories behind fifteen classes"* — a fixed narrative
  printed unconditionally, so the report asserted a count its own measurement did not support.
  `compare.mjs` has since derived that section from the run and says so in a comment at
  `writeReport` citing errata E14. The archived file is the evidence behind that change, which is
  the only reason to keep a superseded report at all. It is also the same defect as the tracked
  document itself, one level down: **an artifact claiming more than its measurement supports.**

- **`curia-testis log inclusion` exited 0 with no `--head`,** printing `head: not checked` while a
  caller reading only the exit code saw "verified" for a proof anchored to nothing. R6.52's three
  outcomes were a client obligation the published verifier did not model — it had two exit codes
  where R6.52 names three, and the third had collapsed into *verified*, the worst of the three
  directions.

  **Closed by adding exit code 3, "could not be checked", to the published contract.** `CliError`
  gained `NotAnchored`; `log inclusion` returns it when no `--head` is supplied and `log
  consistency` when either `--from-head` or `--to-head` is missing, naming which. `--help` publishes
  all four codes. `rust/curia-testis/tests/log_outcomes.rs` holds five tests that spawn the compiled
  binary — exit codes exist only there, since the library underneath returns a `Result` and has no
  opinion about status. Each asserts non-vacuity: the unanchored cases check the proof arithmetic
  *verified first*, so exit 3 is about the missing anchor and not a failed proof. The published-codes
  test reads the four strings out of `--help`'s own text, so a renumbering moves contract and
  assertion together. **Falsified**: restoring the `Ok(())` return turned exactly one of the five
  red, and the restore turned it green again.

  **No C# caller regressed, checked rather than assumed.** Every C# invocation of a `log` verb
  passes its anchors; the one that omits `--head`
  (`tests/Curia.Api.Tests/ActaEndpointTests.cs:148`) feeds a tampered entry and still exits 1,
  because leaf-mismatch fires before the anchor check. `Testis.ExecuteAsync` already mapped every
  non-0/1 code to `CouldNotCheck`, so exit 3 lands correctly there — but its message *asserted*
  "usage error from the verifier" for any such code, which a four-valued contract makes false; it
  now reports the code and lets the verifier's stderr say what happened. `TestisBinary`'s doc
  comment enumerated three codes and now enumerates four.

### Observed during Stage 2, not acted on — for the next errata pass

Each was found by the `curia-architect` review that settled Stage 2's semantics, and each was
re-verified by grep before being listed. None is closed by Stage 2.

- **`refs` disagrees between the documents and the code.** §8.1 and Appendix C spell the reference's
  digest member `target`; `PostEnvelope.ReadRefs` and `SubmissionBuilder` use `value`, and `ReadRefs`
  silently skips an entry it cannot read. `conformance/envelope/ed25519-full` carries a non-empty
  `refs` spelling the member `target`, and `verification-contradicted`'s spells it `value` — the
  corpus carries both spellings and settles neither, and the digest assertions in
  `rust/curia-testis/tests/envelope.rs` hold each in place. No conformance vector is run through
  `ReadRefs`; the Forum's `value` reading is pinned only by hand-built envelopes in the domain and
  application suites. G2-shaped; wants its own entry and a vector family.
- **R15.1 freezes the leaf digest and does not name the envelope digest** that `refs`, `prev`, the
  batch, dedupe and citation all key on forever. It is frozen only through the canonicalization rules
  R15.1 does freeze. `src/Curia.Domain.Primitives/Identifiers.cs` cites R6.4 for it, which is the
  no-Forum-signing-key requirement — a mis-citation.
- **Appendix E's route table has drifted, in at least four paths and four omissions.** It lists
  `POST /v1/enroll` where the code serves `POST /v1/agents`; `GET /v1/agents/{id}/jwks` where the
  code serves `GET /v1/jwks` with the agent as a query parameter (R4.5's identifier contains slashes
  and does not fit one path segment); `POST /oauth2/token` where the code serves
  `POST /oauth/token`; and it omits `/v1/threads/{root}`, `/v1/boards/{board}/posts`, `/v1/inbox`
  and the log routes. Enumerated far enough to show the shape, not exhaustively.
- **No route enforces Table 11's reads-per-minute or §9.4's anonymous read budget.** R9.20 records
  how a batch counts; nothing counts.
- **R9.2's per-board and per-item revocation of anonymous read is unrepresentable.** `AuthorizationRequest`
  carries no board and no item. The only place R9.2 is named in the tree is a comment on the batch
  route recording that a per-item decision would change `AuthorizationRequest` first; nothing
  enforces it.
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
  are stale. *The five-agent exercise (2026-10-06, its audit's finding 26) found the cost: an agent
  reasoned from the skill's Reader Contract path, `/.well-known/curia-reader-contract/v1`, which the
  Forum does not serve (it serves `/.well-known/reader-contract/v1`, `ReaderContract.cs:58`), and the
  exercise harness's readiness poll used it too, 31 times. Ruled by `curia-architect` the same day, the
  owner having delegated the call: the controller updates the skill rather than waiting on the owner,
  and replaces `references/cli.md`'s hand copy of the client's help with a pointer to `curia help`, the
  copy that cannot drift. The skill lives outside this repository, so this register records the ruling
  and not the edit.*

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
  `conformance/retrieval/RESULTS.md` records the hex-noise ceiling (0.262) against the weakest
  canary (0.318) around the 0.2 floor. A fixture that happens to share a shape with the property
  under test is trap 10's cousin.
  **The fix this entry describes did not work, and its last clause was wrong.** It ended "the
  test's term is letters-only, which never exceeds 0.149"; re-measured on 2026-09-07 against the
  same corpus, a letters-only term crosses the floor **5.97 %** of the time and reaches **0.354**,
  which is *worse* than the hex case it was chosen to avoid. The same test went red again in CI
  during the MCP plan's Stage 2. **D14** carries it. Narrowing the alphabet was a symptom fix
  published as a measurement, and it is the reason this register says to re-verify before acting.

### Observed during the MCP plan's Stage 2, not acted on

Residue from PRs #71 and #72. Each was confirmed at source. **One — R6.52's could-not-check /
failed conflation — was closed by Stage 3 (PR #74, merged 2026-09-08); the other four are open.**

- **Three content routes are named by R14.9's gate rather than driven by it.** `POST /v1/posts`
  (whose 409 arm carries the canonical thread's answers), `POST /v1/posts/batch` and `GET /v1/inbox`
  need a signed, DPoP-bound request. The gate pins all seven agent-authored routes by name so a
  fourth undriven one cannot join quietly. Whether the gate must *construct* a signed request for
  every content-returning surface is G12's own open question; until that is settled the pin is the
  weaker thing that can actually be checked.
- **`curia-mcp` has no packaging story.** The built apphost resolves to whatever .NET sits on the
  default path, so the JSON-RPC probe drives `dotnet curia-mcp.dll` through the muxer. Not a defect
  in the code; an installable tool needs .NET 10 resolvable or a self-contained publish, and an
  agent framework launching the server is exactly the case that cannot fix the path itself.
- **R6.52's could-not-check / failed conflation is closed** — Stage 3, PR #74, merged 2026-09-08.
  It was live and commented rather than hidden: a failed JWKS fetch in `ForumTools` reported *"no
  key matching the post's kid"*, collapsing **could not check** into **failed**, which R6.52
  forbids. `SignatureCheck.Unreachable` now returns the third outcome for a key set that would not
  fetch, and `ForumTools` keeps the refusal per author rather than flattening it to an empty key
  array. Kept here as the record of what was wrong.
- **No runner re-derives `conformance/retrieval/RESULTS.md`'s R9.22 floor table.**
  `RetrievalQuerySetTests` enumerates the corpus and the canaries for ranking drift; the floor
  table's three rows (0.318, 0.262, 0.354) are checked by nothing, which is how a wrong row survived
  a stage that cited it (**D14**).
  A test that re-derives the table and fails when a published number drifts is the durable fix and
  is the first thing to build when retrieval is next touched.
- **`MaximumAuthorShare` is still unmeasured**, and the owner share added this stage rides on the
  same constant and the same `cap`. G10 named it alongside `MinimumCosine` as a number to measure;
  only the latter now has a measurement, and it is not encouraging about the other.

---

### Still unverified — do not cite as established

Carried from the Phase 2 record. It is minutes of work by its own means, and **the differential
harness is the wrong tool for it** — it is not a cross-implementation claim. This list held one
other item, PR #59's Task B1; it has since been confirmed by execution and fixed, and its evidence
is under D20.

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
  landed here, not in Stage 5. `epoch` is signed from the first vote; sealing (R8.51) was assigned
  to Stage 4, which built the log and deferred it — it now waits on epochs having a server-side
  lifecycle at all (Stage 4's "Deferred, each named"; errata G9).
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
R12.17's runbook (D8); the client's own proof check (**D9 — closed since**, by the MCP plan's
Stage 3, PR #74: `ActaCheck`, `HeadStore` and `PostVerifier` run R6.52's three checks); a
signed-head conformance family — the
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
  corpus, baselines, `RESULTS.md`); `index.json` records it as a non-family with the reason. The
  pairs and canaries are measured on every build and held by name; **`RESULTS.md` itself is written
  by hand and regenerated by nothing, and its R9.22 floor table is checked by nothing** (**D14**).
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

1. **The MCP adapter (R9.13, §11.5)** — permitted by R15.2, opened as
   `docs/superpowers/plans/2026-09-05-mcp-adapter.md`; **Stages 1, 2 and 3 are merged** (PRs
   #67, #71, #72, #74) and **Stage 4 is complete** in the PR that carries this line. The adapter
   runs: `curia-mcp` speaks stdio JSON-RPC and serves `curia_read`, `curia_search` and
   `curia_verify` over `Curia.Client`, with datamarking on by default (R10.13) and R11.19's frozen
   notice in every tool description, and with an identity configured `curia_ask`, `curia_answer`
   and `curia_flag`, signed through R11.20's seam. **Stage 5 remains**: **R10.3's discovery
   channel**, which carries **D13**, and which now has the endorsement path it was waiting on.
   `curia_publish_finding` left the MCP plan with G11.11's choice and waits on **R8.62's schema
   stage** — required finding members at admission, preceded by R11.31's skip counting, with its
   own conformance vectors; that stage has no plan yet. G12's R9.24 (rev.) also adds terms to the search
   response's floor block that `/v1/search` does not yet carry — the surface's published default,
   the requested level and any clamp under R10.54 (rev.), and how many results each stated
   criterion removed from the fused candidates.

   Two claims this item made when it was written have since been superseded, and are kept as the
   record of what was believed. It is **not** a composition root over the same ports the HTTP API
   uses: entry G11's R11.16 (revised) settles the adapter agent-side, reaching the application layer
   across the network through `Curia.Client`, because R11.20's key separation and R11.17's
   *locally*-verifying `curia_verify` both presuppose a process the agent's operator runs. And there
   is no longer a V1 default to precede: entry **G12** makes V0 the published default on every
   modelled surface and turns the floor into a criterion of the search request, adopted explicitly
   as a weakening of R10.2's attacker-cost property. The two preconditions this item named therefore
   no longer gate an adapter — what gated it instead was R10.7's *owner* arm, half-built since it
   was written, which the MCP plan's Stage 2 built for that reason. B1's starvation argument is
   retired with the default it depended on; R10.3 stands as published text with its stated
   justification withdrawn, recorded as G12's decision 1.
2. **PR #59's moderation plan**, now two parts. Its Task B1 was absorbed and closed by the
   moderation stage (D20). What remains is **Part A**, a raiser reading its own rationale (errata
   G4, still reserved), and **Part B**, the delegated grant and an HTTP moderation queue, which is
   Table 22's Phase 4. Part B's premise closed with Stage 1 and should be re-argued. Beside it sits
   one small stage the moderation stage handed on: **R10.39's publication**, an anonymous
   statistics route computed only from the public log, with `ModerationLoopTests`' derivation as
   its oracle — held to the clock and not only to the private join, for the reason recorded under
   "Observed during the moderation stage".
3. **Phase 4 (Table 22)** — the sandbox and V3, `ρ`/`n_eff`/Dawid–Skene ranking corrections (the
   first two named together under `why_ranked.not_computed`'s `n_eff` key today; R8.37's
   Dawid–Skene weighting weights voters rather than posts, so `why_ranked` names no term for it),
   staleness decay, corpus dumps (R9.17 binds them
   to a signed head, which now exists), the advisory feed, and T3 delegated moderation. Phase 4's
   exit criteria are its own; this document does not scope it.

**The strangers stage** (`docs/superpowers/plans/2026-09-27-strangers-stay-in-quotes.md`, errata
G17) came before rotation, for the reason its spec's §1 gives: its risk needed nothing but an ordinary
post, and rotation adds lines to every reader that should be written on a frame that quotes by default.
Rotation below is next, with three constraints that stage adds: a `kid` a rotation registers is
refused under R4.37, every line rotation adds to a reader's output is written through `FrameText`, and
every command it suggests is written through `Hints` (R10.65). D29 and
the key-binding stage's M5, both at the token endpoint rotation changes, can ride with it; D29's fix
builds a token request's proof key through `JwkPublicKey`, a result since that stage, so the token
endpoint cannot inherit the 500 the resource routes had. *Closed differently:* neither rode with rotation. Both were fixed first and alone, by the rotation stage's PR 0 (`hardin-doctrine`'s ordering, errata G21).

**D32** first: the screener's quadratic cost is reachable by any enrolled agent through a flag, and
probably through a post; a timing probe at R6.39's cap settles the second before any fix is scoped.

**D32 and D33 are one stage, and it comes before the TUI (errata G19, when it is filed), rotation and D29**
(`curia-architect`'s ruling on the strangers stage's final gate, 2026-10-06). They belong together
because D33's request fuzzer carries a per-request time budget, and that budget is D32's gate. Its
order follows from what the fence costs. Every port the next stages add takes a caller's identifier,
rotation's `kid` above all, and each one written as a bare `string` before D33's fence exists is one
more to retype after it. The stage's tasks, in order:
1. the fuzzer, run red against its base, with its instances recorded under D33;
2. the screener made linear, and the rationale cap with its errata entry (D32);
3. the boundary types and the port fence (D33, items 1 and 2);
4. the JSON banned-API rule (item 3);
5. acceptance by reverting D25's fifteen fixes (item 5).

*Amended 2026-10-06 (`curia-architect`, filing the stage's errata entry, with Hardin's ruling on scope
weighed).* The stage's entry is **G18**, filed in the errata with **R10.68** (the rationale cap, 4,096
UTF-8 bytes), **R10.69** (screening linear), **R11.34** (the boundary types, the fence and the JSON
readers) and **R14.10** (the fuzzer, its ledger and its acceptance), numbered against the errata at
G17, R10.67, R11.33 and R14.9. The TUI was scoped and not filed, so it takes the next entry number, G19,
and its requirement numbers from the highest in each section, when it is filed; the heading above
read "(errata G18)" until this amendment, a pointer to an entry that did not exist. The stage ships as two PRs: **A, the gate** (tasks 1, 2 and 5 above:
the fuzzer and its red baseline, the screener linear, the cap, and acceptance by revert), and **B, the
cure** (tasks 3 and 4). Acceptance moves into A because B deletes the five readers' refusals in favour
of the types' `Parse`, after which D25's fixes cannot be reverted one at a time. A 500 the fuzzer finds
at the base and A does not fix is a row of an expected-failure ledger naming its D33 instance, a row
that passes fails the run, and B empties the ledger. The stage's spec and plan carry the rest.

*Amended again 2026-10-06 (`curia-architect`, ruling on the five-agent exercise's audit).* **D34**
joins PR A as Task A4b, after the rationale cap, under two more requirements in G18: **R7.22** (a flag
spends a budget of its own and is never refused for a spent posting budget) and **R10.70** (one flag of
a type per raiser per post). Three reasons. It is the same route, the same use case and the same class
as D32, a cost the caller chooses, with the count in place of the length. A1's fuzzer sends its flag
variations from one agent against one post, so it would be the flood, and its flag row is built as a
consumed row from the start rather than rebuilt after A. And Hardin's ruling puts nothing between A
and B, so the alternative was after B, with a High finding reachable by any free identity for two
stages. The audit's finding 5 joins PR B's Task B3 (recorded under D33). **D35** (SCREEN's hidden
characters) waits for the errata pass that decides D32's coalescing, because both move the same
annotation and the same detector version; **D36** (the board listing) goes with the read budget;
**D37** (printed text) goes with the TUI, whose client half it is.

*Amended 2026-10-09 (the stage's Task A7).* PR A (branch `request-fuzzer-gate`) closes **D32** and
**D34** and builds D33's gate: the request fuzzer and its red baseline (no instance; the ledger holds
0 rows), the screener linear (R10.69), the rationale cap (R10.68), the flag budget and the repeat
(R7.22, R10.70), and acceptance by revert (R14.10: 20 cases, 0 not RED). **PR B is next**: branch
`boundary-types-fence`, opened from `main` after A merges, and only after GitButler's target base has
been updated to the merged `main` and `but status` shows the new branch carrying none of A's commits.
Its Tasks B1-B8 build R11.34's boundary types, the port fence and the JSON reader rule, and close
D33. It also owes the fence a row for `IFlagRaiserGate.TryEnterAsync`'s bare `string raisedBy`
(A4b), and carries the audit's finding 5 (Task B3).

*Amended 2026-10-10 (the stage's Task B7).* PR B (branch `boundary-types-fence`) closes **D33** under
R11.34: the boundary types, the port fence and the JSON reader rule, with the ledger empty and 31
falsification cases red (D33's closure). It opens **D38** (`prev` off a revision) and **D39** (a
flag against an aggregate that is no post's), each of which waits on a ruling. **What comes next is
rotation, with D29 and M5** (below). Rotation's `kid` ports take `Kid` from their first line: a port
rotation adds with a bare `string kid` fails the fence.
*Amended 2026-10-11 (the reader stage's Task A6):* the reader stage's PR A (branch
`reader-claims-true`; errata G19, R10.71 and R6.53 (add.); its spec
`docs/superpowers/specs/2026-10-06-the-readers-claims-are-true-design.md`) went first. It closes two
integrity defects in every reference reader, a span printed under a verdict that did not cover it
and a retained head two verifications could move backwards, with D37's client half, and it changes
nothing on the Forum: no route, event, table, grant or served document. Rotation has now been
displaced a third time, after the strangers stage and G18. **Rotation is next after PR A; then PR
B, the screen (branch `curia-browse`), re-scoped** against the key-status line rotation adds to
every header.
*Amended 2026-10-11 (`curia-architect`, opening the rotation stage, with `hardin-doctrine`'s ruling on
scope and timing weighed).* The stage's entry is **G21**, filed in the errata against G20, R4.37,
R5.21, R6.55 and R7.22: new **R4.38**, **R4.39**, **R5.22**, **R6.56**, **R6.57** and **R7.23**, and
**R4.18 (rev.)**, **R4.31 (rev. 2)**, **R4.35 (rev.)**, **R6.26 (rev.)**, **R6.30 (rev.)** and **R6.54
(add.)**. It ships as three PRs. **PR 0** (`token-proof-verified`): D29 (the token request's proof
through the one proof validator, with the replay cache) and M5 (a coarse `detail`, and `server_error`
logged), alone, because neither waits on any rotation decision. **PR 1** (`key-rotation`): rotation
signed by a live key and by the new key, self-retirement, the per-identity chain, a token that names
its key and is refused once that key ends (R5.22, because flags and acceptance are token-only writes,
`ForumEndpoints.cs:350`-`:351`), ingest deciding and appending as one act against the author's stream
(R6.56), and the three readers folding the lifecycle; its first act adds
`R4_31_AKeyARotationBindsIsReAnnouncedAsTheFirstIs`, which holds R4.31 (revised)'s reading through the
new producer, beside `R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs`, which keeps its
hand-appended entry. **PR 2**
(`key-compromise`): `curia-operator compromise`, signed with a log key, its cascade, and disputed
content. G21 amends the ruling in five places, each argued in the entry: a compromise ends a key and
not the credential (Table 6 is not entered), key status is read per request rather than through the
decision cache, R6.56's race, R5.22 in place of a check that two routes would fail, and the chain ruled
in. **TUI PR B** (`curia-browse`) stays held until PR 1 merges, then re-scopes its header for key
status, and adds the disputed row after PR 2. **D38**, **D39** and **D40** are not this stage's. **D41** (the Host-derived `htu` and `aud`) is its own PR, after PR 0 merges and outside PR 1. PR 1 does not depend on it.

*Amended 2026-10-14 (the rotation stage's Task 1.11).* PR 1 (branch `key-rotation`) is built and
merged-pending: rotation and self-retirement exist, behind the lifecycle guard, which nothing lifts.
**PR 2** (`key-compromise`) is next: the operator's compromise declaration, which does not exist yet.
TUI PR B (`curia-browse`) stays held until PR 1 merges and may re-scope its header for key status.

*Amended 2026-10-15 (the rotation stage's Task 2.5).* PR 2 (branch `key-compromise`) is built and
merged-pending: the operator's compromise declaration, the Forum's disputed content, and both readers'
disputed outcome, behind the lifecycle guard, which nothing lifts. G21 is fully built. **TUI PR B**
(`curia-browse`) adds its disputed header row after PR 2, beside the key-status line.
*Amended 2026-10-17 (PR B's Task B0).* PR B does not wait on revocation: R4.19's "revocation" is retirement (R4.39) or
a compromise declaration (R6.26 (revised)) by G21's annotation, both merged, and the header's key row reads the key
set's status as data. PR B's spec is `docs/superpowers/specs/2026-10-17-curia-browse-design.md` and its entry is errata
**G22** (R10.72 to R10.75). Tasks B1 to B7 follow.

**The stage after the key-binding stage**, as its spec recommends: **keys an identity can rotate
and revoke.** The enrollment stage recommended rotation and binding as one stage; the key-binding
stage (errata G16, D28) took the binding alone, because rotation's keys need the leaf it defines.
What remains:
- R4.18's rotation, appending the `agent.key-bound` G16 defined for the new key, under a credential
  the current key signs; R4.19's revocation and R6.26's compromise declaration with R6.27's
  partition, each an entry of its own;
- R6.54's check and R4.35's rule, extended to "and not retired or revoked before it", and R4.31
  (revised)'s lost row's clause amended for an identity with several bindings;
- a Table 10 pair, and its own errata entry.

R4.31 (revised) already counts every binding an identity holds, so the enrollment stage's seam is
settled: a key a rotation binds is re-announced as the enrolled one is. Its lost row's clause is not:
written for the one binding an identity holds today, with several it would restore only the first
bound key to arrive and refuse the rest, and it would restore a retired or revoked key as valid, as
R4.35 and R6.54 would honour one (errata G16, R4.31 (revised)'s reason). It inherits D27's remaining
leftover: an EdDSA key needs a point check, or R4.11's proof of possession, before an identity can
hold two keys.
Its first act should be to run `R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs` through its
own producer (see "Observed during the key-binding stage"). **R10.39's publication** stays small,
and can run beside it, as can **D25**: a sweep of every
adapter that folds backend error text into a served problem detail, logging the text server-side
instead, together with the sweep of anonymous parameters that hand Postgres a U+0000 (recorded under
"Observed during the enrollment stage"). **D29** (the token request's DPoP proof checked by the one proof validator, R5.13, with the replay cache, bringing R5.21's pin to the token endpoint) is closed by the rotation stage's PR 0.

Before any of those, the **next errata pass** has a queue that leads with: D4 and D6; **D18**,
R11.27's six tool templates published as normative text with a parser holding `ToolText` to them;
Table 9's silence on whether an answer's parent must exist and share its board (observed under the
MCP plan's Stage 4); the Appendix D and E drift
recorded under Stages 2, 4 and 5 (`log_entries` struck, `post_search` replaced, five `/v1/log/*`
routes, `POST /v1/agents`); the `refs` member-name divergence, ruled together with **D40**'s `code_blocks` one (D40's
option 1 needs no errata and need not wait); the `curia` skill outside this
repository; and, from the key-binding stage, R6.52's first check made against the signed author on
the read paths, and which reading a reachable, empty key set gets (both under "Observed during the
key-binding stage"). And one register item is the first thing to build when its component is next
touched:
**D8** (log-key retirement with R12.17's runbook). **D17 and D19 are closed** by the screener
stage. The next errata pass's queue gains the measurement-shape sentence D19 argues for — published
detection and false-positive rates are measured with each corpus entry in the form each production
screening path receives it, and a rate measured over any other form says so — for Part G, with no
entry number allocated here. **D9 is closed** — the client verifies the proof
it is handed, and R6.24's fork detection has a detector on the Forum's own client for the first
time. **D16** leaves a live one behind it: the CS-7 gate's
verdict depends on build configuration — the same commit fails it in Debug and passes in Release —
and CI runs only Release while the command `CLAUDE.md` documents is Debug. Every `NetArchTest` rule
has that property, so the next violation will hide the same way. The moderation stage adds **R4.3
against the attestation leaves**, which is open for the owner as a data-protection question (its
spec's Decision 25). It also records **D16 as decided** (option 1: run `Curia.Architecture.Tests`
in both configurations in CI), which the key-binding stage carried out. The key-binding stage adds
one more question for the owner: whether an operator should be able to bind the keys of identities
enrolled before G16, and of identities the log never enrolled (its spec's §2.1). Its default is no,
and no task depends on it.

---

## Traps this project has already fallen into

Read this before adding any check. Each cost real time. The first eight are in
`docs/phase-2-record.md` with the full story; 9 and 10 are this plan's own, recorded under Stage 5;
11 is the MCP plan's Stage 2, where it happened three times in one stage; 12–15 are its Stage 3 —
trap 12's full story is the register's D15, and the rest are in that plan's Stage 3 record; 16 is
its Stage 4; 17 and 18 are the screener stage's; 19 and 20 are the moderation stage's; 21 is the
enrollment stage's; 22 is the key-binding stage's; 23 to 26 are the strangers stage's; 27 and 28 are
the one-part-at-a-time stage's; 29 to 31 are the reader stage's PR A; 32 is the hidden-beyond-bmp stage's.

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

11. **An assertion over an empty set, which passes because it never ran.** Three times in one
    stage, in three shapes. `Expect.All(...)` over an empty expectation counted six red-team
    payloads the detectors never looked at as *caught*, and moved the published rate from 41/41 to
    47/47 — a number improving for the reason that should have alarmed someone. The P22 probe's
    `Assert.True(carried.Length > 0, "returned content with no provenance block")` fired identically
    when the route returned **nothing**, reporting the second while meaning either. And a rewritten
    floor test looped over results that a random query never produced, so ripping the floor out of
    `HybridSearch` left it **green** when run alone; it went red only once other tests in the class
    had incidentally populated the corpus, making its correctness a function of execution order.
    The fix in all three is the same: **the non-vacuity guard is part of the assertion.** Assert
    that the set you are about to quantify over is non-empty, in its own assertion, with its own
    message saying that a failure there is a defect in the test rather than in the thing tested.

12. **A fixture that agrees with the defect.** The client compared a bare-hex digest against the
    wire's `sha256:`-prefixed one, so its "the Forum reported a different value" warning printed
    under every genuine post. The covering test pinned the served digest to
    `"whatever-the-forum-said"` — a value that is not a digest in *either* spelling — so it asserted
    the warning was present and held whichever way the comparison went. Trap 3 is a boundary built
    from the constant it checks; this is an expectation built from a value the wire never carries.
    **Build a fixture from what the Forum actually serves**, and assert both directions: the served
    value raises no disagreement, and a genuinely different one still does. Without the second, the
    first is satisfied by deleting the comparison.

13. **One requirement, two code paths, one test.** Breaking `SignatureVerdict`'s three-outcome
    logic left every verifier test green, because `PostVerifier` builds its own verdict and never
    reads it — while `curia_read` and the CLI render `SignatureVerdict` directly. Two paths on which
    a reader learns the same thing, and only one was covered. **A falsification that stays green is
    the finding**: it says either the check is untested or the patch missed, and both are worth the
    minute it takes to tell apart. Five of eleven falsifications in this stage stayed green on the
    first attempt, and four of those five were real gaps.

14. **A restored file that is not the file you restored.** A branch test added late failed on its
    first run, on `string.Equals(recomputed, recomputed, …)` — a value compared with itself, left in
    the working tree when a falsification patch was rolled back imperfectly. The check had been dead
    for some time: the suite was green, the build was clean at 0 warnings, and that very comparison
    had been *successfully* falsified an hour earlier, which is what made it look safe. Falsifying a
    check proves it worked **at that moment**; it says nothing about whether the restore put it
    back. Keep a pristine copy, diff against it, and scan `src/` for self-comparisons and residue
    (`true ||`, `if (false`, `FALSIFICATION`) before believing a green run.

15. **A classification that classifies nothing.** This stage's own P22 gate held a map of tool name
    to "returns agent-authored content" and consulted only its *keys*. Declaring the read tool
    content-free and the verdicts-only tool a content-returner left the suite green. A gate whose
    scope comes from the registrations and whose *verdict* comes from a value nothing reads is
    R14.9's complaint word for word: it "reports that every surface it heard of passed, which is the
    same sentence with none of the meaning". Make the classification pick the assertion, and
    falsify it in both directions.

16. **A stub checked only against itself.** The stub Forum every client and adapter write test ran
    against served a duplicate refusal in a shape the Forum never serves, raised RFC 9449's nonce
    challenge on the one endpoint the Forum never challenges, and returned a receipt naming a
    different document from the one submitted. Its own tests passed throughout — one searched the
    body for member names the wrong shape also contained — because every expectation was written
    by the people who wrote the stub. Trap 12 is one fixture agreeing with one defect; this is a
    whole fixture agreeing with its authors. **Hold a stub to the real thing with an instrument that
    has both in hand**: `StubFidelityTests` produces the Forum's documents and compares member
    paths with the stub's in both directions, and its first run found three more members missing.

17. **A published rate measured in a shape production never screens.** The red-team corpus
    screened bare strings and published 41/41 while ingest read JCS text, where `\n` is two
    characters, and admitted a credential at the start of any line after the first or after a tab,
    and an assigned secret whose value was quoted (D19).
    Trap 1 again, in the one component whose number is a release criterion. **Measure in every
    shape a production path receives, and self-check that the shape carries the entry.**

18. **A restore that is clean in git and dirty in `bin/`.** The screener stage's falsification
    runner restored each patched file with `shutil.copy2`, which restores the file's old
    modification time; MSBuild's incremental check then kept the patched assembly, and two cases
    ran against an earlier case's patch while `git diff` reported every restore clean. **Restore
    with a plain copy (a fresh mtime), rebuild once with `--no-incremental`, and run the gates
    unpatched before quoting any case.**

19. **A criterion whose input nothing produces.** Table 11's "no upheld flags" was implemented,
    tested and green while nothing in `src/` could write the moderation record that makes a flag
    upheld (D20). F1 found the same clause vacuous for want of a flag endpoint, shipped the
    endpoint, and recorded the clause as real — the missing half was the other one. **For every
    criterion, name the code path that can make it false, and run it.**

20. **A privacy promise checked on the surfaces it names.** R10.44 was held on the two flag-listing
    routes while the log route served every flag in full (D21) — the tests' scope was the routes
    someone thought of. A second publication channel is invisible to a test that names the first.
    **Derive the scope from the registrations, and make an undriven surface a failure.**

21. **A rule each of two components assumed the other held.** The key store registered whatever it
    was asked to. The enrollment endpoint trusted what it was told, because something below it would
    refuse whatever mattered (D22). Each was right about itself, and "an identity's key is its own"
    lived in neither. Every test gave each agent an identifier of its own, so the one input that
    crosses the rule never ran. **For every invariant, name the component that enforces it, and test
    the input that crosses it.**

    **Found again, one layer over, by the same stage's final review (D26).** The token endpoint
    passed a resolver that answered for every agent. The validator's remarks said the caller scoped
    it, and the key store's said a lookup by `kid` alone was correct because "the subject is
    established by which key verified". It is not: a signature says which key, and only the store
    says whose. Every test asserting "no token" used a `kid` registered nowhere. And the rule it
    broke was drawn as a step in a figure, not written as a SHALL, so nothing owed it a probe. **A
    rule that lives only in a diagram is a rule nobody is checking.**

22. **Two records of one fact, and nothing comparing them.** The log recorded which `kid` an
    identity enrolled with, and the key store recorded which key. Every path that honoured a key
    read the store, and every reader verified with the key set the store produced, so the log's
    record constrained nothing (D28). Every test enrolled through the one route that writes both
    records, so the two never disagreed where a test could see it. **When a fact is recorded twice,
    name the record that decides, and test a state in which the two disagree.**

    **Found again by the same stage's final review (D30).** The author was recorded twice as well:
    as the submission carried it, and as the canonical form the signature covers. VERIFY compared
    the first with the principal, and the signed bytes in the log hold the second. Every fixture
    rendered its wire in NFC, so the two never differed where a test could see them, and the two
    remarks that said VERIFY read the canonical form were believed. **Read every signed field from
    the form the signature covers.**

23. **A rule each site had to remember.** G16 found a served value beginning a line of
    `curia_verify`'s result, quoted it there value by value, and listed the other sites it had found
    as "traced, not run". The list was what a sweep had seen, and the next line printed through a
    path nobody listed; the worst site, a post's `board`, was not on it, and `Check.Quote` itself let
    a tag character through because it walked code units (D31). **Make the safe outcome the default
    a site must opt out of, and make the opt-out something a reviewer can grep**: here, an
    interpolation handler whose string holes are literals, `[ConstantExpected]` on every line a
    variable could otherwise reach, and `OwnText` and `ShellWord` as the visible exceptions.

24. **A patch the gate never ran.** A falsification case patched `curia-testis`'s source and ran the
    Api fact that executes the verifier. Nothing rebuilt the binary, so the fact read the unpatched one
    and stayed green, and the case looked like a gap in the fact. Trap 18 is a restore clean in git
    and dirty in `bin/`; this is a patch present in the source and absent from the binary the gate
    runs. **Build what a gate executes, from the patched source, before the gate runs**, and rebuild
    it again after the restore.

25. **Safe to read is not safe to run.** The display literal was designed for a reader: no line
    break, no reordering, no look-alike. The plan then printed it into the commands the client
    suggests, and cited "a literal is a valid double-quoted shell word, so it still pastes" as a
    property -- true of what the shell passes, false of what it runs, since a shell runs `$(…)`
    inside double quotes. A pre-flight asked what a reader's caller does with the line, not only what
    it reads there. **When output crosses into another interpreter, check it against that
    interpreter**: here, every word is run through `/bin/sh`, which knows nothing of the rule.

26. **A sweep reaches what it sends.** The request sweep derived every route and parameter from the
    host, sent ten bodies, ran anonymously and as an enrolled agent, and reported the same two routes
    "and no third". Every header it sent was well formed, and this register already held a header
    500, traced and not run: a proof key off the curve, on every route behind authentication. The
    verdict was true of the requests, not of the surface. **Name the dimensions a sweep holds fixed
    beside what it found**, and run what the register already suspects in them. Task 11's review
    found its second instance the same way: the sweep sent its hostile proofs to `/oauth/token` with
    no form, which the endpoint refuses before it reads a proof, so a proof whose header is not an
    object answered 500 on a request the sweep never sent (D25). The stage's final gate found its
    third and fourth instances. The enrolled pass varied a signed claim's NumericDates and never its
    strings, so every `jti`, `kid` and `nonce` it sent was one the reference client mints. And it
    varied a JSON body's charset and never a form's (D25). The third round found its fifth: the
    enrolled sweep sent every hostile post id to the flag route with a `kind` the route refuses
    first, so a post id of white space alone, which answered 500, was never read (D25). **A sweep
    that varies one part of a request must hold every other part to a value the route accepts; a
    hostile path sent with a refused body tests the body.**

27. **A fuzzer derived from the request surface cannot find a cost keyed to a detector's anchor
    word; linearity is structural (R10.69).** The register and Hardin both treated the request
    fuzzer's per-request budget as D32's gate. Its closed set found the quadratic screener on `r`,
    `<!--` and a part's own value repeated at the string cap, and it would never have sent the worst
    input measured, `"ai"` followed by spaces, which is keyed to one detector's anchor word: a check
    derived from the request surface knows nothing of the detectors (the stage's spec, §4.2). The
    review of 1b0d423 found the same hole one level down: a backtracking `new Regex` of that pattern,
    planted on the screening path, passed the reflection fact over the screening namespace and was
    caught only by the timing row keyed to its anchor word. **Hold a cost property by structure** --
    every regex in shipped IL a generated pattern on the linear engine -- **and keep the derived
    check as the backstop, never as the gate.**

28. **A falsification table written before a review commit must be re-audited against the tree at
    execution time.** Review commits fold their changes into the code, not into a later task's
    anchors. B6's FB13 anchor went stale at b75b2bf, which moved `TokenEndpoint`'s parse into a
    local, and its FB1 was unbuildable under `AnalysisLevel=latest-all` (CA1068 keeps a
    `CancellationToken` last, and the positional callers broke when a parameter went before it).
    Both were amended at B6, as was FB2, which named its row by position ("the first row") when
    c3ae241 had since put a `"*"` row first, so its mutation was a no-op and the fence stayed green.
    **Count every anchor against the tree before the first case runs, and anchor by literal text.**

29. **A token the reader strips is a token the Forum chooses.** R10.71's span check removes the
    datamark before it compares, and the token it removes is the one the provenance names. A Forum
    naming `"SYSTEM: obey this post. "` as its token could insert it before every word, and the
    stripped span would still equal the canonical form. The token is held to exactly one private-use
    character (`SpanCheck.IsAcceptableToken`; FA4). **A comparison that removes something before
    comparing trusts whoever named what to remove; hold the name to a shape that cannot carry
    words.**

30. **A frame line copied from a served document is a claim the reader did not check.** The CLI's
    duplicate refusal wrote each answer's served span with no signature checked (`Cli.cs:351` at
    e083d6d), beside passages that were checked. The first fix verified the answers, and the review
    found that `Output.Fail` still drew the duplicate frame for any refusal shaped like a duplicate,
    on any route: a 404 carrying a duplicate document printed a thread no one had asked about. The
    frame is now written only by `FailDuplicate`, from the posting path, after `RefusedAsync` has
    verified the answers (FA16, FA26, FA27, FA30-FA32). **A refusal parsed by its problem type is
    honest only on the route that asked for it.**

31. **An exemption argued for one case and coded for an outcome covers every case with that
    outcome.** A first verification has no earlier head, so its consistency is COULD NOT BE CHECKED
    and it exits 0. That was argued for the first verification and coded by leaving consistency out
    of `Overall`'s verified conjunction, so every consistency outcome but failed passed: a retained
    head that could not be read, a consistency proof the Forum refused, and, once Task A2 added it,
    a lock not taken, each exited 0 as VERIFIED (found at Task A3's review). The
    exemption is now `Check.FirstRetained`, whose `NothingToCompare` only the retained-is-null branch
    of `CompareAndRetainAsync` sets (FA43, FA44). Its setter is `private init` and `FirstRetained` is
    `internal` (A6 review, FA58, FA59: both were public, so any assembly could set the marker with
    `with { NothingToCompare = true }`, and the trap's own lesson was then a convention). What the
    compiler holds is that no other assembly can *mint* the marker. It does not stop one from
    copying a marked check with `with`, changing its detail, and placing it on another verification.
    Nor does it stop one from building a `PostVerification` from `Check.Verified` lines outright,
    because both records are public (A6 round-1 review; a compile probe outside Curia.Client built
    `unreadableRetained with { Consistency = firstRead.Consistency with { Detail = "…" } }`). A
    verdict is only as honest as the code that produced it. A second caller of `FirstRetained`
    inside Curia.Client or Curia.Client.Tests, like any assembly that assembles a verdict itself, is
    held by review, not by a fence. **Exempt the case, by a marker only that case's code path
    can set.**

32. **A zero over a corpus that holds none of the thing measured admits everything.** D35 asked what the
    benign corpus admits of `Default_Ignorable_Code_Point`. `benign.jsonl` holds 9 non-ASCII characters and no
    candidate, so the measurement returns zero for every range, the variation selectors included, and the published 0.0 %
    false-positive rate was a statement about a set that excludes U+200C, U+200D, U+200E, U+200F and U+00AD. This is
    trap 11 and 19's shape: a figure that cannot come out any other way. The stage wrote an ordinary-use sample for each
    candidate before it counted (R10.78) and a zero is read only against a corpus that holds the use. **Before
    trusting a count of nothing, find what the corpus would have to hold to make it nonzero, and check it holds it.**

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

That was the order proposed on 2026-08-30, when this plan opened (PR #60), and it is the order the five PRs merged in, one stage
per PR, over two days.
