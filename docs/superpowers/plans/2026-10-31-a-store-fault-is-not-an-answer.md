# A Store Fault Is Not an Answer: Implementation Plan (D95, with D61 item 48 and D91's tail)

> Repository path: `docs/superpowers/plans/2026-10-31-a-store-fault-is-not-an-answer.md`
> Written by `curia-architect` on 2026-10-31 against `main` at `fb04ea74` (#137, the D92 PR, errata G51, `curia-mcp` 0.9.2). Scope and decisions: register D95 (with its Task 10 extension), D61 item 48 and D91's tail, which the register says to fold in one PR; errata **G52** (R5.23, R7.31, R8.82, R9.52, R11.71, R11.72; "specified; not built", applied to `curia-whitepaper-ERRATA-AND-ADDENDUM.md` by the job that wrote this plan, with six index rows). Task 0 re-reads G52 before building on it. Every `file:line` below was read from `main`'s tree; Task 0 re-reads each. **Nothing here has been built, run or measured**: every "predicted red" is a prediction, and every claim that rests on reading alone is a lead that Task 0 measures first (the list is under "Task 0").
> Not read, so Task 0 reads it: `AccessTokenValidator` and `LogBoundKeys.ReadAsync` (which reader they hold), `ForumFixture` and `KestrelForumFixture` (how a decorated store is registered; whether a TestServer host rethrows an unhandled exception to the caller, which I expect and which decides the Kestrel legs), `SurfaceInventory` and `Exemplars` (what a valid request per route needs, and which routes read no store), `ForumResult.cs`'s `FrameBuilder`/`BudgetWording` (the model for `StoreWording`), `WriteTools.cs:604` (how `ServerFault` is relayed), `ToolText.cs`/`Help.cs`, `Curia.Operator/Program.cs` (what it prints on a fault), `README.md`'s operations paragraph, `PostgresAdapters` (where the three pools and the data sources are built, and which adapter methods can throw).

## What the reading found that the register did not know

1. **D95 cannot be fixed at the validator alone, and cannot be fixed at the store alone.** Making the store report (the register's own sentence: "the store reports a failed read as a failed `Result`") turns eleven call sites of `ReadEventsAsync` (`ForumEndpoints.cs:2699`) into `403 table-10/denied tier=Anonymous`; leaving the store throwing leaves the `Result` arms unreachable against Postgres. The plan therefore **removes the swallow in the same task as it adds the mapping** and rules that the store *throws*, not reports (G52 decision 2).
2. **A `Result` is the wrong carrier even on its own terms.** I read every caller that propagates a failed read: `ParentLookup`, `EnrollAgent`, `EnrollIdentity`, `AttestOwner`, `RaiseFlag`, `AcceptAnswer`, `ApplyModeration`, `KeyActs` pass the error up, and the routes answer it five different ways. `ErrorStatus` (`:3336`) maps the type to `503`, but `ForumEndpoints.cs:675` (enrollment), `:1069` (PERSIST) and `:1871` (a moderation record) answer **a use case's failed `Result` as `500` whatever its type**. A reported failure would have reached `500` at those three sites even after the swallow was gone. The fix is that `Problem(status, error)` (`:2937`), the factory every route calls, maps either type to `503`.
3. **The gates already hold the classification, twelve times.** `PostgresAgentWriteGate.cs:123,128,173,177`, `PostgresRevisionGate.cs:81,88,142,146` and `PostgresFlagRaiserGate.cs:93,100,154,158` each catch `NpgsqlException` and `TimeoutException` directly: a fault wrapped one level deep (an `InvalidOperationException` or `AggregateException` around a driver fault, which a pool timeout may be) escapes a gate that was written to catch it. One predicate walks the `InnerException` chain.
4. **A TestServer host does not show the defect.** `WebApplicationFactory`'s in-process server rethrows an unhandled exception to the `HttpClient` caller; Kestrel answers a body-less `500`. A probe that "expects 503" and a seam tree that "answers 500" are both statements the in-process host cannot make (trap 67's family, a fixture that cannot see the quantity under test). The suite already has a Kestrel fixture (`RequestFuzzKestrelTests`, `UseKestrel(0)`); the probes that matter run on it.
5. **A body-less `500` is a refusal that "names no problem type".** The client reports `curia/client/unreadable-problem` for it (`ForumClient.cs:579`). G52 decision 9 makes the unclassified exception a problem document too, so the only `500` left says what it is.
6. **The token route spends the client's `jti` before it reads the log** (`TokenEndpoint.cs:140-148`: `ConsumeAsync`, then `ReadAllAsync`), so a store fault after it makes the identical request a replay. The reference client builds a new assertion and proof per call (`ForumSession.cs`, `_signer.ClientAssertion(endpoint, now)`), so its retry is fine; R5.23 states it for any other client.
7. **A write route's receipt is the thing that must not read a store after the commit.** D92 did it for the vector; the budget receipt is "computed inside the hold, no second read" (R7.29). Whether the post, accept and flag routes read anything else after their append is read, not run: M5.
8. **The fuzzer's oracle is blind to this in the way D61 item 49 names for another reason**: it runs a healthy store and counts any `5xx` as a finding. The sweep this PR adds arms the fault *after* the exemplars are seeded.

## Identifiers (provisional, and what renumbers)

**Allocated 2026-10-31 by the job that wrote this plan, re-grepped (`git grep` over every tracked file, working tree clean, `main` `fb04ea74`) immediately before the edit:** errata **G52** (highest was G51); requirements **R5.23** (highest of section 5 was R5.22), **R7.31** (R7.30), **R8.82** (R8.81), **R9.52** (R9.51), **R11.71** and **R11.72** (R11.70); no section 4, 6 or 10 number; the error types **`curia/store/unavailable`** and **`curia/server/fault`**; the OAuth code **`temporarily_unavailable`**; the types **`LogUnreadableException`**, **`IStoreFaultClassifier`**, **`StoreWording`**; the log event **`StoreFault`** (id 5002; ids 5000 and 5001 are taken); `curia-mcp` **0.9.3** (current 0.9.2, `ToolCatalogue.cs:41`); the probe prefix **ULG** and the falsification prefix **FULG**, none of which occurs in the tree. **Not allocated:** a register entry (D95 is closed by this PR; the leads below are filed at the end, by the number after a re-grep) and trap numbers (the next is 83, and the closing task re-greps). Whoever builds this re-greps every one of these first; if another PR landed, the PR that merges second renumbers by hand.

## Scope (exact file set)

The change, in one sentence: a fault of a store the Forum owns is answered `503` with a problem document and no detail (and, at the token endpoint, RFC 6749's `temporarily_unavailable`), by one predicate and one outermost middleware, with the empty-log fallback removed and the three write routes answering a committed act from what it holds; the clients word the two types from one definition and never retry; nothing else.

**Group A, the classification and the store (R11.71; `Curia.Application`, `Curia.Infrastructure`)**
- `src/Curia.Application/Ports/LogUnreadableException.cs` (new): public sealed, `Cause` (a type name and, for a Postgres fault, the SQLSTATE; never a message), the driver's exception as `InnerException`.
- `src/Curia.Application/Ports/IStoreFaultClassifier.cs` (new): `bool IsUnavailable(Exception)`; an in-memory implementation that names no exception type and answers `false`.
- `src/Curia.Infrastructure/PostgresStoreFaults.cs` (new): the one predicate (`NpgsqlException` or `TimeoutException`, in the exception or its `InnerException` chain to a depth of eight) and the port's implementation.
- `src/Curia.Infrastructure/PostgresEventStore.cs`: `ReadByAggregateAsync` (`:256`) and `ReadForwardAsync` (`:279`) wrap a store fault in `LogUnreadableException` (not the caller's `OperationCanceledException`); nothing else changes.
- `PostgresAgentWriteGate.cs`, `PostgresRevisionGate.cs`, `PostgresFlagRaiserGate.cs`: the twelve `catch (NpgsqlException)`/`catch (TimeoutException)` clauses (six pairs) become `catch (Exception e) when (PostgresStoreFaults.Is(e))` with their existing bodies (a reasoned `CA1031` suppression where the analyzer asks). `PostgresFlagDetailStore.cs` and `PostgresVectorIndex.cs` catch `PostgresException` for a reason of their own (a unique violation; a vector's SQLSTATE) and are listed in the fence with that reason.
- `src/Curia.Application/Ports/IEventStore.cs`: the doc comment of `IEventReader` states that a read does not return a failed `Result` for a fault of the store.

**Group B, the edge (R7.31, R8.82, R9.52; `Curia.Api`)**
- `src/Curia.Api/StoreFaults.cs` (new): the middleware (`UseStoreFaults`) and the factory (`LogUnreadable(cause)`, `Unavailable(cause)`, `Fault(cause)`); it does nothing where `Response.HasStarted` or the request's own token is cancelled; for `/oauth/token` it answers through `OAuthRefusal`.
- `src/Curia.Api/Program.cs` (`:351`): `app.UseStoreFaults()` first, before `UseUnreadableRequests`; the classifier registration.
- `src/Curia.Api/ForumEndpoints.cs`: `ReadEventsAsync` (`:2699-2704`) returns the log or throws `LogUnreadableException` for a failed result, its doc comment rewritten; `Problem` (`:2937`) maps either type to `503` whatever status the caller chose; the local mappings (`:1476`, `:2876`, `ErrorStatus` `:3336`) call the factory; `ActaEndpoints.cs:233` the same, `:239` (the fold error) stays `500`.
- `src/Curia.Api/Issuer/OAuthRefusal.cs`: the code `temporarily_unavailable` (status `503`, the fixed description); `TokenEndpoint.cs` (`:135`, `:150`): a failed read or an `LogUnreadable`-typed validation failure answers it, `:171` (the posture) stays `server_error`.
- Whichever of the three write routes reads a store after its append (M5 decides; expected none).

**Group C, the clients (R11.72; `Curia.Client`, `Curia.Client.Cli`, `Curia.Mcp`)**
- `src/Curia.Client/StoreWording.cs` (new): the three sentences, one definition; `ForumResult.cs` (the `ServerFault` arms beside `GateUnavailableType`, `:159-168`) calls it; `ForumClient.cs` reads `temporarily_unavailable` as `ServerFault` (it is a `503` already; the arm is by status) and `ForumSession.cs` words a token refusal with the token sentence.
- `src/Curia.Mcp/WriteTools.cs`/`ForumTools.cs`: relayed as the gate's `gate-unavailable` is (Task 0 reads `:604`); `ToolText.cs`, `Help.cs`: the repeat sentence in the descriptions of the write tools and the helps of the verbs that write (derived from the dispatch, as PIL30 was); `ToolCatalogue.Version` 0.9.3 and the pins and parsers PR 3's ruling 23 made one.

**Tests (file names are the build's, ids the plan's)**
- `tests/Shared/FaultableStore.cs` (new, the seam): decorators over `IEventStore` with modes `ThrowUnreadable`, `ReportFailure` (a failed `Result`), `ThrowDriver` (`NpgsqlException` with a `TimeoutException` inner), `ThrowOnAppend`, `AppendThenThrow`, `FailReadsAfterFirstAppend`, armed and disarmed by the test after seeding; the same shape for `IDpopNonceStore`, `IReplayCache` and `IAgentKeyRegistry` (`ThrowDriver`); `tests/Shared/PublishedStoreFaults.cs` (reads G52).
- `tests/Curia.Api.Tests/StoreFaultEdgeTests.cs` (ULG1 to ULG6, ULG20 to ULG24, ULG35 to ULG38, TestServer), `tests/Curia.Api.Tests/StoreFaultKestrelTests.cs` (the Kestrel fixture: ULG4, ULG15 to ULG17, ULG19), `tests/Curia.Api.Tests/Fuzz/FaultedStoreSweepTests.cs` (ULG1 to ULG3's inventory sweep), `tests/Curia.Infrastructure.Tests/StoreFaultInfrastructureTests.cs` (ULG8 to ULG14), `tests/Curia.Client.Tests/StoreWordsTests.cs` and `tests/Curia.Mcp.Tests/StoreWordsToolTests.cs` (ULG25 to ULG31), `tests/Curia.Architecture.Tests/StoreFaultFenceTests.cs` (ULG6, ULG14).
- **Existing facts that change their object (named in each commit; Task 0's M10 completes the list):** SEB53 (the readout's `503` at the port) stays green and gains its real-store twin ULG16; `TokenEndpointRefusalTests` and `KeyBindingTests` (they read `server_error`) are re-read for any that pin `500` on a store fault; any fact that pins `unreadable-problem` for a body-less `500`; `ServerFaultTests` stays green (its `503` is the vector index's).
- **Controls that stay green, named so the list is complete:** `ServerFaultTests`, the near-duplicate `503`, the Acta's fold-error `500`, a healthy store's every contract fact, the request fuzzer's ledger (empty).

**Docs**: the errata (G52 applied; Task 9 sets its status), `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`'s status paragraph, `README.md` (the version and the operations paragraph: what `503` `curia/store/unavailable` means to an operator, and that the Forum logs `StoreFault` with the exception), `Help.cs`, `ToolText.cs`.

**No change:** `db/`, `rust/`, `tools/`, `conformance/`, `.github/workflows/ci.yml`, R15.1's set, any event type, the budgets and gates' types and statuses, Table 10 and Table 11, the white paper, exit codes, the vector index, `HybridSearch`.

**Out of scope, named so nobody drifts into it:** a readiness route; the pool-size start-up check (D91's other half); the whole-log read's cost per token and per write (D61 items 47, 48 second half); the operator tool's wording of a fault; caching a read through a fault; a `Retry-After`; a new exit code; the nonce store's failed `Result`.

## Rulings (decided, not asked)

1. **The store throws; it does not report.** A `Result` can be read as empty; an exception has one meaning at one place; twenty callers and five mappings cannot be audited into agreement (reading finding 2). The operator keeps every word the driver said as the wrapped exception, which a failed `Result`'s `Detail` (reduced to the slug by `LogBoundKeys.LogUnreadable`) would have lost.
2. **The swallow is removed in the task that adds the mapping**, never before and never after.
3. **One predicate, by type, walked to depth eight, shared by the gates and the edge.** Rejected: SQLSTATE classes (a drifting list); `Exception`.
4. **One outermost middleware and one factory; `Problem` carries the mapping** so the three sites that answer a use case's failure `500` are covered without a status edit each.
5. **Two 503 types** (`log/unreadable` = a read, nothing recorded; `store/unavailable` = anything else, unknown) and one OAuth code; **an unclassified exception is `500 curia/server/fault`**, a problem document.
6. **A committed act is answered from what it holds** (R8.82), proved by a fault armed after the first append.
7. **No `Retry-After`, no new exit code, no client retry.** The words carry the repeat rule; the refusals that make a repeat safe already exist.
8. **The Kestrel fixture carries the probes that need a real response.** The in-process host is for the sweep, where its rethrow is itself the red.
9. **The real-store faults are made, not simulated:** `REVOKE SELECT ON events` from the app role (restored in a `finally`, and checked restored by a test-end assertion), a row-level-security policy whose function raises on rows only a whole-log scan reaches (the D90 PR's Task 10 method), and a Forum run at `Maximum Pool Size=4;Timeout=1`.

## Tasks

### Task 0: Re-read, operate the unmodified tree, commit the seam and the probes red

Start from `main` after any merge that has landed; otherwise from `fb04ea74`. Re-read G52 and `IMPLEMENTATION_PLAN.md`; **re-grep every identifier under "Identifiers"**; run `check-spec.py` and `falsify-spec-checks.py` (both pass first); build `Curia.sln` in Release first and run the baseline `--no-build` as CI does, recording the failures by name (the Sighup fact is the known one) and the Api suite's time. Then, **with the tree unmodified**, build Release in a detached worktree outside the repository (own `bin/obj`, own port, a throwaway database, Postgres with pgvector), and record verbatim, counting in the database and never in the responses:

1. **M1, D95 as seen.** Mint a token for an enrolled T1 agent; `REVOKE SELECT ON events`; for every verb of `curia` (read, thread, search, board, inbox, budget, flags, recheck, verify, ask, comment, revision, resolve, flag) record the status, the body (is it empty?), the exit code and the text; the Forum's log; restore the grant. **Decides** the baseline each probe must beat and fills ULG15's "actual".
2. **M2, the whole-log-only fault.** The RLS method of the D90 PR: per-aggregate reads work, a whole-log scan raises. `GET /v1/budget`, `/v1/flags`, a post, an accept. **Decides** which routes reach `ReadEventsAsync` before the validator's read is the one that fails, and ULG16.
3. **M3, the token.** With the grant revoked, `POST /oauth/token` and `curia ask` (status, body, text); and with the RLS fault; and with the `jti` spent: the same assertion sent again after the faulted call (a replay refusal, as finding 6 says).
4. **M4, the exception a starved pool throws.** A Forum at `Maximum Pool Size=4;Timeout=1` under 64 concurrent distinct agents: the exception **type, message and inner chain** from the Forum's log for a pool timeout, for a refused connection (`53300`, `max_connections` lowered) and for a killed connection (`pg_terminate_backend`). **Decides** whether the predicate (`NpgsqlException` or `TimeoutException`, depth eight) covers all three; if a pool timeout is an `InvalidOperationException` with no driver inside it, G52 decision 1 is amended before anything is built.
5. **M5, what is written after the commit.** Read the post, accept and flag routes from their append to their return and list every store read, every `await` on a store and every exception it can throw. **Decides** R8.82's first sentence: if one exists, the receipt member that depends on it is composed from the event and the plan says which (the D92 pattern), and ULG22 is red on the seam tree rather than a control.
6. **M6, who else swallows.** `git grep` for `TryGetValue(out var .*, out _) ?` and for `: \[\]` after a read in `src/Curia.Api`, `src/Curia.Application`; every `catch` in `src` that names a store exception; `ReadAllAsync` callers that discard the error. Each is a place where a fault becomes an absence (vacuity question 3).
7. **M7, where the carrier decides.** For each of `:675`, `:1069`, `:1871` and the other `Problem(500, ...)` sites (`:887`, `:1011`, `:1182`, `:1491`, `:1943`, `:2043`): which failures reach them and whether any is a store failure by type. Confirms reading finding 2.
8. **M8, the operator tool and the hosted services.** `curia-operator` (a moderation record, `sign-head`) with the grant revoked: what it prints; the Forum started with the log unreadable: does it start, and what do the reconcile and catch-up services do. Recorded, not changed.
9. **M9, the fixtures.** Whether `WebApplicationFactory` rethrows an unhandled exception (expected) and what the Kestrel fixture does for the same; how `ConfigureTestServices` replaces `IEventStore` so that `LogBoundKeys` and `AccessTokenValidator` hold the decorator (and not a second registration); `SurfaceInventory`'s routes that read no store and `Exemplars`' seed requirements; whether `ILogger` output is capturable (`LogCapture` exists); how a test revokes and restores a grant in a throwaway database and whether the collection serializes it.
10. **M10, what pins them.** `git grep` for `unreadable-problem`, `server_error`, `500` beside a store fault, `curia/log/unreadable`, `SEB53`, `"0.9.2"`, every construction of `OAuthRefusal`, every `catch (NpgsqlException`/`TimeoutException` in `src`, and the version parsers. Each is a fact that changes its object, named.
11. **M11, the gate on a fault.** With the append throwing (a decorator on a Kestrel host): after the `500`, is the agent's next write `agent-in-flight` (the hold not released), is a revision of the same `prev` `revision-in-flight`, is a second flag refused? `await using` suggests not; run it.
12. **M12, the baseline cost.** The Api suite's time on the unmodified tree (a `try` around `next` is free and is not measured).

Write the probes below in their failing form, run them against the seam tree, record each actual red text beside the prediction (a probe green on the seam tree is a control and says so), and amend this plan for what Task 0 changes, in place, dated, continuing the ruling numbers. **The seam (behaviour-preserving):** the new types declared (`LogUnreadableException`, `IStoreFaultClassifier` with its two implementations, `PostgresStoreFaults.Is` real because it is pure and only unit-tested, `StoreWording` with inert bodies, `StoreFaults` declared and **not registered**), the decorators, `PublishedStoreFaults`. No status moves and no member is emitted, so a probe is red by assertion and no red state is built from a stub that is itself a defect (trap 67). Commit the seam, then the probes red (`but commit`), before any behaviour.

### Task 1: The classification and the store (R11.71)
`PostgresEventStore` wraps; the gates onto the predicate; the port's comment. ULG8 to ULG14 go green.

### Task 2: The edge (R9.52, R7.31, R8.82)
The middleware registered, the factory, `Problem`, `ReadEventsAsync` throwing, the local mappings, the Acta. ULG1 to ULG7, ULG20 to ULG24, ULG35 to ULG38 go green. **The swallow's removal and the middleware land in this one commit.**

### Task 3: The token (R5.23)
`OAuthRefusal`, `TokenEndpoint`. ULG17, ULG19 go green.

### Task 4: The clients (R11.72)
`StoreWording`, both clients, descriptions and helps, `curia-mcp` 0.9.3. ULG25 to ULG31 go green.

### Task 5: Real-store faults, existing facts, stale sentences, the whole suite once
ULG15 to ULG16 on the Kestrel/Postgres fixture; the facts that change their object (each named in its commit); a `git grep` for sentences that say an unreadable log is a `500` or "outside this clause: register D95", and the doc comment of `ReadEventsAsync` ("deliberately conservative"), each retired with a reason beside it; then the whole suite once, built first and `--no-build`, the request fuzzer included; compare with Task 0's baseline: nothing new; **report the added minutes of the Api suite** (more than three is a finding).

### Task 6: Review
Spec compliance, then code quality (two passes); fixes in their own commit. The reviewer is asked first: is there a path that turns a failed or faulted read into an empty list or a count of zero (grep, then read); does the middleware rewrite any `4xx`; does it catch the caller's cancellation; can a classified fault escape a gate; does any log line carry a request body, a token or a post's text (the exception's message can carry SQL parameters: the response never carries it, the log does, and the plan says so); does each client word its own line.

### Task 7: Falsification FULG1 to FULG38
Each row on a kept copy (never `git checkout -- <path>`), rebuilt `--no-incremental`, restored with a plain copy and `touch`, one unpatched run after. Record the probes red per row and the second form where the first was green. A row green on its first form is a gap in a probe, and the probe is added before the row is closed.

### Task 8: Operate it again (the live run)
Against the built Release tree, a throwaway database, the events table and the Forum's log as the oracles:
- **L1, D95 as seen.** M1 repeated on the built tree: every verb exits 8 with the `curia/log/unreadable` words; a body is always present; the Forum's log carries `StoreFault` with the Npgsql message; restore the grant and, with no restart, every verb works.
- **L2, the whole-log fault.** M2 repeated: `/v1/budget` and `/v1/flags` `503`; per-aggregate routes pass.
- **L3, the token.** M3 repeated: `curia ask` prints the token words and exits 8; restored, it works with a new request.
- **L4, the pool.** M4's starved Forum under a 64-wide burst of distinct agents and of one agent: no `500`, no body-less answer; only `2xx`, the budget's `403`, `409 agent-in-flight` and `503` of G52's types or `gate-unavailable`; throughput recovers when the burst ends without a restart.
- **L5, a killed connection.** `pg_terminate_backend` on the Forum's own connections during a write burst: every answer a `2xx` or a `503`; the store's rows equal the `2xx` answers plus the `503`s that were committed (counted by digest; a `503` that was committed is the "unknown" case, and its repeat is `409 already-recorded`); the pool heals.
- **L6, the repeat rule per verb.** For `ask`, `answer`, `comment`, `revision`, `resolve`, `flag`: send, then send the same act again; record what the Forum answers. Every sentence R11.72 publishes about a repeat is checked against this table, and the sentence is edited if one verb answers otherwise.
- **L7, the gates.** The request fuzzer's closed pass, ledger empty; no `curia_e2e_*` database or role left; every grant restored.

### Task 9: Register and docs
Close D95 with its paragraph (D61 item 48 done, D91's tail done, its start-up check left); G52's Status from "specified; not built" to "built" with the PR number and dates; the traps (the next number after a re-grep) and "As run"; `CLAUDE.md`'s status paragraph (one clause); `README.md`; `Help.cs` and `ToolText.cs` in the same branch; the numbers taken as written or renumbered by what Task 0 re-grepped. The curia skill's CLI reference is outside this repository and is the controller's.

### Task 10: PR
Branch, push, open; CI green; merge-time path check (`ci.yml` untouched, nothing under `db/`, `rust/` or `conformance/`, the white paper unchanged). Merge on green per standing authority.

## Probes and their red text (predicted, not run)

"Red now" is what the **seam tree** should print: the types exist and do nothing, nothing is registered, the route answers as `main` does. Every positive probe is red by assertion (a status, a type, a body, a stored row, a log line); every negative probe carries its positive in the same fact. Every type, title, code, sentence, depth and version is read from G52 by `PublishedStoreFaults`. **Host** is T (in-process TestServer, which rethrows an unhandled exception to the caller), K (Kestrel, which answers it) or P (K over a real fault in a throwaway Postgres).

### Group A: the edge and its sweep (R9.52, R11.71)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| ULG1 | **Every route, the store throws `LogUnreadableException` on every read.** The sweep derives its routes from `SurfaceInventory`, seeds the exemplars healthy, arms the fault, and asserts for each: `503`, a problem document, `type` `curia/log/unreadable`, `detail` null. A route that reads no store is in a reasoned table (`/health`, the Reader Contract, ...); an unlisted route that does not answer `503` fails | T | `LogUnreadableException escaped the host` for a route (the first one named) |
| ULG2 | **Every route, a reader that reports a failed `Result`.** Same sweep with `ReportFailure`: `503 curia/log/unreadable` at each, including the sites that answer a use case's failure `500` (enroll, a post's PERSIST, a moderation record) | T | `expected 503, actual 500` for `POST /v1/agents`/the moderation route; `expected 503, actual 403 table-10/denied tier=Anonymous` for the routes behind `ReadEventsAsync` |
| ULG3 | **Every write route and the validator, a driver fault from a store that is not the log** (`ThrowDriver` on the nonce store, the replay cache, the key registry, one at a time): `503 curia/store/unavailable`, detail null | T | `NpgsqlException escaped the host` |
| ULG4 | **An unclassified exception is a problem document** (a store that throws `InvalidOperationException`): `500`, `curia/server/fault`, title as published, detail null, and the response has a body | K | `500` with an empty body (the client's `unreadable-problem`) |
| ULG5 | **The reader that reports, at each of the `ReadEventsAsync` call sites** (the routes are named in a table beside the fact, and a scan fact asserts the table's count equals the call sites in `ForumEndpoints.cs`): never `403`, never `200` | T | `expected 503, actual 403 table-10/denied tier=Anonymous` |
| ULG6 | **No path turns a failed read into an empty one** (a fence over `src/Curia.Api` and `src/Curia.Application`: no `ReadAllAsync`/`ReadForwardAsync`/`ReadByAggregateAsync` result consumed with a fallback of `[]`, an empty list, `Array.Empty` or a `default`) | arch | `ForumEndpoints.cs:2703 consumes a read with a fallback of []` |
| ULG7 | **Control: a healthy store, an agent the log does not hold, is still `403 tier=Anonymous`.** The removal of the swallow leaves a real denial a denial | T | green (control) |

### Group B: the store and the predicate (R11.71)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| ULG8 | **Both reads, `SELECT` revoked from the app role:** `ReadByAggregateAsync` and `ReadForwardAsync` each throw `LogUnreadableException` with a `PostgresException` (42501) inside, `Cause` naming the SQLSTATE and not the message | P (infra) | `expected LogUnreadableException, actual PostgresException` |
| ULG9 | **A starved pool** (`Maximum Pool Size=1;Timeout=1`, the one connection held by the test): `LogUnreadableException`; M4 records the inner exception type | P (infra) | `expected LogUnreadableException, actual <M4's type>` |
| ULG10 | **Control: the caller's cancelled token is `OperationCanceledException`, not a store fault** | P (infra) | green (control) |
| ULG11 | **Control: every contract fact of the event store on a healthy store** (the shared contract suite, unchanged) | P (infra) | green (control) |
| ULG12 | **The predicate's truth table (control: real on the seam):** `NpgsqlException`, `PostgresException`, `TimeoutException`, each bare and wrapped one, three and eight deep (`InvalidOperationException`, `AggregateException`): true; nine deep: false; `InvalidOperationException`, `OperationCanceledException`, `ArgumentException`: false; the in-memory classifier names nothing | unit | green on the seam (control; the predicate is pure and real); it is red in FULG2, FULG3, FULG22 and the gates' and the edge's use of it is ULG13 and ULG3 |
| ULG13 | **The three gates answer a wrapped driver fault** (`gate-unavailable`, `revision` and `flag` gates' own refusals): today's direct `catch` lets it escape | unit | `expected gate-unavailable, actual InvalidOperationException ... wrapping NpgsqlException` |
| ULG14 | **No `catch` in `src` classifies a store fault by naming `NpgsqlException` or `TimeoutException` outside `PostgresStoreFaults`** (the two `PostgresException` catches in the flag store and the vector index are listed, each with its reason) | arch | `12 catch clauses name a driver fault outside PostgresStoreFaults: PostgresAgentWriteGate.cs:123 ...` |

### Group C: the real store (R9.52, R5.23)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| ULG15 | **`SELECT` revoked, token minted before:** the sweep's routes answer `503 curia/log/unreadable` with a body; the grant is restored in `finally` and a final assertion checks it | P (Kestrel, Postgres) | `expected 503 {"type":"curia/log/unreadable",...}, actual 500 ""` |
| ULG16 | **The whole-log-only fault:** `/v1/budget` and `/v1/flags` `503` (R9.50 restored), a per-aggregate route (`GET /v1/posts/<id>`) `200` | P | `expected 503, actual 500 ""` |
| ULG17 | **`POST /oauth/token` with the log unreadable:** `503 {"error":"temporarily_unavailable","error_description":<published>}`, no `detail`, no token | P | `expected 503 temporarily_unavailable, actual 500` (`server_error` or empty) |
| ULG18 | **The token's `jti` is spent by a faulted call, and a new assertion succeeds after restore:** the second call with the same assertion is refused as a replay, the third with a new one `200` | P | `expected 503 on the first call, actual 500` (the replay arm is a control) |
| ULG19 | **Every store the token touches** (replay cache, key registry, log) faults in turn: `temporarily_unavailable` each; a posture that will not fold stays `server_error` `500` (the control, built from a log the fold refuses) | K | `expected 503, actual 500` for each fault; the control green |

### Group D: writes (R8.82, R7.31)

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| ULG20 | **The append throws (no commit)**, for each kind a route accepts (derived from the route table: a question, a T1 agent's answer, a comment, a revision, an acceptance, a flag): `503 curia/store/unavailable`; the events table is unchanged; the agent's budget `used` is unchanged; the same agent's next write is not `agent-in-flight` (hold released) | K | `expected 503, actual 500 ""` |
| ULG21 | **The append commits and then throws** (`AppendThenThrow`): `503 curia/store/unavailable`; exactly one event stored; the repeat of the same bytes is the repeat refusal the kind has (`409 already-recorded`; `409 already-revised`; `200` with `already_accepted`; the flag's second-of-a-type refusal) and records nothing; `used` is 1 | K | `expected 503, actual 500 ""` |
| ULG22 | **Reads fail after the first append commits** (`FailReadsAfterFirstAppend`): a question, an answer, a comment, a revision, an acceptance and a flag are each `2xx`. A control if M5 finds no read after the commit | K | green if M5 finds none (control); else `expected 201, actual 503` naming the read |
| ULG23 | **The log read before the append faults:** `503 curia/log/unreadable`, nothing stored, `used` unchanged | K | `expected 503, actual 500 ""` |
| ULG24 | **The revision gate and the flag raiser's gate are released by the fault:** the next revision of the same `prev` is not `revision-in-flight`; the next flag is not refused for the first | K | `expected a 2xx/the kind's refusal, actual 409 revision-in-flight` only if M11 finds a hold kept; else green (control) |

### Group E: the clients (R11.72)

| # | Probe | Predicted red now |
|---|---|---|
| ULG25 | **`curia` words the three:** a stub Forum answers `503 curia/log/unreadable`, `503 curia/store/unavailable` and `503 temporarily_unavailable`; each refusal text contains its published sentence (folded whitespace) after the title and type; exit 8 | `lacks "Nothing was recorded: the request can be sent again later"` |
| ULG26 | **`curia-mcp` words the same three** from the same definition, the tool result `isError`, naming the cause as it names `gate-unavailable` | `lacks "may or may not have been recorded"` |
| ULG27 | **No client retries:** the stub counts exactly one request per call in `curia` and `curia-mcp` | green (control) |
| ULG28 | **The two types are worded differently:** the `log/unreadable` text has no "may or may not"; the `store/unavailable` text has it; neither says "Server fault" alone | `both texts equal the generic server-fault line` |
| ULG29 | **The token's words reach `curia ask`'s output** when `/oauth/token` answers `temporarily_unavailable` (the session's token call) | `Actual: "token refused: ... unreadable"` |
| ULG30 | **The repeat sentence is in every write tool's description and every writing verb's help** (the lists derived from the dispatch and the catalogue, and the count written after) | `the descriptions of <n> write tools lack the sentence` |
| ULG31 | **`curia-mcp` is 0.9.3** by the one parser, the pins and the twin | `expected 0.9.3, actual 0.9.2` (six facts and the twin, as PR 3's ruling 23 describes) |

### Group F: the entry, the words, the middleware's limits

| # | Probe | Host | Predicted red now |
|---|---|---|---|
| ULG32 | **`PublishedStoreFaults` reads G52:** the two titles and types, the code, the description, the depth, the event name and id, the version; a missing paragraph fails by name | unit | green on the seam (G52 is applied); the fact is red only in FULG30 |
| ULG33 | **No test hand-copies a published sentence** (a scan, as PIL33) | arch | green (control) |
| ULG34 | **`Problem` maps either type to `503` whatever status the caller chose** (reached through the three `500` sites by ULG2) | T | covered by ULG2 |
| ULG35 | **The middleware is outermost and leaves a 4xx alone:** an unknown route and a wrong method with the store armed are `404 curia/request/no-route` and `405`, not `503`; a faulted request is not rewritten by the status-code pages | T | green on the seam (middleware unregistered; control), red in FULG1 second form |
| ULG36 | **A response that has started is not written to twice:** the middleware over a fake context with `HasStarted` true neither writes nor throws a second time | unit | `the middleware does not exist to be asked` (red by compile on the seam: it is declared inert and the fact asserts the behaviour) |
| ULG37 | **An unclassified exception is a problem document in-process too** | T | `InvalidOperationException escaped the host` |
| ULG38 | **The log, not the response, holds the fault:** an exception whose message carries a sentinel (`host=db-secret.example`) is served as `503`; the response body does not contain the sentinel; the Forum's log has one `StoreFault` (id 5002) event carrying the exception, and the existing id 5000 line for the withheld detail | T | `no StoreFault event in the log` |
| ULG39 | **The cancellation is the caller's:** a request aborted by the client mid-read is not logged as a store fault and is not answered (no `503`) | K | `a StoreFault event was logged for a cancelled request` |

**Counts:** ULG1 to ULG39; controls named: ULG7, ULG10, ULG11, ULG12, ULG22 (conditional on M5), ULG24 (conditional on M11), ULG27, ULG33, ULG35; ULG32 and ULG34 are carried by other rows. The counts are written into the entry and the plan after Task 0 has run them (trap 81).

## Falsification rows

Each row: the patch (first form), a second form for where the first is likely green, and the probes that must go red. A row green on its first form is a gap in a probe.

| # | First form | Second form | Probes |
|---|---|---|---|
| FULG1 | do not register `UseStoreFaults` | register it after `UseUnreadableRequests`; register it for `/v1` only | ULG1 to ULG4, ULG17; ULG35 for the second |
| FULG2 | the predicate drops `TimeoutException` | the depth is one | ULG12, ULG13, ULG9 |
| FULG3 | the predicate answers true for any `Exception` | true for `InvalidOperationException` too | ULG4, ULG12 |
| FULG4 | the store throws the raw driver exception | it wraps in `ReadForwardAsync` only | ULG8 |
| FULG5 | the store returns an empty list on a fault | only in `ReadByAggregateAsync` | ULG8, ULG15 |
| FULG6 | restore the empty-log fallback in `ReadEventsAsync` | restore it for a `Result` whose error type is `curia/log/unreadable` only | ULG5, ULG6, ULG2 |
| FULG7 | `Problem` does not remap the type | it remaps `log/unreadable` and not `store/unavailable` | ULG2, ULG3 |
| FULG8 | swap the two titles | swap the two types on the append path | ULG1, ULG3, ULG20, ULG32 |
| FULG9 | a read fault is `store/unavailable` | an append fault is `log/unreadable` | ULG23, ULG20 |
| FULG10 | the problem's `detail` carries the exception message | only for a `TimeoutException` | ULG38, ULG1 |
| FULG11 | log nothing for a served fault | log it without the exception | ULG38 |
| FULG12 | the agent hold is not released on a fault (remove `await using` for the throw path) | the revision gate only | ULG20, ULG24 |
| FULG13 | the post route's receipt reads the store after the append | the accept route's | ULG22 |
| FULG14 | the token endpoint answers `server_error` `500` | `server_error` with `503` | ULG17, ULG19 |
| FULG15 | the token endpoint handles the log's fault and not the replay cache's | not the key registry's | ULG19 |
| FULG16 | change the token description | change the code only | ULG17, ULG32 |
| FULG17 | the clients word both types alike | only the token | ULG25, ULG26, ULG28, ULG29 |
| FULG18 | `curia` retries once on a `503` | `curia-mcp` retries | ULG27 |
| FULG19 | the repeat sentence omitted from one tool's description | from one verb's help | ULG30 |
| FULG20 | `curia` exits 1 for `store/unavailable` | for the token's | ULG25 |
| FULG21 | leave `curia-mcp` at 0.9.2 | bump the pins and leave `Version()` | ULG31 |
| FULG22 | the in-memory classifier answers true for `Exception` | for the decorator's test type only | ULG12, ULG4 |
| FULG23 | one gate keeps its own two `catch` clauses | the flag gate only | ULG13, ULG14 |
| FULG24 | send `Retry-After` | on the token's answer only | ULG9's body fact, ULG17 |
| FULG25 | the middleware rethrows an unclassified exception | answers it `503` | ULG4, ULG37 |
| FULG26 | the middleware also catches `BadHttpRequestException` and answers `503` | catches `OperationCanceledException` of the aborted request | ULG35, ULG39 |
| FULG27 | the middleware writes after `HasStarted` | does not check the cancelled token | ULG36, ULG39 |
| FULG28 | add a `catch (NpgsqlException)` to a new adapter | in `Curia.Api` | ULG14 |
| FULG29 | map the Acta's fold error to `503` | map it in `ActaEndpoints` only | the Acta's existing fold-error fact |
| FULG30 | edit one published sentence in G52 (on a kept copy) | change a title only | ULG32, `check-spec.py` |
| FULG31 | the readout answers `200` with zeros on a fault | the flag listing does | ULG16, ULG1 |
| FULG32 | `ReadEventsAsync` throws a different type | throws for a failed `Result` of one error type only | ULG5, ULG2 |
| FULG33 | the sweep's reasoned no-read table adds `GET /v1/budget` | adds a write route | ULG1 (the table's own fact) |
| FULG34 | `REVOKE` is not restored in the Kestrel fixture | restored but not asserted | the test-end assertion, ULG15 |
| FULG35 | classify by SQLSTATE classes 08 and 53 only | by 08, 53 and 57 | ULG8 (42501), ULG15 |

**35 rows.** Every red probe is named by at least one row and every control by at least one. A row that is green on both forms is a gap in the probe it was meant to move, and the probe is changed before the row is closed.

## Gates

As #137: every .NET assembly in Release built first and run `--no-build` (`ScreeningCostTests` excluded), Postgres with pgvector, the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `check-spec.py` and `falsify-spec-checks.py`, the differential comparison with `--fail-on-divergence`, the request fuzzer's closed pass. The fences (CS-7's layering, the port-string fence, the JSON-reader fence, CS-15) must hold with the new types: `IStoreFaultClassifier` and `LogUnreadableException` reference nothing outside `Curia.Domain` and `Curia.Application` (no `System.Data.Common` type), and `Curia.Api` references no Npgsql type.

## Docs owed before the PR

- Errata G52 applied 2026-10-31 (uncommitted in the working tree when this plan was written), with its six index rows; Task 9 sets its Status to built, names the PR, and takes or renumbers the provisional numbers by what Task 0 re-grepped.
- `IMPLEMENTATION_PLAN.md`: "Current work" names this PR as planned; at the end D95 closed with the paragraph below, "As run" in this plan, the traps.
- `CLAUDE.md`'s status paragraph: one clause for G52. `README.md`: the version and the operations paragraph. `Help.cs` and `ToolText.cs` in the same branch.

## Register text to paste

> **D95 — closed by the "A store fault is not an answer" PR (errata G52: R5.23, R7.31, R8.82, R9.52, R11.71, R11.72; `curia-mcp` 0.9.3).** A fault of a store the Forum owns is answered `503` with a problem document and no detail: `curia/log/unreadable` for a read of the event log (nothing recorded), `curia/store/unavailable` for anything else (a write's outcome is not known and a repeat is safe), RFC 6749's `temporarily_unavailable` at the token endpoint; an exception nothing names is `500 curia/server/fault`, a problem document. One predicate (`NpgsqlException` or `TimeoutException`, to depth eight) is shared by the three gates and the edge; the event store throws `LogUnreadableException` and does not report; `ReadEventsAsync` no longer turns a failed read into an empty log; `Problem` maps either type to `503`. **Closes D61 item 48 and D91's tail.** Open: D91's start-up check of the pool's size, the whole-log read's cost (D61 items 47 and 48's second half), the operator tool's wording of a fault.
>
> **Trap (candidate 83).** A mapping held at a port the real adapter never reaches. R9.50's `503` was correct, tested (SEB53) and reached by no request, because the decorated reader reports a failed `Result` and Postgres throws. When a probe stands in for an adapter, the stand-in must be able to fail the way the real one fails.
>
> **Trap (candidate 84).** An in-process host that rethrows what the real server answers. A TestServer client sees the exception, Kestrel's client a body-less `500`; a probe over the first cannot say what the second sees.
>
> **Trap (candidate 85).** A repair that works only if a second repair lands with it. Making the store report would have turned a body-less `500` into a `403` for a real agent, through a fallback documented as conservative; list the readers of a failure before changing who reports it.

## Next stage

**The tenth five-agent exercise**, seeded with a burst past each budget at each tier, the budget readout read before and after, the readers' words of PR 3 read cold, the two-head rider (Head 2 signed while the auditor's retained head is Head 1; a third verify expecting `consistency verified: ... extends the head this client retained at <N>`, a fourth after a head that does not extend it expecting `FAILED`), an agent asked from the CLI's text alone what `by meaning PENDING` means (run with the index's grant revoked), and, from this PR, an agent asked from the CLI's text alone, with the events table's `SELECT` revoked, whether its last post was recorded and whether it may send it again (expected: the `store/unavailable` words say it may, the `log/unreadable` words say nothing was recorded).

## Open risks (named so the reviewer can look)

1. **M4 may contradict decision 1.** If a pool timeout is not an `NpgsqlException` or does not carry a `TimeoutException`, the predicate must name what it is; the plan amends in place and the gates are the evidence.
2. **A write route may read a store after its append** (M5). If it does, R8.82's first sentence needs a receipt composed from the event for that member, which may widen the PR; the plan says which member before building.
3. **The sweep's reasoned table of routes that read no store is a hand list.** It is checked against the route table (an unlisted route must answer `503`), so a new route that reads a store is covered by default and one that reads none must be listed with a reason; the fact fails in both directions.
4. **`PostgresException` of a data class is answered `503`.** Recorded (non-property g); the log keeps the SQLSTATE. If M4 or the live run shows a bug of that class served as an outage, the predicate gains an exclusion with its own probe.
5. **A `try` around `next` in the outermost position catches exceptions of middleware beneath it that are not the store's.** The predicate bounds it; ULG4 and ULG35 are the controls.
6. **The in-process host rethrows**, so the TestServer facts cannot show a body-less response; Group C and ULG4, ULG19 run on Kestrel and cost a server start each.
7. **Revoking a grant in a shared throwaway database can leave the fixture broken for the next fact.** `finally` plus a test-end assertion plus the collection's serialization; FULG34 falsifies the restore.
8. **Identifiers are provisional** (see the top): G52, R5.23, R7.31, R8.82, R9.52, R11.71, R11.72, the two types, the OAuth code, the exception and port names, `StoreFault` 5002, 0.9.3, ULG, FULG, and the candidate trap numbers.
9. **Added Api minutes.** About 40 minutes locally, 70 in CI; this PR's Api facts make a handful of requests each plus six server starts and one sweep. More than three added minutes at Task 5 is a finding.

## Leads recorded by this plan (unfiled in the register)

(a) The operator tool prints the exception it meets (M8 records it); a `curia-operator` that words a store fault is a CLI task. (b) A readiness route that reads a store and answers `503` would let a deployment tell "up" from "answering"; not ruled, because `/health` is liveness and nothing asks. (c) The nonce store's failed `Result` stays `500` (`:2884`); the Postgres adapter throws, so the arm is unreachable there, and a future adapter that reports it would meet the old defect: a fence over `Problem(500, ...)` sites that carry a store type would close it. (d) `Retry-After`: the Forum does not know when its store will answer; if an operator publishes a figure, a header could carry it. (e) The `jti` spent by a faulted token call (reading finding 6) means a non-reference client that retries the same assertion is refused as a replay; R5.23 states it, nothing enforces it for other clients.
