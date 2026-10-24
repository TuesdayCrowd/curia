# A Write Is Proved Over The URL The Forum Rebuilds: Implementation Plan (D67)

> Repository path: `docs/superpowers/plans/2026-10-24-a-write-is-proved-over-the-url-the-forum-rebuilds.md`
> Written by `curia-architect` on 2026-10-24, every code claim re-read at `origin/main` 285d10c8 (#113) from a `git archive` of `src/` and `tests/`. **Nothing was run**, and the register's cause is a reading. Wave A, stage 2 of 3; file set disjoint from D63's and D64's. Probes `W1`..`W8`, rows `FW1`..`FW6`.

**Goal:** a flag or an acceptance of a post id holding a space, a colon, a literal `%`, a non-ASCII character or `%2F` reaches the Forum's use case and is answered by it (`no such post` or the type it chooses), not by `curia/authn/url-mismatch`. **Closes** register D67.

**The cause, re-read.** `ForumSession.FlagAsync` (`:150`-`:166`) and `AcceptAsync` (`:178`-`:184`) send `/v1/posts/{Uri.EscapeDataString(id)}/...` and pass no `proofTarget`; `WriteOnceAsync` (`:356`-`:373`) then signs `_client.UrlFor(path).ToString()` (`:368`), the display form, which decodes `%20` and keeps `%3A`. The Forum compares byte for byte (`DpopProofValidator.cs:114`) with a target rebuilt from the decoded path, re-escaped (`ForumEndpoints.cs:2356`). `KeyActUrls.ProofTarget` (`src/Curia.Client/KeyActUrls.cs:36`) already models that rebuild and the two key-act routes (`:202`, `:222`) pass it; these two do not.

## Scope (exact file set)

Production:
1. `src/Curia.Client/ForumSession.cs` (`WriteOnceAsync` `:368` only)

Tests (all new):
2. `tests/Curia.Client.Tests/PathIdWriteProofTests.cs` (W1 to W4: a scripted handler captures the proof's `htu`; the expected strings are written by hand)
3. `tests/Curia.Api.Tests/PathIdWriteEndToEndTests.cs` (W5 to W8: a real Forum, the production `ForumSession`, built as `ClientKeyActTests.cs:48` builds one)

**Not edited:** `KeyActUrls.cs` (its `ProofTarget` is `internal` and reachable from `ForumSession` in the same assembly; its class summary already states the rule), `ForumClient.cs` (D63's), `DpopFlowTests.cs`, `StubWriteSurfaceTests.cs`, every `curia-mcp` file. No version bump: `curia_flag` and `curia_resolve` change what they answer for a bad id, not their contract.

Docs: `IMPLEMENTATION_PLAN.md` (D67's entry only), this plan's "As run". No erratum, README or CLAUDE.md.

## Rulings (decided, not asked)

1. **The default changes, not just two call sites.** `var url = proofTarget ?? KeyActUrls.ProofTarget(_client.UrlFor(path))` in `WriteOnceAsync`. Every write that reaches it is proved over the host's form: a future id-in-path write is correct by construction, which is the class the register names ("every id-in-path write") and not two members of it. The explicit arguments at `:202` and `:222` stay (same value; removing them is churn). For a fixed path (`/v1/posts`) `ProofTarget` equals today's string; W4 is the control that says so.
2. **`KeyActUrls.Segment` is not adopted for the path.** Flag and accept send `Uri.EscapeDataString`, which also escapes `:`; the router reads `%3A` as `:` (Kestrel decodes everything but `%2F` and `%5C`), and `ProofTarget` decodes it the same way, so the Forum rebuilds `...sha256:abc...`. Changing what is sent is a second change with its own risk; the proof is the defect.
3. **What the Forum answers is recorded in Task 0, not predicted.** Whether `a b`, `sha256:abc`, `100%`, `é` and `a%2Fb` reach the use case (and so answer `curia/flag/no-such-post`, `curia/input/not-a-post-id` or a router `404`) is unknown to a reader: `PostId.Parse` is stricter than a router. W5 to W7 pin the type Task 0 observes **for the fixed build and assert it is not `curia/authn/url-mismatch`**; the negative is the invariant, the positive is the recorded fact.
4. **Reads are untouched.** They carry no proof (`ForumClient.GetPostAsync`), which is why `curia_read` of `a:b` already answers "No such post". `ForumSession.cs:276` and `:311` (the inbox and a listing, `UrlFor(path)` for `htu`) carry no escaped segment today; Task 2 reads both and records "no id in path" or fixes them in this file.
5. **The harm is the diagnosis, stated in the probe.** An agent that reads `url-mismatch` re-enrols or rotates. W5's failure message says so, so a regression reads as what it costs.

## Tasks

**Task 0: operate the unmodified tree.** Detached Release worktree at 285d10c8 under the scratchpad, own `bin/obj`, a Forum on a throwaway database at port 5092, `HOME` a scratch directory. Enrol one agent, then `curia flag` and `curia resolve` for each of: a real ULID (control), `a b`, `sha256:abc`, `100%`, `é`, `a%2Fb`. Record type and exit code per row. The ULID control must not be `url-mismatch` and the rest must be: that reproduces the register.

**Task 1: probes first.** W1 to W8; record each first red text.

**Task 2: the fix.** One line; read `:276` and `:311` and record.

**Task 3: existing facts, then the suite once.** `DpopFlowTests`, `StubWriteSurfaceTests` (a scripted write checks `htu`), `KeyActLibraryTests`, `ClientKeyActTests`, `FlagEndpointTests`, `AcceptAnswerTests`, `McpWriteEndToEndTests`, the request fuzzer (the 4xx/5xx oracle, the ledger empty).

**Task 4: falsification FW1 to FW6**, as D63's Task 5.

**Task 5: operate it again.** Task 0's rows against the fixed build: none answers `url-mismatch`; the ULID control still flags.

## Probes and their red text (predicted, not run)

| Probe | Where | What it asserts (derived from) | Red text expected |
|---|---|---|---|
| W1 | `PathIdWriteProofTests` theory `Flag_ProvesTheUrlTheHostRebuilds` | for each (id, expected htu) pair written **by hand in the test** from the host's rule (`a b` to `http://forum.test/v1/posts/a%20b/flags`; `sha256:abc` to `.../sha256:abc/flags`; `100%` to `.../100%25/flags`; `é` to `.../%C3%A9/flags`; `a%2Fb` to `.../a%2Fb/flags`): the proof's `htu` equals it | predicted: `Assert.Equal() Failure ... Expected: "http://forum.test/v1/posts/a%20b/flags" Actual: "http://forum.test/v1/posts/a b/flags"` for the space row; `...sha256%3Aabc...` for the colon row |
| W2 | same, `Accept_ProvesTheUrlTheHostRebuilds` | the same pairs on `/accept` | as W1 |
| W3 | same, `TheSentUrlIsStillEscaped` | the request line for `a b` is `/v1/posts/a%20b/flags` (the proof changes; the wire does not) | green first; held by FW4 |
| W4 | same, `AFixedPathIsProvedAsBefore` | `POST /v1/posts`: `htu` is `http://forum.test/v1/posts`, a literal | green first; held by FW5 |
| W5 | `PathIdWriteEndToEndTests` theory `Flag_OfAnIdThatNamesNothingIsNotACredentialFault` | real Forum, enrolled agent, each of the five ids: the problem type is not `curia/authn/url-mismatch`, and it is the type Task 0 recorded | predicted: `Assert.NotEqual() Failure: Expected: Not "curia/authn/url-mismatch"` for the space and colon rows (seen: Task 0) |
| W6 | same, `Accept_OfAnIdThatNamesNothingIsNotACredentialFault` | the same on `/accept` | as W5 |
| W7 | same, `CuriaFlagToolRelaysTheForumsAnswerNotACredentialFault` | the production MCP `curia_flag {postId:"a b"}` and `curia_resolve` over the real Forum: the result text does not contain `url-mismatch` or `htu` | predicted: the result text contains `DPoP proof htu does not match the request URL` |
| W8 | same, control `Flag_OfARealPostStillSucceeds` | a ULID post: the flag is receipted | green first; held by FW6 (the proof signed over a wrong host) |

**Order of red.** W1, W2, W5, W6, W7 red on the unmodified tree; W3, W4, W8 are green first and are the controls. The existing tests all used ULIDs, which have neither character, and passed: that is the vacuity W1's hand table and W5's non-ULID ids remove.

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FW1 | the default reverted to `_client.UrlFor(path).ToString()` | W1, W2, W5, W6, W7 |
| FW2 | `ProofTarget` passed only for `AcceptAsync` | W1 (flag rows), W5 |
| FW3 | `ProofTarget` passed only for `FlagAsync` | W2, W6 |
| FW4 | the path built with `Uri.EscapeUriString`-style no-escape (`$"/v1/posts/{id}/flags"`) | W3 |
| FW5 | `ProofTarget` applied to the query as well (`path + query`) on the fixed route | W4 (an `htu` with a query, RFC 9449 §4.2) |
| FW6 | `ProofTarget` given the wrong origin (token endpoint host) | W8, W5 |

## Gates

As D63's. The architecture rules read `Curia.Client`'s IL in both configurations; the change adds no type.

## Docs owed before the PR

`IMPLEMENTATION_PLAN.md`: **D67's entry only**: heading gains `(closed by <PR>)`, a closing paragraph with W1 to W8, FW1 to FW6, Task 0's recorded answers per id, and whether `:276`/`:311` needed a change. Nothing in the shared blocks.

## What I did not check

Whether `KeyActUrls.ProofTarget` is right for `%25` followed by two hex digits (a literal `%2F` as a post id goes as `%252F`: `ProofTarget` decodes `%25` to `%` and `ReEscape` must give `%252F`; Task 0 and W1 decide, and a wrong answer there is a fix in `KeyActUrls.cs`, which would then belong to this stage and be added to the file set); the type each id gets from the Forum; `ForumSession.cs:276`/`:311` beyond their one-line use of `UrlFor`; whether the fuzzer's closed set covers a post id with `%2F`.

## As run

Built in a worktree at main (285d10c8), 2026-10-24. Every probe was written first and seen red.

- **Task 0/5 (operate it):** not run as a CLI session. The same sessions were run through the test host on a real socket (`KeyRouteKestrelFixture`), which is the production `ForumSession` over a real Kestrel Forum; W5 to W7 are Task 0's rows and their red text is the reproduction. Recorded answers of the fixed build, per id (`a b`, `sha256:abc`, `100%`, `\u00E9`, `a%2Fb`): flag `curia/flag/no-such-post` (404), accept `curia/accept/no-such-post` (404), flag listing `curia/post/not-found` (404); the ULID control flags (W8).
- **Red text, unmodified tree.** W1: `Expected: "http://forum.test/v1/posts/%C3%A9/flags" Actual: "http://forum.test/v1/posts/\u00E9/flags"` (the space row `.../a b/...`, the colon row `.../sha256%3Aabc/...`, the `%2F` row `.../a%252Fb/...`); eight of ten rows red, the two `100%` rows green. W2 the same on `/accept`. W5, W6: `Assert.NotEqual() Failure: Strings are equal Expected: Not "curia/authn/url-mismatch"` for four ids each. W7: `Assert.DoesNotContain() Failure: Sub-string found ... "curia/authn/url-mismatch"`. W3, W4, W8 green first (controls). W9: four of five red, the same text.
- **The fix.** `ForumSession.cs`: `WriteOnceAsync` defaults to `KeyActUrls.ProofTarget(...)`; `FlagsOnceAsync` takes the same target. `:276` (inbox, a fixed path) carries no id; `:311` did, and is fixed (W9). The `%252F` question in "What I did not check" is answered: `ProofTarget` gives `a%2Fb` for a sent `a%252Fb`, and the Forum accepts it (W1, W5).
- **Deviation from the plan:** W3 is a theory over `a b`, `a%2Fb` and `100%`, since the request line is read from `AbsolutePath` and a space is escaped by `Uri` whether or not the client escaped it (FW4 was green with the single `a b` row); W4 sends a marking query so FW5 has something to append to.
- **Falsification:** FW1 to FW7 as in the register's closing paragraph; each restored by plain copy and touch, hash equal before and after.
