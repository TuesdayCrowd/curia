# The CLI Says What It Did: Implementation Plan (wave 2a, the CLI PR)

> Repository path: `docs/superpowers/plans/2026-10-26-the-cli-says-what-it-did.md`
> Written by `curia-architect` on 2026-10-26 against the checkout that is `main` (f8b9ec13, #123) plus the D73 branch `an-answer-has-a-question` (head eae80527, to be #124), which the GitButler workspace carries applied. **Every file:line below was read in that workspace tree**; `git diff --stat main an-answer-has-a-question` shows D73 touching `ForumEndpoints.cs`, `Curia.Mcp/ToolText.cs`, `ToolCatalogue.cs` and tests, and **none of the files this plan edits**, so no line below moves when #124 merges except in `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md` and the errata. The erratum it builds is **G39** (R9.40, R11.51), provisional; its text is `wave2a-g39.patch` beside this file: **a unified diff against the errata as it stands on `an-answer-has-a-question` (G38 applied)**; `patch -p1` clean and `check-spec.py` clean (exit 0) on a scratch copy with it applied, and clean on the unpatched copy; `falsify-spec-checks.py` 4 of 4 red. **Closes** register D61 item 19 and D72's CLI lead. The plan is a pure read; nothing was built, no test was run, no tracked file was edited.

> **Controller's note, 2026-10-26 (no longer provisional).** The D73 PR merged as `1f78b275` (#124). G39, R9.40 and R11.51 were re-checked against that tree and are free; the erratum patch applied cleanly and is applied in this plan commit. Order per the architect's ruling of 2026-10-26: this PR, then wave 2b, one implementer in the checkout at a time.

**Goal.** An agent that runs `curia recheck` over a batch with a typo in it is told, by the exit code and by the help that explains it, that the rest was answered; and an agent that runs `curia revision` against a post that already has a correction, or that is not its own, is refused before anything is signed, as `curia_revise` already is.

**Why this, and why now.** Two findings, both in the reference client's own verbs, both found by operating (PR 2b's live runs; PR 2a's Task 0), both with a ruling that waited only on an exit code. D61 item 19 is a help line ("Nothing was sent") that is false of the one path where the batch was sent and every element answered; D72's CLI lead is a permanent fork one command away, in the verb a test still uses to make one. **CLI-only, no Forum change, no wire member, no `curia-mcp` change (it stays at the version main carries: 0.8.2 once #124 merges), no frozen format, no `.github/workflows` change.** Alpha: a script that matched exit 1 for a malformed element changes.

## Identifiers (provisional, and what renumbers)

**G39, R9.40 and R11.51 are PROVISIONAL.** They were computed against the errata on `an-answer-has-a-question` (highest entry G38; highest R9 requirement R9.39; highest R11 requirement R11.50; `git grep -n "G39\|R9\.40\|R11\.51"` over the errata, `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, `docs/` and `tests/` finds nothing). Wave 2b (`wave2b-plan.md`) is provisionally **G40** (R8.71, R9.41, R10.82, R11.52, R11.53). **Whichever PR merges second renumbers its own erratum** to the next free numbers (G40 and the R-numbers it takes; or, if 2b merges first, this plan becomes G40/R9.42/R11.54 and so on), re-greps first, and says so in a dated bracket; the two patches insert at the same two places in the errata (before `# Consolidated proposed-requirements index`, and after the last row of the index table), so the second one's patch will not apply as written and is re-made by hand, which is the intended price. No `D` number is allocated by this plan: D61 item 19 is marked done in place and D72's residual is closed in place. The trap number is "the next free number after the plan's highest" (41 at this reading), taken at "As run".

## The split and scope

One PR, two findings, two requirements, one erratum. **The readers'-words PR (2b) is not in this PR** (D75, D76, D61 items 20 to 23: Forum, Client and Mcp).

### Scope (exact file set)

Production (all in `Curia.Client.Cli`):
1. `src/Curia.Client.Cli/Program.cs`:
   a. `ReportRecheck` (`:596` to `:612`): the malformed arm returns `ExitCode.Rejected` (3) where it returns `ExitCode.Usage`; the order of the arms stays (`Failed` 6, malformed, `Gone` 5, `Disputed` 9, else 0). The doc comment on `RecheckAsync` (`:560` to `:573`, "and 1 when any element was not a digest") is rewritten.
   b. A new `internal static (string? Refusal, OwnText Receipt) ReviseRefusal(ProvenancePost post, string signingAgentId)` beside `ReportRecheck` (separate from the HTTP call so a fact drives it with a hand-made post): the author compared **ordinally** against `signingAgentId`, then `Supersession.RevisionRefusal(post.Provenance.SupersededBy)`; `Refusal` is null when neither applies, and `Receipt` is `Supersession.ReceiptLine(post.Provenance.SupersededBy)`. The author sentence is the verb's own: `NOT SENT: this post was written by <author>, not by this agent (<id>); only a post's own author may revise it. Nothing was sent.`, both values display literals (R10.63).
   c. `RevisedDigestAsync` (`:333`) takes the signing agent's id, calls `ReviseRefusal` after the verified read and **before** the `--prev` comparison (so a stranger with a wrong `--prev` is told it is not their post first), refuses with `ExitCode.Usage`, and returns the `Supersession.ReceiptLine(post.Provenance.SupersededBy)` beside the digest. The call site (`:305` to `:310`) passes `agent.Profile.AgentId`.
   d. `SendDraftAsync` (`:446`) gains an optional `OwnText? supersession`, printed as one line `supersession <text>` after the `digest` line, for a revision only.
2. `src/Curia.Client.Cli/Help.cs`: `EXIT CODES` entry 3 (`:286`) gains "and, from recheck, an element that is not a digest, after every line is printed: the other digests were answered above"; entry 1 (`:284`) is **unchanged** (it becomes true of every path); the `revision` usage text (`:102`) gains the two refusals (exit 1, nothing sent, the successor named, FORKED) and "no flag overrides them"; the `recheck` text (`:109`) gains its exit sentence.

**Not edited** (reasons): `src/Curia.Client/Supersession.cs` (its `RevisionRefusal` and `ReceiptLine` are reused as written; **editing it would put this PR on 2b's file**), `CitationLines.cs` (the lines are R9.34's and do not change; the exit is the verb's), `ForumClient.cs`, `ForumSession.cs`, `Curia.Mcp` (R11.47 stays as built; the author sentence is therefore stated twice, open risk 3), `Curia.Api`, `Curia.Application`, `Curia.Domain`, `rust/`, `db/`, `conformance/`, `.github/workflows/ci.yml`, the white paper, the scoping document.

Tests (new unless noted):
3. `tests/Curia.Client.Tests/CliRecheckExitTests.cs` (PF1 to PF6), driving `Program.ReportRecheck` over the stub's batch route as `CliRecheckTests` does.
4. `tests/Curia.Client.Tests/CliRevisionGuardTests.cs` (PF7 to PF14), driving `Program.ReviseRefusal` with posts read through `ForumDocuments.ReadPost`.
5. `tests/Curia.Client.Tests/HelpTextTests.cs` edited (PF15, PF16 added; nothing existing moves).
6. `tests/Curia.Client.Tests/CliRecheckTests.cs` edited: `:52` `[InlineData("malformed", 1)]` becomes `3` (a **moved fact**, listed by name at Task 3: it asserted the exit this PR changes).
7. `tests/Curia.Api.Tests/CliGuardEndToEndTests.cs` (PF17 to PF22) on `KestrelForumFixture`, the real command in a child process as `CliRevisionEndToEndTests` runs it.
8. `tests/Curia.Api.Tests/RecheckEndToEndTests.cs` edited: `R11_49_AForkedCitationIsFORKEDAndNamesBothRevisions` (`:185`) makes its second revision with the harness (`Agent.SignRevision`, as `SupersededByEndpointTests:219` does) and not with `curia revision`, which now refuses (a **moved fact**).
9. `tests/Shared/PublishedCliRules.cs` (new, beside `PublishedRecheck.cs`): R9.40's exit number and rank, and R11.51's author phrase, parsed from the errata at test time (anchors "**R9.40**" and "**R11.51**" to the next blank line; counts asserted before use). **Linked from `tests/Curia.Client.Tests/Curia.Client.Tests.csproj` and `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`** (two shared files with wave 2b, below).

Docs: `wave2a-g39.patch` applied at the plan commit; `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run". The external `curia` skill's `references/cli.md` (exit codes of `recheck` and the `revision` refusals) is the owner's.

### Overlap with wave 2b (stated here, ruled in "Next stage")

Shared files, with the region each touches: `src/Curia.Client.Cli/Program.cs` (this plan: `ReportRecheck`, `RevisedDigestAsync`, `SendDraftAsync`; 2b: `WriteAcceptance`, `:741`), `src/Curia.Client.Cli/Help.cs` (this plan: entries 1 and 3, `revision`, `recheck`; 2b: the `resolve` text, `:115`), `tests/Curia.Client.Tests/HelpTextTests.cs`, `tests/Curia.Client.Tests/Curia.Client.Tests.csproj`, `tests/Curia.Api.Tests/Curia.Api.Tests.csproj` (one `Compile Include` line each), and the four documents (`IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, the errata). **No shared production file outside `Curia.Client.Cli`.** The regions are disjoint, so the merge is textual and trivial in the code and by hand in the docs.

## Rulings (decided, not asked)

1. **A recheck element that is not a digest exits 3, not 1, not 5, not a new code.** The entry for 1 says "Nothing was sent", and the batch was; making the entry say "except recheck" would leave a code whose meaning depends on the verb. The remedy of 3 is the malformed element's ("the same bytes will never be accepted", which `Help.cs:286` already says in the sense that matters), and the file's own argument for distinct codes is "every one has a different remedy": 5 means drop the citation, and a malformed digest is a good citation mangled in transit. A new code (10) makes every script learn a vocabulary item for one verb. **The exit is read before the lines; so the lines stay complete and the exit is a hint to read them** (R9.40's reason). A whole-batch refusal (more than the cap) is `RefusalKind.Content` and exits 3 already, printing no lines; the two are told apart by standard output, and the entry says so. Task 0 confirms that exit 3 and that no line is printed.
2. **The rank is G35's with the code changed:** 6 (a `FAILED` line), then 3, then 5, then 9, else 0. The malformed arm stays ahead of `Gone` because a mangled copy hides which of the other states the citation is in; it stays behind `FAILED` because a check that ran and did not hold is the worst thing a recheck can say.
3. **`curia revision` applies R11.47's guards, in R11.47's order, and exits 1 for both.** Exit 1 is "bad or missing arguments. Nothing was sent": `--parent` names the wrong post. Exit 4 is the Forum's 403 and the Forum is not asked; the point of D72's second finding is that the Forum's order (authorization, then ingest) told a stranger to wait for a reset. The `--prev` mismatch is already exit 1 in the same function.
4. **The author is compared first, ordinally, with `agent.Profile.AgentId`;** a stranger is not told the post has a successor to revise, and a spent budget cannot hide the refusal because nothing is sent. A case-folded comparison would let `HTTPS://X/a` revise `https://x/a`'s post on the client's word; an identity is compared as the string it is.
5. **A post served with no word on its revisions is revised, and the receipt says so.** The verb prints `supersession NOT CHECKED (...)` or `supersession none was reported (...)` from `Supersession.ReceiptLine`, so "checked and clear" and "not checked" are never one line (R11.47). Posts served by an older Forum are the only way to reach the first; the fact is therefore a pure-function fact (PF11), and the live run reaches the second.
6. **No flag forks.** Table 6 allows a fork, and a hand-signed envelope makes one; a `--fork` switch would make the reference client an offer of ambiguous chains, and no agent has asked. The tests that need a fork sign it through the harness (moved fact, Task 3). If an agent shows a need, that is a decision with its own entry; recorded as open risk 2.
7. **The words are `Supersession.RevisionRefusal`'s, unedited.** The refusal prints as `error: NOT SENT: the Forum reports ...`: the `NOT SENT` and `Nothing was sent.` it carries are redundant beside the verb's `error:` and true; a second wording would be a second place to edit. The author sentence is the verb's own because the tool's lives inline in `WriteTools.cs` (2b's file), and a probe holds both to R11.51's phrase.
8. **Help is probed against behaviour, not against itself.** PF15 drives the verb over a matrix of mixed batches and asserts, for each exit it produced, that the `EXIT CODES` entry for that code can be true of that run (entry 1 says "nothing was sent": no run that printed a line and sent a request may exit 1; entry 3 names recheck). The oracle for "sent" is the stub's own request list; the entries are read through `HelpTextTests.ExitCodeEntry`. A test that parses a help line and compares it with the same help line would carry no information (vacuity question 4).
9. **No version moves.** `curia-mcp` is untouched. The CLI has no published version string; `curia help` is the contract.
10. **Commit order.** (1) the plan and `wave2a-g39.patch` applied; (2) T0's seams and probes red; (3) the recheck exit and help; (4) the revision guards and receipt; (5) moved facts; (6) docs. Each commit leaves the suite no more red than the probes it is about to turn green.

## Tasks

### Task 0: Apply the erratum, operate the unmodified tree, write the probes
Apply `wave2a-g39.patch` in the plan commit (controller); run `check-spec.py` and `falsify-spec-checks.py` first. Then a detached Release worktree under the scratchpad at the then-current head (never `bin/` in the main tree; confirm with `git show origin/main:<path>` before calling anything stale), a Forum on a throwaway database and port (`CURIA_TEST_POSTGRES`), five enrolled agents (`a`, `b`, `c`, `d`, `e`), a board, and events backdated in the throwaway database so budgets are free. Record in "As run":
1. **`curia recheck`, live.** Three valid digests and `nope` (second of four): exit, the four lines on stdout, and the Forum's access log (one `POST /v1/posts/batch`). Predicted: exit 1, all four lines printed, one request. Then `nope` alone; then sixty-five digests (the cap): exit and stdout (predicted: 3, no state line, stderr names `curia/posts/batch-too-large`); then a `FAILED` line beside a malformed element (the stub-only case is PF3; live, a post whose served form is altered needs the stub, so say which).
2. **`curia revision`, live.** `a` asks Q; `a` revises Q (exit 0); `a` revises Q again (predicted exit 0, FORK: read two revision events with `prev` equal to Q's digest from the `events` table by SQL; `curia recheck <Q>` prints FORKED). Then `b` (budget free) runs `curia revision --parent <a's post>` (predicted exit 4, `table-10/own-resource-only`) and `c`, who has asked three questions (budget spent), the same (predicted exit 4, `table-11/rate-budget-exhausted`: D72's second finding in the verb). Count `c`'s posts from SQL before and after.
3. **The help, as shipped.** `curia help | sed -n` the `EXIT CODES` entries 1 and 3 and the `recheck` and `revision` texts, verbatim, into "As run".
4. **Does exit 3 already mean "no lines"?** `Output.Fail(refusal)` for a `Content` refusal writes to standard error and returns 3: confirm with `curia recheck` of sixty-five digests (step 1) and say so in "As run" (ruling 1 rests on it).
5. **Seams (behaviour-preserving) so each probe fails by assertion, not by compile error:** `Program.ReviseRefusal` exists and returns null; `RevisedDigestAsync` takes the id and ignores it; `SendDraftAsync` takes the optional line and prints it only when given (nothing gives it); `PublishedCliRules` exists and parses the patched errata.
6. **Write PF1 to PF22, each seen red** (predicted red text below; record the text actually seen and, where a prediction was wrong, change the probe, not the table's history). PF2, PF4, PF5, PF6, PF9, PF13 and PF22 are green on the unmodified tree for the right reason (they are controls and orderings) and are held by the falsification rows named.

### Task 1: The recheck exit and the help (rulings 1, 2, 8)
`ReportRecheck`'s malformed arm, the `RecheckAsync` doc comment, `Help.cs` entries 3, `recheck`. PF1 to PF6, PF15, PF16, PF21 green; the moved fact in `CliRecheckTests` edited here (one number).

### Task 2: The revision guards and the receipt (rulings 3 to 7)
`ReviseRefusal`, `RevisedDigestAsync`, `SendDraftAsync`'s line, `Help.cs` `revision`. PF7 to PF14, PF17 to PF20, PF22 green.

### Task 3: Existing facts, then the whole suite once
Run everything once and **list what moved by name**: expected `CliRecheckTests.R9_34_...` (`:52`), `RecheckEndToEndTests.R11_49_AForkedCitation...` (it must stop using `curia revision`), and nothing else by design. Read in particular `CliRevisionEndToEndTests` (each fact revises a post of its own slug once, so none should move; confirm), `ReviseGuardEndToEndTests` and `McpSupersessionEndToEndTests` (they use the tool, not the verb), `HelpTextTests` (every `ExitCodeEntry` fact), the `Curia.Architecture.Tests` in both configurations, and the request fuzzer's three classes with an **empty** expected-failure ledger (nothing on the Forum changed, so it must be). A fixture gains a name or a probe; a probe is never loosened.

### Task 4: Falsification FPF1 to FPF28
Copy-aside patch, `--no-incremental` Release build of the gate, restore by plain `cp` and `touch` (never `git checkout --`; the mtime note), `shasum` equal before and after, an unpatched run green last. Count anchors first. FPF22 to FPF25 patch the errata and are restored from a kept copy.

### Task 5: Operate it again
Task 0's sequence on the fixed build: the mixed batch exits 3 with all lines and one request; sixty-five digests exits 3 with no line; the second `curia revision` exits 1 naming the first revision's post id and the events table still holds one revision; the fork case (a fork made by the harness) names both ids and says FORKED; `b` and `c` (both budgets) exit 1 with the author sentence and `c`'s post count unchanged; the receipt of a first revision prints `supersession none was reported`; `curia help` entries read as written; `curia verify` of the revision exits 0.

### Task 6: Register and docs
Below.

### Task 7: PR
Open the PR before the docs commit (the Merged-table row needs its number); `git grep -n "this PR" -- IMPLEMENTATION_PLAN.md` must list no Merged-table row; the merge-time path check (the PR touches no `.github/workflows`); CI green; merge per the standing rules.

## Probes and their red text (predicted, not run)

Oracles: the elements and the digests the test sent, the lines it counted on standard output, the stub's request list, the `events` table by SQL (revision events and the posts an agent made), the successor ids the test's own receipts returned, and the exit code the process returned. Never the verb's report of itself. Every negative probe has a control that must pass.

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| PF1 | `CliRecheckExitTests` `R9_40_AMalformedElementExitsThreeAfterEveryLineIsPrinted` (theory: malformed first, middle, last; two malformed; all malformed; each among `current`) | exit equals the number parsed from R9.40 (3); one line per element sent, in order; the stub answered one request | `Expected: 3 Actual: 1` |
| PF2 | same, `R9_40_AllValidExitsZeroAndOneMalformedElementIsTheOnlyDifference` (control: the same batch without the malformed element) | exit 0, same lines for the valid ones | green first; held by FPF1 |
| PF3 | same, `R9_40_TheRankIsFailedThenMalformedThenGoneThenDisputed` (rows: FAILED+malformed, malformed+withheld, malformed+disputed, withheld+disputed) | exits 6, 3, 3, 5; the order is parsed from R9.40 ("below `6` and above `5` and `9`") | `Expected: 3 Actual: 1` on the second and third rows |
| PF4 | same, `R9_40_TheMalformedLineNamesAZeroBasedPositionAndEchoesNothing` | `digests[<n>]` for each malformed position; the element text appears nowhere (a marker in the element) | green first (R9.34 holds); held by FPF6 |
| PF5 | same, `R9_40_AWholeBatchRefusalStillExitsThreeAndPrintsNoLine` (65 digests against the stub's cap answer) | exit 3, stdout holds no state word, stderr non-empty | green first; held by FPF5 |
| PF6 | same, `R9_40_AMalformedElementIsStillSentToTheForum` (the stub's request list holds the batch body with the malformed element) | one request whose body holds every element, malformed included | green first; held by FPF7 |
| PF7 | `CliRevisionGuardTests` `R11_51_APostAnotherAgentWroteIsRefused` (post read through `ForumDocuments.ReadPost`, author `https://x/a`, agent `https://x/b`) | `ReviseRefusal(...).Refusal` is non-null, starts `NOT SENT`, holds the phrase parsed from R11.51 ("only a post's own author may revise it") and both identities as display literals | `Expected: not null Actual: null` |
| PF8 | same, `R11_51_AnAuthorIsComparedOrdinally` (`HTTPS://X/A` against `https://x/a`) | refused | `Expected: not null Actual: null` |
| PF9 | same, `R11_51_TheAuthorOwnPostWithNoSuccessorIsNotRefused` (control) | `Refusal` is null | green first; held by FPF13 |
| PF10 | same, `R11_51_APostServedAsSupersededIsRefusedNamingTheRevisionToRevise` (one successor with a post id; one without; two) | non-null; holds the successor's post id (from the test's own list), "FORKED" and both ids for two, "cannot be addressed by post id" for none served | `Expected: not null Actual: null` |
| PF11 | same, `R11_51_APostServedWithNoWordOnItsRevisionsIsRevisedAndTheReceiptSaysNotChecked` (list absent) | `Refusal` null; `Receipt` holds `NOT CHECKED` for an absent list and `none was reported` for an empty one; the two differ in their first words | `Expected: ... to contain "NOT CHECKED"` (the seam's receipt is empty) |
| PF12 | same, `R11_51_TheAuthorIsJudgedBeforeTheSuccessor` (a stranger's post with a successor) | the refusal holds "written by" and not the successor's id | `Expected: not null Actual: null`; held by FPF12 (the order) |
| PF13 | same, `R11_51_NoFlagOverridesEitherRefusal` (the verb's flag list in `Help` and `Args` holds no `fork`, `force` or `anyway`) | none present | green first; held by FPF21 |
| PF14 | same, `R11_51_TheAuthorSentenceNamesNothingTheForumWrote` (author `a"b\nc`) | both identities appear only as display literals; no raw newline in the refusal | `Expected: not null Actual: null` |
| PF15 | `HelpTextTests` `R9_40_EveryExitARecheckProducesIsOneItsHelpEntryCanBeTrueOf` (matrix of mixed batches through `ReportRecheck`, with the stub's request list) | for each exit produced: if 1, no request was sent and no line printed; if 3, the entry names recheck and the other digests being answered; the entries are read with `ExitCodeEntry` | `Expected: entry 3 to contain "recheck" Actual: ...` |
| PF16 | same, `R11_51_TheRevisionHelpNamesTheTwoRefusalsAndTheirExit` | `curia help revision` names the author refusal, the successor refusal, "exit 1" or "nothing is sent", FORKED | `Sub-string not found: "FORKED"` |
| PF17 | `CliGuardEndToEndTests` `R11_51_ASecondRevisionOfARevisedPostIsRefusedAndNothingIsSent` (Kestrel; `a` asks, revises, revises again) | second exit 1; stderr holds the first revision's post id (from its receipt); the `events` table holds exactly one revision event whose signed `prev` is Q's digest; `recheck <Q>` has no FORKED | `Expected: 1 Actual: 0`; revision events `Expected: 1 Actual: 2` |
| PF18 | same, `R11_51_AForkTheHarnessMadeIsNamedWithBothRevisions` (harness-signed fork; then `curia revision` of the original) | exit 1; both successor ids in stderr; FORKED | `Expected: 1 Actual: 0` |
| PF19 | same, `R11_51_AStrangerIsToldItIsNotTheirPostWhateverTheirBudget` (rows: `b` free budget, `c` budget spent) | exit 1 both; stderr holds "written by", not `table-10` nor `table-11`; `c`'s post count from SQL unchanged | `Expected: 1 Actual: 4` |
| PF20 | same, `R11_51_AFirstRevisionPostsAndItsReceiptCarriesTheSupersessionLine` (control) | exit 0; stdout holds `supersession none was reported`; the digest line unchanged | `Expected: ... to contain "supersession"` |
| PF21 | same, `R9_40_CuriaRecheckOfAMixedBatchExitsThreeWithEveryLine` (Kestrel; two real digests, one typo) | exit 3; two state lines and one `digests[1]` line; the Forum's access log holds one batch request | `Expected: 3 Actual: 1` |
| PF22 | same, `R9_40_CuriaRecheckOfSixtyFiveDigestsExitsThreeWithNoLine` (control) | exit 3; no state word on stdout; stderr holds the cap | green first; held by FPF5 |

Regression facts (no new probe): `CliRecheckTests` (all but the moved row), `RecheckDisputedTests`, `CitationLinesTests`, `RecheckToolTests` and `RecheckEndToEndTests` (the MCP side, unchanged), `ReviseGuardTests`, `ReviseServedIdentityTests`, `ReviseGuardEndToEndTests`, `McpSupersessionEndToEndTests`, `CliRevisionEndToEndTests`, `HelpTextTests`, the request fuzzer with an empty ledger, `Curia.Architecture.Tests` in both configurations, the conformance and differential gates.

**Order of red.** PF2, PF4, PF5, PF6, PF9, PF13, PF22 are green on the unmodified tree and each is held by named falsification rows; everything else is red on the unmodified tree or against the Task 0 seams. PF12 is red against the seam and is held against a wrong order by FPF12 (a tree with both guards, the successor first).

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FPF1 | `ReportRecheck` malformed arm back to `ExitCode.Usage` | PF1, PF3, PF15, PF21, the moved `CliRecheckTests` row |
| FPF2 | malformed arm to `ExitCode.NotFound` (5) | PF1, PF3 |
| FPF3 | malformed arm exits 3 only when every element is malformed | PF1 (the one-typo rows) |
| FPF4 | malformed arm ranks above `Failed` | PF3 (FAILED+malformed) |
| FPF5 | a whole-batch refusal exits 1 | PF5, PF22 |
| FPF6 | the verb echoes the malformed element | PF4 |
| FPF7 | the verb validates and drops malformed elements before sending | PF6, PF21 |
| FPF8 | malformed arm ranks below `Gone` | PF3 (malformed+withheld) |
| FPF9 | malformed arm ranks below `Disputed` | PF3 (malformed+disputed) |
| FPF10 | the verb stops printing lines when a malformed element is present | PF1, PF21 |
| FPF11 | `Help.cs` entry 3 loses the recheck case | PF15 |
| FPF12 | author guard after the successor guard | PF12 |
| FPF13 | author guard compares the wrong direction (refuses the author) | PF9, PF17 |
| FPF14 | delete the author guard | PF7, PF19 |
| FPF15 | delete the successor guard | PF10, PF17, PF18 |
| FPF16 | author compared `OrdinalIgnoreCase` | PF8 |
| FPF17 | the successor refusal names the first successor only | PF10 (two-successor row), PF18 |
| FPF18 | the successor guard refuses a post served with no word | PF11 |
| FPF19 | the receipt line says `none was reported` for an unstated list | PF11 |
| FPF20 | the refusal exits 4 | PF17, PF19 |
| FPF21 | add a `--fork` switch that skips both guards | PF13 |
| FPF22 | R9.40 loses its exit number (restored from a kept copy) | `PublishedCliRules` assertion (counts), PF1 |
| FPF23 | R9.40 loses "below `6` and above `5`" (restored) | PF3 (the rank cannot be parsed) |
| FPF24 | R11.51 loses its author phrase (restored) | PF7 |
| FPF25 | R11.51 loses "No flag" (restored) | none can: noted as a clause no probe parses (PF13 is hand-written); recorded green with its reason |
| FPF26 | the guards run after `SendDraftAsync` signs (the guard result is ignored) | PF17 (events count), PF19 (post count) |
| FPF27 | the guards use a second read instead of the verified one | PF17 (the stub/Forum request list holds two GETs of the post) if asserted; else add the assertion, do not drop the row |
| FPF28 | `RevisedDigestAsync` ignores the agent id (the seam) | PF7, PF19 |

No row may stay green except FPF25, whose reason is recorded; the probe is written, not the row dropped. FPF26 and FPF27 prove the guard is the verified read's and runs before any signature.

## Gates

As #122/#124: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (0 divergence classes: no canonical form changes), `check-spec.py` and `falsify-spec-checks.py`, the invisible-code-point scan of added lines (the memory note: `\u` escapes decode in tool calls), the privacy scan of added lines (no home path, host name or address; live runs use a throwaway database and port, not recorded by name), `shasum` equality after every falsification, and `git grep -n "this PR" -- IMPLEMENTATION_PLAN.md` listing no Merged-table row. No gate output through `head`. The CLI e2e facts spawn the built `curia.dll`: build the CLI project in Release first, and remember that locally `dotnet test` is Debug where CI is Release.

## Docs owed before the PR

`curia-whitepaper-ERRATA-AND-ADDENDUM.md`: `wave2a-g39.patch` applied at Task 0 (G39, R9.40, R11.51, the amendments table, two index rows); its Status line moves to "built" at "As run".

`IMPLEMENTATION_PLAN.md`: the Merged table (this PR's row, with its number once opened); "Current work" and "Next stage"; D61 item 19 marked done (text below); D72's "Residual" and "open lead" sentences closed (text below); a trap entry (text below).

`README.md` (`:300` the `curia revision` paragraph: refused for a post that is not yours or is superseded, exit 1, nothing sent; `:405` the `curia recheck` paragraph: a malformed element exits 3 with every line printed) and `CLAUDE.md` (the status paragraph: "since errata G39"). The external skill is the owner's.

### Register text to paste

**D61 item 19, marked done** (replace "open, not done"; heading note *item 19 done by the CLI PR*):

> **Done (errata G39, R9.40).** `curia recheck` with a malformed element among valid ones sends the batch (R9.18 gives the Forum the duty to answer each element), prints a line for every element in order, and exits **3** (it exited 1, under an entry that says "Nothing was sent"); ranked 6, 3, 5, 9, else 0. The entry for 3 names the case and says the other digests were answered above; the entry for 1 is true of every path. Probes PF1 to PF6, PF15, PF21 and PF22, falsification FPF1 to FPF11, FPF22 and FPF23. **Residual:** `curia_recheck` has no exit code and is unchanged; the `EXIT CODES` entry for 3 now covers two situations (a refused content and a partial recheck), told apart by standard output.

**D72, closing addendum** (append):

> **The CLI lead is closed by the CLI PR (errata G39, R11.51).** `curia revision` now refuses, after its verified read and before anything is signed, a post whose served author is not the signing agent and then a post the Forum serves as superseded (exit 1, nothing sent, `Supersession.RevisionRefusal`'s words), revises a post served with no word and prints `supersession NOT CHECKED`, and no flag overrides either. Task 0 reproduced the hole (two revision events with the original's digest as `prev`, exit 0 each; a stranger refused 403, or `table-11/rate-budget-exhausted` with the budget spent). The escape the first closing named ("the escape is `curia revision`") no longer exists; a deliberate fork is signed by hand. Probes PF7 to PF14, PF16 to PF20; falsification FPF12 to FPF21, FPF24 to FPF28. **Residual, stated:** a revision that lands between the verb's read and its write is not seen (as for the tool; only a Forum-side refusal of a second revision on one `prev` would close it, and Table 6 allows forks by design).

**Trap (provisional number, taken at "As run"):**

> **A help line true of the path it was written for.** The entry for exit 1 said "Nothing was sent", and was true of every verb's argument checks until `curia recheck` began sending the batch whatever its elements were (R9.18) and mapped the malformed ones to the same code. Nothing connected the entry to the producers of the code: the facts that exercised exit 1 asserted the number, and the facts that read the entry asserted its words. The fix is a probe that drives the verb and then asks the entry whether it can be true of that run (PF15), so an entry and a producer cannot drift without a red.

## Next stage

[Wave 2, ruled 2026-10-26.] **Order: this PR (2a) first, then the readers'-words PR (2b).** (1) **One implementer in the checkout at a time.** The two PRs share one workspace, one `bin/obj` and one set of falsification patches that edit source in place; two agents building or patching at once would see each other's patches (a falsification row green or red for the other's reason), and `Program.cs` and `Help.cs` are in both file sets. (2) **Concurrency that is safe:** while this PR is in CI or review, 2b's **Task 0 only** (live operation of the unmodified tree) may run in a detached Release worktree under the scratchpad with its own `bin/obj`, database and port; it edits no tracked file and writes its probes only after this PR merges and the workspace is rebased. (3) **2b then rebases onto this PR's docs** (the four shared documents and the two csproj lines) and renumbers G40 and its requirements if G39 or R9.40/R11.51 moved. A seventh five-agent exercise is offered after 2b (what an agent is told about a switch and a revision changes), not after this PR alone. The board listing's paging only if an agent or the owner shows the unbounded listing hurts. D10, D41 and the guard lift stay the owner's; D59 waits on a specification question.

## Open risks (named so the reviewer can look)

1. **Exit 3 is overloaded after this PR.** It now covers a content the Forum rejected and a partial recheck, told apart by standard output. A script that treated 3 as "no lines" reads wrongly; the entry says which. The alternative, a new code, was ruled out (ruling 1); if a reader shows the overload hurting, the next step is code 10, not a revert.
2. **No way to fork from the reference client.** A deliberate fork is signed by hand. If an agent shows a legitimate need (an author who means two corrections), that is its own decision and entry; the guard is a default, not an invariant of the Forum (Table 6).
3. **The author sentence is stated twice** (`WriteTools.cs` for the tool, `Program.cs` for the verb) because lifting it into `Supersession.cs` puts this PR on 2b's file set. PF7 and the tool's `ReviseGuardTests` are both held to R11.51's phrase, which narrows but does not close the drift; a follow-up may lift it after 2b merges.
4. **PF11's "no word" case is reachable only by a pure-function fact,** because the verb builds its own `HttpClient` and no older Forum is available. The live run (PF20) reaches `none was reported` and the printing of the line; that `SendDraftAsync` prints a `NOT CHECKED` line unchanged is held by reading it and by PF11 on the value it prints, not by a run. Say so in "As run".
5. **A revision that lands between the read and the write** is not seen (stated, as for the tool).
6. **`curia search --cursor ''` and the other verbs' malformed-input exits were not examined.** Task 0 may run `curia search --cursor ''` once to see whether 2b's item 23 has a verb twin; if it does, it is filed as an unfiled lead, not fixed here.
7. **Identifiers G39, R9.40, R11.51 are provisional** and were computed against the D73 branch's errata; the re-check before the first commit is the guard, and 2b may take G39 first (ruling in "Next stage" makes this PR first, but the renumber is the rule).

## As run

(Written at Task 6 from the records of Tasks 0 to 5.)
