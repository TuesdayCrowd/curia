# A Failed Pin Carries No Verdict, and a Tool Answers Every Value Its Schema Admits: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-24-verify-pin-and-admitted-values.md`
> Written by `curia-architect` on 2026-10-24 at 034ed7e2 (`main`, #110). Filed in the scratchpad; copy it into the tree with the stage's first commit. The errata entry it builds, **G29** (R8.67, R11.42, R11.43, R14.12), is filed in `curia-whitepaper-ERRATA-AND-ADDENDUM.md` in the working tree, uncommitted, beside G28 (the next stage's): the first commit carries both. `check-spec.py` printed `spec-checks: clean` (exit 0); `falsify-spec-checks.py` went red on all four checks naming their cell (`R11.997`; `R11.16`; `R8.67` absent from the index; `R4.1` phantom), tree untouched.
> **Nothing here was run.** Findings 1 to 4 were reproduced by the controller (the exercise's reproduction, R1 to R4; the transcript is not in the tree); every code claim was read at 034ed7e2. Probes are `V1`..`V16`, rows `FV1`..`FV12` (no bare `P<n>`).

**Goal:** `curia_verify` never closes `VERIFIED.` about a document the caller did not name, and no `curia-mcp` tool answers a value its schema admits with the bare `An error occurred invoking '<tool>'.`. **This jumps ahead of "Readers say how a thread stands"**, as a small PR of its own, because R1 is the one defect on the surface that makes an agent cite a document it never pinned, it is five lines of behaviour with a loud probe, and the next stage adds a ninth tool (`curia_revise`) whose identifier and `refs` handling should be born under R11.43. It touches no frozen format, no Forum route and not `ci.yml`: agent-mergeable. Errata **G29**.

**Scope.** `src/Curia.Mcp/ForumTools.cs`, `WriteTools.cs`, `ToolText.cs` (`SearchTemplate`'s `min_verification` sentence only), a new `src/Curia.Mcp/ArgumentGuards.cs`; `src/Curia.Client/ReferenceForms.cs`; tests under `tests/Curia.Mcp.Tests/`, `tests/Curia.Client.Tests/`, `tests/Curia.Api.Tests/` (one fact in `McpToolContractEndToEndTests.cs`; the D56 PR also edits that file, in a different fact); docs.

## Disposition of the exercise's findings (decided, not asked)

| Finding | Decision | Reason |
|---|---|---|
| R1 pinned FAILED then VERIFIED, `isError=false` | **This PR**, first (R11.42) | an agent is misled by the closing line and the flag; reproduced |
| R2 `refs:[null]` uncaused error | **This PR** (R11.43) | G27 regression, reproduced; remedy is the tool's named refusal, not the schema (G29 decision 2: the checker reads the published schema and must keep doing so) |
| R3 `postId:""` uncaused error | **This PR** (R11.43, R14.12) | same; five tools carry the guard (`ForumTools.cs:118`, `:213`; `WriteTools.cs:96`, `:131`, `:158`) |
| R4a `https://a b` signed | **This PR** (R8.67) | a permanent signed post; the classifier is shared with `curia`, one edit |
| R4b a digest naming no post accepted | **Register D59**, not decided | a spec question (R8.57, R10.33): a citation of a post not yet served may be legitimate |
| L1 no line says answered/accepted | **Next stage** (G28, D36 F11) | already its item |
| L2 `min_verification` "excludes unendorsed" | **This PR**, a sentence | description truth (G27 ruling 7); no requirement |
| L3 `curia_resolve` of a non-answer, no next step | **Next stage** (G28 amendment) | it is the resolve tool's wording, beside `accepted` |
| L4 override rationale unchecked | **Rejected** | the Forum authenticates authorship, never truthfulness (principle P1); R8.20 already penalises a wrong override; a detector is a gate whose false-positive ceiling is zero |
| L5 consistency "verified" against the same head; "in the log" while inclusion unchecked | **Register D60**, unverified lead | not re-run; the wording may state more than was checked (trap 31's shape); first step is to run it |
| L6 refusal-format and wording nits | **Register D61**, one entry, items | low; several are SDK behaviour (the prefix is already recorded) |
| L7 "no read in this session" warning | **Rejected** | an artefact of one process per call in the harness; the warning is correct and is R11.29's |

## Rulings

1. **A failed pin returns the subject line, the `pinned FAILED` line and the served digest, `isError = true`, and no checks** (R11.42). `VerifyAsync` (`ForumTools.cs:210`-`:243`) decides the pin before it renders: `Pinned` returns a result type (held / failed / none) and not a string; on failed it builds the short result and returns. Held and none are today's text. The comment at `:235`-`:238` is rewritten to say which failure stays a value (an unsound post) and which does not (a document that is not the named one). The comparison stays against the digest the verifier computed (`verification.Digest`), never a served member.
2. **One guard for identifiers and one for array items** (R11.43). `ArgumentGuards.Identifier(string value, string argument)` throws `McpException($"NOT SENT: {argument} is empty or blank. Nothing was sent.")` and replaces the five `ThrowIfNullOrWhiteSpace` calls; `ReplyAsync` refuses a `null` or blank item of `refs` by index before `ReferenceForms.Parse` (`NOT SENT: refs[1] is null`), and `AskAsync`'s `tags` the same for a `null` item (it would otherwise reach `SubmissionBuilder`; the probe decides whether that throws). The message names the argument and its index and never a value. It is the `NOT SENT` voice of G27 ruling 3 so a model sees one refusal shape.
3. **`ReferenceForms.Parse(null)`** keeps its `ArgumentNullException` (a contract on a C# caller); the adapter never reaches it with a null.
4. **A URL is a URL** (R8.67). In `ReferenceForms.Parse` the URL arm requires `Uri.TryCreate(given, UriKind.Absolute, out var uri)`, `uri.Scheme` of `http` or `https`, and no character for which `char.IsWhiteSpace` or `char.IsControl` holds. The digest and package arms are unchanged (D59 holds the first's question).
5. **`SearchTemplate`'s `min_verification` sentence** (L2) says the floor applies to answers and findings only and that the result's `applies_to` and `not_applicable_to` say which kinds a floor touched. The words are held against the Forum's own `not_applicable_to` (R10.45).
6. **Oracles are not the tools.** (a) *The pin:* the test computes the document's digest with `Curia.Canon`'s `EnvelopeDigest` from the stub's canonical bytes, and uses a stub whose served `digest` member lies, so a tool that compares the served member passes the wrong value. (b) *What a value is:* the boundary table in the test with R14.12's sentence beside each row (strings: empty, blank; arrays: empty, `[null]`; integers: zero, negative), applied to every parameter of the schema `tools/list` serves, so a tenth tool is varied without anyone remembering to. (c) *Classification:* RFC 3986's absolute-URI rule through `System.Uri` in the test, a different call path from `ReferenceForms`, over a table with the published sentence (R8.67) beside each row.

## Tasks

### Task 0: Baselines and the pre-state, operated

Re-measure all eleven assemblies and `cargo test --locked` in a detached worktree under the scratchpad at 034ed7e2 (#110's figures: Mcp 296 + 1 skipped, Client 718, Api 622, Architecture 59; the rest carried from #109). Rerun the controller's four probes against a Release build in that worktree on a throwaway database (R1 to R4); keep transcripts outside the tree. Never build in the main tree's `bin/`.

### Task 1: Every probe first, each seen red

Write V1 to V16 below before any production edit; stubs only to compile (`ArgumentGuards` returning the old behaviour). Record each first red text.

### Task 2: A failed pin (R11.42)

Ruling 1. V1 to V4 green; `ToolText.VerifyTemplate` gains one sentence (a failed pin answers an error and no verdict).

### Task 3: Admitted values (R11.43, R14.12)

Rulings 2 and 3. V5 to V10 green. The boundary theory (V9) is the gate; it is written against the registered set, so it varies the five tools this PR fixes and any later one.

### Task 4: A URL is a URL (R8.67)

Ruling 4. V11 to V13 green. `ReferenceFormsTests` gains the rows.

### Task 5: The `min_verification` sentence

Ruling 5. V14 green.

### Task 6: Existing facts, then the whole suite once

`ToolDescriptionTests` (`VerifyTemplate`, `SearchTemplate` backticked identifiers stay schema properties), `ReaderFrameToolTests`' verify facts that assumed the checks follow a pin line, `McpToolContractEndToEndTests`, the fuzzer's exemplars for a reference. Record what moved.

### Task 7: Falsification FV1 to FV12

Copy-aside patch, `--no-incremental` Release build of the gate (trap 24), restore by plain `cp` and `touch` (never `git checkout --`), `shasum` equal before and after, unpatched run green last. Count anchors first (trap 28).

### Task 8: Operate it again

Task 0's four probes against the fixed build: R1 answers `isError=true` with no `VERIFIED.`; R2 and R3 answer `NOT SENT` naming the argument; R4a is refused with the three forms named. Then a pinned verify with the right digest, which still prints the checks and closes as before (the control that the fix did not remove the verdict).

### Task 9: Register and docs

Below.

## Probes and their red text (predicted, not run)

Each negative probe has a control that must pass.

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| V1 | `tests/Curia.Mcp.Tests/ReaderFrameToolTests.cs` (verify facts) `R11_42_AFailedPinAnswersAnErrorAndNoVerdict` | stub serving a post; `expectedDigest` = 64 zeros: `IsError`, text names both digests (display literals) and says no verdict was reached; control: the right digest answers `isError=false` | predicted, not run: `a failed pin answered isError=false` |
| V2 | same, `R11_42_NothingAfterAFailedPinIsAboutTheOtherDocument` | the text holds none of `VERIFIED`, `signature   `, `inclusion   `, `consistency `, `key         `, `lifecycle   ` (the checks' labels, taken from `PostVerification.Render`'s output for the control) | predicted, not run: `the result after a failed pin still contains 'VERIFIED.'` (seen: R1) |
| V3 | same, `R11_42_ThePinIsComparedToTheDigestComputedAndNotTheOneServed` | stub whose served `digest` equals the pin while the canonical form's digest (computed in the test with `Curia.Canon`) differs: the pin fails | predicted, not run: `a pin equal to the served digest member held, though the canonical form digests to <x>` |
| V4 | same, `R11_42_ANoPinAndAHeldPinAreUnchanged` | no pin and a held pin keep today's text byte for byte (golden taken from the unmodified build in Task 0) | green first; held by FV3 |
| V5 | `tests/Curia.Mcp.Tests/ToolProtocolTests.cs` `R11_43_ANullItemOfRefsIsRefusedByIndexAndNothingIsSent` (`curia_comment`, `curia_answer`) | `refs:[null]` and `refs:["https://a.example/x", null]`: `NOT SENT`, names `refs[0]`/`refs[1]`, empty request log | predicted, not run: `An error occurred invoking 'curia_comment'.` (seen: R2) |
| V6 | same, `R11_43_ANullItemOfTagsIsRefusedByIndex` (`curia_ask`) | as V5 for `tags` | predicted, not run: as V5, or green if `tags` already tolerates null; if green it is held by FV9 |
| V7 | same, `R11_43_AnEmptyIdentifierIsRefusedByNameAndNothingIsSent` (theory: `postId` of `curia_read`, `curia_verify`, `curia_flag`; `parentPostId` of `curia_answer`, `curia_comment`; `answerPostId` of `curia_resolve`; and `""` and `"   "`) | `NOT SENT`, names the argument, empty request log | predicted, not run: `An error occurred invoking 'curia_verify'.` (seen: R3) |
| V8 | same, `R11_43_TheMessageNamesTheArgumentAndEchoesNoValue` | for a blank of spaces and tabs the text contains the argument's name and none of the blank characters' escapes | predicted, not run: as V7 |
| V9 | same, `R14_12_NoToolAnswersABoundaryValueItsSchemaAdmitsWithTheUncausedError` (theory generated from every registered tool's schema: empty/blank strings, empty/`[null]` arrays, 0/-1 integers, per parameter, others valid) | the answer is never the SDK's bare `An error occurred invoking '<tool>'.`; it is a result or a refusal naming the argument; non-empty set asserted first | predicted, not run: the bare text, for `curia_verify` `postId=""` and `curia_comment` `refs=[null]` and the three other identifier tools |
| V10 | same, `R14_12_TheBoundaryTableIsHeldToRequirementText` | the table's rows equal the values R14.12's sentence names, parsed from the errata, so a row cannot be dropped quietly | green first; held by FV10 |
| V11 | `tests/Curia.Client.Tests/ReferenceFormsTests.cs` `R8_67_AUrlIsAnAbsoluteHttpUriWithoutWhitespaceOrControls` (theory) | `https://a b`, `http://x\ty`, `https://x\u0007`, `http://`, `https:///`, `http:relative` refused; `https://a.example/x?y=1#z` accepted; compared with `Uri.TryCreate` in the test | predicted, not run: `'https://a b' was classified as a url` (seen: R4) |
| V12 | `tests/Curia.Mcp.Tests/WriteToolTests.cs` `R8_67_AMcpReplyWithAnUnparseableUrlIsNotSent` | `curia_comment` `refs:["https://a b"]`: `NOT SENT`, the three forms named, empty request log | predicted, not run: `POSTED` |
| V13 | `tests/Curia.Api.Tests/CliRefsEndToEndTests.cs` `R8_67_TheCliRefusesTheSameUrlBeforeSending` | `curia comment --refs 'https://a b'` exits 1 with the forms named; no request | predicted, not run: exit 0 |
| V14 | `tests/Curia.Mcp.Tests/ToolDescriptionTests.cs` `R10_45_TheFloorSentenceNamesTheKindsItAppliesTo` | `SearchTemplate`'s floor sentence names answers and findings and does not say "unendorsed results"; the kinds named equal the Forum's gradable kinds read from `VerificationLevels`/the policy table, not typed in the test | predicted, not run: `the description says the floor excludes unendorsed results, which it applies to answers and findings only` |
| V15 | `tests/Curia.Api.Tests/McpToolContractEndToEndTests.cs` `R11_42_ARealForumsPinnedVerifyFailsWithoutAVerdict` | real Forum: post, `curia_verify` with the digest of a different real post: error, both digests, no check lines | predicted, not run: as V1 |
| V16 | same, `R11_43_ACommentWithANullRefsItemPostsNothing` | the Forum's own thread route still holds exactly the posts before the call | predicted, not run: bare error, and the thread unchanged (the control passes today; the red is the message) |

**Order of red.** V4, V10 are green first and held by FV3, FV10; V6 may be (its row says so); everything else is red on the unmodified tree.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FV1 | `VerifyAsync` renders the checks after a failed pin | V2, V15 |
| FV2 | `isError` false after a failed pin | V1, V15 |
| FV3 | the held-pin text changes (the pinned line dropped) | V4 |
| FV4 | pin compared to the served `Digest` member | V3 |
| FV5 | `ThrowIfNullOrWhiteSpace` restored in `VerifyAsync` | V7 (`curia_verify`), V9 |
| FV6 | restored in `ReadAsync` | V7, V9 |
| FV7 | restored in `ReplyAsync` | V7, V9 |
| FV8 | restored in `ResolveAsync` and `FlagAsync` (two sub-rows) | V7, V9 |
| FV9 | the null-item refusal removed | V5, V6, V9, V16 |
| FV10 | `ArgumentGuards` refuses a blank only, not an empty string | V7 (empty row), V9 |
| FV11 | `ReferenceForms` back to the prefix test | V11, V12, V13 |
| FV12 | the description sentence restored | V14 |

No row may stay green; the probe is written, not the row dropped.

## Gates

As #110: every .NET assembly in Release (`ScreeningCostTests` excluded), Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence`, `check-spec.py` and `falsify-spec-checks.py`, invisible-code-point scan of added lines, `git status --porcelain conformance/` empty, no `.github/workflows/` in the diff. Task 8's probes.

## Docs owed before the PR

`IMPLEMENTATION_PLAN.md`: correct the Merged table's "this PR" row (`:49`) to `#110` if the stage before did not; add this PR's row; update "Current work" and "Next stage". Open and close **D58** (the third exercise's reproduced R1 to R4 and the L2 sentence, findings in a paragraph each, V1 to V16 by name, FV1 to FV12 as run). Open **D59** (a reference's digest naming no post is accepted; first step: decide whether a citation may name a post not yet served), **D60** (lead L5: `curia_verify`'s consistency and "the post is in the log" wording; first step is to run it against a post past the head and a repeated head, and read what the line claims), **D61** (L6's low items as a list: refusal-shape inconsistency, nonexistent vs malformed id, `kinds` refusal not listing valid kinds, silently empty results for an unknown board, unexplained `why_ranked` terms, the empty-rationale refusal not naming the field, the budget refusal without a reset time). Numbers are computed against the register at 034ed7e2 (highest D57) and allocated when written; the thread-standing stage then takes the next. Add trap **34**: *a gate that varies only ordinary values has not varied the schema*: G27's gate sent omitted, added and mistyped arguments, and the edge of every admitted type was never sent; derive the boundary values from the schema and from the requirement's sentence. `README.md` MCP section (`:605`-`:650`): the failed-pin behaviour and the named refusals. `CLAUDE.md`: one sentence. Class summaries in `ForumTools.cs` and `ToolCatalogue.cs`. Errata G29's Status line gains "Built <date>". The external `curia` skill is the owner's: unchanged here (the CLI gains only R8.67's refusal).

## Next stage after this one

`docs/superpowers/plans/2026-10-24-readers-say-how-a-thread-stands.md` (G28), unchanged in order; that plan is not in the tree yet and is committed with that stage, written to build on this PR: its `curia_revise` uses `ArgumentGuards` for `postId` and `refs` (probe Q27 there), and its D numbering follows this PR's D58 to D61.

## As run

Run 2026-10-24 from 034ed7e2 (main, #110) to 5877ab42, plus this record. Release, Postgres local. The sections follow #110's plan: baselines, the probes red first, the commits, the refusal-shape change, the falsification, the gates, the live transcripts, and the divergences. Every figure below is quoted from the stage's reports (`.superpowers/sdd/2026-10-24-verify-pin-and-admitted-values/`); none is the plan's prediction.

### Task 0, as run (2026-10-24, at 034ed7e2)

**Baselines, re-measured** (Release, `ScreeningCostTests` excluded, Postgres local, in a detached worktree at 034ed7e2 so no build of this branch touched them): Domain.Primitives 64, AuthN 140, Canon.Sodium 32, Architecture 59, Application 443, Mcp 296 (+1 skipped), Domain 677, Client 718, Infrastructure 150, Canon 329, Api 622; Rust `cargo test --locked` 268 passed, 2 ignored. All equal #110's figures. The pre-state was not re-operated by this stage's tasks: the controller's reproduction of R1 to R4 on 034ed7e2's build (the exercise's reproduction, whose transcript is not in the tree, run against `curia-mcp` 0.5.0 on a local Forum and a throwaway database) is the "before" of the live probes below.

### Probes, red first (Task 1; commits 10304b65 and its fix round 405688d1; Release)

Only a compile-only stub of `ArgumentGuards` (`Identifier` = `ArgumentException.ThrowIfNullOrWhiteSpace`, nothing calling it) was in `src/`; `StubLog` gained `ServedDigest` (a served `digest` member that lies, for V3). Red on the unmodified tree: Mcp `Failed!  - Failed:    29, Passed:   298, Skipped:     1, Total:   328`, Client `Failed!  - Failed:     4, Passed:   721, Skipped:     0, Total:   725`, Api (the two classes it touched) `Failed!  - Failed:     3, Passed:    11, Skipped:     0, Total:    14`: 36 expected reds, no other fact moved.

| Probe | Test | Seen (first red) | Differs from the plan |
|---|---|---|---|
| V1 | `R11_42_AFailedPinAnswersAnErrorAndNoVerdict` | `a failed pin answered isError=False:` and a text opening `pinned      FAILED. You asked about "sha256:000..."` and going on to every check | none |
| V2 | `R11_42_NothingAfterAFailedPinIsAboutTheOtherDocument` | `the result after a failed pin still holds check content of the other document (verified: recanonicalized bytes are byte \| ... \| VERIFIED. This client checked the signat)` | the labels and the closing sentence are taken from the control's own text (fix round 1) |
| V3 | `R11_42_ThePinIsComparedToTheDigestComputedAndNotTheOneServed` | `a pin equal to the served digest member held, though the canonical form digests to sha256:a7e7...` | red for the `isError` half: the comparison was already against the computed digest, so the pin failed as it should |
| V4 | `R11_42_ANoPinAndAHeldPinAreUnchanged` | green first | as planned; held by FV3. The golden masks the two digests (the agent key is random per run) after asserting they carry the supplied pin |
| V5 | `R11_43_ANullItemOfRefsIsRefusedByIndexAndNothingIsSent` (four rows) | `curia_answer with a null at refs[1] did not answer a NOT SENT naming it:` `An error occurred invoking 'curia_answer'.` | none |
| V6 | `R11_43_ANullItemOfTagsIsRefusedByIndex` | `curia_ask with a null at tags[1] did not answer a NOT SENT naming it:` `An error occurred invoking 'curia_ask'.` | red, not green (the plan allowed either); a control with valid tags precedes it (fix round 1) |
| V7 | `R11_43_AnEmptyIdentifierIsRefusedByNameAndNothingIsSent` (12 rows) | `curia_verify with postId="   " did not answer a NOT SENT naming the argument:` `An error occurred invoking 'curia_verify'.` | none |
| V8 | `R11_43_TheMessageNamesTheArgumentAndEchoesNoValue` (6 rows) | `curia_read's answer does not name postId:` the bare error | none |
| V9 | `R14_12_NoToolAnswersABoundaryValueItsSchemaAdmitsWithTheUncausedError` | `16 boundary value(s) the schema admits were answered with the bare invocation error:` the five identifier sites, `curia_ask tags=[null]`, `curia_comment` and `curia_answer` `refs=[null]`, and `curia_search criteria.tags=[null]` | two sites beyond the plan's list (below); no bare error for 0, -1 or an empty array |
| V10 | `R14_12_TheBoundaryTableIsHeldToRequirementText` | green first | as planned; held by FV10. Each row's kind and value are bound to the phrase R14.12 names, parsed from the errata (fix round 1) |
| V11 | `ReferenceFormsTests.R8_67_AUrlIsAnAbsoluteHttpUriWithoutWhitespaceOrControls` (7 rows) | four red: `'https://a b' was classified as a url`, likewise the tab row, the BEL row and `https:///` | `http://`, `http:relative` and the accepted row were green; `System.Uri` agrees with every row |
| V12 | `R8_67_AMcpReplyWithAnUnparseableUrlIsNotSent` | `Assert.Throws() Failure: No exception was thrown` (the comment was posted) | none |
| V13 | `R8_67_TheCliRefusesTheSameUrlBeforeSending` | `exit 0, not 1:` then `posted "01M5P7KMHHEZ0JFZSP537CFCXQ" kind "comment"` | none |
| V14 | `R10_45_TheFloorSentenceNamesTheKindsItAppliesTo` | `Assert.DoesNotContain() Failure: Sub-string found ... "unendorsed results"`; the kinds are read from `RetrievalFloorPolicy.GradableKinds` and `PostKinds.Wire` | none |
| V15 | `R11_42_ARealForumsPinnedVerifyFailsWithoutAVerdict` | `a failed pin answered isError=false:` and `pinned FAILED` followed by all five checks | none |
| V16 | `R11_43_ACommentWithANullRefsItemPostsNothing` | `a null refs item did not answer a NOT SENT naming refs[0]:` the bare error; the thread unchanged, as the plan says | none |

**Beyond the plan, found by V9.** `curia_search` with `criteria.tags=[null]` answered the bare error (the schema admits null items of `tags`); `criteria.kinds` was varied by the theory and did not fail. After Task 6 made a build error a result, V9 found a second: `curia_ask` with `board=""` answered a `NOT SENT` that did not name `board` (the envelope schema's message). Both are guarded, and both are recorded under D58.

**Oracles.** V1 to V3 and V15 compute the digest with the BCL (`SHA256` over the UTF-8 canonical form, lower hex), not with `Curia.Canon` and not from a served member; V3 also asserts it equals the stub's `Submission.PrefixedDigest`. V10 parses R14.12's sentence from the errata. V11 asks `System.Uri`. V14 reads the Forum's own gradable kinds.

### The fixes (Tasks 2 to 5; commits 1d8d7253, cb13e590, 1803695d, 7e8332f7)

- **Task 2 (R11.42, 1d8d7253).** `Pinned` returns a held/failed flag with its text; on a failure `VerifyAsync` returns the subject line, the `pinned FAILED` line (both digests as display literals, "No verdict was reached and no check is reported: every one would be about the other document.") with `IsError = true`, and no check. The comparison stays against `verification.Digest`, the digest computed. `ToolText.VerifyTemplate` says a mismatch answers an error and no verdict.
- **Task 3 (R11.43, cb13e590).** `ArgumentGuards.Identifier` and `Items` return the refusal as a result. The five `ThrowIfNullOrWhiteSpace` calls are replaced; `refs` (null or blank items) in `ReplyAsync`, `tags` in `AskAsync`, and `criteria.kinds` and `criteria.tags` in `SearchAsync` are guarded.
- **Task 4 (R8.67, 1803695d).** `ReferenceForms.IsUrl`: an absolute `http`/`https` URI, the `scheme://` prefix kept, no whitespace or control character.
- **Task 5 (7e8332f7).** `SearchTemplate`'s `min_verification` sentence says the floor applies to `answer` and `finding` posts only and that `applies_to` and `not_applicable_to` say which kinds a floor touched (without backticks: `R11_26` requires a backticked name to be a schema property).

Gate after Tasks 2 to 5: Mcp `Passed!  - Failed:     0, Passed:   327, Skipped:     1, Total:   328`, Client `Passed!  - Failed:     0, Passed:   725, Skipped:     0, Total:   725`, Api (McpToolContractEndToEndTests, CliRefsEndToEndTests, McpWriteEndToEndTests, ReaderFrameTests) `Passed!  - Failed:     0, Passed:    27`, Architecture Release and Debug `Passed!  - Failed:     0, Passed:    59`. Intermediate reds seen and fixed: a guard that threw `McpException` failed 24 facts (the SDK prefixes an exception's message), and a backticked `applies_to` failed `R11_26`.

### Task 6 (5877ab42): one refusal shape, and the facts it changed

The reference-form refusal in `ReplyAsync` and `SubmitAsync`'s build error were the two local `NOT SENT` refusals still thrown, so a model saw them behind `An error occurred invoking '<tool>': `. Both now return the result the guards return (`NotSentResult`: `IsError`, text beginning `NOT SENT`), so a model meets one local-refusal shape and the next tool (`curia_revise`) is born under it. `curia_ask` with `board=""` (V9's second extra) is guarded by name.

Facts that pinned the thrown shape, changed with intent unchanged: `WriteToolTests` `R11_35_AValueThatIsNoneOfTheThreeFormsIsRefusedBeforeAnythingIsSent` (answer and comment), `R8_67_AMcpReplyWithAnUnparseableUrlIsNotSent` (V12), `R10_26_ACredentialIsStoppedBeforeAnythingLeavesThisHost` and `R11_20_ADecliningSignerSendsNothing` (each from `ThrowsAsync` to an `IsError` result beginning `NOT SENT`, with the other assertions kept). Not changed: the facts that go through `WriteRefused` or the Forum's refusals (those still throw and still begin `REFUSED`). Added: `ToolProtocolTests.R11_43_ALocalRefusalOfAWriteBeginsNotSentWithNoSdkPrefix` (through the protocol: `isError`, the text begins exactly `NOT SENT`, an empty request log).

### The whole suite (Task 6, Release, `ScreeningCostTests` excluded, Postgres local)

`Passed!` for Domain.Primitives 64, Canon.Sodium 32, AuthN 140, Application 443, Domain 677, Architecture 59, Mcp 329 (+1 skipped), Canon 329, Infrastructure 150 and Client 725; Api `Failed!  - Failed:     1, Passed:   624, Skipped:     0, Total:   625, Duration: 28 m 6 s`. **The one failure is `BrowseEndToEndTests.R10_73_SighupAfterTheMasterClosesExitsWithinTwoSeconds` (expected exit 129, got 2).** It passed alone twice (`Passed!  - Failed:     0, Passed:     1`). It is a PTY and signal timing fact in `Curia.Client.Cli` browse, code this stage did not touch, and the same area has flaked under load before (TUI PR B); **the cause is unexplained, not resolved**, and CI is the next independent run. The Debug build is 0 warnings, 0 errors, `Curia.Architecture.Tests` in Debug `Passed!  - Failed:     0, Passed:    59`, and `dotnet restore Curia.sln --locked-mode` succeeded. The controller re-ran the remaining gates at 5877ab42: `dotnet restore Curia.sln --locked-mode` exit 0; `cargo fmt --check` and clippy clean; `cargo test --locked` 268 passed, 2 ignored; the differential comparison 0 divergence classes; Architecture in Debug 59; `conformance/` untouched (the diff touches no Rust, no `conformance/` file and no `.github/workflows/` file).

### Falsification FV1 to FV12 (Task 7)

Each patch was applied to a copy-aside file, the gate's assemblies rebuilt `--no-incremental` in Release, the facts run, and the file restored by plain `cp` and `touch` with `git diff --quiet` asserted. Every anchor was counted before the first row. **12 of 12 rows red (14 patches with the FV7, FV8 sub-rows and FV7b), none stayed green.** `shasum` of the 17 files under `src/Curia.Mcp/`, `src/Curia.Client/ReferenceForms.cs` and `tests/Shared/StubLog.cs` was equal before and after; the residue grep over `src/` found no patch text.

| Row | Patch as applied | Red |
|---|---|---|
| FV1 | `VerifyAsync`: `if (pinFailed)` became `if (pinFailed && pinned.Length < 0)` (the checks render after a failed pin) | V2 (naming the other document's check content), V1, V3 (collateral), V15 |
| FV2 | `IsError = true` became `false` after a failed pin | V1, V3, V15 |
| FV3 | the held-pin line `pinned      to the digest you supplied, ` became `pin held, ` | V4 |
| FV4 | `PostVerifier.cs:221` (Client, not Mcp): `digest = post.Digest;` after the deconstruction, so the pin is compared to the served member | V3 |
| FV5 | `ThrowIfNullOrWhiteSpace` restored in `VerifyAsync` | V7 (2), V8, V9 (`curia_verify`) |
| FV6 | restored in `ReadAsync` | V7 (2), V8, V9 (`curia_read`) |
| FV7a | restored in `ReplyAsync` (`parentPostId`) | V7 (4), V8 (2), V9 (4 values) |
| FV7b | `WriteTools.cs:107`, the reference-form `return NotSentResult(...)` became `throw new ArgumentException("ref form")` | V12, `R11_35` (6 rows), the protocol fact for a local refusal |
| FV8a | restored in `ResolveAsync` | V7 (2), V8, V9 |
| FV8b | restored in `FlagAsync` | V7 (2), V8, V9 |
| FV9 | the four `ArgumentGuards.Items` calls (`refs`, `tags`, `criteria.kinds`, `criteria.tags`) commented out | V5 (4), V6, V9, V16 |
| FV10 | `string.IsNullOrWhiteSpace(value)` became `value is null` | V7 (12: the empty and the blank rows), V8 (6), V9 |
| FV11 | `ReferenceForms`: `IsUrl(given)` became the prefix test | V11 (4), V12, V13 |
| FV12 | `SearchTemplate`'s floor sentence restored to "excludes unendorsed results" | V14 |

Patches that differ from the plan's wording: FV4 targets `PostVerifier.cs` (the served `Digest` member is not visible to `Pinned`); FV10 uses `value is null`, which lets both an empty and a blank string through; FV7b is added for Task 6's change. Final unpatched run (Release `--no-incremental` build, 0 warnings, 0 errors): Mcp `Passed!  - Failed:     0, Passed:   329, Skipped:     1, Total:   330`, Client `Passed!  - Failed:     0, Passed:   725`, Architecture `Passed!  - Failed:     0, Passed:    59`, Api (the three classes) `Passed!  - Failed:     0, Passed:    24`.

### The four probes, live, after (Task 8, 2026-10-24, at 5877ab42, Release, a fresh throwaway Forum)

The same four probes the exercise reproduced on 034ed7e2, against the fixed build: **R1** a wrong pin answers `isError=True` and only the subject line and `pinned      FAILED. You asked about "sha256:..." and the Forum served "sha256:b80f..." under this id. These are different documents. "No verdict was reached and no check is reported: every one would be about the other document."` (no `VERIFIED.`, no check line); **R2** `refs: [null]` answers `isError=True`, `NOT SENT: refs[0] is null. Nothing was sent.`; **R3** `postId: ""` answers `isError=True`, `NOT SENT: postId is empty or blank. Nothing was sent.`; **R4a** `https://a b` is refused, `NOT SENT:` naming the three forms (a post digest, a package coordinate, or `http://` or `https://` followed by an address). **The control:** the right pin answers `isError=False`, prints `pinned      to the digest you supplied, "sha256:b80f..."` and closes with a verdict line, `NOT ESTABLISHED.`, here only because no head had been signed on the fresh Forum (the transcript keeps the pinned line and the closing line; the checks between them are not in it). The fix removed the verdict only for the wrong document.

### Divergences from the plan

1. **The guards return a result; they do not throw.** Ruling 2 and G29 decision 2 say `McpException`; the SDK puts `An error occurred invoking '<tool>': ` before an exception's message, which V5, V7 and V8 (`StartsWith` `NOT SENT`) reject, and G27's own contract refusal is returned for the same reason. R11.43 holds as worded (the refusal names the argument and says nothing was sent). The errata's Status line says so.
2. **Both thrown `NOT SENT` sites became results** (the reference-form refusal in `ReplyAsync`, `SubmitAsync`'s build error), which the plan did not list; Task 6's facts moved with them (above).
3. **V9 found two sites the plan did not list**: `curia_search` `criteria.tags=[null]` (and `criteria.kinds` is guarded with it), and `curia_ask` `board=""`. D58 records them.
4. **The digest oracle is the BCL's SHA-256**, not `Curia.Canon`'s `EnvelopeDigest` (ruling 6a): the digest is by definition SHA-256 over the canonical bytes, and `EnvelopeDigest`'s input type would have to be borrowed from the code under test.
5. **`StubLog.ServedDigest`** was added for V3 (a served `digest` member that disagrees with the canonical form).
6. **V6 was red** (the plan allowed green) and **V3 is red for its `isError` half only** (the comparison was already against the computed digest); both are held by FV9 and FV4/FV2.
7. **FV4's patch** is in `PostVerifier.cs`, **FV7b** was added, and FV10's patch is `value is null`.
8. **The `min_verification` sentence names `applies_to` and `not_applicable_to` without backticks**, because `R11_26` requires a backticked identifier in a description to be a schema property.
9. **`ToolCatalogue.Version` is 0.5.1** (bumped in the fix wave below, from 0.5.0): no tool, parameter or schema changed, but tool results and refusal shapes did (a failed pin, a local refusal as a result) and two descriptions (`curia_verify`'s and `curia_search`'s).
10. **Deferred review minors**: V9 does not assert that a legal boundary value is accepted (R14.12's "nor a success where the value is meaningless"); V14 is brittle to a restructure of the paragraph; V11 has seven rows; `ReferenceForms.IsUrl` keeps a redundant `StartsWith(uri.Scheme + "://")`; the protocol fact for a local refusal covers a credential build error and not a schema build error (the boundary theory covers others).
11. **Dispatch**: Task 1; Tasks 2 to 5 (one dispatch); Task 6; Task 7; this record. Tasks 0 and 8 were the controller's.

### Fix wave (after review of Task 9)

Docs: the blank-item claim narrowed (only `refs` refuses a blank item; `tags`, `criteria.kinds` and `criteria.tags` refuse a null one), the local-refusal claim narrowed to the guards' and the build and reference-form refusals, `criteria.kinds` no longer said to have answered the bare error, errata G29's decision 2 corrected to say the guards return a result and why, the reproduction transcript marked as not in the tree, D61 gains items 8 and 9. Code: the failed-pin result gains a next-step sentence after "No verdict was reached ..." (`Read the post again to get the digest it now has, or call without expectedDigest to verify what the Forum serves now.`; no test pinned the old sentence exactly, and V1 to V4 and V15 stay green), `ReferenceForms`' class summary states R8.67's rule, and `ToolCatalogue.Version` is 0.5.1. The live transcript above predates the next-step sentence.
