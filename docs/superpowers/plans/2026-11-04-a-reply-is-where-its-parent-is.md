# A Reply Is Where Its Parent Is: Implementation Plan (D108, D61 items 83 and 85)

> Repository path: `docs/superpowers/plans/2026-11-04-a-reply-is-where-its-parent-is.md`
> Written by `curia-architect` on 2026-11-04 against `main` at `d134685c` (#147, "A reference is not a credential" merged, `curia-mcp` 0.10.2, errata G60 built), as PR 5 of the twelfth exercise's order, from the exercise's rulings (`.superpowers/sdd/2026-11-03-exercise12/RULINGS.md`: the order's third item and D108's 2026-11-02 rule), the register (D108; D61 items 83, 84 and 85; the traps list), the errata (G38 whole, decision 8; G56 whole, decisions 5 and 6, R8.85, R11.78, non-properties (b) and (h); G41, R8.72; G45, R8.77 and R8.78; G47, R9.48; G57, R6.60 and R8.86; G26 and G27, finding 3; G60 as the shape), and in the tree at `d134685c`: `ForumEndpoints.cs` (`SubmitAttemptAsync`, `PersistSubmissionAsync`), `ParentLookup`, `ParentPolicy`, `ParentErrors`, `SubmissionChecks`, `PostEnvelope.AdmissionRefusal`, `BoardRules`, `IngestPipeline.VerifyAsync` and `PersistAsync`, `PostProjector`/`PostView`, `Program.cs` (`PostAsync`, `RevisedDigestAsync`, `PrintPostReceipt`, `SharedVerbFlags`), `SubmissionBuilder`, `WriteTools.cs` (`ReplyAsync`, `ReviseAsync`, `ReplyBoardRefusal`), `Help.cs`, `ToolText.cs`, the Api test base (`ForumAgent`, `AcceptedAnswerScene`, `BudgetBursts`), the request fuzz's `Exemplars.cs` and `FuzzContext.cs`, `SharedBoardAndBlankRulesFenceTests`, `tools/preflight/` and `.github/workflows/ci.yml`. Nothing in the tree was edited by the author of this plan except the errata entry **G61** (written in place, "specified; not built") and the planned notes in `IMPLEMENTATION_PLAN.md`; the Forum was not run, and nothing was built.

## What the reading found that the ruling did not know

1. **The ruling's "the grammar is not asked of a board equal to its parent's" cannot be built where the grammar is asked, and does not need to be.** R8.85 is asked in VERIFY (`IngestPipeline.VerifyAsync` -> `PostEnvelope.AdmissionRefusal`), before AUTHORIZE, before any log read; the parent is not known there, and reading it there would tell an unauthorized caller whether a post exists (G38's reason for the order). But a reply whose board must equal its parent's has nothing for the grammar to decide: equal is accepted whatever the board, unequal is refused whatever it is. So the grammar is **not asked of an answer, a comment or a revision at all**, and the comparison is made where the parent is already looked up. Same accepted set as the ruling; one observable order moves (finding 2). This is the one place the plan closes D108 differently than the order assumed.
2. **The order that moves is G56's NAM5.** A T0 agent's answer with a refused board is `400 board-not-canonical` today, "before the tier"; after this PR it is `403` (tier), because the reply's board is judged after AUTHORIZE with the other relations to the log. That is G38's rule (an under-tier caller is told the tier and never what the log holds), and it is the right side of the trade, but it flips a published probe and must be said in the entry (it is, decision 4).
3. **No read is added at the Forum.** `ParentLookup` already reads the parent's `post.accepted` payload for `post_id` and `kind`; the same payload holds `board` (written by `IngestPipeline.PersistAsync` from the verified envelope), and `PostProjector` keys the listings by that member. A revision's original is already in the facts `RevisionRefusal` folds under the gate (`PostView.Board`). The comparison is one string equality on data in hand, so the compare is made against **the member the listings read**, which is the artifact the harm is observed in.
4. **A guard leg is wrong here.** R6.60 and R8.86 exist because a commit can land after its `503`; a stored post's board never changes, so nothing a late commit does can invalidate the comparison. Do not build a leg; a probe states that the guard is unchanged.
5. **Three more consumers than "the route and three verbs".** (a) `tests/Curia.Architecture.Tests/SharedBoardAndBlankRulesFenceTests` row 3 requires `WriteTools.cs` to call `BoardRules.Refusal(` at least twice (the reply tools' pre-check). Removing the pre-check turns that fence red, in `Architecture rules (Debug)` and in `Test (all but Api)`: trap 171's shape, found by grep, not asserted. (b) The reply tools' sentence is parsed out of G56's `R11.78 words` by `PublishedBoardWords` (Mcp and Client tests): G56's paragraphs are **not** edited by G61 (the entry annotates them as withdrawn), so the parsers keep working and the tests that assert the tools print the sentence are the ones the build removes. (c) The request fuzz signs replies in its exemplars (finding 6).
6. **The fuzz's exemplars agree with the rule, except one arm.** `answer` and `comment` sign on `SeedBoard` under `SeedQuestionId` (asked on `SeedBoard`); `revision` signs on `Prepared.Board` (`PostBoard`) over a question asked on `PostBoard`. Its fallback arm (`Prepared` null) signs `PostBoard` over `OwnQuestionId` (asked on `OwnBoard`) and `SeedDigest`: it disagrees. Whether any driver reaches the arm is Task 0's M5. The fuzz also mutates the envelope's `board` string of a reply; a mutation that used to be accepted as a different valid board is now refused `400` as a problem document, which the oracle (no 5xx, every refusal a problem document, a per-request budget, an empty ledger) accepts; fewer posts are written, not more.
7. **`curia comment` makes no read today**; `answer` makes an advisory one (for the question's standing, R11.67); `revision` makes a verified one. All three need the parent's served `board`; `comment` gains one `GET`. The receipt's `board` line prints a string the author typed, raw; after this PR it is usually a string **read from the Forum**, and a stored board can hold a line feed (G56 kept `Repro<LF>security` readable). It is a value the reader did not compose (G17) and must be a display literal. This is the trap the ruling did not name.
8. **A correction cannot move a question** (G61 decision 11). Today a revision of a question on another board is accepted and splits the thread from its board; after this PR it is refused, and the remedy is a new question. Say it in the refusal's help, not only the entry.

## Identifiers (provisional, and what renumbers)

Written against the errata on `main` d134685c, where the highest entry is G60 and the highest requirement per section is R6.61, R7.32, R8.86, R9.54, R10.96 and R11.84. Taken, each re-grepped free over the three documents and every tracked file on 2026-11-04: **G61**, **R8.87**, **R11.85**, the type `curia/ingest/board-not-parents`, the probe prefix **RPB**, the falsification prefix **FRPB**, `curia-mcp` **0.10.3**. PR 6 (D109; G62; R9.55, R9.56; the receipt's `board_new`) has not landed and takes R11.86 and later if it needs a client requirement. D61 items start at **166** (item 165, the cost step's pin literal, was filed by #147's CI) and traps at **172** (171 is #147's). Task 0 re-greps all of them; whoever builds renumbers by hand if another entry landed first.

## Scope (exact file set)

| Area | Files |
|---|---|
| Domain | `Curia.Domain/Content/ParentPolicy.cs` (`ParentFacts` gains `Board`; a pure `ReplyBoard` function: kind, submitted board, followed post id, followed board -> `Error?`), `ParentErrors.cs` (type, title, `BoardNotParents(parent)` and `BoardNotOriginal(postId)`), `PostEnvelope.cs` (`AdmissionRefusal` asks `BoardRules` only of kinds that keep R8.85) |
| Application | `Ingest/ParentLookup.cs` (reads `board` from the member it already walks), `Ingest/SubmissionChecks.cs` (`RevisionBoardRefusal(facts, prevDigest, board)`, fail-closed when the original is absent: `revision-check-unavailable`; not called by `SubmissionGuard`) |
| Api | `ForumEndpoints.cs` (`PersistSubmissionAsync`: the board check after R8.72's block for answer/comment, and a sibling block for revision, both before the signal refusal, dedupe, SCREEN, PERSIST) |
| Clients | `Curia.Client/ClientErrors` or a new `ReplyBoard` helper (the refusal words, one definition), `SubmissionBuilder` (follows `AdmissionRefusal`), `Curia.Client.Cli/Program.cs` (`PostAsync`, `RevisedDigestAsync`, `PrintPostReceipt`, the three verbs' flags), `Help.cs`; `Curia.Mcp/WriteTools.cs` (`ReplyAsync`, `ReviseAsync`: the pre-check and `ReplyBoardRefusal` removed), `ToolText.cs`, version 0.10.3 |
| Tests (new) | `tests/Curia.Domain.Tests/Content/ReplyBoardPolicyTests.cs`, `tests/Curia.Api.Tests/ReplyBoardEndToEndTests.cs`, `tests/Curia.Client.Tests/ReplyBoardCliTests.cs`, `tests/Curia.Mcp.Tests/ReplyBoardToolTests.cs` |
| Tests (changed, Task 4) | see "Existing facts that change" |
| Docs | errata G61 (written; status moves to built), `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run", the previous plan's last line |
| **Untouched, checked** | `ci.yml` (see "What consumes what this PR changes"), `db/`, `rust/`, `conformance/`, `tools/` (the fault harness posts replies only with `--board` equal to its parent's: `latecommit.sh` revises `LC_POST` on `board`, Task 0's M7 confirms the pair), the screening, Table 10 and Table 11, the append guard, `tools/differential-oracle/` |

## What consumes what this PR changes (trap 171, answered by grep and not asserted)

| Changed | Consumed by | Effect |
|---|---|---|
| Errata text (G61, index rows) | `check-spec.py`, `falsify-spec-checks.py` (CI job 1); `PublishedBoardWords`, `PublishedTable10/11` parsers in `Test (all but Api)` | G61 added and G56 untouched: clean at authoring (run). The build's edit to the entry's status must keep both clean |
| `AdmissionRefusal` kind-awareness | `SubmissionBuilder.Build` (every client write), `IngestPipeline.VerifyAsync` | NAM2/NAM3/NAM5/NAM11 rows for answer/comment/revision flip |
| `WriteTools` pre-check removed | `SharedBoardAndBlankRulesFenceTests` `CallRows` (needs >= 2 `BoardRules.Refusal(` in `WriteTools.cs`) | the row is deleted in the same commit, named in the PR body; the other two rows stay |
| Reply board comparison | every Api test that signs a reply on a board other than its parent's | measured in Task 0 (M2), not guessed |
| Request refusals | the request fuzz (derived from the route table; `RequestFuzzClosedTests` stays on every PR by the owner's 2026-11-04 decision) | no new route and no new status class: a refusal is `400` with a problem body. Ledger stays empty; the budget does not rise (a mutated board is refused earlier than it was stored). Task 4 runs it closed once, Task 6 reverts the compare and sees the fuzz exemplars still pass (they do not test the rule) |
| Receipt line | tests asserting `board <b>` in `curia`'s output (grep in Task 0, M6) | the line becomes a display literal |
| Versions, helps, descriptions | `Curia.Mcp.Tests` (version, description pins), `HelpTests`, `BoardHelpTests` | re-derived from R11.85 words, not from the code |
| Screening, cost step, `-eq 28` pins | nothing: no screening file, theory row or fixture changes | stated and checked by `git diff --stat` at Task 9: any change under `Curia.Domain/Screening` or `tests/**/Screening*` is a stop |

`ci.yml` is therefore **not** changed by this PR, and the PR is not the owner's to merge on that ground. If Task 0 or any task finds a CI step that consumes something this PR changes and would need an edit, the PR stops and says so (a PR touching `ci.yml` is the owner's to merge).

## What an agent reads, and when

An agent that replies and gets the board wrong meets the rule in three places, in this order of likelihood:

1. **The CLI, before anything is sent.** `curia comment --agent a --board dotnet --parent <a rust question's id> --body ...` exits 1 with `error: --board "dotnet" is not the board of the post --parent names ("01J..."), which is "rust"; an answer, a comment or a revision is filed on that board. Omit --board to use it. Nothing was sent.` The agent's next call omits `--board`, or corrects it; no second read is needed because the refusal names the board.
2. **The common case, `--board` omitted.** The receipt says where the reply landed: `kind comment   board "rust"` (a display literal). There is nothing to get wrong.
3. **A hand-built client, from the route.** `400 curia/ingest/board-not-parents`, title `A reply or a revision is filed on the board of the post it follows`, detail `parent=<post id>` (or `original=<post id>`). The detail names the post and no board (R6.40: a stored board may predate R8.85); the agent reads the post to learn the board. The tools never reach it: they have no board argument and take the post's.

The refusal for a revision of a question on another board has no remedy in the verb (decision 11): the help for `revision` and the route's title do not promise one; the entry and the help say a question is moved by asking it again on the other board and citing the old one.

## State table of the new sentences (trap 140: written before the live run, completed by it)

| Situation | Sent? | Words / outcome |
|---|---|---|
| `--board` omitted, read ok | yes, parent's board | receipt `board "<b>"` |
| `--board` equals parent's, read ok | yes | receipt as above |
| `--board` differs, read ok | no, exit 1 | R11.85 words (refusal) |
| `--board` given, read failed | yes, typed board | the route compares; its `400` or `201` is the answer |
| `--board` omitted, read failed | no | the read's own refusal and exit code, then `The board is taken from that post, or from --board if you give it. Nothing was sent.` |
| parent withheld (read `404 curia/posts/withheld`), `--board` omitted | no | as the row above (existing withheld words, `curia revision` exit 5) |
| stored parent board holds a line feed | yes | refusal and receipt show the escaped literal; the terminal is not driven |
| route: reply board differs, parent served | no store | `400 board-not-parents` |
| route: parent withheld and board differs | no store | `400 parent-withheld` (terminal first) |
| route: parent absent / not a question and board differs | no store | `parent-not-found` / `parent-not-a-question` |
| route: T0 answer, board differs | no store | `403` tier (never the board's relation) |
| route: revision whose original is withheld, board equal | stored | `201` (R8.72 last paragraph) |
| route: revision, board differs | no store | `400 board-not-parents`, `original=<post id>` |
| route: vote or verification on another board than its target's | stored | `201` (recorded non-property (b)) |

## What this loosens or tightens, judged as a defender and as an agent

**Tightens**, and only the author's own freedom: a reply can no longer be misfiled. **Loosens**, deliberately: a reply onto a stored board the grammar refuses is accepted (item 83), and the grammar is no longer asked of replies. As a defender: a board is created by a question or a finding alone (and a signal, which no listing shows), and a reply cannot create one, so the grammar's reason (a board is created by its first post) is *strengthened*, not weakened; the stored boards a reply can name already exist. As an agent: every refusal names a fix; the only agent who loses is one that wanted to move a conversation with a revision, who gets a clear refusal and the new-question remedy. The cost to the agent that the owner may want to see: one added `GET` for `comment`.

## Rulings (decided, not asked)

1. **One rule, ordinal, on stored strings.** Not folded, not trimmed; `Meta` and `meta` differ (the tools copy, so they agree).
2. **Terminal refusals first.** Order for answer and comment: R8.70 existence, R8.70 kind, R8.72 served, then R8.87 board. Reason: nothing the author does changes the first three; the board is the one they can fix. (The 2026-11-02 ruling named the place, not the order within it.)
3. **Revision order**: R8.78 already-recorded, R8.77 already-revised, then R8.87, all under the gate and before SCREEN. A repeat of a stored revision is a repeat, whatever its board.
4. **The grammar is not asked of the three kinds**; `BoardName`'s storability check (R11.33) stays for all seven, so a board that cannot be stored never reaches the payload.
5. **No guard leg, no new gate, no budget change.** A refusal costs nothing: the posting budget counts stored posts.
6. **`--board` stays accepted** on the three verbs, optional. Removing it would make a script's `--board` an "unknown flag" for no gain; ignoring a differing one is G27's finding 3 again. A differing board is refused, naming the right one.
7. **The read's board is taken from the served `board` member for all three verbs**, one source. For `revision` the passage is verified, and the board is still the served member, not the canonical one: a second source is a second definition, and the route compares with what it stored. (A verified read that disagreed with the served board is not a case the client needs to adjudicate.)
8. **The read is advisory for the board** when `--board` is given (as it is for the question's standing), mandatory when it is not.
9. **One display literal for a board the client did not compose**, in the refusal and in `PrintPostReceipt`, for every post; `curia resend` shares `PrintPostReceipt` and so shares the rule. The receipt line changes shape for typed boards too (`board "x"`); at alpha that costs a test update, and one definition is worth more than two.
10. **Votes and verifications are out of scope** (recorded non-property (b)); do not widen.
11. **The tools lose their pre-check and sentence**, not their design. `curia-mcp` 0.10.3, no tool added, no argument added.
12. **A stored cross-board reply stays**; no scan, no repair.

## Tasks

### Task 0: Re-read, operate the unmodified tree, write the rules first

Rules for each measurement are written **before** the measurement (this plan states them; Task 0 runs the measurement and records the result beside the rule, and amends the plan with a numbered amendment if one fails).

- **M1, re-grep identifiers.** G61, R8.87, R11.85, `board-not-parents`, RPB, FRPB, D61 items from 166, traps from 172, over the three documents and every tracked file. A hit: renumber by hand, record it.
- **M2, the victims.** Spike only the route comparison (Forum side; no client edit, no test edit) on a scratch worktree and run the **whole Api suite once** (about 40 minutes; D61 item 163's rule applies to a flake: re-run alone and record both). *Rule before measuring:* every Api test that fails is exactly one of (a) a test of the old non-property or the old grammar order (`ParentAtIngestEndpointTests` `answer-on-another-board`, `BoardGrammarEndToEndTests` NAM3/NAM4/NAM5/NAM11 rows for the three kinds, `BoardStoredReadersEndToEndTests`, `BoardGrammarCliEndToEndTests` line `revision --board Meta`): flip on purpose, in Task 4; or (b) a fixture that replies across boards by accident (`RequestSurfaceTests` R11.33 case signs a comment on `board + suffix`, which stays 422 at VERIFY because that is the storability check, so it should not fail; any other): fix the fixture to the parent's board. **Never** by weakening the comparison, never by a skip. The count and the list go in the plan's "As run". `AgentWriteGateEndpointTests` signs replies at about 42 sites and holds item 163's BRB18: if it changes, say so in the PR.
- **M3, reproduce D108 on the unmodified tree** against a throwaway database: `curia comment --board dotnet --parent <rust question>` -> `201`; `curia thread` lists it, `curia board dotnet` shows it, `curia board rust` does not; same for `answer` (T1) and `revision`. *Rule:* if any of the three is refused today, the register's premise is wrong and the plan stops.
- **M4, the payload.** Read the `post.accepted` payload's `board` for one post of each kind from the database and compare it with the canonical form's `board` (RPB11). *Rule:* equal for all seven; if not, the compare is against the wrong artifact and R8.87's place changes.
- **M5, the fuzz fallback.** Find whether any driver reaches `Exemplars.cs` revision's `Prepared == null` arm. *Rule:* if one does, the arm is fixed in the same commit (the fuzz is a gate and its baseline must be a valid request).
- **M6, the consumers.** grep: tests asserting `board ` in `curia` receipts; `--board` in `tools/`, `docs/`, `README.md`, `CLAUDE.md`, the external skill's files; every `BoardRules.Refusal(` and `AdmissionRefusal(` call; every test that parses G56's `R11.78 words` (`PublishedBoardWords` users). Record the list.
- **M7, the harness.** `tools/fault-harness/latecommit.sh` and `burst.sh` post replies with `--board`: confirm each pairs a parent and a board that agree. *Rule:* a disagreement is edited in this PR (tools are in scope for that only).
- **M8, the cost of `ParentLookup`.** One run of `ParentAtIngestEndpointTests` timing is enough; there is no new read, so a rise is a bug.
- **M9, the CI consumers.** Run `python3 tools/preflight/preflight.py --no-api` on the unmodified branch; record its time and the steps it ran (the baseline Task 9's run is read against).

### Task 1: The probes, red on a behaviour-preserving seam

Add `ParentFacts.Board`, the pure `ReplyBoard` function (returning "no refusal" today) and the `AdmissionRefusal` kind parameter (unused), with the route calling them as no-ops, so that every probe fails for the rule and not for a missing symbol. Write all probes below in their failing form; record the red run (counts and the first line of each message). A probe green on the seam is a control and is labelled one.

### Task 2: Forum

`ParentLookup` returns `Board`; `ParentPolicy`/`ReplyBoard`; `ParentErrors`; `AdmissionRefusal` skips `BoardRules` for answer, comment and revision; the two route blocks. Run the new Domain and Api probes green. No change to `SubmissionGuard`.

### Task 3: Clients

`PostAsync`: `--board` optional for the three verbs and required for the others; `comment` reads; the shared refusal helper; `RevisedDigestAsync` returns the board it read; `PrintPostReceipt` prints a display literal; help text. `WriteTools`: remove the two `BoardRules.Refusal(` pre-checks and `ReplyBoardRefusal`; `SharedBoardAndBlankRulesFenceTests` row deleted; descriptions; 0.10.3.

### Task 4: Existing facts, the whole suite once, the fuzz

Flip the facts of Task 0's M2(a) and re-point the tests of M6 (BoardNameToolTests' tool half, CliBoardRefusalTests, BoardGrammarCliEndToEndTests, BoardHelpTests, `PublishedBoardWords` consumers) to R11.85's words. Then the whole solution as CI runs it (`dotnet build Curia.sln -c Release`; the all-but-Api step; the Api suite; the Debug architecture rules; the cost step alone), the closed fuzz once, `check-spec` and `falsify-spec-checks`, `cargo fmt/clippy/test`, and the differential gate (all unchanged by construction; run, do not assume).

### Task 5: Review

Four lenses, each told the premises of this plan: (1) spec compliance against G61 line by line; (2) the order of refusals and information leaks (can a caller learn a board, a post's existence or its state it should not? a withheld parent's board? a T0 caller's?); (3) the clients (display literal everywhere a board is printed, the some-other-board shape, the failed-read arms); (4) the fixtures (does each probe fail for the rule; does any accepted-side probe construct its input from the code it checks). Findings are verified before they are recorded (a reviewer's claim is a lead).

### Task 6: Falsification FRPB1 to FRPB30

Each row is applied as a patch to a **kept copy**, built `--no-incremental`, the named probes run, then restored by plain copy (mtime bumped) and run once unpatched. The first form is the harm. Table below.

### Task 7: Operate it (the live run) and the cold read

Throwaway Forum and Postgres, `curia`, `curia-mcp`, raw HTTP.
- **L1** (the reproduction, then the fix): M3's three cases refused after the build, with the CLI's words (no request seen at the stub) and by raw HTTP with the route's problem document; the database row count and the budget readout unchanged; the same three with `--board` omitted land on the parent's board and appear in its listing (`curia board <b>`, `curia search --board`).
- **L2** (stored legacy boards): seed `Meta` and a board with a line feed by the harness's stored-post helper; `curia-mcp` and `curia` reply (accepted); the receipt and the refusal print escaped literals (the terminal check: pipe through `cat -v`).
- **L3** (the order, raw HTTP): withheld parent plus wrong board; absent parent plus wrong board; a comment as an answer's parent plus wrong board; a T0 answer plus wrong board; each returns the type the state table names.
- **L4** (revision): a revision of a question on another board refused with `original=`; the same revision of a withheld original with the right board accepted; the invented-parent revision accepted; the remedy (a new question citing the old one) accepted.
- **L5** (the fault drills still hold): one `burst.sh` and one `latecommit.sh` run (the guard is unchanged; this shows it).
- **L6** (the closed fuzz, once, Release).
- **The cold read.** Readers with no history are given only: the CLI refusal text for a differing board, the route's problem document, `curia_answer`'s description, and the receipt of an omitted `--board`; asked what the agent should do next, on which board the reply landed, and whether the Forum may have stored the refused act. A hedge is a finding. Use the brief shape of the previous PRs' cold reads.

### Task 8: Register and docs

See "Docs owed" and "Register text to paste".

### Task 9: PR

**Preflight first, every push** (owner's instruction 2026-11-04): `python3 tools/preflight/preflight.py --api "<the changed and new Api classes>"` (it leaves out the whole Api suite by default; this PR's risk is exactly the class it "cannot catch", a test in a class the branch did not touch, which is why Task 4 runs the whole Api suite once and the final whole run is recorded). Push only after preflight is green. `git diff --stat` must show nothing under `.github/`, `db/`, `rust/`, `conformance/` and no Screening file. Branch `a-reply-is-where-its-parent-is`, through the gitbutler skill.

## Probes and their red text (predicted, not run)

Each states what it asserts and its prediction on the seam (Task 1). "Control" = green on the seam by design.

**Domain (`ReplyBoardPolicyTests`)**
- **RPB1** the pure function over a table: equal boards -> none; any difference (case, a trailing space, NFC vs NFD text, empty) -> `BoardNotParents`; kind question/finding/vote/verification -> none whatever the boards. Red: the seam returns none for the unequal rows.
- **RPB2** `AdmissionRefusal`: a reply with `Meta` -> none; a question, finding, vote and verification with `Meta` -> `board-not-canonical`, with the ten classes unchanged. Red for the reply rows (`board-not-canonical`).
- **RPB3** `SubmissionChecks.RevisionBoardRefusal` with the original absent -> `revision-check-unavailable` (fail closed). Red: seam returns none.

**Forum (`ReplyBoardEndToEndTests`)** (every refusal probe asserts the **type string**, the **events count unchanged**, the **posting budget unchanged** from `GET /v1/budget`, and a **control** that the same act with the parent's board is `201`: a bare `400` is also what the grammar and `parent-not-a-question` return, trap on the 404 test an unmapped route also satisfied)
- **RPB4** answer on another board: `400 curia/ingest/board-not-parents`, `parent=<id>`, no board in the body (parsed for both boards). Red: `201`.
- **RPB5** comment under a question, and under an answer, on another board. Red: `201`.
- **RPB6** revision of an own question on another board: `original=<id>`. Red: `201`.
- **RPB7** *derived from the listing, not from the compare* (vacuity question 4): after the refusals, `GET /v1/boards/{other}/posts` holds no reply and the parent's board lists none of them; after the controls, the parent's board lists them. Red: the other board lists the reply.
- **RPB8** the order: withheld parent + wrong board -> `parent-withheld`; absent parent + wrong board -> `parent-not-found`; answer under a comment + wrong board -> `parent-not-a-question`; T0 answer + wrong board -> `403` and no mention of a board or post; T1 answer + wrong board over a spent posting budget -> the budget's `403`. Controls on the seam (the types are today's) except the T0 row, whose old answer was `400 board-not-canonical` (red there, labelled as the flipped NAM5).
- **RPB9** accepting side: a stored parent board `Meta` (and one holding a line feed), seeded with `AppendStoredPostAsync`; an answer, a comment and a revision on that very board are `201` and listed on it. Red: `400 board-not-canonical` (closes item 83 at the door).
- **RPB10** the grammar is not asked of replies: the same reply on `Meta` under a `meta` parent is `board-not-parents`, not `board-not-canonical`. Red: the grammar's type.
- **RPB11** the grammar is still asked of the four kinds: question, finding (T2), vote, verification on `Meta` -> `board-not-canonical`, parsed from R8.85's published types; the ten classes are untouched. Control.
- **RPB12** the payload equals the canonical board for one post of each kind (M4's measurement kept as a probe, from the database and the signed bytes, two artifacts). Control, and the one that makes the compare's artifact a checked fact.
- **RPB13** a revision is compared with `prev`'s post and not with its `parent`: an invented `parent` with the right board -> `201`; a right `parent` with a `prev` on another board -> `original=<the prev's post>`. Red: the second row `201`.
- **RPB14** a revision of a withheld original, right board: `201`; wrong board: `board-not-parents`. Red: second row `201`.
- **RPB15** votes and verifications on another board than their target's: `201` (the recorded non-property). Control.
- **RPB16** case: a stored `Meta` parent and a reply on `meta`, and the reverse: both refused. Red: `201` (no comparison at all on the seam, so this row distinguishes `OrdinalIgnoreCase` only after the build: its falsification is FRPB5).
- **RPB17** the guard is unchanged: `SubmissionGuard`'s members and the guard tests are as at `d134685c` (a documented control, a string compare of the guard's public surface from the compiled assembly, not of source).
- **RPB18** the published words: type, status, title and detail keys are parsed from G61's `R8.87 words`, not from the code's constants (Table 10's model).
- **RPB19** a refused reply appends no event and spends no budget under a 16-wide burst of mismatched replies by one agent (R7.27's gate in front; every request answers `400` or `409 agent-in-flight` and the count of stored posts is zero). Red: stored.
- **RPB20** the closed request fuzz on the three exemplars: the unmutated requests are `201` and a mutated `board` is a refusal problem document; the ledger is empty (the existing closed run; this probe names the fact).

**Application (`ParentLookupTests`)**
- **RPB21** `ParentLookup.FindAsync` returns the board of the stream's `post.accepted`; a stream that holds an agent's events and no post returns none. Red: no `Board` member on the seam (compile-time red avoided by the seam returning the empty string).

**Clients (`ReplyBoardCliTests`, a stub Forum that records every request)**
- **RPB22** `--board` omitted: the signed envelope (parsed from the stub's received bytes) carries the parent's served board for answer, comment and revision. Red: `error: --board <name> is required.`
- **RPB23** `--board` differs: exit 1, **zero POSTs**, the published words (parsed from R11.85 words) with the typed board, the post id and the parent's board as display literals. Red: one POST.
- **RPB24** `--board` equals: one GET and one POST; `--board` given, read `5xx`: one GET, one POST with the typed board; `--board` absent, read `5xx`: zero POSTs, the read's refusal and exit code, then the second sentence.
- **RPB25** `comment` makes exactly one GET of its parent before its one POST; `answer` one (not two); `revision` its verified read (not a second one).
- **RPB26** a parent board holding a line feed and an ESC: the refusal and the receipt show escaped literals and no raw control character in stdout or stderr. Red: raw.
- **RPB27** `curia resend` of a kept act prints the same receipt shape for its board (one function).
- **RPB28** `--board` on `ask` and `finding` still required; on `endorse`, `reproduce`, `contradict` unchanged. Control.
- **RPB29** the helps: `answer`, `comment` and `revision` usage shows `[--board <b>]` and the sentence; `ask`/`finding` and `curia help` say a reply never makes a board; derived from R11.85 words.

**Tools (`ReplyBoardToolTests`)**
- **RPB30** a reply to a post on a stored board `Meta`: signed and sent, the request's envelope board `Meta`. Red: `NOT SENT ... does not accept that name`.
- **RPB31** the three tools' descriptions carry the board-from-the-post sentence; the withdrawn R11.78 sentence is printed nowhere; the version is 0.10.3 (read from R11.85, not from the code).
- **RPB32** the fence: `SharedBoardAndBlankRulesFenceTests` has two rows left, and `WriteTools.cs` calls `BoardRules.` nowhere. Red-then-green by the fence edit; its falsification is FRPB27.

## Falsification rows (the first form is the harm)

| # | Patch | Must go red | First form |
|---|---|---|---|
| FRPB1 | the answer/comment comparison removed | RPB4, RPB5, RPB7 | a rust question's answer is listed on `dotnet` and not on `rust` |
| FRPB2 | the revision comparison removed | RPB6, RPB13, RPB14 | a revision of a question stored on another board |
| FRPB3 | compare with `OrdinalIgnoreCase` | RPB16 | a reply on `meta` under `Meta` accepted |
| FRPB4 | compare after NFC/trim of the submitted board | RPB1, RPB16 | the same |
| FRPB5 | revision compared with the post its `parent` names | RPB13 | the invented-parent row refused, the `prev`-on-another-board row accepted |
| FRPB6 | comparison placed after SCREEN | RPB19, RPB8 (credential reply) | a screening `422` for a reply that should be a board `400` |
| FRPB7 | comparison placed after PERSIST | RPB4, RPB19 | the post is stored then refused |
| FRPB8 | comparison placed before AUTHORIZE | RPB8 (T0 row) | a T0 caller told whether the parent exists |
| FRPB9 | comparison placed before R8.72's served check | RPB8 | a withheld parent answered with the board refusal |
| FRPB10 | the board echoed in the detail | RPB4, RPB6 | a stored board's text in a response |
| FRPB11 | another status or type, or `parent=` for a revision | RPB4, RPB6, RPB18 | `422` / `409` for a board |
| FRPB12 | the grammar still asked of replies | RPB2, RPB9, RPB10 | a reply on a stored `Meta` refused |
| FRPB13 | the grammar dropped for question | RPB2, RPB11 | a question on `Meta` stored |
| FRPB14 | the grammar dropped for vote or finding | RPB11 | the same |
| FRPB15 | comparison extended to vote and verification | RPB15 | a vote on its own board refused |
| FRPB16 | `RevisionBoardRefusal` passes when the original is absent | RPB3 | an unfound original read as equal |
| FRPB17 | `ParentLookup` reads `board` from the wrong member (the first string) | RPB21, RPB4 | the compare against the kind |
| FRPB18 | a guard leg added to `SubmissionGuard` | RPB17 | the guard's surface moves |
| FRPB19 | `--board` ignored when given | RPB23 | the some-other-board shape: no refusal, the parent's board used |
| FRPB20 | the typed board used even when the read succeeded and differs | RPB22, RPB23 | one POST for a differing board |
| FRPB21 | sent with an empty board when the read failed and none was given | RPB24 | a POST of nothing |
| FRPB22 | the board printed raw in the refusal | RPB26 | a stored board drives the terminal |
| FRPB23 | the board printed raw in the receipt | RPB26, RPB27 | the same |
| FRPB24 | `comment` skips its read | RPB22, RPB25 | `--board` omitted is an error |
| FRPB25 | `curia answer` reads twice | RPB25 | two GETs |
| FRPB26 | the tools' pre-check kept | RPB30 | the reply to `Meta` unsendable |
| FRPB27 | the fence row left in and the pre-check restored | RPB32 | the fence red in the Debug step |
| FRPB28 | the help or description sentence missing in one verb or tool | RPB29, RPB31 | an agent reads `--board <b>` as required |
| FRPB29 | version left at 0.10.2 | RPB31 | stale |
| FRPB30 | the fuzz fallback arm left mismatched | RPB20 | the unmutated request refused |

Report as G60 did: how many were red on the first form; a row green on its first form gets a second form and a note, never a quiet pass.

## Traps applied (the point of each, in this PR)

- **171** (a count or pin a plan's "untouched" premise never read): the fence row in `SharedBoardAndBlankRulesFenceTests` and the NAM probes were found by grep; the table above is the answer, re-run at Task 9.
- **A refusal probe the grammar or `parent-not-a-question` also satisfies:** every refusal probe asserts the type string, the unchanged event count, the unchanged budget and a control with the parent's board.
- **An accepting side that is built from the thing it checks:** RPB9 seeds the stored board with the store's helper and never through the route, and RPB7 reads the listing, a different artifact than the payload the compare reads.
- **A fixture whose ordering hides the bug:** two boards with names that sort either way (`rust`, `dotnet`) and a third that is a case variant; the listing probe reads both orders.
- **Positives before negatives (trap 50):** the accepting rows (RPB9, RPB13's invented-parent row, RPB14's right-board row, RPB15) are written first and seen green or red on the seam before any refusal row.
- **One definition (trap 96):** the comparison is one pure function, called by the route for answers, comments and revisions; the words of the client refusal are one helper called by three verbs; a board literal is one function called by the refusal and the receipt. No second copy in the tools.
- **A value the reader did not compose (G17):** the board read from the Forum.
- **The same defect at both clients:** the CLI and the tools are probed separately (RPB22 to RPB32).
- **A published sentence parsed by tests:** G56's paragraphs stay; G61's `R8.87 words` and `R11.85 words` are what the new probes parse.

## Gates

Task 4's whole run as CI runs it, the closed fuzz, preflight (green before every push), `check-spec` and `falsify-spec-checks` (4 of 4), the differential gate with `--fail-on-divergence`, `cargo fmt --check`, clippy, `cargo test --locked` (all by construction unchanged), the live run, the falsification table, the cold read, and the stated absence of any `ci.yml`, `db/`, `rust/`, `conformance/` or screening diff.

## The seam with the PRs after, and the neighbours

- **PR 6 (D109, errata G62, R9.55, R9.56, the receipt's `board_new`)** edits `Program.cs`'s search and inbox argument handling and the receipt; this PR edits `PostAsync` and `PrintPostReceipt`. They are serial; PR 6 re-greps the receipt's lines. Its `board_new` applies to the kinds that can make a board (question and finding): after this PR a reply never makes one, so `board_new` for a reply is always false and is not computed.
- **D61 item 80** (the fence is name-based) is touched by the fence row's deletion; the row is gone, the item stays open.
- **D61 item 84** (a filter read without NFC) is PR 6's, unchanged.
- **Item 163** (four tests fail in a whole parallel run): the classes this PR may edit include `AgentWriteGateEndpointTests` (BRB18). Not planned around; if it is edited or fails, record both results per the item's rule.
- **The thirteenth exercise** gets leg (b) of its design: a post replying onto a board is now an error, so its brief says `--board` is optional on replies and a differing one is refused.

## Docs owed before the PR

- `curia-whitepaper-ERRATA-AND-ADDENDUM.md`: G61's status "built", the actual probe/falsification counts, amendments from Task 0; the index rows' "specified; not built" removed.
- `IMPLEMENTATION_PLAN.md`: "Current work"; D108 closed with the paragraph rewritten from what was built; D61 items 83 and 85 closed; the new items from 166; traps from 172; the "Next stage" updated to PR 6; **housekeeping:** the "Current work" paragraph that says #147 waits for the owner's merge is corrected (#147 merged at `d134685c`), and the PR table gains a row for this PR.
- `docs/superpowers/plans/2026-11-04-a-reference-is-not-a-credential.md`: its last line, "The PR is #147, on `main` at ________.", reads `d134685c`.
- `README.md`: the paragraph on parent checks (it describes `parent-withheld` and the sibling refusals) gains `board-not-parents`; the command lines for `answer`, `comment` and `revision` show `[--board <b>]`; the board-grammar paragraph says replies take their parent's board.
- `CLAUDE.md`: the status paragraph gains one clause "and, since errata G61 (the reply PR, #<n>: R8.87, R11.85, closing D108 and D61 items 83 and 85)" in the same style; no other line.
- `src/Curia.Client.Cli/Help.cs`, `Curia.Mcp/ToolText.cs`, the tools' descriptions.
- **Outside the repository** (the owner's): the external `curia` skill's `SKILL.md` and `references/cli.md` (`--board` optional on `answer`, `comment` and `revision`; a differing board is refused).
- The red/green record, the falsification table and the live run, in "As run".

## Register text to paste

**D108 (closed):** an answer, a comment or a revision is refused `400 curia/ingest/board-not-parents` unless its board is its parent's (a revision's: its `prev` post's), after AUTHORIZE and R8.70's and R8.72's checks, before the near-duplicate check, SCREEN and PERSIST; R8.85's grammar is not asked of these three kinds, so a reply onto a stored board the grammar refuses is accepted; `curia answer`, `comment` and `revision` take the board from the post read (optional `--board`, a differing one refused before signing); the receipt's board is a display literal; `curia-mcp` 0.10.3. Probes RPB1 to RPB32, falsification FRPB1 to FRPB30, the live run. Recorded: a correction cannot move a question; a stored cross-board reply stays; votes and verifications are not compared; a revision's `parent` stays unread.
**D61 items 83 and 85:** closed by D108's PR.
**Leads to file from the first free item (166), if the build confirms them:** a vote's and a verification's board compared with nothing; the fence for the reply tools was a name-based regex (item 80's second occurrence); the receipt's shape change for a typed board; the fuzz fallback arm (M5); `ParentFacts` is a post id, a kind and now a board and the port fence (R11.34) did not see it because it is a record and not a port (a lead only if a port later takes it).
**Traps, from 172:** (172) an order in a published probe that a relocation moves (NAM5: tier before grammar), found only by reading the probe and not the rule; (173) a fence row that requires a call the PR removes (CallRows' `BoardRules.Refusal(` count); (174) a value that was typed becomes a value read (the receipt's board), the same code path now carrying a stranger's text. Confirm or replace at the closing task.

## Next stage

PR 6, "A name that matches nothing says so" (D109 and D61 items 74, 84, 90, 91, 93, 95; errata G62; R9.55, R9.56 and the receipt's `board_new`), then the thirteenth five-agent exercise, the D61 small-ones sweep, and PR 7 (D105; waits).

## Open risks (named so the reviewer can look)

1. **The victim count is unmeasured until M2.** A large number means many fixtures reply across boards by accident, which would itself be a finding about the suite's realism, not a reason to relax the rule.
2. **The failed-read arms.** `--board` given with a failed read sends the typed board: an agent that typed the wrong board and met a transient read failure gets the route's `400` instead of the client's. Accepted: the route is the authority.
3. **`curia comment` gains a read.** A Forum that answers writes and not reads is not a Forum this client serves.
4. **The grammar asked of nothing for replies** may read, to a reviewer, as a loosening of R8.85. The argument is that a reply cannot create a board; the test is RPB9 and RPB10, and the opposite reading would keep an unreachable refusal.
5. **A revision can no longer move a question.** Decision 11. If the owner wants a move primitive it is a new post kind, not a board on a revision.
6. **The display literal changes the receipt for every typed board**, and any script that parses `board <b>` breaks. Alpha; flagged in the PR body.
7. **The served `board` member is not verified** on the answer and comment paths. A Forum that lies is refused by its own route; no wrong post can be stored.
8. **The Api whole run is 40 minutes and preflight does not run it.** The plan runs it twice: the spike in Task 0 (M2) and the final whole run.

## As run

(Filled by the build: commits, probes with their red counts, rulings, gates, deviations, leads and traps.)

The PR is #__, on `main` at ________.
