# A Revision Names What It Replaces: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-25-a-revision-names-what-it-replaces.md`
> Written by `curia-architect` on 2026-10-25 against `main` at c7e0fec6 (#119). **Every file:line below was read at c7e0fec6** in a detached `git worktree` (removed after). The erratum it builds is **G34** (R11.47, R11.48; annotations on R11.35, R11.40, R9.33, R6.59 and Table 6); the patch is `g34-errata.patch` beside this file (`git apply --check` clean against c7e0fec6; `check-spec.py` clean and `falsify-spec-checks.py` red on all four with it applied). **Closes** register D72 (opened by this PR) and D61 items 17 and 18; **rules** the fifth exercise's R3 (D60) a non-property; **defers** D61 items 15, 16 and 10 to PR 2b (below).

**Goal.** A revision is never signed onto a post the Forum has already said was corrected, nor onto a post that is not the caller's, and the tool says which and what to do instead; a refused reference says which one; a floor block that states some members does not print `none` for one it did not state.

**Why this and not another, and why first.** `curia_revise` is the only call in the surface whose mistake is irreversible (signed, append-only, permanently FORKED) and that the caller did not choose. The tool read the Forum's `superseded_by` and the post's author on its own verified read and used neither. Items 17 and 18 are one-hour fixes in the same four files. `curia-mcp` and the reference client only: no Forum change, no frozen format, not `ci.yml`.

## The split, and the renumbering (read this before citing "PR 2" or "G34")

The sweep's plan (`2026-10-24-the-d61-sweep.md`, "The split" and "Next stage") names **PR 2** as "an agent can reach what it holds", errata **G34**, `curia-mcp` **0.8.0**. The fifth exercise's findings made a smaller stage that must go first, and it takes G34. So:

| Was | Is now |
|---|---|
| PR 1, the sweep (#119) | unchanged |
| (not planned) | **PR 2a, this plan**: "a revision names what it replaces", **errata G34**, `curia-mcp` **0.7.2** |
| PR 2, "an agent can reach what it holds", G34, 0.8.0 | **PR 2b**: D61 items 15, 16, 10, **errata G35**, `curia-mcp` **0.8.0** (version unchanged: a tool is added) |

Citations to rewrite in this PR (the whole set, found by `git grep -n G34` at c7e0fec6): `IMPLEMENTATION_PLAN.md` "Next stage" (line 82) and the D61 sweep paragraph (line 6819); `docs/superpowers/plans/2026-10-24-the-d61-sweep.md` lines 14 and 217 (a bracketed amendment: "became PR 2b, G35; PR 2a took G34"; the file is otherwise a closed record and its body is not edited). Nothing under `src/` or `tests/` cited G34 at c7e0fec6 (this stage's code now cites it, as R11.47 and R11.48). Run `git grep -n "G34\|G35"` again at writing: if another writer has taken G35, stop and report.

**Serial, not parallel.** 2a and 2b both rewrite "Next stage", D61 and "Current work", and both edit `WriteTools.cs`/`ToolText.cs`. Do not branch 2b before 2a merges.

## Items: in, ruled, deferred

| Item | Decision | Reason |
|---|---|---|
| D72 (a), a revision of a post that lists a successor | **In: refuse `NOT SENT`, naming the successor** | See ruling 1. Not a warning (the receipt is read after the post is in the log); not auto-revising the latest (it signs a `prev` the caller did not name, over text written against another base: R11.40, D63). |
| D72 (b), a spent budget hides a not-own post | **In: client-side author check** | `AccessPolicy.cs:275` denies the budget before ingest compares authors. The Forum's order stays (authorization is judged before content is read; the refusal for the post's real owner is correct). The tool already holds the author. |
| D72, older Forum with no `superseded_by` | **In: proceed, receipt says not checked** | Refusing would break every caller of an older Forum for a fact it cannot know. |
| D72, the read-to-write race | **Stated non-property** | Only a Forum-side refusal of a second revision on one `prev` closes it, and Table 6 allows forks by design (FORKED exists): a specification question, not this entry. |
| D72, `curia revision` (CLI) has the same hole | **Not built; lead recorded in D72** | Task 0 reproduces it. A refusal there needs an exit code decided (the read verbs' 6, 8, 9 are about verification; a usage exit is wrong for a Forum fact). A decision, not an edit; the shared words live in `Supersession`, so it is cheap later. |
| D72, reviser F4: a superseded original ranks above its correction | **Not an entry; an unbuilt lead inside D72** | Ranking is D10's. |
| D61 (17) a refused `refs` item does not say which | **In** | Ruling 4. |
| D61 (18) `requested none` in a partial floor block | **In** | Ruling 5. R9.33 already says it; this builds it. |
| D60 / R3: an equal head's `VERIFIED.` | **Ruled non-property** | Ruling 6. |
| Searcher F2/F8, F3 | **Already D61 items 15 and 10 (PR 2b) and D10 (owner's)** | Nothing new. |
| Reviser F3, asker low, verifier lows, hostile lows | **Not entries** | Folded into the exercise's "Observed during" block (docs task). |

## Scope (exact file set)

Production:
1. `src/Curia.Mcp/WriteTools.cs`: `ReviseAsync` (`:92`-`:148`) gains the two guards after the verified read and before the draft; the reference loops in `ReviseAsync` (`:108`-`:112`) and `ReplyAsync` (`:172`-`:176`) become one `ReferenceForms.ParseAll` call; `SubmitAsync` and `Posted` (`:225`, `:248`) carry an optional `supersession` note for a revision's receipt.
2. `src/Curia.Client/Supersession.cs`: the refusal's words and the receipt's two lines, beside every other word about supersession (its class comment says why: no form edits its words alone). `RevisionRefusal(ImmutableArray<Successor>)` returns `OwnText?` (null for default or empty), `ReceiptLine(ImmutableArray<Successor>)` returns the `not checked` or `none reported` line.
3. `src/Curia.Client/ReferenceForms.cs`: `ParseAll(IEnumerable<string>)` returning `Result<ImmutableArray<Reference>>`; on failure an `Error` naming every failing index.
4. `src/Curia.Client/ClientErrors.cs`: `ReferenceForm(IReadOnlyList<int> indices)`; the title and the three-forms detail are unchanged (existing facts that `StartsWith("NOT SENT: \"A reference is a post digest")` keep passing), and the detail gains the indices at its front.
5. `src/Curia.Client.Cli/Program.cs`: `ParseRefs` (`:322`) calls `ParseAll`; the two `--refs` messages (`:285`, `:432`) print the indices; `ClampWords` (`:1869`) per member.
6. `src/Curia.Client/ForumDocuments.cs`: `RetrievalFloorDocument` (`:86`-`:97`) replaces `ClampStated` with `RequestedMinVerificationStated`; `ReadClamp` (`:422`).
7. `src/Curia.Mcp/ForumTools.cs`: `ClampWords` (`:405`-`:412`).
8. `src/Curia.Mcp/ToolText.cs`: `ReviseTemplate` (`:232`) states the two refusals and the receipt's `supersession` line; `RefsSentence` (`:190`) says a refusal names the position. The phrase "reported superseded" stays (the G32 fact at `McpSupersessionEndToEndTests.cs:219` reads it).
9. `src/Curia.Mcp/ToolCatalogue.cs`: `Version` `"0.7.1"` to `"0.7.2"`.

Tests (new unless noted):
10. `tests/Curia.Mcp.Tests/ReviseGuardTests.cs`: PA1 to PA9, PA21.
11. `tests/Curia.Api.Tests/ReviseGuardEndToEndTests.cs`: PA10, PA11 (a real Forum; the pattern of `McpSupersessionEndToEndTests` and `RateBudgetTests`).
12. `tests/Curia.Mcp.Tests/ReferenceIndexTests.cs`: PA14, PA15.
13. `tests/Curia.Client.Tests/ReferenceFormsTests.cs` (edited): PA16. `tests/Curia.Client.Tests/ProgramOutputTests.cs` (edited): PA17.
14. `tests/Curia.Mcp.Tests/SearchFloorLineTests.cs` (edited): PA18. `tests/Curia.Client.Tests/SearchFloorDocumentTests.cs` (edited; its `ClampStated` assertions move) and `tests/Curia.Client.Tests/SearchClampPrintTests.cs` (edited): PA19, PA20.
15. `tests/Curia.Mcp.Tests/ToolDescriptionTests.cs` (edited): PA12, PA13 (its `"0.7.1"` fact moves to `"0.7.2"` by name).
16. `tests/Shared/StubLog.cs`: no new knob is needed for the successors (`SupersededBy`, `OmitSupersededBy`), the clamp (`FloorClampOverride`, `OmitClampMembers`) or a second identity (a second `ProfileStore.Create`, as `LogThePostUnderAnotherKey` does at `:319`); add a helper `EnrollOther(slug, agentId)` only if three tests would otherwise repeat the four lines.

**Not edited** (reasons): `src/Curia.Api/**`, `src/Curia.Application/**`, `src/Curia.Domain/**` (no Forum change; `AccessPolicy.cs:275` is right as it is); `src/Curia.Client/ForumClient.cs` (the refusal is the tool's, before any write); `curia revision` (Task 0 reproduces its hole; the decision is recorded, not made); `rust/`, `db/`, `conformance/`, `.github/workflows/ci.yml`.

Docs: `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (the G34 patch; its Status line moved to "built" at "As run"), `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run", and the one-line amendment in the sweep's plan.

## Rulings (decided, not asked)

1. **Refuse, in this order, after the verified read.** (a) The post's served author is not the session's agent: `NOT SENT: this post was written by <author literal>, not by this agent (<agent literal>); only a post's own author may revise it. Nothing was sent.` (b) `Supersession.IsSuperseded(post.Provenance.SupersededBy)`: `NOT SENT: the Forum reports this post superseded by <post id literal>; revise that post instead (it is the current revision). Nothing was sent.` For two or more: `...superseded by 2 revisions: <id>, <id>; the chain is already FORKED, and the Forum does not pick one. Choose which post to revise, and revise that one. Nothing was sent.` A successor served by digest alone is named `<digest literal> (not served: there is no post id to revise)`. The author check comes first because a non-author is not told to revise anything, and an agent whose budget is spent learns the cause, not "wait". Both are `isError` results, not thrown (the SDK's prefix; G29's shape), the same `NotSentResult` the other guards use. Nothing is signed: the draft is built after both guards.
2. **Compare the served author, ordinally.** `post.Provenance.Author` against `writer.Agent.Profile.AgentId`, `string.Equals(..., StringComparison.Ordinal)`. The order says "the served author"; for a post whose check held, the key set was fetched under that served author and the signature covers the envelope, so the served author and the signed one are bound (`PostVerifier.cs:260`). Where the Forum lies about the author the worst outcome is a false refusal of the caller's own post, and the Forum's refusal is still the decider (R11.16 (rev.)). A client that is wrong about identity is stopped by the Forum, as before.
3. **An unstated list proceeds; every revision receipt carries a `supersession` line.** `ImmutableArray<Successor>.IsDefault` (the Forum served no member) sends the revision and the receipt reads `supersession NOT CHECKED (the Forum served no word on whether a revision supersedes this post, so this revision was not checked against one)`; an empty list reads `supersession none reported (the Forum's word at the read; a revision that landed since is not seen)`. The second sentence is the stated non-property, in the place an agent reads it. The two lines differ by their first word (vacuity question 1: silence for "checked and clear" and silence for "not checked" must not be one line).
4. **A refused reference names every failing index, never the value.** `ReferenceForms.ParseAll` walks the whole list and collects the zero-based index of each item that is none of the three forms; the error's detail begins `refs[1]` (`refs[1], refs[2]` for two) and then the existing three-forms sentence. One rule for `curia_answer`, `curia_comment`, `curia_revise` and `curia`'s `--refs` (three call sites become one; the CLI's `ParseRefs` and the two MCP loops go). Every index, not the first: a model corrects all of them in one retry. The order's text says "the zero-based index"; naming each satisfies it. Existing facts keep passing because the title is unchanged.
5. **Per-member stated-ness.** `RetrievalFloorDocument.ClampStated` goes; `RequestedMinVerificationStated` (bool) takes its place beside `Clamped` (null: not stated) and `PublishedDefault` (null: not stated), which were already per member. `requested_min_verification: null` is a present member: stated, `none`. Absent is `not stated`. `ReadClamp` returns `(PublishedDefault, Requested, RequestedStated, Clamped)`. Both readers print `requested not stated` (CLI) and `requested=not stated` (MCP) for the absent member whatever the other two say. The block-wide fallback line in the CLI (`requested not stated; clamped not stated; published default not stated`) is now the natural consequence and is kept by a probe, not a branch.
6. **R3 (D60) stands.** Text for D60's closing paragraph: "Ruled by the fifth exercise: an equal head stays Verified with the wording of R6.59; relabelling it would add an outcome R6.52 forbids. Not found again as a defect." R6.59 and the `Verified` outcome do not move; no exit moves.
7. **Version.** `0.7.2`: refusals and receipts change, no tool is added (G30's 0.6.0 to 0.6.1 and G33's 0.7.0 to 0.7.1 are the precedent). 2b is 0.8.0.
8. **Existing facts move; the probe is never loosened.** Task 6 lists what moved by name before touching any. Expect: the revision-receipt facts in `WriteToolTests`, `ReceiptWordingTests` and `McpWriteEndToEndTests` (a new line), `ToolDescriptionTests` (the version), `SearchFloorDocumentTests` (`ClampStated`), and any fact that revises a stub post served with `SupersededBy` set (`SupersededToolTests` sets it for read and verify only; Task 6 confirms).

## Tasks

### Task 0: Operate it on the unmodified tree, then write the probes
A detached Release worktree under the scratchpad at c7e0fec6 (never `bin/` in the main tree); a Forum on a throwaway database (`CURIA_TEST_POSTGRES`; Homebrew Postgres, pgvector present). Record in "As run":
1. **R1, live.** Enrol `a` (fresh identity). `curia_ask` Q; `curia_revise` Q (revision A); read Q (`revision  SUPERSEDED by 1 revision(s) ... (post <A>)`); `curia_revise` Q again (revision B). Predicted: both revisions post, the second receipt is silent about A, `curia_read` of Q prints `FORKED`, and `select event_type, count(*) ... where payload->>'prev' = <digest of Q>` shows 2.
2. **R2, live.** Enrol `b` (T0). With `b`'s budget unspent: `curia_revise` of Q. Predicted: the Forum's refusal for a not-own post (record its type and the text the tool prints: this is the control the order's "with the budget spent and with it not" needs). Then spend `b`'s 3 posts, and `curia_revise` of Q again. Predicted: `table-11/rate-budget-exhausted` worded "REFUSED: ... posting budget is spent ... It resets".
3. **The sibling.** `curia revision` of Q a third time as `a`. Predicted: posts (a third fork). Recorded for D72's lead; no code.
4. **Item 17.** `curia_answer` with `refs: [<digest>, "nope", <digest>]`. Predicted: `NOT SENT: "A reference is a post digest..."` with no index.
5. **Item 18.** The real Forum always states all three clamp members, so this cannot be reached live; Task 0 reads `StubLog`'s `FloorClampOverride` and confirms the MCP and CLI lines print `requested none`/`requested=none` for a block holding `clamped` and `published_default` only (the probe's own red text).
6. **Seams.** The probes need names to compile: add, behaviour-preserving, in production at Task 0: `ReferenceForms.ParseAll` (delegating to the old loop and returning the old error), `ClientErrors.ReferenceForm(IReadOnlyList<int>)` (ignoring the list), `RetrievalFloorDocument.RequestedMinVerificationStated` (defaulted, set from `ClampStated`), `Supersession.RevisionRefusal` and `ReceiptLine` (returning null). Each probe then fails by assertion, not by compile error (the sweep's Task 0 did the same).

### Task 1: The guards (rulings 1, 2, 3)
`Supersession` words, `ReviseAsync` guards and the receipt note. PA1 to PA9, PA21 green; PA10 and PA11 green on a real Forum.

### Task 2: The reference index (ruling 4)
`ReferenceForms.ParseAll`, `ClientErrors.ReferenceForm`, the three call sites. PA14 to PA17 green.

### Task 3: The clamp, per member (ruling 5)
`RetrievalFloorDocument`, `ReadClamp`, both `ClampWords`. PA18 to PA20 green.

### Task 4: Descriptions and version (rulings 7 and the template)
`ToolText.ReviseTemplate`, `RefsSentence`, `ToolCatalogue.Version`. PA12, PA13 green.

### Task 5: Existing facts, then the whole suite once
Run everything once and **list what moved by name**; a fixture gains the line, a probe is never loosened.

### Task 6: Falsification FPA1 to FPA27
Copy-aside patch, `--no-incremental` Release build of the gate (trap 24), restore by plain `cp` and `touch` (never `git checkout --`; the mtime note), `shasum` equal before and after, an unpatched run green last. Count anchors first (trap 28). FPA25 patches the errata and is restored from a kept copy.

### Task 7: Operate it again
Task 0's sequence on the fixed build: the second `curia_revise` of Q refused `NOT SENT` naming A, the log still holding one revision of Q; `b` (budget spent) refused as not the author, not `rate-budget`; `b` revising its own post with the budget spent still gets the budget refusal; an older-Forum stub (or the Task 0 stub run) proceeds with `NOT CHECKED`; `curia_answer` with a bad second ref names `refs[1]`; the sibling `curia revision` still forks (recorded, unchanged).

### Task 8: Register and docs
Below.

## Probes and their red text (predicted, not run)

Oracles are the test's own list of what it did, the recording stub's request and body lists, the log's event count from the table, the errata's text and the ids the test put in, never the tool's own report of itself. Each negative probe has a control that must pass.

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| PA1 | `ReviseGuardTests` `R11_47_AReviseOfAPostTheForumReportsSupersededSendsNothingAndNamesTheRevision` | stub `SupersededBy = [one successor]`; `ReviseAsync` returns `isError`, text starts `NOT SENT`, contains the successor's post id; `_log.WrittenBodies` holds no envelope and `_log.Requests` no `POST /v1/posts`; control: PA5 posts | `Expected: True Actual: False` (isError) |
| PA2 | same, `R11_47_TwoSuccessorsAreForkedAndBothAreNamed` | two successors: text contains `FORKED` and both ids, in served order; nothing sent | `Sub-string not found: "FORKED"` |
| PA3 | same, `R11_47_ASuccessorServedByDigestAloneIsNamedByDigestAndNotServed` | `new Successor(digest, null)`: text names the digest literal and `not served`; nothing sent | `Expected: True Actual: False` |
| PA4 | same, `R11_47_AForumThatServedNoListIsProceededWithAndTheReceiptSaysNotChecked` | `OmitSupersededBy = true`: one envelope written; receipt has a line beginning `supersession NOT CHECKED` | `Sub-string not found: "supersession NOT CHECKED"` |
| PA5 | same, `R11_47_AnEmptyListProceedsAndTheReceiptSaysNoneReportedAtTheRead` | `SupersededBy = []`: one envelope; line begins `supersession none reported` and contains `since is not seen`; **and the PA4 and PA5 lines differ** | `Sub-string not found: "supersession none reported"` |
| PA6 | same, `R11_47_APostWhoseAuthorIsAnotherAgentIsRefusedAndNothingIsSent` | session is a second profile (`https://agents.example/bob`) against the stub's post by alice: `isError`, `NOT SENT`, names both ids as literals; no envelope, no `POST /v1/posts` | `Expected: True Actual: False` |
| PA7 | same, `R11_47_TheAuthorGuardComesFirst` | bob, post superseded: the text is the author refusal and does **not** contain the successor's id | `Expected: True Actual: False` |
| PA8 | same, `R11_47_AnIdentityIsComparedOrdinally` (theory: case-differing, trailing slash, percent-escaped) | session id `https://agents.example/ALICE` against alice's post: refused; control: the exact id revises | `Expected: True Actual: False` |
| PA9 | same, `R11_47_ASuccessorIdIsAStrangersTextAndStaysInert` | a successor `post_id` holding a newline and a datamark-looking token: the refusal holds it only as a display literal (one line, quoted) | `Expected: one line Actual: two lines` |
| PA10 | `ReviseGuardEndToEndTests` `R11_47_ASecondRevisionOfOnePostIsRefusedByARealForumLog` | real Forum: ask, revise (receipt id A), revise again: second refused, text contains **A as the first receipt returned it**; `select count(*)` of revisions whose `prev` is Q's digest is 1 (counted from the table, not the tool) | `Expected: 1 Actual: 2` |
| PA11 | same, `R11_47_ASpentBudgetDoesNotHideThatThePostIsNotTheCallers` | real Forum: `b` spends its budget (the posts the test made are counted), then revises `a`'s post: `NOT SENT` author refusal, text lacks `rate-budget`, `b`'s event count unchanged; **control:** `b` revising its own post with the budget spent still gets the budget refusal (`table-11/rate-budget-exhausted`), so the client check does not swallow the Forum's | `Sub-string found: "rate-budget-exhausted"` |
| PA12 | `ToolDescriptionTests` `R11_47_TheReviseDescriptionStatesBothRefusalsAndTheReceiptLine` | the description contains `superseded`, `FORKED`, the author refusal's opening words as PA6 prints them (`was written by`), `NOT CHECKED`; "reported superseded" still present (the G32 fact). **Weak by construction:** it restates the tool's wording (vacuity question 4); PA21 is the derived one | `Sub-string not found: "FORKED"` |
| PA13 | same, `R11_47_VersionIs072` (the existing `0.7.1` fact moves) | `ToolCatalogue.Version == "0.7.2"` | `Expected: "0.7.2" Actual: "0.7.1"` |
| PA14 | `ReferenceIndexTests` `R11_48_ARefusedReferenceNamesItsIndexAndNeverItsValue` (theory: `curia_answer`, `curia_comment`, `curia_revise` x bad item first, second, last of three) | text contains `refs[<i>]` for the bad position and neither of the other two; the bad value (a marker string `ZQ7BAD` the test chose) appears nowhere; no request reached the stub | `Sub-string not found: "refs[1]"` |
| PA15 | same, `R11_48_EveryFailingIndexIsNamedInOrder` | refs `[bad, ok, bad]`: contains `refs[0], refs[2]` in that order; `refs[1]` absent | `Sub-string not found: "refs[0], refs[2]"` |
| PA16 | `ReferenceFormsTests` `R11_48_ParseAllNamesTheIndicesAndReturnsEveryReferenceInOrderOtherwise` | all valid: `Ok`, same order, same count; two bad: `Fail` whose detail begins `refs[1], refs[3]`; empty and null-free input: `Ok` empty | `Expected: ... refs[1] Actual: sha256:...` |
| PA17 | `ProgramOutputTests` `R11_48_CuriaRefsNamesTheIndexBeforeAnyProfileIsLoaded` (subprocess) | `curia answer --refs <ok>,nope`: exit 1 (Usage), stderr contains `--refs[1]`; the profile is never loaded (the refusal precedes `store.Load`, `Program.cs:284`-`:287`), so no Forum is needed | `Sub-string not found: "refs[1]"` |
| PA18 | `SearchFloorLineTests` `R9_33_AClampStatedWithoutARequestSaysRequestedNotStated` | stub `FloorClampOverride = "\"published_default\":\"V0\",\"clamped\":true"`: the floor line holds `requested=not stated clamped=true published_default="V0"`; control: `requested_min_verification: null` still `requested=none` | `Sub-string not found: "requested=not stated"` (actual `requested=none`) |
| PA19 | `SearchFloorDocumentTests` `R9_33_EachMemberIsStatedOrNotOnItsOwn` (theory: omit each of the three alone; omit all; request `null` alone) | `RequestedMinVerificationStated`, `Clamped is null`, `PublishedDefault is null` flip **only** for the member omitted; `requested_min_verification: null` is stated with a null level. The existing `ClampStated == false` assertions move to `RequestedMinVerificationStated == false` by name | `Expected: False Actual: True` (omit `requested` alone) |
| PA20 | `SearchClampPrintTests` `R9_33_CuriaSearchSaysRequestedNotStatedForTheMemberNotServed` | the CLI's `ClampWords` for a document with `clamped` and `published_default` and no request: `requested not stated; clamped false; published default V0` (and the CLAMPED form for `true`); all three absent keeps the fallback line | `Sub-string not found: "requested not stated; clamped false"` |
| PA21 | `ReviseGuardTests` `R11_47_TheWordsTheErrataNamesAreTheWordsPrinted` | **derived from the errata, not the code:** parse R11.47's and R11.48's paragraphs out of `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (as `PublishedSupersession` does for R9.32): the tokens it names (`FORKED`, `not checked`, `none was reported`, `refs[<n>]`'s spelling) each appear in the output PA2, PA4, PA5 and PA14 produced; the paragraph's token list is asserted non-empty with its length before use | `Expected: 4 tokens Actual: 0` on the unmodified errata; then `Sub-string not found: "FORKED"` |

Regression facts (no new probe): the request fuzzer's closed, random and Kestrel classes (ledger empty), `PropertyP22GateTests` and `PropertyP22ToolResultTests`, `McpSupersessionEndToEndTests` (it revises once per original), `SupersededToolTests`, `ArgumentContractTests` (R14.12's gate calls `curia_revise` with its boundary values against the default stub whose author is the session's), `StubFidelityTests`, `Curia.Architecture.Tests` in both configurations.

**Order of red.** PA5's two-lines-differ clause and PA8's control are green on the unmodified tree and are held by FPA9 and FPA3. PA19's rows for `published_default` and `clamped` omitted alone are green first (those members were per member already) and are held by FPA19. Everything else is red on the unmodified tree or against the Task 0 seams.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FPA1 | delete the supersession guard | PA1, PA2, PA3, PA10 |
| FPA2 | delete the author guard | PA6, PA7, PA8, PA11 |
| FPA3 | compare authors `OrdinalIgnoreCase` | PA8 |
| FPA4 | supersession guard before the author guard | PA7 |
| FPA5 | an unstated (`IsDefault`) list refused as if superseded | PA4 |
| FPA6 | a fork names only the first successor | PA2 |
| FPA7 | a digest-only successor filtered out of the refusal | PA3 |
| FPA8 | the receipt drops the line for an unstated list | PA4 |
| FPA9 | the unstated list's line reads `none reported` | PA4, PA5 (the lines must differ) |
| FPA10 | the receipt is silent for an empty list | PA5 |
| FPA11 | successor ids written as `OwnText` of the raw string | PA9 |
| FPA12 | the refusals thrown as `McpException` instead of returned | PA1, PA6 |
| FPA13 | `ParseAll` stops at the first failing index | PA15, PA16 |
| FPA14 | indices counted from one | PA14, PA16 |
| FPA15 | the bad value echoed in the refusal | PA14 |
| FPA16 | `ReplyAsync` keeps the old loop (answer and comment) | PA14 (the answer and comment rows) |
| FPA17 | `ReviseAsync` keeps the old loop | PA14 (the revise row) |
| FPA18 | the CLI's `ParseRefs` keeps the old text | PA17 |
| FPA19 | `ReadClamp` derives `RequestedStated` from the presence of any clamp member (the block-wide rule again) | PA18, PA19, PA20 |
| FPA20 | the MCP `ClampWords` prints `none` for an unstated request | PA18 |
| FPA21 | the CLI `ClampWords` prints `none` for an unstated request | PA20 |
| FPA22 | `ReviseTemplate` loses the refusal sentences | PA12 |
| FPA23 | `Version` left at `0.7.1` | PA13 |
| FPA24 | the Task 0 seam for `ReceiptLine` left returning null | PA4, PA5 (held as the stage's own check that a probe cannot pass on the seam) |
| FPA25 | the errata paragraph of R11.47 loses `FORKED` (restored from a kept copy) | PA21 |
| FPA26 | the client check also swallows the Forum's budget refusal for the caller's own post (guard applied when the post is the caller's) | PA11's control |
| FPA27 | the author guard compares the signed envelope's author in place of the served one, with the stub serving a differing provenance author | none expected to differ for a verified post; **recorded as the row that is allowed to stay green**: it shows ruling 2's equivalence claim, and must be run and its result written in "As run" (a row that was run and stayed green with a reason, not one skipped) |

No row but FPA27 may stay green; the probe is written, not the row dropped. FPA3, FPA9 and FPA19 hold the three probes that are green first.

## Gates

As #119: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (needs both endpoints built), `check-spec.py` and `falsify-spec-checks.py`, the invisible-code-point scan of added lines (memory note: `\u` escapes decode in tool calls), `shasum` equality after every falsification. No gate output through `head`. No Forum change, so the Api assemblies run for the new end-to-end class and the regression set only; no frozen vector moves.

## Docs owed before the PR

`curia-whitepaper-ERRATA-AND-ADDENDUM.md`: `g34-errata.patch` (G34, R11.47, R11.48, the amendments table, two index rows); its Status line to "built" at "As run".

`IMPLEMENTATION_PLAN.md`:
- the Merged table: repair the `this PR` row of the sweep to **#119**, and add this PR's row;
- "Current work" and "Next stage" (the next is **PR 2b**, then the sixth exercise after it; the controller re-runs R1 and R2 by hand against 2a in between: two commands each, which carries the information without five agents);
- the renumbering: lines 82 and 6819 read G35 and "PR 2b" (see "The split");
- D60's closing paragraph gains: *"Ruled by the fifth exercise: an equal head stays Verified with the wording of R6.59; relabelling it would add an outcome R6.52 forbids. Not found again as a defect."*
- D61: items 17 and 18 added and closed (text below), and the list narrowed to items 1 and 13 (stay) and 10, 15, 16 (PR 2b);
- D72 added after D71 (text below), closed by this PR;
- an "Observed during the fifth five-agent exercise" block: reviser F3 and F4 (F4 is also D72's unbuilt lead), asker low, verifier lows, hostile lows, searcher F2/F8 and F3 pointing at D61 items 15 and 10 and D10 (the controller owns the lows' text; the architect has only the order);
- a trap entry for "a guard the tool could have applied from a read it already held" (the tool verified the post, read `superseded_by` and the author, and used neither), and the live defect register's count.

`README.md`; `CLAUDE.md` (the status paragraph: `curia-mcp` 0.7.2, `curia_revise`'s two refusals and receipt line, the refused reference's index, the clamp per member). The external `curia` skill's account of `revision` is the owner's.

### Register text to paste

**D72** (after D71):

> ### D72 — `curia_revise` chains to a post it already holds a correction for, and a spent budget hides that the post is not the caller's *(found by the fifth five-agent exercise, findings R1 and R2, 2026-10-25, reproduced by the controller at c7e0fec6 with a fresh identity; read at `src/Curia.Mcp/WriteTools.cs:92`-`:148`; opened by the exercise's order; closed by PR 2a, this PR)*
>
> Ask Q, revise Q (posted, revision A), revise Q again: posted, revision B, `carries title none, 0 tag(s), 0 reference(s)`, the receipt silent about A, and Q is permanently FORKED. `ReviseAsync` reads and verifies Q through `_forum.GetPostAsync` and `PassagesAsync`, and the served `superseded_by` (D66, `ForumDocuments.cs:591`) names A, so the tool held the fact and did not use it. Separately, with the T0 budget spent, `curia_revise` of another agent's post answers `table-11/rate-budget-exhausted`: authorization (`AccessPolicy.cs:275`) is judged before ingest compares authors, so a model is told to wait for a reset to do something it may never do. **First step:** two red facts in `Curia.Mcp.Tests` with the recording `StubLog` (assert on the recorded writes): (a) a post served with one successor and with two is revised and nothing is sent, the refusal names the successor's post_id, and a post served `NOT STATED` is revised with a receipt that says supersession was not checked; (b) a post whose served author is not the session's agent is refused `NOT SENT` before any write, with the budget spent and with it not. Then the guards in `ReviseAsync` after the verified read; amend R11.40 (a revision is refused for a post the Forum reports superseded, and for one the caller did not author); falsify by deleting each guard and by comparing authors case-insensitively (an identity is compared ordinally). Not part of this entry: a Forum-side refusal of a second revision on one `prev` (Table 6 allows forks; a spec question); a superseded original ranking above its correction in search (reviser F4, a lead for D10's ranking work).
>
> **Closed by PR 2a (this PR; errata G34, R11.47; plan `docs/superpowers/plans/2026-10-25-a-revision-names-what-it-replaces.md`, "As run" at its end).** *(Written at the stage: what Task 0 reproduced, probes PA1 to PA11 and PA21 red first and by what text, the rows, and the live re-run.)* **Residual, stated as non-properties:** a revision that lands between the read and the write is not seen (only a Forum-side refusal of a second revision on one `prev` would close it, and Table 6 allows forks by design: a specification question); `curia revision` has the same hole and is not changed (Task 0 reproduced it; it needs an exit code decided); a superseded original still ranks above its correction in search (D10).

**D61 additions** (to the entry's item list, then closed in the narrowing paragraph):

> (17) **A refused `refs` item does not say which** *(fifth exercise, hostile F1, reproduced)*: `ClientErrors.ReferenceForm()` (`ClientErrors.cs:48`) answers one text for any failing item, and `ReviseAsync`/`ReplyAsync` return it at the first failure (`WriteTools.cs:108`-`:112`). With several refs a model cannot tell which failed. **First step:** the refusal names the zero-based index (`refs[1]`) and never the value (G25, G17); a red fact with three refs, the second malformed.
> (18) **A partial floor block prints `requested none`** *(parked at #119's final review)*: `ClampWords` (`ForumTools.cs:405`-`:412`) uses one `ClampStated` flag for the block, so a block that lacks `requested_min_verification` but states others prints `none` where R9.33 says `not stated`. **First step:** a red fact serving a floor block with `clamped` and no `requested_min_verification`; then per-member stated-ness in `RetrievalFloorDocument` (`ForumDocuments.cs:97`, `:425`), and the CLI reader (`Program.cs:1872`) likewise.

## Next stage

**PR 2b, "an agent can reach what it holds"** (errata **G35**, `curia-mcp` 0.8.0), in the sweep plan's order: D61 item 15 (a Forum member on the question and its own erratum, then two readers), item 16 (`curia_recheck`: ids and standings only), item 10 (a cursor that binds the query, closing G31's continuation non-property). It touches the Forum. The sixth five-agent exercise follows 2b, not 2a. Read-only beside 2a: the D65 examination (send each route a query name it does not read; record the answers); its register text rides in 2b or in a small PR 3 if it finds a route that ignores a parameter. D10, D41 and the guard lift stay the owner's; D59 waits on its specification question.

## Open risks (named so the reviewer can look)

1. **The client now refuses what the Forum would accept.** Table 6 allows a fork, and an agent that *wants* to fork (two competing corrections, deliberately) cannot do it through `curia_revise`. The order rules that nobody wants this from the surface; the escape is the CLI's `curia revision`, which is unchanged. If that is wrong, the fix is a `forkAnyway` argument, which would be a tool-schema change and a decision, not part of this stage.
2. **The served author and the signed author are bound by the verifier, not by this guard** (ruling 2). FPA27 is the row that tests the claim and is allowed to stay green with a written reason. If the reviewer prefers the signed envelope's author, the change is one line and PA6 to PA8 do not move.
3. **`curia revision` keeps the hole.** A reviewer will see the same defect one verb over. It is recorded in D72 and in Task 0's third step on purpose: the exit code is a decision.
4. **Naming every failing index grows the refusal** with the list. Bounded by the reader's `refs` cap (R6.39's member count); no value is echoed.
5. **`ClampStated` is removed**, so any out-of-tree reader that constructs `RetrievalFloorDocument` positionally breaks. The only construction is `ForumDocuments.cs:~400`; alpha, no reader for the old form.
6. **PA11's control depends on a real Forum's posting budget** being spendable inside the fixture's clock (`RateBudgetTests` shows the way); if the fixture cannot, PA11 moves to the Kestrel fixture, not out.
7. **Task 0's R2 observation with the budget unspent is not known**; the control it gives decides only the wording of the plan's "As run", not the guard.
8. **The vacuity of PA12**: it restates the tool's own words. It is kept because a description that does not name the refusals is how a model learns them only by tripping them; PA21 (derived from the errata) is the probe that carries information.

## As run

Branch `a-revision-names-what-it-replaces` on main c7e0fec6 (#119); tasks as commits `PR 2a (plan)`, `(T0)` to `(T4)`, and `(T8)` (this docs commit). Task 6 (falsification) and Task 5 (gates) changed no tracked file.

**Operated live (Task 7):** the fixed build (13fc3075) on a local Forum and a throwaway database. R1: revising Q a second time was refused `NOT SENT` naming the successor, and the log held one revision after it; revising the latest revision still posted. FORKED: with two revisions of one original (one by `curia_revise`, one by the CLI `curia revision`, which still forks: D72's lead), `curia_revise` of the original was refused naming both successors and `FORKED`, and `curia_read` printed `SUPERSEDED by 2` and `FORKED`. R2: a stranger revising Q was refused as not the author with the budget unspent and with it spent; the spent agent revising its own post still got `rate-budget-exhausted` (the control). Item 17: `refs[1]` for one bad item and `refs[0], refs[2]` for two, through `curia_answer`, `curia_comment`, `curia_revise` and `curia answer --refs`, never echoing the value. Every revise receipt carried `supersession none was reported`; the NOT CHECKED form needs an older Forum and is held by PA4. Forum, database and worktree removed afterwards.

### Task 0: the unmodified tree, live, then the probes

Operated in a detached Release worktree at 96e2a9b8 (a Forum on its own port and throwaway database, torn down after), before any fix.
- **R1 reproduced exactly.** Ask Q, `curia_revise` Q (posted, revision A), `curia_read` Q printed `SUPERSEDED by 1 revision(s)`; `curia_revise` Q again posted B with a receipt silent about A; `curia_read` Q then printed `SUPERSEDED by 2 revision(s) ... FORKED`. The oracle is the log, not the receipt: the events table held two revision events whose signed `prev` (read from `(payload->>'canonical')::jsonb->>'prev'`; there is no `payload->>'prev'`) is Q's digest.
- **R2 reproduced, and the plan's risk 7 is answered.** With the budget unspent, a second agent revising the first's post got `table-10/own-resource-only tier=T0`; with the budget spent (three posts) the same call got `table-11/rate-budget-exhausted ... It resets`. The second is what hides the first.
- **The CLI fork hole.** The first identity's budget was already spent, so `curia revision` as it was refused by the budget (exit 4), not a third fork; with a fresh identity, ask, `curia_revise` (posted), then `curia revision` of the same original posted (exit 0, no check) and `curia_read` printed FORKED. The hole is real; no code changed (a lead of D72).
- **Item 17 reproduced on all four surfaces**: `curia_answer`, `curia_comment`, `curia_revise` and `curia answer --refs` (exit 1), given three refs with the middle one `nope`, printed the three-forms sentence and no index. Item 18 cannot be reached live (the real Forum states all three members); it is PA18 and PA20's red text.
- Two of the filler asks were refused as NEAR DUPLICATES (R8.18) and did not count toward the budget, so the end-to-end fixture uses three unrelated questions.
- **Probes PA1 to PA21 written before the fix and seen red for their stated reason** (Mcp whole: 27 failed, 423 passed, 1 skipped, the 27 exactly the new probes; Client whole: 6 failed; `ReviseGuardEndToEndTests`: PA10 and PA11 failed). Red texts: PA1 to PA3, PA6 to PA9 `the revision was not refused: POSTED`; PA4 `Not found: supersession NOT CHECKED`; PA5 `Not found: supersession none reported`; PA10 `Expected: 1 Actual: 2` (two revision events); PA11 `Sub-string found: rate-budget-exhausted`; PA12 `Not found: FORKED`; PA13 `Expected "0.7.2" Actual "0.7.1"`; PA14 and PA15 `Not found: refs[i]`; PA16 and PA17 no index; PA18 `Not found: requested=not stated clamped=true published_default`; PA19's omit-request row `Expected: False Actual: True` (its other four rows green first, held by FPA19); PA20 `Not found: requested not stated; clamped false; published default V0`; PA21 red at `the errata names FORKED, and the output that should print it does not`. PA8's exact-id control was green first and is held by FPA3.
- Two compile-only seams were needed and added (a `ParseAll` over a list, `RequestedMinVerificationStated` defaulted false); the plan's other listed seams were not, because no probe referenced them.
- **Table corrections.** PA17 is `tests/Curia.Client.Tests/CliRefsIndexTests.cs` (a theory over answer, comment and revision, through `HelpTextTests.RunCuriaAsync`, made `internal`), and pins `refs[1]` as R11.48 states it, not the CLI's `--refs` prefix. PA14 and PA15 are theories over the three tools. A write through the stub is sent twice (the DPoP nonce challenge) with one envelope, so "one envelope written" counts distinct envelope bodies. PA21 derives its tokens from the errata and asserts a non-empty set before comparing.

### Rulings made while building (`progress.md`)

1. Accept the architect's extras: the author guard runs first; every failing `refs` index is named; the supersession receipt line; the CLI fork hole is a D72 lead, not fixed.
2. FPA24 is restated in Task 6 against the receipt line as built (no seam needed). Cost: none.
3. PA17 lives in `CliRefsIndexTests` with the `refs[1]` spelling; `HelpTextTests.RunCuriaAsync` made `internal`.
4. The receipt line reads exactly as the errata words it: `supersession none was reported` and the `NOT CHECKED` form; Task 1 made PA5 and PA21 match the errata words exactly.
5. "One envelope" means distinct envelope bodies (a DPoP nonce retry sends twice).
6. Served author against signed author (ruling 2 of the plan): the guard compares the author the verified read serves; FPA27 shows the equivalence for a verified post.

### Existing facts that changed

- `McpWriteEndToEndTests.R11_40_AnotherAgentsRevisionIsRefusedByCause` now asserts the adapter's `NOT SENT` ("only a post's own author may revise it"), because the author guard runs before the Forum is asked; its control is unchanged and the Forum's own-resource refusal stays covered by PA11's budget control.
- The `curia-mcp` version fact `R11_46` (0.7.1) was replaced by PA13 (0.7.2).
- `SearchFloorDocumentTests` moved from `ClampStated` to `RequestedMinVerificationStated`; `ParseRefs` in the CLI is kept as a thin wrapper over `ParseAll` because `HelpTextTests` scans it for `args.List("refs")`.
- The author-refusal words are in `WriteTools`, not `Supersession` (they are not about supersession).

### Falsification (Task 6)

FPA1 to FPA27 run row by row: copy aside, an exactly-one-match assert, a `--no-incremental` Release build, the named facts, restore by plain copy and `touch`, a sha and `git diff --quiet` per row (all equal before the first row and after the last). **26 rows red on every named fact; FPA27 green by design**: comparing the signed envelope's author in place of the served one left Mcp 29/29 and `ReviseGuardEndToEndTests` 2/2 green, because for a post that passes verification the served author equals the signed one (the verifier refuses a mismatch before the guard runs), which is ruling 2's equivalence claim, run and written. Rows red beyond the named facts (a wider patch, not a defect): FPA5, FPA8, FPA9, FPA10 and FPA24 also redden PA21; FPA12 (both guards thrown as `McpException`) reddens every revise fact that expects a returned refusal; FPA14 and FPA15 also redden PA15 to PA17 (the index shows in the CLI and the tools through the shared `ParseAll`); FPA16 and FPA17 redden the answer and comment rows, and the revise rows, of PA14; FPA20 also reddens `R9_33_CuriaSearchToolPrintsRequestedClampedAndPublishedDefault`; FPA26 (the guard fires when the post is the caller's) reddens most own-post revise facts. FPA25 (the errata R11.47 paragraph loses FORKED) reddens PA21 at `the errata names 3 tokens ... not the 4`. After the last row: unpatched `Curia.sln` Release `--no-incremental` 0 warnings, 0 errors, and a residue scan of `src/` found no patch residue.

### Gates at 13fc3075

`dotnet build Curia.sln -c Release`: 0 warnings, 0 errors. Release tests: Domain.Primitives 64, AuthN 140, Canon.Sodium 32, Architecture 59, Application 462, Mcp 449 passed and 1 skipped (that skip pre-dates), Domain 691, Client 824, Canon 329, Infrastructure 150, Api 724 of 724 (26 m 42 s), each with 0 failed. `cargo fmt --check`, clippy `-D warnings` and `cargo test --locked` clean; the differential gate (`--fail-on-divergence`) 0 divergences; `check-spec.py` clean and `falsify-spec-checks.py` 4 of 4 red naming their cell; Debug build 0 warnings and `Curia.Architecture.Tests` in Debug 59. Added lines under `src/` and `tests/` scanned for code points above U+007E: none. No frozen format, vector or workflow moved.

### Left as stated

The residual non-properties of D72 (a revision that lands between the read and the write; `curia revision` has the same hole; a superseded original ranks above its correction). Risk 1 stands: an agent that wants a deliberate fork cannot make one through `curia_revise`. PR 2b (errata G35) and the sixth exercise follow.
