# The Floor A Request Cannot Lower: Implementation Plan (D64)

> Repository path: `docs/superpowers/plans/2026-10-24-the-floor-a-request-cannot-lower.md`
> Written by `curia-architect` on 2026-10-24, every code claim re-read at `origin/main` 285d10c8 (#113) from a `git archive` of `src/` and `tests/` and the errata from `git show`. **Nothing was run.** Wave A, stage 3 of 3; file set disjoint from D63's and D67's. This stage **opens errata G31** (below). Probes `C1`..`C10`, rows `FC1`..`FC9`.

**Goal:** `GET /v1/search` serves a request that names a verification level below the surface's configured floor at the floor in force, and says so (R10.54 (rev.), R9.24 (rev.)): the floor block states the surface's published default, the level requested where one was asked for, and whether it was clamped. **Closes** register D64.

**Why now and why dormant.** `HybridSearch.cs:119` is `var floor = query.RequestedFloor ?? surfaceFloor;`: a request lowers any configured floor and the response reads `source: "requested"`. No deployment configures above V0, so nothing is wrong today; the moment one does (R10.45 (rev.) permits it once R10.3's channel exists, and `RetrievalFloors.Parse` already accepts it, `HybridSearchTests.cs:82`), R10.54's threat is live: a poisoned V0 post persuades a consuming model to pass `min_verification=V0`. The stage that introduced the removal count left this unbuilt so as not to change what "the floor in force" means under it; the count's meaning is now fixed (R9.30), so the clamp can land. **Dormant is not vacuous**: C4 to C7 run a real deployment configured at V1.

## Does it need an erratum? Yes: G31.

R10.54 (revised) fixes the behaviour (a lower request is served at the floor in force, the substitution stated). R9.24 (revised) fixes that the response states the published default, the level requested and that it was not honoured. **Neither spells a member, orders it against `source`, or says what `source` reads after a clamp or what a request equal to the floor reads.** G30 had to write R9.29 and R9.30 for exactly this gap (a count "argued and not spelled"), and a clamp whose statement is named by one reader and spelled by another is the same defect. So the wire is requirement text, not a coding choice:

```
**R9.31** The floor block of a search response SHALL carry `published_default`, the surface's
published default floor as a level name; `requested_min_verification`, the level the request named
or `null` where it named none; and `clamped`, `true` where the request named a level below the
floor in force and the response was served at the floor in force (R10.54 rev.), and `false`
otherwise. `min_verification` SHALL state the level applied. `source` SHALL name a request as the
origin of the level applied only where the request's level was applied (a request equal to or above
the floor in force); where the request was clamped, `source` SHALL name the surface's own origin
(`configured`). A level above the floor in force is honoured unchanged: there is no ceiling.
`excluded_by_floor` (R9.30) counts what the floor in force removed, not what the request named.
The reason: R9.24 (revised) argues the statement and does not spell it; "the response was served
at V1 and V0 was asked for" is two numbers in two members, and a client that reads one and not
the other has told the reader the corpus is wider than it is.
```

(Proposed text; the entry carries R9.31's index row, an annotation row for R9.24 (rev.) and R10.54 (rev.), and the "Applied" bookkeeping the G30 entry shows. `R9.31` is the next number in §9 after R9.30 on main; re-read the errata and the highest `## G` heading **immediately before writing**, and if either moved stop and report.)

**Parallelism.** G31 is the only erratum in wave A (D63 and D67 carry none, by their plans), so no race for a number. The errata file, its index and `check-spec.py`/`falsify-spec-checks.py` are touched by this stage alone; the other two never run them with a changed errata. **D66 (G32) must not start before this merges.** That is the one place the erratum constrains order, and the architect's order already puts D66 after wave A.

## Scope (exact file set)

Production:
1. `src/Curia.Application/Retrieval/HybridSearch.cs` (`SearchPage` gains `PublishedDefault`, `RequestedFloor`, `Clamped`; the clamp at `:119`; `source` at `:134`)
2. `src/Curia.Api/ForumEndpoints.cs` (`FloorResponse` `:216`-`:232` three members; the construction near `:1789`)

Tests:
3. `tests/Curia.Application.Tests/Retrieval/HybridSearchTests.cs` (edit: C1 to C5; the existing fact at `:70`-`:78`, `(V1, "requested")` over a published V0 surface, stays green and is the no-ceiling control)
4. `tests/Curia.Api.Tests/ConfiguredFloorForumFixture.cs` (new; a `ForumFixture` subclass whose `ConfigureWebHost` calls the base then `UseSetting("Curia:Retrieval:Floors:rest-search", "V1")`, as `GuardedForumFixture.cs` varies one aspect)
5. `tests/Curia.Api.Tests/SearchFloorClampTests.cs` (new: C6 to C10, parsing R9.31's member names out of the errata as `PublishedSearchParameters.cs` parses R9.29)

Docs: `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (G31 and its index/annotation rows), `README.md` (the floor JSON example in the search section, `:442`-`:445` on main), `IMPLEMENTATION_PLAN.md` (D64's entry, and the new entry below), this plan's "As run".

**Not edited, with reasons.** The readers (`ForumDocuments.cs`, `Program.cs`, `ForumTools.cs`, `ToolText.cs`): the order says Forum side only, and `ForumTools.cs` is D66's and the D61 sweep's file; the three new members are a string, a nullable string and a boolean, so `R9_24_TheDocumentCarriesEveryCountTheFloorBlockServes` (Number-valued members only) and `R9_24_NoDescriptionPromisesARemovalCountTheForumDoesNotServe` (its `serves` regex `remov|dropp|exclud|filtered` over member names) stay green: **the member names were chosen so neither matches**, and C10 asserts it. Registered instead as **D71** ("the readers do not state the clamp"): computed against D70 as the highest entry on main at 285d10c8; re-read before writing; folded into the D61 sweep, which owns `ForumTools.cs`. It is dormant until a deployment configures above V0, which none does. `McpToolContractEndToEndTests.cs`, `StubLog.cs`, `SearchEndpointTests.cs` unedited (its `(V2, requested)` assertion at `:242` is the no-ceiling control). `RetrievalFloor.cs` is not edited: the comparison is local (`VerificationLevel` orders V0 < V1 < V2).

## Rulings (decided, not asked)

1. **The clamp is `max(requested, surface floor)` over V0, V1, V2**, in `HybridSearch`, before `Admit` (`:120`): `clamped = requested < surfaceFloor`. `V−` is not a floor (`IsFloor`), so it never reaches here.
2. **`source`**: `requested` only if the request's level was applied (requested >= surface floor), exactly as today for every currently reachable case; `configured` or `published` (the surface's own) after a clamp. A request equal to the configured floor reads `requested`: the caller got what it named, and R9.24 (rev.) forbids only naming a request as the origin of a level not received.
3. **`excluded_by_floor` is computed with the floor applied.** After a clamp it counts what V1 removed, not what V0 removed (zero). C5 pins it, because a count taken against the requested level would state `0` beside a page the floor emptied.
4. **`published_default` is `RetrievalFloorPolicy.PublishedFloor(surface)`**, never the configured level; `min_verification` is the applied one. Three distinct claims, as R9.24's own argument has it.
5. **Always stated.** `requested_min_verification` is `null` when none was asked (a present member, as `excluded_by_floor` is); `clamped` is `false`, never absent.
6. **No ceiling, and nothing else in `SearchAsync` changes.** `RequestedFloor` still reaches the use case as a nullable; the unknown-parameter check, paging and cursor are untouched. A cursor from a clamped query carries the corpus bound and offset only (the floor is recomputed per request), so a continuation page that names a different `min_verification` than page 1 is served at its own (clamped) floor: stated as a non-property in the entry, not built here.
7. **The fixture is configuration, not code.** `Curia:Retrieval:Floors` is the production path (`Program.cs:175`), so C6 to C9 prove the configured deployment the clamp exists for, through the real host.

## Tasks

**Task 0: operate the unmodified tree.** Detached Release worktree at 285d10c8 under the scratchpad, a Forum on a throwaway database at port 5093 with `Curia__Retrieval__Floors__rest-search=V1`, `HOME` a scratch directory. Seed three answers at V0 and a question. Record `GET /v1/search?board=<b>&kind=answer,question&min_verification=V0`: expected, from the reading, the three answers served, `min_verification: "V0"`, `source: "requested"`, no clamp member. That is the defect observed. Also record the same query with no `min_verification` (V1 applied, three removed) and with `V2` (honoured).

**Task 1: probes first.** C1 to C10, stubs only to compile (`Clamped` false, the two strings empty); record each first red text.

**Task 2: the clamp in `HybridSearch`.** C1 to C5 green.

**Task 3: the wire.** `FloorResponse`, the construction. C6 to C10 green.

**Task 4: the erratum, the README, the register.** G31 as above; `python3 tools/spec-checks/check-spec.py` and `falsify-spec-checks.py` in this worktree; the README example.

**Task 5: existing facts, then the suite once.** `SearchEndpointTests` (floor block shape), `DisputedContentTests`, `SearchRemovalAndParameterTests`, `SearchFloorReadersEndToEndTests` (the readers print the counts they hold: unchanged), `PropertyP22GateTests`, the request fuzzer (S18's shape: ledger empty), `McpToolContractEndToEndTests` P23.

**Task 6: falsification FC1 to FC9**, as D63's Task 5; FC9 patches the errata and restores from a kept copy by `shasum`.

**Task 7: operate it again.** Task 0's three queries against the fixed build: `V0` serves a page of the question alone, `min_verification: "V1"`, `requested_min_verification: "V0"`, `clamped: true`, `source: "configured"`, `excluded_by_floor: 3`; no parameter, `clamped: false`; `V2` honoured, `source: "requested"`.

## Probes and their red text (predicted, not run)

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| C1 | `HybridSearchTests` `R10_54_ARequestBelowAConfiguredFloorIsServedAtTheFloor` | surface configured V1, request V0, three answers at V0 and a question: results are the question alone; `Floor == V1` | predicted: `Assert.Equal() Failure: Collection length differs; Expected: 1, Actual: 4` (the request lowers it) |
| C2 | same, `R10_54_ThePageStatesTheClamp` | `Clamped == true`; `RequestedFloor == V0`; `PublishedDefault == V0` | predicted, before the stub: `error CS1061: 'SearchPage' does not contain a definition for 'Clamped'`; after: `Expected: True, Actual: False` |
| C3 | same, `R9_24_ASourceNeverNamesARequestForALevelNotReceived` | clamped: `FloorSource == "configured"`; control: request V2 over configured V1: `(V2, "requested")`, `Clamped == false` | predicted: `Expected: "configured" Actual: "requested"` |
| C4 | same, `R10_54_ThereIsNoCeiling` | request V2 over configured V1: honoured | green first; held by FC5 |
| C5 | same, `R9_30_AClampedQueryCountsWhatTheFloorInForceRemoved` | request V0 over configured V1: `ExcludedByFloor == 3` (the answers the test appended) | predicted: `Expected: 3, Actual: 0` |
| C6 | `SearchFloorClampTests` (`ConfiguredFloorForumFixture`) `R10_54_AConfiguredDeploymentServesALowRequestAtItsFloor` | real Forum, three answers posted through the API by an attested-owner agent, `min_verification=V0`: the page holds the question alone | predicted: `Expected: 1 Actual: 4` |
| C7 | same, `R9_31_TheFloorBlockStatesTheClamp` | the four members are exactly as R9.31 spells them: `published_default "V0"`, `requested_min_verification "V0"`, `clamped true`, `min_verification "V1"`, `source "configured"`; control: no parameter states `null` and `false` | predicted: `KeyNotFoundException: The given key 'clamped' was not present` |
| C8 | same, `R9_31_ARequestAtOrAboveTheFloorIsNotClamped` (theory V1, V2) | `clamped false`, `source "requested"`, level applied equals requested | green first for V2; V1 green first; held by FC4 |
| C9 | same, `R9_31_TheMemberNamesAreTheOnesTheErrataPublishes` | the four names parsed from R9.31's paragraph in the errata (non-empty and of length four asserted first) equal the names on the wire's floor block that are not already R9.21/R9.30 members | predicted: `Expected: [ published_default, ... ] Actual: [ ]` (stub); held by FC9 |
| C10 | same, `R9_24_NoMemberNameLooksLikeARemovalCount` | none of the new names matches `remov\|dropp\|exclud\|filtered`, so P23 and the document's reflection test are not silently re-aimed | green first; held by FC8 (a member named `clamped_filtered`) |

**Order of red.** C1, C2, C3, C5, C6, C7, C9 red on the unmodified tree; C4, C8, C10 are controls, green first.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FC1 | the clamp deleted (`floor = query.RequestedFloor ?? surfaceFloor`) | C1, C5, C6 (the assertion about results) |
| FC2 | `Clamped` the literal `false` | C2, C7 (the assertion about the response, and C1 stays green: the two defects are different) |
| FC3 | `Clamped` the literal `true` | C8, C7's control |
| FC4 | the clamp uses `<=` | C8 (V1 over V1 becomes clamped) |
| FC5 | the clamp is `min` (a ceiling) | C4, C8 (V2) |
| FC6 | `source` left `"requested"` after a clamp | C3, C7 |
| FC7 | `PublishedDefault` reads the configured level | C2, C7 |
| FC8 | a new member named `clamped_filtered` | C10 |
| FC9 | R9.31's paragraph in the errata loses `clamped` | C9 (parse and code disagree) |

No row may stay green. FC1 and FC2 are the pair R10.54 (rev.)'s own probe sentence asks for: deleting the clamp fails an assertion about results, deleting the statement fails an assertion about the response.

## Gates

As D63's, plus: `check-spec.py` clean and `falsify-spec-checks.py` green with G31 applied (a scratch check first: the ids it cites exist, the index row for R9.31 is in the right column). The architecture rules read `Curia.Api`'s IL in both configurations.

## Docs owed before the PR

`IMPLEMENTATION_PLAN.md`: D64's entry (heading `(closed by <PR>)`, closing paragraph with C1 to C10, FC1 to FC9, and the non-property of ruling 6); **D71 opened** at the end of the register ("the readers do not state the clamp", first step: `ForumDocuments.RetrievalFloorDocument` carries the three members and the two readers print `clamped` beside the floor line, with `not stated` for an old Forum; owner of the work: the D61 sweep). `README.md`: the floor JSON example only. `CLAUDE.md`: **no edit** (its "published per-surface verification floor" sentence stays true). Not in this PR: the Merged row, "Current work", "Next stage" (the touch-up).

## What I did not check

Whether `FloorResponse`'s construction at `ForumEndpoints.cs:~1789` has other callers or a second place where the floor block is built (the MCP stub's `SearchJson` is a stub, not the route); the exact README lines (on main, not on the plan's tree); whether `ForumFixture.ConfigureWebHost` is sealed or its base call order lets a subclass's setting win over `ForumFixture`'s own; whether any existing fact asserts the floor block's exact member list (a theory over `floor.EnumerateObject()` would go red by name, which is wanted); whether `check-spec.py` needs the new requirement id in the §9 section map.

## As run

Built in an isolated worktree at 285d10c8 (#113). Differences from the plan above, and what was observed:

- **Not operated live.** Tasks 0 and 7 (a Forum on a port, queried by hand) were not run. C6 to C9 drive a real host (`ConfiguredFloorForumFixture`, the production `Curia:Retrieval:Floors` path, the production store on Postgres) and read the response document, so the three queries Task 7 names are asserted: `V0` over a V1 deployment (the question alone, `clamped: true`, `source: "configured"`, `excluded_by_floor: 3`), no parameter (`clamped: false`, `requested_min_verification: null`), `V1` and `V2` (`source: "requested"`, not clamped).
- **R9.31's first sentence** reads "SHALL carry three members: ... and `clamped`." so that the probe parses its list from one sentence (the plan's draft put the members in a run of clauses). The rest of the paragraph is the plan's.
- **`SearchPage` types.** `PublishedDefault` is a `VerificationLevel`, `RequestedFloor` a `VerificationLevel?` and `Clamped` a `bool`; the wire strings are made at the route.
- **Probe reds recorded.** On the stub (`PublishedDefault` default, `RequestedFloor` null, `Clamped` false): C1 `Collections differ; Expected ["q"], Actual ["q","a1","a0","a2"]`; C2 `Assert.True() Failure`; C3 `Expected: Tuple (V1, "configured", True); Actual: Tuple (V0, "requested", False)`; C4 `Expected: V2; Actual: null`; C5 `Expected: 3; Actual: 0`. C9 against the unchanged route: `Expected: ["clamped","published_default","requested_min_verification"]; Actual: []`; C7 and C8 and C10 red for the missing members. C6 red under FC1.
- **C10** checks every member the block serves other than the two counts, not only the three the errata lists, so FC8 (a member named `clamped_filtered`) turns C10 red as well as C9.
- **Equal request.** A request equal to the configured floor reads `requested` and `clamped: false` (ruling 2); FC4 shows the fact that holds it.

Falsification, each row restored by plain copy, `touch` and equal `shasum`:

| Row | Red |
|---|---|
| FC1 clamp deleted | C1, C3, C5, C6, C7 |
| FC2 `Clamped` false | C2, C3, C7 |
| FC3 `Clamped` true | C3, C7, C8 (both levels), the equal-request fact |
| FC4 `<=` | the equal-request fact, C8 (V1) |
| FC5 ceiling | C1, C3, C4, C5, C6, C7, C8 (V2), and four existing facts |
| FC6 `source` stays `requested` | C3, C7 |
| FC7 `PublishedDefault` from the configured level | C2, C7 |
| FC8 member `clamped_filtered` | C9, C10 |
| FC9 `clamped` dropped from the paragraph | C9, C10 (the parse refuses a list of two) |

**Register (controller, at commit time).** D64's closing paragraph and D71's entry sit in `IMPLEMENTATION_PLAN.md` beside D63's entry, which the parallel D63 stage also edits; the two branches could not hold adjacent hunks of one file, so both register edits ride in the wave-A docs touch-up after the three stages merge, with the Merged rows and "Next stage".
