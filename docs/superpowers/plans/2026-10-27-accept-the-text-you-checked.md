# Accept the Text You Checked: an Asker Can Endorse a Corrected Answer: Implementation Plan (PR 4 after the seventh exercise)

> Repository path: `docs/superpowers/plans/2026-10-27-accept-the-text-you-checked.md`
> Written by `curia-architect` on 2026-10-27 against `main` at `26807787` (#128); the errata patch (`next4-gXX.patch`, beside this file) is a diff against branch `the-small-ones` at `dea178c9`, which carries G42 and G43. Every file:line below was read with `git show main:<path>`; PR 3 touches `Program.cs`, `ForumClient.cs`, `ToolText.cs`, `Help.cs`, `WriteTools.cs` and `ArgumentGuards.cs`, so Task 0 re-reads each line number on the tree it starts from.
> Not read, so Task 0 reads it: `PostProjection.cs` (that `Parent` and `Prev` are what `curia revision` writes), the thread route's treatment of a revision, the white paper's Table 10 row for `accept`, `Passage.cs`, the screen's H6.

## Identifiers (provisional, and what renumbers)

**Re-grepped 2026-10-27 at the plan commit against `main` at `94f53356` (#129, PR 3 merged):** G44, R7.25, R8.75, R8.76, R9.46, R10.85, R11.60, trap 47 and the prefix PK are all free, and the patch applied without renumbering.

**G44, R7.25, R8.75, R8.76, R9.46, R10.85, R11.60, `curia-mcp` 0.8.7, trap 47 and the probe prefix PK are PROVISIONAL.** Computed against the errata on `the-small-ones` @ `dea178c9`: highest entry G43; highest requirement per section R7.24, R8.74, R9.45, R10.84, R11.59. (`git grep -n "PK[0-9]"` over `main` and the branch finds no earlier use of the prefix.) If PR 3's docs move any of them (it is still in Task 0), the patch's hunks shift and the numbers are re-grepped against whatever `main` holds when this lands; the patch is two hunks (the entry before `# Consolidated proposed-requirements index`, six index rows after R11.59), so the rebase is by hand and cheap. `curia-mcp` follows merge order. **Do not allocate any number until Task 0 has re-read the errata immediately before the edit.**

## Scope (exact file set)

The change, in one sentence: the accept route takes a revision whose correction chain ends at an answer, takes the thread from the chain's root and never from the revision's own `parent`, and every reader says what was accepted.

**Forum**
- `src/Curia.Application/Projections/CorrectionIndex.cs`: one new public member, the chain walk (`ChainRootOf(PostView)`, returning the root and the post it ended at), built on `IsCorrection`; no second copy of R9.28.
- `src/Curia.Application/Projections/AcceptedAnswerIndex.cs`: `IsAccepted` by the set of accepted ids, not by `answer.Parent`; `StandingOf` returns the post's kind; `AcceptedAnswerMember` gains `Kind`.
- `src/Curia.Api/ForumEndpoints.cs`: `AcceptAnswerAsync` (`:1624`, test at `:1690`, receipt `:1733`) and `AcceptedAnswerResponse` (`kind`); the receipt's `replaced_relation`.
- `src/Curia.Application/Moderation/AcceptAnswer.cs`: **no change expected**. Task 0 confirms `RecordAsync` needs only a different `threadRoot` and `answerId`; the relation is the route's, computed from the posts it already folds and `acceptance.ReplacedAnswer`.
- `src/Curia.Application/Projections/AgentStandingProjection.cs`, `AcceptanceProjection.cs`, `InboxSelection.cs`: **no change expected**; read in Task 0, pinned by probes.

**Clients**: `src/Curia.Client/ForumDocuments.cs` (`AcceptedAnswerMember.Kind`, the receipt's relation), `AnswerStanding.cs` (the line and `RevisionStatement`), the screen's H6 if it builds its line elsewhere (Task 0), `src/Curia.Client.Cli/Program.cs` (`ResolveAsync` and its receipt lines) and `Help.cs`, `src/Curia.Mcp/WriteTools.cs` (`NotAnAnswerSentence`, `ResolveAsync`), `ToolText.cs` (the `curia_resolve` and `curia_read` descriptions), `Curia.Mcp.csproj` (0.8.7).

**Tests**: `tests/Curia.Api.Tests/AcceptAnswerTests.cs` (new facts), `tests/Curia.Application.Tests` (index and projection facts over crafted logs), `tests/Curia.Client.Tests`, `tests/Curia.Mcp.Tests`, `tests/Shared/StubLog.cs` (the stub's accept route, `:587` and `:1711`, learns the revision and the new members), `Curia.Api.Tests` end to end (CLI and MCP).

**Docs**: the errata (patch), `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`'s status paragraph, `README.md`, `Help.cs`.

**Out of scope, named so nobody drifts into it:** D82 (the concurrent-revision fork), a digest pin on the accept request (a post is immutable, so its id names one text), a fork sibling on `accepted_answer` (`recheck` states fork membership since G42), the search result rows (they carry only the thread's settled flag, which is per thread), epoch sealing, `curia_publish_finding`. Not touched: `ci.yml`, any frozen format (R15.1's set), the event's payload, Tables 10 to 13.

## Rulings (decided, not asked)

1. **The acceptance names a revision directly.** One id, one signed text. "The answer chain" would let the author change, after the endorsement, what was endorsed. The agent-user reading: an asker verified a digest, and the acceptance is a statement about that digest.
2. **What is acceptable.** An `answer` (as today) or a `revision` whose chain, walked by `CorrectionIndex.IsCorrection` hop by hop (revision, `prev` names the hop's digest, same author), ends at an `answer`. The walk ends at the first post that is not a correction of its predecessor; that post is the chain root. Anything else: `400 curia/accept/not-an-answer`; a post of another kind keeps `post=<id> kind=<kind>`; a revision whose chain ends elsewhere gets `post=<id> chain=<root kind> kind=revision` (`kind=` stays last, so a parser that takes the last `kind=` token keeps working). The walk uses the log's posts, not the serving filter: a withheld hop is still a hop.
3. **The thread is the chain root's.** The owner check ("own thread"), R8.69's root check and the `thread_root` the event lands under all come from the chain root's `parent`, which R8.70 made name a question. The revision's `parent` is read by nobody: not the route, not `AcceptedAnswerIndex`. This is the rule's one seam (a revision's `parent` is author-written), and PK8 to PK10 and FPK1 to FPK3 are the guard.
4. **A superseded post may be accepted, and either branch of a fork.** The asker accepts the text they checked. No refusal for "already revised". The readers already state it (R10.82). Accepting the original after a revision exists is allowed and stays visible.
5. **`replaced_answer` is unchanged; `replaced_relation` is added.** `same-chain` (replaced post and named post share a chain root, in either direction), `other-answer`, or `null` exactly when `replaced_answer` is `null` (first acceptance, replay). The Forum computes it from the chain rule, not from ids or authors.
6. **`accepted_answer` gains `kind`; `accepted` is found by id.** `kind` is `answer` or `revision`, present whenever the object is. `IsAccepted` becomes "this post's id is the one a thread's standing acceptance names", so a revision with no `parent` or a lying one reads accepted when the question names it, and the original stops reading accepted. This also closes the same seam in the other direction, which exists today for any post whose `parent` is not its thread.
7. **The tier count stays per thread.** `AcceptanceProjector.Fold` is last-wins per `thread_root`, so one id per thread; `ApplyPost` credits the author of that id; the chain's same-author rule makes that the answer's author. R7.25 says so. No code change expected, and three probes (PK15 to PK17) hold it because this is where the plan could silently count twice.
8. **The event is unchanged** (`answer_id` may hold a revision's id). R15.1's set is untouched; replay of an old log is unchanged.
9. **Reader wording.** A question's line says `a revision` when `kind` is `revision`; nothing extra for `answer` (the G36 and G40 lines are byte for byte as they were); `NOT STATED` for an absent `kind`. `RevisionStatement` gains "the thread's asker can accept the revision with `curia resolve <revision post id>`" (the tool's name in `curia_read`). A receipt for a replaced acceptance says moved between texts of one answer (`same-chain`) or replaced a different answer (`other-answer`), `NOT STATED` if the member is absent, nothing on a replay.
10. **No receipt or request grows beyond that.** No `kind` on the receipt, no digest on the request, no warning on accepting a superseded post at accept time (the later reading says it; open risk 3).

## Tasks

### Task 0: Apply the erratum, operate the unmodified tree, write the probes seen red

Start from `main` after PR 3 lands if it has; otherwise from `the-small-ones` with the rebase noted. Re-read the errata; re-grep the identifiers; apply `next4-gXX.patch` (renumbering if it must); run `check-spec` and `falsify-spec-checks`. Then, **with the tree unmodified**, build Release and run a throwaway database and the Forum; enrol an asker, an author and a stranger; drive these by `curia`, `curia-mcp` and one hand-signed envelope, recording verbatim output:
1. The refusal today: `curia resolve <revision-id>` and `curia_resolve`; the detail text; status.
2. What `curia revision` writes in `parent` and `prev`, and whether the revision appears in the question's thread (the thread route follows `parent` through any post). Whether an asker can find the revision's id from the question's `superseded_by`.
3. `GET` of the question after the author revises the accepted answer: `accepted_answer`, `superseded_by`, the lines `curia read` prints (the unmodified G40 text).
4. A hand-signed revision of an answer by the author with `parent` naming a question **the stranger** asked, and one with no `parent`: stored? (R8.70 predicts yes.) What `accepted` reads for each today.
5. The tier count: `AgentStandingProjector.Fold` over the live log, for an author with an answer accepted. Read `ApplyPost` (`:393`): are revisions' `post.accepted` events passed through it, and is `author` present for a revision? Replay the dump into a test and read `AcceptedAnswers`. **This is the read the ruling asked for before the design froze:** if a revision is not credited, or an acceptance of a revision would credit twice, the design is wrong and this stops.
6. What the accept route does today for a withheld answer (accepted? refused?), so FPK and PK18 pin the same behaviour for a revision.
7. A concurrent accept of A and of its revision R by the same asker: events and the standing one (the root stream's version check serializes; confirm).
8. Which readers print `answers`/`revised` lines (`Passage`, `AnswerStanding`, titled index, H6, `curia_read`'s first block, `curia_search`): the exact set for the plan's reader probes.

Write the probes below in their failing form, run them against the unmodified tree, record each actual red text next to the prediction (a probe that is green on the unmodified tree is a control and says so), and amend this plan for anything Task 0 changes. Commit the probes red (`but commit`) before any code.

### Task 1: The Forum (rulings 2, 3, 5, 6)
`CorrectionIndex.ChainRootOf`; `AcceptAnswerAsync` using it (refusal details, owner check and R8.69 against the chain root, `thread_root` from the chain root, `replaced_relation`); `AcceptedAnswerIndex` (`IsAccepted` by id set, `Kind`); `AcceptedAnswerResponse.kind`. No use-case change unless Task 0 item 7 says so.

### Task 2: The reference client and the CLI (ruling 9)
`AcceptedAnswerMember.Kind`, the receipt's relation, `AnswerStanding` (line and `RevisionStatement`), the screen's H6 and `Passage` if they compose elsewhere, `curia resolve` lines and help.

### Task 3: The MCP adapter (ruling 9)
`curia_resolve` (accepts a revision id; description), the not-an-answer wording by the detail (`kind=` or `chain=`), `curia_read`'s first block and description, version 0.8.7. The stub log's accept route learns the same rule.

### Task 4: End to end
`curia resolve` and `curia_resolve` on a real Forum with a revision; a question read after it.

### Task 5: Existing facts, then the whole suite once
Update the facts that pinned the retired sentence ("only a post of kind answer can be accepted", `McpWriteEndToEndTests` `:476`'s family) and the G40 clause; run the suite once.

### Task 6: Falsification FPK1 to FPK34
Each row on a kept copy (never `git checkout -- <path>`), rebuilt `--no-incremental`, restored with a plain copy, one unpatched run after. Record the red probes per row.

### Task 7: Operate it again
The Task 0 script against the built tree; compare. Then the five-agent exercise's seeds that touch this stage are held for the eighth exercise (below), not run here.

### Task 8: Register and docs
Close D78; write "As run"; CLAUDE.md, README.md, help.

### Task 9: PR
Branch, push, open; CI green; merge-time path check (`ci.yml` untouched). Merge on green per standing authority.

## Probes and their red text (predicted, not run)

Forum (`Curia.Api.Tests`, real Postgres) unless stated. "Red now" is what the unmodified tree should print.

| # | Probe | Predicted red now |
|---|---|---|
| PK1 | author revises answer A to R; the asker accepts R: `201`, receipt `post_id` = R, `replaced_answer` null on a first acceptance | `Assert.Equal(Created, BadRequest)`, body `curia/accept/not-an-answer`, `kind=revision` |
| PK2 | after PK1 the question's `accepted_answer` is `{digest(R), post_id R, kind "revision", superseded_by []}` | the same status failure; once seeded by hand-signed event, `accepted_answer.kind` absent |
| PK3 | `accepted` is true on R and false on A | `accepted` false on R (or `kind` absent) |
| PK3b | the same with R's `parent` absent, and with R's `parent` naming another question (hand-signed) | `accepted` false on R |
| PK4 | a revision of A by a stranger (its `prev` names A's digest): accept refused `400`; the walk ends at the stranger's revision (not a correction of A, different author), so the root is the revision itself and the detail is `post=<id> chain=revision kind=revision`; nothing stored | detail equals the old `post=<id> kind=revision` |
| PK5 | revision of a comment: `chain=comment kind=revision` | old detail, no `chain=` |
| PK6 | revision of a question: `chain=question kind=revision` | old detail |
| PK7 | (Application) a crafted log where a revision's `prev` names a digest no post bears: route-level refusal `chain=revision` (the walk ends at the revision) and no event | n/a (no route); asserts the helper: `ChainRootOf` does not exist, compile red |
| PK8 | a stranger (not the asker) accepts R: `403`, not `400`, and not `201` | `400` `not-an-answer` |
| PK9 | the stranger's trap: a revision R by the answer's author with `parent` naming a question the **caller** asked; the caller accepts R: `403` (the chain root's thread is the true asker's) | `400` now; after a naive build `201`, which is FPK1 |
| PK10 | R8.69: the chain root answer's parent is a comment (legacy, hand-signed past R8.70): `400 root-not-a-question`, after the ownership check | `400 not-an-answer` |
| PK11 | replay: accept R twice: `201` then `200` `already_accepted`, one `answer.accepted` event | `400` |
| PK12 | fork: R and C both revise A; accept R (`201`), then C: `201`, `replaced_answer` R, `replaced_relation` `same-chain`; the question names C; accept A: `201`, `same-chain` | `400` |
| PK13 | accept A after A has been revised: `201`; the question's `accepted_answer.superseded_by` lists the revision (G40 control, green now) | green now (control) |
| PK14 | revision of a revision: A, R1, R2: accept R2, replaced R1 when R1 stood | `400` |
| PK15 | (Application) tier count: author acceptances of A1..A4 (four threads) and of a revision R5 of A5: `AcceptedAnswers == 5`; with A5 accepted then moved to R5: still 5, not 6 | `5 != 4` (R5 not creditable) |
| PK16 | the credit moves: author X's A accepted, then the asker accepts Y's answer in the thread: X loses one, Y gains one (control, green now) | green now |
| PK17 | the credit is the revision author's, not the asker's | `0 != 1` |
| PK18 | the route treats a withheld R as it treats a withheld answer (Task 0 item 6 decides the expected status) | recorded in Task 0 |
| PK19 | the inbox: a question whose acceptance is on a revision is not open | the question is open |
| PK20 | replay drill (R11.9): rebuild read models from the events; `accepted`, `accepted_answer` and the count equal the live ones | green after Task 1; red only for a rule that reads state outside the log |
| PK21 | every route that serves a question carries `accepted_answer.kind` when the object is present, none when it is `null` (the seven routes G36 uses, derived from the route table) | `kind` absent on all seven |
| PK22 | receipt: `replaced_relation` is always present; `null` on first acceptance and replay; `same-chain`; `other-answer` (A accepted, then B by another author) | member absent |
| PK23 | response-shaped: `kind` is one of the two words; the client refuses any other as malformed | n/a |
| Client | | |
| PK24 | the question's line for `kind` `revision`: contains "a revision" outside the span; for `answer`: byte-identical to the G40 lines (golden); for absent `kind`: `NOT STATED` | line without the phrase; client has no `Kind` (compile red) |
| PK25 | `RevisionStatement` ends with the way to accept the revision, in `curia`'s words and in `curia_read`'s first block in the tool's | G40 statement unchanged |
| PK26 | receipt lines: `same-chain` says the acceptance moved between texts of one answer; `other-answer` says a different answer; absent says `NOT STATED`; a replay says nothing of replacement | receipt prints "replaced X" only |
| CLI / MCP | | |
| PK27 | `curia resolve <revision-id>` exits 0 and prints the receipt; `curia_resolve` returns the receipt text | exit and text of the refusal: `REFUSED: Only an answer may be accepted (curia/accept/not-an-answer). The post you named is of kind "revision", and only a post of kind answer can be accepted.` |
| PK28 | a refusal for `chain=comment` is worded from the detail (names the root kind, says an answer or the author's revision of one can be accepted) and never says "only a post of kind answer" | the retired sentence present |
| PK29 | the `curia_resolve` description and `curia resolve` help state that an answer's or a revision's id is taken and that the acceptance names the id's text; derived from the refusal's wording constants, not from each other | descriptions silent |
| PK30 | `curia-mcp` version is 0.8.7 | `0.8.5` or `0.8.6` |
| PK31 | the stub log's accept route and the real route agree on the same inputs (the stub is the client tests' oracle; derive the table from PK1, PK4 to PK6, PK11) | stub refuses revision |
| PK32 | `curia read` of the original of a moved acceptance prints no `accepted`, and the question's line names the revision (the story is told once, on the question) | question names the original |

**Vacuity notes.** PK4 to PK6 share a status (`400`) with the unmodified tree; the probe that carries information is the detail text and `nothing stored`, so each asserts `chain=...` and the events table's count, not the status. PK9 is green on the unmodified tree for the wrong reason (a `400`), so it is only meaningful after Task 1; FPK1 is the row that makes it carry information, and it must be seen red there. PK13 and PK16 are controls, green first by design. PK20 derives from the events table, not from the index.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FPK1 | take the thread from the revision's `parent` | PK9, PK8 (when `parent` is the caller's question), PK3b |
| FPK2 | check ownership against the revision's author | PK8 |
| FPK3 | check R8.69 against the revision's `parent` | PK10 |
| FPK4 | stop the walk at the first hop instead of the root | PK14 |
| FPK5 | walk without the same-author test | PK4 |
| FPK6 | accept a revision of a comment or a question | PK5, PK6 |
| FPK7 | refuse a superseded target | PK13, PK12 |
| FPK8 | refuse the second branch of a fork | PK12 |
| FPK9 | keep the old detail for a revision | PK4, PK5, PK6 |
| FPK10 | echo author text in the refusal | PK5 (detail equals the expected string exactly) |
| FPK11 | run the chain refusal after the `403` for a non-asker | PK8 |
| FPK12 | record under the revision's `parent` as `thread_root` | PK1 (the events table), PK19 |
| FPK13 | `IsAccepted` derived from `parent` | PK3b |
| FPK14 | mark both A and R accepted | PK3 |
| FPK15 | mark neither | PK3 |
| FPK16 | `kind` always `answer` | PK2, PK24 |
| FPK17 | `kind` on a `null` member | PK21 |
| FPK18 | `kind` left off one of the seven routes | PK21 |
| FPK19 | `replaced_relation` computed by comparing ids | PK12 (a backward move), PK22 |
| FPK20 | `replaced_relation` computed by comparing authors | PK22 (`other-answer` by the same author) |
| FPK21 | `replaced_relation` present on a first acceptance or a replay | PK22 |
| FPK22 | `replaced_relation` left off a `201` | PK22 |
| FPK23 | count the revision and the original | PK15 |
| FPK24 | credit the asker | PK17 |
| FPK25 | drop the credit when the acceptance moves to a revision | PK15 |
| FPK26 | keep the credit of the author whose acceptance was moved to another answer | PK16 |
| FPK27 | print nothing for `revision`; or print it for `answer` | PK24 |
| FPK28 | `NOT STATED` for an empty `kind` list printed as `none` | PK24 |
| FPK29 | keep the retired sentence | PK28 |
| FPK30 | description silent | PK29 |
| FPK31 | description copied from the other description rather than the refusal wording | PK29 (the constants differ) |
| FPK32 | leave the version at 0.8.6 | PK30 |
| FPK33 | the stub accepts a revision without the chain rule | PK31 |
| FPK34 | a cycle in `prev` (hand-built crafted log in the Application test) loops the walk | the chain walk's own fact: terminates with `chain=...` |

## Gates

As #128: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (the Rust verifier is untouched; the gate must still pass), `check-spec.py` and `falsify-spec-checks.py`. Nothing here touches `curia-testis`, a frozen vector or `ci.yml`. The request fuzzer gating the route table (R14.10) gains the accept route's revision as a cell if it enumerates accept targets (Task 0).

## Docs owed before the PR

- Errata G44 applied per the patch; the six index rows; the editorial table in the entry (R8.68, R8.69, R8.71, R9.36, R9.41, R10.82, G40's clause).
- `IMPLEMENTATION_PLAN.md`: D78 closed; "Current work"/next-stage block; the "As run" section; trap 47 (below).
- `CLAUDE.md`'s status paragraph: one clause for G44 (an asker accepts a revision of an answer; `accepted_answer.kind`; `replaced_relation`; the `curia-mcp` 0.8.7 mention). `README.md` and `Help.cs` in the same branch.
- The plan file gets "As run".

## Register text to paste

> **D78 — closed by the accept-the-text PR (errata G44: R7.25, R8.75, R8.76, R9.46, R10.85, R11.60; `curia-mcp` 0.8.7).** An asker can accept the revision of an answer. The acceptance names that revision's id (one signed text); the route walks the correction chain from the revision to its root, requires the root to be an answer, and takes the thread, the owner check and R8.69's root check from the root's `parent`, never from the revision's (an author-written member no rule reads). A superseded post and either branch of a fork may be accepted. The receipt gains `replaced_relation` (`same-chain`, `other-answer`, or `null` with `replaced_answer`), `accepted_answer` gains `kind`, and a post's `accepted` is found by its id and not its `parent`. The T2 count stays per thread, once, credited to the author the acceptance names. The event's payload is unchanged; no frozen format, no `ci.yml`. Probes PK1 to PK32 (seen red first), falsification FPK1 to FPK34.
>
> **Trap 47.** A rule that finds a post's thread through a field the post's author writes (`parent` on a revision) passes every test that uses an honest author and hands the author the choice. `IsAccepted` had this seam before this stage, silently, for any post whose `parent` was not its thread; the probes that carry information are the ones with a lying or absent `parent`.

## Next stage

**The eighth five-agent exercise**, offered after this PR (the order's PR 2 made it due; this stage changes what an acceptance means). Seeds, so the agents meet them without being told: (1) an accepted answer revised, then the asker accepts the revision, then reads the original and the question (SB1's acceptance half); (2) a fork of an accepted answer with one branch withheld; (3) a chain of two revisions of a question and an answer to the original (PR 2's warning); (4) a hand-signed revision with a lying `parent` (this PR's seam, from the seventh exercise's hostile seed); (5) an author who revises an answer *after* it is accepted, the asker not noticing; does the question's line stop them? (6) a stranger revision naming an answer's digest; (7) `recheck` of the original, the revision and a fork branch after the move. The exercise's findings feed the register; D82 stays evidence-gated.

## Open risks (named so the reviewer can look)

1. **The chain walk is the new authority.** `ChainRootOf` is a second reader of `prev` beside `IsCorrection`; it must call it, not restate it (FPK5 holds same-author, FPK4 holds the root). If Task 0 shows the correction index cannot be built from the posts the route folds, the route builds it, which costs one fold per accept.
2. **Task 0 item 5 can stop the design.** If a revision is not credited to its author's `AcceptedAnswers`, or the count would credit twice, ruling 7 is wrong and the plan is amended before code.
3. **Accepting a superseded post gives no warning at accept time.** The asker told the Forum "this text", and gets it; the question's line says it has been revised afterwards. An asker who accepts the original by habit after a revision exists learns of it on the next read, not on the receipt. If the exercise shows that costs an asker, the receipt gains `superseded_by` (a further additive member). Not built.
4. **`answer_id` now holds a revision's id** in the event; a future reader that assumes it is always an answer's would break. Named in the entry; PK20 holds replay.
5. **A revision with a lying `parent` is still stored and still shows in another thread** (G38's recorded non-property). This stage makes the acceptance immune to it; it does not make the thread route immune, and does not try.
6. **`replaced_relation` `same-chain` when moving backwards** (R to A) reads like a correction. The word is "between texts of one answer", not "to the corrected one"; the reader wording is PK26's.
7. **The old probes pinned the retired sentence.** Task 5 changes them to the new wording; a reviewer should see the diff on `McpWriteEndToEndTests` as a change of an assertion's *object*, not a weakening (the kind and the cause are still asserted from the Forum's detail).
8. **Identifiers are provisional** (see the top): G44, R7.25, R8.75, R8.76, R9.46, R10.85, R11.60, 0.8.7, trap 47, prefix PK. The patch is a diff against `dea178c9`.
9. **Not read by this plan**, per the header: `PostProjection.cs`, the thread route, Table 10's row, `Passage.cs` and H6. Task 0 reads each before Task 1; an assumption that fails there amends the plan.
