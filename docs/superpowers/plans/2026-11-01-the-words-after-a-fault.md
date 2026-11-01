# The Words After a Fault: Implementation Plan (D101, with D61 items 55 to 58)

> Repository path: `docs/superpowers/plans/2026-11-01-the-words-after-a-fault.md`
> Written by `curia-architect` on 2026-11-01 against `main` at `8b21a1b6` (#139, the D95 PR, errata G52, `curia-mcp` 0.9.3), from the tenth exercise's rulings (`.superpowers/sdd/2026-10-31-exercise10/RULINGS.md`, read in full: decisions 2, 4, 5 and 8 and the findings table are this PR's) and the register (D101; D61 items 55 to 58). Errata **G53** (R10.93, R11.73, R11.74; "specified; not built", applied to `curia-whitepaper-ERRATA-AND-ADDENDUM.md` by the job that wrote this plan, with three index rows). Every `file:line` below was read from `main`'s tree; Task 0 re-reads each. **Nothing here has been built, run or measured**: every "predicted red" is a prediction, and every claim that rests on reading alone is a lead that Task 0 measures first.
> Not read, so Task 0 reads it: `ForumTools.cs`'s clock (whether `curia-mcp` holds a `TimeProvider` the recheck tool can use), `Program.cs`'s `ReportRecheck` caller for the same, `CliRecheckTests`/`CitationLinesTests` (what they pin of the `unknown` line; a first grep found no test that holds its words), `HeadStoreLockTests.cs:235` and `ReaderFrameToolTests.cs:337` (the two facts that hold "retained earlier"), the lifecycle check's inputs (`PostVerifier.cs:380-435`: whether it rests on inclusion), `Help.cs:379`'s context, `StubLog` and `CountingProxy` (what a stub token endpoint can count), `KestrelForumFixture` and `FaultableStore` (how `ThrowOnAppend` and `AppendThenThrow` reach a resolve and a flag), and the curia skill's CLI reference (outside this repository: the controller's).

## What the reading found that the register did not know

1. **The tool description says it too.** `ToolText.cs:162-163` ends a recheck's word list with "'unknown' says to drop the citation": the contradiction is in the description an agent reads before it calls, not only in the line it reads after. The register's D101 names the line (`CitationLines.cs:99-100`); the fix covers the description and the help (`Help.cs:164` states `withheld` and `Help.cs:379` explains exit 5 as "withheld or unknown").
2. **No test holds the `unknown` line's words.** A first `git grep` over `tests/` for "then drop it" and "bears this digest" found nothing; the tests hold the state word (`StateWords`, `CitationLinesTests`). The line has never been pinned, which is why the contradiction survived a PR that pinned R11.72's sentence. The new probe pins both ends.
3. **"Retained at" already means a tree size.** `ActaCheck.cs:505` prints "extends the head this client retained at 101" (a size). The ruling's "retained at <instant>" would put two meanings behind one phrase in one block of output. G53 keeps the phrase for the instant and states in a probe (WAF19) that the two are told apart by form; Task 0 reads whether an agent confuses them (M4).
4. **The head's instant has no home.** `HeadStore.Write` stores `head`, `kid`, `log_index` and `signature` (`HeadStore.cs:342`); the file's modification time is a filesystem's clock that a fixture cannot move. G53 adds `retained_at`, written by the one `Write`, under the lock R6.53 (add.) already holds.
5. **"Before this check began" has to be taken before the lock is waited for.** `ConsistencyAsync` waits up to the lock's 30 seconds; a sibling that stores while this call waits stores *after* this call began. A line that took "now" after the lock would call that head earlier. The third form of the equal-head line exists for that case, and WAF17's third leg and WAF18 are the probes.
6. **The leaf's position is known where the cause is.** `InclusionAsync` (`PostVerifier.cs`, the `logIndex >= head.TreeSize` arm) holds the cause the closing line lacks; it is carried as a typed fact of the verification (`PastHeadTreeSize`), not matched out of the detail string. Whether the lifecycle line truly rests on inclusion is read, not run (M5): the words say "which need it" and Task 0 may narrow them to the key check.
7. **No client verb is a token request.** Every refusal of `/oauth/token` reaches the client as the refusal of the call that needed a token (`ForumSession.AccessTokenAsync` is called by each of the call paths at `:258, :312, :338, :395`), so there is one sentence and no per-call variant. The claim it makes ("nothing you sent was read or recorded") is the kind trap 87 names: it is checked against what a stub saw, not against the sentence's own reading.
8. **A repeat's reading has a precondition the sentence did not state.** A resolve of an answer that already stood before the failed call prints the replay words whether or not the failed call recorded anything, and a flag of a type already raised likewise. The reading is only as good as "was not already so before the failed call", and the sentence says that (WAF14).
9. **A late commit is not ruled out by reading.** A fault classified from a `TimeoutException` leaves a client that gave up and a server that may complete the statement, so "unknown" at one instant can precede a commit at a later one. G53's non-property (c) and M3 measure whether the window exists; the `unknown` line says "as of that instant" for that reason.
10. **G52's published spans are read by existing probes at test time.** Editing `R11.72 words` in place before the build would turn ULG25, ULG26, ULG28, ULG29, ULG32 and ULG55 red on `main`'s own tree. G53 therefore publishes replacements beside the old spans and the build edits G52 in the commit that moves the probe helper (G53 decision 7; WAF29 holds that no replaced span is left behind).

## Identifiers (provisional, and what renumbers)

**Allocated 2026-11-01 by the job that wrote this plan, re-grepped (`git grep` over every tracked file, working tree clean apart from this entry, `main` `8b21a1b6`) immediately before the edit:** errata **G53** (highest was G52; the string `G53` occurs only inside the probe name `ULG53`, so the check is by word boundary); requirements **R10.93** (highest of section 10 was R10.92), **R11.73** and **R11.74** (R11.72); no section 4 to 9 number; `curia-mcp` **0.9.4** (current 0.9.3, `ToolCatalogue.cs:41`); the probe prefix **WAF** and the falsification prefix **FWAF**, neither of which occurs in the tree. **Not allocated:** R11.75 to R11.77, which the rulings name for PR 2 (G54: R8.83, R9.53, R11.75), PR 3 (G55: R8.84, R11.76) and PR 4 (G56: R8.85, R9.54, R11.77) and which this PR does not take; a register entry (D101 is closed by this PR and the leads below are filed at the end by the number after a re-grep); trap numbers (the next is 89, and the closing task re-greps). Whoever builds this re-greps every one of these first; if another PR landed, the PR that merges second renumbers by hand (trap 88).

## Scope (exact file set)

The change, in one sentence: four places where a true statement is read by a reader it was not written for now say what they know and when (the `unknown` line, the repeat's reading, the retained head's age and a `NOT ESTABLISHED`'s cause, the token refusal and `whoami`), from one definition each, and the probes follow each instruction across the seam to the act it instructs; no server, no wire, no `db/`, no `ci.yml`.

**Group A, the `unknown` line (R10.93; `Curia.Client`, `Curia.Client.Cli`, `Curia.Mcp`)**
- `src/Curia.Client/CitationLines.cs`: `For(sent, items, answeredAt)` takes the reader's own instant (UTC, to the second, taken by the caller right after the batch returned, from a `TimeProvider`, CS-9); the `unknown` arm composes R10.93 words' two clauses and the instant. `src/Curia.Client.Cli/Program.cs` (`ReportRecheck`, `:667`) and `src/Curia.Mcp/ForumTools.cs` (`RecheckAsync`, `:373`) pass it.
- `src/Curia.Mcp/ToolText.cs` (`:162-163`) and `src/Curia.Client.Cli/Help.cs` (`:164`, `:379`): `unknown` names no post and carries both readings; "says to drop the citation" is gone.

**Group B, the repeat's reading (R11.73; `Curia.Client`)**
- `src/Curia.Client/StoreWording.cs`: `StoreUnavailableSentence` equals R11.73 words; the two phrases of the reading pairs are constants the receipts' printers share (`curia resolve`'s and `curia_resolve`'s replay line at `Program.cs:826` and `WriteTools.cs:304`; the flag refusal's title is the Forum's, `RaiseFlag.AlreadyRaised`, quoted by `Refusal.Summary`). `WriteSentence` and `RepeatSentence` follow.

**Group C, the verification's lines (R11.74 a, b; `Curia.Client`)**
- `src/Curia.Client/HeadStore.cs`: `Write(forum, head, retainedAt)` stores `retained_at` (UTC, to the millisecond); `ReadRetained(forum)` returns the head and its instant or `null`; `Read` stays for callers that need no instant. `src/Curia.Client/PostVerifier.cs`: `ConsistencyAsync` takes `began` from `_time` before the lock is waited for; `CompareAndRetainAsync` words the equal-head line in the three forms and `NOT STATED`; the extension line is unchanged; `InclusionAsync` carries `PastHeadTreeSize` into `PostVerification`; `Render`'s `NOT ESTABLISHED` closing names the cause where it is the one cause.

**Group D, the token and `whoami` (R11.74 c)**
- `StoreWording.TokenSentence` equals R11.74 words; `ForumSession.TokenStatus` says `cached, expired; a new one is requested at the next call`; `src/Curia.Client.Cli/Program.cs:223` unchanged in form. `curia-mcp` 0.9.4 (`ToolCatalogue.Version`, the pins and the parsers PR 3's ruling 23 of the D95 plan made one).

**Tests (file names are the build's, ids the plan's)**
- `tests/Shared/PublishedClientWords.cs` (new): reads G53's three `words` paragraphs (`UnknownLine`, `ReadingPairs`, `StoreSentence`, `EqualHeadLine`, `PastHeadClosing`, `TokenSentence`, `WhoamiExpired`), each parse asserting its count before it returns; `tests/Shared/PublishedStoreFaults.cs` is moved by Task 2 to read the replaced sentences from G53.
- `tests/Curia.Api.Tests/WordsAfterFaultKestrelTests.cs` (Kestrel with `FaultableStore`: WAF1 to WAF3, WAF10 to WAF15, WAF18, WAF20 to WAF23), `tests/Curia.Client.Tests/RecheckUnknownWordsTests.cs` (WAF4 to WAF8), `tests/Curia.Client.Tests/RetainedHeadInstantTests.cs` (WAF16, WAF17, WAF19), `tests/Curia.Client.Tests/TokenWordsTests.cs` and `tests/Curia.Mcp.Tests/TokenWordsToolTests.cs` (WAF24 to WAF27), `tests/Curia.Client.Tests/PublishedClientWordsTests.cs` (WAF28 to WAF31).
- **Existing facts that change their object (named in each commit; Task 0's M7 and M8 complete the list):** `HeadStoreLockTests.cs:235` and `ReaderFrameToolTests.cs:337` hold "retained earlier"; ULG25, ULG26, ULG28, ULG29, ULG30, ULG32, ULG55 to ULG57, ULG60 read G52's two spans; `HelpTextTests`/`ToolText` facts that derive the sentence; every fact that holds `Nothing was minted` or the `unknown` line.
- **Controls that stay green, named so the list is complete:** the exit codes of `recheck` (5 for `unknown`), the `malformed`, `withheld`, `current`, `superseded` and `disputed` lines, the extension line, the lock (`HeadStoreLockTests`), a healthy store's every contract fact, ULG27's no-retry.

**Docs**: errata G53 (Task 9 sets its status), `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`'s status paragraph (one clause), `README.md` (the version, and where it describes the verification's lines), `Help.cs` and `ToolText.cs` in the same branch.

**No change:** `src/Curia.Api`, `src/Curia.Infrastructure`, `src/Curia.Application`, `src/Curia.Domain*`, `db/`, `rust/`, `tools/`, `conformance/`, `.github/workflows/ci.yml`, R15.1's set, any response member, any exit code, the lock R6.53 (add.) holds, the order of a verification's checks, Table 10 and Table 11, the white paper.

**Out of scope, named so nobody drifts into it:** the `withheld` line's "drop this citation" for an act that was recorded and then withheld (G53 non-property g; a lead); a wire instant on the batch; a resend or a retained signed act (D99); a readiness route; the cadence of signed heads (the operator's); the rider's `FAILED` leg (needs a forked log: the next live run's controller act or the eleventh exercise); D100, D102 to D105.

## Rulings (decided, not asked)

1. **The `unknown` line says both readings, resend first, with the reader's own instant** (G53 decision 1). The Forum cannot tell a mistyped citation from an act that was not recorded, so the line says what the Forum said, when, and what each case means. Rejected: two lines (exit and ranking are per line); a wire instant (a wire change in a words PR).
2. **A probe that follows an instruction across the seam takes its input from the instruction's own output.** The digest a probe rechecks is parsed out of the act line a real client printed after a real `503`, and "recorded" is the events table. A digest built by the test from the envelope it sent, or a status code, would derive from the artifact it checks (vacuity question 4); this is the correction of the D95 PR's pinning, which read R11.72's words and the `unknown` line each in isolation.
3. **The repeat's reading is told in the words the repeat prints, and a probe reads the pairs from the errata.** The two phrases are published beside their meaning; the probe drives the act, reads the meaning from the errata and the outcome from the events table, so a client sentence that reverses them and an errata edit that reverses them are each caught by a different artifact (FWAF8, both forms).
4. **The instant a head was stored is a member of the file, from the verifier's clock.** Not the file's mtime; not a sidecar; one `Write` sets it, so the equal-head path that does not write leaves it alone and the extension path replaces it (WAF16).
5. **"Began" is taken before the lock, and the line has three forms and a `NOT STATED`.** Which of the three is printed is decided by the two instants only; no form is chosen by whether the lock was waited for.
6. **The past-head cause is a fact the verification carries, and the closing line never claims a sole cause where another check also could not be made.** Four lines of absence with one reason is clear; four lines of absence with a reason that hides a fifth is the trap.
7. **One token sentence for every call.** No call is a token request. Its claim is checked by a counting stub, not by reading.
8. **G52's two replaced spans move at the build, in one commit** (reading finding 10, G53 decision 7).
9. **Each of Groups A to D is its own commit and none depends on another**; Group A goes first (the ruling's order: it corrects the sentence the last merged PR left contradicting its own requirement).

## Tasks

### Task 0: Re-read, operate the unmodified tree, commit the seam and the probes red

Start from `main` after any merge that has landed; otherwise from `8b21a1b6`. Re-read G53 and `IMPLEMENTATION_PLAN.md`; **re-grep every identifier under "Identifiers"** (G53 by word boundary, since `ULG53` contains it); run `check-spec.py` and `falsify-spec-checks.py` (both pass first); build `Curia.sln` in Release first and run the baseline `--no-build` as CI does, recording the failures by name and the Api, Client and Mcp suites' times. Then, **with the tree unmodified**, build Release in a detached worktree outside the repository (own `bin/obj`, own port, a throwaway database, Postgres with pgvector) and record verbatim, counting in the database and never in the responses:

1. **M1, D101 as seen, on the faults that make it.** Through `FaultableStore` on a Kestrel host (`ThrowOnAppend`, `AppendThenThrow`) **and** through the live grant window (`REVOKE INSERT` on `events` from the application role for a refusal before the append; the exercise's commit-cut proxy, kept in `.superpowers/keep-2026-10-31-ex10/`, for a commit and then a cut): `curia ask`, then `recheck` of the digest the act line printed, by `curia` and by `curia-mcp`. Record the status, the act line, the `recheck` line verbatim, the exit code, and the events table's count. Fills WAF1 to WAF3's "actual".
2. **M2, the repeat of a resolve and of a flag, in four legs and two clients.** `ThrowOnAppend` and `AppendThenThrow` for an acceptance and for a flag, then the repeat: the lines `curia resolve`, `curia_resolve`, `curia flag` and `curia_flag` print, and the events. Also: a flag repeated with the flag budget spent (which refusal wins, the budget's or `already-raised`), an acceptance repeated with the acceptance budget spent (R7.26 says a replay is never refused), and a resolve of an answer already accepted before a faulted call. **Decides** WAF13 to WAF15 and whether "any other kind says nothing" is true as written.
3. **M3, the late commit.** A fault classified from a `TimeoutException`: hold the append past the command timeout with a server-side delay (a trigger or a proxy delaying the COMMIT) and watch the events table after the client's `503`: does a post land after `unknown` was answered, and how long after. **Decides** non-property (c) of G53: if the window exists, the resend clause gains the bound the measurement names and the plan amends in place; if not, the clause stands and "as of that instant" is the whole of it.
4. **M4, the parallel verify.** Fourteen `curia_verify` calls in parallel from a fresh client home against a Forum with one signed head (the auditor's R1 and R2): every line verbatim, which call printed `FirstRetained`, which printed the equal-head line, the lock waits, `head.json`'s members and mtime before and after. Fills WAF18's "actual" and answers whether an agent confuses the instant with the tree size (finding 3).
5. **M5, a fresh post's `verify`.** Head signed at tree size N, then a post at leaf N (and at N+3, the middle and past it, trap 77): every line and the closing verbatim, exit 8; then the same with the key set answering `500`, and with no head signed at all. Read `PostVerifier.cs:380-435` and record whether the lifecycle line rests on the post's inclusion. **Decides** the words "which need it" (R11.74 words) and WAF22's legs.
6. **M6, the token words, per verb.** With `SELECT` revoked on `events` for the token's read (and with the RLS whole-log fault, the D95 plan's method), run every verb of `curia` and every tool of `curia-mcp` that needs a token and record the line and the exit; count in the Forum's request log (not the client's) every request that reached it. **Decides** whether "nothing you sent was read or recorded" holds for each (trap 82: a claim about which requests a path makes is a lead until a leg counts them), and `curia whoami` before and after the cached token ages out (the fake clock is not enough: record a real expiry).
7. **M7, what pins the words.** `git grep` over `src`, `tests`, `docs`, `README.md`, `CLAUDE.md`, `tools` for `retained earlier`, `Nothing was minted`, `bears this digest`, `then drop it`, `says to drop the citation`, `"expired"`, `a repeat of a resolve or of a flag is safe`, `0.9.3`, every parser of `curia-mcp`'s version. Each is a fact that changes its object, named.
8. **M8, who prints a head's age.** Every site that reads or writes `head.json` (`HeadStore`, `PostVerifier`, the operator tool, `curia-testis`, `tools/Curia.Differential`): the Rust verifier and the differential gate must not read `retained_at` as a signed member (it is not one) and must ignore it.
9. **M9, the baseline cost.** The Client, Mcp and Api suites' time on the unmodified tree.

Write the probes below in their failing form, run them against the seam tree, record each actual red text beside the prediction (a probe green on the seam tree is a control and says so), and amend this plan for what Task 0 changes, in place, dated, continuing the ruling numbers. **The seam (behaviour-preserving):** `CitationLines.For` takes `answeredAt` and ignores it; `HeadStore.Write` takes `retainedAt` and ignores it, `ReadRetained` returns the head and a `null` instant; `PostVerifier` takes `began` and ignores it; `PostVerification.PastHeadTreeSize` exists and is always `null`; `PublishedClientWords` exists and reads G53; the stub token endpoint counts requests. No word moves, so a probe is red by assertion and no red state is built from a stub that is itself a defect (trap 67). Commit the seam, then the probes red (`but commit`), before any behaviour.

### Task 1: The `unknown` line (R10.93)
`CitationLines`, both callers, `ToolText`, `Help`. WAF1 to WAF8 go green.

### Task 2: The repeat's reading (R11.73)
`StoreWording`; the two phrase constants shared by the printers; `PublishedStoreFaults` moved to read the store sentence from G53; G52's `R11.72 words` store span replaced by a pointer **in this commit**. WAF9 to WAF15 go green.

### Task 3: The verification's lines (R11.74 a, b)
`HeadStore`, `PostVerifier`. WAF16 to WAF23 go green.

### Task 4: The token and `whoami` (R11.74 c)
`StoreWording.TokenSentence`, `TokenStatus`, G52's token span replaced by a pointer **in this commit**, `curia-mcp` 0.9.4. WAF24 to WAF31 go green.

### Task 5: Existing facts, stale sentences, the whole suite once
The facts that change their object (each named in its commit); a `git grep` for `retained earlier`, `Nothing was minted`, `bears this digest; check the encoding`, `says to drop the citation`, `is safe, because the Forum answers`, each retired with a reason beside it; then the whole suite once, built first and `--no-build`, the request fuzzer included; compare with Task 0's baseline: nothing new; **report the added minutes of the Client, Mcp and Api suites** (more than two on the Api suite is a finding: this PR adds Kestrel facts and a 14-wide concurrent leg).

### Task 6: Review
Spec compliance, then code quality (two passes); fixes in their own commit. The reviewer is asked first: does any line say a thing true of its own call that a reader will read as true of another (read each of the four); does any new line carry a value the Forum supplied that is not a display literal (the instant is the client's; the tree size is a number the client parsed); can `retained_at` be read as a signed member by anything; does `curia whoami` or any word path make a call it did not; does a client keep or resend an act.

### Task 7: Falsification FWAF1 to FWAF22
Each row on a kept copy (never `git checkout -- <path>`), rebuilt `--no-incremental`, restored with a plain copy and `touch`, one unpatched run after. Record the probes red per row and the second form where the first was green. A row green on its first form is a gap in a probe, and the probe is added before the row is closed. FWAF8's second form edits a kept copy of the errata, not the code.

### Task 8: Operate it again (the live run)
Against the built Release tree, a throwaway database, the events table and the Forum's request log as the oracles:
- **L1, an act not recorded.** The grant window (`REVOKE INSERT` on `events`): `curia ask` and `curia_ask` print `store/unavailable` and the act line; `recheck` and `curia_recheck` of that digest print the new `unknown` line, resend clause first, with the instant; restore the grant and send again: one post stored.
- **L2, an act recorded.** The commit-cut window: the same, and `recheck` answers `current` with the post id and no resend clause; the events table holds the post once.
- **L3, resolve and flag, both windows.** Each repeat's line against the published pair; the events agree with the meaning the errata gives.
- **L4, the parallel verify.** M4 repeated: exactly one `no earlier head`, every equal line states `retained at <instant>` and a form a reader can tell from an earlier head; `head.json` holds one `retained_at`.
- **L5, a fresh post.** Head signed, then a post: `curia verify` and `curia_verify` close with the cause and the tree size and exit 8; sign a head: VERIFIED.
- **L6, the token words.** With the log's `SELECT` revoked: `curia budget`, `curia ask`, `curia_budget`, `curia_ask` print the session's-token sentence and exit 8; the Forum's request log shows only `POST /oauth/token`; `curia whoami` of an aged-out token says why, and makes no request.
- **L7, a cold read.** Two agents, each given only the CLI's text for L1 and L2 (the refusal, the act line, the `recheck` output) and asked whether to send the act again: expected **resend in L1 only**. The controller dispatches it (a proposal, not assumed), and it is the eleventh exercise's item too.
- **L8, the gates.** The request fuzzer's closed pass, ledger empty; no `curia_e2e_*` database or role left; every grant restored.

### Task 9: Register and docs
Close D101 and D61 items 55 to 58 with their paragraph; G53's Status from "specified; not built" to "built" with the PR number and dates and a pointer from G52's amended spans; the traps (the next number after a re-grep) and "As run"; `CLAUDE.md`'s status paragraph (one clause); `README.md`; `Help.cs` and `ToolText.cs` in the same branch; the numbers taken as written or renumbered by what Task 0 re-grepped. File the leads below. The curia skill's CLI reference is outside this repository and is the controller's.

### Task 10: PR
Branch, push, open; CI green; merge-time path check (`ci.yml` untouched, nothing under `db/`, `rust/`, `conformance/` or `src/Curia.Api`, the white paper unchanged). Merge on green per standing authority.

## Probes and their red text (predicted, not run)

"Red now" is what the **seam tree** should print. Every positive probe is red by assertion (a line's words, an instant, a count, a stored row); every negative probe carries its positive in the same fact. Every sentence, phrase, form and version is read from G53 by `PublishedClientWords`, never from the code. **Host** is K (Kestrel with `FaultableStore`, the real client, Development), S (a stub Forum that counts requests), U (unit).

### Group A: the `unknown` line (R10.93)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| WAF1 | **Across the seam, an act that was not recorded, `curia`.** `ask` under `ThrowOnAppend` prints `503 store/unavailable` and the act line; the digest is **parsed from the printed act line**; `recheck <digest>` prints a line that begins `unknown`, holds R10.93's resend clause and its instant, and the events table holds nothing | K | `lacks "If it is the digest of an act a refusal printed"`; actual `unknown  sha256:...  no post here bears this digest; check the encoding (sha256:<64 hex>), then drop it` |
| WAF2 | **The same, `curia-mcp`** (`curia_ask`'s `isError` text, then `curia_recheck`) | K | the same text |
| WAF3 | **Control: an act that was recorded.** `AppendThenThrow`: the act line's digest, `recheck`: `current` with the post id, no resend clause in any of the six states' lines but `unknown`; the events table holds the post once | K | green (control); red under FWAF4 |
| WAF4 | **A digest that is a typo.** A shape-valid digest never signed: the line holds the spelling clause and the drop clause, and the resend clause **precedes** the spelling clause by index | U | `lacks "if you meant to cite a post"` (the old line has "check the encoding", not R10.93's clause) |
| WAF5 | **The instant is the reader's clock, two values.** A fake `TimeProvider` at two instants: the line holds each, to the second, in `2026-11-01T09:30:00Z` form | U | `lacks "(answered at 2026-"` |
| WAF6 | **The description and the help say it too.** `curia_recheck`'s description and `curia recheck`'s help hold both readings and neither says `unknown` means drop the citation | U | `ToolText.cs: "'unknown' says to drop the citation"` |
| WAF7 | **Controls: the exit and the neighbours.** `unknown` still exits 5 and ranks as before; the `malformed`, `withheld`, `current`, `superseded`, `disputed` lines are byte-for-byte as before (the `withheld` line's "drop this citation" is pinned as the status quo, G53 non-property g) | U | green (control) |
| WAF8 | **`PublishedClientWords` reads R10.93:** the clause starts and the order, count asserted | U | green (control; red only under FWAF2's errata form) |

### Group B: the repeat's reading (R11.73)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| WAF9 | **The store sentence equals R11.73 words**, carried by the refusal in `curia` and `curia-mcp`, by every writing verb's help and every write tool's description (the lists derived from the dispatch and the catalogue, the count written after, trap 81) | U | `lacks "was recorded the first time"` |
| WAF10 | **`curia resolve`, the pair, both sides.** The phrase is read from R11.73's pairs; a recording `resolve` prints a receipt without it; the repeat prints it; the store sentence contains it | K | the sentence leg: `lacks "already the accepted answer; nothing was recorded"`; the printing legs are controls |
| WAF11 | **`curia_resolve`, the same** | K | the same |
| WAF12 | **`curia flag` and `curia_flag`, the pair, both sides.** The repeat's refusal holds the phrase; the recorded flag's receipt lacks it; the sentence contains it | K | `lacks "has already raised a flag of this type against this post"` in the sentence |
| WAF13 | **The reading is true, end to end, four legs per verb.** For a resolve and a flag, `AppendThenThrow` and `ThrowOnAppend`: the first call answers `503`, the repeat's print is compared with the errata's pair, and the **meaning the errata gives is compared with the events table** (one event after a commit and a repeat; one event, made by the repeat, after no commit) | K | the sentence leg red as in WAF10 and WAF12; the legs are controls (the Forum already behaves so) |
| WAF14 | **The condition is stated and is true.** A resolve of an answer already accepted, then a faulted repeat that records nothing, then another repeat: the print is the replay phrase though the faulted call recorded nothing (pinned); the sentence says the reading holds "only if" before the failed call | K | `lacks "this holds only if the answer was not already the accepted one"` |
| WAF15 | **"Any other kind says nothing" is true.** The flag repeated with the flag budget spent is refused for the budget with no `already raised` phrase; the sentence says an answer of any other kind says nothing of the first call | K | sentence leg: `lacks "an answer of any other kind says nothing"`; the budget leg is a control (M2) |

### Group C: the verification's lines (R11.74 a, b)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| WAF16 | **The instant is stored by the one write.** `Write` then `ReadRetained` returns the instant; `head.json` holds `retained_at`; a file without it reads as `null`; an extension replaces the instant; an equal head leaves it | U | `retained_at is missing from head.json` |
| WAF17 | **The equal-head line, three forms and `NOT STATED`, on a clock that advances.** The sibling's head stored 10 s, 0.2 s and (while the lock is held) after the check began, and a file with no instant: each form's published words, the instant in the published form, and **no line says "retained earlier"** | U | `lacks "retained at 2026-"`; actual `the served head equals the head this client retained earlier` |
| WAF18 | **Concurrent, with overlap.** Fourteen verifications of a fresh home against a Forum with one head, behind a barrier so that at least two wait on the lock (asserted, or the fact fails as vacuous, trap 77): exactly one `no earlier head`; every other line's instant equals the file's `retained_at` and its form is not `<k> s` with k of a second or more | K | `14 lines say "retained earlier"` or `lacks "retained at"` |
| WAF19 | **Control: two meanings of "retained at".** The equal line's token after "retained at" parses as an instant; the extension line's is an integer and is unchanged | U | green (control) |
| WAF20 | **Past the head, in the middle and beyond it.** A head at tree size N, a post at leaf N and one at N+3: `curia verify` and `curia_verify` close with R11.74's cause, the tree size equal to the **Forum's own head route's**, exit 8 | K | `lacks "the post is newer than the latest signed head"`; actual `NOT ESTABLISHED. Nothing failed, and something could not be checked.` |
| WAF21 | **Control: after the next head.** The same post, a second head signed: `VERIFIED.`, no cause words | K | green (control) |
| WAF22 | **No sole cause is claimed over another.** (a) past the head and the key set answering `500`: the cause words and `Another check could not be made`; (b) no head at all: no "newer than the latest signed head" | K | (a) `lacks "Another check could not be made"` and `lacks` the cause; (b) is a control |
| WAF23 | **Control: a failed check is not softened.** A failed signature on a post past the head: `FAILED.` text unchanged, no cause words | K | green (control) |

### Group D: the token and `whoami` (R11.74 c)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| WAF24 | **The token's words, and what was sent.** A stub `/oauth/token` answers `503 temporarily_unavailable`; each of `curia ask`, `budget`, `read`, `flag` and `curia_budget`, `curia_ask`, `curia_read` is refused with R11.74's token sentence and exit 8 (CLI), **and the stub saw no request but the token's** (the claim in the sentence, counted) | S | `lacks "could not issue this session's token"`; actual `Nothing was minted: a new request can be sent later` |
| WAF25 | **Control: no retry.** Exactly one token request per call, in both clients | S | green (control; ULG27's twin) |
| WAF26 | **`whoami`, three states and no call.** A cached token expired, none cached, valid: `cached, expired; a new one is requested at the next call`, `none cached`, `valid for <n>s`; the stub counts no request | S | `lacks "cached, expired"`; actual `token     expired` |
| WAF27 | **A scan: no client word says "Nothing was minted" or a bare `expired` for a token** (sources, helps, descriptions) | U | `src/Curia.Client/StoreWording.cs: "Nothing was minted"` |

### Group E: the entry, the words, the version

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| WAF28 | **`PublishedClientWords` reads G53:** every span parsed with its count asserted; a missing paragraph fails by name | U | green (control; G53 is applied) |
| WAF29 | **No replaced span is left in G52** after the build: G52's `R11.72 words` holds neither "a repeat of a resolve or of a flag is safe, because" nor "Nothing was minted", and holds the pointer | U | `G52's R11.72 words still hold "Nothing was minted"` (red until Tasks 2 and 4 edit it) |
| WAF30 | **No test hand-copies a G53 sentence** (a scan, as ULG33) | U | green (control) |
| WAF31 | **`curia-mcp` is 0.9.4** by the one parser, the pins and the twin | U | `expected 0.9.4, actual 0.9.3` |

*Counts:* WAF1 to WAF31. Controls on the seam tree: WAF3, WAF7, WAF8, WAF19, WAF21, WAF23, WAF25, WAF28, WAF30 (nine); the legs of WAF10 to WAF13 and WAF15 that print what the Forum already says are controls inside red facts. The red text of each row is a prediction from reading until the seam tree runs it; Task 0b records the actual (trap 81).

## Falsification rows

Each row: the patch (first form), a second form for where the first is likely green, and the probes that must go red. A row green on its first form is a gap in a probe.

| # | First form | Second form | Probes |
|---|---|---|---|
| FWAF1 | restore "check the encoding (sha256:<64 hex>), then drop it" | keep only the spelling and drop clauses of R10.93 (say nothing of an act) | WAF1, WAF2, WAF4 |
| FWAF2 | the resend clause second | the resend clause without "a refusal printed" | WAF4, WAF1, WAF8 (the second form edits a kept errata copy) |
| FWAF3 | the instant from `TimeProvider.System` | a constant | WAF5 |
| FWAF4 | the resend clause on every state's line | on `withheld` only | WAF3, WAF7 |
| FWAF5 | `unknown` exits 0 | exits 6 | WAF7 |
| FWAF6 | the tool description keeps "says to drop the citation" | the help keeps it | WAF6 |
| FWAF7 | the store sentence omits the reading | omits the condition only; omits "any other kind" only | WAF9, WAF14, WAF15 |
| FWAF8 | the client's sentence reverses which phrase means recorded | the errata's pair reversed on a kept copy | WAF9; WAF13 (the legs: the meaning against the events table) |
| FWAF9 | the reading for a resolve and none for a flag | for a flag and none for a resolve | WAF9, WAF12 |
| FWAF10 | a recording `resolve` prints the replay phrase | `curia_flag`'s receipt prints "has already raised" | WAF10, WAF11, WAF12 |
| FWAF11 | `retained_at` from the system clock | not rewritten on an extension | WAF16, WAF17 |
| FWAF12 | "retained earlier" kept for the within-a-second form | kept for the after-began form | WAF17 |
| FWAF13 | `began` taken after the lock | `began` taken at the verifier's construction | WAF17 (third form), WAF18 |
| FWAF14 | the past-head closing for every `NOT ESTABLISHED` | for a sole cause where another check also could not be made | WAF22, WAF23 |
| FWAF15 | the tree size from the post's proof | from the retained head | WAF20 (the Forum's own head route is the oracle) |
| FWAF16 | the cause matched out of the detail string | the typed fact never cleared when a check failed | WAF22, WAF23 |
| FWAF17 | the token sentence keeps "Nothing was minted" | in `curia-mcp` only | WAF24, WAF27 |
| FWAF18 | `whoami` prints `expired` unqualified | `none cached` also says expired | WAF26 |
| FWAF19 | `whoami` calls the Forum to refresh | the token line only | WAF26 (the stub counts) |
| FWAF20 | one retry of a token refusal | in `curia-mcp` | WAF25 |
| FWAF21 | `curia-mcp` stays 0.9.3 | the pins bumped and `Version` left | WAF31 |
| FWAF22 | G52's replaced spans left whole | G53 applied and G52's pointer missing | WAF29 |

**22 rows.** Every red probe is named by at least one row and every control by at least one (WAF3 and WAF7 by FWAF4 and FWAF5, WAF19 by FWAF11's second form, WAF21 by FWAF14, WAF23 by FWAF14 and FWAF16, WAF25 by FWAF20). A row that is green on both forms is a gap in the probe it was meant to move, and the probe is changed before the row is closed.

## Gates

As #139: every .NET assembly in Release built first and run `--no-build` (`ScreeningCostTests` excluded), Postgres with pgvector, the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `check-spec.py` and `falsify-spec-checks.py`, the differential comparison with `--fail-on-divergence`, the request fuzzer's closed pass. The fences (CS-7's layering, the port-string fence, the JSON-reader fence, CS-15) must hold: nothing here adds a port, and `HeadStore`'s new member is the client's, outside the signed head.

## Docs owed before the PR

- Errata G53 applied 2026-11-01 (uncommitted in the working tree when this plan was written), with its three index rows; Task 9 sets its Status to built, names the PR, and takes or renumbers the provisional numbers by what Task 0 re-grepped.
- `IMPLEMENTATION_PLAN.md`: "Current work" names this PR as planned; at the end D101 and D61 items 55 to 58 closed with the paragraph below, "As run" in this plan, the traps.
- `CLAUDE.md`'s status paragraph: one clause for G53. `README.md`: the version and the verification's lines. `Help.cs` and `ToolText.cs` in the same branch.

## Register text to paste

> **D101 and D61 items 55 to 58 — closed by the "The words after a fault" PR (errata G53: R10.93, R11.73, R11.74; `curia-mcp` 0.9.4).** `recheck`'s `unknown` line says that no post bears the digest as of the call (with the reader's own instant), that an act a refusal printed was not recorded as of that instant and may be sent again, and that a citation meant is to be spelled right and dropped, in that order; the description and the help say that `unknown` names no post. The store sentence says how the repeat of a resolve or a flag is read, in the words the repeat prints, with its condition (item 55). An equal-head verification line says when the retained head was stored (`retained_at` in the retained file, from the verifier's clock) against the instant the check began (item 56), and a `NOT ESTABLISHED` over a leaf past the latest signed head names that cause with the tree size, without claiming a sole cause (item 57). The token sentence says the Forum could not issue this session's token and that nothing sent was read or recorded, and `whoami` says why a cached token looks expired (item 58). Open: D100 and D102 to D105; items 59 to 61 are leads.
>
> **Leads to file (unfiled until the build re-greps the number):** (a) the `withheld` line says "drop this citation" of an act that was recorded and then withheld; for an act the right words are that it is recorded and not to be sent again. (b) If M3 finds a late commit after a command timeout, the `unknown` line's resend clause needs a bound, and D99's keeping of the signed bytes is worth more than it looked.
>
> **Trap candidates (numbers by re-grep at the closing task; the last is 88).** *An instruction pinned from both ends and never run across the seam:* R11.72's sentence and the `unknown` line were each tested for their own words and no probe took the digest from the act line and asked the question the sentence asks; take a probe's input from the instruction's own output. *A fixture that cannot see the quantity under test:* a frozen clock cannot see elapsed time, a sequential run cannot see a sibling, a Forum whose head is current cannot see a leaf past it, and a stub that answers every route cannot see that nothing was sent; each probe here is built so that the quantity changes the answer. *A line true of its own call and read as another's:* "retained earlier", "Nothing was minted", "drop it", "safe": each was correct where it was written and printed where its reader was someone else.

## Next stage

**PR 2, "A committed post is found"** (D100; errata G54: R8.83, R9.53, R11.75; `curia-mcp` 0.9.5), then PR 3 (D104, G55), PR 4 (D102 and D103, G56), PR 5 (D105, G57, waits); the eleventh exercise after PR 3, with the commit-cut window re-run and "no `503` over a recorded post" named in advance, the R0 drill, the rider's `FAILED` leg as a controller act, and one agent given only the CLI text and asked what `recheck` of an unknown digest means (this PR's L7 is its dry run). The exercise's controller harness (`ctl/`) is promoted to `tools/` before PR 2's Task 0, as the rulings ask.

## Open risks (named so the reviewer can look)

1. **M3 may find a late commit.** Then the resend clause is not "may be sent again" unqualified; the plan amends in place and the sentence gains the bound. Until measured, "as of that instant" is the guard.
2. **The concurrent probe can be vacuous.** Fourteen verifications that happen to run one after another produce the same lines as fourteen that overlap; WAF18 asserts overlap (a barrier, and a count of calls that waited on the lock) and fails as vacuous otherwise.
3. **`retained_at` is a member of a file that other readers open.** The Rust verifier and the differential gate read heads from the Forum, not this file (M8 confirms); the member is unsigned and must never be read as signed.
4. **The lifecycle line may not rest on inclusion.** M5 reads it; if it does not, the words say "the key check, which needs it" and WAF20 and WAF22 follow.
5. **The two meanings of "retained at".** An agent may read the instant as a tree size or the reverse; WAF19 holds the forms apart and M4 records whether the output confuses (finding 3). If it does, the instant's phrase changes to "stored at" and G53's words with it.
6. **A sentence of sixteen places.** The store sentence is longer by a third; the six descriptions and ten helps carry it from one definition, and a description's length is not a requirement.
7. **Identifiers are provisional** (see the top): G53, R10.93, R11.73, R11.74, 0.9.4, WAF, FWAF, and the candidate trap numbers.
