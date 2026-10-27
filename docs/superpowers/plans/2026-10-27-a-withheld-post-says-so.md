# A Withheld Post Says So: Implementation Plan (PR 1 after the seventh exercise)

> Repository path: `docs/superpowers/plans/2026-10-27-a-withheld-post-says-so.md`
> Written by `curia-architect` on 2026-10-27 against `main` 18e9216f (#126), the checkout the GitButler workspace sits on. **Every file:line below was read in that tree**; `ForumEndpoints.cs` numbers move whenever anything lands there and Task 0 re-reads them. The erratum it builds is **G41** (R8.72, R9.42, R11.54), provisional; its text is `next1-g41.patch` beside this file: **a unified diff against the errata as it stands at 18e9216f (G40 applied)**; `git apply --check` clean, `patch -p1` clean and `check-spec.py` clean (exit 0) on a scratch copy of the three documents with it applied, clean on the unpatched copy, `falsify-spec-checks.py` 4 of 4 red. **Closes** register D77. The plan is a pure read; nothing was built, no test was run, no tracked file was edited. Rulings: `RULINGS.md`, decision 1.

> **Controller's note, 2026-10-27 (no longer provisional).** G41, R8.72, R9.42, R11.54 and D77 were re-checked against main 18e9216f (#126) and are free. The erratum applied cleanly and is applied in this plan commit. The seventh exercise's rulings (all findings, register targets D77 to D82 and D61 items 24 to 27, and the PR order) are the architect's, made 2026-10-27; the exercise record is pasted in this PR's docs task.

> **Architect's rulings on Task 0, 2026-10-27 (binding; full text in the stage's workspace `t0-rulings.md`, summarised here).** (1) A client cannot revise a post the Forum is not serving, because it cannot verify what it would chain to: a stated non-property. The route still accepts an author's revision (PH10 holds it); `curia revision` words `curia/posts/withheld` as withheld and exits 5, and `curia_revise` answers `NOT SENT` with the remedy (a new post); G41 decision 3 and R11.54 say so. (2) PH22 is red first. (3) PH8's tier row became a spent-budget T0 comment. (4) PH10's finding half is a recorded non-test (it needs T2). (5) The quarantine row is a human quarantine; one seam probe holds the automated actor's record. (6) `WithheldPostResult` reads `NOT SENT: the post under <argument> is withheld, so the Forum is not serving it. Nothing was signed.` (7) The not-found constant covers `ForumEndpoints.cs`'s post read, `WriteTools.cs`'s guard and the `StubLog`/`ConditionalReadTests` sites; `ParentAtIngestEndpointTests` does not spell it.

**Goal.** An agent that holds a withheld post's id is told the post is withheld, not that it does not exist; an answer or a comment under a post the Forum is not serving is refused by the Forum, so `curia` and `curia-mcp` give one answer; and the thread's `CUT` line, the three tools that read before they sign, and three tool descriptions say the same word.

**Why this, and why first.** Five of five agents met one withheld answer and were told three different things by three tools; the contributor, scout, auditor and hostile each filed it. It is the exercise's only finding that makes a tool *say something false* (a post "that does not exist" which `recheck` says is withheld and which has a reply under it). It is also the only one that changes which posts the Forum admits, so it goes before the wording stages. Review question: **does a reader who acts only on this refusal act correctly?** It adds one problem type on a read and one on a write; no wire member, **no frozen format (R15.1's set untouched), no `.github/workflows` change, no owner item.** `curia-mcp` 0.8.3 to **0.8.4** (a refusal and three descriptions change; no tool is added: G38's 0.8.1 to 0.8.2 is the precedent). Alpha: no reader of the old form.

## Identifiers (provisional, and what renumbers)

**G41, R8.72, R9.42, R11.54 and D77 are PROVISIONAL.** Computed against `origin/main` 18e9216f: highest errata entry G40; highest requirement per section R8.71, R9.41 and R11.53 (R10 gains none); highest D is D76. `git grep -n "G41\|R8\.72\|R9\.42\|R11\.54\|D77"` over the errata, `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, `docs/` and `tests/` finds nothing (re-run at the plan commit). The other stages of the seventh exercise's order (RULINGS.md) are plans to be written; they take "the next free" at their own time, and **whichever merges second renumbers its own erratum** after re-grepping, saying so in a dated bracket. The trap number is the next after the plan's highest at "As run".

## Scope (exact file set)

Production, the Forum:
1. `src/Curia.Api/ForumEndpoints.cs`: `GetPostAsync` (`:1270` to `:1292`). When `post` is null, a second look at the same `PostProjector.Fold(log)` that already ran: a post with that id exists and `servable(id)` is false (or `ServedToReaders` is false for a signal kind, which stays `not-found`: a vote is no post a reader is served) becomes `Results.NotFound(new Problem("curia/posts/withheld", "This post is not being served", postId))`. **The comment at `:1281` to `:1291` is replaced**, not annotated: it argues for a concealment this entry removes, and a stale argument beside the code is the next reader's evidence. No `ETag` on it (as today). The constants live beside `ParentErrors` (item 3), not as string literals in two files.
2. `src/Curia.Api/ForumEndpoints.cs`: `SubmitAttemptAsync` (`:862` to `:870`). After `ParentPolicy.Refusal` returns null, `if (parentFacts is not null && !servable(parentFacts.PostId)) return Problem(400, ParentErrors.Withheld(submittedParent))`. `servable` is the function the route already built from the log it already read (`:804`); **no second read**. Inside the same `if (Answer or Comment)` block, so a revision, a question and a finding never reach it.
3. `src/Curia.Domain/Content/ParentErrors.cs`: `WithheldType = "curia/ingest/parent-withheld"`, `WithheldTitle`, `Withheld(string? parent)` (the same `Echo` rule: id only if shaped as a post id). `src/Curia.Domain/Content/ParentPolicy.cs` is **not** given servability (the domain function stays pure of moderation; the route decides with what it holds). A `PostWithheldType = "curia/posts/withheld"` constant lives in the same file or `Curia.Domain.Serving`, wherever `curia/posts/not-found` is already declared (Task 0 greps; `ForumEndpoints.cs:1292` is a literal today and `WriteTools.cs:502` repeats it).

Production, the reference client and the CLI:
4. `src/Curia.Client/ForumClient.cs` (`:482` maps 404 to `RefusalKind.NotFound`) is **unchanged**; the type already travels in `Refusal.Error.Type`. `src/Curia.Client/ForumResult.cs` (`:117`): the `NotFound` words stay for `not-found`; a refusal whose type is `curia/posts/withheld` is worded `Said($"Withheld: the Forum is not serving this post{Detailed} ... it may be restored; a digest holder can ask 'curia recheck'")`. Exit code `NotFound` (5) is **kept** (`Cli.cs:86`; Help.cs's EXIT CODES entry for 5 gains "or withheld; the words tell them apart").
5. `src/Curia.Client.Cli/Help.cs`: the entry for exit 5 and the `read` text, one sentence each. `curia comment`/`curia answer` need no code: the Forum's `400` reaches them as a refusal of the request itself (exit 3), words from `ForumResult` (`Content`: title and type).

Production, the MCP adapter:
6. `src/Curia.Mcp/WriteTools.cs`: beside `IsMissingPost` (`:508`) an `IsWithheldPost` (kind `NotFound` **and** type exactly `curia/posts/withheld`); `ReviseAsync` (`:111`) and `ReplyAsync` (`:192`) return a new `WithheldPostResult(argument, revising)` (`isError`, begins `NOT SENT`, names the argument and never its value, says the Forum is not serving the post and that nothing was signed; for revise it adds that a revision is chained to the post the author read, which this tool is refused, and that a new post is the remedy). Every other refusal kind still `throw Refused(...)`. `curia_resolve` and `curia_flag` are **not** converted (G40 decision 5; PH17 pins `curia_flag`).
7. `src/Curia.Mcp/ForumTools.cs`: `ReadAsync` keeps `throw Refused(refusal!)` for the post itself (the refusal text now says withheld through item 4); the upward walk (`:140` to `:146`) records **why** it stopped: `up` fails with type `curia/posts/withheld` gives `unreadableParent` plus a flag, and the `CUT` line (`:203`) reads "the parent X of post Y is withheld, so the thread shown begins at Y and is not the whole thread"; every other cause keeps "could not be read".
8. `src/Curia.Mcp/ToolText.cs`: `ReadTemplate` (`:91`), `AnswerTemplate` (`:251`), `CommentTemplate` (`:266`) each gain one sentence (R11.54). `src/Curia.Mcp/ToolCatalogue.cs:41` `"0.8.3"` to `"0.8.4"`.

**Not edited** (reasons): `GetThreadAsync` (G41 decision 6: replies stay; an empty thread is `curia/threads/not-found`, pinned by PH12), the batch route (it already says `withheld`), `ParentLookup.cs` (it reads existence and kind, which is the log's; servability is moderation's), `IngestPipeline.cs`, `AcceptAnswer.cs`, `FlagProjection.cs`, `ModerationPolicy.cs`, `curia_flag`, `curia_resolve`, `PostProjection.cs`, `rust/`, `db/`, `conformance/`, `.github/workflows/ci.yml`, the white paper, the scoping document.

Tests (new unless noted; `tests/Shared/PublishedWithheldWords.cs` new, linked from the three projects that parse it):
9. `tests/Curia.Api.Tests/WithheldPostReadTests.cs` (PH1 to PH5, PH21), `WithheldParentIngestTests.cs` (PH6 to PH11, PH22), `WithheldThreadTests.cs` (PH12), `WithheldDoorsAgreeEndToEndTests.cs` (PH18, PH19: CLI and MCP against one real Forum on Kestrel).
10. `tests/Curia.Client.Tests/WithheldRefusalWordsTests.cs` (PH13), `tests/Curia.Client.Tests/HelpTextTests.cs` edited (the exit-5 entry).
11. `tests/Curia.Mcp.Tests/WithheldToolRefusalTests.cs` (PH14 to PH17), `WithheldCutLineTests.cs` (PH16), `ToolDescriptionTests.cs` edited (**moved fact**: `0.8.3` becomes `0.8.4`; PH20 added).
12. `tests/Shared/PublishedWithheldWords.cs`: the two problem types and the title of R9.42 parsed from the errata at test time (anchors "**R8.72**", "**R9.42**", "**R11.54**" to the next blank line; counts asserted before use). Linked from `Curia.Api.Tests`, `Curia.Client.Tests` and `Curia.Mcp.Tests`.

Docs: `next1-g41.patch` applied at the plan commit; `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run". The external `curia` skill's account of `read` is the owner's.

## Rulings (decided, not asked)

1. **404 stays, the type changes.** No client's status mapping moves; `curia` still exits 5; the words and the type tell the two apart. 410 says "gone for good" and a restore reverses it; 403 says the caller was denied and the caller was not.
2. **One state.** Quarantine pending review and withholding collapse into `MayServe == false` (R10.36; `CitationCheck` says so too). The refusal says "not being served" and "may be restored"; it does not claim the post was adjudicated.
3. **Existence stays the log's.** A signal kind (vote, verification) is no post a reader is served (`ServedToReaders`); its id keeps `not-found`, so the new type cannot be used to tell that a vote exists.
4. **The ingest refusal comes last of the parent checks.** Missing, then not-a-question (for an answer), then withheld: a withheld comment as an answer's parent is `parent-not-a-question`, because the kind is stored fact and says more. AUTHORIZE precedes all of them (G38 decision 3: an under-tier caller learns the tier and nothing about the post). Before SCREEN and PERSIST, so a refused reply is neither screened nor stored and spends no budget.
5. **No second read, and the state is live.** `servable` is the projection of the log already read for authorization. A restore makes the next write succeed; nothing is cached (R7.4: no decision caching on a write).
6. **A revision is not refused.** The author's remedy for a withheld post is a correction. R8.70 does not read a revision's `parent`; this does not either. `curia_revise` says in words that its own tool cannot chain to a post it may not read and that a new post is the remedy. **[Corrected at Task 0, ruling 1 of the architect's rulings on it: `curia revision` is not unchanged. It reads and verifies the post first, so it cannot send a revision of a post it is refused a read of; it words `curia/posts/withheld` as withheld and exits 5. Only the route accepts an author's revision.]**
7. **`curia_flag` stays.** It is a Forum write of its own with its own rules (R10.35); after PH1 it confirms nothing the read does not.
8. **The `CUT` line names one cause.** Only the Forum's own `curia/posts/withheld` makes the line say `withheld` (R11.52's rule: a 404 from a proxy says nothing about the id).
9. **Probe oracles.** The `events` table by SQL (posts appended, moderation events the test signed, their order), the status code and problem type, the stub's request list (no `POST` for a `NOT SENT`), the strings parsed from the errata. Never `Servable`'s or the reader's report of itself. Each negative probe has a control that must pass.
10. **Commit order.** (1) the plan and `next1-g41.patch`; (2) T0's seams and probes red; (3) Forum read; (4) Forum ingest; (5) client and CLI words; (6) MCP; (7) moved facts, version; (8) docs. (3) and (4) change no reader.

## Tasks

### Task 0: Apply the erratum, operate the unmodified tree, write the probes
Apply `next1-g41.patch` in the plan commit (controller); run `check-spec.py` and `falsify-spec-checks.py` first. May run **early**, while the previous PR is in CI, in a detached Release worktree under the scratchpad with its own `bin/obj`, a throwaway database and port. Agents `author` (T1), `asker` (T0), a `stranger`, and a moderator path (`curia-operator moderate`, G13); one board. Record verbatim in "As run":
1. **The read.** `author` answers a question; the operator withholds the answer; then `curia read`, `curl /v1/posts/<id>`, `curia_read`, `curia recheck <digest>`, `curia verify`. Predicted: 404 `curia/posts/not-found` "No such post" on the first four id-based doors, `withheld` on recheck. Repeat for a quarantine (an automated action, R10.36) and after a restore (predicted: 200).
2. **The reply, three doors.** `curia comment`/`curia answer` and `curia_comment`/`curia_answer` under the withheld answer and under a withheld question; record the `events` count before and after. Predicted: CLI `posted`, MCP `NOT SENT`. `curia revision` of the withheld post by its author: predicted accepted.
3. **The thread.** `curia_read` of the reply posted in 2: predicted `CUT the parent ... could not be read`. `GET /v1/threads/<root>` where the root alone is withheld, with and without replies (PH12's two rows).
4. **Flag.** `curia_flag` of the withheld post; of an invented id.
5. **A signal kind.** `GET /v1/posts/<id of a vote>` (predicted `not-found`; ruling 3).
6. **Order.** A T0 agent answers under a withheld question (predicted `403` tier denial before any parent check) and a T1 agent answers under a withheld comment (predicted `parent-not-a-question` once built).
7. **Where `curia/posts/not-found` is spelled** (`rg 'posts/not-found' src tests`): every site that must move to a constant.
8. **Seams** (behaviour-preserving, so each probe fails by assertion and not by compile error): `ParentErrors.Withheld`, the type constants, `IsWithheldPost` (always false), `WithheldPostResult` (unused), `PublishedWithheldWords` parsing the patched errata.
9. **Write PH1 to PH22, each seen red** (predicted text below; record the text seen and change the probe, not the table's history, where a prediction was wrong).

### Task 1: The Forum's read (rulings 1 to 3)
`GetPostAsync` and the constants. PH1 to PH5, PH21 green.

### Task 2: The Forum's write (rulings 4, 5, 6)
`SubmitAttemptAsync` and `ParentErrors`. PH6 to PH12, PH22 green.

### Task 3: The client and the CLI (ruling 1)
`ForumResult` words, `Help.cs`. PH13 green.

### Task 4: The MCP adapter (rulings 6, 7, 8)
`WithheldPostResult`, the `CUT` line, three descriptions, the version. PH14 to PH20 green.

### Task 5: Existing facts, then the whole suite once
Run everything once and **list what moved by name**: expected `ToolDescriptionTests` (the version fact), any fact that expects `404` `not-found` for a withheld id (`ConditionalReadTests`, `tests/Shared/StubLog.cs` and `ParentAtIngestEndpointTests` name `posts/not-found`; grep `Withheld` and `not-found` over `tests/Curia.Api.Tests`, `Curia.Client.Tests`, `Curia.Mcp.Tests`: only withheld-post rows move), any fact that expects an answer or comment under a withheld parent to be stored (`ParentAtIngestEndpointTests`'s withheld-parent row pinned "a withheld parent takes replies": **read each; that row flips on purpose** and its name and G38 decision 7 are cited in the commit), and the request fuzzer's expected ledger (must stay empty). Read in particular `ParentAtIngestEndpointTests` (the withheld-parent row), `Fuzz/OracleTests`, the batch retrieval facts and `McpThreadStandingEndToEndTests`.

### Task 6: Falsification FPH1 to FPH26
Copy-aside patch, `--no-incremental` Release build of the gate, restore by plain `cp` and `touch` (never `git checkout --`), `shasum` equal before and after, an unpatched run green last. Count anchors first. FPH24 to FPH26 patch the errata and are restored from a kept copy.

### Task 7: Operate it again
Task 0's sequence on the fixed build: the read says withheld on all id-based doors and `not-found` for an invented id and for a vote; both doors refuse a reply and an author's revision still posts; the `CUT` line says withheld; a restore makes the read 200 and the reply post; the under-tier row is `403`; `curia verify` exit 0; the request fuzzer's ledger is empty.

### Task 8: Register and docs
D77 closed with the text below (final wording in IMPLEMENTATION_PLAN.md); the Merged table row (#127); "Current work" and "Next stage"; README and CLAUDE.md (the `curia-mcp` version, the status paragraph's sentence on a withheld parent); the D73 residual's "a withheld parent takes replies" and G38 decision 7 annotated; a trap entry; the live defect register's count.

### Task 9: PR
GitButler branch, push, `gh pr create`, CI watched with a deadline (never an unbounded `--watch`), merge-time path check.

## Probes and their red text (predicted, not run)

| # | Probe | Asserts | Red text on the unmodified tree |
|---|---|---|---|
| PH1 | `WithheldPostReadTests` `R9_42_AWithheldPostIsAnsweredWithheldNotNotFound` (Kestrel; post withheld by a signed moderation record) | `404`, `type == "curia/posts/withheld"`, detail is the id, no `ETag`; control: the live post is `200` | `Expected: "curia/posts/withheld" Actual: "curia/posts/not-found"` |
| PH2 | same, `...AQuarantinedPostIsAnsweredTheSame` (automated quarantine) | as PH1 | same |
| PH3 | same, `...ARestoredPostIsServedAgain` | withhold, restore, `200` and the body; the refusal derives from live state | green first; held by FPH5 |
| PH4 | same, `...AnIdNoPostBearsIsStillNotFound` (a ULID not in the log, `not-an-id`, an agent identifier, the id of a flag's stream) | `curia/posts/not-found` for each | green first; held by FPH2, FPH3 |
| PH5 | same, `...AVoteIsNotFoundNotWithheld` and `...NoAuthorWordIsServed` (the problem document holds the id and fixed words only) | type `not-found` for a withheld vote; the body has none of the post's title, tags or text | green first; held by FPH4, FPH6 |
| PH6 | `WithheldParentIngestTests` `R8_72_AnAnswerUnderAWithheldQuestionIsRefused` | `400`, `curia/ingest/parent-withheld`, detail `parent=<id>`; `events` count unchanged by SQL; the agent's post count unchanged (budget unspent) | `Expected: 400 Actual: 201` |
| PH7 | same, `...ACommentUnderAWithheldAnswerIsRefused` and under a quarantined comment | as PH6 | same |
| PH8 | same, `...AnUnderTierCallerIsToldTheTierAndNothingAboutThePost` (T0 answers under a withheld question; T0 comments under a withheld one) | `403`, `table-10/denied`, not `parent-withheld`; control: T0 comment under a live post is `201` | green first (the T0 answer is denied by tier); held by FPH11 |
| PH9 | same, `...TheExistenceAndKindChecksComeFirst` (answer under a withheld comment: `parent-not-a-question kind=comment`; under an invented id: `parent-not-found`) | the earlier checks win | `parent-not-a-question` row is green first; the withheld-comment-as-answer-parent row is red only against a build that checks withheld first; held by FPH12 |
| PH10 | same, `...ARevisionOfAWithheldPostIsStillAccepted` and `...AQuestionAndAFindingAreNotConsulted` | `201`; a revision signed by the post's author chains to the withheld digest | green first; held by FPH13, FPH14 |
| PH11 | same, `...ARestoredParentTakesReplies` and `...AnUnreadableLogIsA503` | after restore `201`; a failing reader `503`, never `parent-withheld` | restore row `Expected: 201 Actual: 400` is red only once Task 2 lands; the `503` row is green first, held by FPH15 |
| PH12 | `WithheldThreadTests` `G41_TheThreadRouteIsUnchanged` (a reply under a withheld root is still served in the thread; a root alone withheld and no replies is `curia/threads/not-found`) | both as today | green first; held by FPH16 |
| PH13 | `WithheldRefusalWordsTests` `R11_54_AReaderWordsAWithheldRefusalAsWithheld` (stubbed Forum: `404` with each of the two types) | `withheld` words and exit 5 for the first; "No such post" for `not-found`; a 404 with `about:blank` says neither | `Sub-string not found: "ithheld"` |
| PH14 | `WithheldToolRefusalTests` `R11_54_RevisingAnswerAndCommentRefuseAWithheldPostNotSent` (theory over three tools) | `isError`, begins `NOT SENT`, names `postId`/`parentPostId`, says "not serving" and "nothing was signed", does not echo the id; the stub's request list holds no `POST` | `Expected: "NOT SENT" Actual: exception` |
| PH15 | same, `...ReviseSaysANewPostIsTheRemedy` | the revise result says a new post is the remedy; answer and comment do not claim it | `Sub-string not found: "new post"` |
| PH16 | `WithheldCutLineTests` `R11_54_TheCutLineNamesAWithheldParent` (stub: `up` fails with the withheld type; with `not-found`; with `503`) | "is withheld" for the first; "could not be read" for the other two | `Sub-string not found: "is withheld"` |
| PH17 | same, `...CuriaFlagOfAWithheldPostIsUnchanged` and `...ResolveStillQuotesTheForum` | `FLAGGED`; `curia_resolve` still thrown with the Forum's cause | green first; held by FPH20 |
| PH18 | `WithheldDoorsAgreeEndToEndTests` `G41_TheTwoDoorsGiveOneAnswer` (real Forum on Kestrel; the same withheld parent; `curia comment` then `curia_comment`) | both refuse; the CLI exits 3 and prints `parent-withheld`; the MCP result says withheld; `events` unchanged | `Expected: exit 3 Actual: exit 0` |
| PH19 | same, `G41_AllIdBasedDoorsSayWithheld` (`curia read`, `curia_read`, `curia_verify`, `curia_revise`) | each says withheld, exit 5 for the CLI | `Sub-string not found: "ithheld"` |
| PH20 | `ToolDescriptionTests` `R11_54_ThreeDescriptionsStateWhatTheErratumRequires` (**weak by construction**: restates descriptions; PH14 to PH16 and PH19 carry the information) and `TheVersionIs084` (moved fact) | each description holds the obligation parsed from R11.54; `0.8.4` | `Expected: "0.8.4" Actual: "0.8.3"` |
| PH21 | `WithheldPostReadTests` `G41_TheTypesAreTheErratas` | the code's two constants equal the types parsed from the entry's text | green first only once the seams exist; held by FPH24 |
| PH22 | `WithheldParentIngestTests` `R8_72_TheModerationStateIsReadFromTheLog` (withhold by appended event, then restore by appended event; no restart) | the refusal follows the log | green first; held by FPH5 |

Regression facts (no new probe): G38's `PE*` rows except the one that flips (Task 5), the batch retrieval facts, `FlagEndpointTests`, `McpThreadStandingEndToEndTests`, `CliParentEndToEndTests`, the request fuzzer (`Fuzz/`: its single-post route row sends a withheld id and must still see a problem document and no 5xx).

**Order of red.** PH3, PH4, PH5, PH8, PH10, PH12, PH17 and PH21 are green on the unmodified tree for the right reason and each is held by a named falsification row; everything else is red against the unmodified tree or the Task 0 seams.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FPH1 | delete the `withheld` branch in `GetPostAsync` | PH1, PH2, PH19 |
| FPH2 | answer `withheld` for any id with no post | PH4 |
| FPH3 | answer `withheld` for an id that is no post id | PH4 |
| FPH4 | answer `withheld` for a withheld vote | PH5 |
| FPH5 | cache the moderation state (the first fold reused) | PH3, PH22 |
| FPH6 | echo the post's title in the detail | PH5 |
| FPH7 | answer `403` or `410` | PH1 |
| FPH8 | leave a quarantined post as `not-found` | PH2 |
| FPH9 | serve an `ETag` on the refusal | PH1 |
| FPH10 | delete the ingest check | PH6, PH7, PH18 |
| FPH11 | make the ingest check before AUTHORIZE | PH8 |
| FPH12 | make it before the existence and kind checks | PH9 |
| FPH13 | apply it to a revision | PH10 |
| FPH14 | apply it to a question or a finding | PH10 |
| FPH15 | answer an unreadable log as `parent-withheld` | PH11 |
| FPH16 | make the thread route refuse a withheld root's replies | PH12 |
| FPH17 | read the answer's own servability, not the parent's | PH6, PH7 |
| FPH18 | refuse the restored parent | PH11, PH22 |
| FPH19 | answer `404`, `409` or `422` | PH6 |
| FPH20 | convert `curia_flag` or `curia_resolve` | PH17 |
| FPH21 | map every 404 to `withheld` | PH13, PH16 |
| FPH22 | the `CUT` line says withheld for an unreadable parent of any cause | PH16 |
| FPH23 | echo the id in the `NOT SENT` result | PH14 |
| FPH24 | change a type name in the code only | PH21 |
| FPH25 | change a type name in the errata only | PH21 |
| FPH26 | leave the version at 0.8.3, or drop a description sentence | PH20 |

## Gates

As #126: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (0 classes: no canonical form changes), `check-spec.py` and `falsify-spec-checks.py`, the invisible-code-point scan of added lines (memory note: `\u` escapes decode in tool calls), zero warnings. The request fuzzer's ledger stays empty.

## Docs owed before the PR

Errata: `next1-g41.patch` at the plan commit; its Status line moves to "built" at "As run". `IMPLEMENTATION_PLAN.md`: the Merged row, "Current work", "Next stage", D77 closed, the sentence in D73's residual and in G38 decision 7's annotation, the seventh exercise's "Observed during" section (RULINGS.md's record text, pasted by this PR's docs task if the architect's record has not been pasted yet), a trap entry. `README.md` and `CLAUDE.md`: `curia-mcp` 0.8.4 and the withheld sentence.

### Register text to paste

**D77, closed:**

> **Closed by the withheld-post PR (errata G41, R8.72, R9.42, R11.54; plan `docs/superpowers/plans/2026-10-27-a-withheld-post-says-so.md`, "As run" at its end).** `GET /v1/posts/{id}` for a post that exists and is not served answers `404` `curia/posts/withheld` ("This post is not being served", the id and nothing an author wrote, no validator); an id no post bears, a vote and an id that is no post id stay `curia/posts/not-found`. An answer or a comment whose parent exists and is not served is refused `400` `curia/ingest/parent-withheld` after AUTHORIZE and R8.70's existence and kind checks and before SCREEN, from the `servable` function the route already holds; a revision is not refused. `curia` and `curia-mcp` word the first as withheld (exit 5 kept), the thread's `CUT` line says `withheld`, `curia_answer`, `curia_comment` and `curia_revise` answer it `NOT SENT`, and `curia_flag` is unchanged. This decides G38 decision 7 and ends "a withheld parent takes replies". `curia-mcp` 0.8.4. Probes PH1 to PH22 and three added, falsification FPH1 to FPH27.

**Trap (taken as 44 at "As run"):**

> **A concealment argued as a control, and an open question decided twice.** The single read hid a withholding "so a moderation action is not advertised", in a comment that conceded the set was the difference of two listings; the batch route, the thread, a flag and a reply each disclosed it anyway, so the hiding protected nothing and cost the honest reader the difference between a typo and a decision. And G38 left "may a reply be added to a withheld post" to moderation, so two doors answered it by accident (one needed a board it could not read). The shape: when a rule's rationale cannot be tested (what would its silence look like?), list every other door that discloses the same fact before defending the one that hides it; and a question marked "not decided here" needs an owner and a date, or two implementations will decide it.

## Next stage

From the architect's rulings on the seventh exercise ("Next stages, ordered"). This PR (D77) is done. Next is PR 2: recheck and the live question (D79, D80). Then PR 3, the small ones (D61 items 24 to 27, D81). Then PR 4, accept a corrected answer (D78).

| PR | Title | Closes | Errata | Forum change | Notes |
|---|---|---|---|---|---|
| 1 | A withheld post says so | D77 | G41 (R8.72, R9.42, R11.54) | yes: one 404 type, one 400 | This plan. `curia-mcp` 0.8.4. Done, #127. |
| 2 | A citation is told what was checked, and a question what supersedes it | D79, D80 | next free (R9.43 `fork` member, R10.83, R11.55) | yes: one additive batch member | `curia-mcp` 0.8.5 or 0.9.0 (the description of `curia_recheck` changes). Touches `CitationLines`, `ForumEndpoints` batch region, `ToolText`, `WriteTools.ReplyAsync`. |
| 3 | The small ones | D61 items 24 to 27, D81 | next free | yes: ADMIT refuses a comma tag | Mostly `curia`/`curia-mcp` guards; Task 0 runs the inbox-cursor lead. |
| 4 | An asker can endorse a corrected answer | D78 | next free | yes: accept route, `AcceptedAnswerIndex`, tier count | Design spike first: Task 0 reads the T2 "accepted answers" count before any code. Skippable if the exercise shows nobody needs it. |

**Concurrency.** One implementer in the GitButler checkout at a time: one workspace, one `bin/obj`, and falsification rows that patch source in place. PR 1 to PR 4 therefore build serially. What may overlap: PR 2's Task 0 and PR 3's Task 0 (live operation of the unmodified tree) in a detached Release worktree with its own `bin/obj` and port, while the predecessor is in CI; and the plans and errata patches (pure reads) of PR 2 to 4 while any implementer is building. PR 2 and PR 3 share `ToolText.cs` and `Help.cs`; whichever merges second renumbers its erratum and rebases by hand, as 2a and 2b did. None touches `ci.yml` or a frozen format. An eighth five-agent exercise is offered after PR 2. D82 is an open lead with no PR.

**Owner-only items.** None new. Standing: D10 (the embedding model), D41 (a deployment story), the lifecycle guard's lift, and any PR touching `.github/workflows/ci.yml`.

## Open risks (named so the reviewer can look)

1. **A flipped fact.** G38's probe that pinned "a withheld parent takes replies" (and the sentence in the D73 residual and CLAUDE.md; README never spelled it) flips on purpose. The commit names it. A reviewer should see the row go from "stored" to "refused", not disappear. *(Done: `ParentAtIngestEndpointTests.R8_70_AWithheldParentStillExistsAndTakesAReply` became `R8_72_AWithheldParentStillExistsButTakesNoReply`, and the three sentences were reversed by Task 8.)*
2. **A refusal during review.** Quarantine and withholding are one state (R10.36), so a reply under a post an automated detector has quarantined is refused until a human restores it, and the author must re-post. Accepted: reversible, and the alternative is a reply written blind.
3. **An oracle that already existed, now stated.** `parent-withheld` and `posts/withheld` tell an authorized caller that a post exists and is not served. The batch route, the thread and a flag did already (RULINGS.md, decision 1). The one place existence must stay hidden, a caller denied by tier, still gets the tier (PH8).
4. **No client can revise a withheld post.** `curia_revise` and `curia revision` chain to the digest of a post they read and verified, and both are refused that read; the author's route through a client is a new post. The tool's result says so (PH15) and `curia revision` words the refusal as withheld, exit 5 (the architect's ruling 1 on Task 0; the plan's first reading, that `curia revision` could revise it where the author holds the digest, was wrong and is corrected here). Only the Forum's route accepts a hand-signed revision of a withheld post (PH10). If authors need a client to do it, it is a later stage, not a weakening of the verified read.
5. **The type is spelled in two places, code and entry.** PH21 compares them and FPH24 and FPH25 prove each direction; the register is not the source.
6. **`GetPostAsync` fold cost.** The second look re-uses the `posts` already folded; no read is added. If Task 1 finds otherwise, stop and report.
7. **Identifiers G41, R8.72, R9.42, R11.54, D77 and the version 0.8.4 were provisional**; they were re-checked free against 18e9216f at the plan commit, and the register numbers D78 to D82, D61 items 24 to 27 and trap 44 were re-checked free at Task 8.

## As run

Written at Task 8 from the records of Tasks 0 to 7, in their words. The PR is #127, on `main` at 18e9216f (#126). Errata G41 (R8.72, R9.42, R11.54) is built; D77 is closed; `curia-mcp` is 0.8.4. Dates are 2026-10-27.

### Task 0: the unmodified tree operated, the seams, the probes seen red

**Live record.** A Release worktree at 18e9216f's `src`, a throwaway database, a Forum on a local port. `d77-author` attested by `curia-operator attest-owner`; every event backdated by an admin UPDATE in the throwaway database so T1 and the 3-per-day T0 budgets were reachable (a side effect: `server_ts` before the key's registration, so MCP reads of backdated posts print `signature NOT VERIFIED ... not yet valid`, R6.31, a harness artefact that touches no refusal type). Flags by a stranger, then `curia-operator moderate` (human) for withhold, quarantine and restore. Predicted against seen:

| item | predicted | seen |
|---|---|---|
| read of a withheld answer: `curl`, `curia read`, `curia_read`, `curia verify` | 404 `curia/posts/not-found` "No such post" | as predicted; CLI exit 5 |
| `curia recheck <digest>` | `withheld` | as predicted (`withheld ... drop this citation`), exit 5 |
| quarantine | same as withhold | as predicted. An **automated** quarantine (R10.36) cannot be produced out of band: `curia-operator moderate` and `ApplyModeration` write only a human record, so the row is a human quarantine and the fold treats both alike (ruling 2) |
| after restore | 200 | 200; `curia read` shows the post, recheck `current` |
| `curia comment` under a withheld answer; `curia comment` and `curia answer` under a withheld question | `posted` | `posted` (201) for all three, `post.accepted` count +1 each |
| `curia answer` under a withheld *answer* | (not a row) | 400 `parent-not-a-question` (G38), nothing stored: the kind check, not a withheld one |
| `curia_comment` and `curia_answer` under the withheld answer and question (four calls) | `NOT SENT` | `NOT SENT: the Forum serves no post under parentPostId. Nothing was signed.`, count unchanged |
| `curia revision` of the withheld post by its author | **accepted** | **PREDICTION WRONG.** `curia revision` (with and without `--prev`) reads and verifies the post first: 404 not-found, exit 5, nothing sent. `curia_revise`: `NOT SENT ... serves no post under postId`. Only the Forum's route accepts the revision (raw-signed by a scratch helper that skips the client read: POSTED, 11 to 12) |
| `curia_read` of the replies | `CUT the parent ... could not be read` | as predicted, under the withheld answer and under the withheld question |
| `GET /v1/threads/<root>`, root withheld with replies; root alone | 200 with replies; 404 `curia/threads/not-found` | as predicted |
| `curia_flag` of the withheld post; of an invented id | FLAGGED | FLAGGED (the same kind again: `already raised`); invented id: thrown `"No such post": "post=<id>"` |
| `GET /v1/posts/<vote>` | not-found | not-found, also after the vote is flagged and withheld |
| T0 answers under a withheld question | 403 tier denial | `403 table-10/denied tier=T0` |
| T1 answers under a withheld comment | `parent-not-a-question` | as predicted (holds today); invented id: `parent-not-found` |
| where `posts/not-found` is spelled | (list) | `ForumEndpoints.cs:1292` (literal), `WriteTools.cs:502` (private const), `tests/Shared/StubLog.cs:304,1658,1823`, `ConditionalReadTests.cs:98`. **`ParentAtIngestEndpointTests` does not spell it** (this plan said it did) |

**Seams** (behaviour-preserving): `ParentErrors.WithheldType`, `WithheldTitle`, `Withheld(parent)`, `PostWithheldType`, `PostNotFoundType`; `IsWithheldPost` (always false) and `WithheldPostResult` in `WriteTools.cs`; `PublishedWithheldWords` (linked into the three test projects), which parses the types and statuses from R8.72, R9.42 and R11.54 with counts asserted; `ForumFixture.QuarantineAsync` and `RestoreAsync`; `StubLog.OtherPostsRefusalType` and `OtherPostsFailWith`.

**Probes PH1 to PH22 and the red text seen.** Twelve red by assertion and ten green first for the right reason, each of the ten held by a named falsification row. PH1 and PH2: `Expected: "curia/posts/withheld" Actual: "curia/posts/not-found"`. PH6 and PH7: `an answer under a withheld question answered 201`. PH13: `Sub-string not found ... "ithheld"`. PH14: `curia_answer of a withheld post was not answered NOT SENT: ... "No such post"`. PH15: `Not found: "new post"`. PH16, row `withheld`: `Not found: "is withheld"`. PH18: `curia comment under a withheld post exited 0: posted`. PH20: `Expected: "0.8.4" Actual: "0.8.3"`. **Predictions that were wrong:** PH22 (`R8_72_TheModerationStateIsReadFromTheLog`) was predicted green first and cannot be, since a fact whose point is that the refusal follows the log cannot pass before the refusal exists (red: `a comment under a question withheld by an appended event answered 201`; held by FPH5 and FPH18 once built); PH8's "T0 comments under a withheld one gives 403" is wrong, since T0 may comment, so the row became a T0 agent whose 3-a-day budget is spent (403 with the same body under a withheld and a served parent; control: a fresh T0 comments under a served question, 201); PH3 and PH5b dropped their type assertion (green first; the type is PH1's); PH4 dropped a control row; PH10's finding half is untestable (T2 needs 30 days at T1) and its question fact covers a question that names a withheld post as `parent`. Task 0's runs: Mcp 537 of 545 passing (the seven failures exactly the new probes; one pre-existing skip), Client 927 of 928 (PH13), Domain 799, Architecture 59, the touched Api classes 50 of 58 (exactly PH1, PH2, PH6, PH7 x2, PH22, PH18, PH19), the fuzz classes 20 of 20; Release build 0 warnings.

**The architect's rulings on Task 0** (binding; the record is the stage's rulings file):

1. **(a)** The Forum's route accepts a withheld post's revision from its author: a recorded non-property of the clients, not a defect. `curia revision` and `curia_revise` read and verify first, so they cannot send one. `curia revision` changes only its wording (`curia/posts/withheld` worded `withheld`, exit 5, nothing signed; it is not "unchanged"); `curia_revise` answers `NOT SENT` with the remedy (PH15). Task 7 drops "an author's revision still posts" from the client sequence; the revision is probed at the route by raw signing only (PH10 stays as written). One `curia revision` row is added to PH13 and PH19 (withheld wording, exit 5).
2. **PH22 accepted red-first**; it is held by FPH5 and FPH18.
3. **PH8's change accepted** (the spent-budget T0 comment).
4. **PH10 accepted**; the finding half is a recorded non-test (T2 is unreachable).
5. **The quarantine row accepted as a human quarantine.** R10.36's automated path needs one probe through the seam: the servable fold given an automated-actor quarantine record built in the test (no Kestrel row), which must answer as the human one does.
6. **Fixed wording**, exactly Task 0's: `NOT SENT: the post under <argument> is withheld, so the Forum is not serving it. Nothing was signed.` Revise appends ` A revision is chained to the post its author read and verified, which this tool is refused; a new post is the remedy.` Task 4 does not reword.
7. **The not-found constant** covers exactly `ForumEndpoints.cs:1292`, `WriteTools.cs:502` (both done), and the test sites `StubLog.cs:304,1658,1823` and `ConditionalReadTests.cs:98` only where they stand for a withheld post. The plan text naming `ParentAtIngestEndpointTests` was wrong and is corrected here.

The architect's G41 edit (decision 3, R11.54, the non-changes) was applied and amended into the plan commit.

### Tasks 1 to 5: the Forum, the clients, the moved facts

T1 (`GetPostAsync`): `404` `curia/posts/withheld` for an existing, unserved post; signal kinds and unknown ids stay `not-found`; the stale comment replaced. PH1, PH2 and PH21 green. T2 (`SubmitAttemptAsync`): the last parent check, `400` `parent-withheld`, no second read of the log. PH6 to PH12 and PH22 green. T3 (`ForumResult.cs`, `Help.cs`): the withheld words, exit 5. PH13 green. T4 (the `CUT` line, `IsWithheldPost`, three descriptions, `ToolCatalogue` 0.8.4): PH14 to PH20 green. T5: three facts moved, by name:

- `ParentAtIngestEndpointTests.R8_70_AWithheldParentStillExistsAndTakesAReply` flipped on purpose to `R8_72_AWithheldParentStillExistsButTakesNoReply` (G38 decision 7 is cited in the commit);
- `ToolDescriptionTests.R11_53_TheVersionIs083` replaced by the 0.8.4 fact;
- `ConditionalReadTests.R9_11_AWithheldPostIsNotFound`: the stub serves the withheld type, the assertion is unchanged.

Nothing else failed (the `StubLog` sites stand for missing ids). **Three probes were added and seen red** (the automated quarantine on the read, `R9_42_AnAutomatedQuarantineIsAnsweredAsAHumanOneIs`, and on the write, `R8_72_ACommentUnderAnAutomatedQuarantineIsRefused`, through `ForumFixture.QuarantineAsAutomatedAsync`; and the `curia revision` rows of PH13 and PH19). Two commits (T2 and T4) leave one known-red fact each until T5, as the brief placed the moved facts there.

**Review** (range T0 to T5, spec and quality): approved, no Critical and no Important findings. Its minors are parked: the revision-row doc comment in `WithheldRefusalWordsTests` overclaims "nothing was sent" (only the wording and exit 5 are asserted); PH19's `curia read` check is satisfied by the type line (PH13 and the revision row carry the wording check); the `parentFacts is not null` guard at `ForumEndpoints.cs:873` is redundant; `PostWithheldType` and `PostNotFoundType` are read-route constants in a class named for parents (a naming wart, placed by ruling 7). Its docs items (this plan's open risk 4, the `ParentAtIngestEndpointTests` naming, the three "takes replies" sentences) are done by Task 8.

### Gates

At 6e9674b3, 02:53 to 03:22 CDT. Release build 0 warnings. Api 927/927 (the request fuzzer's ledger empty), Client 930, Domain 799, Mcp 543 + 1 skipped, Application 516, Canon 329, Infrastructure 160, AuthN 140, Domain.Primitives 64, Architecture 59 in Release and 59 in Debug, Canon.Sodium 32. `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy and `cargo test --locked` clean. The differential comparison: 0 divergence classes. `check-spec.py` clean; `falsify-spec-checks.py` 4 of 4 red. Conformance clean; the tree is unchanged by `shasum`.

### Falsification (Task 6)

28 runs: FPH1 to FPH26, with variants of FPH20 (a, b) and FPH26 (a, b), plus the controller's FPH27. Each: a copy kept aside, the anchor counted, a `--no-incremental` Release build, restore by plain `cp` and `touch`, `shasum` equal after every restore, an unpatched run green last (23 Api, 5 Client, 71 Mcp facts). **27 red, 1 green.**

- Each row went red on the fact it was written to turn: the read rows (FPH1 to FPH9) on PH1, PH2, PH4, PH5, PH19; the ingest rows (FPH10 to FPH19) on PH6 to PH12, PH18 and PH22; FPH20a and b on PH17; FPH21 to FPH23 on PH13, PH14 and PH16; FPH24 and FPH25 on PH21 (each direction of the type's two spellings); FPH26a and b on PH20.
- **FPH27** (extra, the controller's): the route refuses a revision whose `prev` digest names an unserved post as `parent-withheld`. Red on `R8_72_ARevisionOfAWithheldPostIsStillAccepted`.
- **FPH14 stayed green.** Widening the check to a question or a finding changed nothing, because ADMIT refuses a question or a finding that carries a parent (`PostEnvelope.cs`, `curia/content/parent-not-allowed`) before any lookup; a diagnostic probe (removed) confirmed no question reached the block. The widened check is unreachable, so no test can see it; this is ruling 4's "T2 unreachable", for the question half as well as the finding half.
- **PH10's question fact was vacuous, and the controller fixed it.** `R8_72_AQuestionAndAFindingAreNotConsulted` asserted only "not `parent-withheld`", so it passed for the question ADMIT refused first. The controller rewrote it to assert `400 curia/content/parent-not-allowed`, saw it red with ADMIT's check narrowed (`Expected BadRequest, Actual Created`), restored it, and amended it into T0 (class 11/11 green). The revision fact asserts 201 and goes red for any refusal (FPH13 and FPH27).

### Live (Task 7)

The fixed build, a Release build with 0 warnings, a throwaway database, a human quarantine (an automated one cannot be produced out of band). **18 of 18 held.** The read says withheld on `curl`, `curia read`, `curia verify`, `curia_read`, `curia_verify` and, as before, `recheck`; an invented id and a vote's id stay `not-found` on every door; the quarantined answer reads as withheld; replies under a withheld parent through `curia` are `400 curia/ingest/parent-withheld` (exit 3, `post.accepted` 12 to 12 each; an answer under a withheld *answer* stays `parent-not-a-question`) and through `curia-mcp` are `NOT SENT: the post under parentPostId is withheld, so the Forum is not serving it. Nothing was signed.`; the clients refuse to revise a withheld post (`curia revision`, with and without `--prev`: exit 5, count unchanged; `curia_revise`: `NOT SENT` with the remedy) while the route accepts a hand-signed one (POSTED, 12 to 13); the `CUT` line says `"is withheld"` in both cases; the thread route is unchanged; `curia_flag` is unchanged; the order holds (403 for an under-tier caller, `parent-not-a-question`, `parent-not-found`, and a T0 over budget gets the budget refusal identically under a withheld and a served parent); a restore makes the read 200 and the reply post through both doors; `curia verify` of a fresh live post exits 0. Backdated posts read `NOT VERIFIED ... not yet valid at server_ts`, a harness artefact. The request fuzzer's ledger was not rerun here; the gates ran it empty.

- **Final whole-Api run:** 927 of 927, 03:47 to 04:15 CDT 2026-10-27 (before the final review's doc-only fixes; the errata and withheld classes re-run after them: Api 37, Client 12, Mcp 11).
- **Final review:** ready to merge with doc fixes; no code defect, no leak of a withheld post's content or author, no failed read treated as withheld. Its Important finding (the docs disagreed on whether G41 reverses G38 decision 7) was checked: G38 decision 7 had left servability open, so G41 decides it; CLAUDE.md's wording, the errata's R8.70 mutation line and the D73 plan's ruling 7 were corrected. Parked: `ForumResult.cs` spells `curia/posts/withheld` as a literal.
