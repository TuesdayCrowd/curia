# Refusals Tell the Truth: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-18-refusals-tell-the-truth.md`

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. **Every subagent runs on Sonnet 5.5** (`model: "sonnet"`); the main loop gates each task's output and sends it back when it does not pass.

> **Drafted, not build-checked.** Written 2026-10-18 against `main` at cd754621 from the `curia-architect` stage report, with every anchor re-read in the tree. Nothing in the C# tree was built or run to write this plan. Line numbers are cd754621's and are given only to help find a place; **every anchor below is either a literal line the tree already holds or a literal line a task here writes**, and every falsification anchor is re-found by its literal text and counted once before it is used (trap 28); a changed anchor is a stop.

**Goal:**
- a revision without `prev` is a 400 `curia/content/prev-required`, and `prev` on any other kind is a 400 `curia/content/prev-not-allowed`, both from one admission function that `PostEnvelope.Read` does not call (**R8.64**, D48 item 3, D38's ingest half);
- a VERIFY refusal of an envelope's own content answers 400, the author mismatch alone 401 (**R6.58**; the finding the register does not yet hold);
- `superseded` and `successors` count only a revision by the original's author (**R9.28**, D38's projection half);
- a denial's title names its cause, chosen from the decision's reason (**R7.24**, D37's open item);
- the client's authorization summary, `curia keys` and the inbox summary stop saying what is not true (D37's reopened items).

**Architecture:** one PR, branch `refusals-tell-the-truth` (exists, empty at cd754621), Tasks 0 to 7. One commit per task 1 to 4 and 7. `Curia.Domain` gains no dependency. No `db/` change, no event, no new route, no frozen format (R15.1). **No workflow file is touched, so the PR is not path-gated** (the ruleset's merge-time path check still runs before any merge and is expected to report no protected path).

**Tech stack:** .NET 10, C# 14, xUnit v3, CsCheck 4.8.0, Python 3 stdlib, GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-10-18-refusals-tell-the-truth-design.md`. **Errata:** G24 (R6.58, R7.24, R8.64, R9.28), already in `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, status "proposed; not applied to the white paper"; this plan cites it and edits none of it.

## Where the architect's report is wrong about the code

Each is read in the tree; the spec's section 2.7 has the evidence. The plan follows the code.

1. Ruling 3's precedent, `parent-not-allowed`, is enforced **inside** `PostEnvelope.Read`. Ruling 4 (the `prev` rules stay out of `Read`) rests on replay (spec 2.6), not on that precedent.
2. The report's replay fact over `PostProjector.Fold` is **vacuous**: `Fold` never calls `PostEnvelope.Read`. S1 (`SearchProjector.Fold`) is the fact that goes red with the rule in `Read`; S2 (`PostProjector.Fold`) is kept as the control and FR6 records that it stays green.
3. `tests/Shared/StubLog.cs:1289` is **not stale**: it is `table-10/denied` under the tier title, which stays that reason's title, and `StubFidelityTests` compares it to the real Forum's. Only two `RefusalClassificationTests` rows (the budget details under the tier title) are stale data.
4. Of the nine title sites, the ninth (`PrincipalOf`) has no decision; it takes the neutral title.
5. Ruling 2's `curia/content/*` leaves `curia/canon/*` VERIFY refusals at 401 (spec 7, question 2).
6. Ruling 8 names three causes; `table-11/quarantined-read-only` and `table-10/delegated-grant-required` print the T0 paragraph too, so the per-cause summary covers all five.
7. Task 5's "confirm no vector involves prev on a post" is false: `conformance/envelope/ed25519-full` is a **finding** with a non-null `prev`. It is a cryptographic vector read only by the Rust verifier, frozen, and not run through admission; Task 5 records it.
8. The Api replay and successor facts need a fixture that does not exist (`ForumFixture.AppendStoredPostAsync`, Task 2).

## Global Constraints

**Build and test** (CLAUDE.md's list, which is the list CI runs)
- `dotnet build Curia.sln -c Release` reports 0 warnings. Build `Curia.sln` in Debug as well before the Architecture run (CS-15 reads two test assemblies' Debug output).
- `dotnet test Curia.sln -c Release --filter "FullyQualifiedName!~ScreeningCostTests"`. **This stage does not touch `ScreeningCostTests`, its rows, its thresholds or `ci.yml`**; the cost class is run once at Task 6 only to show it unchanged, with `CURIA_SCREEN_TIMINGS=<file> CURIA_SCREEN_SCALING=<file> dotnet test tests/Curia.Domain.Tests -c Release --filter "FullyQualifiedName~ScreeningCostTests"` and both files removed first (the class appends).
- `dotnet restore Curia.sln --locked-mode`; `python3 tools/spec-checks/check-spec.py`; `python3 tools/spec-checks/falsify-spec-checks.py`; `cd rust/curia-testis && cargo fmt --check && cargo clippy --all-targets --locked -- -D warnings && cargo test --locked`; `node tools/differential-oracle/compare.mjs --fail-on-divergence` (build `tools/Curia.Differential` in Release and `cargo build --release --bin curia-differential` first).
- `export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"`; never write the username out. Build `curia-testis` and export `CURIA_TESTIS_BIN`. `python3` is required (the external-signer tests).
- Count test assemblies, never totals: **eleven** (`Curia.Api.Tests`, `Application`, `Architecture`, `AuthN`, `Canon.Sodium`, `Canon`, `Client`, `Domain.Primitives`, `Domain`, `Infrastructure`, `Mcp`). Record each assembly's count at the base (Task 0). Never quote counts from this plan.
- Read gate output only through `grep -E "Passed!|Failed!"`. Never `head` it. Long runs go to the background with output to a file, read when the run ends; never `tail -f`.
- The shell is fish: a `diff <(...)` or an unquoted glob goes through `bash -c`.

**Invariants**
- R15.1's set does not move: no envelope, canonical form, leaf computation or stored member changes. `prev` stays an optional member of the schema; `PostEnvelope.Read` is **unchanged** (P4 holds that).
- No component other than `PostEnvelope.AdmissionRefusal` decides that `prev` is required or not allowed. `IngestPipeline.VerifyAsync` and `SubmissionBuilder.Build` call it; neither restates the rule.
- No refusal's JSON members change: a problem is still `type`, `title`, `detail`. Only the **status** of VERIFY content refusals and the **title** string differ. The `detail` prefixes the client classifies on (`table-10/...`, `table-11/...`) do not change (G24, "What this deliberately does not change").
- The author mismatch stays 401 (`curia/content/author-principal-mismatch`), Table 10's cells do not change, and `Help.TierReminder` does not change.
- A stored post is read exactly as before: no projection gains a rule it can fail a stored post on (S1, S2).
- Nothing is added to `Curia.Mcp`, `curia-testis` or `conformance/`.

**Numbering:** G24, **R6.58**, **R7.24**, **R8.64**, **R9.28**. Task 0 re-derives and **stops** if the tree disagrees: G24 present once, the highest `**R6.n**`, `**R7.n**`, `**R8.n**`, `**R9.n**` defined across the white paper, the errata and the scoping document are 58, 24, 64, 28, and no `*.cs`, `*.rs` or `*.py` file cites them yet. The register entry's number (expected D50) is allocated at Task 7 by re-reading the register.

**Characters:** the tests hold hostile values only as escapes, and this stage needs none. **No added line of any file may contain a Cf, Zl, Zp, Co, Cn or unpaired surrogate** (a tool can decode an escape into the invisible character it names, and nobody sees it). Task 7 scans every added line.

**Version control:** `but` only. Never `git commit`, `checkout`, `stash`, `rebase`, `restore` or `reset`. Commit and PR text end with the attribution lines the session gives. No usernames, IPs or home paths in tracked files. The PR edits no workflow file; after CI is green the main loop may merge it, running the merge-time path check first.

**Falsification discipline** (traps 13, 14, 18, 24, 28): RED only on its own `Failed!` line naming the expected fact. A build error aborts a case as not measured and is never followed by a `--no-build` run. Restore by plain copy (never `cp -p` or `shutil.copy2`, which keep the old mtime so MSBuild keeps the patched DLL), prove with `filecmp.cmp` and a clean `git diff --quiet -- <file>`, rebuild `--no-incremental`, run unpatched once. **No patch is a constant expression** (the compiler would warn and the build would fail on a warning).

## Review Focus

1. Task 1: `PostEnvelope.Read` is byte-for-byte unchanged; the admission function is the only home of the two `prev` rules and has exactly two callers; the status mapping defaults by `curia/content/` prefix and names the mismatch as the one exception; the digest's form is **not** validated (H6 would turn 400).
2. Task 1: S1 is a real discriminator (red with the rule in `Read`) and S2 is recorded as the control that is not.
3. Task 2: `successors` needs a revision **and** the original's author, and A1 reproduces D38 by a stored comment (it is red against the old `Where` and green after).
4. Task 3: the title table is complete against the PDP (T1 drives `AccessPolicy.Decide`, not a list), the tier title survives only for `table-10/denied`, and no site in `ForumEndpoints.cs` writes the literal.
5. Task 4: the summary is held to Table 10 parsed from the white paper, every authorization cause has a distinct sentence, and no summary says "nothing else" or "Waiting is the only remedy" of a cause waiting does not fix.
6. Task 6: every row's anchor is counted once and each named fact is seen red, S1 by the rule-in-`Read` row and A1 by the old-`Where` row.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/Curia.Domain/Content/{PostKind,ContentErrors,PostEnvelope}.cs` | `RequiresPrev`; two errors and the type constants; `AdmissionRefusal` | 1 |
| `src/Curia.Application/Ingest/IngestPipeline.cs` | the call after `Read` | 1 |
| `src/Curia.Api/VerifyRefusals.cs` (NEW), `src/Curia.Api/ForumEndpoints.cs` | the status of a VERIFY refusal; the call site | 1 |
| `src/Curia.Client/SubmissionBuilder.cs` | the client's pre-check calls the same function | 1 |
| `tests/Curia.Domain.Tests/Content/PrevAdmissionTests.cs` (NEW) | P1 to P6 | 1 |
| `tests/Curia.Application.Tests/Ingest/IngestPipelineTests.cs` | I1 to I3, `Wire` gains `prev` | 1 |
| `tests/Curia.Application.Tests/Projections/StoredPrevReplayTests.cs` (NEW) | S1, S2 | 1 |
| `tests/Curia.Api.Tests/{VerifyRefusalsTests,RefusalStatusTests}.cs` (NEW), `RevisionOwnershipTests.cs` | V1 to V3, H1 to H6 | 1 |
| `tests/Curia.Client.Tests/{SubmissionBuilderTests,RefusalClassificationTests}.cs` | B1, K1, K2 | 1 |
| `src/Curia.Application/Projections/CitationCheck.cs` | the successor rule | 2 |
| `tests/Curia.Application.Tests/Projections/CitationCheckTests.cs`, `tests/Curia.Api.Tests/{BatchRetrievalTests,ForumFixture}.cs` | C1 to C5, A1, `AppendStoredPostAsync` | 2 |
| `src/Curia.Domain/Authorization/AccessPolicy.cs` | the title table | 3 |
| `src/Curia.Api/ForumEndpoints.cs` | the nine sites | 3 |
| `tests/Curia.Domain.Tests/Authorization/AccessPolicyTests.cs`; `tests/Curia.Api.Tests/{RateBudgetTests,FlagEndpointTests,FlagListingTests,AcceptAnswerTests,KeyRotationEndpointTests,RevisionOwnershipTests}.cs`; `tests/Curia.Client.Tests/RefusalClassificationTests.cs` | T1 to T5, R1 to R6 | 3 |
| `src/Curia.Client/ForumResult.cs`, `src/Curia.Client.Cli/Program.cs` | the summary, `keys`, the inbox summary | 4 |
| `tests/Curia.Client.Tests/{HelpTextTests,RefusalClassificationTests,KeyActVerbTests,DpopFlowTests}.cs` | U1 to U6 | 4 |
| `IMPLEMENTATION_PLAN.md`, `README.md` | the register, the current-work block, the README | 7 |

## The facts (named once, cited by number)

| # | Fact | Derived from (not from the code it checks) |
|---|---|---|
| P1 | `PrevAdmissionTests.R8_64_OnlyARevisionRequiresPrev` | `Enum.GetValues<PostKind>()` against Table 12's "Required fields" column (`revision`: `prev`, `body`, `revision_reason`), as the errata's R8.64 reads |
| P2 | `R8_64_ARevisionWithoutAPrevIsRefusedAsPrevRequired` (theory: absent, `""`, white space) | the same column; the blank test is `RevisesOwnPost`'s |
| P3 | `R8_64_EveryOtherKindThatCarriesAPrevIsRefusedAsPrevNotAllowed` (theory over the six kinds) | R8.64's second sentence; one valid envelope per kind built from `PostKinds.RequiresTitle/RequiresParent/RequiresTarget`, not copied |
| P4 | `R8_64_TheSchemaReadAdmitsWhatTheAdmissionRuleRefuses` | R8.64's third sentence: `PostEnvelope.Read` returns Ok for a comment with `prev` and for a revision without |
| P5 | `R8_64_ARevisionWithAnyNonBlankPrevIsAdmitted` | ruling 7: includes `"sha-256:"` + 43 `a` (not the served form), which is admitted here and refused later as unowned |
| P6 | `R8_64_TheRefusalsAreContentTypedAndNameTheKind` | R6.58: both types start `curia/content/`, the detail is the kind's wire spelling |
| I1 | `IngestPipelineTests.R8_64_AVerifyRefusesARevisionThatNamesNoPrev` | `VerifyAsync` returns `curia/content/prev-required` before the key is consulted |
| I2 | `R8_64_AVerifyRefusesACommentThatCarriesAPrev` | `curia/content/prev-not-allowed` |
| I3 | `R8_64_AVerifyAdmitsARevisionThatNamesItsPrev` | the control: the same revision with `prev` verifies |
| S1 | `StoredPrevReplayTests.R11_31_AStoredCommentThatCarriesPrevIsStillSearchable` | R8.64 and R11.31: `SearchProjector.Fold` over a stored comment with `prev` projects it |
| S2 | `R11_31_AStoredCommentThatCarriesPrevStillFoldsIntoThePostModel` | the control, not a discriminator: `PostProjector.Fold` keeps `Prev`; stays green with the rule in `Read` |
| V1 | `VerifyRefusalsTests.R6_58_EveryContentRefusalButTheAuthorMismatchIsBadRequest` | R6.58: every public static `Error`-returning factory on `ContentErrors`, invoked by reflection, so a new factory gets a row without an edit |
| V2 | `R6_58_TheAuthorMismatchStaysUnauthorized` | R6.58's exception |
| V3 | `R6_58_TheOtherVerifyRefusalsKeepTheirStatus` | `curia/log/unreadable` 503, `curia/ingest/unstorable-member` 422, `curia/keys/retired` and `/compromised` 403, `curia/keys/not-registered-to-agent` and `curia/jws/signature-invalid` 401 |
| H1 | `RefusalStatusTests.R6_58_AnAnswerWithNoParentIsABadRequest` | **red today (401)** if the reading holds; the probe of spec 2.2 |
| H2 | `R6_58_AQuestionWithNoTitleIsABadRequest` | the same |
| H3 | `R6_58_AQuestionNamingAParentIsABadRequest` | the same |
| H4 | `R8_64_ACommentCarryingPrevIsABadRequestAndStoresNothing` | the board lists no new post |
| H5 | `RevisionOwnershipTests.R6_7_ARevisionThatChainsToNothingIsRefused` (edited) | 400 and `curia/content/prev-required` where it was 403 |
| H6 | `ARevisionChainingToAnUnknownDigestIsRefused` (edited) | still 403 and `own-resource-only` (ruling 7) |
| B1 | `SubmissionBuilderTests.R8_64_ARevisionDraftWithoutPrevFailsLocally` | the builder refuses with `curia/client/envelope-invalid` naming `prev-required`, and signs nothing |
| K1 | `RefusalClassificationTests.R6_58_ABadRequestContentRefusalIsContentAndExitsThree` | `Interpret(400, …prev-required…)` is `RefusalKind.Content` and `Cli ExitCode.For` is 3 |
| K2 | `R6_58_AnAuthorMismatchIsStillAuthentication` | 401 with the mismatch type is `Authentication`, exit 8 |
| C1 | `CitationCheckTests.R9_28_ACommentByAnotherAgentThatNamesADigestIsNotItsSuccessor` | R9.28: original stays `Current`, no successors |
| C2 | `R9_28_ANonRevisionByTheOriginalsAuthorIsNotASuccessorEither` | isolates the kind filter |
| C3 | `R9_28_ARevisionByAnotherAgentIsNotASuccessor` | isolates the author filter |
| C4 | `R9_28_ARevisionByTheOriginalsAuthorSupersedesIt` | the control |
| C5 | `R9_28_AnAbsentOriginalHasNoSuccessors` | spec 4.5 detail |
| A1 | `BatchRetrievalTests.R9_28_AStoredCommentThatNamesAnotherAgentsPostDoesNotSupersedeIt` | D38's reproduction: a stored comment appended by hand, then `POST /v1/posts/batch` |
| T1 | `AccessPolicyTests.R7_24_EveryDeniedReasonAPdpCanGiveHasItsOwnTitle` | the PDP driven over `ModelledPairs` x tiers x credential states x budget extremes, plus `Discharge(false)` of each qualifier |
| T2 | `R7_24_OnlyTheTierDenialIsTitledAsTheTier` | R7.24: no title but `table-10/denied`'s contains "trust tier" |
| T3 | `R7_24_AnUnknownOrAbsentReasonTakesTheNeutralTitle` | R7.24: never the tier title |
| T4 | `R7_24_TheCausesTheErrataListsAreTheReasonsThePdpGives` | R7.24's cause list parsed from the errata (seven) equals the distinct deny reasons T1 observed |
| T5 | `R7_24_NoApiSiteWritesTheTierTitleAsALiteral` | the text of `src/Curia.Api/ForumEndpoints.cs`: the literal does not occur |
| R1 to R6 | route facts: posting budget (`RateBudgetTests`), flag budget (`FlagEndpointTests`), ownership of a revision (`RevisionOwnershipTests`), flags on a post (`FlagListingTests`), thread ownership (`AcceptAnswerTests`), quarantine (`KeyRotationEndpointTests`) | each asserts the served `title` names its cause and does not say "trust tier" |
| U1 | `HelpTextTests.D37_TheAuthorizationSummaryIsHeldToTable10` | Table 10 parsed by `PublishedTable10`; the summary for `table-10/denied` names every T1-only verb and the T1 numbers equal `TierPolicy`'s; "nothing else" and "may ask and comment" absent |
| U2 | `D37_AnOwnershipRefusalIsNotToldToWait` | `table-10/own-resource-only` and `own-thread-only` summaries do not contain "Waiting" and say a higher tier does not change it |
| U3 | `D37_EveryAuthorizationCauseHasItsOwnSummary` | `AccessPolicy.DenialReasons` minus the two budgets: pairwise distinct summaries, none contains "nothing else" or "only remedy" |
| U4 | `KeyActVerbTests.D37_KeysSaysWhoseWordItPrintsAndWhatAnchorsIt` | the printed output |
| U5 | `D37_NoneEnabledKindsNamesTheLifecycleGuard` | the printed output with an empty enabled set |
| U6 | `DpopFlowTests.D37_TheInboxSummaryCountsOpenBeforeExclusionsAndSaysHowManyAreListed` | `InboxCorpus`'s own documentation of `Matching` |

## Task 0: Branch, base, numbering, and the baseline

- [ ] `git merge-base --is-ancestor cd754621 origin/main` exits 0, `git log refusals-tell-the-truth -1 --oneline` prints cd754621 and `but status` shows `refusals-tell-the-truth` with no commits, and the working tree is clean except this plan, the spec, `IMPLEMENTATION_PLAN.md` and `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (the last two are the architect's, already modified and uncommitted; they ship with Task 7's commit or the architect's own). Otherwise stop.
- [ ] Re-derive; each must print as shown or **stop and report**:
  - `grep -c '^## G24 ' curia-whitepaper-ERRATA-AND-ADDENDUM.md` is `1`;
  - the highest `**R6.n**`, `**R7.n**`, `**R8.n**`, `**R9.n**` across `curia-agent-forum-WHITEPAPER.md`, the errata and `curia-csharp-scoping.md` are 58, 24, 64 and 28 (`grep -ohE '\*\*R6\.[0-9]+' <the three> | grep -oE '[0-9]+$' | sort -n | tail -1`, and the same for 7, 8 and 9);
  - `git grep -n -E 'R(6\.58|7\.24|8\.64|9\.28)' -- '*.cs' '*.rs' '*.py'` prints nothing;
  - `python3 tools/spec-checks/check-spec.py` is clean; `python3 tools/spec-checks/falsify-spec-checks.py` goes red on all four checks.
- [ ] Re-find each anchor this plan quotes from the current tree (a literal that does not occur exactly once is a stop): `if (!read.TryGetValue(out var envelope, out var schemaError))` in `IngestPipeline.cs`; `if (!PostEnvelope.Read(envelope).TryGetValue(out _, out var schemaError))` in `SubmissionBuilder.cs`; `var codeBlocks = ReadCodeBlocks(fields);` in `PostEnvelope.cs`; `"curia/content/author-principal-mismatch",` in `ContentErrors.cs`; `: StatusCodes.Status401Unauthorized,` and `string.Equals(verifyError!.Type, LogBoundKeys.LogUnreadableType, StringComparison.Ordinal)` in `ForumEndpoints.cs`; `.Where(p => string.Equals(p.Prev, digest, StringComparison.Ordinal))` in `CitationCheck.cs`; and the nine literals `"Not permitted at this trust tier"` in `ForumEndpoints.cs` (nine occurrences, by enclosing method: `SubmitAttemptAsync`, `RaiseFlagAsync`, `ListRaisedFlagsAsync`, `ListPostFlagsAsync`, `AuthorizeFlagListAsync`, `AcceptAnswerAsync`, `InboxAsync`, `AuthorizeKeyActAsync`, `PrincipalOf`).
- [ ] Record each of the eleven test assemblies' passed counts at the base (Release), and `cargo test --locked`'s. Run `ScreeningCostTests` alone once and keep both timing files as the base reading (this stage must not move it).
- [ ] **The probe of the reading** (not yet a commit): run H1's body by hand against a host if one is up, or defer to Task 1 Step 3. The architect's 401 finding is read, not run; Task 1 Step 3 is where it is proved or the spec is corrected.

## Task 1: Admission rules and the status of a VERIFY refusal (R8.64, R6.58, D48 item 3, D38 ingest half)

**Files.** Modify `src/Curia.Domain/Content/PostKind.cs`, `ContentErrors.cs`, `PostEnvelope.cs`; `src/Curia.Application/Ingest/IngestPipeline.cs`; `src/Curia.Api/ForumEndpoints.cs`; `src/Curia.Client/SubmissionBuilder.cs`. New `src/Curia.Api/VerifyRefusals.cs`. Tests as in the file structure table.

- [ ] **Step 1: the facts first (P1 to P6, I1 to I3, S1, S2, V1 to V3, H1 to H6, B1, K1, K2).**
  - `tests/Curia.Domain.Tests/Content/PrevAdmissionTests.cs` (NEW), shaped like `SignalEnvelopeTests` (copy its private `Envelope(kind, extra…)` and `M(...)` helpers; they are private to that file). A local `Valid(PostKind kind)` adds exactly the members the kind needs, chosen by `PostKinds.RequiresTitle`, `RequiresParent`, `RequiresTarget`, `RequiresBody` (title, parent `"01P"`, target `"sha256:" + 64 a`, a vote's `endorse`/`predicted_endorsement_bp`/`epoch`, a verification's `method`/`result`/`refs`), so a kind that gains a requirement fails here and not in a copy. P1 asserts `PostKinds.RequiresPrev(k)` is `k == PostKind.Revision` for every `PostKind`. P2 asserts, for the `prev` values absent, `""` and `"   "`, that `PostEnvelope.Read(Valid(Revision) + prev)` is Ok and `PostEnvelope.AdmissionRefusal(read)` has `Type == "curia/content/prev-required"`. P3 asserts, for each of the six other kinds with `prev = "sha256:" + 64 'b'`, `Read` is Ok and `AdmissionRefusal` is `curia/content/prev-not-allowed`. P4 asserts `Read` is Ok for `Valid(Comment) + prev` and for `Valid(Revision)` with no `prev`, and `Read(...).Value.Prev` carries the comment's. P5 asserts `AdmissionRefusal` is null for a revision with `prev` `"sha256:" + 64 'b'` and with `"sha-256:" + 43 'a'`. P6 asserts both types start with `ContentErrors.TypePrefix`, the `Detail` is `PostKinds.Wire(kind)`, and `ContentErrors.AuthorIsNotThePrincipal().Type == ContentErrors.AuthorMismatchType`.
  - `IngestPipelineTests.cs`: give the file's `Wire(Harness, PostKind kind = …, string? parent = null, …)` a `string? prev = null` parameter after `parent`, written beside it: `if (prev is not null) members.Add(new("prev", new JsonValue.String(prev)));`. I1: a revision with `parent: "01J0000000000000000000000A"` and no `prev`; admit, `VerifyAsync(admitted!, Typed.Agent(Agent), ct)`, assert `Assert.False(verified.TryGetValue(out _, out var error))` and `Assert.Equal("curia/content/prev-required", error!.Type)`. I2: a comment with `parent` and `prev: "sha256:" + 64 'b'`, `curia/content/prev-not-allowed`. I3: the I1 revision with `prev`, `Assert.True(verified.TryGetValue(…))`. Model all three on `Kind_specific_obligations_are_enforced` (the file's parent fact).
  - `tests/Curia.Application.Tests/Projections/StoredPrevReplayTests.cs` (NEW). A local `Canonical(...)` builds a valid **comment** envelope (`parent`, `prev`) the way `SearchProjectorTests.Canonical` builds a question (copy its shape: `v`, `kind`, `author`, `board`, `parent`, `prev`, `body`, `code_blocks`, `refs`, `tags`, `content_type`, `created_at`, `nonce`; `CanonicalJson.CanonicalizeWithNfc`), and an `AcceptAsync` appends a `post.accepted` event for it with `kind: "comment"`, `parent` and the payload members `SearchProjectorTests.AcceptAsync` writes. S1: `Assert.Single(SearchProjector.Fold(log))` and the post's `Kind` is `PostKind.Comment`. S2: `PostProjector.Fold(log)` has the post with `Prev` equal to the one signed. Both green today; S1 is the discriminator for FR6, S2 is the control, and its doc comment says so.
  - `tests/Curia.Api.Tests/VerifyRefusalsTests.cs` (NEW; no fixture needed). V1: for every public static method on `ContentErrors` whose return type is `Error`, invoke it with default arguments (`int` 0, `PostKind` `Question`, `string` `"x"`; any other parameter type fails the test naming the method), and assert `VerifyRefusals.StatusOf(error)` is 400 except the method named `AuthorIsNotThePrincipal`, which is 401 (V2). Assert at least 10 factories were invoked (non-vacuity). V3: `new Error(LogBoundKeys.LogUnreadableType, "t")` 503; `IngestErrors.UnstorableMember("board")` 422; `AuthorKeyErrors.Retired("a", "k")` and `Compromised("a", "k")` 403; `AuthorKeyErrors.NotRegisteredToAgent("a", "k")` 401; a `curia/jws/signature-invalid` error 401; a `curia/canon/duplicate-normalized-key` error 401 (spec 7, question 2: held so the question is visible).
  - `tests/Curia.Api.Tests/RefusalStatusTests.cs` (NEW, `[Collection("forum")]`, the shape of `RevisionOwnershipTests`: `AuthenticatedAsync`, `AskAsync`, `dpop.PostAsync`). H1: `agent.Sign(PostKind.Answer, board, "an answer", title: null, parent: null, forum.Now)`; status `BadRequest`, body type `curia/content/parent-required`. H2: `agent.Sign(PostKind.Question, board, "a question", title: null, parent: null, forum.Now)`; `curia/content/title-required`. H3: `agent.Sign(PostKind.Question, board, "a question", "Title", parent: "01J0000000000000000000000A", forum.Now)`; `curia/content/parent-not-allowed`. H4: after `AskAsync`, `agent.Sign(PostKind.Comment, board, "a comment", null, parent: postId, forum.Now, prev: digest)`; `BadRequest`, `curia/content/prev-not-allowed`, and `GET /v1/boards/{board}/posts` still lists exactly the question (the shape of `TwoAgentsConversationTests` line ~201).
  - `RevisionOwnershipTests.cs`: H5, edit `R6_7_ARevisionThatChainsToNothingIsRefused` to `Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode)` and assert the body contains `curia/content/prev-required`; its doc comment now says why (R8.64, not ownership). H6: in `ARevisionChainingToAnUnknownDigestIsRefused` keep `Forbidden` and add `Assert.Contains("own-resource-only", body)`; its doc comment says why the digest's form is not validated (ruling 7: no oracle, and the fact signs a malformed digest on purpose).
  - `SubmissionBuilderTests.cs`: B1 beside `D48_ARevisionDraftSignsItsPrev`: `new PostDraft { Kind = PostKind.Revision, Board = "b", Body = "x", Parent = "01J00000000000000000000000" }` (no `Prev`), `Assert.False(SubmissionBuilder.Build(_agent, draft, When).TryGetValue(out _, out var error))`, `error!.Type == "curia/client/envelope-invalid"`, `error.Detail` contains `prev-required`.
  - `RefusalClassificationTests.cs`: K1 with `Interpret(400, """{"type":"curia/content/prev-required","title":"A revision must name the digest it revises as prev","detail":"revision"}""")`: `RefusalKind.Content`, `Cli.ExitCode.For(refusal) == 3`. K2: status 401 with `curia/content/author-principal-mismatch`: `RefusalKind.Authentication`, exit 8. (`ExitCode` is `internal` to `Curia.Client.Cli`, visible to `Curia.Client.Tests`; the file already imports `Curia.Client.Cli` in `RefusalOutputTests`.)
- [ ] **Step 2: build the red.** The new tests reference `PostKinds.RequiresPrev`, `PostEnvelope.AdmissionRefusal`, `ContentErrors.TypePrefix`, `AuthorMismatchType`, `PrevRequired`, `PrevNotAllowed` and `VerifyRefusals`, which do not exist, so they do not compile. Add the declarations only, bodies `throw new NotImplementedException()`, so the solution builds, and run the new facts: record each `Failed` line. Expected red: all of P1 to P6, I1 to I3, V1 to V3, H1 to H5, B1, K1 (K2 and S1, S2, H6 are expected green: they pin what must not move). **H1, H2 and H3 are the probe**: if any is green with the stubbed mapping unwired, the reading in spec 2.2 is wrong; stop and correct the spec first.
- [ ] **Step 3: run H1 to H3 against the unmodified status mapping** (stubs present, `SubmitAttemptAsync` untouched) to prove the 401 on the live path: `dotnet test tests/Curia.Api.Tests -c Release --filter "FullyQualifiedName~RefusalStatusTests"`. Expected: H1 to H3 fail with `Unauthorized` where `BadRequest` was expected. Record the lines. This is the evidence that the register's new entry rests on.
- [ ] **Step 4: implement.**
  - `PostKind.cs`, after `RequiresTarget`'s body line `Match(kind, () => false, () => false, () => false, () => false, () => false, () => true, () => true);`:

```csharp
    /// <summary>
    /// R8.64 (errata G24): Table 12 lists <c>prev</c> as a required member of a <c>revision</c>, and Table 9
    /// defines it as the digest of the previous revision. No other kind carries it.
    /// </summary>
    public static bool RequiresPrev(PostKind kind) =>
        Match(kind, () => false, () => false, () => false, () => false, () => true, () => false, () => false);
```

  - `ContentErrors.cs`: add

```csharp
    /// <summary>R6.58: every refusal of an envelope's own content has a type that starts here.</summary>
    public const string TypePrefix = "curia/content/";

    /// <summary>R6.58: the one content refusal that answers 401, an identity claim the credential does not support.</summary>
    public const string AuthorMismatchType = "curia/content/author-principal-mismatch";

    /// <summary>R8.64: Table 12 requires <c>prev</c> of a revision.</summary>
    public static Error PrevRequired(PostKind kind) => new(
        "curia/content/prev-required",
        "A revision must name the digest it revises as prev",
        PostKinds.Wire(kind));

    /// <summary>R8.64: <c>prev</c> chains a revision; on any other kind it is a claim the stored post cannot honour.</summary>
    public static Error PrevNotAllowed(PostKind kind) => new(
        "curia/content/prev-not-allowed",
        "Only a revision carries prev",
        PostKinds.Wire(kind));
```

    and `AuthorIsNotThePrincipal` uses `AuthorMismatchType` in place of the literal `"curia/content/author-principal-mismatch",`.
  - `PostEnvelope.cs`, before `private static Result<PostEnvelope> Fail(Error error) => Result<PostEnvelope>.Fail(error);`:

```csharp
    /// <summary>
    /// R8.64 (errata G24): the two rules about <c>prev</c> that apply to a submission and to nothing the log already holds.
    /// A revision carries a non-blank <c>prev</c>; every other kind carries none. Called by VERIFY and by the client's
    /// pre-check, from this one function.
    ///
    /// <para><b>Not part of <see cref="Read"/>.</b> <see cref="Read"/> is applied to every stored post by the search and
    /// signal projections and by the client's passage, and a post that fails it is dropped on replay (R11.31). Posts
    /// that carry <c>prev</c> on a non-revision may be stored. The digest's form is not checked: a <c>prev</c> that
    /// names nothing the author wrote is the ownership refusal's (ruling 7), so that "not yours" and "not there" read alike.</para>
    /// </summary>
    public static Error? AdmissionRefusal(PostEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (PostKinds.RequiresPrev(envelope.Kind))
            return string.IsNullOrWhiteSpace(envelope.Prev) ? ContentErrors.PrevRequired(envelope.Kind) : null;

        return envelope.Prev is not null ? ContentErrors.PrevNotAllowed(envelope.Kind) : null;
    }
```

  - `IngestPipeline.cs`, directly after the lines `if (!read.TryGetValue(out var envelope, out var schemaError))` / `return Result<VerifiedSubmission>.Fail(schemaError!);`:

```csharp
        // R8.64 (errata G24): the rules about `prev` that apply to a submission and not to a stored post, so not PostEnvelope.Read's.
        if (PostEnvelope.AdmissionRefusal(envelope!) is { } admissionRefusal)
            return Result<VerifiedSubmission>.Fail(admissionRefusal);
```

  - `SubmissionBuilder.cs`: replace `if (!PostEnvelope.Read(envelope).TryGetValue(out _, out var schemaError))` with `if (!PostEnvelope.Read(envelope).TryGetValue(out var read, out var schemaError))`, and after its `return Result<SignedSubmission>.Fail(ClientErrors.EnvelopeInvalid(...));` statement add:

```csharp
        if (PostEnvelope.AdmissionRefusal(read!) is { } admission)
            return Result<SignedSubmission>.Fail(
                ClientErrors.EnvelopeInvalid($"{admission.Type}: {admission.Title}"));
```

    and extend the comment above the first check to say it now reads the Forum's admission rule as well.
  - `src/Curia.Api/VerifyRefusals.cs` (NEW):

```csharp
using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Http;

namespace Curia.Api;

/// <summary>
/// R6.58 (errata G24): the status of a refusal VERIFY makes. A refusal of the envelope's own content is a request
/// problem and answers 400; 401 and 403 are for a credential that fails and an act the principal may not do.
/// Public, and a type of its own, so that a fact can drive it without a new <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class VerifyRefusals
{
    public static int StatusOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (string.Equals(error.Type, LogBoundKeys.LogUnreadableType, StringComparison.Ordinal))
            return StatusCodes.Status503ServiceUnavailable;

        if (string.Equals(error.Type, IngestErrors.UnstorableMemberType, StringComparison.Ordinal))
            return StatusCodes.Status422UnprocessableEntity;

        if (error.Type is AuthorKeyErrors.RetiredType or AuthorKeyErrors.CompromisedType)
            return StatusCodes.Status403Forbidden;

        // By prefix, so a content refusal added later answers 400 without an edit here.
        if (error.Type.StartsWith(ContentErrors.TypePrefix, StringComparison.Ordinal)
            && !string.Equals(error.Type, ContentErrors.AuthorMismatchType, StringComparison.Ordinal))
            return StatusCodes.Status400BadRequest;

        return StatusCodes.Status401Unauthorized;
    }
}
```

    (Match the `using` list to what compiles: `LogBoundKeys` is in `Curia.Application.Credentials`, `IngestErrors` in `Curia.Application.Ingest`, `AuthorKeyErrors` in `Curia.Application.Ports`; confirm by the file's neighbours and let the compiler say.) In `SubmitAttemptAsync` replace the whole `return Problem(string.Equals(verifyError!.Type, LogBoundKeys.LogUnreadableType, …) ? … : StatusCodes.Status401Unauthorized, verifyError);` expression with `return Problem(VerifyRefusals.StatusOf(verifyError!), verifyError!);`, and edit the comment above `PrincipalOf(subject)` ("A log that could not be read decides nothing about the key (R4.35), so it is a 503 …") to point to `VerifyRefusals` for the mapping.
- [ ] **Step 5: green.** `dotnet build Curia.sln -c Release` (0 warnings), then the Task 1 facts: all green. Run the whole Domain, Application, Client and Api assemblies: any existing fact that goes red is read; the ones this change moves are `R6_7_ARevisionThatChainsToNothingIsRefused` (edited above) and none other. **The fuzzer** (`Curia.Api.Tests/Fuzz`) is part of the Api assembly: its oracle is "no 5xx, every refusal a problem document", which a status change does not break; a fuzz row that goes red is read, not edited around.
- [ ] **Step 6: commit.** `A revision without prev is a 400, prev on any other kind is refused, and a VERIFY schema refusal is a 400 (R8.64, R6.58, D48 item 3)`.

**Falsification rows (run at Task 6).**

| # | File | Anchor → replacement | Must go red |
|---|---|---|---|
| FR1 | `PostEnvelope.cs` | `return string.IsNullOrWhiteSpace(envelope.Prev) ? ContentErrors.PrevRequired(envelope.Kind) : null;` → `return null;` | P2, I1, H5, B1 |
| FR2 | `PostEnvelope.cs` | `return envelope.Prev is not null ? ContentErrors.PrevNotAllowed(envelope.Kind) : null;` → `return null;` | P3, I2, H4 |
| FR3 | `PostKind.cs` | `Match(kind, () => false, () => false, () => false, () => false, () => true, () => false, () => false);` → `Match(kind, () => false, () => false, () => false, () => false, () => false, () => false, () => false);` | P1, P2, P5, I1, I3, H5 |
| FR4 | `IngestPipeline.cs` | `if (PostEnvelope.AdmissionRefusal(envelope!) is { } admissionRefusal)` → `if (envelope!.Nonce.Length == 0 && PostEnvelope.AdmissionRefusal(envelope!) is { } admissionRefusal)` | I1, I2, H4, H5 (P1 to P6 stay green: the domain facts do not cover the wiring) |
| FR5 | `SubmissionBuilder.cs` | `if (PostEnvelope.AdmissionRefusal(read!) is { } admission)` → `if (read!.Nonce.Length == 0 && PostEnvelope.AdmissionRefusal(read!) is { } admission)` | B1 |
| FR6 | `PostEnvelope.cs` | `        var codeBlocks = ReadCodeBlocks(fields);` → `        if (OptionalString(fields, "prev") is not null && !PostKinds.RequiresPrev(kind)) return Fail(ContentErrors.PrevNotAllowed(kind));` + newline + `        var codeBlocks = ReadCodeBlocks(fields);` (the rule in `Read`) | P3, P4, **S1** (the stored comment drops out of search); **S2 stays green** and is recorded as the control |
| FR7 | `VerifyRefusals.cs` | `&& !string.Equals(error.Type, ContentErrors.AuthorMismatchType, StringComparison.Ordinal)` → `&& error.Type.Length > 0` | V1, V2, `An_impersonated_author_is_rejected`, `SignedAuthorTests`, `IngestContentionTests`' mismatch row |
| FR8 | `VerifyRefusals.cs` | `error.Type.StartsWith(ContentErrors.TypePrefix, StringComparison.Ordinal)` → `error.Type.StartsWith(ContentErrors.TypePrefix + "x", StringComparison.Ordinal)` | V1, H1 to H5 (V2 stays green: the mismatch is 401 either way) |
| FR9 | `ForumEndpoints.cs` | `return Problem(VerifyRefusals.StatusOf(verifyError!), verifyError!);` → `return Problem(StatusCodes.Status401Unauthorized, verifyError!);` | H1 to H5, and whichever existing Api facts pin 422, 503 or 403 for a VERIFY refusal (record which) |

## Task 2: A successor is a revision by the original's author (R9.28, D38 projection half)

**Files.** Modify `src/Curia.Application/Projections/CitationCheck.cs`. Tests: `tests/Curia.Application.Tests/Projections/CitationCheckTests.cs`; `tests/Curia.Api.Tests/BatchRetrievalTests.cs`, `ForumFixture.cs`.

- [ ] **Step 1: the facts first (C1 to C5, A1).**
  - `CitationCheckTests.cs`: widen the file's private helper `Post(string id, string digest, string? prev = null)` to `Post(string id, string digest, string? prev = null, string? kind = null, string author = "https://agents.example/a")`, keeping its defaults (`kind` is `prev is null ? "question" : "revision"`, parent `prev is null ? null : "q-1"`), so every existing fact is unchanged. C1: `Post("q-1", D1)` by author `a`, `Post("c-1", D2, prev: D1, kind: "comment", author: "https://agents.example/b")`; `Resolve(D1, …)` is `Current` with empty `Successors`. C2: the comment by author `a` itself (same author, kind `comment`, and `question` as a second row via `[Theory]`): `Current`, no successors. C3: a `revision` by `b` naming D1: `Current`, no successors. C4: a `revision` by `a`: `Superseded`, `Successors == [D2]`, not forked. C5: `Resolve(D3, …)` where only a revision by `a` names D3 and no post bears D3: `Unknown`, `Successors` empty.
  - `ForumFixture.cs`: `internal async Task<(string PostId, string Digest)> AppendStoredPostAsync(byte[] wire, CancellationToken ct)`, beside `WithholdAsync`, doc-commented as "the shape of a post accepted before R8.64: the one way to put a `prev` on a comment in the log now". It parses `wire` (`JsonReader.ParseUnrestricted`), takes the `envelope` and `signature` members, canonicalizes the envelope (`CanonicalJson.CanonicalizeWithNfc`), computes `Digests.Sha256(canonical).ToPrefixed()`, mints the id with `UlidGenerator` as `IngestPipeline.PersistAsync` does, builds the `post.accepted` payload with the members `PersistAsync` writes (`post_id`, `canonical`, `signature`, `digest`, `author`, `board`, `kind`, `parent`, `server_ts`, `risk_flags: []`), and appends it through the host's `IEventStore` (`Services.CreateScope()`, as `WithholdAsync` does) on a new aggregate. It does not go through the pipeline, which is the point.
  - `BatchRetrievalTests.cs`: A1. Agent A asks (`PostAsync` with `SignQuestion`), agent B is a second `AgentAsync`; B's comment is `agentB.Agent.Sign(PostKind.Comment, board, "a comment", null, parent: questionId, forum.Now, prev: original)`; stored with `forum.AppendStoredPostAsync(wire, ct)`. `BatchAsync(http, new { digests = new[] { original } })`: item 0 `state` is `current`, `successors` is empty, `forked` is false. Add a second row in the same fact: B's stored comment's own digest is `current` with no successors (it is served, and it is not superseded by anything).
- [ ] **Step 2: red.** C1, C2, C3, C5 and A1 fail against the unmodified `Resolve` (it counts any post whose `Prev` names the digest); C4 is green (control). Record the `Failed` lines; A1's is D38's reproduction.
- [ ] **Step 3: implement.** In `CitationCheck.Resolve` replace the block that begins `// Every revision whose prev names this digest, in log order.` and the `var successors = posts.Where(p => string.Equals(p.Prev, digest, StringComparison.Ordinal)).Select(p => p.Digest).ToImmutableArray();` statement, and the `var post = posts.FirstOrDefault(...)` line after it, with the original located first:

```csharp
        var post = posts.FirstOrDefault(p => string.Equals(p.Digest, digest, StringComparison.Ordinal));

        // R9.28 (errata G24): a successor is a revision by the original's own author, the only way an author
        // corrects a post. A comment, or a revision by someone else, that names the digest corrects nothing, and
        // `superseded` would tell a citer the original stands corrected when no one with standing said so.
        // Computed before the serving checks below, because a withheld original still has successors worth
        // reporting. An original the log does not hold has no author to compare, so it has none.
        var successors = post is null
            ? ImmutableArray<string>.Empty
            : [.. posts.Where(p => IsCorrectionOf(p, post, digest)).Select(p => p.Digest)];
```

  and add

```csharp
    private static bool IsCorrectionOf(PostView candidate, PostView original, string digest) =>
        string.Equals(candidate.Prev, digest, StringComparison.Ordinal)
        && PostKinds.TryParse(candidate.Kind, out var kind) && kind is PostKind.Revision
        && string.Equals(candidate.Author, original.Author, StringComparison.Ordinal);
```

  The `CitationState.Successors` and `CitationStatus.Superseded` doc comments name the rule ("Digests of the revisions … by the original's author"). The remaining `Resolve` body is unchanged.
- [ ] **Step 4: green and commit.** The Task 2 facts and `R9_19_ARevisionMarksItsPredecessorSuperseded`, `R9_19_AnItemIsExactlyWhatTheSingleReadServes` green. Commit: `successors and superseded count only a revision by the original's author (R9.28, D38)`.

**Falsification rows.**

| # | File | Anchor → replacement | Must go red |
|---|---|---|---|
| FR10 | `CitationCheck.cs` | `        && PostKinds.TryParse(candidate.Kind, out var kind) && kind is PostKind.Revision` → `        && candidate.Kind.Length > 0` | C2 (C1 and A1 stay green: the author filter still excludes another agent's comment) |
| FR11 | `CitationCheck.cs` | `        && string.Equals(candidate.Author, original.Author, StringComparison.Ordinal);` → `        && candidate.Author.Length > 0;` | C3 (C1 and A1 stay green: the kind filter still excludes a comment) |
| FR12 | `CitationCheck.cs` | `.Where(p => IsCorrectionOf(p, post, digest))` → `.Where(p => string.Equals(p.Prev, digest, StringComparison.Ordinal))` | C1, C2, C3, A1 (the old behaviour: A1 is D38's reproduction) |
| FR13 | `CitationCheck.cs` | `            ? ImmutableArray<string>.Empty` → `            ? [.. posts.Where(p => string.Equals(p.Prev, digest, StringComparison.Ordinal)).Select(p => p.Digest)]` | C5 |

## Task 3: A denial's title names its cause (R7.24, D37's open item)

**Files.** Modify `src/Curia.Domain/Authorization/AccessPolicy.cs`, `src/Curia.Api/ForumEndpoints.cs`. Tests as in the file structure table; `tests/Shared/StubLog.cs` is **not** edited (report item 3).

- [ ] **Step 1: the facts first (T1 to T5, R1 to R6).**
  - `AccessPolicyTests.cs` (it has private `Request(...)` and `Decide(...)` helpers; reuse them). T1: for every `(Resource, Action)` in `ResourceActionModel.ModelledPairs` x every `PrincipalTier` x every `CredentialState` x `postsToday` in {0, `TierPolicy.PostsPerDay(tier)`, `int.MaxValue`} x `flagsToday` in {0, `TierPolicy.FlagsPerDay(tier)`} (only where the pair is `Flag`/`Raise`, which requires a count), collect `AccessPolicy.Decide(...)`'s decisions; for every allow carrying a qualifier also collect `decision.Discharge(false)`. Collect the distinct `Reason` of each `DecisionEffect.Deny`. Assert the set is not empty, and that for each reason `AccessPolicy.TitleFor(reason)` is not `AccessPolicy.NeutralDenialTitle` and is distinct from every other reason's title. T2: for every reason in the set other than `table-10/denied`, the title does not contain `"trust tier"`; and `TitleFor("table-10/denied")` does. T3: `TitleFor(null)`, `TitleFor("")` and `TitleFor("table-99/unheard-of")` all equal `NeutralDenialTitle`, which does not contain `"trust tier"`. T4: read `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (walk up as `PublishedTable10` does), take the `**R7.24**` paragraph to the next blank line, read the cause list that follows `SHALL name its cause:` up to the next `.`, split on `, ` and ` or ` into items, and assert there are seven and equal the number of distinct deny reasons T1 observed; throw naming the errata if the sentence is not found (the parse must not return empty). T5: read `src/Curia.Api/ForumEndpoints.cs` and assert `"Not permitted at this trust tier"` does not occur (the title lives only in `AccessPolicy`).
  - Route facts, each editing an existing fact in place and adding one assertion on the served `title`: R1 `RateBudgetTests` (the fact that asserts `table-11/rate-budget-exhausted`, line ~55): title contains `"budget"` and not `"trust tier"`. R2 `FlagEndpointTests` (the eleventh-flag fact, line ~488): title contains `"flag budget"`. R3 `RevisionOwnershipTests.Table10_AnotherAgentMayNotReviseAPostItDidNotWrite`: title contains `"does not exist"` and not `"trust tier"`; `ARevisionChainingToAnUnknownDigestIsRefused` (H6) asserts the same, which is ruling 7 in a fact. R4 `FlagListingTests` (line ~210, `table-10/own-resource-only`): the same. R5 `AcceptAnswerTests` (line ~149, `own-thread-only`): title contains `"thread"` and not `"trust tier"`. R6 `KeyRotationEndpointTests.Table10_QuarantineRefusesRotationAndPermitsRetirement`: title (the third value `KeyActRequests.ProblemOfAsync` returns, or the body's `title`) contains `"quarantine"`.
  - `RefusalClassificationTests.cs`: change the `detail` and `title` pairing of the two budget rows (`table-11/rate-budget-exhausted tier=T1` and `table-11/flag-budget-exhausted tier=T0`) to the titles Step 3 gives them; leave the `table-10/denied` row as it is.
- [ ] **Step 2: red.** The facts reference `AccessPolicy.TitleFor`, `NeutralDenialTitle`, and (Task 4's U3) `DenialReasons`; add the declarations returning the tier title, build, and record. Red: T1, T2, T3, T4 (if `DenialReasons` is consulted; T4 only counts reasons), T5, R1 to R6. Green: `RefusalClassificationTests` after its data edit.
- [ ] **Step 3: implement.** In `AccessPolicy.cs`, beside `AuthorizationDecision.ReasonFor`:

```csharp
    /// <summary>R7.24 (errata G24): the title of a denial whose reason names no cause this table knows. Never the tier's.</summary>
    public const string NeutralDenialTitle = "Not permitted";

    private static readonly FrozenDictionary<string, string> DenialTitles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["table-10/denied"] = "Not permitted at this trust tier",
            ["table-10/own-resource-only"] = "Not permitted: this is not your resource, or it does not exist",
            ["table-10/own-thread-only"] = "Not permitted: only the agent that started the thread may do this",
            ["table-10/delegated-grant-required"] = "Not permitted without a delegated grant",
            ["table-11/rate-budget-exhausted"] = "Not permitted: today's posting budget is spent",
            ["table-11/flag-budget-exhausted"] = "Not permitted: today's flag budget is spent",
            ["table-11/quarantined-read-only"] = "Not permitted: this credential is quarantined and may only read",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The reasons a denial can carry, in no order: the keys of the title table, so a reason added to the PDP without a title fails T1.</summary>
    public static IReadOnlyCollection<string> DenialReasons => DenialTitles.Keys;

    /// <summary>R7.24: the title of a denial, chosen from its reason and from nothing else. An unknown or absent reason is neutral.</summary>
    public static string TitleFor(string? reason) =>
        reason is not null && DenialTitles.TryGetValue(reason, out var title) ? title : NeutralDenialTitle;
```

  with `using System.Collections.Frozen;`, a `Title` property on `AuthorizationDecision` (`public string Title => AccessPolicy.TitleFor(Reason);`) and the wording above (the facts hold the property, not the words; U3 reads `DenialReasons`). `AccessPolicy.cs` is `Curia.Domain`, which depends on nothing outside the BCL; `FrozenDictionary` is the BCL (as `PostKinds.ByWire` uses it).
  - `ForumEndpoints.cs`: add `private static Error Denial(AuthorizationDecision decision, string detail) => new("curia/authz/denied", decision.Title, detail);` beside `Problem`, and replace the eight literals that have a decision, keeping each detail exactly: `SubmitAttemptAsync` (`$"{d.Reason} tier={tier.Tier}"`), `RaiseFlagAsync` (same), `ListRaisedFlagsAsync` (`decision.Reason`), `ListPostFlagsAsync` (`$"{decision.Reason} post={postId}"`), `AuthorizeFlagListAsync` (`d.Reason`), `AcceptAnswerAsync` (`$"{d.Reason} tier={tier.Tier}"`), `InboxAsync` (`d.Reason`), `AuthorizeKeyActAsync` (`$"{d.Reason} tier={tier.Tier}"`). `PrincipalOf` has no decision: `new Error("curia/authz/denied", AccessPolicy.TitleFor(null), "the token's subject names no agent")`. The `"Anonymous read is not permitted here"` title (`d.Reason` detail) is a different title, judged correct, and is left. `Problem`, `RevisesOwnPost` and the VERIFY mapping are untouched.
- [ ] **Step 4: green and commit.** Task 3 facts green; the whole Domain, Api and Client assemblies green (`StubFidelityTests` included: the stub's tier title is the real one for `table-10/denied`). Commit: `A denial's title names its cause (R7.24, D37)`.

**Falsification rows.**

| # | File | Anchor → replacement | Must go red |
|---|---|---|---|
| FR14 | `AccessPolicy.cs` | `            ["table-11/flag-budget-exhausted"] = "Not permitted: today's flag budget is spent",` → *(deleted)* | T1, R2 (T4 stays green: it counts the PDP's reasons, not the table's rows) |
| FR15 | `AccessPolicy.cs` | `["table-11/rate-budget-exhausted"] = "Not permitted: today's posting budget is spent",` → `["table-11/rate-budget-exhausted"] = "Not permitted at this trust tier",` | T2, R1 |
| FR16 | `AccessPolicy.cs` | `public const string NeutralDenialTitle = "Not permitted";` → `public const string NeutralDenialTitle = "Not permitted at this trust tier";` | T3, T2 |
| FR17 | `ForumEndpoints.cs` | `Denial(decision, decision.Reason)` → `new Error("curia/authz/denied", "Not permitted at this trust tier", decision.Reason)` (the `ListRaisedFlagsAsync` site, counted once) | T5 only: no route fact reaches a title there |
| FR18 | `ForumEndpoints.cs` | `Denial(decision, $"{decision.Reason} post={postId}")` → `new Error("curia/authz/denied", "Not permitted at this trust tier", $"{decision.Reason} post={postId}")` | T5, R4 |
| FR19 | `ForumEndpoints.cs` | `new("curia/authz/denied", decision.Title, detail)` → `new("curia/authz/denied", "Not permitted at this trust tier", detail)` | T5, R1 to R6 |
| FR20 | `ForumEndpoints.cs` | `AccessPolicy.TitleFor(null), "the token's subject names no agent"` → `"Not permitted at this trust tier", "the token's subject names no agent"` | T5 |

## Task 4: The client's words are true (D37's reopened items)

**Files.** Modify `src/Curia.Client/ForumResult.cs`; `src/Curia.Client.Cli/Program.cs` (`Summary`, `ShowKeysAsync`). Tests: `HelpTextTests.cs`, `RefusalClassificationTests.cs`, `KeyActVerbTests.cs`, `DpopFlowTests.cs`.

- [ ] **Step 1: the facts first (U1 to U6).**
  - `HelpTextTests.cs`: lift the `verbs` map out of `D37_TheTierReminderListsWhatTable10GrantsT0` into a `private static` field the two facts share (the existing fact is unchanged in behaviour). U1: build the refusal with `Interpret(403, """{"type":"curia/authz/denied","title":"Not permitted at this trust tier","detail":"table-10/denied tier=T0"}""")` (the file's or `RefusalClassificationTests`' `Interpret`; add a one-line copy if it is private), take `refusal.Summary`, compute `t1Only` from `PublishedTable10` exactly as the existing fact does, and assert the summary contains every verb of every `t1Only` grant as a whole word; assert it contains `TierPolicy.T1MinimumHours` and `TierPolicy.T1MinimumCleanQuestions` and `TierPolicy.T2MinimumDaysAtT1` as invariant-culture numbers (so the numbers it quotes are the published ones); assert it does not contain `"nothing else"`, `"may ask and comment"` or `"Waiting is the only remedy"`; assert it contains `"owner"` (time alone is not enough). U2: for details `table-10/own-resource-only tier=T0` and `table-10/own-thread-only tier=T0`, the summary does not contain `"Waiting"`, contains `"higher tier"`, and does not contain `"48 hours"`. U3: for every reason in `AccessPolicy.DenialReasons` except the two `table-11/...-budget-exhausted` ones, `Interpret(403, …detail: reason + " tier=T0")` is `RefusalKind.Authorization`; the summaries are pairwise distinct once the reason text is removed; none contains `"nothing else"` or `"only remedy"`.
  - `KeyActVerbTests.cs`: U4 beside `R4_39_KeysIsCleanWhenTheLifecycleIsTheProfilesOwn`: the printed stdout contains `"the Forum's word"` and `"curia verify"` on one line, and that line precedes the first `key ` line. U5: with a key set whose enabled kinds are empty (the file's double has a way to set `EnabledKinds`; if it has none, add one beside the existing one), the stdout contains `lifecycle kinds this Forum enables: none (every lifecycle kind is guarded until the owner lifts the guard, R4.38)`; and the existing nonempty line (`lifecycle kinds this Forum enables: "rotation", "retirement"`, line ~72) is unchanged.
  - `DpopFlowTests.cs` beside `TheInboxIsADpopBoundGetCarryingTheAgentsFilters` (line ~561): U6 builds an `InboxPage(Results: [one post], NextCursor: null, OpenBeforeExclusions: 3, ExcludedAsOwn: 1, ExcludedAsAlreadyAnswered: 1)` (or reuses the page that fact reads) and asserts `Program.Summary(inbox).ToString()` contains `"3 open before exclusions"`, `"1 yours"`, `"1 already answered by you"` and `"1 listed"`, and does not contain `"3 open,"`.
- [ ] **Step 2: red.** U1 to U6 fail on the old text. Record each.
- [ ] **Step 3: implement.**
  - `ForumResult.cs`: replace the `RefusalKind.Authorization => new FrameBuilder()…` arm with a call to a private `AuthorizationSummary()` that begins `.Append($"{Error.Title} ({Error.Detail}). ")` (title and detail stay quoted display literals, R10.63) and appends, by the detail's prefix (`StartsWith(…, Ordinal)` on `Error.Detail`, as `IsFlagBudget` does), one of:
    - `table-10/denied`: `Your trust tier does not permit this action. T1 adds answer, endorse, reproduce and contradict, and needs 48 hours since enrolment, 3 questions with no upheld flags and an owner verified by the Forum's operator, so time alone is not enough; T2 adds finding and needs 30 days at T1.`
    - `table-10/own-resource-only`: `This act is open only to the agent that owns the resource it names, and the one named is not yours or does not exist. A higher tier does not change that; check the id or digest you named.`
    - `table-10/own-thread-only`: `Only the agent that started the thread may do this. A higher tier does not change that.`
    - `table-10/delegated-grant-required`: `This act needs a delegated grant from the Forum's operator. Neither a higher tier nor waiting supplies one.`
    - `table-11/quarantined-read-only`: `This credential is quarantined: it may read and retire its own key; every other act is refused until the operator lifts the quarantine. A higher tier does not change that.`
    - anything else: `Retrying will not change this.`
    The literals are this client's own (not `Error` values), so they go through `.Append($"…")` with no interpolation, as the old sentences did. Update the `RefusalKind.Authorization` doc comment.
  - `Program.cs` `ShowKeysAsync`: after the loop that writes the `key …` lines and before `lifecycle published through log index`, add `frame.Line($"these lines are the Forum's word: its key set as served; only a signed head, checked by curia verify, anchors a key's binding");` (a literal line, no interpolated Forum value), placed **before** the first `key ` line (U4 asserts the order; build it into the `frame` before the loop). Replace `enabled.IsEmpty ? new OwnText("none") : Literals(enabled)` with `enabled.IsEmpty ? new OwnText("none (every lifecycle kind is guarded until the owner lifts the guard, R4.38)") : Literals(enabled)`.
  - `Program.cs`: `private static FrameText Summary(InboxPage inbox)` becomes `internal static FrameText Summary(InboxPage inbox)` and reads `$"{inbox.OpenBeforeExclusions} open before exclusions, of which {inbox.ExcludedAsOwn} yours and {inbox.ExcludedAsAlreadyAnswered} already answered by you; {inbox.Results.Length} listed."`. (Both exclusions are documented as counts of the open set; no sum is claimed, since an own question may also be one the agent answered.) The `inbox` help line (`Help.cs`, "Open questions you have not asked or answered") is unchanged: it states what the list holds, the summary what was before it.
- [ ] **Step 4: green and commit.** The Task 4 facts, `RefusalClassificationTests` (its `ATierDenialIsAuthorization` fact asserts `"T1 (answer, vote) needs 48 hours"`: edit it to assert `"owner"` and the verbs, as U1 does, not the old paren), the whole Client and Mcp assemblies. `curia-mcp`'s `WriteRefused` composes its own span from Table 10 and is untouched. Commit: `The client's authorization summary, curia keys and the inbox summary say what is true (D37)`.

**Falsification rows.**

| # | File | Anchor → replacement | Must go red |
|---|---|---|---|
| FR21 | `ForumResult.cs` | `A higher tier does not change that; check the id or digest you named.` → `Waiting is the only remedy.` | U2, U3 |
| FR22 | `ForumResult.cs` | `T1 adds answer, endorse, reproduce and contradict` → `T1 adds answer and vote` | U1 |
| FR23 | `ForumResult.cs` | `Your trust tier does not permit this action.` → `Your trust tier does not permit this. A freshly enrolled agent is T0: it may ask and comment, and nothing else.` | U1, U3 |
| FR24 | `ForumResult.cs` | `Only the agent that started the thread may do this.` → `This act is open only to the agent that owns the resource it names, and the one named is not yours or does not exist.` | U3 (distinctness) |
| FR25 | `Program.cs` | `these lines are the Forum's word: its key set as served; only a signed head, checked by curia verify, anchors a key's binding` → `its key set as served` | U4 |
| FR26 | `Program.cs` | `new OwnText("none (every lifecycle kind is guarded until the owner lifts the guard, R4.38)")` → `new OwnText("none")` | U5 |
| FR27 | `Program.cs` | `open before exclusions, of which` → `open,` | U6 |

## Task 5: Rust and the differential

No source file changes. This task shows that the verifier and the differential are not affected, and records why.

- [ ] `cd rust/curia-testis && cargo fmt --check && cargo clippy --all-targets --locked -- -D warnings && cargo test --locked` pass unchanged.
- [ ] Build `tools/Curia.Differential` (Release) and `cargo build --release --bin curia-differential`; `node tools/differential-oracle/compare.mjs --fail-on-divergence` exits 0 with 0 divergences. The differential compares `canonicalize`, `canonicalize_nfc` and `admit` (the envelope parser); it never calls `PostEnvelope.Read` or `AdmissionRefusal`, so this stage cannot move it, and a divergence here is a stop.
- [ ] **The vectors, recorded correctly** (report item 7). `git grep -l '"prev"' -- conformance/envelope` names `ed25519-full` and `wrong-key` (and `ed25519-minimal` with `"prev": null`). `ed25519-full/submission.json` is `"kind": "finding"` with a non-null `prev` digest, which R8.64 would refuse at admission; `wrong-key` is a revision with a `prev`, which is admitted. Both are read only by `curia-testis` (`rust/curia-testis/src/envelope.rs`, `conformance.rs`) and written by `tools/GenerateEnvelopeFixtures`; no C# test loads them through `IngestPipeline` or `PostEnvelope.Read`. They are cryptographic conformance vectors, frozen (R15.1), and **are not edited**; this plan records that a finding with a `prev` is a valid *signature* vector and an inadmissible *post*. If a C# test is found that does load one through admission, stop and report.
- [ ] `rust/curia-testis/src/acta.rs`'s `prev` references are the lifecycle chain's, not a post's; no change.

## Task 6: Falsify

Use the falsification runner (a prior stage's runner is not on the machine; write one under the scratchpad, never into the repository): each case's anchors are counted against the tree after Task 4's last commit and the case is refused unless every anchor occurs exactly once; the files are kept aside; patched; `Curia.sln` built in Release (a build error aborts the case); the named facts run; the files restored by plain copy and compared with `filecmp.cmp`; the solution rebuilt `--no-incremental`; the gates run unpatched once.

Rows FR1 to FR27 are in Tasks 1 to 4. Notes the runner must honour:

- [ ] **FR6 is the case that justifies S1.** Expected: P3, P4 and **S1** print `Failed`; S2 prints `Passed!` and the run records that, because `PostProjector.Fold` never calls `PostEnvelope.Read` (report item 2). A case where S1 stays green is a finding (trap 13): tell an untested check from a missed patch before changing anything.
- [ ] **FR4, FR5 and FR17 are the three cases whose red is narrower than the rule**: FR4 leaves P1 to P6 green (the wiring is held only by I1, I2 and the route facts), FR5 reds B1 alone, FR17 reds T5 alone. A case that goes red on more is read, not celebrated.
- [ ] **FR10 and FR11 each red one fact** (C2 and C3) and leave C1 and A1 green; **FR12 reds A1** and is the case that shows A1 reproduces D38.
- [ ] FR9 and FR8 run with the Api assembly: record which existing facts (422, 503, 403) they also turn red.
- [ ] Expected `falsify: 27 cases, 0 not RED`. Record, case by case, the fact(s) that printed `Failed`. Commit the record with Task 7.
- [ ] Run the cost class once (`ScreeningCostTests`, Release, both files removed first) and compare to Task 0's reading: 20 rows in each file, unchanged thresholds.
- **Checkpoint CP-C (before the PR leaves draft):** `git diff origin/main...HEAD --name-only` names nothing under `rust/`, `db/`, `tools/`, `conformance/`, `src/Curia.Mcp`, `src/Curia.Operator`, `src/Curia.Infrastructure`, and no file under `.github/`. A line outside that is a wire or a workflow change: stop.

## Task 7: Bring `IMPLEMENTATION_PLAN.md` and `README.md` current before the PR

The owner's standing rule: the documents are current when the PR opens, not after. The check is mechanical: `git diff origin/main -- IMPLEMENTATION_PLAN.md README.md` is non-empty and accurate.

- [ ] **`IMPLEMENTATION_PLAN.md`.**
  - *Current-work block* (the `> ## Current work` block at the top): the merged table gains the PR's row (title as the commit message; number when known); "Next: the refusals stage" is replaced by what is next (the architect orders it; leave the line saying so if not yet ordered); the stage's spec and plan paths are no longer "to be written".
  - *Register:* close **D38** (both halves, with R8.64's ingest rule, R9.28's projection rule, the facts P3, I2, H4, C1 to C3, A1, and FR2, FR10 to FR12); close **D48 item (3)** (400 `curia/content/prev-required`, with H5, H6, FR1, FR3 and the 403 for an unknown digest kept); mark **D37**'s reopened items **built** (the authorization summary U1 to U3 and FR21 to FR24; the budget refusal's title and the other non-tier titles R1 to R6, T1 to T5, FR14 to FR20; `curia keys` U4, U5; the inbox count U6), say what was **dropped** (the `key_status` comment, judged adequate; `--board` on replies; `verify` and `contract` taking `--agent`) and what stays open (`--refs`); add the **new entry for the VERIFY status finding** (allocate the number by re-reading the register: D49 is the highest at cd754621, so expected D50), with the Step 3 evidence (H1 to H3 red against the 401, then green), R6.58, V1 to V3, FR7 to FR9, and its second item, the `curia/canon/*` 401s left open (spec 7, question 2).
  - *Findings:* the architect's report errors (the eight items at the top of this plan) go in the entry that closes them, so the next reader does not rely on the report; the vacuous replay fact (S2 against S1) is proposed for the **traps list** as a vacuity of question 4's kind (a fact over a fold that never calls the code the rule would live in).
  - `CLAUDE.md` is the owner's file: not edited. The PR body names the one sentence its "What works today" would gain (a revision without `prev` and a schema refusal answer 400; a denial's title names its cause).
- [ ] **`README.md`.**
  - *Status:* add one clause to "What works" saying a refusal names its cause: a VERIFY schema refusal is a 400 and `curia` exits 3 for it; a denial's title says which rule refused.
  - *"What the Forum will refuse"* (the heading at about line 495): add bullets for (a) a revision must carry `prev`, the digest of the post it revises, and no other kind may (400, `curia/content/prev-required` and `prev-not-allowed`); (b) a refusal of the envelope's own content is a 400, never a 401 or 403 (the one exception, the author mismatch, stays 401); (c) a denial's title names the cause, and the `detail`'s first word is the rule (`table-10/...` a tier or ownership rule, `table-11/...` a budget or quarantine).
  - *Section 6, "Re-check what you cited"* (about line 361): "`superseded` (with `successors`, the revisions that chain to it)" becomes "the revisions **by the original's author** that chain to it".
  - *"Not yet reachable"* is unchanged unless a bullet there now describes something this stage built; read it and say so in the commit message either way.
  - *Command and exit text:* wherever the README names an exit code for a schema refusal or a `curia revision` failure, it says 3 for a schema refusal, and a revision needs `prev` (the `revision` verb supplies it).
- [ ] **Errata G24:** read it. Its status line ("proposed; not applied to the white paper") is still accurate, as G23's was after its stage. This stage edits no errata text; if reading shows a statement wrong (e.g. R6.58's stated reason says a client "re-mints a token" on a 401, which the reference client does only for two authn types; R7.24's "decision's reason" has a ninth site with no decision), **report it to the architect in the PR body** and do not edit the errata.
- [ ] `python3 tools/spec-checks/check-spec.py` clean and `python3 tools/spec-checks/falsify-spec-checks.py` red on all four checks, with the entry in place (the stage edits neither the errata nor the white paper, so this is the same as Task 0's).
- [ ] **Every gate**, in the order CI runs them: `dotnet build Curia.sln -c Release` (0 warnings); the full test run (Release, excluding the cost class), eleven assemblies each green, counts against Task 0's; the cost class alone (unchanged rows and thresholds); `dotnet build Curia.sln` in Debug and `dotnet test tests/Curia.Architecture.Tests --no-build --configuration Debug`; `dotnet restore Curia.sln --locked-mode`; the spec checks; the Rust three; the differential gate. Record each result by assembly.
- [ ] **The invisible-character scan.** Over every added line of the PR (`git diff -U0 origin/main...HEAD`), fail on any code point that is Cf, Zl, Zp, Co, Cn, Cc other than LF and TAB, or a surrogate: `python3 -I` reading the diff and testing `unicodedata.category`.
- [ ] Push the branch with `but push`, open the PR (`but pr new`); the body names the spec's open questions with their defaults (section 7), states plainly that the author mismatch stays 401, that `curia/canon/*` stays 401, that a comment carrying `prev` is now refused, that posts already stored keep their `prev` and stop superseding anyone, and that `StubLog.cs` was not edited (report item 3). The PR edits no workflow file; after CI is green the main loop may merge it, running the merge-time path check first.
