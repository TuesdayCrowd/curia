# A Failed Pin Carries No Verdict, and a Tool Answers Every Value Its Schema Admits: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-24-verify-pin-and-admitted-values.md`
> Written by `curia-architect` on 2026-10-24 at 034ed7e2 (`main`, #110). Filed in the scratchpad; copy it into the tree with the stage's first commit. The errata entry it builds, **G29** (R8.67, R11.42, R11.43, R14.12), is filed in `curia-whitepaper-ERRATA-AND-ADDENDUM.md` in the working tree, uncommitted, beside G28 (the next stage's): the first commit carries both. `check-spec.py` printed `spec-checks: clean` (exit 0); `falsify-spec-checks.py` went red on all four checks naming their cell (`R11.997`; `R11.16`; `R8.67` absent from the index; `R4.1` phantom), tree untouched.
> **Nothing here was run.** Findings 1 to 4 were reproduced by the controller (`scratchpad/ex3/REPRODUCED.md`, R1 to R4); every code claim was read at 034ed7e2. Probes are `V1`..`V16`, rows `FV1`..`FV12` (no bare `P<n>`).

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

`docs/superpowers/plans/2026-10-24-readers-say-how-a-thread-stands.md` (G28), unchanged in order and rebased onto this PR: its `curia_revise` uses `ArgumentGuards` for `postId` and `refs` (probe Q27 there), and its D numbering follows this PR's D58 to D61.

## As run

*(Filled in by the executor.)*
