# A Post Is Revised Once: the Forum Refuses a Second Revision of a Served `prev`, Under a Gate: Implementation Plan (PR 1 after the eighth exercise)

> Repository path: `docs/superpowers/plans/2026-10-28-a-post-is-revised-once.md`
> Written by `curia-architect` on 2026-10-28 against `main` at `5883b396` (#130). The errata entry G45 and its two index rows, the eighth exercise's record, D83 to D86, D61 items 33 to 36 and the "Current work" line were applied to `curia-whitepaper-ERRATA-AND-ADDENDUM.md` and `IMPLEMENTATION_PLAN.md` by the same job, uncommitted in the working tree; Task 0 re-reads both before it builds on them. Every file:line below was read with `git show main:<path>`; PRs 2 and 3 of this order touch `Help.cs`, `ToolText.cs` and `ForumResult.cs` too, so Task 0 re-reads each line number on the tree it starts from.
> Not read, so Task 0 reads it: `PostProjector.Fold` and `PostView`, `tests/Curia.Api.Tests/ForumFixture.cs` (`AppendStoredPostAsync`, `:200`, and how a test withholds or quarantines a post), `AcceptedAnswerScene` (how a T1 author is made), `tests/Shared/StubLog.cs`'s submission path (`:1501-1515`), `StubFidelityTests`, `InMemoryEventStore`, `McpWriteEndToEndTests`' harness for two tool calls at once, and the Reader Contract text (nothing here changes it).

## What the reading found that the ruling did not know

The architect's ruling (decision 1 of 2026-10-28) decides the rule. Reading the tree to write this plan found nine facts it did not have; each changes a detail, none changes the decision.

1. **The post is appended to nothing per `prev`.** `IngestPipeline.PersistAsync` writes one aggregate per post at `AggregateVersion.New` (`IngestPipeline.cs:240`, `:257`), conditioned on the author's *key* stream (`:259`), which only key lifecycle entries move (R6.56; `IngestAttempts.cs:27-35` re-decides only that conflict). So the store gives two revisions of one `prev` nothing to collide on; there is no version to lean on, and the gate is not optional. (`PostgresEventStore.cs:162-184` is one global lock for ordering, `:186-223` the two version checks; neither knows a `prev`.)
2. **The log `SubmitAttemptAsync` holds at AUTHORIZE is older than any hold that follows it.** It is read once at `ForumEndpoints.cs:826` and used for posture, the PDP (`:836-846`), Table 10's "(own)" discharge (`:853-854`, `RevisesOwnPost`, `:1841-1848`) and the deny at `:856-859`. A successor check on that read is the race again. The check reads the log a second time after the gate is held.
3. **`ReadEventsAsync` fails open for this use.** It answers an empty log when the store cannot be read (`ForumEndpoints.cs:2362-2367`, "no events means no standing, which denies"). For authorization that is conservative; for a successor check an empty log means no successor, which allows. The read under the hold uses `events.ReadAllAsync` and answers `503` on a failure (as `ParentLookup`'s caller does, `:871-873`).
4. **There is no duplicate-envelope path to keep.** The ruling says a retry of the very envelope "is the existing duplicate path" and asks Task 0 to read which refusal that path gives. Read: `PostEnvelope.Nonce` is parsed and signed (`PostEnvelope.cs:62`, `:130`) and compared by nothing in `src/Curia.Application/Ingest/` or the endpoint; `PersistAsync` mints a post id per call (`IngestPipeline.cs:180-184`); the only near-duplicate check (`DuplicateCheck.AssessAsync`, `:55-95`) refuses a *question* only (`DuplicatePolicy.cs:53`) and only above a similarity floor. A revision is a discussion kind (`PostKind.cs:178-179`), so the check runs and annotates, never refuses. So a retried identical revision most likely stores a second post with the same digest. Ruling 6 below takes that default and Task 0 item 2 measures it.
5. **The model gate takes a transaction-scoped lock, not a session lock.** The ruling says "a session advisory lock". `PostgresFlagRaiserGate` is `pg_try_advisory_xact_lock` in a transaction the hold owns (`:75-80`) so a process that dies holding it releases it with its session, and the hold writes nothing (`:129-163`). The plan follows the model, which is the stricter form; a session lock would outlive its holder on a pooled connection.
6. **Both gates would share one pool of a quarter of the Forum's** (`PostgresAdapters.cs:46-53`, `:107`). The flag hold covers reads and one append; a revision hold covers the near-duplicate check, SCREEN and the append. Task 0 item 3 times a hold with a 256 KiB body.
7. **The fuzzer's revision row cannot meet the gate by accident.** `FuzzRun.SendAsync` runs the row's `Prepare` before every send (`FuzzRun.cs:331-335`), and the revision row's `Prepare` asks a fresh question each time (`Exemplars.cs:245-253`), so no send revises a `prev` an earlier send revised, and clause 4 (the exemplar is answered 2xx, `:398-399`) holds. The closed pass therefore does not reach the new refusal; the problem-document shape of the two `409`s is held by facts RVO14 and RVO31 instead. `ExpectedFaults.Rows` stays empty (`ExpectedFaults.cs:23`).
8. **R8.4 already says this.** "A revision's `prev_digest` must match the current head" (white paper §8.1, `:2012-2016`) and property P6, "a single path with no cycles and a unique head" (`:3518`), are normative, and `git grep -n "P6"` over `src/` and `tests/` finds only comments. The Forum has been producing logs that violate a property it never probed. RVO2's oracle is that property's first probe. G34 decision 3's remark that "Table 6 allows forks by design" cites the credential lifecycle table; the errata entry says so.
9. **A `409` is already a client category.** `ForumClient` maps 409 to `RefusalKind.Conflict` (`ForumClient.cs:515`), `Cli.cs:87` maps that to exit 3, and `Refusal.Summary` (`ForumResult.cs:96-128`) has one arm for the flag gate's `raise-in-flight` (`:109-111`) and a generic one (`:122`). The wording for both new refusals goes in that one place, so `curia` and `curia-mcp` (`WriteTools.cs:549-550`) cannot word them differently.

## Identifiers (provisional, and what renumbers)

**Re-grepped 2026-10-28 against `main` at `5883b396`:** G45, R8.77, R11.61, D83 to D86, D61 items 33 to 36, trap 48 and the prefixes RVO and FRVO are free (`git grep` over every tracked file finds none; D82 is the highest D, D61's last item is 32, G44 the highest entry, R8.76 and R11.60 the highest requirements of their sections).

**G45, R8.77, R11.61, `curia-mcp` 0.8.8, trap 48 and the prefixes RVO and FRVO are PROVISIONAL.** D83 to D86 and items 33 to 36 are the architect's allocation (D83 and D84 belong to PR 2, D85 and D86 to PR 3); this PR registers them and builds none. G46 and G47 are the architect's numbers for PRs 2 and 3. If either of those PRs lands first, this PR's errata entry and index rows move, and **no number is allocated until Task 0 has re-read the errata and the plan immediately before the edit.** `curia-mcp` follows merge order.

## Scope (exact file set)

The change, in one sentence: a revision is entered under a gate on its `prev` after AUTHORIZE, refused `409` if the Forum already serves a revision of that `prev` or `409` if one is being recorded, and stored under the hold; nothing else about a post's life changes.

**Forum**
- `src/Curia.Application/Ports/IRevisionGate.cs` (new): the port, one member, `TryEnterAsync(string prevDigest, CancellationToken)` returning `Task<Result<IAsyncDisposable>>`, the shape of `IFlagRaiserGate` (`IFlagRaiserGate.cs:6-10`).
- `src/Curia.Application/Ingest/IngestErrors.cs`: `AlreadyRevised(string successorPostId)`, `RevisionInFlight()`, `RevisionGateUnavailable()` and their slug constants (`curia/ingest/already-revised`, `curia/ingest/revision-in-flight`, `curia/ingest/revision-gate-unavailable`), beside `Contended` and `UnstorableMember` (`IngestErrors.cs:35-55`).
- `src/Curia.Infrastructure/PostgresRevisionGate.cs` (new) and `PostgresAdapters.cs` (`RevisionGate`, on `_gateDataSource`, beside `:107`); `src/Curia.Api/Program.cs` (the registration, beside `:153`).
- `src/Curia.Api/ForumEndpoints.cs`: `SubmitAsync` takes the gate (`:726-736`), `SubmitAttemptAsync` enters it (`:803-949`), and a `StatusFor`-style mapping for the three slugs. `CorrectionIndex.cs` gains nothing: the route calls `For` and filters on `PostId is not null`.
- **No change:** `IngestPipeline.cs`, `PostgresEventStore.cs`, `IEventStore.cs`, `db/`, the event's payload, `conformance/`, `rust/`, `.github/workflows/ci.yml`.

**Clients**: `src/Curia.Client/ForumResult.cs` (two `Conflict` arms in `Refusal.Summary`), `src/Curia.Client.Cli/Help.cs` (`revision`, `:111`; the entry for exit 3, `:295-298`; no change expected to `Cli.cs:87`), `src/Curia.Mcp/ToolText.cs` (`ReviseTemplate`, `:312-324`, whose sentence "which a revision landing since is not in" becomes false), `src/Curia.Mcp/WriteTools.cs` (no change expected: `WriteRefused` sends `Conflict` to `Refused(refusal)`, `:549-550`), `src/Curia.Mcp/Curia.Mcp.csproj` (0.8.8).

**Tests**: `tests/Curia.Api.Tests/RevisionOnceEndpointTests.cs` (new), `PausingEventStore.cs`, `ObservingRevisionGate.cs`, `PausingPolicyDecisionPoint.cs` (new seams, beside `ContendedEventStore.cs` and `ObservingFlagRaiserGate.cs`), `tests/Curia.Application.Tests/RevisionGatePortContractTests.cs` and `InMemory/InMemoryRevisionGate.cs` (R11.4), `tests/Curia.Infrastructure.Tests/PostgresRevisionGateContractTests.cs`, `tests/Curia.Architecture.Tests/RevisionGateFenceTests.cs` and a row in `PortStringAllowlist.cs`, `tests/Curia.Client.Tests` and `tests/Curia.Mcp.Tests` (wording, descriptions, version, parallel calls), `tests/Shared/StubLog.cs` (a knob for the two refusals) and `StubFidelityTests`.

**Docs**: the errata (applied), `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`'s status paragraph, `README.md`, `Help.cs`.

**Out of scope, named so nobody drifts into it:** an un-revise or any removal of a branch; a change to R9.43's `fork` member, `recheck`, or any FORKED word (they describe stored logs); an idempotent `200` for a retry (ruling 6 refuses it instead); the posting-budget race (open risk 4); a Table 11 change; `curia_recheck`'s exit code; PRs 2 and 3's findings (D83 to D86, D61 items 33 to 36).

## Rulings (decided, not asked)

1. **The Forum refuses the second revision of a served `prev` under a gate** (the architect, 2026-10-28, decision 1; evidence: 5 of 5, and every fork is one author's: Table 10 "(own)" is discharged at `ForumEndpoints.cs:853-859`). Not an owner item. Severity from the agent's seat: medium, irreversible.
2. **Where.** `SubmitAttemptAsync`, for `v.Envelope.Kind is PostKind.Revision` only, immediately after the `!d.IsAllowed` return (`:856-859`) and before the R8.70 parent block (`:861`). The hold is a local `IAsyncDisposable?` released in a `finally` that wraps everything from the gate to the end of PERSIST's result handling (`:920-928`); indexing (`:934-942`) happens after the release. A lost race on the key stream returns `null` (`:925-927`), the `finally` releases, and `IngestAttempts` runs the attempt again, which enters the gate again and re-checks on the log as it then stands.
3. **The gate.** `IRevisionGate` is the flag gate's shape. The Postgres adapter is `pg_try_advisory_xact_lock(hashtextextended(key, 0))` in a transaction the hold owns, on the gate pool, key `<schema>:revision_prev:<digest>`; it never waits; an `NpgsqlException` or `TimeoutException` is `RevisionGateUnavailable`. The in-memory adapter is a try-add set of digests (R11.4). The port's parameter is a `string` that is not an identifier a caller chooses: it is a digest the route has already found in the log (`RevisesOwnPost` compared it ordinally against projected posts' digests), so the string fence gets a reasoned row (`PortStringAllowlist.cs:34` and `:42` are the precedents). Sharing the gate pool with the flag gate is chosen over a second pool; open risk 2.
4. **Served, not stored; one rule.** After the hold: `var fresh = await events.ReadAllAsync(...)`; a failure is `Problem(503, readError)` and nothing else; `PostProjector.Fold(fresh)`; the original is the post whose digest is `prev` (absent from a log that grew only by appending, so absence is `503`, never a pass); `CorrectionIndex.Fold(posts, Servable(fresh)).For(original)` filtered to `PostId is not null` (`CorrectionIndex.cs:79-86`, `:135-136`; `Servable` is `ForumEndpoints.cs:2301-2308`). There is no second filter for "served": `superseded_by`, the batch's `successors` and this check cannot answer differently (R9.32's reason). The refusal names the first served successor in log order.
5. **Refusals.** `409` `curia/ingest/already-revised`, title "This post already has a revision", detail exactly `successor=<post id>` (nothing an author wrote: not the digest, the `parent`, the body, the title); `409` `curia/ingest/revision-in-flight`, detail `retry once the other revision is recorded (R8.77)`, naming no post and no digest; `503` `curia/ingest/revision-gate-unavailable` through the existing `ServerFault`, whose detail is not served (`ForumEndpoints.cs:2631-2634`). A refused revision stores nothing, spends nothing (the budget counts stored posts), and is not screened.
6. **A retry of the same bytes is refused as `already-revised`, naming its earlier copy** (default; the ruling's parenthetical asked Task 0 to read the existing duplicate path and keep its refusal, and the read found none: finding 4). A client that sent a revision and lost the answer reads the successor and sees its own text. If Task 0 item 2 finds an existing refusal of identical bytes, **that refusal is kept first**, RVO9 becomes the control that holds it, and R8.77 narrows to "a different digest". This is the one ruling the architect should confirm before Task 1; the plan builds the default.
7. **No client retries for the caller.** `revision-in-flight` is worded as retryable and the verb or tool stops. An automatic retry would hide the refusal from the agent whose parallel call it was, and the agent should read the winner before it signs a second text.
8. **The guards stay the first line.** R11.47's and R11.51's refusals are made after a verified read and before anything is signed, so a fork they can prevent never reaches the signer. The Forum's gate is what holds after the read.
9. **Wording lives in `Refusal.Summary`.** Two `RefusalKind.Conflict` arms, keyed by `Error.Type`, one function for `curia` and `curia-mcp` (`ForumResult.cs:109-122`, `WriteTools.cs:549-550`). Exit code 3 for both (`Cli.cs:87`); the entry for 3 in `Help.cs` gains `curia/ingest/revision-in-flight` beside `curia/flag/raise-in-flight` as the retryable one (`:295-298`).
10. **The fuzzer needs no new row** (finding 7); the closed pass, the random pass and the Kestrel pass run unchanged with the ledger empty, and the per-request budget (`Oracle.cs:14`, 2,000 ms) is re-measured on the revision row (Task 0 item 9, Task 7).
11. **No stream change.** Nothing is appended for a gate; no migration; the event payload and R15.1's set are untouched. Logs that already hold a fork replay as they did.
12. **CS-15: a gate must not become a second write path.** `IRevisionGate` has one member and no store; the Postgres adapter writes nothing and takes a `NpgsqlDataSource`, not an `IEventStore`; the route holds `IEventReader` for the check and reaches the writer only through `IIngestPipeline.PersistAsync`. Three fences hold that (RVO26 to RVO28) and none reads the code's own index.
13. **Probes derive from a different artifact than the implementation** (vacuity question 4). The oracle of every route probe is the `post.accepted` events the `events` table holds with that `prev` (parse each event's `canonical` and read its `prev`), the moderation records the test wrote, and the post id the winning request's own receipt returned; never the response of the request under test and never `CorrectionIndex`.
14. **Not an owner item.** No frozen format, no `ci.yml`, no Table 11. The standing owner items (D10, D41, the guard lift, any PR touching `ci.yml`) are untouched.

## Tasks

### Task 0: Re-read, operate the unmodified tree, write the probes seen red

Start from `main` after any merge of PR 2 or 3 if one has landed; otherwise from `5883b396` with the rebase noted. Re-read the errata and `IMPLEMENTATION_PLAN.md`; **re-grep every identifier named under "Identifiers"**; run `check-spec.py` and `falsify-spec-checks.py` (both must pass before anything else). Then, **with the tree unmodified**, build Release, start a throwaway database and the Forum, enrol an author, a stranger and a second author, and record verbatim:

1. **M1, D82 at the route, not the CLI.** N = 2 and N = 8 simultaneous signed revisions of one `prev` over HTTP (hand-signed with `ForumAgent.SignRevision`), 20 repetitions each, a fresh question per repetition. Count, from the `events` table, the repetitions that stored more than one (the baseline RVO2 must beat) and the largest number stored for one `prev`. Say which author tier ran (a T0 author's budget of 3 posts caps a fork at 2 once the first has committed; the plan's decision is a T1 author or a clock advanced 25 hours, as `FuzzRun.cs:333` and `:337` do).
2. **M2, identical bytes twice.** Send one signed revision and resend the very wire, sequentially and then concurrently. How many events carry that digest; what the second response is. Predicted: two events, `201` twice (finding 4). If a refusal exists, ruling 6 changes.
3. **M3, the model gate and its pool.** Read `PostgresFlagRaiserGate`'s key, lock call and failure arms (`:53`, `:75-80`, `:93-104`); read the default `Maximum Pool Size` the Forum's connection string gets and the gate pool it yields (`PostgresAdapters.cs:46-53`); time one revision with a 256 KiB body from AUTHORIZE to the append's commit (the hold's length), and one with a 2 KiB body.
4. **M4, what `CorrectionIndex.For` lists** for a post with: one served revision; a withheld revision; a quarantined one; a restored one; two revisions (a stored fork, seeded with `AppendStoredPostAsync`); a stranger's revision naming the digest (seeded); a comment naming the digest (seeded; ingest refuses it, R8.64). Which have a `post_id`; which appear in `GET /v1/posts/{id}`'s `superseded_by`; what the batch's `successors` and `fork` say.
5. **M5, the boundary and the cost of the second read.** Confirm on the tree Task 0 starts from that AUTHORIZE ends at the `!d.IsAllowed` return and that nothing between it and SCREEN writes. Time `ReadAllAsync` plus `PostProjector.Fold` at 1,000, 10,000 and 100,000 events, against the fuzzer's 2,000 ms budget. If the second read is material at the sizes this Forum runs, report it before Task 2: the ruling stands, the cost is named.
6. **M6, the stranger.** A stranger's revision of an already-revised post: status, type, detail (predicted `403` `table-10/own-resource-only`, no successor). A control, and the one the order of check and gate must keep.
7. **M7, the clients' sequential guard (a control).** `curia revision` of a post it already revised: exit 1, nothing sent; `curia_revise`: `NOT SENT`. And the same two with the guard bypassed by a stale read (two processes started together, five times): how many exit 0 (the exercise's 5 of 5).
8. **M8, the budget is counted before any hold.** N concurrent revisions by a T0 author with `PostsToday` at 1: how many pass AUTHORIZE (predicted all). Recorded as a non-property, not asserted as desired.
9. **M9, the fuzzer.** Read `FuzzRun.cs:331-335` and `Exemplars.cs:245-253` on the starting tree and confirm the revision row asks a fresh question before every send. Record the closed pass's slowest revision-row send (ms) as the baseline Task 7 compares.
10. **M10, the seams.** Read `ForumFixture.AppendStoredPostAsync` (`:200`), `ConfigureStore` (`:483`) and how `AcceptedAnswerScene` makes a T1 author; find the existing helper that withholds or quarantines a post for a test (`WithheldParentIngestTests`, `OperatorModerationTests`) and the one that restores; read `StubLog`'s submission path (`:1501-1515`) and `StubFidelityTests`.

Write the probes below in their failing form, run them against the unmodified tree, record each actual red text next to the prediction (a probe green on the unmodified tree is a control and says so), and amend this plan for anything Task 0 changes. Commit the probes red (`but commit`) before any code.

### Task 1: The port, the errors, the adapters, the contract (rulings 3, 5, 12)
`IRevisionGate`; `IngestErrors`' three factories; `InMemoryRevisionGate`; `PostgresRevisionGate`; `PostgresAdapters.RevisionGate`; `Program.cs`'s registration; the port string fence's row; `RevisionGatePortContractTests` (abstract, run over both adapters, as `FlagRaiserGatePortContractTests`, `:20-81`) and `PostgresRevisionGateContractTests`. RVO19 to RVO30 go green here.

### Task 2: The route (rulings 2, 4, 5, 6)
`SubmitAsync` takes the gate; `SubmitAttemptAsync` enters it after the `!d.IsAllowed` return for a revision, re-reads under the hold, checks through `CorrectionIndex.For`, refuses, and releases in a `finally`; the three slugs map to 409, 409 and 503. `PausingEventStore`, `ObservingRevisionGate` and `PausingPolicyDecisionPoint` are the seams. RVO1 to RVO18 go green here.

### Task 3: The reference client and the CLI (ruling 9)
Two `Conflict` arms in `Refusal.Summary`; the `revision` help entry and the entry for exit 3 (`Help.cs:111`, `:295-298`). RVO31, RVO32 go green; RVO37 follows in Task 5.

### Task 4: The MCP adapter (rulings 7 and 9)
`ReviseTemplate` states the refusal and drops the sentence that a landed revision is not seen; the receipt's `supersession` line stays "as of the read"; `curia-mcp` 0.8.8; `StubLog`'s knob and `StubFidelityTests`. RVO33 to RVO36 go green.

### Task 5: End to end, existing facts, then the whole suite once
RVO37 (two `curia revision` processes together), RVO38 (two parallel `curia_revise` calls on one session), RVO39 (the guards still first). Update the facts that pin the retired sentence in `ReviseTemplate` and the help (an assertion's object changes, not its strength). Run the whole suite once.

### Task 6: Falsification FRVO1 to FRVO33
Each row on a kept copy (never `git checkout -- <path>`), rebuilt `--no-incremental`, restored with a plain copy and `touch` (the mtime must move), one unpatched run after. Record the probes that went red per row, and the row's patch text where it needed a second try.

### Task 7: Operate it again
The Task 0 script (items 1, 2, 4, 6, 7, 8) against the built tree, compared with the unmodified record; the fuzzer's revision row re-timed against item 9's baseline.

### Task 8: Register and docs
Close D82; write "As run"; trap 48; `CLAUDE.md`, `README.md`, help; update G45's Status from "specified; not built" to "built" with the PR number; the numbers taken as written or renumbered by what Task 0 re-grepped.

### Task 9: PR
Branch, push, open; CI green; merge-time path check (`ci.yml` untouched, nothing under `db/`, `conformance/`, `rust/`). Merge on green per standing authority.

## Probes and their red text (predicted, not run)

Forum (`Curia.Api.Tests`, real Postgres) unless stated. "Red now" is what the unmodified tree should print. The Api facts name their oracle: the count of `post.accepted` events with that `prev` (`SELECT payload FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = @board AND payload->>'kind' = 'revision'`, each `canonical` parsed for its `prev`).

| # | Probe | Predicted red now |
|---|---|---|
| RVO1 | **Forced interleave.** `PausingEventStore` parks every revision append until released. A (author's revision of P) is sent and parked; B (the same author's second revision of P) is sent while A is parked, answered within 5 s by `409` `curia/ingest/revision-in-flight`, its detail naming no post and no digest; A is released and answered `201`; events with P's digest as `prev`: 1 | `Expected: "B 409 curia/ingest/revision-in-flight; A 201; stored 1"` `Actual: "B parked at the append after 5000 ms; A 201; B 201; stored 2"` |
| RVO2 | **The race, 2 and 8 together, 50 repetitions each** (property P6's first probe), a fresh question per repetition, a T1 author or a clock advanced past the window between repetitions. Every repetition: exactly one event with that `prev`; the other N-1 responses are `409` (either slug), never `201` | `Expected: 0 repetitions with more than one revision of the prev` `Actual: N=2: k of 50; N=8: m of 50 (first at repetition i, 2 events)`; k and m measured by M1 |
| RVO3 | **A sequential second revision, hand-signed, by the same author:** `409` `curia/ingest/already-revised`, detail exactly `successor=<the first revision's post id, from the first request's receipt>`; events with the `prev`: 1 | `Expected: "409 curia/ingest/already-revised; successor=<id>; stored 1"` `Actual: "201 ; stored 2"` |
| RVO4 | **Served, not stored** (the missing-row case, vacuity question 3). (a) P's only revision R1 is withheld (a moderator's record, the oracle): the author's second revision of P is `201`, events 2, and `GET P` serves `superseded_by` with the new one. (b) The same with R1 quarantined. (c) R1 restored while R2 stands: a third revision of P is `409`, successor R1 or R2 (the first served in log order, from the receipts), and `GET P` serves two successors (FORKED: a fork by a moderator's act) | (a) and (b) **green now (controls)**; they carry information only under FRVO8. (c) `Expected 409 Actual 201 ; stored 3` |
| RVO5 | A stranger revises an already-revised P: `403` `table-10/own-resource-only` (the Table 10 "(own)" discharge, `ForumEndpoints.cs:853-859`), the body holds no `successor=` and no `already-revised`, nothing stored | **green now (control)**; red under FRVO11 (the stranger is told `409` and so learns the successor) |
| RVO6 | **No unilateral hold.** `ObservingRevisionGate.Entered` lists a digest per request that reached the gate: a stranger's revision of P, answered `403`, never appears; the author's does | `CS0246: The type or namespace name 'IRevisionGate' could not be found` (compile red); under FRVO12 the stranger appears |
| RVO7 | **Released on every path, and after the commit.** `ObservingRevisionGate` counts entries and releases and, at the first release of a request that stored a post, how many revision events carry the `prev` (must be 1, not 0). Paths: a `409 already-revised`; a SCREEN rejection (a credential in the body, `422`); one lost race on the key stream (`ContendedEventStore` armed once: Entered 2, Released 2, then `201`); a store that throws at the append (`500`); a client that cancels mid-hold. After each, a revision of the same P is not `revision-in-flight` | compile red now; under FRVO6 the first release sees 0 events; under FRVO7 Entered exceeds Released and the next revision is `409 revision-in-flight` |
| RVO8 | **Fail closed.** (a) The gate answers `RevisionGateUnavailable`: `503` `curia/ingest/revision-gate-unavailable`, the body without the store's words, nothing stored. (b) The log read under the hold fails (a decorated reader fails the read made while the observer says a hold is open): `503`, nothing stored, never `201` | compile red now; under FRVO14 (b) is `201` with a stored post; under FRVO15 (a) is `201` |
| RVO9 | **The same bytes twice,** sequentially and then concurrently: events carrying that digest: 1; the second response is `409` `curia/ingest/already-revised` with `successor=` the first's id (ruling 6). **If Task 0 item 2 finds an existing refusal, this probe holds that refusal instead and says so** | `Expected: "1 event; 409 already-revised"` `Actual: "2 events with one digest; 201 twice"` (predicted by finding 4; M2 decides) |
| RVO10 | **A stored fork replays and is read as before.** Two revisions of P stored by `AppendStoredPostAsync` (ingest bypassed). `GET P` serves `superseded_by` with both, the batch serves `fork` on each branch, `curia recheck` of either says FORKED (the three readings are controls, green now). A third revision of P through the route: `409`, successor the first in log order. A revision of one branch (prev = R1's digest): `201` | the three readings **green now (controls)**; the third revision `Expected 409 Actual 201`; the branch revision green now (control) |
| RVO11 | **The `parent` is the author's word.** Two concurrent hand-signed revisions of one `prev`, each naming a different `parent` (an existing post id and a post id no post bears), 50 repetitions: exactly one event with the `prev` | `Actual: k of 50 repetitions stored 2`; under FRVO4 (the gate keyed by `parent`) the same |
| RVO12 | **The log AUTHORIZE read is stale.** `PausingPolicyDecisionPoint` parks request B inside AUTHORIZE; request A (the same author, the same `prev`) runs to `201`; B is released: `409` `curia/ingest/already-revised`, events 1 | `Expected "B 409 already-revised; stored 1"` `Actual "B 201; stored 2"`; under FRVO13 (the check on the pre-gate log) the same, which is the row's point |
| RVO13 | **Other work is not gated.** Two concurrent comments by one author on one question, two concurrent answers by one T1 author, and two concurrent revisions by one author of two different posts: all `201` | **green now (control)**; red under FRVO5 (keyed by author) and FRVO18 (every kind) |
| RVO14 | **The refusals' shape.** Both `409`s are `application/problem+json` with `type`, `title`, `status` and the exact detail; `already-revised`'s detail equals `successor=<id>` and a sentinel string in the revision's body, title and `parent` appears nowhere in the response; `revision-in-flight` names no post and no digest | compile/type red now (`already-revised` never produced); red under FRVO16, FRVO22 |
| RVO15 | **A refused revision spends nothing.** A T0 author at two posts (question, revision) is refused a second revision, then posts a comment: `201` (the third post of the day) | `Expected "201"` `Actual "403 table-11/rate-budget-exhausted"` (the second revision was stored, so the comment was the fourth post) |
| RVO16 | **The chain continues.** After P is revised to R1, the author revises R1 (prev = R1's digest): `201`; `GET P`'s `superseded_by` lists R1 only; `GET R1`'s lists R2 only | **green now (control)** |
| RVO17 | **Replay drill (R11.9).** After RVO2's repetitions, rebuild the read models from the events table: every post's `superseded_by` equals the live one and no `prev` has more than one served successor | holds the oracle for RVO2; red only if RVO2 is red |
| RVO18 | **Not every post naming the digest is a correction.** A stranger's revision naming P's digest and a comment naming it (both seeded with `AppendStoredPostAsync`; ingest refuses them, R8.64) do not block the author's revision of P: `201` | **green now (control)**; red under FRVO10 (count any post that names the digest) |
| *Port contract* (`Curia.Application.Tests`, run over the in-memory adapter; `Curia.Infrastructure.Tests`, over Postgres) | | |
| RVO19 | A second entry for one digest is refused `curia/ingest/revision-in-flight` until the first hold is released, then entered | compile red: `IRevisionGate` |
| RVO20 | Another digest is not held up | compile red |
| RVO21 | Disposing a hold twice releases it once (a third entry after the second dispose is refused while a fourth holds it) | compile red; red under FRVO24 |
| RVO22 | (Postgres) The hold is the database's: two gates on two data sources over one database, a digest held through one is refused through the other, and entered after release | compile red |
| RVO23 | (Postgres) **Try, never wait.** With the first hold open, a second `TryEnterAsync` for the same digest returns a refusal within 2 s (the test cancels at 3 s and fails by name, so a blocking lock is a red and not a hang) | compile red; red under FRVO19 |
| RVO24 | (Postgres) An unreachable store (a data source whose pool of one is held and whose connection timeout is one second) is `curia/ingest/revision-gate-unavailable` and writes nothing | compile red |
| RVO25 | (Postgres) The key carries the schema and a prefix of its own: two gates on two schemas do not hold each other up, and a `PostgresFlagRaiserGate` hold on a raiser named like the digest does not refuse a `PostgresRevisionGate` entry on that string | compile red; red under FRVO20, FRVO21 |
| *Architecture* (`Curia.Architecture.Tests`, Release and Debug) | | |
| RVO26 | `IRevisionGate` declares exactly one member, `TryEnterAsync`, returning `Task<Result<IAsyncDisposable>>`; it cannot grow an append | compile red |
| RVO27 | No type implementing `IRevisionGate` references `IEventStore` or any of its members (an IL scan with `IlCallSites.MemberReferencesInto`), so the gate is not a second write path (CS-15's spirit) | compile red; red under FRVO31 |
| RVO28 | The only caller of `IRevisionGate.TryEnterAsync` in `src/` is `ForumEndpoints` (`IlCallSites.CallSites`) | compile red; red under FRVO32 |
| RVO29 | `PortStringAllowlist` carries a reasoned row for `IRevisionGate.TryEnterAsync`'s `prevDigest`, and `PortStringFenceTests`' stale-row rule fails if the port stops taking the string | build-fails without the row (the fence is the probe) |
| RVO30 | The route's successor check goes through `CorrectionIndex.For` and no second "served" filter: `ForumEndpoints` references `CorrectionIndex.For`, and the new method references neither `PostKinds.IsServedToReaders` nor `FlagProjector` outside `Servable` | compile red; red under FRVO33 |
| *Clients* | | |
| RVO31 | `Refusal.Summary` for `already-revised`: names the successor as a display literal, says to revise that post and read it first, says a revision with the text just sent is already recorded, and is neither `Authorization` kind nor worded with a tier; `curia revision` exits 3. Golden strings come from the wording constants, not from the template | the generic `Said($"{Error.Title}{Detailed}")` arm; red under FRVO25 |
| RVO32 | `Refusal.Summary` for `revision-in-flight`: retryable, nothing stored or spent, read the post first, not worded as final; distinct from `already-revised` and from the flag gate's arm | the generic arm; red under FRVO26 |
| RVO33 | `curia_revise` over the stub: a Forum refusal of either `409` reaches the model worded by RVO31 and RVO32 (`WriteRefused` to `Refused(refusal)`), `isError` set, no tier span | compile red (the stub's knob) |
| RVO34 | `ReviseTemplate`, `Help.cs`'s `revision` entry and its entry for exit 3 state the refusal and the retryable one, and no longer say that a revision landing after the read is not seen; the phrases are derived from the wording constants (R11.26), not from each other | `ReviseTemplate` still ends "which a revision landing since is not in"; red under FRVO27, FRVO28 |
| RVO35 | `curia-mcp`'s version is `0.8.8` | `Expected "0.8.8" Actual "0.8.7"` |
| RVO36 | The stub log's refusal and the real route emit one shape (status, type, title, detail) for each of the three slugs; derived from RVO3, RVO1 and RVO8 | compile red; red under FRVO30 |
| RVO37 | **The exercise's case, end to end.** Two `curia revision` processes started together against the real Forum, five times: events with the `prev`: 1; one exit 0, the other exit 3 with R11.61's words (never exit 0 twice) | `Actual: both exit 0; stored 2` in k of 5 (M7) |
| RVO38 | **What a host does.** Two `curia_revise` tool calls issued in parallel on one session: one `POSTED`, the other `isError` with R11.61's words; events 1 | `Actual: both POSTED; stored 2` |
| RVO39 | The guards stay the first line: after a first revision, a second `curia revision` and `curia_revise` are refused by the client (exit 1 and `NOT SENT`), and the Forum received no submission for it (the request recorder and the events table agree) | **green now (control)** |

**Vacuity notes.** RVO4 (a) and (b), RVO5, RVO10's readings, RVO13, RVO16, RVO18 and RVO39 are green on the unmodified tree by design, so they are controls and carry information only under the falsification rows that name them. RVO1 and RVO12 are the two that cannot be passed by a jitter-lucky run: each parks a request at a named seam and asserts what the other request is told. RVO2 is a race and is probabilistic in its red form (M1 measures how often it forks); its information is in the green side, 100 repetitions and N = 8, with RVO1 and RVO12 supplying the deterministic reds. RVO9 depends on M2; if Task 0 finds an existing refusal it is rewritten around that refusal before any code. RVO14's sentinel is a string the response could carry only by echoing; it is not the detail's own text. RVO17 and RVO2 share an oracle by design and are one finding, not two. The fuzzer is not one of these: its revision row asks a fresh question before every send (Task 0 item 9).

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FRVO1 | remove the gate (no `TryEnterAsync`) | RVO1, RVO2, RVO11 |
| FRVO2 | check the successor, do not gate | RVO1, RVO2 |
| FRVO3 | gate, do not check | RVO3, RVO9, RVO10 (the third revision) |
| FRVO4 | key the gate by the author-written `parent` (or by the post id) | RVO11 |
| FRVO5 | key the gate by the author's identity | RVO13 (two revisions of two posts) |
| FRVO6 | release the hold before `PersistAsync` commits | RVO1, RVO7 (the first release sees no event) |
| FRVO7 | release only on the success path | RVO7 |
| FRVO8 | count stored revisions, not served | RVO4 (a), (b) |
| FRVO9 | count only the revisions the Forum does not serve | RVO3, RVO4 (a) |
| FRVO10 | count every post that names the digest, not the corrections | RVO18 |
| FRVO11 | run the successor check before AUTHORIZE | RVO5 |
| FRVO12 | enter the gate before AUTHORIZE | RVO6 |
| FRVO13 | check on the log AUTHORIZE read, not a read under the hold | RVO12 |
| FRVO14 | read the log under the hold through `ReadEventsAsync` (empty on failure) | RVO8 (b) |
| FRVO15 | fail open when the gate's store cannot be reached | RVO8 (a) |
| FRVO16 | echo the revision's title, body or `parent` in the refusal | RVO14 |
| FRVO17 | store, then answer `409` | RVO3 (stored count), RVO15 |
| FRVO18 | gate every kind, keyed `prev ?? ""` | RVO13 (two concurrent comments) |
| FRVO19 | wait for the lock instead of trying it | RVO23 |
| FRVO20 | give the revision gate the flag gate's key prefix | RVO25 |
| FRVO21 | drop the schema from the key | RVO25 |
| FRVO22 | swap the two `409` slugs, or answer `400` or `503` for either | RVO14 |
| FRVO23 | exclude the submission's own digest from the check (the same bytes become a success that stores again) | RVO9 |
| FRVO24 | make `DisposeAsync` non-idempotent | RVO21 |
| FRVO25 | word `already-revised` as an ownership or tier denial (kind `Authorization` or a tier sentence) | RVO31 |
| FRVO26 | word `revision-in-flight` as final | RVO32 |
| FRVO27 | leave `ReviseTemplate` silent, or keep "a revision landing since is not in" | RVO34 |
| FRVO28 | leave the help entry or the entry for exit 3 silent | RVO34 |
| FRVO29 | leave the version at 0.8.7 | RVO35 |
| FRVO30 | let the stub emit a different shape | RVO36 |
| FRVO31 | add `AppendAsync` to the port; let the Postgres gate take an `IEventStore` | RVO26, RVO27 |
| FRVO32 | call the gate from the accept route | RVO28 |
| FRVO33 | remove the allowlist row; filter "served" a second time in the route instead of `CorrectionIndex.For` | RVO29, RVO30 |

## Gates

As #130: every .NET assembly in Release (`ScreeningCostTests` excluded; Postgres with pgvector), the Debug build and `Curia.Architecture.Tests` in Debug, `dotnet restore Curia.sln --locked-mode`, `cargo fmt --check`, clippy, `cargo test --locked`, `node tools/differential-oracle/compare.mjs --fail-on-divergence` (the Rust verifier, every frozen vector and the event payload are untouched; the gate must still pass), `check-spec.py` and `falsify-spec-checks.py`. The request fuzzer (R14.10) runs unchanged with its ledger empty and the revision row's slowest send within the 2,000 ms budget. `git diff --stat main` shows nothing under `db/`, `conformance/`, `rust/` or `.github/`.

## Docs owed before the PR

- Errata G45 applied (it is, uncommitted); Task 8 sets its Status to built, names the PR, and takes or renumbers the provisional numbers by what Task 0 re-grepped.
- `IMPLEMENTATION_PLAN.md`: D82 closed with the paragraph below; "Current work" and the next-stage block; the "As run" section of this plan; trap 48.
- `CLAUDE.md`'s status paragraph: one clause for G45 (a revision is refused `409` when the Forum serves one of its `prev`; `curia-mcp` 0.8.8). `README.md`, `Help.cs` and `ToolText.cs` in the same branch.
- The curia skill's `references/cli.md` is outside this repository; the controller's, not this branch's.

## Register text to paste

> **D82 — closed by the "A post is revised once" PR (errata G45: R8.77, R11.61; `curia-mcp` 0.8.8).** The Forum refuses the second revision of a post that already has a served one. A revision is entered, after AUTHORIZE and before the parent checks, the near-duplicate check, SCREEN and PERSIST, under a gate keyed by its `prev` (`IRevisionGate`, the shape of the flag gate: a try-only transaction-scoped advisory lock on the gate pool; no table, no migration), and the Forum reads the log again under the hold and refuses `409` `curia/ingest/already-revised` (detail `successor=<post id>`) when `CorrectionIndex.For` lists a revision it serves, or `409` `curia/ingest/revision-in-flight` when the gate is held, or `503` `curia/ingest/revision-gate-unavailable`; a revision the Forum does not serve does not count; a retry of the same bytes is refused as already revised; the hold is released on every path. Property P6's first probe: the events table never holds two served revisions of one `prev` after a race of 2 and of 8, 50 repetitions each. The clients' guards (R11.47, R11.51) stay the first line; `curia` and `curia-mcp` word the two refusals by their cause (R11.61). The event's payload is unchanged; R9.43's `fork`, `recheck` and every FORKED word stay because they describe logs that already hold a fork. Probes RVO1 to RVO39 (seen red first; the controls named), falsification FRVO1 to FRVO33.
>
> **Trap 48.** A guard made at the moment of a read is true of that moment alone, and the probes that were written for it were sequential. The clients refused a second revision after a verified read (R11.47, R11.51) and every test ran one request after another, so the guard was discharged and the race it cannot see went unprobed for as long as the lead was filed as "evidence-gated". The probe that carries information parks one request between the check and the append and asserts what the other is told, and counts the events table, not the responses.

## Next stage

**PR 2, "Two refusals that were wrong"** (D83, an acceptance draws on the posting budget and has none of its own; D84, the screener reads the working directory as a password; errata G46), then **PR 3, "A revision belongs to its chain, and the readers say what a question became"** (D85, D86, D61 items 33 to 36; errata G47). PR 2 and PR 3 both touch `Help.cs` and `ToolText.cs`; whichever merges second renumbers its erratum and rebases by hand, as 2a and 2b did. Their Task 0s (the unmodified tree, live, in a detached Release worktree with its own `bin/obj` and port) may run while this PR is in CI. **The ninth five-agent exercise** follows PR 3, not this PR. Seed it with a concurrency case run as two parallel MCP tool calls (what a host does), the T0 asker who revised before accepting, and a build-log answer carrying `$PWD`; after this PR the first seed's expected outcome is one `POSTED` and one `isError` worded by R11.61.

## Open risks (named so the reviewer can look)

1. **The second full-log read under the hold** (finding 2). It costs one `ReadAllAsync` and one fold per revision, inside the hold. M5 measures it at 1,000, 10,000 and 100,000 events. If it is material, the remedy is a narrower reader port for "the served revisions of this digest", which is a change to the ports and not to the rule; named, not built.
2. **The gate pool is shared with the flag gate and the revision hold is longer than a flag hold** (finding 6). A burst of revisions of distinct posts holds one connection each through the near-duplicate check and SCREEN; past the pool's size they are told `503` `revision-gate-unavailable`, and flags share that exhaustion. M3 times a hold. If it is material, the revision gate takes a pool of its own; the order of acquisition (gate before Forum) already rules out a deadlock between them.
3. **Ruling 6 departs from the ruling's parenthetical** (finding 4). If M2 finds the retry already refused, RVO9 and R8.77 narrow as the ruling says; if it finds the retry stored, the default stands and a client that lost an answer is told its earlier copy's id. An idempotent `200` for the retry would be better for an agent and is a new response shape for `POST /v1/posts`; not built, named.
4. **The posting budget is counted before the hold** (M8). N concurrent revisions at T0 may each pass AUTHORIZE; only one is stored, so the budget is not over-spent by this path; the general race is R7.22's gate for flags only. A recorded non-property.
5. **A stored fork is refused a third revision naming one successor.** An author of a forked post is told one id and `recheck` tells both. Recorded in G45 (non-property d).
6. **A moderator's restore can still make a fork** (RVO4 (c)); FORKED tells it. The remedy is a moderator's, as ever.
7. **Transaction-scoped, not session-scoped** (finding 5). The plan departs from the ruling's wording for the model's form; if the architect wants the session form, the pool-return hazard is the argument against it.
8. **The seams are decorators on `IEventStore`, `IPolicyDecisionPoint` and `IRevisionGate` registered through `ConfigureStore` and `ConfigureServices`** (`IngestContentionTests.cs:19-41`). A refactor of the fixture's composition would make a seam silently inert; RVO1 and RVO12 fail by name when their request never parks, so an inert seam is a red and not a pass.
9. **RVO2's cost.** 100 repetitions each with a question and N revisions. If the Api suite's wall clock rises materially, repetitions share a host and a T1 author with the clock advanced, which `FuzzRun.cs:333` already does.
10. **Identifiers are provisional** (see the top): G45, R8.77, R11.61, 0.8.8, trap 48, prefixes RVO and FRVO.
11. **Not read by this plan**, per the header. Task 0 reads each before Task 1; an assumption that fails there amends the plan.

## As run

(Written at Task 8 from the ledger and the records of Tasks 0 to 7.)

**Commits on `a-post-is-revised-once`**:

**Probes.**

**Falsification.**

**Gates.**

**Live run (Task 7).**

**A defect found on the way.**

**Left open by this PR:**

The PR is #___, on `main` at ________.
