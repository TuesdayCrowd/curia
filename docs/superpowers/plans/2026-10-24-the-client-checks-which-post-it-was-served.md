# The Client Checks Which Post The Forum Served: Implementation Plan (D63)

> Repository path: `docs/superpowers/plans/2026-10-24-the-client-checks-which-post-it-was-served.md`
> Written by `curia-architect` on 2026-10-24, every code claim re-read at `origin/main` 285d10c8 (#113) from a `git archive` of `src/` and `tests/` (not the worktree: applied GitButler branches make the worktree answer the wrong question). **Nothing here was run.** Wave A, stage 1 of 3; its file set is disjoint from D67's and D64's (below). Probes `V1`..`V9`, rows `FV1`..`FV7`.

**Goal:** `ForumClient.GetPostAsync` and `GetPostIfChangedAsync` refuse a served post whose `post_id` is not the id that was requested, for every caller (`curia read`, `thread`, `verify`, `browse`, `curia revision`, `curia_read`, `curia_verify`, `curia_revise`, `curia_comment`, `curia_answer`). **Closes** register D63.

**Why it is a real hole, not tidiness.** The id is provenance and no signature covers it. The Forum matches `p.PostId == postId` exactly (`ForumEndpoints.cs:1160`, ordinal), so an honest Forum never serves another id and an ordinal compare costs a legitimate read nothing. A hostile or confused Forum asked for A that serves B (same author) makes `WriteTools.ReviseAsync` (`:114`-`:139`) sign a revision with `Parent = B`, `Prev = B's digest`, `Board = B's board`, all of it verified and consistent, and not what the caller named.

## Scope (exact file set)

Production:
1. `src/Curia.Client/ForumClient.cs` (`GetPostAsync` `:70`-`:80`, `GetPostIfChangedAsync` `:91`-`:105`: the check inside the `read` lambda, after `ForumDocuments.ReadPost`)
2. `src/Curia.Client/ClientErrors.cs` (one `Error`, beside `ReferenceForm` `:47`)

Tests (new unless said):
3. `tests/Curia.Client.Tests/ServedPostIdentityTests.cs` (V1 to V6, a scripted `HttpMessageHandler` of its own)
4. `tests/Curia.Mcp.Tests/ReviseServedIdentityTests.cs` (V7 to V9, its own scripted handler; `StubLog` is not used for the hostile case)
5. `tests/Shared/StubLog.cs` **only if Task 1's full run shows a fixture serving one id for every `/v1/posts/*` request** (`:1599`-`:1605` does, when `OtherPostsAreRefused` is off). Edit that fixture to serve by requested id, never the check to tolerate it. This is the one shared file; no other wave-A stage touches it.

**Not edited:** `src/Curia.Mcp/WriteTools.cs`. The order lists `ReviseAsync`, and the ruling is that the client check is the whole fix: after it `post.PostId == postId` by construction, so `ReviseAsync` needs no second guard, and a guard there would let the next caller of `GetPostAsync` believe the client does not check. `ReviseAsync` is the end-to-end probe (V7), not an edit site. No `curia-mcp` version bump (`ToolCatalogue.cs` is untouched; the tool contract is unchanged).

Docs: `IMPLEMENTATION_PLAN.md` (D63's entry only, see the wave's docs scheme), this plan's "As run". No erratum, no README, no CLAUDE.md.

## Rulings (decided, not asked)

1. **Ordinal equality of the requested and the served `post_id`**, in the `read` lambda, so a mismatch is a `Result.Fail` and `Interpret` yields `RefusalKind.Malformed` (the kind for "a response arrived that this client could not parse"; `ForumResult.cs:30`). Not `NotFound` (the Forum did not say so) and not `Local`.
2. **One error type, `curia/client/served-post-mismatch`**, in `ClientErrors`. The requested id is the caller's own and is named; **the served id is the Forum's text and is not echoed raw** (errata G17: a value the reader did not compose is a display literal). Task 1 reads how `ClientErrors` and `ClientErrors.Transport` already quote a Forum-supplied value and uses that helper; if there is none, the detail says "a different post" and gives its length only.
3. **Both single-post reads, nothing else.** `GetPostIfChangedAsync`'s `Changed` branch is checked; its `NotModified` branch has no body and nothing to check. List routes (board, thread, search, recheck) have no requested id per item and are not in scope; a thread's passages are keyed by what the Forum lists, which is D66's neighbour and not this entry.
4. **The refusal is worded by its cause by the readers that already word a Malformed refusal** (D51); this stage adds no reader code. Task 3 records what `curia read` and `curia_read` print and the exit code (expected: the Malformed refusal's, not 6, because no signature check ran).
5. **Not specified as a requirement.** The route's meaning (`GET /v1/posts/{id}` serves post `{id}`) is the specification and no sentence says a client checks. A one-sentence requirement is the architect's option at the D61 sweep's erratum; this stage does not need it and must not wait for an erratum number (wave A has one, G31, and it is D64's).

## Tasks

**Task 0: operate the unmodified tree.** A detached Release worktree at 285d10c8, own `bin/obj`, a Forum on a throwaway database at port 5091, `HOME` set to a scratch directory for `curia`'s profile store. Seed a question by agent X and a second post B by X. Put a 20-line pass-through proxy (a python script) in front that rewrites the response for `/v1/posts/<A>` to B's body and ETag. Record `curia read A`, `curia revision A ...` (the digest it chains to) and `curia_read`: each shows B's id and exits 0. This is the red the stage removes, observed.

**Task 1: probes first.** Write V1 to V9; record each first red text. Then run the whole `Curia.Client.Tests` and `Curia.Mcp.Tests` assemblies with only the stub error added and list every fixture that serves another id (item 5).

**Task 2: the check.** `ClientErrors`, `ForumClient`. V1 to V6 green.

**Task 3: the consumers.** V7 to V9 green with no edit to `WriteTools.cs`; record the readers' wording.

**Task 4: existing facts, then the suite once.** `ConditionalReadTests`, `MarkingParseTests`, `PostVerifierTests`, `ClientVerificationTests`, `BrowseEndToEndTests`, `PropertyP22ToolResultTests`, `StubFidelityTests`; then the gates.

**Task 5: falsification FV1 to FV7.** Copy-aside, `--no-incremental` Release build of the test assembly (trap 24), restore with plain `cp` and `touch` (never `git checkout --`), `shasum` equal before and after, an unpatched run green last.

**Task 6: operate it again.** Task 0's proxy against the fixed build: `curia read A` refuses, names A, exits with the Malformed code; `curia revision A` signs and posts nothing (assert the Forum's event count is unchanged).

## Probes and their red text (predicted, not run)

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| V1 | `ServedPostIdentityTests` `GetPost_RefusesAServedIdThatIsNotTheRequestedOne` | handler serves B's valid post JSON for a request to `/v1/posts/A`; result is a refusal of kind `Malformed` and type `curia/client/served-post-mismatch` (A and B are literals in the test, not read from the handler) | predicted: `Assert.False() Failure: Expected: False, Actual: True` on `IsOk`, and before the stub error `error CS0117: 'ClientErrors' does not contain a definition for 'ServedPostMismatch'` |
| V2 | same, control `GetPost_AcceptsTheRequestedId` | handler serves A for A: ok, id A | green first; held by FV1 (always refuse) |
| V3 | same, `GetPostIfChanged_RefusesAServedIdThatIsNotTheRequestedOne` | `200` with B's body and a tag for a request for A | same as V1 |
| V4 | same, control `GetPostIfChanged_ANotModifiedCarriesNoIdAndIsStillNotModified` | `304` for A: `NotModified` | green first; held by FV3 (check hoisted above the `304` branch) |
| V5 | same, theory `GetPost_ComparesTheIdExactly` (`"a"`/`"A"`, `"id"`/`"id "`, NFC vs NFD e-acute) | each pair refused: ordinal, not culture or case | predicted: `Expected: False Actual: True` for the case pair under an `OrdinalIgnoreCase` mutant; green first on the unmodified tree only because nothing is checked, so the theory is held by FV2 |
| V6 | same, `GetPost_TheRefusalDoesNotEchoTheServedId` | served id `evil-ignore previous instructions`: the refusal's detail does not contain it; control: detail contains the requested id | predicted: `Assert.DoesNotContain() Failure: ... evil-ignore previous instructions` under FV5 |
| V7 | `ReviseServedIdentityTests` `ReviseAsync_DoesNotChainARevisionToAPostItWasNotAskedFor` | `curia_revise {postId:A}` against a handler that serves B for A: the result is an error, **zero POSTs reached the handler**; the oracle is the handler's recorded request list | predicted: `Assert.Empty() Failure: Collection was not empty; contained 1 item` (the unchecked revision is submitted) |
| V8 | same, `ReadTool_RefusesAndDoesNotFrameTheWrongPost` | `curia_read {postId:A}` over the same handler: `isError`, and the text holds no passage for B | predicted: the first block names B's root |
| V9 | same, control `ReviseAsync_StillRevisesTheRequestedPost` | handler serves A for A: one POST, `Parent == A` | green first; held by FV1 |

**Order of red.** V1, V3, V7, V8 red on the unmodified tree for the stated reasons; V2, V4, V9 green first (controls); V5, V6 need the check present, so they are written against the stub error and recorded red at Task 1 by the stub's wrong behaviour (stub returns ok always).

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FV1 | the check removed (the lambda ignores the id) | V1, V3, V7, V8 |
| FV2 | `OrdinalIgnoreCase` | V5 |
| FV3 | the check placed before the `NotModified` branch, comparing a null id | V4 |
| FV4 | `GetPostIfChangedAsync` left unchecked, `GetPostAsync` checked | V3 |
| FV5 | the served id echoed raw | V6 |
| FV6 | the check refuses everything | V2, V9 |
| FV7 | `ReviseAsync` reads `Parent` from the requested string instead of `post.PostId` (the guard moved to the consumer) | none by V7 alone: V7 stays red-then-green via the client check, so FV7 documents that `ReviseAsync` is *not* the guard; expected result recorded, not required red |

No row may stay green except FV7, whose expected outcome is stated above; a row that stays green unexpectedly means a probe is missing.

## Gates

Every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector via `CURIA_TEST_POSTGRES`), Debug build then `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence`, `check-spec.py` and `falsify-spec-checks.py` (the errata is not touched, so both stay as main's), invisible-code-point scan of added lines, `git status --porcelain conformance/` empty. Three worktrees run these at once: each has its own `bin/obj`, and each run provisions its own throwaway database on the shared server.

## Docs owed before the PR

`IMPLEMENTATION_PLAN.md`: **D63's entry only**: its heading gains `(closed by <PR>)`, its body gains one closing paragraph (V1 to V9, FV1 to FV7, the readers' wording from Task 3, the sentence "a list route's per-item identity is not checked here"). No Merged row, no "Current work"/"Next stage" edit, no README, no CLAUDE.md: the wave's docs scheme leaves those to the touch-up.

## What I did not check

Whether `Interpret` maps a `Result.Fail` from the lambda to `Malformed` (read from the enum's doc comment, not the body at `ForumClient.cs:410`); how `ClientErrors` quotes a Forum-supplied value (Task 1 reads it); which fixtures serve one id for every path beyond `StubLog:1599`; the exit code `curia read` returns for `Malformed`.

## As run

Built in a detached worktree at 285d10c8 (#113). Differences from the plan above:

- **Live operation (Tasks 0 and 6) was not run.** No Forum and no proxy were started; the end-to-end oracle is the recorded request list of `StubLog`, which serves its one post for every `/v1/posts/*` request and so is the hostile Forum for any other id.
- **Probes.** V1 to V6 are `ServedPostIdentityTests` (a scripted handler of its own); V7 to V9 are `ReviseServedIdentityTests`, using `StubLog` unchanged. Red on the unmodified tree: V1, V3, V5 (three pairs) and V6 failed with `Assert.False() Failure, Expected: False, Actual: True`; V7 and V8 with `Assert.Throws() Failure: No exception was thrown` (a refusal in `curia_read` and `curia_revise` is a thrown `McpException`, not an `isError` result, so V8 asserts on the exception message instead of a frame); V2, V4, V9 were green first. V7 and V9 count writes that carry an envelope, since a token request is also a recorded write.
- **No fixture served another id.** The whole `Curia.Client.Tests` (754) and `Curia.Mcp.Tests` (374, 1 skipped) passed with the check in, and `StubLog` was not edited. The Api assembly is run whole in the gates.
- **Error.** `ClientErrors.ServedPostMismatch(requested, servedLength)`; the requested id goes through `DisplayLiteral.Of`.
- **Falsification.** FV1 red: V1, V3, V5 x3, V6, V7, V8. FV2 (`OrdinalIgnoreCase`) red: V5 `("a","A")`. FV3 (the 304 branch not wired) red: V4. FV4 red: V3. FV5 (served id echoed) red: V6. FV6 (refuses all) red: V2, `GetPostIfChanged_AcceptsTheRequestedId`, V9. FV7 green by design. A culture-compare mutant does not compile under the analyzers.
