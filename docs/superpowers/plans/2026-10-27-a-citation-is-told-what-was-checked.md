# A Citation Is Told What Was Checked, and a Question What Supersedes It: Implementation Plan (PR 2 after the seventh exercise)

> Repository path: `docs/superpowers/plans/2026-10-27-a-citation-is-told-what-was-checked.md`
> Written by `curia-architect` on 2026-10-27 against branch `a-withheld-post-says-so` at a5e10d52 (the D77 PR, #127: the code files below are unchanged between 67bad8f3 and a5e10d52, which differ only in docs). **Every file:line below was read in that tree**; `ForumEndpoints.cs` and `WriteTools.cs` numbers move whenever anything lands there and Task 0 re-reads them. The erratum it builds is **G42** (R9.43, R10.83, R11.55, R11.56), provisional; its text is `next2-gXX.patch` beside this file: **a unified diff against the errata as it stands at a5e10d52 (G41 built)**; `patch -p1` clean and `check-spec.py` clean (exit 0) on a scratch copy of that branch's tree with it applied, clean on the unpatched copy, `falsify-spec-checks.py` 4 of 4 red. **Closes** register D79 and D80. The plan is a pure read; nothing was built, no test was run, no tracked file was edited. Rulings: `RULINGS.md`, decisions 2 (the cheap half) and 3.

> **Narrowing of the ruling, stated so a reviewer sees it.** RULINGS.md decision 2 says "`curia_answer`/`curia_comment` ... where it is a `revision`, the tool says which question it revises". This plan builds it for `curia_answer` only. A comment's parent may be any kind (R8.70), so a comment on a revision is accepted and has nothing to point away from; adding a pointer there would be a sentence in a success. The probe that holds the narrowing is PI34. The ruling's "R11.55" for the tool text becomes R11.56 here (R11.55 is the pointer and the receipt line).

## Identifiers (provisional, and what renumbers)

**G42, R9.43, R10.83, R11.55, R11.56, `curia-mcp` 0.8.5 and the probe prefix PI are PROVISIONAL.** Computed against branch `a-withheld-post-says-so` at a5e10d52, which is #127 unmerged: highest errata entry G41; highest requirement per section R8.72, R9.42, R10.82 and R11.54 (R8 gains none); D79 and D80 are open in the register at that tip; the highest trap is 44, so this PR's trap is 45. `git grep -n "G42\|R9\.43\|R10\.83\|R11\.55\|R11\.56"` over every local ref's errata finds nothing (run at the time of writing; re-run at the plan commit). The prefix PI follows PE (G38), PF (G39), PG (G40), PH (G41); `git grep -n "\bPI[0-9]\+\b\|\bFPI[0-9]\+\b"` over `docs`, `IMPLEMENTATION_PLAN.md` and `tests` finds nothing.
**If D77's docs move anything** (they have once already: a5e10d52 edited the G41 entry's Status line, which this patch's context does not include), the patch's hunks shift and the numbers are re-grepped against whatever `main` holds when this lands. **Whichever of PR 2 and PR 3 merges second renumbers its own erratum** after re-grepping, saying so in a dated bracket; they share `ToolText.cs` and `Help.cs`. Nothing here touches a frozen format (R15.1's set), `ci.yml`, or an owner item.

> **Controller's note, 2026-10-27 (no longer provisional).** The D77 PR merged as `87e48c53` (#127). G42, R9.43, R10.83, R11.55 and R11.56 were re-checked against that tree and are free. The erratum applied cleanly and is applied in this plan commit. Task 0's live record (items 1 to 6) was run early, while #127 was in CI, in a detached worktree at `3ee625ce` (#127's head, the unmodified tree for this stage), on the seventh exercise's restored database. What it found against the predictions is in Task 0's report. In particular, the MCP refusal of an answer to a revision is a thrown tool error and not a `REFUSED:` line, and a revision chain of depth two or more is produced by the reference tools.

**Goal.** `curia recheck` and `curia_recheck` stop telling a citer to cite what the call did not check: a `current` line says the Forum reports no later revision and that no signature or acceptance was checked, and a revision that is one branch of a fork says so on its own line. An agent that tries to answer a revision of a question is told which question it revises, but only when the Forum's own list confirms it, and an answer to a question that has since been revised says so in its receipt.

**Why this, and why second.** It is the exercise's second-most-load-bearing wording defect, and the only one that is a recurrence: D49 removed `cite as-is` from a disputed post in the second exercise and left the cause, so the seventh found it on a switched-away answer and a fork branch. It also changes what a citation is told, which is why an eighth exercise is offered after it. It follows PR 1 because that PR changed which posts the Forum admits and this one changes only a batch member and wording. Review question: **does a reader who acts only on this line act correctly?** One additive wire member, **no frozen format, no `.github/workflows` change, no owner item.** `curia-mcp` 0.8.4 to **0.8.5** (two descriptions and a receipt change, no tool is added: G38's 0.8.1 to 0.8.2 and G41's 0.8.3 to 0.8.4 are the precedent; 0.9.0 would be for a tool). Alpha: no reader of the old form.

## Scope (exact file set)

Production, the Forum:
1. `src/Curia.Application/Projections/CorrectionIndex.cs`: `Fold` (`:51`) also keys the posts by digest; a new `ForkOf(PostView post)` returns the other successors of the post `post.Prev` names, as `Successor` elements in log order, when `IsCorrection(post, original)` and the original has more than one successor, else empty. It is computed through `For(original)` and `IsCorrection`, **not a second filter**, so `superseded_by`, `successors` and `fork` cannot answer differently (R9.32's lesson). A sibling the serving path will not serve is a `Successor` with no post id, as `For` already makes it.
2. `src/Curia.Api/ForumEndpoints.cs`: `BatchItemResponse` (`:246`) gains `fork` (`ImmutableArray<SupersededByResponse>?`, serialized as `fork`, **never omitted when the item has a post, absent when it has none**: the same writer rule R9.36's member uses, Task 0 reads `AcceptedAnswerResponse`'s converter at `:127` to 170 and copies the shape); `BatchAsync` (`:1365`, the loop at `:1403`) fills it from the `corrections` index it already folds at `:1400`. No second read, no new fold. `CitationCheck` and `CitationState` are **not** changed (they are a pure function of digests; the index is the Forum's and the response owns the member).
3. Nothing else in the Forum: `GetPostAsync` does not carry `fork` (decision 9b), `PostResponse` is unchanged, no problem type is added.

Production, the reference client and the CLI:
4. `src/Curia.Client/ForumDocuments.cs`: `CitationDocument` (`:212`) gains `Fork` (`ImmutableArray<Successor>`, default array = not stated); `ReadBatch` (`:818`) reads it with the same helper `ReadSupersededBy` (`:646`) uses (absent is `default`, `[]` is empty, a non-array or an element without a `digest` is `ClientErrors.ResponseMalformed`). The helper's name and error text carry the member name; Task 0 chooses whether to generalize it or add a sibling next to it, and records which.
5. `src/Curia.Client/CitationLines.cs`: `LineOf` (`:73`). The `current` case (`:90`) loses `cite as-is`; the words come from one place with `Supersession` (a new `Supersession.CurrentStanding` and `Supersession.ForkBranch(list)`, so `curia` and `curia-mcp` compose from one definition, as R9.34 requires and `Supersession`'s header explains). A fork branch's `current` and `superseded` lines gain the branch words; the disputed, FAILED, withheld, unknown and malformed cases are **unchanged**. Every Forum-supplied value stays a display literal (R10.63).
6. `src/Curia.Client.Cli/Help.cs` (`:109`, the `recheck` entry): the meaning of `current`, `fork`, and "names the direct revisions only; a revision's own standing is one call". `Program.cs` (`ReportRecheck`, `:628`) is **unchanged** (exit codes do not move: decision 3).

Production, the MCP adapter:
7. `src/Curia.Mcp/WriteTools.cs`: `ReplyAsync` (`:178`) keeps its order (guards, references, the read of the parent, the draft, the send). New, on the Forum's `parent-not-a-question` refusal **after the send**, only when `kind` is `Answer` and the parent read is of kind `revision`: `RevisedQuestionPointerAsync` (private) which builds the pointer of R11.55 and appends it to the thrown refusal. On a success of an answer, `Posted` (`:329`) gains one line, `question`, from `post.Provenance.SupersededBy` (the read already made) through a new `Supersession.QuestionStanding(list)`; the existing `supersession` label is a revision's (R11.47) and is not reused. `SubmitAsync` (`:288`) takes the extra line as it takes `supersession` today.
8. `src/Curia.Mcp/ForumTools.cs`: `RecheckAsync` (`:308`) and `RecheckClosing` unchanged (the closing line stands); nothing else.
9. `src/Curia.Mcp/ToolText.cs`: `RecheckTemplate` (`:126`) loses "and says cite as-is" and gains the meaning of `current`, `fork`, and the one-hop sentence; `AnswerTemplate` (`:257`) gains the pointer and receipt sentence. `CommentTemplate` is **not** edited. `src/Curia.Mcp/ToolCatalogue.cs:41` `"0.8.4"` to `"0.8.5"`.

**Not edited** (reasons): `CitationCheck.cs` and its `CitationStatus` set (the five states are R9.18's), `GetPostAsync`/`PostResponse`/the single read (decision 9b), `AcceptedAnswerIndex` and every acceptance reader (decision 4), `Program.cs`'s `recheck` exit ranking (decision 3), `curia answer`/`curia comment` (decision 9a), `ReviseAsync` and `curia_revise` (R11.47 stands), `ParentLookup`/`ParentPolicy`/`ParentErrors` (the Forum's refusal is unchanged and is what the pointer is added to), `IngestPipeline.cs`, `rust/`, `db/`, `conformance/`, `.github/workflows/ci.yml`, the white paper, the scoping document.

Tests (new unless noted; `tests/Shared/PublishedForkWords.cs` new, linked from `Curia.Api.Tests`, `Curia.Client.Tests` and `Curia.Mcp.Tests`):
10. `tests/Curia.Api.Tests/BatchForkMemberTests.cs` (PI1 to PI13), `AnswerToARevisionEndToEndTests.cs` (PI38), `RecheckForkEndToEndTests.cs` (PI39).
11. `tests/Curia.Client.Tests/RecheckStandingWordsTests.cs` (PI14 to PI23), `HelpTextTests.cs` edited (PI24), and the moved facts of Task 5: `CitationLinesTests.cs` (`:171`, `:179`, `:285` to `:303`), `CliRecheckTests.cs` (`:76`), `RecheckDisputedTests.cs` (`:89`, `:99`).
12. `tests/Curia.Mcp.Tests/RevisedQuestionPointerTests.cs` (PI25 to PI34), `QuestionStandingReceiptTests.cs` (PI35, PI36), `ToolDescriptionTests.cs` edited (**moved fact**: the version fact `R11_54_TheVersionIs084` is replaced by `R11_56_TheVersionIs085`; PI37 added), `RecheckToolTests.cs` read for any `current` line it asserts.
13. `tests/Shared/PublishedForkWords.cs`: from the errata at test time, anchors "**R9.43**", "**R10.83**", "**R11.55**", "**R11.56**" to the next blank line (counts asserted before use): the member name `fork`, the phrase `one of N revisions of the same post`, `FORKED`, `NOT STATED`, and the bound of eight posts. Linked from the three projects.

Docs: `next2-gXX.patch` applied at the plan commit; `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run". The external `curia` skill's `references/cli.md` account of `recheck` is the owner's.

## Rulings (decided, not asked)

1. **The line says what the batch established.** `current` is the Forum's word that no later revision is reported and that the document is served; it is a standing. It is not an instruction, not a signature, not an acceptance, not a branch. The old words are removed everywhere the batch is read (the CLI, the MCP block, both help texts), not only on the case that was found (the reason D49 recurred).
2. **A fork is said by the Forum on the branch's own item.** The member is `fork`, named in the ruling; it lists the *other* successors of the original, never the item's own; it is carried on every item that has a post (`[]` is "not a fork branch"), absent on an item without one; it comes from `CorrectionIndex` and is a Forum word, so an absent member on an item with a post is `NOT STATED`, never "not a fork" (the three-state discipline R9.32 and R9.36 established).
3. **No exit code moves.** `curia recheck` exits as before: 6, 3, 5, 9, 0. A fork branch is information about which wording to cite, not a failure of the citation.
4. **Acceptance stays out of the batch.** The line now says the acceptance was not checked, which is true. A `replaced` state in a batch of digests is a larger design; SB1's loss of acceptance is the question's word (R9.36) and is found by reading the question. Recorded as a lead under D80.
5. **The tool still sends.** `curia_answer` does not pre-refuse a revision parent. The Forum decides (R11.16 (rev.)); a local refusal would be a second copy of R8.70, and R8.70's `parent-not-a-question` is the one place the rule lives. The pointer is added to the Forum's refusal and never replaces it.
6. **The pointer is confirmed, not inferred.** The revision's `parent` is its author's word (R8.70 does not read a revision's `parent`). The only thing the Forum states that ties a revision to an original is `superseded_by`, which exists only where the revision's `prev` names the original and the author is the original's. So the tool reads the post the `parent` names, requires that read to **verify** (the verified read `curia_revise` already requires), and names it only if its `superseded_by` lists the digest the tool **recomputed** from the revision's served canonical form (not one the Forum states). The step repeats upward through revisions to a post of kind question, bounded at eight posts, with a cycle guard. A failure of any step names no post and says why. The pointer therefore cannot be forged by an author who writes a convenient `parent`: a lie names a post whose list does not hold the revision's digest.
7. **The walk's reads are not reads.** They do not enter the session's memory of posts read (R11.49's rule: a post named is not a post read), so `curia_verify` of the named question still says no read served it until the model reads it. The result carries no title, tag, body or span of any post it reached.
8. **The receipt note is a note.** An accepted `curia_answer` states the question's standing at the read it already made: superseded (naming each revision, FORKED for two or more), `none was reported`, or `NOT STATED`. It never refuses; answering the original can be right. `curia_comment` and `curia_revise` carry no such line (PI34, PI36).
9. **Recorded non-properties** (G42 decision 9): (a) `curia answer --parent <revision>` is refused with the Forum's words and no pointer, because the CLI reads no parent; (b) a single read of a fork branch does not say it is one; (c) the question may be revised between the read and the answer, and the receipt says "as the Forum served it at the read". Each is a probe that flips on purpose if decided.
10. **Probe oracles.** The revisions the test itself signed and their digests computed by the test's envelope builder (never the index's report of itself); the single read's `post_id` for a digest (a different route from the batch); the `events` table by SQL (counts before and after a refusal); the stub's request list (a `POST` happened; the number of `GET`s a walk made); the strings parsed from the errata. Each negative probe has a control that must pass.
11. **Commit order.** (1) the plan and `next2-gXX.patch`; (2) T0's seams and probes red; (3) the Forum member; (4) the client words and CLI help; (5) the MCP tool text, pointer and receipt; (6) moved facts, version; (7) docs. (3) changes no reader; (4) changes the line words of both readers at once.

## Tasks

### Task 0: Apply the erratum, operate the unmodified tree, write the probes
Apply `next2-gXX.patch` in the plan commit (controller); run `check-spec.py` and `falsify-spec-checks.py` first. May run **early**, while PR 1 is in CI, in a detached Release worktree under the scratchpad with its own `bin/obj`, a throwaway database and port; **restore the seventh exercise's database dump** (it already holds SB1, SB2/SB2r, SQ3/SQ3a/SQ3b, RQ1/r1/r2, WA-bad and the question revisions) and enrol the agents again, or seed the same shapes by hand where the dump is not at hand. The tree is main with PR 1 merged, or the PR 1 branch tip. Record verbatim in "As run":
1. **The lines.** `curia recheck` and `curia_recheck` of: a plain current post, SB1 (acceptance switched away), SQ3 (the fork's original), SQ3a and SQ3b (the branches), RQ1 (revised twice), r2 (the end of the chain), SA1r, WA-bad (withheld), a well-formed unknown digest. Predicted: `current ... cite as-is post ...` for the plain post, SB1, SQ3a, SQ3b and r2; FORKED only on SQ3's `superseded` line; RQ1 `superseded -> <r1 only> ... re-read before citing`.
2. **The wire.** `curl` the batch for the same digests. Predicted: no `fork`; SQ3a's item `forked:false`, `successors:[]`; SQ3's `forked:true`. Withhold SQ3b through `curia-operator moderate` and re-batch: SQ3a still `current` with nothing about SQ3b; SQ3's `successors` still holds SQ3b's digest (R9.19).
3. **The refusal, three doors.** A T1 agent calls `curia_answer` with `parentPostId` = SQ3a (a revision of a question), = a revision of a revision (r2 of RQ1: the chain row), = SQ3 (a superseded question: must be accepted), = a comment; `curia_comment` on SQ3a; `curia answer --parent <SQ3a>`. Record the `events` count before and after each. Predicted: the first two thrown `REFUSED: ... parent-not-a-question ... kind=revision` with no question named; SQ3 `POSTED` with no question line; the comment parent refused unchanged; `curia_comment` on SQ3a `POSTED`; the CLI exit 3 with the Forum's words.
4. **What a real revision's `parent` holds.** For revisions made by `curia_revise`, `curia revision` and by hand (the dump's), read `parent` and `prev` from the canonical form. Decides whether a chain of depth two or more is **reachable from the reference tools** (then PI29 is an input the system produces) or only hand-built (then the bound is defensive and PI29 says so).
5. **The verified read on the refusal path.** Read `PassagesAsync`, `Passage` and `ForumTools.ReadAsync`'s use of `Remember`; record what one extra verified read costs (key set, log proof, the retained head under R6.53's shared lock) and whether it can be made without `Remember`. If it cannot, stop and report: decision 7 is a ruling, not a hope.
6. **Where `cite as-is` is spelled.** `git grep -n "cite as-is\|cite it as"` over `src`, `tests`, `docs`, `README.md`, `CLAUDE.md`: the moved-facts list of Task 5, and any help or description the plan missed. Predicted: `CitationLines.cs:91`, `ToolText.cs:133`, `CitationLinesTests`, `CliRecheckTests:76`, `RecheckDisputedTests:89,99`; `Help.cs:338` and `Program.cs:1249` are about *disputed* and stay.
7. **Seams** (behaviour-preserving, so each probe fails by assertion and not by compile error): `CorrectionIndex.ForkOf` (empty), `BatchItemResponse.Fork` (served as `[]` on an item with a post), `CitationDocument.Fork` (default), `Supersession.CurrentStanding`/`ForkBranch`/`QuestionStanding` (unused), `RevisedQuestionPointerAsync` (returns null), `PublishedForkWords` parsing the patched errata.
8. **Write PI1 to PI39, each seen red or recorded green-first with the row that holds it** (predicted text below; record the text seen and change the probe, not the table's history, where a prediction was wrong).

### Task 1: The Forum's member (rulings 2, 3)
`CorrectionIndex.ForkOf`, `BatchItemResponse`, `BatchAsync`. PI1 to PI13 green.

### Task 2: The readers' words (rulings 1, 2, 3, 4)
`Supersession`, `CitationLines`, `ForumDocuments.ReadBatch`, `Help.cs`. PI14 to PI24 green.

### Task 3: The MCP adapter (rulings 5 to 8)
`RevisedQuestionPointerAsync`, the `question` line, the two descriptions, the version. PI25 to PI37 green.

### Task 4: End to end
`AnswerToARevisionEndToEndTests` and `RecheckForkEndToEndTests` against one real Forum on Kestrel, driving `curia` and `curia-mcp`. PI38 and PI39 green.

### Task 5: Existing facts, then the whole suite once
Run everything once and **list what moved by name**: expected `CitationLinesTests` (the `current` rows at `:171`, `:179`, `:285` to `:303` assert `cite as-is`; each flips to the new words and is kept, not removed, with the old assertion's name and D49 cited in the commit), `CliRecheckTests` (`:76`), `RecheckDisputedTests` (`:89`, `:99`: the *controls* of a disputed post are `current ... cite as-is`; they flip to the new words), `ToolDescriptionTests` (the recheck description's assertions and the version), `HelpTextTests` (the `recheck` entry), `RecheckToolTests`, `RecheckEndToEndTests`/`RecheckCapEndToEndTests` (any line they print), any fact that deserializes a batch item with a closed member set, and the request fuzzer's expected ledger (must stay empty; the batch route's row must see the new member as a problem-free `200`). Read in particular `McpSupersessionEndToEndTests` and `PublishedSupersession.cs`: the supersession words are parsed from the errata, and G42 adds words to the same family.

### Task 6: Falsification FPI1 to FPI38
Copy-aside patch, `--no-incremental` Release build of the gate, restore by plain `cp` and `touch` (never `git checkout --`), `shasum` equal before and after, an unpatched run green last. Count anchors first. FPI35 to FPI38 patch the errata and are restored from a kept copy.

### Task 7: Operate it again
Task 0's sequence on the fixed build: the plain current post says no later revision and no signature or acceptance checked; SB1 and SQ3a say the same and SQ3a says it is one of two (FORKED) naming SQ3b; SQ3b withheld is named by digest alone on SQ3a's line; RQ1 still names r1 only and the help says a hop is a call; `curia_answer` on SQ3a and on r2 names SQ3 and RQ1 and the `events` count is unchanged; on a lying `parent` (hand-signed) names nothing; `curia_answer` on SQ3 posts and its receipt has the `question` line naming both branches with FORKED; `curia_comment` on SQ3a posts with no extra line; `curia answer --parent <SQ3a>` exits 3 with the Forum's words; `curia verify` exit 0; the request fuzzer's ledger is empty.

### Task 8: Register and docs
D79 and D80 closed with the text below; the Merged table row; "Current work" and "Next stage"; README and CLAUDE.md (the `curia-mcp` version, the status paragraph's sentences on `recheck` and `curia_answer`); D49 annotated (second instance); a trap entry; the live defect register's count; the lead for SB1's lost acceptance filed under D80.

### Task 9: PR
GitButler branch, push, `gh pr create`, CI watched with a deadline (never an unbounded `--watch`), merge-time path check.

## Probes and their red text (predicted, not run)

"Red" means on the unmodified tree plus Task 0's seams.

| # | Probe | Asserts | Red text on the unmodified tree |
|---|---|---|---|
| PI1 | `BatchForkMemberTests` `R9_43_ABranchNamesTheOtherBranch` (Kestrel; Q by A, revisions r1 and r2 by A, both `prev` = Q's digest) | batch of [r1, r2]: each item `current`, `fork` has one element, the other's digest and `post_id` | `Expected: 1 Actual: 0` |
| PI2 | same, `...ThreeBranchesNameTwoOthersInLogOrder` | each of three branches lists the other two, in log order, never itself | `Expected: 2 Actual: 0` |
| PI3 | same, `...ALoneRevisionHasAnEmptyForkNotAnAbsentOne` | `fork` present and `[]` on a lone revision; control: the member exists on the item | green first (seam serves `[]`); held by FPI10 |
| PI4 | same, `...AQuestionAnAnswerAndACommentCarryAnEmptyFork` | `[]` for posts that are not revisions | green first; held by FPI4 |
| PI5 | same, `...OnlyTheAuthorsRevisionsAreSiblings` (a stranger's revision and a comment that name Q's digest) | neither is listed on r1's `fork`; control: the author's own second revision is | the control row `Expected: 1 Actual: 0`; the exclusion rows green first, held by FPI3, FPI5 |
| PI6 | same, `...AWithheldSiblingIsNamedByDigestAlone` (withheld and, in a second row, quarantined) | the sibling's digest is listed, no `post_id`; the withheld sibling's own item is `withheld` and carries no `fork` | `Expected: 1 Actual: 0` |
| PI7 | same, `...ARevisionOfARevisionIsNotAFork` (Q <- r1 <- r2) | `fork` is `[]` on r1 and r2; Q's item: `successors` [r1], `forked` false | green first; held by FPI6 |
| PI8 | same, `...ASupersededBranchStillNamesItsSibling` (r1 revised by r1b; r2 sibling) | r1's item is `superseded`, `fork` [r2]; r2's item `fork` [r1] only (the branch's descendants are not siblings) | `Expected: 1 Actual: 0` |
| PI9 | same, `...ItemsWithNoPostCarryNoForkMember` (withheld, unknown, malformed) | the member is absent; the batch is the same length and order as the request | green first (the seam attaches the member only where a post is); held by FPI9 |
| PI10 | same, `...TheOriginalsItemIsUnchanged` | Q's item: `forked` true, `successors` both, `fork` `[]` | green first; held by FPI7 |
| PI11 | same, `...TheSingleReadCarriesNoForkMember` (a recorded non-property) | `GET /v1/posts/{r1}` has no `fork` member; flips on purpose when decided | green first; held by FPI11 |
| PI12 | same, `...TheForkFollowsTheLog` (batch, append a second revision, batch again; no restart) | the sibling appears on the next batch | `Expected: 1 Actual: 0` on the second batch; held by FPI12 (a cached index) |
| PI13 | same, `...ASiblingsPostIdIsTheOneTheSingleReadServesForThatDigest` | the `post_id` in `fork` equals the id `GET /v1/posts/{id}` serves for that digest (a different route from the batch) | `Expected: 1 Actual: 0` |
| PI14 | `RecheckStandingWordsTests` `R10_83_CurrentSaysWhatWasEstablishedAndNotToCite` (a stubbed `current` item with a post, empty `fork`) | the line holds the phrases the entry names (no later revision reported; standing; no signature or acceptance checked), does not contain `cite as-is`; control: the post id and "digest recomputed here: matches" are still there | `Sub-string found: "cite as-is"` |
| PI15 | same, `...AForkBranchSaysItIsOneOfNAndForked` (one other branch, served) | `one of 2 revisions of the same post`, `FORKED`, the other's digest and `post <id>`, the Forum picks none; not `cite as-is` | `Sub-string not found: "FORKED"` |
| PI16 | same, `...NIsOnePlusTheOthersAndANotServedBranchIsSaidSo` (three branches, one without a post id) | `one of 3`; the unserved one as its digest and `not served` | `Sub-string not found: "one of 3"` |
| PI17 | same, `...AnAbsentForkIsNotStatedAndAnEmptyOneIsSilent` | `NOT STATED` only for the default array; the empty array says nothing of forks; the two lines differ | the `NOT STATED` row `Sub-string not found`; the silence row green first, held by FPI16 |
| PI18 | same, `...ASupersededBranchStatesBoth` | the `superseded -> <revisions>` clause and the branch words in one line | `Sub-string not found: "one of"` |
| PI19 | same, `...DisputedAndFailedLinesAreUnchanged` | a disputed `current` and a digest mismatch print the pre-change lines byte for byte | green first; held by FPI13 |
| PI20 | `CliRecheckTests` `R10_83_AForkBranchDoesNotMoveTheExitCode` (and a FORKED original) | exit 0 for a `current` fork branch and for a `superseded` original that is FORKED | green first; held by FPI19 |
| PI21 | `RecheckStandingWordsTests` `R10_83_ReadBatchTellsAbsentFromEmptyFromMalformed` | absent is the default array; `[]` is empty; elements are read as successors; a non-array, a non-object element and an element without `digest` are `ResponseMalformed` | the elements row `Expected: 1 Actual: 0`; the malformed rows `Expected: Fail Actual: Ok` |
| PI22 | same, `...AForumSuppliedForkValueIsAQuotedLiteral` (a `fork` digest and post id holding a newline and a forged `current  sha256:... cite as-is` line) | the forged text is escaped and no line is forged (P22; R10.63) | `Sub-string found` of the forged line, or green first once the member is read: held by FPI20 |
| PI23 | `RecheckStandingWordsTests` `G42_TheWordsAreTheErratas` | the member name, the phrase and `FORKED` the code emits equal those parsed from the entry's text | green first only once the seams exist; held by FPI35, FPI36 |
| PI24 | `HelpTextTests` `R11_56_TheRecheckHelpStatesWhatTheErratumRequires` | `recheck`'s help states the meaning of `current`, `fork`, direct revisions only, and does not say `cite as-is` | `Sub-string not found: "fork"` |
| PI25 | `RevisedQuestionPointerTests` `R11_55_AnAnswerToARevisionNamesTheQuestionItRevises` (stub: R (revision) served; Q served with `superseded_by` [R's recomputed digest]; the `POST` answers `400 parent-not-a-question kind=revision`) | the thrown refusal holds the Forum's words **and** Q's post id and "answer"; nothing was recorded | `Sub-string not found: "<Q's post id>"` |
| PI26 | same, `...APointerTheForumDoesNotConfirmIsNotGiven` (a theory over the same fixture changed by one field: Q's list empty; Q's list holds a different digest; Q's `superseded_by` absent; R's `parent` names an unrelated question Z) | no post id is named; the result says the post is a revision and why none is named; the Forum's words are still there | green first (the unmodified tool names nothing), **paired with PI25 by the same fixture so it is not vacuous**; held by FPI21, FPI22 |
| PI27 | same, `...AnOriginalThatDoesNotVerifyIsNotNamed` (Q's served digest does not match its canonical form) | none named, says the read did not verify | green first; held by FPI23 |
| PI28 | same, `...AWithheldOrMissingOriginalIsNotNamedAndTheRefusalStands` (`curia/posts/withheld`; `curia/posts/not-found`; a `503`) | none named; the thrown refusal is the Forum's, not the walk's; a `503` of the walk never replaces it | green first; held by FPI24 |
| PI29 | same, `...ARevisionOfARevisionIsFollowedToTheQuestion`, `...NineHopsNameNothingAndSayTheBound`, `...ACycleTerminates` | a chain of two reaches Q; a chain of nine names none and says the bound; a `parent` cycle ends within the bound (the stub's request list is counted: at most the bound's reads) | the first `Sub-string not found: "<Q's post id>"`; the other two green first (no walk), held by FPI25 |
| PI30 | same, `...ARevisionOfAnAnswerNamesNoQuestion` | a chain that ends at an answer or a comment names no post and says the root is not a question | green first; held by FPI26 |
| PI31 | same, `...TheWalkIsNotARead` (after the pointer, `curia_verify` of Q) | says no read in this session served it (R11.29) | green first; held by FPI27 |
| PI32 | same, `...NothingAnAuthorWroteIsInTheResult` (Q's title, body and tag hold a marker) | the marker is in neither the refusal nor the pointer | green first; held by FPI28 |
| PI33 | same, `...TheToolSendsAndTheForumDecides` | the stub's request list holds the `POST` and the result holds the Forum's own refusal | green first; held by FPI29 |
| PI34 | same, `...ACommentOnARevisionIsAcceptedAndANonRevisionParentIsUnchanged` | a comment on a revision is `POSTED` with no further `GET`; an answer under a comment parent is refused as before, no walk | green first; held by FPI30 |
| PI35 | `QuestionStandingReceiptTests` `R11_55_AnAnswerReceiptStatesTheQuestionsStanding` (theory: one revision; two; `[]`; absent) | a `question` line: `SUPERSEDED by 1 revision(s) ...` naming it; the same with `FORKED`; `none was reported`; `NOT STATED`; the four lines differ | `Sub-string not found: "question"` |
| PI36 | same, `...ACommentAndARevisionReceiptCarryNoQuestionLine` (the revision's `supersession` line is unchanged) | no `question` line on either; `supersession` line byte for byte | green first; held by FPI31 |
| PI37 | `ToolDescriptionTests` `R11_56_TheDescriptionsStateWhatTheErratumRequires` (**weak by construction**: restates descriptions; PI14 to PI18, PI25, PI35 and PI39 carry the information) and `R11_56_TheVersionIs085` (moved fact) | `curia_recheck` has no `cite as-is` and states `fork` and the one-hop rule; `curia_answer` states the pointer and the receipt line; `0.8.5` | `Expected: "0.8.5" Actual: "0.8.4"` |
| PI38 | `AnswerToARevisionEndToEndTests` `G42_TheTwoDoorsAndTheRefusalAreTold` (real Forum on Kestrel; ask Q, revise Q to R, a T1 agent) | `curia_answer` on R: names Q, the `events` count unchanged by SQL; `curia_answer` on Q: `POSTED` with a `question` line naming R; `curia answer --parent R`: exit 3 with the Forum's words and (non-property 9a) no pointer | `Sub-string not found: "<Q's post id>"` |
| PI39 | `RecheckForkEndToEndTests` `G42_BothReadersTellTheBranchAndNeitherSaysCite` (real Forum; a fork, a switched-away answer, a plain post) | `curia recheck` and `curia_recheck` print the branch words for each branch, the standing words for the switched-away answer and the plain post, and neither prints `cite as-is`; exit 0 | `Sub-string found: "cite as-is"` |

Regression facts (no new probe): the G35 batch facts (`RecheckEndToEndTests`, `RecheckCapEndToEndTests`), `McpSupersessionEndToEndTests`, G38's `PE*` rows, G41's `PH*` rows, `CliParentEndToEndTests`, the request fuzzer (`Fuzz/`: the batch row sees the member and a problem-free `200`).

**Order of red.** PI3, PI4, PI7, PI9, PI10, PI11, PI19, PI20 and the exclusion rows of PI5, the silence row of PI17, and PI26 to PI34 and PI36 are green on the unmodified tree for the right reason (the unmodified behaviour names nothing, walks nothing, prints no new line) and **each is held by a named falsification row**; PI26 is deliberately built on PI25's fixture so that "names nothing" cannot pass for "confirmed nothing". Everything else is red against the unmodified tree or the Task 0 seams.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FPI1 | serve `fork` as `[]` always (the seam, left in) | PI1, PI2, PI8, PI13, PI39 |
| FPI2 | list the item's own digest among its `fork` | PI1, PI2 |
| FPI3 | drop the author clause (siblings of any author) | PI5 |
| FPI4 | list non-revision posts that name the digest, or give a question or answer a `fork` | PI4, PI5 |
| FPI5 | reimplement the rule in the endpoint (`Prev` equals the digest only) | PI5 |
| FPI6 | walk the chain (transitive successors) | PI7 |
| FPI7 | put `successors` in the original's `fork`, or clear its `forked` | PI10 |
| FPI8 | list a withheld sibling with its `post_id` | PI6 |
| FPI9 | carry `fork` on an item with no post | PI9 |
| FPI10 | omit `fork` when it is empty | PI3, PI4 |
| FPI11 | carry `fork` on the single read | PI11 |
| FPI12 | cache the index (the first fold reused) | PI12 |
| FPI13 | change a disputed or FAILED line as a side effect of the new words | PI19 |
| FPI14 | restore `cite as-is` on `current` | PI14, PI39 |
| FPI15 | ignore `fork` in `LineOf` | PI15, PI16, PI18, PI39 |
| FPI16 | read an absent member as empty (or an empty one as not stated) | PI17, PI21 |
| FPI17 | read a non-array `fork` as empty | PI21 |
| FPI18 | N off by one (the others alone, or the others plus two) | PI15, PI16 |
| FPI19 | make a fork branch exit non-zero | PI20 |
| FPI20 | print a Forum-supplied `fork` digest or post id raw | PI22 |
| FPI21 | name the revision's `parent` without confirming it | PI26 |
| FPI22 | confirm against any non-empty list, or against a digest the Forum states and not the one recomputed | PI26 |
| FPI23 | skip the verified read of the original | PI27 |
| FPI24 | let the walk's failure replace the Forum's refusal, or name a withheld original | PI28 |
| FPI25 | remove the bound or the cycle guard; walk through a missing step | PI29 |
| FPI26 | name an answer or a comment as the question | PI30 |
| FPI27 | `Remember` the walk's reads | PI31 |
| FPI28 | echo the original's title in the pointer | PI32 |
| FPI29 | refuse locally and send nothing | PI33, PI38 |
| FPI30 | give a comment parent, or a non-revision parent, a pointer | PI34 |
| FPI31 | put a `question` line on a comment or a revision receipt | PI36 |
| FPI32 | drop the receipt line; say FORKED for one revision; read an empty list as `NOT STATED` or an absent one as none | PI35 |
| FPI33 | leave `cite as-is` in the tool description or the help text | PI24, PI37 |
| FPI34 | leave the version at 0.8.4, or drop a description sentence | PI37 |
| FPI35 | change the member name in the code only | PI23 |
| FPI36 | change the member name in the errata only | PI23 |
| FPI37 | change the bound in the code only (eight) | PI29 (its nine-hop row) and PI23 |
| FPI38 | change the bound in the errata only | PI23 |

## Gates

As #127: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (0 classes: no canonical form changes), `check-spec.py` and `falsify-spec-checks.py`, the invisible-code-point scan of added lines (memory note: `\u` escapes decode in tool calls), zero warnings. The request fuzzer's ledger stays empty. `Curia.Architecture.Tests`' port-string and JSON-reader fences are read after Task 1: the member is read through the guarded reader, and nothing new takes a caller's `string` at a port.

## Docs owed before the PR

Errata: `next2-gXX.patch` at the plan commit (renumbered if D77's docs moved anything); its Status line moves to "built" at "As run". `IMPLEMENTATION_PLAN.md`: the Merged row, "Current work", "Next stage", D79 and D80 closed, D49 annotated, the seventh exercise's "Observed during" section (already pasted by PR 1's docs task; this PR adds nothing there), a trap entry (45), the lead for acceptance in the batch under D80. `README.md` and `CLAUDE.md`: `curia-mcp` 0.8.5, the status paragraph's sentences on `curia recheck` (the `current` words and `fork`) and `curia_answer` (the pointer and the `question` line), and the recorded non-properties.

### Register text to paste

**D79, closed:**

> **Closed by the citation PR (errata G42, R11.55, R11.56; plan `docs/superpowers/plans/2026-10-27-a-citation-is-told-what-was-checked.md`, "As run" at its end).** `curia_answer` still sends and the Forum still refuses an answer whose parent is a revision (`parent-not-a-question`, `kind=revision`); the refusal now also names the question that revision revises, as the Forum's own `superseded_by` confirms it (the post the revision's `parent` names must verify and list the digest recomputed from the revision's served canonical form; up to eight posts, a cycle guard; any failed step names no post and says why; the walk's reads are not reads and carry nothing an author wrote). An accepted `curia_answer`'s receipt has a `question` line stating the question's standing at the read (superseded and by which revision, FORKED for two or more, none reported, or NOT STATED). `curia_comment` is unchanged (a comment's parent may be any kind). `curia answer --parent <revision>` is refused with the Forum's words and no pointer, a recorded non-property. `curia-mcp` 0.8.5. Probes PI25 to PI38, falsification FPI21 to FPI32.

**D80, closed:**

> **Closed by the citation PR (errata G42, R9.43, R10.83, R11.56; plan as above).** `current` in `curia recheck` and `curia_recheck` says the Forum reports no later revision and that no signature or acceptance was checked there; the words `cite as-is` are gone from both readers, both help texts and the tool description. Every batch item that carries a post carries `fork` (the other successors of the original its post revises, as `superseded_by`'s elements, from `CorrectionIndex`; `[]` for none; absent on an item with no post), and a fork branch's line says `one of N revisions of the same post (FORKED)` and names the others, `NOT STATED` where a Forum served no member. The description and help say that `superseded` names the direct revisions only and a hop is one call. No exit code moved. **Residual (leads):** `recheck` still does not report an acceptance (SB1's loss of it is found by reading the question); a single read of a fork branch does not say it is one. Probes PI1 to PI24, PI39, falsification FPI1 to FPI20, FPI33 to FPI38.

**Trap (number 45, taken at "As run"):**

> **A verdict word fixed at the case that found it, and a pointer taken from the field it points from.** D49 removed `cite as-is` from a disputed post and left it on every other `current` line, so the seventh exercise found it twice more (a switched-away answer, a fork branch): the cause was a line that said what an agent should do about a fact the call had not established, and it was removed one symptom at a time. And the first design of the question pointer read the revision's `parent`, which is its author's word and which the Forum never reads for a revision. The shape: when a word claims more than the check, find every place the word is spelled before fixing the one that was reported; and a pointer an agent will act on is confirmed by something the Forum states about *both* ends (here a list that holds the recomputed digest), never by the field one end wrote.

## Next stage

PR 3 of the seventh exercise's order (RULINGS.md): the small ones (D61 items 24 to 27, D81; ADMIT refuses a tag containing a comma, `curia`/`curia-mcp` guards for a blank `--cursor`, a NUL in an identifier and an upper-case URL scheme in `refs`, and the cosmetic lows). Its Task 0 (live operation of the unmodified tree, including the inbox-cursor lead) may run while this PR is in CI, in a detached worktree. It shares `ToolText.cs` and `Help.cs` with this PR, so **whichever merges second renumbers its erratum** (from G43 if G42 has landed) and rebases by hand. **An eighth five-agent exercise is offered after this PR**, because it changes what a citation is told: the cases to seed are SB1, a fork with a withheld branch, a chain of two revisions of a question, and a lying `parent` on a hand-signed revision. PR 4 (an asker can endorse a corrected answer, D78) follows; D82 stays an open lead.

## Open risks (named so the reviewer can look)

1. **A changed word.** Any agent or script that matched `cite as-is` stops matching. Alpha, no reader of the old form; the moved facts name it (PI14, Task 5). A reviewer should see the five `current` assertions flip to the new words, not disappear.
2. **A refusal path with cost.** A refused `curia_answer` of a revision now makes up to eight further verified reads. It happens only on the refusal, which stores nothing and spends no budget. If Task 0 item 5 finds the verified read cannot be made without side effects on the session (`Remember`, the retained head), stop and report; decision 7 is a ruling and the alternative is to name nothing.
3. **A pointer true at the read.** The question may be withheld or revised after the pointer; the next `curia_answer` then gets G41's `parent-withheld` or the receipt's `question` line. The pointer says it is the Forum's word as of the read.
4. **Fork is a relation among one author's revisions.** Two revisions of one original by different authors are not a fork, because R9.28 does not count the second as a correction. A revision whose `prev` names no post the log holds has no original and an empty `fork`. Both are rules, pinned by PI5 and PI3.
5. **Acceptance is still not in the batch.** SB1's line now says it did not check acceptance, which is true and is not the same as telling the reader it lost it. The eighth exercise is where an agent that needed it would show; a `replaced` state in the batch is its own design.
6. **The fork branch's single read is silent.** A citer who reads rather than rechecks is told nothing of the fork (decision 9b). The batch is the citer's door by R8.57 (a citation is a digest); a reader can still miss it.
7. **The CLI door is words-only.** `curia answer --parent <revision>` gets the Forum's refusal and no pointer (decision 9a). Two doors that give different answers were D77's defect; here the difference is deliberate, stated, and held by PI38's CLI row so it flips on purpose.
8. **A probe weak by construction.** PI37 restates descriptions. The information is in PI14 to PI18, PI25, PI35 and PI39; PI26 is built on PI25's fixture so that a tool that names nothing cannot pass it.
9. **The narrowing to `curia_answer` departs from RULINGS.md's wording.** Stated at the top. If the architect wants a comment pointer anyway, it is a sentence in a success and a probe, and it can be added without moving anything else.
10. **Identifiers G42, R9.43, R10.83, R11.55, R11.56, D79, D80, the prefix PI and 0.8.5 are provisional**; the re-grep before the first commit is the guard, and the patch is a diff against a5e10d52, which is the D77 branch and not `main`.

## As run

(Written at Task 8 from the records of Tasks 0 to 7.) The PR is #128, on `main` at 87e48c53 (#127). Errata G42 is built; its Status line says so.

### Task 0: the live part, then the probes

- **The live part ran early**, while #127 was in CI, in a detached worktree at 3ee625ce (#127's head, the unmodified tree for this stage) on the seventh exercise's restored database (99 events at start). Setup needed two restore attempts: the first, with `--no-owner` only, failed on a grant to a role that was absent, and the second, with `--no-privileges`, restored everything; the app role's grants were then applied by hand from `db/0001` to `0005`. The dump was restored, not seeded by hand. The worktree, database and role were removed and the Postgres server was not touched.
- **Item 1, the lines: predicted equals seen.** The CLI and the MCP block printed identical lines for ten digests: `current ... cite as-is` for the plain post, for SB1 (acceptance switched away), for both fork branches and for the end of a chain, and nothing said an acceptance was lost or that two posts were branches of a fork. SQ3 carried FORKED on its own line alone; RQ1 named its first revision only.
- **Item 2, the wire: predicted equals seen.** The batch item's keys were exactly `digest, state, successors, forked, post`, with no `fork`. After a withhold the original still listed the withheld branch's digest in `successors` (R9.19) and the CLI printed it `(not served)`.
- **Item 3, the refusal, three doors: predicted with one deviation.** `curia_answer` to a revision, to a revision of a revision and to a comment were refused (events 101 to 101, 102 to 102), `curia_answer` to the superseded forked question posted with no `question` line, `curia_comment` to a revision posted, and `curia answer --parent <revision>` exited 3 with the Forum's words. The deviation, carried into the probes: **the MCP refusal is a thrown tool error** (`An error occurred invoking 'curia_answer': ...`, `isError`), **not a `REFUSED:` line**, so PI25 and after match the Forum's problem type and `parent=<id> kind=revision`.
- **Item 4, what a real revision's `parent` holds.** Across all 16 revisions in the log, `parent` was the post id of the immediate predecessor and `prev` that predecessor's digest, and **a chain of depth three (SX1, r1, r2, r3) was produced by the reference tools alone**, so PI29 is an input the system produces and not only a defensive bound. `curia_revise` of r1 after r2 existed was refused `NOT SENT` as R11.47 says.
- **Item 5, the verified read on the refusal path.** `PassagesAsync` does a JWKS GET per distinct author, a local signature verify, a local span check, and `Remember` unconditionally; the retained head is used only by `PostVerifier`. A verified read measured exactly 2 Forum requests (about 3 ms each), so a walk of up to eight posts is at most 16. It can be made without `Remember` (one line in the shared loop), so decision 7 held and nothing stopped. The reason the skip must be real and have a probe: eight verified reads would otherwise add eight entries and could evict reads the model made (the 256 cap).
- **Item 6, where `cite as-is` is spelled.** Code: `CitationLines.cs` and `ToolText.cs` (`Help.cs` and `Program.cs` carry the disputed words, which stay). Tests: five `Contains` assertions to move and the `DoesNotContain` ones to keep as controls. History hits only in the register, the errata and an earlier plan; the runbook's line about `disputed` stays true.
- **Items 7 and 8, the seams and PI1 to PI39** (one commit). A withheld batch item's `post` is null (the key is present), so the rule is `fork` present on an item whose post is an object and absent where it is null. Behaviour-preserving seams (`ForkOf`, `BatchItemResponse.Fork`, `CitationDocument.Fork`, three `Supersession` entries, the pointer method returning null, `PublishedForkWords`), then probes: **23 test cases red on the seams (Api 10, Client 8, Mcp 5), the rest green first and held by named falsification rows**. Changes from the plan are recorded in the report: one-element checks are `Assert.Single` (xUnit2013); the in-process fixture for PI1 to PI13; PI14 asserts the four fragments the erratum names and not a full sentence; and **"says why no post is named" was not asserted**, because the erratum fixes no phrase. That last choice is the one the review corrected, below.
- **Two Debug test hosts hung during Task 0's fuzz attempt** (`RequestFuzzClosedTests`, about 23 minutes at 0% CPU, and the same on an untouched tree in Debug); **the controller killed them**. The fuzzer was run in Release only from then on, with a ten-minute 0% CPU kill rule.

### Tasks 1 to 5

Task 1 (R9.43) made `ForkOf` go through `For` and `IsCorrection`, so there is no second filter; Task 2 (R10.83, R11.56) wrote `CurrentStanding` and `ForkBranch` in `Supersession` so both readers compose from one definition, with `ReadRevisionList` shared by `superseded_by` and `fork`; Task 3 (R11.55, R11.56) wrote the pointer walk (bound eight, a visited set, verified reads, `remember: false`), the `question` receipt line, the descriptions and `curia-mcp` 0.8.5; Task 4 changed no file (PI38 and PI39 went green on the Task 1 to 3 code); Task 5 moved the existing facts: each `cite as-is` `Contains` became a `Contains` on `no later revision` plus a `DoesNotContain` on `cite as-is`, the 0.8.4 version fact was replaced by the 0.8.5 one, and the stub in `StubLog` serves `fork: []` with a held post, which fixed `StubFidelityTests`. No probe or assertion was loosened. In the Task 5 run the `browse` SIGHUP fact failed (exit 2, not 129) while other suites ran concurrently, passed alone in Debug and Release and passed in the gates: a load flake.

**The review failed spec on one Important finding, and the fault was the controller's.** R11.55's "SHALL say ... why no post is named" was missing: when any step failed, the result was the Forum's refusal alone. Task 0 had skipped the assertion, and **the controller's own ruling during Task 0 had wrongly made the sentence optional ("a nicety")**; the erratum text is a SHALL. Fix round 1 wrote the probe first (`R11_55_AFailedStepSaysWhyNoQuestionIsNamed`, six rows: does not list, does not verify, unreadable, bound, cycle, not a question), **all six seen red** (`Not found: "is a revision; no question is named because"`), then made `RevisedQuestionPointerAsync` always return text, the pointer or "The post you named is a revision; no question is named because <fixed cause>.", with no post id and nothing an author wrote, the Forum's refusal first. The same round made each post verified once (the parent's passage seeds the next step), which closed the review's Minor 2.

The review's other rulings and parked items:
- A `superseded` line stays silent on `fork NOT STATED`, because R10.83's "the line" is the `current` line; the register's D80 entry says so.
- Parked minor: `ForkWords` in `CitationLines.cs` partly duplicates `ForkBranch`; its empty branch in `ForkBranch` is reachable only from tests. Not changed.
- Parked minor: PI25's "answer that one" check is weak (the word also appears in the Forum's own text); it still goes red with the pointer removed.

### Gates

At 87f27b6d, 06:31 to 07:00 CDT on 2026-10-27:
- Release, 0 warnings. Api 943/943, Client 941, Domain 799, Mcp 570+1, Application 516, Canon 329, Infrastructure 160, AuthN 140, Domain.Primitives 64, Architecture 59 in Release and in Debug, Canon.Sodium 32.
- The locked restore, `cargo fmt --check`, clippy and `cargo test --locked` pass.
- Differential: 0 divergence classes.
- Spec checks clean; the falsifier 4 of 4 red.
- Conformance clean; the tree unchanged by shasum.

### Falsification: FPI1 to FPI39, 39 of 39 red in the end

Each row was built `--no-incremental` in Release, run on the named classes with `--no-build`, and restored by a plain copy and a touch; a shasum over 782 files (src, tests, errata) was empty before and after, and `git status --porcelain` was empty. Four rows were adapted to the code as built, and the report records how:
- **FPI4**: the first half (list non-revisions) went red on PI5 only, because PI4's fixture has no non-revision naming a digest; the adapted row gives a post with no `prev` a fork, and PI4 is red.
- **FPI22**: confirming against any non-empty `superseded_by` is red on PI26; comparing with the Forum-stated digest instead of the recomputed one is not separable in PI26's fixture and is red on PI29's rows and the new why-no-question fact.
- **FPI25**: with the cycle guard removed the bound still ends a cycle, so PI29's cycle row stayed green and the cycle row of the why-no-question fact went red; a larger bound is red on PI29's nine-hop row.
- **FPI30**: the answer-under-a-comment variant stayed green (the walk's first step reads nothing for a comment); the adapted row runs the walk for a comment on a revision, and PI34 is red.

Other forced changes of patch text: FPI21 (an unreachable-code analyzer refused `if (false)`) and FPI29 (a redundant-condition analyzer refused the first placement). Tests were never edited.
- **FPI39 (the controller's)** drops the "no question is named because" sentence and goes red on all six rows of `R11_55_AFailedStepSaysWhyNoQuestionIsNamed`.
- **FPI38 stayed green on the first run**: the errata's bound changed from eight to nine and nothing compared the code's bound with the published one, because the tests build chains of the errata's bound plus one, which the code's smaller bound still refuses; FPI37 was red on PI29's nine-hop row only. That was a gap, so a fix round added `R11_55_TheWalkBoundInTheCodeIsTheBoundTheErrataPublishes`, which reads the bound from R11.55 and compares it with the code's constant (`WalkBound` became `internal`): **red under FPI38 (Expected 8, Actual 9) and under FPI37 (Expected 9, Actual 8)**, green unpatched. The Mcp suite was then 571 passed, 1 skipped.

### Live: Task 7, the fixed build, 13 of 13 held

Run on a restored copy of the seventh exercise's database (99 events at start), against the Release build, with each item compared to Task 0's record of the unmodified tree:
- A plain `current` post, SB1, the end of a chain and the revised answer now say `the Forum reports no later revision of this document, which is its standing in the log; no signature and no acceptance was checked here (a standing is not soundness)`; the CLI and MCP lines are identical.
- Both fork branches say `one of 2 revisions of the same post ... FORKED ... the Forum picks none` and name the other; with one branch withheld, the other names its sibling by digest alone (`not served`), and the withheld branch's own line is `withheld ... drop this citation`.
- RQ1 names its first revision only, and the CLI help says a hop is one call.
- The batch items gain `fork` (`[]` for plain posts and the forked original, the sibling for each branch, none on a withheld item).
- `curia_answer` to a revision and to a revision of a revision were refused with the Forum's words plus the question named (events unchanged).
- **A live failing pointer step**, made with a hand-signed revision whose `parent` lies, was refused with `no question is named because the Forum's superseded_by of the post it names as its parent does not list this revision` (events unchanged).
- `curia_answer` to the forked question posted with a `question` line, `SUPERSEDED by 2 revision(s) ... FORKED`; `curia_comment` to a revision posted with no extra line; `curia answer --parent <revision>` exited 3 with the Forum's words and no pointer; `curia verify` of a fork branch exited 0.
- **The request fuzzer's ledger was empty, 20 of 20 in Release.** The Forum, database and role were removed afterwards.

### Lead

`RequestFuzzClosedTests` hangs at 0% CPU in Debug on the controller's machine, including on an untouched tree; it passes in Release, which CI runs. Recorded under the register's open leads.

### Final

- **Final whole-Api run:** on the finished tree (7baee15b), 943 of 943, 07:59 to 08:26 CDT 2026-10-27.
- **Final review:** ready to merge; no critical or important findings (`fork` from the one rule; the pointer names only a post whose own `superseded_by` lists the recomputed digest, as a display literal; the walk is bounded, cycle-guarded and remembers nothing; docs match). Minor, parked: "eight posts" counts reads; a thrown (not returned) exception in the walk would replace the Forum's refusal, unlikely as the client returns Results; one extra key-set fetch on the refusal path; `PublishedForkWords` reads the errata by design.
