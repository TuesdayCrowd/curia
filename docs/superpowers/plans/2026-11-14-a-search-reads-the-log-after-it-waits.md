# A Search Reads the Log After It Waits: Implementation Plan (register item 286; errata G71)

**Slug:** `a-search-reads-the-log-after-it-waits`. The controller installs this file as `docs/superpowers/plans/2026-11-14-a-search-reads-the-log-after-it-waits.md`. Branch name: `search-reads-after-wait`.

Written 2026-11-14 against `main` `b61d40b5` (PR N2, #156, errata G70). Gate: **before the hosted instance**. Independent of PR N2 (merged) and PR O. **Touches neither `.github/workflows/ci.yml` nor `db/`**, so it is not the owner's merge (it is the controller's, on green); the approval record `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` stays `{"version":1,"approved":[]}`; no wire member changes, so `curia-mcp` stays 0.12.0 and the Rust verifier is untouched.

**Identifiers, re-grepped 2026-11-14 against the three documents and `IMPLEMENTATION_PLAN.md`:** the highest errata heading is `## G70` (`curia-whitepaper-ERRATA-AND-ADDENDUM.md:13291`); G64 (PR 8) and G66 (D10b) are reserved and unused; **G71** is named only as "the next number nobody has named" (`IMPLEMENTATION_PLAN.md:15`, `:17`, `:7742`) and appears in no entry. Highest requirement per section I touch: **R9.64** (G65, errata `:13082`), **R11.97** (G70), **R10.98**, R14.13. This plan proposes **G71** and **R9.65**, nothing else. `rg 'R9\.65|G71|SQW|FSQW'` finds none outside the plan's own prose. **Task 1 re-reads the errata immediately before pasting and stops if either number has been taken** (another writer is active). Prefixes: probes `SQW1` to `SQW9`, falsification `FSQW1` to `FSQW11`.

## What this is, in one paragraph

`SearchAsync` in `src/Curia.Api/ForumEndpoints.cs:2487` reads the whole log and folds everything it serves (`:2584-2597`: the verified log, the posts, the standings, the disputed set, what is servable, the verification fold) and **then** calls `HybridSearch.SearchAsync` (`:2599`), which embeds the query at the gate (`HybridSearch.cs:203`, the gate at `OnnxTextEmbedder.cs:198`) and asks the vector index for neighbours (`HybridSearch.cs:211`). R11.92 bounds the gate's order and nothing about its length, so a search that reads first and waits second serves the log as it was before it queued. Measured by PR N's Task 8a, on `main` `03487f9e` and on N's tip alike: 300 searches at once, a genuine declaration 0.46 s in; **292 of 292** searches sent before it and answered after it served the declared post with `excluded_disputed=0`, the last 21.3 s after the declaration, the median 10.8 s (`.superpowers/sdd/2026-11-11-planN/task-8a-report.md`, section 3). The fix is an order: **wait first, read second, rank without waiting.**

## The four states for what this PR adds

| Requirement | Specified | Implemented | Discharged | Falsified |
|---|---|---|---|---|
| R9.65 (new): a search reads the log it serves after every step it can wait at, and awaits nothing between that read and its answer | no (Task 1 pastes it) | no | no: `DisputedContentTests` (`tests/Curia.Api.Tests/DisputedContentTests.cs:127`, `:206`) proves a declaration *between requests* is served, with a fixture whose embedder never waits, so it could not see this: **the probe existed and carried no information about the wait** (vacuity question 1) | no |

## Order against the neighbours

- **D10b** (re-derive the near-duplicate thresholds and the floor for the learned model) touches `HybridRanking.MinimumCosine` and the floors, not the route's order; if it merges first this PR rebases mechanically (`HybridSearch.cs` hunks do not overlap `Search`'s ranking lines). If D10b has already moved `SearchAsync`'s body, Task 1 re-reads before it writes.
- **PR 8 / PR 9** (routes): `ForumEndpoints.cs` hunks are inside `SearchAsync` only. Mechanical.
- **PR O**: unrelated. **The join**: this PR is one of the three "before the hosted instance" gates (items 278, 282, 286); closing 286 does not close the others.

## What the reading found that the register item did not say

1. **Two waits, not one.** The item says "embeds the query first". `NearestAsync` (`HybridSearch.cs:211`) also awaits, a database round trip after the embedding. `Prepare` must hold both, or a window of one index query stays open after the fix. The index needs only the embedding and not the corpus, so nothing forces it after the read.
2. **The corpus filter is what keeps a withheld post off the page, and it must stay after the read.** The vector channel today keeps a hit only if its digest is in the corpus folded from the *verified, honoured* log (`HybridSearch.cs:216-228`). Moving the index query ahead of the read moves hits that were computed over the whole index (withheld and disputed posts included) ahead of the filter. The filter therefore runs inside `Search`, after the read. A patch that drops it (FSQW7) serves withheld posts through the vector channel.
3. **A refusal that "embeds nothing" has a place in the order.** R9.35's cursor-binding check (`HybridSearch.cs:130-134`) precedes both channels on purpose. `Prepare` must run it before it embeds, and the route's `MarkingFrom` 400 (`ForumEndpoints.cs:2594`) currently precedes the search's refusals, so it moves **above** `Prepare` to keep its precedence (SQW8).
4. **There is a sibling, found by reading and not run.** The write path folds the honoured corpus from `decided.Verified` (`ForumEndpoints.cs:1129`) and **then** awaits `dedupe.AssessAsync` (`:1133`), which embeds at the same gate (`DuplicateCheck.cs`, `_embedder.EmbedAsync`, passage role). A question near enough to a servable one is refused with that thread's answers (`DuplicateRefusal`, `:2424`, built from the same `decided.Verified`). If the canonical thread is withheld while the ask waits, the `409` may carry withheld content, which would be worse than a stale search page (R10.37). Task 0 (M4) measures it; ruling 5 says what happens.
5. **No other `await` sits between the read and the answer in this handler.** `AnonymousReadAllowedAsync` (`:2497`) precedes the read; after `:2599` the handler is synchronous (`:2600-2660`). Task 0 (M5) lists the same for the other read routes `[read]`, so the claim "this route is the one" is checked and not assumed.

## The design

**Application (`src/Curia.Application/Retrieval/HybridSearch.cs`).** Two public members replace the two public `SearchAsync` overloads (`:86`, `:102`):

- `Task<Result<PreparedSearch>> PrepareAsync(RetrievalSurface surface, SearchQuery query, CancellationToken ct)`: resolve the floor and the clamp (`:122-127`), compute the binding and refuse a mismatched cursor **before anything is embedded** (`:130-134`), then, for a query with non-blank text, embed it in the query role and ask the index for `HybridRanking.CandidateDepth` neighbours. `NoFeatures` is "no vector hits", as today (`:205-209`). Every failure carries the error type it carries today (mismatch is the caller's `400`; the rest `503`). Takes no log, no corpus, no `VerifiedLog`.
- `Result<SearchPage> Search(VerifiedLog verified, PreparedSearch prepared, VerificationFold verification)`: **synchronous, no `Task`, no token.** Everything from `:136` to the page: the bound, the honoured corpus, the lexical rank, the vector hits kept where their digest is in the filtered corpus and the cosine is at or above the line, fusion, the floor, dispute, diversification, the cursor.
- `PreparedSearch`: a sealed class with an internal constructor, holding the query, the floor, its source, the clamp, the binding and the raw neighbours. Only `PrepareAsync` makes one, so `Search` cannot be handed a hand-built one, and the pairing of a prepared query with the query it was prepared for cannot be mismatched (it carries the query).

**Route (`ForumEndpoints.cs`, the handler `:2487-2660`).** Order after the change: PDP, every parameter refusal (unchanged) → `MarkingFrom` (moved from `:2594`) → `PrepareAsync` (the only waits) → `ReadEventsAsync` → the folds → `Search` → the response. The status mapping of a failed `Prepare` is the mapping `:2600-2605` has today.

**Tests that call the removed overloads** (six sites, five Application classes: `HybridSearchTests`, `HybridSearchCursorBindingTests`, `RetrievalQuerySetTests`, `EmbedRoleTests`, `StoredDuplicateDigestTests`) move to one helper in `tests/Curia.Application.Tests/Retrieval/RetrievalTestLog.cs`, `SearchOverAsync(search, log, surface, query, verification)`, which is `PrepareAsync` then `Search`. The production overloads are **removed, not kept**: a production `Task`-returning method that accepts a log is exactly the shape that lets a future route read first and wait second, and SQW7 can then assert there is none.

## Rulings (decided, not asked)

1. **The order is the requirement, and the type carries what it can.** `Search` returning a bare `Result<SearchPage>` makes "no wait after the read" a property of the signature for the Application layer. It cannot make the *route's* order a property of types, because the route can call `ReadEventsAsync` first and `PrepareAsync` second and both compile. `IlCallSites` returns the calls of a body and not their order (`tests/Curia.Architecture.Tests/EmbedderFenceTests.cs:12`, SEM15 was not built for the same reason), so the route's order is carried by SQW1 to SQW3 alone and **no fence stops a new route doing it again**. Recorded as non-property (e) in G71.
2. **The index is queried before the read, and its answer is read as of that moment.** A post appended after the index query and before the log read is in the corpus and not in the neighbours; the lexical channel finds it, and the vector channel finds it on the next sweep (R9.51's `pending` already states this lag). The reverse, a neighbour whose post the corpus lacks, cannot occur: a post is in the index only after it is in the log. Non-property (c).
3. **`MarkingFrom` moves above `Prepare`.** A request with an invalid marking and a mismatched cursor answers the marking's `400` today and must still (SQW8). The only observable change is that a bad marking over an unreadable log now answers `400` and not `5xx`, which is the order every other parameter refusal already has (R9.54: "before the log is read").
4. **No wire change, no new refusal, no new member.** The page, the problem types, the status codes and `curia-mcp` are unchanged; SQW4 and the existing search suites are the evidence.
5. **The dedupe sibling (finding 4) is decided by Task 0.** If M4 shows a `409` carrying a thread withheld or disputed during the ask's wait, it is the same class at higher cost and rides in this PR as Task 2b: `DuplicateCheck` gains the same split (`PrepareAsync(envelope)` embeds and asks the index, `Assess(corpus, prepared)` is synchronous) and the route folds the corpus after `Prepare`; SQW9 and FSQW11 are then built. If M4 shows the `409` serves nothing the log has withheld (for example because the thread read in `DuplicateRefusal` is folded fresh, `:2424-2431`, and the corpus only picks the canonical digest), it is filed as a register item with the measurement and not built. Either way it is not an owner question; the architect rules on Task 0's report.
6. **A stale answer from a read that preceded the append is not a defect.** What must go is the stale answer whose staleness was the *queue*. The live run's gate states the bound (LS1); it is not "zero stale answers".
7. **No new vector is needed.** `conformance/` holds frozen formats and retrieval numbers; this PR changes neither. R14.6's differential gate is untouched (same wire).

## The errata entry (G71), text to paste at Task 1

Paste after G70 at the end of Part G (re-read the file first; stop if `G71` or `R9.65` is taken). The status is written from the run by Task 5.

```
## G71 — A search reads the log after it waits: the embedding of the query and the index's answer come first, the read of the log and everything folded from it come after, and nothing waits between that read and the answer

**Location.** R9.7 (the corpus a page is cut from), R9.35 (a cursor and its binding), R11.92 (the embedding gate), R6.57 (a post of a key declared compromised is omitted from search), R10.37 (a moderation action withholds); register item 286; PR N's Task 8a (ruling 104 (b)).

**How it surfaced.** PR N's Task 8a ran 300 searches at once through the gate and appended a genuine compromise declaration 0.46 s in: all 292 searches sent before the declaration and answered after it served the declared post with `excluded_disputed=0`, the last 21.3 s after the declaration. The tree before the learned model had no gate and the burst ended before a declaration could be appended, so the defect came with G65.

**Class.** Two requirements, each correct and each tested, composed in an order that makes the first one's guarantee stale: R6.57's omission and R10.37's withholding are decisions of the log as read, and R11.92's gate bounds the order of the wait and not its length. The probe that existed (a declaration between two requests) could not see it, because the fixture's embedder never waits.

### The finding

The search route read the log, folded what is servable, verified and disputed, and only then called the searcher, which embeds the query and asks the index. Every search in the gate's queue therefore served the log as of the moment it joined the queue. The page for a search waiting behind N others was N inferences old.

### Decisions this entry makes

(the four rulings of the plan that belong in the record: wait first and read second; the index is queried before the read and its neighbours are intersected with the honoured corpus after it; the refusals keep their precedence; the page and the wire do not change.)

### Requirement text

**R9.65** A search SHALL read the log it ranks, filters and serves after every step at which it can be made to wait (the embedding of its query, R11.92, and the vector index's answer), and SHALL NOT wait between that read and its answer. A compromise declaration (R6.57) or a moderation record (R10.37) that the log honours at that read is therefore on the page, whatever the length of the queue the search stood in. R11.92 bounds the order in which callers are admitted and not how many are ahead, so a search that read first and waited second served a page as old as its place in the queue, and a withheld post is not served to anyone.

### Non-properties (stated so that no later reading takes them for guarantees)

(a) A search whose read preceded an append serves the page as of its read: the window is one request's folds and serialization, not the queue. (b) A continuation page is a new read and a new wait; R9.7's corpus bound still fixes which posts it is cut from. (c) The index's neighbours are as of the index query, which precedes the read; a post appended between them is found by the lexical channel and, after the next sweep, by the vector channel (R9.51). (d) The write path's near-duplicate check is [decided by Task 0; this line states which]. (e) The route's order is carried by probes and not by a fence; a new route that reads first and waits second compiles.
```

Index rows (Task 5): `| R9.65 | A search reads the log after every step it can wait at and awaits nothing between that read and its answer | G71 |` in the consolidated index, and one row in the "Applied" table if the errata keeps one for G entries.

## Probes and their red text (predicted, not run)

"Control" = green on the unmodified tree by design. Every refusal probe asserts the problem type and has a positive control in the same test. Every probe that waits **asserts first that the wait was reached** (`NOT REACHED`, the pattern of `EmbedRoleTests`), so a harness whose park never parked cannot pass for one that did. **Every `Actual:` is a prediction from reading the code and Task 0's measurement; Task 1 records the real text.**

**The Api fixture.** `ParkingEmbedderForumFixture : ForumFixture` (new, `tests/Curia.Api.Tests/`), overriding `ConfigureStore` (`ForumFixture.cs:533`, which the base leaves empty) to replace the `ITextEmbedder` the base registers (`:503-507`) with a decorator over `HashedNGramEmbedder` that **parks only `EmbeddingRole.Query` calls and only while armed**, signals "entered" through a `TaskCompletionSource`, and releases on the test's command. Passage-role calls (the indexer's, for the posts the test seeds) never park; a decorator that parked them would hang the seed. `ForumFixture.cs` is not edited (it is shared by every Api class; `ConfiguredFloorForumFixture` is the precedent for a subclass).

- **SQW1** *(Api; the harm)* **a declaration appended while a search waits is on that search's page.** Seed two posts by two agents on one board; a control search (unarmed) serves author X's post with `excluded_disputed == 0`. Arm the park; send a search; **assert `entered`** (else `NOT REACHED`); then the genuine path: `sign-head`, `compromise` X's key (`DisputedContentTests.DeclareAsync`'s pattern, `:44`); release; the parked search's page omits X's post and `floor.excluded_disputed == 1`. Control in the same test: a search sent after the release also omits it. Oracle: the declaration's own log index against the page, not the code's order. Red on the tree: `Expected: X's post absent, excluded_disputed 1  Actual: X's post present, excluded_disputed 0`.
- **SQW2** *(Api; the harm, the withhold case)* **a withhold appended while a search waits is on that search's page, and a forged one is not.** As SQW1 with `curia-operator moderate` signed by the operator-acts key (`forum.OperatorActsKeyAsync`, `OperatorModerationTests.cs:283`), `effect=withhold`. The parked search omits the post; `GET /v1/posts/<id>` is `404 curia/posts/withheld`. **Second leg, the control that shows the observer reads a difference:** a *forged* `moderation.applied` row appended as the application role (`ForgedLifecycleRowsTests.InsertRowAsync`'s pattern) while a search is parked leaves the post on that page, as N2 says an unsigned row must. Red on the tree: `Expected: post absent (genuine withhold)  Actual: post present`; the forged leg is green on the tree and after.
- **SQW3** *(Api)* **a continuation page waits and reads too.** Three posts matching one term, `limit=1`; page 1 served; park page 2's request (`cursor` from page 1); declare the key of the post page 2 would serve; release: page 2 omits it. Red on the tree: `Expected: page 2 omits the declared post  Actual: page 2 serves it`.
- **SQW4** *(Api; control, green on the tree)* **the wait changes no page.** Seven requests, each served once unarmed and once parked and released with nothing appended: the two documents are equal member for member except `pending`, `checked_at`, `due_at` (read at Task 1). Includes a **vector-only** query (a term sharing character n-grams with a post and no whole word, so only the vector channel finds it: the pair is read from `HybridSearchTests`'s vector tests at Task 1) and a `min_verification` below the surface's floor (`clamped: true`, `ConfiguredFloorForumFixture`'s case). Guards ruling 4 and finding 2's neighbours.
- **SQW5** *(Application)* **the steps are where the design says.** Recording embedder and index: `PrepareAsync` of a text query makes **1** embed call (role `Query`) and **1** index call; `Search` makes **0** of each; a blank-text query makes **0** and **0** in `Prepare`. Red on the tree: compile (`HybridSearch does not contain PrepareAsync`); **not red by assertion on any tree**, so it carries information only through FSQW2, FSQW3.
- **SQW6** *(Application; the edited cursor-binding tests)* **a refusal embeds nothing, and the errors keep their types.** `PrepareAsync` with a cursor minted by another query: `Fail(RetrievalCursor.QueryMismatch())`, embedder calls 0, index calls 0, for each member of the binding (the existing table in `HybridSearchCursorBindingTests`); `NoFeatures` from the embedder: `Ok` with no neighbours, not a refusal; any other embedder error: `Fail` with that error's type. Red on the tree: compile; carries information through FSQW4, FSQW5.
- **SQW7** *(Architecture; the fence, both configurations)* **no method of `HybridSearch` that takes a `VerifiedLog` or a log list returns a `Task` or a `ValueTask`, and `Search` takes no `CancellationToken`.** Reflection over the shipped `Curia.Application` assembly (a different artifact from the source it constrains), Debug and Release (D16). Red on the tree: `HybridSearch.SearchAsync(VerifiedLog, ...) returns Task<Result<SearchPage>>`.
- **SQW8** *(Api; control, green on the tree)* **the refusals keep their precedence.** An invalid marking (the parameter `MarkingFrom` reads, `ForumEndpoints.cs:3165`; Task 1 reads its name) with a cursor from another query: the marking's `400` type, not the cursor mismatch; each alone answers its own type. Guards ruling 3 and FSQW6.
- **SQW9** *(Api; **conditional on Task 0's M4**, ruling 5)* **a question near a thread withheld while the ask waits is accepted and carries no withheld content.** Red on the tree if M4 confirmed: `Expected: 201  Actual: 409 carrying the withheld thread`.

**Counts to record:** probes written **8 firm + 1 conditional**; red by assertion on the tree: SQW1, SQW2 (genuine leg), SQW3, SQW7 (on the seam); red by compile only: SQW5, SQW6; green by design: SQW4, SQW8, SQW2's forged leg; the ones whose red text differs from the prediction.

## Falsification rows (the first form is the harm)

Patch a tracked file, run the named probes, restore **from a kept copy by plain copy and rebuild** (never `git checkout -- <path>`; a restore that keeps the old mtime keeps the patched DLL: `shutil.copy2`/`cp -p` are wrong). The unpatched run after each row must be green. A row whose named probe stays green is recorded `green` and is a finding, not a tolerance.

| # | Patch | Must go red | First form (the harm) |
|---|---|---|---|
| FSQW1 | route: `ReadEventsAsync` and the folds moved back above `PrepareAsync` | SQW1, SQW2 (genuine), SQW3 | the 292: every parked search serves the log as of its queueing |
| FSQW2 | `Prepare` skips the embedding; `Search` embeds with `.GetAwaiter().GetResult()` | SQW1, SQW5 (embed count in `Search`) | the same 292 under a signature (SQW7) that reads honest: **the fence cannot see this, the probes must** |
| FSQW3 | `Prepare` skips the index query; `Search` blocks on `NearestAsync` | SQW5 (index count in `Search`) | a window of one index query: may leave SQW1 green by speed, and that is recorded as `masked-by-speed`, with SQW5 the probe that is red |
| FSQW4 | `Prepare` skips the cursor-binding check | SQW6; the existing `HybridSearchCursorBindingTests` | a continuation of another query is served as this one (R9.35) |
| FSQW5 | `Prepare` embeds before the binding check | SQW6 (embedder calls on a refused request) | a refused request queues at the gate |
| FSQW6 | route: `MarkingFrom` left below `PrepareAsync` | SQW8 | an invalid marking answers the cursor's `400` |
| FSQW7 | `Search` keeps every neighbour, dropping the intersection with the honoured corpus | SQW2 (genuine leg); the existing withheld-post search tests | **a withheld post served through the vector channel**: worse than the harm this PR fixes |
| FSQW8 | the fixture's decorator never parks (`Park` returns completed) | SQW1, SQW2, SQW3 must fail `NOT REACHED`, not pass | a probe that waits for nothing is green on every tree (vacuity question 1) |
| FSQW9 | a `Task<Result<SearchPage>> SearchAsync(VerifiedLog, ...)` convenience restored | SQW7 | the shape that lets a future route read first and wait second |
| FSQW10 | `Prepare` returns no neighbours (the channel silently dropped) | SQW4 (the vector-only query); the existing `HybridSearchTests` vector tests | the hybrid searcher becomes the lexical one with a vector's name (the fallback R10.1 forbids) |
| FSQW11 | *(conditional on SQW9)* the dedupe corpus folded before `Prepare` again | SQW9 | the `409` carries what the log withheld |

**Counts to record:** rows **10 firm + 1 conditional**; red on the first record by a named probe's own assertion; `masked`, `green` and `masked-by-speed` each with its reason; no control red.

## Tasks

**Task 0: Re-read, operate the unmodified tree.** The brief is `.superpowers/sdd/2026-11-14-item286/task-0-brief.md`; the report is `task-0-report.md` beside it. Run on `b61d40b5`, change no tracked file. Measurements: **M1** the declaration burst re-measured (the 292), with controls that read both outcomes; **M2** the withhold burst, measured for the first time; **M3** a continuation page; **M4** the dedupe sibling; **M5** every other `await` between a log read and a response in `ForumEndpoints.cs`, `[read]`; **M6** the cost of everything after the gate (the residual window the fix leaves); **M7** the tests that call the removed overloads, run, so a red after Task 2 is distinguishable from a flake.

**Rules fixed before the run (what each result would change).** *M1 below roughly 280 of 292, or the last stale answer under a few seconds:* the burst is not in the regime the register measured, and the plan's "queue-bound" claim is restated with the new numbers before Task 1. *M1 equals the register:* confirmed. *M2 serves the withheld post on the span searches:* the register's by-reading claim is confirmed and SQW2's red text is the measurement. *M2 does not:* a withhold is folded somewhere after the gate (read where first), the harm is the declaration's alone, SQW2 becomes a control, and FSQW1 loses SQW2. *M3 serves the declared post on page 2:* SQW3 stands; *does not:* R6.57's whole-log note (`HybridSearch.cs:149-160`) is wrong for the wait and Task 1 reads why before building. *M4 confirms a `409` carrying withheld content:* ruling 5's first branch, Task 2b is built. *M4 does not:* register item, not built. *M5 finds another read route with an `await` after its read:* register item per route; this PR does not fix it. *M6:* the number goes into the PR body and into LS1's bound.

**Task 1: The errata, the fixture and the probes, red first.** Re-read the errata and `IMPLEMENTATION_PLAN.md` (stop if G71 or R9.65 has been taken). Paste G71 (R9.65, non-properties, index row; status "specified; not built"). Write `ParkingEmbedderForumFixture` and SQW1 to SQW4 and SQW8 against the unmodified route and **see SQW1 to SQW3 red by their own assertions** (the real text goes in the report). Add `PrepareAsync`/`Search`/`PreparedSearch` as a **behaviour-preserving seam** (the old `SearchAsync` overloads become `Prepare` then `Search`; the route is unchanged) so SQW5, SQW6, SQW7 can be written and seen red (SQW7) or compile (SQW5, SQW6). Commit nothing that does not build.

**Task 2: The fix.** Remove the two overloads; convert the six test sites to `SearchOverAsync`; reorder the route (ruling 3); build 0 warnings; SQW1 to SQW8 green and every existing Retrieval and search class green (the list from M7). **Task 2b** only under ruling 5's first branch.

**Task 3: Falsification FSQW1 to FSQW10 (and FSQW11).** One row at a time, from a kept copy, in the order above, with the unpatched run after each; the results are counted by script from a jsonl record, never by hand.

**Task 4: Operate it.** The live run below.

**Task 5: Register and docs** (below). Re-grep every first-free number immediately before writing.

**Task 6: PR.** Branch `search-reads-after-wait`; `gh pr list` for the number; the body states "touches no `.github/` and no `db/`", the live run's numbers beside Task 0's, and the residual (non-property (a)); `python3 tools/preflight/preflight.py --api "<the new and changed Api classes>"` first; CI green; **merge on green is the controller's** (not the owner-merge kind).

## The live run (Task 4; no cold read, and why)

**A live run is warranted.** The defect was found only by operating, and the probe that should have seen it was green. Throwaway database `curia_item286_*`, the real model, the built Forum, `curia`, `curia-operator`; hostname, user and paths appear in no record. **Every leg runs first on `c` (`main` `b61d40b5`, as built by Task 0) with the control's result recorded, then on the tip.** The harness is Task 0's (`.superpowers/sdd/2026-11-14-item286/harness/`), reused unchanged.

- **LS1 (declaration burst).** 300 searches at once, a genuine declaration 0.46 s in. **Rule on the tip:** every search answered more than 2.0 s after the declaration's `server_ts` omits the declared post and states `excluded_disputed=1`; the count of stale answers and the latest stale answer's delay are recorded beside Task 0's (292; 21.3 s). A stale answer inside 2.0 s is a search whose read preceded the append (non-property (a)): its count is at most the gate's size plus the searches then between their read and their answer, and is **recorded, not asserted**. **Control:** `c` serves 292 stale answers (or Task 0's number).
- **LS2 (withhold burst).** The same with `moderate`.
- **LS3 (continuation page).**
- **LS4 (parity).** Twenty distinct queries, unparked, on `c` and on the tip over one database: responses equal member for member except `pending`, `checked_at`, `due_at`. **Control:** `c` against `c`, shown equal under the same comparison (so the comparison can tell equal from different: also `c` against `c` with one result's score edited by the harness, which must differ).
- **LS5 (cost).** A single search's p50 and the 300-burst's total time, tip against `c`: p50 within 10%. The fix moves two awaits and adds none, so a larger difference is a finding.
- **LS6 (dedupe).** Only under ruling 5's first branch.

**No cold read:** no word a person reads changes (no help text, no refusal, no banner, no member, no documentation). The reason is recorded in the PR body so its absence is a decision.

## Gates

`dotnet build Curia.sln` (0 warnings); `dotnet build Curia.sln --configuration Release` then `dotnet test Curia.sln --no-build --configuration Release` (needs `CURIA_TEST_POSTGRES` and `CURIA_EMBEDDING_MODEL_DIR`); `dotnet build Curia.sln` in Debug then `dotnet test tests/Curia.Architecture.Tests --no-build --configuration Debug` (SQW7 reads metadata in both configurations); `dotnet restore Curia.sln --locked-mode`; `python3 tools/spec-checks/check-spec.py` and `python3 tools/spec-checks/falsify-spec-checks.py`; the three `cargo` commands and `node tools/differential-oracle/compare.mjs --fail-on-divergence` (unchanged wire: expected green and recorded, not skipped). `git diff --stat origin/main` shows **no `.github/`, no `db/`, no `conformance/`, no `rust/`, no `ForumFixture.cs`, no `Curia.Client*`, no `Curia.Mcp`** and the approval record byte-identical. Run the closed fuzz as CI does (it stays on every PR).

## Docs owed before the PR

- **Errata:** G71 (status written from the run, after G70's), the index row for R9.65, the non-properties' (d) line stating Task 0's M4 result.
- **`IMPLEMENTATION_PLAN.md`:** "Current work" and "Next stage" blocks; **item 286 closed** with the probe and the leg that shows it (LS1, LS2); the first-free line re-read (D125, item 305, trap 276 as of N2's "As run"; Task 5 re-greps); a new item only if M4 or M5 found one; traps proposed below.
- **`CLAUDE.md`:** one clause in the status paragraph (errata G71: a search reads the log after it waits). The file may be edited per PR (the owner's 2026-10-19 permission); `ci.yml` and the guard are not.
- **`README.md`:** no edit unless it states the stale behaviour (Task 5 greps `README.md` for "waiting" and "gate"); the plan did not read it.

## Traps applied and proposed

- **Applied:** trap 263 (an observer that cannot read the other outcome measures nothing: SQW1's pre-declaration control, SQW2's forged leg, FSQW8); trap 275 (a probe named for one step of an ordered procedure must be shown to reach that step: `NOT REACHED`); the "a fixture that serializes what the bug depends on" shape (the existing declaration probes never queued).
- **Proposed (numbers allocated at Task 5, not here):** **a read followed by a wait is a stale read, and a test whose fixture never waits cannot see it** (the general form of item 286: two correct rules, a queue between them); **an order a type cannot carry is carried by a probe that parks the wait** (ruling 1: the fence covers the seam, the probe covers the route).

## Open risks (named so the reviewer can look)

1. **SQW1 to SQW3 depend on a test-controlled park, not on timing.** If the decorator parks the wrong call (the indexer's passage embedding, or a second query call), the test hangs or passes vacuously; the `NOT REACHED` assertion and FSQW8 are the guards.
2. **The in-process fixture is not the live process.** The Api suite runs the host in-process with the hashed embedder; the real gate and the real model appear only in LS1 to LS3. A defect that exists only under the real gate's scheduling is for the live run to find.
3. **`Prepare` before the read changes what a cancelled search costs:** it now reads nothing. A client that goes away while parked used to leave the log read already done. Probably only better; not probed.
4. **The index query's answer is older than the log read** (ruling 2). Nothing else in the route assumes the two are one instant; Task 1 greps `NearestAsync`'s other callers (`DuplicateCheck`) and says so.
5. **Ruling 5's sibling may be wider than the dedupe.** M5 is `[read]` for the other routes; any `await` after a read in a route that serves content is a candidate, and this PR does not claim to have enumerated them by running them.

## What this plan did not read or verify

`[unverified]` and left to Task 0 or Task 1: the name of the parameter `MarkingFrom` reads and the problem type `RetrievalCursor.QueryMismatch()` returns; `OperatorModerationTests.cs`'s exact helper for a signed `moderate` under N2 (the plan cites `:283` for the key host only); `HybridSearchTests`'s vector-only query; whether `ForumFixture`'s `ConfigureStore` is called after the hashed-embedder registration in every composition (`:503-510` says yes); `OnnxTextEmbedder.cs`'s cancellation behaviour of a parked caller beyond line `:198`; `README.md`; `DuplicateCheck.cs` past its constructor and `AssessAsync`; `ci.yml` for any pinned test or class count (Task 0 M7 reads it). The 292 figure is PR N's record, not a number this plan ran. `src/Curia.Mcp` and `src/Curia.Client` were not opened: the claim "no wire change" rests on `SearchAsync`'s response construction (`ForumEndpoints.cs:2614-2660`) being untouched by the design.

## Owner's questions

**None.** The change is internal and reversible; it touches no `ci.yml`, no `db/`, no guard, no privacy surface. Whether the dedupe sibling rides is the architect's ruling on Task 0's report (ruling 5), not the owner's.
