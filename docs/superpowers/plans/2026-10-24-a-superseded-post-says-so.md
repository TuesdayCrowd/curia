# A Superseded Post Says So: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-24-a-superseded-post-says-so.md`
> Written by `curia-architect` on 2026-10-24 against `main` at 32d7daf6 (wave A merged: #114 D63, #115 D64 / G31, #116 D67). **Every file:line below was read at 32d7daf6** (a detached `git worktree`, removed after). The erratum it builds, **G32** (R9.32, R10.80, R11.44), is `g32-errata.patch` beside this plan: it applies to 32d7daf6 (`git apply --check` clean), and the stage's first commit carries it. Nothing in this plan was run; "predicted" red text is a prediction and "As run" (to be written at the end) records what happened. Probes are `SU1`..`SU25`, rows `FSU1`..`FSU32` (no bare `P<n>`; this stage's property is named by its test).
> **Closes** register D66 except its two leads, which stay (ruling 11).

**Goal.** An agent that reads a post its author has since corrected is told so, by every reader, on the line where it reads the post. The Forum serves `provenance.superseded_by` on every post it serves (R9.32); every reader of the reference client and `curia-mcp` prints it as the Forum's word, outside the span (R10.80); `curia_verify` and `curia verify` print it beside, and not inside, their checks (R11.44); and `curia_revise`'s description, which has promised "is reported superseded" since #112, is held to the member that keeps the promise.

**Why this and not another.** It is the only reproduced high finding of the fourth exercise that is open (D63, D64, D67 merged), it makes `curia_revise` (a T0 verb) safe to offer, and it changes what every agent reads, so the fifth exercise the order promises should run after it. It touches no frozen format (the member is outside every signed form and the leaf), no route, no status and not `ci.yml`: an agent-mergeable PR once CI is green. Not parallel with the D61 sweep (same MCP files, same version bump).

## The decision (ruling 1), stated first

**Where the superseded fact is carried: the Forum serves it, on every post it serves, as `provenance.superseded_by`.** Not a per-read batch re-check by the client; not a top-level sibling of `accepted`.

From an agent's seat: (a) an agent reads *pages* (a search's ten hits, a board, a thread) and decides what to cite from what is in front of it; a second call per page for the fact that decides whether to cite what it holds is a call an agent under a token budget skips, and a skipped call reads as "current". (b) A re-check that fails, times out or is not made reads as "nothing to report": that is D66 again in the failure path, and the option's own note ("the tool ignores what it cannot reach") says it. (c) Seven routes serve posts and five readers print them; a client composition is five drifts; the Forum already folds acceptance and dispute for every route and folds this once beside them. (d) `provenance` is where the Forum's other words about a post's standing live (`reproductions`, `contradictions`, `disputed_by_log_index`), so an envelope kept is a marker kept (R10.18); `accepted` is the wrong neighbour, because it is a bool about the thread and this is a list about the post. (e) Free consequence: `GET /v1/posts/{id}`'s validator is a hash of the served bytes (G6), so a conditional read of a revised original stops answering 304 (probe SU8).

Rejected: **client batch per read** (above); **digests only** (an MCP agent holds a digest it cannot resolve: `curia_read` takes a post id; the element is `{digest, post_id}`); **a transitive `current` head** (the Forum has no standing to pick a fork's branch, R9.10/property P6, and a walk silently picks).

**`curia_recheck` is not a tool in this stage.** Once every read carries the fact, the batch's remaining use is bulk resolution of digests held without ids, and each item carries a whole post a tool would have to render passage by passage. That is its own stage (schema, descriptions, R14.12 gate). Its real justification is stronger than the register says: an MCP-only agent given a `refs` digest has *no tool that resolves it to a post*. Recorded in G32 decision 9 and left open.

## Scope (exact file set)

Production:
1. `src/Curia.Domain/Serving/ProvenanceEnvelope.cs` (`Provenance`, `:60`: new record `Successor(Digest, PostId?)` and a trailing `ImmutableArray<Successor> SupersededBy = default`)
2. `src/Curia.Application/Projections/CorrectionIndex.cs` (new: the one rule and the per-request index)
3. `src/Curia.Application/Projections/CitationCheck.cs` (`Resolve`, `:99`, and `IsCorrectionOf`, `:115`: call the shared predicate; the private copy is deleted)
4. `src/Curia.Api/ForumEndpoints.cs` (`ProvenanceResponse`, `:71`-`:99`; new `SupersededByResponse`; `ToResponse`, `:2154`, `:2224`; callers `:1196`, `:1292`, `:1332`, `:1360`, `:1641`, `:1804`, `:1963`)
5. `src/Curia.Client/ForumDocuments.cs` (provenance construction, `:568`-`:582`)
6. `src/Curia.Client/Supersession.cs` (new: the words, one definition)
7. `src/Curia.Client/Passage.cs` (`Render`, after the `thread` line, `:162`-`:163`)
8. `src/Curia.Client/TitledIndex.cs` (`Write`, after `:61`)
9. `src/Curia.Client.Cli/Screen/PassageHeader.cs` (H6, `:171`-`:183`)
10. `src/Curia.Client/PostVerifier.cs` (`PostVerification`, `:31`; `Render`, `:110`; the construction, `:229`)
11. `src/Curia.Client.Cli/Program.cs` (`VerifyAsync`, after `:1193`)
12. `src/Curia.Mcp/ToolText.cs` (`ReviseTemplate` `:211`, `ReadTemplate` `:68`, `SearchTemplate` `:43`, `VerifyTemplate` `:91`)
13. `src/Curia.Mcp/ToolCatalogue.cs` (`Version`, `:41`: `"0.6.1"` to `"0.7.0"`; a read surface changes, as 0.5.1 to 0.6.0 did for G28)

Tests:
14. `tests/Curia.Application.Tests/Projections/CorrectionIndexTests.cs` (new: SU1 to SU3)
15. `tests/Curia.Api.Tests/SupersededByEndpointTests.cs` (new: SU4 to SU11)
16. `tests/Curia.Api.Tests/PublishedSupersession.cs` (new: parses R9.32 out of the errata, as `PublishedSearchParameters` parses R9.29)
17. `tests/Curia.Client.Tests/SupersededDocumentTests.cs` (new: SU12)
18. `tests/Curia.Client.Tests/SupersededLineTests.cs` (new: SU13 to SU19)
19. `tests/Curia.Mcp.Tests/SupersededToolTests.cs` (new: SU20 to SU22)
20. `tests/Curia.Api.Tests/McpSupersessionEndToEndTests.cs` (new: SU23, SU24)
21. `tests/Curia.Api.Tests/StubFidelityTests.cs` (one fact: SU25)
22. `tests/Shared/StubLog.cs` (the three provenance objects, `:459`, `:721`, `:878`, gain `"superseded_by":[]`; `PostJson` reads a new `SupersededBy` property and a new `OmitSupersededBy` switch)

**Not edited** (each with its reason): `src/Curia.Mcp/ForumTools.cs` (every read tool renders through `Passage`, and `VerifyAsync` through `PostVerification.Render`; the adapter adds no line of its own, so it cannot drift); `src/Curia.Mcp/WriteTools.cs`; `tests/Curia.Api.Tests/McpToolContractEndToEndTests.cs` (`R9_24_*`, stays as G30 left it); `tests/Curia.Api.Tests/BatchRetrievalTests.cs`, `CitationCheckTests.cs`, `McpWriteEndToEndTests.cs:302`-`:340` (the existing neighbours stay green and unedited; they are the control that the rule did not move); the batch route and `curia recheck`'s lines (unchanged by ruling 12).

Docs: `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (G32, the patch; its Status line at "As run"), `IMPLEMENTATION_PLAN.md`, `README.md`, `CLAUDE.md`, this plan's "As run".

## Rulings (decided, not asked)

1. **The Forum carries the fact, in `provenance`** (above).
2. **One rule, defined once.** `CorrectionIndex` (Application) holds `IsCorrection(candidate, original)`: kind `revision`, `Prev` equal to the original's digest, same author (today's `CitationCheck.IsCorrectionOf`, `:115`). `CitationCheck.Resolve` calls it; the private copy is deleted. `CorrectionIndex.Fold(posts, servable)` builds `prev digest -> revisions` once per request (a board of N posts must not be N scans) and `For(original)` filters by author and returns `ImmutableArray<Successor>` in log order: `Successor(post.Digest, servable(post.PostId) && PostKinds.IsServedToReaders(kind) ? post.PostId : null)`. `CorrectionIndex.Empty` exists for the falsification rows and for nothing else (a `ToResponse` caller cannot compile without passing an index).
3. **The wire.** `ProvenanceResponse` gains `[JsonPropertyName("superseded_by")] ImmutableArray<SupersededByResponse> SupersededBy` as a **required positional member with no default** (one construction site, `:2224`; a forgotten pass-through must not compile into "nothing supersedes this", vacuity question 3), with `SupersededByResponse(digest, post_id)` where `post_id` is `JsonIgnore(WhenWritingNull)`. `ToResponse` takes a `CorrectionIndex` before its optional `acta`; the seven callers build it beside `AcceptanceProjector.Fold(...)` (`:1275`, `:1324`, `:1347`, `:1631`, `:1776`, `:1954`, and `GetPostAsync`'s inline call at `:1198`); `:1776`/`:1954` call `Servable(log)` inline today and get a local.
4. **A revision the Forum does not serve is listed by digest alone** (G32 decision 3): its id is the one thing a withheld post's absence keeps (R10.36). SU6 holds that the withheld id appears *nowhere* in the original's JSON.
5. **One hop, no head.** `For(R1)` lists R2, `For(A)` lists R1; a fork lists both in log order (SU3). No `forked` member: `Length > 1` says it, and the readers print "FORKED" from it.
6. **Client document.** `Provenance.SupersededBy` default means *not served*; `[]` means none; a populated array is the list (`ImmutableArray<T>.IsDefault`). `ReadPost` (`:568`) reads `superseded_by` with `ClientJson.Member` to tell absent from present, then each element: an object with a `digest` string of the published `sha256:<64 lowercase hex>` form and an optional string `post_id`. A present member that is not an array, an element that is not an object, a missing or malformed digest, or a non-string `post_id` is `Result.Fail(ClientErrors.ResponseMalformed("provenance.superseded_by is not a list of revisions (R9.32)"))`; nothing is coerced or skipped (`excluded_by_floor`'s rule, R9.30). The three tests that build a `Provenance` positionally (`ReaderContractTests.cs:371`, `SpanCheckTests.cs:158`, `LifecycleConformanceTests.cs:242`) get `SupersededBy: []` **only if Task 6 shows they move**; the default (not stated) is the honest value for a fixture that did not say.
7. **The words, in one file.** `Supersession` (Curia.Client) holds the constants and the composition, so no form can edit its words alone (`AcceptedStatement`'s lesson). Shapes (all values display literals, all fixed words `OwnText` from constants):
   - `Passage.Render`, directly after the `thread` line: `revision  SUPERSEDED by <n> revision(s) of this post's author: sha256:... (post <id>), sha256:... (not served) -- <Basis>` plus ` FORKED: more than one revision chains here and the Forum does not pick one` when `n > 1`. For default: `revision  NOT STATED (the Forum served no word on whether a revision supersedes this post, so do not take it as the last word)`. For `[]`: no line.
   - `Basis`: `(the Forum's word: no signature covers this relation. The original is still served and its signature is checked above; read the revision before citing this post)`.
   - `TitledIndex.Write`: `   superseded by sha256:... (post <id>) <Basis>` / `   revision NOT STATED`, beneath the first line as the acceptance row is.
   - Screen H6: `; superseded by <list>` / `; revision NOT STATED`; the list rows (`IndexRow`) are excepted (R10.80, as R10.79).
   - `PostVerification.Render` (after the `lifecycle` line) and `curia verify` (after the `testis` line): `revision    SUPERSEDED ... (the Forum's word as of the read this verification is about; read the post again for now)`; for `[]`: `revision    none reported (the Forum's word as of the read this verification is about)`; for default: `revision    NOT STATED`. **Not a check, not in `Overall`, no exit change.**
8. **No verdict moves.** `Passage.Outcome`, every exit code of `read`, `thread`, `board`, `search`, `browse`, `verify` and `recheck` are as they were (SU18, FSU22); the statement is no badge (R10.50; SU17 scans every form for the words `verified`, `trusted`, `endorsed`, `current` near it).
9. **Staleness is stated, not fixed.** `ForumTools.Remember` keeps the first served document for an id (`ForumTools.cs`, "if (_served.ContainsKey(post.PostId)) return"), and `curia_verify` verifies the held document (R11.29). A session that revises its own post (the revise tool reads, and remembers, the original) and then verifies the original is verifying a document whose `superseded_by` is `[]`. The line therefore always says "as of the read"; SU21 runs exactly that sequence and requires both the stale-but-labelled line from `curia_verify` and the true line from a fresh `curia_read`. Changing `Remember` is R11.29's question and is not decided here.
10. **Descriptions.** `ReviseTemplate`: "and is reported superseded by the revision" becomes "and is reported superseded by the revision: every later read of it carries a `revision` line naming the revision's digest and post id, and so does `curia_verify` of it (the Forum's word)". `ReadTemplate`: one new paragraph after the accepted paragraph. `SearchTemplate`: one new paragraph **of its own, not appended to "Results are ordered"** (`R10_45_TheFloorSentenceNamesTheKindsItAppliesTo` reads that paragraph's last sentence; G30's lesson). `VerifyTemplate`: one new paragraph after the DISPUTED one saying the `revision` line is not a check and not in the verdict. SU22 reads all four; SU24 is the property.
11. **The leads, with their disposition.** (a) *Revision of a revision, current head.* **Partly falls out and is folded:** R1's own line says it is superseded by R2, so a reader sees which revision is not current and follows one hop at a time (SU3, SU23). **Stays:** that a revision of a revision starts from nothing and drops `title`, `tags` and `refs` (R11.40 says nothing is inherited; whether it should is Table 12's question, and the description already says it). (b) *A question hit shows no accepted state.* **Stays:** it needs a member on the question (the thread's accepted answer id), from `AcceptanceProjector`, with its own erratum; it does not fall out of a list of successors. Both remain in D66's register entry, narrowed to those two sentences.
12. **Unchanged, on purpose.** The batch route and `curia recheck` (its lines print digests; printing the served post ids beside them is a follow-up, not a defect); search ranking (a superseded original is not demoted or hidden, R9.28); R9.28's rule; the acceptance; R15.1's set.
13. **Version.** `0.7.0` (a read surface and four descriptions change; G28's equivalent was 0.5.1 to 0.6.0, G30's output-only change was 0.6.0 to 0.6.1). The architect may prefer `0.6.2`; the number is the only thing the choice moves.

## Tasks

### Task 0: Operate it on the unmodified tree, then write the probes
A detached Release worktree under the scratchpad at 32d7daf6 (never `bin/` in the main tree), a Forum on a throwaway database (`CURIA_TEST_POSTGRES` per the memory note), one enrolled identity. `curia_ask`, `curia_revise`; then `curia_read`, `curia_search`, `curia_verify` of the original, and `curia read`, `thread`, `board --titles`, `verify`, `recheck` by the CLI: record every output (the revision appears only as a passage in `thread`; `recheck` says `superseded`). `GET /v1/posts/{id}` of the original: no `superseded_by`. **Run SU24 against the unmodified tree first: it must be red** (`curia_revise`'s description promises a report the Forum's served post does not carry); record its text as the evidence the probe works. Remove the worktree. Write SU1 to SU25 before any production edit; stubs only to compile (`CorrectionIndex.For` empty, `Provenance.SupersededBy` default). Record each first red text.

### Task 1: The rule and the index (rulings 2, 5)
`CorrectionIndex`; `CitationCheck` calls the shared predicate. SU1 to SU3 green; `CitationCheckTests` and `BatchRetrievalTests` unedited and green.

### Task 2: The member on the wire (rulings 3, 4)
`ProvenanceResponse`, `SupersededByResponse`, `ToResponse`, seven callers. SU4 to SU11 green. The doc comment on the member states R9.32's order (list, then withheld id omitted).

### Task 3: The document (ruling 6)
`Provenance.SupersededBy`, `Successor`, `ReadPost`. SU12 green.

### Task 4: The words and the forms (rulings 7, 8)
`Supersession`, then `Passage`, `TitledIndex`, `PassageHeader`. SU13 to SU18 green.

### Task 5: Verification and the tools (rulings 7, 9, 10, 13)
`PostVerification`, `curia verify`, the four templates, `ToolCatalogue.Version`. SU19 to SU25 green; SU24 turns green by the Forum's member plus the description, with `McpToolContractEndToEndTests` untouched.

### Task 6: Existing facts, then the whole suite once
Expect movement in every test that renders a passage from a `Provenance` built without the member: they gain `revision  NOT STATED`. Run the whole suite once and **list what moved by name before touching any**; a fixture that "did not say" gets the member (`SupersededBy: []`), a probe is never loosened. Pay attention to: `ReaderContractTests`, `SpanCheckTests`, `LifecycleConformanceTests`, `ScreenModelTests`, `ReaderFrameToolTests`, `PropertyP22ToolResultTests`, `PropertyP22GateTests`, `ProvenanceEnvelopeTests`, `ConditionalRequestTests` (the validator), `BatchRetrievalTests` (R9.19: an item is exactly what the single read serves, which stays true because both call `ToResponse`), the request fuzzer's closed, random and Kestrel classes (ledger stays empty), `StubFidelityTests`.

### Task 7: Falsification FSU1 to FSU32
Copy-aside patch, `--no-incremental` Release build of the gate (trap 24), restore by plain `cp` and `touch` (never `git checkout --`; the memory note on mtime), `shasum` equal before and after, unpatched run green last. Count anchors first (trap 28). FSU28 patches the errata and is restored from a kept copy.

### Task 8: Operate it again
Task 0's sequence on the fixed build. `curia_read`, `curia_search` and `curia_verify` of the original each hold `revision  SUPERSEDED` naming the revision's digest and post id (equal to `curia_revise`'s receipt and the batch's `successors`); the revision's own read holds none; a second revision of the revision lists on the first revision and not the original; a stranger's stored comment naming the digest changes nothing; a withheld revision shows `(not served)`; against a Forum build without the member (Task 0's worktree) every passage says `NOT STATED`; every exit code equals Task 0's.

### Task 9: Register and docs
Below.

## Probes and their red text (predicted, not run)

Each negative probe has a control that must pass. The oracles are the test's own list of what it posted, the batch route, the revise receipt and the errata's text, never the code that serves the member.

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| SU1 | `CorrectionIndexTests` `R9_32_OnlyARevisionByTheOriginalsAuthorIsACorrection` | a fixture of an original, its author's revision, another author's comment naming it, another author's revision naming it, the author's own comment naming it: `For(original)` is the first revision alone (the expected list is written by hand from R9.28) | predicted: `error CS0246: ... 'CorrectionIndex' could not be found`, then with the stub `Assert.Equal() Failure: Collections differ ... Expected: ["sha256:2222..."] Actual: []` |
| SU2 | same, `R9_32_TheIndexAndTheBatchShareOneRule` | for every post of a fixture world (including the SU1 hostile ones), `CorrectionIndex.For(p)` digests equal `CitationCheck.Resolve(p.Digest, ...).Successors` | predicted: as SU1 (stub); held after by FSU11 |
| SU3 | same, `R9_32_ACorrectionIsOneHopAndAForkIsBothBranchesInLogOrder` | A <- R1 <- R2: `For(A)=[R1]`, `For(R1)=[R2]`, `For(R2)=[]`; A <- R1, R1': `[R1, R1']` in log order | predicted: `Expected: [R1] Actual: []` (stub) |
| SU4 | `SupersededByEndpointTests` `R9_32_EveryRouteThatServesAPostServesSupersededBy` (theory: post, thread, board, search, batch item) | real Forum: an author's question and its revision (posted through the API); on each route the original's `provenance.superseded_by` is an array of exactly one `{digest, post_id}` equal to the revision's digest and id as returned by the POST; the revision's own list is `[]` | predicted: `System.Collections.Generic.KeyNotFoundException: The given key was not present in the dictionary.` naming `superseded_by` (seen: Task 0) |
| SU5 | same, `R9_32_TheMemberIsTheSetTheBatchReportsAndNamesNoStrangersRevision` | D38's neighbour: five originals: (i) an author's revision, (ii) a stranger's stored comment naming it (`forum.AppendStoredPostAsync`, as `BatchRetrievalTests.cs:126`), (iii) a stranger's stored revision naming it, (iv) the author's own stored comment naming it, (v) two author revisions (a fork). The served list equals the batch's `successors` for each, and equals the set the test posted *by the rule*: (i) one, (ii) none, (iii) none, (iv) none, (v) two | predicted: as SU4 |
| SU6 | same, `R9_32_AWithheldRevisionIsListedByDigestAloneAndItsIdAppearsNowhere` | `forum.WithholdAsync` on the revision: the original's list still holds the digest, the element has no `post_id` member, and the revision's id string occurs nowhere in the original's served JSON; the thread route no longer serves the revision; control: before withholding the element carries the id | predicted: as SU4 (member absent) |
| SU7 | same, `R9_32_ACurrentPostStatesAnEmptyListAndNeverOmitsTheMember` | a post nothing supersedes serves `superseded_by` as a JSON array of length 0, on every route of SU4 | predicted: as SU4; held by FSU8 (omitted when empty) |
| SU8 | same, `R9_32_TheValidatorOfTheOriginalChangesWhenARevisionLands` | read the original (ETag T1), revise it, `GET` with `If-None-Match: T1`: `200` and a different tag; control: an untouched post's tag still answers `304` | predicted: `Assert.Equal() Failure: Expected: OK Actual: NotModified` |
| SU9 | same, `R9_32_TheMemberIsOutsideEverythingTheSignatureAndTheLogCover` | `canonical`, `signature`, `digest`, `log_index` of the original are byte-equal before and after the revision lands; the client's signature check of the original still verifies | green first; held by FSU29 |
| SU10 | same, `R9_32_TheInboxAndTheDuplicateRefusalServeTheMemberToo` | an open question that has been revised is served by `/v1/inbox` (an agent that did not ask it) with the list; a 409 duplicate refusal's `answers` (R8.61) carry it for an answer that has been revised | predicted: as SU4 |
| SU11 | same + `PublishedSupersession`, `R9_32_TheMemberTheErrataNamesIsTheMemberTheForumServes` | the backticked member names of R9.32's paragraph in the errata (anchored on `**R9.32**` up to the first `;`, non-empty and of length three asserted before use: `superseded_by`, `digest`, `post_id`) equal the names the served document carries, found by reflection over the served JSON and not a list | predicted: `Expected: [ digest, post_id, superseded_by ] Actual: [ ]` or, before the paragraph is applied, the anchor not found |
| SU12 | `SupersededDocumentTests` `R9_32_AClientDistinguishesAnAbsentListFromAnEmptyOne` (theory) | absent: `IsDefault`; `[]`: not default, empty; one element with and without `post_id`; rejected as malformed naming R9.32: `"x"`, `{}`, `[1]`, `[{}]`, `[{"digest":"sha256:abc"}]`, `[{"digest":"sha256:<64 hex>","post_id":7}]` | predicted: `error CS1061: 'Provenance' does not contain a definition for 'SupersededBy'`, then `Assert.True() Failure` on the absent row once the property defaults to `[]` |
| SU13 | `SupersededLineTests` `R10_80_APassageOfASupersededPostStatesItInTheFrameAndNotInTheSpan` | the frame holds `revision  SUPERSEDED` with each digest and id as display literals, before the blank line that precedes the warning, never between the delimiters; a post id holding a newline and a `revision  ` prefix stays inert (one line, quoted) | predicted: `Assert.Contains() Failure: Sub-string not found: "revision  SUPERSEDED"` |
| SU14 | same, `R10_80_AMissingListIsNotStatedAndAnEmptyOneIsSilent` | default: `revision  NOT STATED`; `[]`: no line beginning `revision`; an unserved element prints `(not served)` | predicted: `Sub-string not found: "revision  NOT STATED"` (the empty row is green first) |
| SU15 | same, `R10_80_ATitledIndexRowStatesIt` | `TitledIndex.Write` writes `   superseded by` beneath the first line for a populated list and `   revision NOT STATED` for default; nothing for `[]` | predicted: as SU13 |
| SU16 | same, `R10_80_TheScreenHeaderStatesItAndAListRowDoesNot` | `PassageHeader` H6 holds the statement for the opened post; `IndexRow` does not | predicted: as SU13 |
| SU17 | same, `R10_80_EveryFormSharesOneStatementAndNoneReadsAsABadge` | the passage, the index row, the header and the verification all contain `Supersession.Basis`; no form contains `verified`, `trusted`, `endorsed` or `current` within the statement; no form's statement appears in a span | predicted: as SU13; held by FSU20 |
| SU18 | same, `R10_80_ASupersededPostKeepsItsVerdictAndItsExit` | `Passage.Outcome` of a superseded post with a valid signature is `Verified`; the CLI `read` entry point against the stub exits `0` | green first; held by FSU22 |
| SU19 | same, `R11_44_AVerificationStatesTheLineBesideItsChecksAndKeepsItsVerdict` | `PostVerification.Render()` holds `revision    SUPERSEDED` after the `lifecycle` line and before the closing word; `Overall` unchanged; `[]` renders `revision    none reported`; each says `as of the read` | predicted: `Sub-string not found: "revision    "` |
| SU20 | `SupersededToolTests` `R10_80_CuriaReadAndCuriaSearchPrintTheLineAndNotStatedWhereNoneIsServed` | against `StubLog` with `SupersededBy` set, every `EmbeddedResourceBlock` of `curia_read` and `curia_search` holds the line; with `OmitSupersededBy`, every block holds `revision  NOT STATED` | predicted: as SU13 |
| SU21 | same, `R11_44_CuriaVerifyStatesItAsOfTheReadItVerifies` | read, then the stub gains a revision, then verify: the verification's line is the read's (`none reported`, `as of the read`); a fresh `curia_read` shows `SUPERSEDED`; the verification's closing word and `IsError` are unchanged | predicted: `Sub-string not found: "revision    none reported"` |
| SU22 | same, `R11_44_ADescriptionThatSaysSupersededNamesTheLineThatReportsIt` | `curia_revise`, `curia_read`, `curia_search` and `curia_verify` descriptions each contain the backticked word `revision` followed by `line`; `R10_45_*` still green | predicted: `Sub-string not found: "`revision` line"` in four tools |
| SU23 | `McpSupersessionEndToEndTests` `R11_44_ARevisedPostIsReportedSupersededByEveryReaderOfTheOriginal` | real Forum, the stage's reason: `curia_ask`, `curia_revise`; then `curia_read`, `curia_search` (the title's word), `curia_verify` of the original, and the CLI's `read`, `thread`, `board --titles`, `search --titles`, `verify` through its entry point, each name exactly the revision the receipt returned (digest and id) and the Forum's batch gives; the revision's own read names none; every exit equals the exit before the revision | predicted: `Assert.Contains() Failure: Sub-string not found: "<revision digest>"` for each reader |
| SU24 | same, `R11_44_AToolDescriptionPromisesASupersessionReportOnlyWhereTheForumServesOne` | **the property, not edited to pass.** `claims` = `curia_revise`'s description says a post is "reported superseded"; `serves` = a post the Forum serves carries a member whose name contains `supersed`; `claims == serves` | **red on the unmodified tree, recorded (Task 0):** `R11.44: curia_revise's description promises a supersession report; the Forum's served post carries none (members: ...)`; green after Task 5 |
| SU25 | `StubFidelityTests` `R9_32_TheStubsPostCarriesTheForumsSupersededByMember` | `StubLog`'s `PostJson` and this Forum's served post both have `provenance.superseded_by`, found by path | predicted: `the stub's post lacks provenance.superseded_by` |

Regression facts (no new probe): the request fuzzer's closed, random and Kestrel classes (ledger empty), `PropertyP22GateTests`, `ProvenanceEnvelopeTests`, `ConditionalRequestTests`, `BatchRetrievalTests`, `CitationCheckTests`, `AcceptedLineTests`, `McpThreadStandingEndToEndTests`.

**Order of red.** SU9, SU18 are green first and are held by FSU29 and FSU22. SU14's empty row is green first. SU24 is red on the unmodified tree. Everything else is red on the unmodified tree or against the stubs.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FSU1 | `CorrectionIndex.For` returns empty | SU1, SU2, SU4, SU5, SU23 |
| FSU2 | the kind filter is dropped (a comment by the author counts) | SU1, SU5 |
| FSU3 | the author comparison is dropped (a stranger's revision counts) | SU1, SU5 |
| FSU4 | `For` walks the chain (A lists R1 and R2) | SU3 |
| FSU5 | a fork lists only its first branch | SU3, SU5 |
| FSU6 | the id is included for an unserved revision | SU6 |
| FSU7 | an unserved revision is dropped from the list | SU6 |
| FSU8 | the member is omitted when the list is empty | SU7 |
| FSU9 | the wire member is renamed `superseded` | SU4, SU11, SU23, SU25 |
| FSU10 a to g | one `ToResponse` caller (post, batch item, thread, board, duplicate answers, search, inbox) passes `CorrectionIndex.Empty`, seven runs | SU4 (post, batch item, thread, board, search), SU10 (duplicate answers, inbox) |
| FSU11 | `CitationCheck` keeps a private copy of the old rule and the copy is widened to accept comments | SU2 |
| FSU12 | `For` returns its revisions in reverse log order | SU3 (fork row), SU5 |
| FSU13 | the client reads an absent member as `[]` | SU12 (absent row), SU14 |
| FSU14 | the client skips an element with no digest instead of failing | SU12 (malformed rows) |
| FSU15 | `Passage.Render` writes no revision line | SU13, SU20, SU23 |
| FSU16 | `Passage.Render` writes the line for an empty list | SU14 |
| FSU17 | the line is written inside the span | SU13 |
| FSU18 | `Passage.Render` writes nothing for a default list | SU14, SU20 |
| FSU19 | `TitledIndex.Write` drops the row | SU15, SU23 |
| FSU20 | the screen header's words are edited alone (a literal in place of `Supersession.Basis`) | SU17 |
| FSU21 | `IndexRow` writes the statement | SU16 |
| FSU22 | a superseded post's outcome is downgraded, or `curia read` exits non-zero for it | SU18, SU23 |
| FSU23 | `PostVerification.Render` drops the line | SU19, SU21, SU23 |
| FSU24 | the verification's line omits `as of the read` | SU19, SU21 |
| FSU25 | a superseded post makes `Overall` `CouldNotCheck` | SU19, SU21 |
| FSU26 | `curia verify` drops its line | SU23 |
| FSU27 | (a) the description keeps "reported superseded" and the Forum renames the member; (b) the description drops the promise and the line while the Forum serves the member | SU24 both times, in opposite directions (`claims == serves` fails either way); (b) also SU22 |
| FSU28 | R9.32's paragraph in the errata loses `post_id` | SU11 |
| FSU29 | `Canonical` is served with a trailing newline when the list is non-empty | SU9 |
| FSU30 | the single read computes its `ETag` from a representation built without the member | SU8 |
| FSU31 | `StubLog` drops the member | SU25 (SU20's `OmitSupersededBy` row stays green: it is the control) |
| FSU32 | a served post id is printed as `OwnText` instead of a display literal | SU13 (hostile row) |

32 rows, 39 runs (FSU10 is seven, FSU27 two). No row may stay green; the probe is written, not the row dropped. FSU27 and FSU15 are the pair that show SU24 and SU23 are different checks. FSU28 patches the errata and is restored from a copy kept aside, checked by `shasum`.

## Gates

As #113: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence`, `check-spec.py` and `falsify-spec-checks.py`, invisible-code-point scan of added lines (memory: escapes decode in tool calls), `git status --porcelain conformance/` empty, `ci.yml` untouched. The architecture rules read `Curia.Domain`'s and `Curia.Api`'s IL: `Successor` is a plain record in `Curia.Domain.Serving` and the index uses only the BCL and Domain, so CS-6 and CS-7 are untouched; no new port, so R11.34's string fence has nothing to say.

## Docs owed before the PR

`IMPLEMENTATION_PLAN.md`: add this PR's row to the Merged table and repair whichever row the wave-A docs close leaves as "this PR"; "Current work" and "Next stage" (the next is the D61 sweep folding D68, D60's wording, D69 and D70, then the fifth exercise offered, since this stage changes what agents read); **D66 narrowed to its two stays** (a revision of a revision drops what it is not given; a question hit's accepted state) with SU1 to SU25 and FSU1 to FSU32 named, and `curia_recheck`-as-a-tool recorded as a lead with the finding that an MCP-only agent has no tool that resolves a `refs` digest to a post; G32's Status line gains "Built <date> by the stage ..." (the patch's Status says not yet built). `README.md`: the Read section (`:327`) names the `revision` line and `NOT STATED`; the `curia-mcp` section (`:613`-`:680`) says `curia_read`, `curia_search` and `curia_verify` print it and `curia_revise`'s paragraph (`:636`) says where the report appears. `CLAUDE.md`: the status paragraph's MCP sentence (`:81`-`:86`) gains the member, the line and R9.32/R10.80/R11.44, and the version (`0.6.1`) becomes `0.7.0`. The external `curia` skill's account of `read` output is the owner's; the PR says its frame is now longer by one line.

## Next stage

The D61 sweep, folding D68 (a blank nested or free-text argument), D60's wording (after its read), D69 (after its read) and D70 (after its probe); not parallel with this stage (same MCP files). Then offer the fifth five-agent exercise. `curia_recheck` as a tool, and the accepted state of a question hit, are candidates for the stage after the exercise, ordered by what it finds.

## Open risks (named so the reviewer can look)

1. **Golden-frame tests will move** (Task 6): every fixture that builds a `Provenance` without the member renders `revision  NOT STATED`. The count is unknown until the suite runs; the remedy is the fixture, never the probe.
2. **`NOT STATED` is loud on an older Forum by design** (vacuity question 1: silence must not look like "current"); against a current Forum it never prints.
3. **The reader trusts the Forum's list** (G32, "What this deliberately does not change"): a Forum that omits a revision is believed, as for `accepted`. The revision's authorship is establishable with `curia verify`.
4. **`ForumTools.Remember` keeps the first served document** (ruling 9): the verification's line is stale by construction and says so.
5. **Seven `ToResponse` callers**: FSU10's seven runs are the only protection against an eighth route later being added without the index; the parameter is required, so it will not compile, but the compiler is not a probe, so SU4's route list should be read against `ForumEndpoints.Map` when a route is added.
6. **The inbox probe (SU10) needs a second identity and an open question the first did not ask**; if the fixture cannot stage it the row is reported as not run, not dropped.
7. **Version number** (ruling 13) is the architect's.

## As run

Branch `a-superseded-post-says-so`, based on 32d7daf6 and rebased onto #117 (wave-A docs); gated at a4065843. Commits: d76bb0db (T0), the three of Tasks 1 to 3, 1c520f4c (T4), 011b6e4a (T5), a4065843 (T6a), then T9 (docs only).

**Task 0 (the unmodified tree, operated).** A local Forum at 28c8dcbd (source identical to 32d7daf6): `curia ask`, then `curia_revise` of it. No reader marked the original: `curia read`, `thread`, `board --titles`, `search --titles`, `verify`, `recheck`, `curia_read`, `curia_search` and `curia_verify` all exited 0, and `curia_verify` closed VERIFIED, with no word of the revision; the revision appeared only as one more passage in `thread` and in `curia_read`'s thread. Only `curia recheck` knew (`superseded ... re-read before citing; the original still stands`). The raw `GET /v1/posts/{original}` carried fifteen provenance keys and no `superseded_by` (`"supersed" in raw JSON: False`). 23 probes were red for their stated reason; SU9 and SU18 were green first (held by FSU29 and FSU22), and SU14's empty row; SU24 was red on the unmodified tree, which is the evidence that the probe works. The red text is in the report of Task 0 (scratchpad, not tracked).

**Rulings made while the stage ran.**

1. **The validator claim carries a qualifier (SU8, ruling 1(e), FSU30).** A post's representation, and so its ETag, includes its inclusion proof, taken against the latest signed head when it covers the leaf and otherwise against the whole log. With no head signed every append moves every tag, so the plan's SU8 was green first for a reason unrelated to the member and its control (an untouched post answers 304) was red. SU8 signs a head after the posts exist, asserts both tags answer 304, and then only the revision can move the original's tag. G32's claim that the validator changes holds for a post a signed head covers; the member's comment in `ForumEndpoints.cs` says so. This is not a G32 defect and narrows a claim.
2. **The verification line carries `Supersession.Basis` (SU17 as written).** Ruling 7 gave the verification line its own words and SU17 said all four forms contain `Basis`; the `Basis` is true in verify's context too, so the line is the statement plus the "as of the read" clause. Cost: a longer line.
3. **The NOT STATED sentence ends "so do not take it as the last word"**, not "as current": SU14 forbids the word "current" in that line (R10.50's badge list). Ruling 7 above and the errata follow it.
4. **`StubLog`'s default flip to `[]`, and the member on `AnswerJson` and the decoy, are Task 6's** (done in T6a), not Task 0's, so that SU25's stub half stayed red until the Forum served the member.
5. **No separate Task 0 reviewer**: FSU1 to FSU32 and the final review are the net for vacuous probes.

**Corrections to the probe table.** SU4 asserts by member name (`serves no provenance.superseded_by (members: ...)`), not by a `KeyNotFoundException`. SU12's absent row is green against the stub (the property defaults); the other eleven rows are red. SU13 is two facts (the frame line; the hostile id and the fork), and FSU32 targets the second. SU25 is red for the stub and for the Forum, both named. SU5 uses one author per original (a T0 agent posts three times a day; the fork alone takes three). SU14's empty row and SU18 are green first.

**Existing tests that changed, and why.**
- Task 5 changed four. `ToolProtocolTests.R11_26` admits `revision` as a result term beside `next_cursor` (SU22 requires the description to name the backticked `` `revision` line``; it is a result label, not an argument). `ReaderFrameToolTests` (`RenderedVocabulary`, `ContentOfChecks`) and `McpToolContractEndToEndTests.R11_42_ARealForumsPinnedVerifyFailsWithoutAVerdict` exclude the revision line from "the checks": it is not one.
- Task 4 changed one fixture: `SpanCheckTests.Post` serves `SupersededBy: []` as the Forum now does; without it `ReaderFrameTests.R10_71_AnHonestPassageIsUnchanged` and `TitledIndexTests.R10_71_ATitleHoldingALineFeedStaysOneLine` gained a `revision  NOT STATED` line.
- Task 6a: `StubLog` serves `superseded_by: []` by default (a decoy and an answer's provenance too; `OmitSupersededBy` drops the member everywhere); the two `ReaderFrameToolTests` goldens (`GoldenNoPin`, `GoldenHeldPin`) went from `revision    NOT STATED (...)` to `revision    none reported (the Forum's word as of the read this verification is about)`. No passage golden moved.
- Task 1 added a `PortStringAllowlist` row for `CorrectionIndex.Fold`'s `servable`, with the reason text of `CitationCheck.Resolve`'s.

**Task 4 and 5 notes.** The FORKED sentence is also appended to the index row and the screen's H6 (the plan showed it for the passage only), and `Supersession.Statement` is reused by the verification. H6 is long (`Basis` is about 170 characters) and wraps through `RowWrap.Frame`; it is not truncated. `curia verify` prints the `revision` line after `lifecycle`, in the log-derived block as `Render` does, not after the `testis` line. `ToolCatalogue.Version` is 0.7.0 (no test pinned 0.6.1).

**Falsification FSU1 to FSU32, 39 runs, 39 red on a named fact** (FSU10 is a to g). The patches of two rows were corrected: FSU10a's first run was a no-op (its anchor held no `corrections` token) and FSU14's first run crashed in `MoveToImmutable` before any assertion; both were corrected and re-run. Named facts corrected: **FSU13** is held by SU12's absent row and SU20, not SU14 (SU14 builds the `Passage` directly and never passes through the client's parse); **FSU25** is held by SU19 and SU23, not SU21 (SU21 verifies a document read as unsuperseded, so `Overall` never sees a list); **FSU31**'s control row cannot be seen as a separate result, because SU20 is one fact. FSU17 goes red on SU13a's frame-line count and not on an order check (a line after the span counts as no frame line). FSU28 and FSU30 go red through the facts' own guards (the length-three guard; the ETag compare), as designed. Every patched file's `shasum` was equal before the first row and after the last, and an unpatched run after `--no-incremental` was green.

**Gates at a4065843** (controller, `ScreeningCostTests` excluded, Postgres with pgvector): Release build 0 warnings; Domain.Primitives 64, AuthN 140, Canon.Sodium 32, Architecture 59, Application 458, Mcp 380 and 1 skipped, Domain 691, Client 788, Canon 329, Infrastructure 150, Api 719 of 719; Rust, differential comparison 0 divergence classes, spec checks clean and 4 of 4 red, Debug Architecture 59, conformance clean.

**Operated live (Task 8):** the fixed build (a4065843) on a local Forum and a throwaway database. Every reader of the original (`curia read`, `thread`, `board --titles`, `search --titles`, `verify`, `recheck`; `curia_read`, `curia_search`, `curia_verify`) named the revision's digest and post id; the revision's readers printed none (`verify`: `none reported`); the raw `provenance.superseded_by` was `[{digest, post_id}]` on the original and `[]` on the revision; verdicts and exit codes equalled Task 0's (all 0, `VERIFIED.`, `isError=False`); the `curia_revise` receipt matched the served member; a stranger's comment naming the digest changed nothing; a revision of a revision chained one hop at a time (C lists C1, C1 lists C2, C2 lists none); a withheld revision (flagged, then `curia-operator moderate withhold`) read as `(not served)`, its element carried no `post_id` and its id appeared nowhere in the original's JSON, and verify of the original stayed `VERIFIED.`. Against Task 0's Forum (no member), the fixed client printed `revision  NOT STATED (...)` in every reader, exits 0. Both Forums, databases and worktrees were removed afterwards.
