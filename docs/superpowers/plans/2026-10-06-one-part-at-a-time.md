# One Part at a Time: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-06-one-part-at-a-time.md`

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. **Every subagent runs on Opus** (`model: "opus"`).

**Goal:** Close **D32** and **D33**, gated by **R14.10** and recorded as errata **G18**. After both PRs:
- screening is linear on every path (R10.69);
- a flag spends a budget of its own and never the posting budget, and one flag of a type per raiser per post (R7.22, R10.70; D34);
- a flag or moderation rationale is at most 4,096 UTF-8 bytes, refused before screening (R10.68);
- a fuzzer derived from the route table, varying one part at a time over a closed set, gates the request surface. It is shown adequate by reverting D25's fixes (R14.10);
- a caller's string reaches a port or a host-called use case only as a boundary type, behind a build-failing fence, and a caller's JSON is read only through a guarded reader (R11.34).

**Architecture:**
- **PR A** (branch `request-fuzzer-gate`): Tasks A0–A8, with A4b between A4 and A5.
- **PR B** (branch `boundary-types-fence`, opened from `main` after A merges, and only after GitButler's target base has been updated to the merged `main` and `but status` shows the new branch carrying none of A's commits): Tasks B1–B8.
- No frozen format moves (R15.1). No event, table or grant changes. No wire slug changes (spec §4.7).

**Tech stack:** .NET 10, C# 14, xUnit v3, CsCheck 4.8.0, System.Reflection.Metadata, Npgsql + Postgres 18 with pgvector, GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-10-06-one-part-at-a-time-design.md`. Commit the spec and this plan first on branch A, as `Spec: one part at a time` and `Plan: one part at a time — two PRs, the gate first`.

## Global Constraints

**Build and test**
- `dotnet build Curia.sln -c Release` must report 0 warnings.
- Tests run with `-c Release`. Export `CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"`, and never write the username out.
- Build `curia-testis` first (`cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis`) and export `CURIA_TESTIS_BIN`.
- Count test assemblies, never totals. **Eleven** must appear. Record each assembly's count at the base in Task A1, Step 1. Never quote counts from this plan.
- Read gate output only through `grep -E "Passed!|Failed!"`. Never `head` it.
- Long runs go to the background with output to a file, which you read when the run ends. Never `tail -f`.
- Clean build output only under `src/*/`, `tests/*/` and `tools/*/`.

**Invariants**
- R15.1's set does not move.
- No content is normalized.
- `Curia.Domain` depends on nothing new.
- Boundary types live in `Curia.Domain.Primitives`, and `Rationale` in `Curia.Domain.Moderation`.
- A refusal names a field, a fault and a byte count, never the value.
- Read-back is never parsed (spec §4.6).

**Numbering:** G18, R7.22, R10.68, R10.69, R10.70, R11.34, R14.10; register D34; ledger rows D33-1…n. Task A1 re-derives these against the errata and **stops** if the tree disagrees.

**Characters:** No added line may contain a character of category Cf, Zl, Zp, Co, or Cc other than LF and TAB. Write hostile test values as escapes: in C#, `"\\u" + "2028"` or `(char)0x2028`; in prose, `U+2028`. A7 and B7 scan for this.

**Version control:** `but` only. Never `git commit`, `checkout`, `stash`, `rebase`, `restore` or `reset`. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Pass the message with `-m "$(printf '…')"`. No usernames, IPs or home paths in any tracked file.

**Falsification discipline:** A suite is RED only on its own `Failed!` line naming the expected test or row. Restore by plain copy (`shutil.copyfile`), prove the restore with `filecmp.cmp` and `git diff --quiet`, rebuild with `--no-incremental`, and run unpatched once. No patch is a constant expression.

## Review Focus

1. The fuzzer derives routes, parts and read headers. Nothing in it lists a position by hand except `TransportHeaders` (with reasons) and the request-read query names (shared with the hand sweep).
2. Acceptance: all twenty cases in the runner's `CASES` (D25's fifteen fixes, split as listed, plus case 16) are RED, each naming its route and part, and none relies on the hand sweep.
3. The red baseline is recorded under D33 before any fix. The ledger is a ratchet and never holds a budget row.
4. The screener: every regex is NonBacktracking. The corpus differential is identical. Versions are bumped. `HighEntropyAssignment` reports the value span.
5. R10.68 is checked after authentication and authorization, and before screening and before the post is looked up, on both paths, counting UTF-8 bytes.
6. PR B: no wire slug changes, read-back is never parsed, the fence's scope is derived, and its allowlist reasons follow the spec's §4.8 classification.
7. A4b: a flag is never refused for a spent posting budget, a null flag count is a failure and never zero, the repeat is keyed on post, raiser and type, and the fuzzer's flag row is a consumed row.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `docs/superpowers/specs/…`, `docs/superpowers/plans/…` | spec and plan | A0 |
| `tests/Curia.Api.Tests/Fuzz/{Part.cs, Variation.cs, RawJson.cs, JwsBuilder.cs, RequestModel.cs, Exemplars.cs, Mutator.cs, Oracle.cs, ProblemShape.cs, ExpectedFaults.cs, HeaderReadRecorder.cs, SurfaceInventory.cs, FuzzRun.cs}` (new) | the fuzzer | A1 |
| `tests/Curia.Api.Tests/Fuzz/{RequestFuzzClosedTests.cs, RefusedQueryTests.cs, RequestFuzzRandomTests.cs, RequestFuzzKestrelTests.cs}` (new) | its gates | A1, A2 |
| `tests/Curia.Api.Tests/RequestSurfaceTests.cs` | uses `SurfaceInventory` and `ProblemShape` (moved, not changed) | A1 |
| `tests/Curia.Api.Tests/ForumFixture.cs` | unsealed; `protected virtual void ConfigureFuzz(IServiceCollection)` hook | A1 |
| `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`, `packages.lock.json` | `CsCheck` reference | A2 |
| `src/Curia.Domain/Screening/{SecretScanner.cs, InjectionDetector.cs, DerivedViews.cs}` | NonBacktracking; `HighEntropyAssignment` rewritten; versions | A3 |
| `tests/Curia.Domain.Tests/Screening/ScreeningCostTests.cs` (new), `DetectorTests.cs` | R10.69 facts | A3 |
| `tests/Curia.Architecture.Tests/RegexEngineTests.cs` (new) | R10.69's engine rule over every shipped assembly's IL | A3 |
| `src/Curia.Domain/Moderation/RationaleLimit.cs` (new), `src/Curia.Application/Moderation/{RaiseFlag.cs, ApplyModeration.cs}`, `src/Curia.Api/ForumEndpoints.cs` (status map), `src/Curia.Operator/Program.cs` (exit map, if it maps by slug) | R10.68 | A4 |
| `tests/Curia.Application.Tests/Moderation/RationaleLimitTests.cs` (new), `tests/Curia.Api.Tests/FlagEndpointTests.cs`, `OperatorModerationTests.cs` | R10.68 facts | A4 |
| `src/Curia.Domain/Authorization/{TierPolicy.cs, AccessPolicy.cs, AuthorizationErrors.cs}`, `src/Curia.Application/Moderation/RaiseFlag.cs`, `src/Curia.Api/ForumEndpoints.cs` (flag route, status map), `src/Curia.Client/ForumClient.cs` | R7.22, R10.70 | A4b |
| `tests/Curia.Domain.Tests/Authorization/{AccessPolicyTests.cs, TierPolicyTests.cs}`, `tests/Curia.Api.Tests/FlagEndpointTests.cs`, `tests/Curia.Client.Tests/RefusalClassificationTests.cs` | R7.22, R10.70 facts | A4b |
| `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md` | register, documents | A7, B7 |
| `.github/workflows/ci.yml` | upload `CURIA_FUZZ_TIMINGS` as an artifact on the .NET job; `CURIA_SCREEN_TIMINGS` likewise | A1, A3 |
| `src/Curia.Domain.Primitives/Boundary/{StorableText.cs, AgentId.cs, Kid.cs, PostId.cs, Jti.cs, DpopNonce.cs, BoardName.cs, ParentReference.cs}` (new), `src/Curia.Domain/Moderation/Rationale.cs` (new) | the types | B1 |
| `src/Curia.AuthN/**`, `src/Curia.Infrastructure/**`, `src/Curia.Application/**`, `src/Curia.Api/**`, `src/Curia.Operator/**` | retype (signatures listed in B2/B3) | B2, B3 |
| `tests/Curia.Architecture.Tests/{PortStringFenceTests.cs, PortStringAllowlist.cs, JsonReaderFenceTests.cs, IlCallSites.cs}` (new) | fence, JSON rule | B4, B5 |

---

## PR A — the gate (branch `request-fuzzer-gate`)

### Task A0: Branch, spec, plan, numbering check

- [ ] `but branch new request-fuzzer-gate`.
- [ ] Re-derive the numbers. Each command must print the value shown, or **stop and report**:
  - `grep -c '^## G18 — A gate that costs' curia-whitepaper-ERRATA-AND-ADDENDUM.md` → `1`
  - `grep -c '^\*\*R7\.22\*\*\|^\*\*R10\.68\*\*\|^\*\*R10\.69\*\*\|^\*\*R10\.70\*\*\|^\*\*R11\.34\*\*\|^\*\*R14\.10\*\*' curia-whitepaper-ERRATA-AND-ADDENDUM.md` → `6`
- [ ] `python3 tools/spec-checks/check-spec.py` → `spec-checks: clean`. `python3 tools/spec-checks/falsify-spec-checks.py` → all 4 red.
- [ ] Write the spec and this plan to their paths. Commit the two docs, plus the errata and `IMPLEMENTATION_PLAN.md` changes `curia-architect` left uncommitted, as `Spec: one part at a time`, `Plan: one part at a time — two PRs, the gate first`, and `Errata G18 and register: the gate first, the cure second`.

### Task A1: The fuzzer, run red against its base

**Interfaces (pinned):**

```csharp
namespace Curia.Api.Tests.Fuzz;

internal enum PartKind { Path, Query, Header, HeaderParameter, Form, Json, Jws }
internal enum CopyKind { Plain, ReSigned, Unsigned }

/// Address grammar: spec §4.10. Equality is ordinal on Address.
internal sealed record Part(string Address, PartKind Kind, PartValueKind ValueKind, bool Fresh, int? PublishedCap);
internal enum PartValueKind { String, Number, Boolean, Null, Array, Object }

/// One stable id per variation (spec §4.10's table); Applies(part) decides which variations a part gets.
internal sealed record Variation(string Id, Func<Part, bool> Applies, Func<Part, string?, VariedValue> Make);
/// JSON/JWS positions: the bytes written at the leaf. nul, lone-high, lone-low and C0 are the six-character JSON escape; their *-raw twins, bad-utf8 and overlong are raw bytes (spec §4.10). Percent-encoded text for path/query/form; a string for headers.
internal abstract record VariedValue
{
    internal sealed record Removed : VariedValue;
    internal sealed record RawJson(byte[] Bytes) : VariedValue;   // written verbatim at the leaf's position
    internal sealed record Encoded(string Text) : VariedValue;    // already percent-encoded
    internal sealed record HeaderText(string Text) : VariedValue;
    internal sealed record Body(byte[] Bytes, string? ContentType) : VariedValue; // body-removed, body-truncated
}

internal sealed record ExemplarRow(
    string Method, string Pattern, string Variant,
    Func<FuzzContext, CancellationToken, Task<RequestModel>> Build,   // called per send; regenerates Fresh parts
    Func<FuzzContext, CancellationToken, Task>? Prepare = null,       // consumed rows only
    bool CreatesPosts = false);

internal static class Variations { internal static IReadOnlyList<Variation> Closed { get; } }  // exactly spec §4.10
internal static class Mutator { internal static IEnumerable<(Part Part, Variation Variation, CopyKind Copy)> Plan(RequestModel exemplar); }
internal static class Oracle
{
    internal const int BudgetMilliseconds = 2_000;    // spec §4.10; Release; measured machine named in the failure message
    internal static string? Verdict(string pattern, HttpStatusCode status, string body, TimeSpan elapsed, bool warmUp);
}
internal sealed record FaultRow(string Route, string Variant, string Part, string Variation, string Copy, string Register);
internal static class ExpectedFaults { internal static readonly ImmutableArray<FaultRow> Rows = []; }
```

- `RequestModel` holds:
  - `Method`;
  - `Pattern`;
  - an ordered `RouteValues` dictionary;
  - an ordered `Query` list;
  - an ordered `Headers` list, with a header's parameters as children;
  - a `Body` that is a JSON tree, form fields, a multipart form, or none;
  - a `Jws` map `{token, proof, assertion, post}` → `(JsonObject Header, JsonObject Claims-or-null, Signer)`.
- `RequestModel.Parts()` enumerates parts by walking that model. `RequestModel.Render(Part?, VariedValue?, CopyKind)` returns an `HttpRequestMessage`.
- Recorded as built: `FuzzContext.cs`, a file the list above does not name, holds `FuzzAgent`, `Prepared` and `FuzzContext` (one seeded Forum the fuzzer sends to). A form body has a root part `form:` beside its `form:<field>` parts, as a JSON body has `json:`, so `body-removed` and `body-truncated` apply to a form too. Retype variations apply to every JSON and JWS position, the inner objects and arrays included, not only the leaves and roots.
- `JwsBuilder.Compact(byte[] header, byte[] claims, ECDsa key)` = `b64u(header) + "." + b64u(claims) + "." + b64u(ES256(ascii(b64u(header) + "." + b64u(claims))))`, with the IEEE P1363 signature format.
- The post signature is re-signed by copying the signing-input construction from `DetachedJws.Sign` (`src/Curia.Canon/Jws/DetachedJws.cs`) with the header bytes replaced. Reuse and move `RequestSurfaceTests.WithSignatureHeader` (`:647`) if it already does this. Read it first.

**Red facts first** (`RequestFuzzClosedTests`):

| Fact | Asserts |
|---|---|
| `R14_10_EveryRegisteredRouteHasAnExemplarAndEveryExemplarARoute` | `Routes(forum)` (moved from `RequestSurfaceTests.cs:1114`) equals the set of `(Method, Pattern)` over `Exemplars.All`, both ways; message lists the difference |
| `R14_10_EveryPostKindHasAnExemplar` | `PostKinds` wire values ⊆ `POST /v1/posts` variants |
| `R14_10_EveryQueryParameterARouteReadsHasAnExemplarValue` | per route, bound query parameters (as in `EveryQueryParameterAHandlerBindsIsProbed`, `RequestSurfaceTests.cs:954`) ∪ `SurfaceInventory.RequestReadQuery` restricted to the route ⊆ row's `Query` keys |
| `R14_10_EveryEnvelopeMemberAndBodyPropertyIsAPart` | the envelope parser's known-member set, element members of its arrays of domain records (`code_blocks/*/language\|source\|license`, `refs/*/kind\|value\|version`) included, and each body DTO's `JsonPropertyName` set, ⊆ the union of the exemplars' `json:` parts (spec §4.10) |
| (inside the closed-pass fact) header and query coverage | `HeaderReadRecorder`, installed on every fuzz fixture through `ConfigureFuzz`, records header reads and `IQueryFeature` reads, each by name and by the route the host matched (`RouteEndpoint.RoutePattern.RawText` at the moment of the read; `(no endpoint)` when none matched), across the **whole** closed pass. At the end: recorded header names ⊆ varied header names ∪ `TransportHeaders` (`Host`, `Content-Length`, each with reason "framing, computed by the client; Kestrel's parser"; `Transfer-Encoding` and `Connection` were listed too, and the first pass found both stale: nothing reads either through the request's headers on the test host), and every `TransportHeaders` entry was read at least once, and every `RequestReadQuery` entry `(Method, Pattern, Name)` was read at least once **by that route**. A name read only by another route does not count (review of 7bf1160: by name alone, /v1/search's reads made nine entries unfalsifiable). Each miss is a failure line beginning `coverage:`. Every query parameter a route reads (by the route the host matched) is sent by some exemplar of that route or listed in `SurfaceInventory.RefusedQuery` (R9.26's refusable list, errata G12 item 9: `/v1/search`'s `verification` and `environment_version`), every `RefusedQuery` entry was read by its route, and a query read with no endpoint matched is a failure. Header coverage is by name across the pass. Per-route header coverage is recorded as observed, not acted on |
| `R14_10_NoVariationOfAnyPartIsAServerFaultAProblemlessRefusalOrOverBudget` | runs the closed pass. Failures that are not ledger rows, plus stale ledger rows (other than `random-N` rows, which the random pass judges), plus any ledger row that matches a budget failure or names an exemplar send (spec §4.10: a clause-4 failure is an exemplar defect and never a ledger row), fail with one line each: `"{status} {Method} {Pattern} [{Variant}] {Part} {Variation} {Copy}: {reason} ({elapsed} ms)"`. Non-vacuity: spec §4.10's clause 5 (reach per part, sent + unsent + superseded = planned per row, and at least one superseded copy in the pass, the unsent ceiling), and every row's exemplar answered 2xx before and after. Every failure also goes to `CURIA_FUZZ_FAILURES` when set |
| `R14_10_AHostOverTheFuzzedLogStartsAndServesWhatItAccepted` | runs after the closed pass in the same test (one fact, two phases). The restart check (spec §4.10) |
| `R14_10_AnEnvelopeThatCannotBeCanonicalizedHasNoReSignedCopy` | offline: the `question` row's `json:/envelope/body` varied by `nul-raw` throws `NotReSignableException` when rendered re-signed, and renders unsigned (review of 7bf1160) |
| `R14_10_OnlyAVariationWithNoCanonicalFormIsSuperseded` | offline: over the seven `POST /v1/posts` rows, the variation ids whose re-signed copy throws `NotReSignableException` are exactly nul-raw, lone-high, lone-high-raw, lone-low, lone-low-raw, bad-utf8, overlong and 1e400, plus `removed` on `json:/envelope` alone, 520 in all, against a list stated in the test; the closed pass names any other superseded copy with a `superseded:` line (review of 0277b97) |
| `R14_10_AnExemplarFailureIsNeverLedgerable` | `FuzzRun.Ledgerable` refuses an exemplar send's failure and accepts a variation's server fault (review of 7bf1160) |
| `R14_10_EveryRefusedQueryParameterIsRefusedByItsRoute` | on a plain `ForumFixture`, so its reads never enter the pass's recorder: each `RefusedQuery` entry, sent with `q=x`, answers 400 with its problem type and a detail naming the parameter; no entry is also in `RequestReadQuery` |

**Steps:**

- [ ] **Step 1.** Run the whole suite at the base in Release and record each assembly's count in your report.
- [ ] **Step 2.** Write the interfaces above and `Exemplars.All`. One or more rows per route, as follows:

| Route | Exemplar |
|---|---|
| `GET /health`, the reader contract, `/oauth/jwks`, `/.well-known/oauth-authorization-server`, `/v1/log/head`, `/v1/log/jwks` | none needed |
| `/v1/log/proof/{index}` | `index=0`, `tree_size` = head's size |
| `/v1/log/consistency` | `from=1`, `to` = head's size |
| `/v1/log/entries/{index}` | `index=0` |
| `/v1/jwks` | `agent` = seed agent |
| `GET /v1/posts/{postId}` | seed question; `If-None-Match: "x"`; `marking` = the first wire value the batch accepts; and `If-Match`, `If-Modified-Since`, `If-Unmodified-Since`, which the framework's file result reads on the 200 (the coverage of reads found them; with no validator passed to it, none changes the answer) |
| `/v1/threads/{rootPostId}` | seed question, `marking` (the route reads it, so `RequestReadQuery` lists it) |
| `/v1/boards/{board}/posts` | seed board, `marking`. The route reads no other query parameter (`ForumEndpoints.cs:1216`–`:1241`; register D36), and a value for one nothing reads would count as reach (clause 5) without reaching anything |
| `/v1/search` | `q` = a word of the seed title (`FuzzContext.SeedWord`), plus `board` (seed board), `author` (seed agent), `kind=question`, `tags=jcs`, `cursor` = the `next_cursor` of a first search page the seeding takes (`c.SearchCursor`, `FuzzContext.cs:154`), `limit=10`, `why=true`, `min_verification=V0`, `marking`. `verification` and `environment_version` are read only to be refused (R9.26), so no exemplar can send them; they are in `SurfaceInventory.RefusedQuery` |
| `/v1/inbox` | as the T1 fuzz agent; `board` (seed board), `tags=jcs`, `cursor` = the `next_cursor` of a first inbox page the seeding takes (`c.InboxCursor`, `FuzzContext.cs:157`), `limit=10`, `marking` |
| `/v1/flags`, `GET /v1/posts/{postId}/flags` | as the T1 fuzz agent; the second takes the agent's own question (`c.OwnQuestionId`); no query |
| `POST /v1/posts/batch` | `{"digests":[seed digest]}`, and `marking` on the query |
| `POST /v1/agents` | fresh enrollment, as `ForumAgent.EnrollAsync` sends it; consumed |
| `POST /oauth/token` | urlencoded and multipart variants, each sending `; charset=utf-8` on its `Content-Type` (on the request for urlencoded, on each part for multipart), written by the fuzzer's renderer because `FormUrlEncodedContent` sends none; the `DPoP` proof is a JWS part |
| `POST /v1/posts` | one row per kind: question (`title` and `body` Fresh, high-entropy per send), answer (`parent` = seed), comment, revision (consumed: `Prepare` posts a fresh post of the agent's own, and `parent` and `prev` name it), vote and verification as `ForumAgent.SignVote` and `SignVerification` build them (both consumed: `Prepare` creates a fresh target owned by a second agent, result-bearing for a verification); finding (Table 10 grants `finding`/`create` from T2 only, so its `Prepare` raises the fuzz agent to T2 once: five of its answers accepted, then 31 days; `R14_10_EveryPostKindHasAnExemplar` requires the row); one question row carries every optional member (`not_duplicate`, `duplicate_rationale`, `model_hint`, and one `code_blocks` element with `language`, `source` and `license`), and the verification row `artifact_digest` and a `version` on its ref, so no `question-not-duplicate` row is needed; `CreatesPosts`, each on a fixture of its own (spec §4.10) |
| `POST /v1/posts/{postId}/flags` | `{"kind":"spam","rationale":"r"}`; consumed: `Prepare` posts a fresh question as a second agent and advances the clock 25 hours, and the path takes that question's id, so A4b's budget and repeat rule never refuse a variation (spec §4.10, §4.12) |
| `POST /v1/posts/{postId}/accept` | consumed; `Prepare` asks and answers as `AcceptAnswerTests` does. Read `AcceptAnswerAsync` (`ForumEndpoints.cs:1340`) for which id the path takes |

  Every JSON-body row sends `Content-Type: application/json; charset=utf-8`, so `header:Content-Type;charset` is a part of every body route (acceptance cases 5 and 11 need it). Recorded as built (review of 0277b97): every query value above is load-bearing. `R14_10_EveryQueryParameterARouteReadsHasAnExemplarValue` and the closed pass's per-route coverage hold each route's reads to its own exemplars' `Query` keys, so a row that omits one fails that fact.

  Authenticated rows carry `jws:token:*` (re-signed with `forum.IssuerSigningKeyPem`) and `jws:proof:*`. A write row's proof carries `nonce`: obtain it first by a send that answers 401 with `DPoP-Nonce`, as `SendAsAgentAsync` does (`RequestSurfaceTests.cs:1076`). The fuzz agent is raised to T1 as `EnrolledAtT1Async` does (`:1049`). Move that helper to `FuzzRun`.

- [ ] **Step 3.** Write the closed pass in `FuzzRun.ClosedAsync`:
  - Run the groups in order (spec §4.10).
  - Per row, send the exemplar plain, then each `(part, variation, copy)` from `Mutator.Plan`, then the exemplar again.
  - Print `Mutator.Plan`'s send count per row and in total before the first send.
  - Each `CreatesPosts` row and the `accept` row runs on a new `ForumFixture`, seeded by the same steps and disposed after the row (spec §4.10).
  - Before any send on a `CreatesPosts` row or a `Prepare`: `forum.Clock.Advance(TimeSpan.FromHours(1))`, then get a fresh token **and** a fresh DPoP nonce. The flag row's `Prepare` advances 25 hours instead, past R7.22's trailing window. Every `iat`, `exp`, `nbf` and `created_at` comes from `forum.Clock`, never the wall clock.
  - Record elapsed time against send index per row in the `CURIA_FUZZ_TIMINGS` file.
  - Time each send with `Stopwatch` around `SendAsync` plus `ReadAsStringAsync`.
  - Collect created post ids from 201 bodies.
  - Add headers with `TryAddWithoutValidation`. Count a send as `unsent` only when building the `HttpRequestMessage` throws, before `SendAsync` is called; record unsent per part. Any exception from `SendAsync` or the body read is a failure line carrying the exception type, never `unsent`.
  - Recorded as built: a path variation holding U+0000 is counted `unsent`, because no request can be built for a path the host cannot read, so no `htu` can be bound to it.
  - Recorded as built: created post ids are taken only from the 201s of `POST /v1/posts` rows (`CreatesPosts`); a flag's or an acceptance's 201 names a post it did not create.
  - Recorded as built: reach (clause 5) excludes by name, in `Oracle.AuthenticationAndSignatureRefusals`, `curia/authn/signature-invalid`, `curia/jws/signature-invalid`, `curia/authn/missing-authorization` and `curia/authn/missing-dpop-proof`; any other problem type, or a 2xx, counts as reach.
  - Recorded as built: the `CURIA_FUZZ_FAILURES` file is JSONL, one object `{route, variant, part, variation, copy, status, problemType, elapsedMs, ledgered}` per line. The `CURIA_FUZZ_ANSWERS` file is one indented JSON object whose keys are `"{Route} [{Variant}] {part} {variation} {copy}"` (the exemplar's sends as `exemplar first plain` and `exemplar last plain`) and whose values are `"{status} {problem type}"`. Since A2's review, all three passes append to the file and none truncates it; delete it before a run.
  - Recorded as built (review of 7bf1160): a variation whose envelope has no canonical form (nine variation ids: the raw-byte nul-raw, lone-high-raw, lone-low-raw, bad-utf8 and overlong; the escaped lone-high and lone-low, `\ud800` and `\udc00`, which decode to lone surrogates; 1e400, which has no IEEE double; and `removed` of the `json:/envelope` root, which leaves nothing to sign) has no re-signed copy. Rendering one throws `NotReSignableException`, the pass counts it `superseded`, and clause 5 reads sent + unsent + superseded = planned. Before the fix, the re-signed copy was sent carrying the original signature, under a `re-signed` key: 15,991 such keys in the first answers file, and reach credited to them. `Mutator.Plan`'s printed counts still include superseded copies. Recorded as built (review of 0277b97): the set is bounded. `FuzzRun.MayBeSuperseded` names a superseded copy outside it on a `superseded:` line, and `R14_10_OnlyAVariationWithNoCanonicalFormIsSuperseded` holds it offline to a hand-stated list, so a renderer regression cannot move a canonical variation out of the unsent ceiling.
  - Recorded as built (review of 7bf1160): the restart check on the `vote` row's fixture asks only `GET /health`. A vote is never served (R8.55), so the vote ids its 201s name are not read back. Spec §4.10 says every post the run created.
  - Recorded as built (review of 7bf1160): the 2,000 ms budget excludes the first send of each row, not of each route (`POST /v1/posts` has seven rows and `/oauth/token` two). Rows on a fixture of their own start a new host, so a per-route exclusion would budget a cold start. The excluded send is always the plain exemplar's `first`, which clause 4 still requires to answer 2xx. The `last` exemplar send and every variation send are budgeted. The per-row `slowest` in `CURIA_FUZZ_TIMINGS` skips the same send.
  - Recorded as built (review of 7bf1160): the first pass reduced each envelope pointer to its top-level member, so an empty `code_blocks` satisfied the fact and no element member was ever varied. The fact now compares element members too.
  - Recorded as built (review of 7bf1160): `Judge` refused to ledger budget failures only, so a `FaultRow` with Part `exemplar` could silence a clause-4 failure. `FuzzRun.Ledgerable` now refuses both.
  - Recorded as built (review of 7bf1160, second round): the two-way query check found `GET /v1/search` reading `verification` and `environment_version` (`ForumEndpoints.cs:1531`-`:1537`) through `ContainsKey`, only to refuse them. No exemplar can send one, because clause 4 requires a 2xx. They are listed in `RefusedQuery`. The closed pass holds that list to the host's reads both ways, and a fact holds it to the answer, so a filter the route honours cannot be parked there.
- [ ] **Step 4.** Run it red at the base:

  ```
  dotnet test tests/Curia.Api.Tests -c Release --filter "FullyQualifiedName~Fuzz" > /tmp/fuzz-base.log 2>&1
  ```

  Expected: `Failed!`, including budget rows on `POST /v1/posts json:/envelope/body long-r-262144` and `long-comment-262144`. This is a prediction from the measured times and has not been run. **If the `long-r-262144` budget row is absent, stop and report**: the fuzzer did not reach a body string at the cap, and the reach clause should say which part it missed. `long-comment-262144` is predicted at about 3 s against the 2 s budget, which is a narrow margin, so its absence is recorded and does not stop the task. Record **every** failure line verbatim.
- [ ] **Step 5.** Sort the failures:
  - **Budget rows** are D32. Leave them failing; A3 fixes them.
  - **Each 5xx and each problemless 4xx on a variation** becomes ledger row `D33-<n>`, numbered in order of first appearance, plus one register line under D33's "Red facts first" giving the row, the exception type from the log (event 5000), and the file:line of the throw. One on an exemplar send is an exemplar defect: fix the exemplar, never ledger it.
  - **Do not fix any of them in PR A.** That includes rows that look like one-liners (Hardin; spec §4.1).
- [ ] **Step 6.** Run again, with `CURIA_FUZZ_FAILURES` and `CURIA_FUZZ_ANSWERS` set. Diff the sorted failure rows of Steps 4 and 6 (excluding rows Step 5 ledgered). Every row that is not a budget row (D32) must be identical in both runs, or stop and report the rows that flapped. Budget rows are compared as a set and may differ only near the budget: a budget row present in one run and absent in the other is recorded, and does not stop the task, because a budget row's elapsed time depends on the machine and on the content of Fresh parts (a `long-self` of a Fresh part repeats a value that is regenerated on every send). A budget row on a part or variation that has none in the other run, a budget row that becomes a non-budget row, or any non-budget difference stops the task. On the first run, one row flapped: verification `json:/envelope/nonce long-self-262144` re-signed (3081 ms in Step 6, under budget in Step 4). A3 Step 7 is the check that no budget row survives. Expected: only budget rows fail. Keep the answers file as `/tmp/answers-A1.json`, the wire snapshot A3 and A4 compare against. Delete the failures file before each run.
  - Recorded as built (review of 7bf1160, second round): the baseline was regenerated after `RefusedQuery` landed, because that change alters the coverage lines. Two closed passes, both with `CURIA_FUZZ_FAILURES` and `CURIA_FUZZ_ANSWERS` set; run 2's answers file is `/tmp/answers-A1.json`. Planned sends per row and in total (identical in both runs; question 3421 and verification 3407 carry the new `code_blocks` element and `refs[0].version`):

    ```
    GET /health [plain]: 0 sends
    GET /.well-known/reader-contract/v1 [plain]: 0 sends
    GET /oauth/jwks [plain]: 0 sends
    GET /.well-known/oauth-authorization-server [plain]: 0 sends
    GET /v1/log/head [plain]: 0 sends
    GET /v1/log/jwks [plain]: 0 sends
    GET /v1/log/proof/{index:long} [plain]: 50 sends
    GET /v1/log/consistency [plain]: 50 sends
    GET /v1/log/entries/{index:long} [plain]: 25 sends
    GET /v1/jwks [plain]: 25 sends
    GET /v1/posts/{postId} [plain]: 150 sends
    GET /v1/threads/{rootPostId} [plain]: 50 sends
    GET /v1/boards/{board}/posts [plain]: 50 sends
    GET /v1/search [plain]: 250 sends
    GET /v1/inbox [plain]: 1911 sends
    GET /v1/flags [plain]: 1786 sends
    GET /v1/posts/{postId}/flags [plain]: 1811 sends
    POST /v1/posts/batch [plain]: 127 sends
    POST /oauth/token [urlencoded]: 1498 sends
    POST /oauth/token [multipart]: 1498 sends
    POST /v1/agents [plain]: 200 sends
    POST /v1/posts/{postId}/flags [plain]: 2013 sends
    POST /v1/posts/{postId}/accept [plain]: 1881 sends
    POST /v1/posts [question]: 3421 sends
    POST /v1/posts [answer]: 3023 sends
    POST /v1/posts [comment]: 3023 sends
    POST /v1/posts [revision]: 3097 sends
    POST /v1/posts [vote]: 2945 sends
    POST /v1/posts [verification]: 3407 sends
    POST /v1/posts [finding]: 3023 sends
    total: 35314 sends
    ```

    Superseded: 520 re-signed copies in the pass (each run), all on `json:/envelope` parts of the seven `POST /v1/posts` rows (question 100, verification 100, revision 72, answer 65, comment 65, finding 65, vote 53) across nine variation ids (nul-raw, lone-high, lone-high-raw, lone-low, lone-low-raw, bad-utf8, overlong 72 each; 1e400 9; removed of the root 7), counted as the (part, variation) pairs the answers file holds `unsigned` with no `re-signed` key. The answers file no longer carries `re-signed` keys for these variations of the envelope: 0, against 15,991 such keys in the first answers file. The 1,665 `re-signed` keys for raw-byte variations that remain are all on `jws:` positions, whose compact signature is computed over the varied bytes, so those copies are real. Every failure is a budget row (D32): 101 in run 1 and 103 in run 2, no 5xx, no problemless 4xx, no `coverage:`, `superseded:` or `ledger:` line, and no exemplar failure, so nothing is ledgered. The runs differ by two budget rows present only in run 2, comment and verification `json:/envelope/nonce long-self-262144 re-signed` (a Fresh part, the shape Step 6 already records); each run has budget rows on that part and that variation, so this is recorded and does not stop the task. Run 2's failure lines, verbatim:

    ```
    201 POST /v1/posts/{postId}/flags [plain] json:/rationale long-r-262144 plain: over budget: 7980 ms against 2000 ms (set in Release on an Apple M3 Max) (7980 ms)
    201 POST /v1/posts/{postId}/flags [plain] json:/rationale long-comment-262144 plain: over budget: 2953 ms against 2000 ms (set in Release on an Apple M3 Max) (2953 ms)
    201 POST /v1/posts/{postId}/flags [plain] json:/rationale long-self-262144 plain: over budget: 7981 ms against 2000 ms (set in Release on an Apple M3 Max) (7981 ms)
    201 POST /v1/posts [question] json:/envelope/board long-r-262144 re-signed: over budget: 7987 ms against 2000 ms (set in Release on an Apple M3 Max) (7987 ms)
    201 POST /v1/posts [question] json:/envelope/board long-comment-262144 re-signed: over budget: 2972 ms against 2000 ms (set in Release on an Apple M3 Max) (2972 ms)
    201 POST /v1/posts [question] json:/envelope/board long-self-262144 re-signed: over budget: 7224 ms against 2000 ms (set in Release on an Apple M3 Max) (7224 ms)
    201 POST /v1/posts [question] json:/envelope/body long-r-262144 re-signed: over budget: 8006 ms against 2000 ms (set in Release on an Apple M3 Max) (8006 ms)
    201 POST /v1/posts [question] json:/envelope/body long-comment-262144 re-signed: over budget: 2973 ms against 2000 ms (set in Release on an Apple M3 Max) (2973 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/language long-r-262144 re-signed: over budget: 8015 ms against 2000 ms (set in Release on an Apple M3 Max) (8015 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/language long-comment-262144 re-signed: over budget: 2976 ms against 2000 ms (set in Release on an Apple M3 Max) (2976 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/language long-self-262144 re-signed: over budget: 7998 ms against 2000 ms (set in Release on an Apple M3 Max) (7998 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/source long-r-262144 re-signed: over budget: 8008 ms against 2000 ms (set in Release on an Apple M3 Max) (8008 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/source long-comment-262144 re-signed: over budget: 2988 ms against 2000 ms (set in Release on an Apple M3 Max) (2988 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/source long-self-262144 re-signed: over budget: 8019 ms against 2000 ms (set in Release on an Apple M3 Max) (8019 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/license long-r-262144 re-signed: over budget: 8014 ms against 2000 ms (set in Release on an Apple M3 Max) (8014 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/license long-comment-262144 re-signed: over budget: 3001 ms against 2000 ms (set in Release on an Apple M3 Max) (3001 ms)
    201 POST /v1/posts [question] json:/envelope/code_blocks/0/license long-self-262144 re-signed: over budget: 2370 ms against 2000 ms (set in Release on an Apple M3 Max) (2370 ms)
    201 POST /v1/posts [question] json:/envelope/duplicate_rationale long-r-262144 re-signed: over budget: 8033 ms against 2000 ms (set in Release on an Apple M3 Max) (8033 ms)
    201 POST /v1/posts [question] json:/envelope/duplicate_rationale long-comment-262144 re-signed: over budget: 3007 ms against 2000 ms (set in Release on an Apple M3 Max) (3007 ms)
    201 POST /v1/posts [question] json:/envelope/nonce long-r-262144 re-signed: over budget: 8049 ms against 2000 ms (set in Release on an Apple M3 Max) (8049 ms)
    201 POST /v1/posts [question] json:/envelope/nonce long-comment-262144 re-signed: over budget: 3016 ms against 2000 ms (set in Release on an Apple M3 Max) (3016 ms)
    201 POST /v1/posts [question] json:/envelope/nonce long-self-262144 re-signed: over budget: 3085 ms against 2000 ms (set in Release on an Apple M3 Max) (3085 ms)
    201 POST /v1/posts [question] json:/envelope/tags/0 long-r-262144 re-signed: over budget: 8055 ms against 2000 ms (set in Release on an Apple M3 Max) (8055 ms)
    201 POST /v1/posts [question] json:/envelope/tags/0 long-comment-262144 re-signed: over budget: 3025 ms against 2000 ms (set in Release on an Apple M3 Max) (3025 ms)
    201 POST /v1/posts [question] json:/envelope/tags/0 long-self-262144 re-signed: over budget: 8074 ms against 2000 ms (set in Release on an Apple M3 Max) (8074 ms)
    201 POST /v1/posts [question] json:/envelope/title long-r-262144 re-signed: over budget: 8061 ms against 2000 ms (set in Release on an Apple M3 Max) (8061 ms)
    201 POST /v1/posts [question] json:/envelope/title long-comment-262144 re-signed: over budget: 3035 ms against 2000 ms (set in Release on an Apple M3 Max) (3035 ms)
    201 POST /v1/posts [question] json:/envelope/model_hint long-r-262144 re-signed: over budget: 8057 ms against 2000 ms (set in Release on an Apple M3 Max) (8057 ms)
    201 POST /v1/posts [question] json:/envelope/model_hint long-comment-262144 re-signed: over budget: 3047 ms against 2000 ms (set in Release on an Apple M3 Max) (3047 ms)
    201 POST /v1/posts [question] json:/envelope/model_hint long-self-262144 re-signed: over budget: 8064 ms against 2000 ms (set in Release on an Apple M3 Max) (8064 ms)
    201 POST /v1/posts [answer] json:/envelope/board long-r-262144 re-signed: over budget: 8005 ms against 2000 ms (set in Release on an Apple M3 Max) (8005 ms)
    201 POST /v1/posts [answer] json:/envelope/board long-comment-262144 re-signed: over budget: 2965 ms against 2000 ms (set in Release on an Apple M3 Max) (2965 ms)
    201 POST /v1/posts [answer] json:/envelope/board long-self-262144 re-signed: over budget: 7215 ms against 2000 ms (set in Release on an Apple M3 Max) (7215 ms)
    201 POST /v1/posts [answer] json:/envelope/body long-r-262144 re-signed: over budget: 8004 ms against 2000 ms (set in Release on an Apple M3 Max) (8004 ms)
    201 POST /v1/posts [answer] json:/envelope/body long-comment-262144 re-signed: over budget: 2973 ms against 2000 ms (set in Release on an Apple M3 Max) (2973 ms)
    201 POST /v1/posts [answer] json:/envelope/nonce long-r-262144 re-signed: over budget: 7996 ms against 2000 ms (set in Release on an Apple M3 Max) (7996 ms)
    201 POST /v1/posts [answer] json:/envelope/nonce long-comment-262144 re-signed: over budget: 2979 ms against 2000 ms (set in Release on an Apple M3 Max) (2979 ms)
    201 POST /v1/posts [answer] json:/envelope/nonce long-self-262144 re-signed: over budget: 3783 ms against 2000 ms (set in Release on an Apple M3 Max) (3783 ms)
    201 POST /v1/posts [answer] json:/envelope/parent long-r-262144 re-signed: over budget: 8014 ms against 2000 ms (set in Release on an Apple M3 Max) (8014 ms)
    201 POST /v1/posts [answer] json:/envelope/parent long-comment-262144 re-signed: over budget: 2994 ms against 2000 ms (set in Release on an Apple M3 Max) (2994 ms)
    201 POST /v1/posts [answer] json:/envelope/parent long-self-262144 re-signed: over budget: 4056 ms against 2000 ms (set in Release on an Apple M3 Max) (4056 ms)
    201 POST /v1/posts [answer] json:/envelope/tags/0 long-r-262144 re-signed: over budget: 8038 ms against 2000 ms (set in Release on an Apple M3 Max) (8038 ms)
    201 POST /v1/posts [answer] json:/envelope/tags/0 long-comment-262144 re-signed: over budget: 3004 ms against 2000 ms (set in Release on an Apple M3 Max) (3004 ms)
    201 POST /v1/posts [answer] json:/envelope/tags/0 long-self-262144 re-signed: over budget: 8037 ms against 2000 ms (set in Release on an Apple M3 Max) (8037 ms)
    201 POST /v1/posts [comment] json:/envelope/board long-r-262144 re-signed: over budget: 7988 ms against 2000 ms (set in Release on an Apple M3 Max) (7988 ms)
    201 POST /v1/posts [comment] json:/envelope/board long-comment-262144 re-signed: over budget: 2960 ms against 2000 ms (set in Release on an Apple M3 Max) (2960 ms)
    201 POST /v1/posts [comment] json:/envelope/board long-self-262144 re-signed: over budget: 7219 ms against 2000 ms (set in Release on an Apple M3 Max) (7219 ms)
    201 POST /v1/posts [comment] json:/envelope/body long-r-262144 re-signed: over budget: 7999 ms against 2000 ms (set in Release on an Apple M3 Max) (7999 ms)
    201 POST /v1/posts [comment] json:/envelope/body long-comment-262144 re-signed: over budget: 2977 ms against 2000 ms (set in Release on an Apple M3 Max) (2977 ms)
    201 POST /v1/posts [comment] json:/envelope/nonce long-r-262144 re-signed: over budget: 8006 ms against 2000 ms (set in Release on an Apple M3 Max) (8006 ms)
    201 POST /v1/posts [comment] json:/envelope/nonce long-comment-262144 re-signed: over budget: 2980 ms against 2000 ms (set in Release on an Apple M3 Max) (2980 ms)
    201 POST /v1/posts [comment] json:/envelope/nonce long-self-262144 re-signed: over budget: 3048 ms against 2000 ms (set in Release on an Apple M3 Max) (3048 ms)
    201 POST /v1/posts [comment] json:/envelope/parent long-r-262144 re-signed: over budget: 8007 ms against 2000 ms (set in Release on an Apple M3 Max) (8007 ms)
    201 POST /v1/posts [comment] json:/envelope/parent long-comment-262144 re-signed: over budget: 2995 ms against 2000 ms (set in Release on an Apple M3 Max) (2995 ms)
    201 POST /v1/posts [comment] json:/envelope/parent long-self-262144 re-signed: over budget: 5285 ms against 2000 ms (set in Release on an Apple M3 Max) (5285 ms)
    201 POST /v1/posts [comment] json:/envelope/tags/0 long-r-262144 re-signed: over budget: 8052 ms against 2000 ms (set in Release on an Apple M3 Max) (8052 ms)
    201 POST /v1/posts [comment] json:/envelope/tags/0 long-comment-262144 re-signed: over budget: 2999 ms against 2000 ms (set in Release on an Apple M3 Max) (2999 ms)
    201 POST /v1/posts [comment] json:/envelope/tags/0 long-self-262144 re-signed: over budget: 8047 ms against 2000 ms (set in Release on an Apple M3 Max) (8047 ms)
    201 POST /v1/posts [revision] json:/envelope/board long-r-262144 re-signed: over budget: 7991 ms against 2000 ms (set in Release on an Apple M3 Max) (7991 ms)
    201 POST /v1/posts [revision] json:/envelope/board long-comment-262144 re-signed: over budget: 2967 ms against 2000 ms (set in Release on an Apple M3 Max) (2967 ms)
    201 POST /v1/posts [revision] json:/envelope/board long-self-262144 re-signed: over budget: 7225 ms against 2000 ms (set in Release on an Apple M3 Max) (7225 ms)
    201 POST /v1/posts [revision] json:/envelope/body long-r-262144 re-signed: over budget: 8015 ms against 2000 ms (set in Release on an Apple M3 Max) (8015 ms)
    201 POST /v1/posts [revision] json:/envelope/body long-comment-262144 re-signed: over budget: 2982 ms against 2000 ms (set in Release on an Apple M3 Max) (2982 ms)
    201 POST /v1/posts [revision] json:/envelope/nonce long-r-262144 re-signed: over budget: 8021 ms against 2000 ms (set in Release on an Apple M3 Max) (8021 ms)
    201 POST /v1/posts [revision] json:/envelope/nonce long-comment-262144 re-signed: over budget: 3010 ms against 2000 ms (set in Release on an Apple M3 Max) (3010 ms)
    201 POST /v1/posts [revision] json:/envelope/nonce long-self-262144 re-signed: over budget: 3813 ms against 2000 ms (set in Release on an Apple M3 Max) (3813 ms)
    201 POST /v1/posts [revision] json:/envelope/parent long-r-262144 re-signed: over budget: 8032 ms against 2000 ms (set in Release on an Apple M3 Max) (8032 ms)
    201 POST /v1/posts [revision] json:/envelope/parent long-comment-262144 re-signed: over budget: 3010 ms against 2000 ms (set in Release on an Apple M3 Max) (3010 ms)
    201 POST /v1/posts [revision] json:/envelope/parent long-self-262144 re-signed: over budget: 4385 ms against 2000 ms (set in Release on an Apple M3 Max) (4385 ms)
    201 POST /v1/posts [revision] json:/envelope/tags/0 long-r-262144 re-signed: over budget: 8077 ms against 2000 ms (set in Release on an Apple M3 Max) (8077 ms)
    201 POST /v1/posts [revision] json:/envelope/tags/0 long-comment-262144 re-signed: over budget: 3029 ms against 2000 ms (set in Release on an Apple M3 Max) (3029 ms)
    201 POST /v1/posts [revision] json:/envelope/tags/0 long-self-262144 re-signed: over budget: 8062 ms against 2000 ms (set in Release on an Apple M3 Max) (8062 ms)
    201 POST /v1/posts [vote] json:/envelope/nonce long-r-262144 re-signed: over budget: 7983 ms against 2000 ms (set in Release on an Apple M3 Max) (7983 ms)
    201 POST /v1/posts [vote] json:/envelope/nonce long-comment-262144 re-signed: over budget: 2965 ms against 2000 ms (set in Release on an Apple M3 Max) (2965 ms)
    201 POST /v1/posts [vote] json:/envelope/nonce long-self-262144 re-signed: over budget: 2785 ms against 2000 ms (set in Release on an Apple M3 Max) (2785 ms)
    201 POST /v1/posts [verification] json:/envelope/body long-r-262144 re-signed: over budget: 7976 ms against 2000 ms (set in Release on an Apple M3 Max) (7976 ms)
    201 POST /v1/posts [verification] json:/envelope/body long-comment-262144 re-signed: over budget: 2963 ms against 2000 ms (set in Release on an Apple M3 Max) (2963 ms)
    201 POST /v1/posts [verification] json:/envelope/method long-r-262144 re-signed: over budget: 7997 ms against 2000 ms (set in Release on an Apple M3 Max) (7997 ms)
    201 POST /v1/posts [verification] json:/envelope/method long-comment-262144 re-signed: over budget: 2988 ms against 2000 ms (set in Release on an Apple M3 Max) (2988 ms)
    201 POST /v1/posts [verification] json:/envelope/nonce long-r-262144 re-signed: over budget: 8038 ms against 2000 ms (set in Release on an Apple M3 Max) (8038 ms)
    201 POST /v1/posts [verification] json:/envelope/nonce long-comment-262144 re-signed: over budget: 2997 ms against 2000 ms (set in Release on an Apple M3 Max) (2997 ms)
    201 POST /v1/posts [verification] json:/envelope/nonce long-self-262144 re-signed: over budget: 3810 ms against 2000 ms (set in Release on an Apple M3 Max) (3810 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/kind long-r-262144 re-signed: over budget: 8054 ms against 2000 ms (set in Release on an Apple M3 Max) (8054 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/kind long-comment-262144 re-signed: over budget: 3023 ms against 2000 ms (set in Release on an Apple M3 Max) (3023 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/kind long-self-262144 re-signed: over budget: 8084 ms against 2000 ms (set in Release on an Apple M3 Max) (8084 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/value long-r-262144 re-signed: over budget: 8069 ms against 2000 ms (set in Release on an Apple M3 Max) (8069 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/value long-comment-262144 re-signed: over budget: 3030 ms against 2000 ms (set in Release on an Apple M3 Max) (3030 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/value long-self-262144 re-signed: over budget: 3291 ms against 2000 ms (set in Release on an Apple M3 Max) (3291 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/version long-r-262144 re-signed: over budget: 8088 ms against 2000 ms (set in Release on an Apple M3 Max) (8088 ms)
    201 POST /v1/posts [verification] json:/envelope/refs/0/version long-comment-262144 re-signed: over budget: 3038 ms against 2000 ms (set in Release on an Apple M3 Max) (3038 ms)
    201 POST /v1/posts [finding] json:/envelope/board long-r-262144 re-signed: over budget: 8008 ms against 2000 ms (set in Release on an Apple M3 Max) (8008 ms)
    201 POST /v1/posts [finding] json:/envelope/board long-comment-262144 re-signed: over budget: 2972 ms against 2000 ms (set in Release on an Apple M3 Max) (2972 ms)
    201 POST /v1/posts [finding] json:/envelope/board long-self-262144 re-signed: over budget: 7214 ms against 2000 ms (set in Release on an Apple M3 Max) (7214 ms)
    201 POST /v1/posts [finding] json:/envelope/body long-r-262144 re-signed: over budget: 8004 ms against 2000 ms (set in Release on an Apple M3 Max) (8004 ms)
    201 POST /v1/posts [finding] json:/envelope/body long-comment-262144 re-signed: over budget: 2977 ms against 2000 ms (set in Release on an Apple M3 Max) (2977 ms)
    201 POST /v1/posts [finding] json:/envelope/nonce long-r-262144 re-signed: over budget: 7996 ms against 2000 ms (set in Release on an Apple M3 Max) (7996 ms)
    201 POST /v1/posts [finding] json:/envelope/nonce long-comment-262144 re-signed: over budget: 2975 ms against 2000 ms (set in Release on an Apple M3 Max) (2975 ms)
    201 POST /v1/posts [finding] json:/envelope/nonce long-self-262144 re-signed: over budget: 4538 ms against 2000 ms (set in Release on an Apple M3 Max) (4538 ms)
    201 POST /v1/posts [finding] json:/envelope/tags/0 long-r-262144 re-signed: over budget: 8025 ms against 2000 ms (set in Release on an Apple M3 Max) (8025 ms)
    201 POST /v1/posts [finding] json:/envelope/tags/0 long-comment-262144 re-signed: over budget: 3006 ms against 2000 ms (set in Release on an Apple M3 Max) (3006 ms)
    201 POST /v1/posts [finding] json:/envelope/tags/0 long-self-262144 re-signed: over budget: 8124 ms against 2000 ms (set in Release on an Apple M3 Max) (8124 ms)
    201 POST /v1/posts [finding] json:/envelope/title long-r-262144 re-signed: over budget: 8060 ms against 2000 ms (set in Release on an Apple M3 Max) (8060 ms)
    201 POST /v1/posts [finding] json:/envelope/title long-comment-262144 re-signed: over budget: 2997 ms against 2000 ms (set in Release on an Apple M3 Max) (2997 ms)
    ```
- [ ] **Step 7.** Commit: `R14.10: a fuzzer derived from the route table varies one part at a time, and its base is red (D32 budget rows; D33-1…n ledgered)`.

### Task A2: The random pass and the Kestrel pass

- [ ] Add `<PackageReference Include="CsCheck" />` to `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`. Run `dotnet restore Curia.sln --use-lock-file`, then `dotnet restore Curia.sln --locked-mode`. The second must succeed.
- [ ] **Seed.** In a scratch console referencing CsCheck 4.8.0, print `new CsCheck.PCG(1, 20261006).ToString()`. Paste the output into `RequestFuzzRandomTests.Seed` with a comment giving that derivation. If `PCG`'s constructor or `SampleAsync`'s parameter names differ in 4.8.0, adapt the call and keep the seed fixed, `iter: 2000` and `threads: 1`. Make no other change.
- [ ] **Fact `R14_10_NoRandomStringInAnyPartIsAServerFault`.** Asserts the same oracle over draws described in spec §4.10, and prints the seed and the row on failure. Its remark says that it is the only pass that combines hostile parts. Raw invalid bytes and lone surrogates are drawn per string (one string in five), not per unit, since each of them (and a noncharacter, which ADMIT refuses: curia/admit/noncharacter) refuses the whole value at the first parser. The fact asserts a floor on JSON and JWS draws that got past the first parser: a 2xx, or a refusal that is not one of ADMIT's (`curia/admit/*`), not one of `Oracle.ParserRefusals` (matched by exact slug, `curia/authn/malformed-jwk` included), not the token endpoint's refusal of its DPoP proof (`invalid_dpop_proof`, missing or unreadable, matched exactly), and not a credential or signature refusal; and a second floor on `POST /v1/posts` `json:/envelope/*` draws past ADMIT (`EnvelopeDivisor`), set at half the fraction measured on the first run, which the plan records.
- [ ] **Kestrel fixture.** `KestrelForumFixture : ForumFixture` calls `UseKestrel(0)` in its constructor.
- [ ] **Fact `R14_10_NoRawPathByteIsAServerFault`.** For each route with a path parameter and each of `%00`, `a%00b`, `%FF`, `%C0%80`, `%ED%A0%80`, and the raw bytes `0xFF` and `0x00` in the request line, write `"{METHOD} {path} HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"` to a `TcpClient` on the bound port and read the status line. Asserts: the status is below 500. A send that throws (no answer within 30 s, or a reset) is a failure, with status 0 and the exception's type, and is appended to the failures file like any other. Every route with a path parameter was sent at least one row that got past Kestrel's parser, meaning its answer was not Kestrel's own 400 (Kestrel's has an empty body), a 5xx included: a server fault is reported as one and also counts as past Kestrel. This is not reach of the route's path parser, which this unauthenticated pass does not claim (clause 5 of the closed pass holds that). If no row did, the fact fails with a message saying Kestrel refused every row.
- [ ] Both passes append every failure to `CURIA_FUZZ_FAILURES` when it is set, in the closed pass's schema (spec §4.10, "Files"): the random pass with variation `random-N` and its ledgered flag, the Kestrel pass with variant `kestrel`, part `path:<param>`, variation the value's name, copy `plain`, problemType empty, ledgered false. No pass truncates the file, so the caller deletes it before a run.
- [ ] Run both. New closed-pass and random-pass failures go to the ledger as in A1, Step 5. A random row's Variation is `random-N`; the random pass judges it, stale included, and the closed pass judges every other row. A Kestrel-pass failure is not ledgerable: stop and report it.
  - Recorded as built: `new CsCheck.PCG(1, 20261006).ToString()` printed `0000001diyh1` under 4.8.0 (constructor `PCG(uint stream, ulong seed)`), and the pass reads it back with `PCG.Parse`. Spec §4.10 forbids `Check.Sample*`, so there is no `SampleAsync` call: 2,000 draws in a plain loop on one thread, each through the generator's own `Gen<T>.Generate(PCG, Size, out Size)`.
  - Recorded as built: a row with no derived part (`GET /health` and the five other anonymous reads with no parameter) has nothing to draw a part from, so the row is drawn uniformly from the rows that have one. A string's length is drawn in UTF-8 bytes (a lone surrogate counted as its three generalized bytes), and the longest prefix of whole units within it is kept. A signed part is sent re-signed, and unsigned when its envelope has no canonical form. A draw no request can be built for is counted unsent, and the fact fails when more than a tenth of the draws are unsent. The budget excludes the first send of each route.
  - Recorded as built: `R14_10_NoRawPathByteIsAServerFault` got past Kestrel's parser on every route with a path parameter. On the two `{index:long}` routes, nothing past routing answers: `%FF`, `%C0%80` and `%ED%A0%80` fail the constraint and are answered 404 by routing, which is not Kestrel's 400 and so counts as past Kestrel's parser, as this step defines it. `%00`, `a%00b`, raw `0xFF` and raw `0x00` are answered 400 on every route. (Review of 9411deb.) On three routes a handler answered: GET post and GET thread 404 not-found, GET boards/{board}/posts 200. On the other five, what answered `%FF`, `%C0%80` and `%ED%A0%80` came before the path parser:
    - authentication (401 `curia/authn/missing-authorization`) on POST /v1/posts/{postId}/accept and GET /v1/posts/{postId}/flags
    - the body binder (400 `curia/request/unreadable`, empty body) on POST /v1/posts/{postId}/flags
    - routing (404 `curia/request/no-route`) on both {index:long} routes
  - Recorded as built: the closed pass appends its `CURIA_FUZZ_TIMINGS` header rather than truncating the file, so the `slowest-random` and `slowest-kestrel` rows the other two passes append survive whatever order the classes run in.
  - First run: both green, nothing ledgered. The random pass sent 1,939 draws and left 61 unsent (200 x165, 201 x26, 400 x648, 401 x792, 403 x1, 404 x234, 415 x73). In the whole suite, the one failure is still the closed pass, on 102 budget rows (D32), which A3 fixes.
  - Second run (review of 9411deb, per-string generator): both green, nothing ledgered. The random pass sent 1,952 draws and left 48 unsent (200 x232, 201 x54, 400 x595, 401 x781, 404 x238, 415 x51, 422 x1); 643 of 1,151 JSON and JWS draws got past the first parser. In the whole suite, the one failure is still the closed pass, on 103 budget rows (D32), which A3 fixes. Superseded (review of 6cbfa9f): 643 counted 149 ADMIT refusals as past the first parser (139 curia/admit/noncharacter, 5 malformed-json, 3 nul-byte, 1 raw-control-character, 1 missing-envelope), because ParserRefusals listed only invalid-utf8 and matched by substring. Without them the fraction was 494 of 1,151 (0.429), and Divisor should have been 5.
  - Recorded as built (review of 9411deb): the reviewer's probe of the per-unit generator found that of 154 signed `json:` draws, 145 were sent unsigned and 141 answered `curia/admit/invalid-utf8`; about 5 of 574 `POST /v1/posts` body or JWS draws got past ADMIT or authentication; and 623 of 796 JWS draws answered `curia/authn/malformed`. Raw invalid bytes and lone surrogates were each drawn per unit at a weight of 2 of 22, so a median-length JSON string carried one with probability above 99.9%, and escaped lone surrogates are refused too (the envelope parser, and `CompactJws`'s `EveryStringDecodes`). The corrected generator draws them per string: `CleanUnitGen` (every class but lone surrogates, raw invalid bytes and both noncharacter classes, all four of which ADMIT refuses wherever they appear; C0 and U+0000 kept, since an escaped NUL passes ADMIT) in four strings in five, and `DecodingHostileUnitGen` (the full mixture, unchanged) in one. Measured on the first run with the same seed: 643 of 1,151 JSON and JWS draws got past the first parser (0.559), against 87 of 1,100 (0.079) for the per-unit generator. `RequestFuzzRandomTests.Divisor` is 4, the smallest integer whose reciprocal is at most half the measured fraction (0.279). Status histogram: 200 x232, 201 x54, 400 x595, 401 x781, 404 x238, 415 x51, 422 x1. `CleanStrings` and `DecodingHostileStrings` are static properties, not fields, so F27, which leaves `CleanStrings` unread, still builds (an unread private field is CA1823, an error in this build). Superseded (review of 6cbfa9f): 643 counted 149 ADMIT refusals as past the first parser (139 curia/admit/noncharacter, 5 malformed-json, 3 nul-byte, 1 raw-control-character, 1 missing-envelope), because ParserRefusals listed only invalid-utf8 and matched by substring. Without them the fraction was 494 of 1,151 (0.429), and Divisor should have been 5.
  - Third run (review of 6cbfa9f): oracle by exact slug with every ADMIT refusal STOP and the token endpoint's invalid_dpop_proof STOP; noncharacters drawn only in the decoding-hostile strings. Sent 1,936, unsent 64; 643 of 1,217 JSON and JWS draws past the first parser (fraction 0.528), Divisor 4; 103 of 132 POST /v1/posts json:/envelope/* draws past ADMIT (fraction 0.780), EnvelopeDivisor 3; status histogram 200 x216, 201 x141, 400 x450, 401 x878, 404 x197, 415 x51, 422 x3. `OracleTests` was run red first against 6cbfa9f's classification: 15 of the 16 ADMIT slugs reflected from `CanonErrors` were counted past the parser, and both token-endpoint probes (no proof, and `DPoP: a.b.c`) answered `invalid_dpop_proof` and were counted past it.
- [ ] Commit: `R14.10: a seeded random pass, and raw path bytes over a real socket`.

### Task A3: The screener made linear (D32, R10.69)

- [ ] **Step 1. The corpus differential, before any change.** Write a scratch console (outside the repo) that loads `src/Curia.Domain/bin/Release/net10.0/Curia.Domain.dll` by path at run time (`AssemblyLoadContext.Default.LoadFromAssemblyPath`), never a copy in its own bin, and writes `SecretScanner.Version` and `InjectionDetector.Version` as its first line. For every line of `conformance/red-team/{payloads,benign,known-evasions,known-false-positives}.jsonl` it screens the entry the way `RedTeamCorpusTests` does, and writes `(file, line, outcome, [category, offset, length]…)` to `/tmp/screen-before.jsonl`.
- [ ] **Step 2. Red facts** in `tests/Curia.Domain.Tests/Screening/ScreeningCostTests.cs`:
  - **`R10_69_EveryScreeningPatternRunsOnTheLinearEngine`.** By reflection, take every static method returning `Regex` (any visibility) on every type in namespace `Curia.Domain.Screening` of `Curia.Domain.dll`. Assert the count is **15** (8 in `SecretScanner`, 6 in `InjectionDetector`, 1 in `DerivedViews`) and that each instance's `Options` has `NonBacktracking`. The message names each offender.
  - **`R10_69_EveryRegexInShippedCodeIsAGeneratedPatternOnTheLinearEngine`**, in `tests/Curia.Architecture.Tests/RegexEngineTests.cs` (review of 1b0d423). The screening path cannot be computed statically, because the reference client screens too, so the rule is structural over the IL of every assembly built from `src/` (Mono.Cecil, `Shipped.ShippedAssemblies()`, at least 12). (b) Every `newobj` of `Regex` or of a type deriving from it, every call to a static `Regex` pattern method (`IsMatch`, `Match`, `Matches`, `Replace`, `Split`, `Count`, `EnumerateMatches`, `EnumerateSplits`), and every type deriving from `Regex`, in every method body of every type, nested types and type initializers included, (c) is an offender unless it, or a type enclosing it, is in `System.Text.RegularExpressions.Generated` and carries `GeneratedCodeAttribute` with the tool `System.Text.RegularExpressions.Generator`: a rule, never a list of today's sites. The message names `Type::Method`, the opcode and its target, and cites R10.69. (d) Every `[GeneratedRegex]` method in shipped code has `NonBacktracking` in its options; (e) there are 15 of them, by name, and a future pattern that truly needs a backtracking construct is excluded in the fact with its reason, never allowed silently. (f) Not vacuous: the generator types the rule allows number exactly the attributed methods (15 and 15; an SDK bump that changes the generator's shape turns this red, and the remedy is to re-observe, not to loosen), and the same predicate, run over the nested `Bypasses` class alone, names exactly its three bypasses (a `static readonly Regex` field, so `Bypasses::.cctor`; a method returning `new Regex`; a method calling `Regex.IsMatch(s, "a+b")`). The self-scan is scoped to `Bypasses` and not the whole test assembly because `MutationResidueTests`' backreference pattern is legitimate test code that the linear engine cannot run.
  - **`R10_69_ScreeningAtTheStringCapStaysWithinItsBudget`**, a theory with rows at 262,144 UTF-8 bytes: `r`×, space×, tab×, `"ai"`+spaces, `<!--`×, `http://`×, `a:`×, `token = `×, `ignore all `×, U+200B×(262,144/3). Each row runs `ScreenEnvelope({"body":…})` and `ScreenText(…)`. Assert each takes at most `BudgetMilliseconds = 2_000`. The message names `RuntimeInformation.ProcessorArchitecture`, `Environment.ProcessorCount` and the measured time, and the machine the budget was set on, stated in source beside the constant (R10.69: "stated with the machine it was measured on"). With `CURIA_SCREEN_TIMINGS` set, each row appends one TSV line per call to the file it names (row, call, UTF-8 bytes, elapsed ms, `ProcessArchitecture`, `ProcessorCount`, invariant culture, under a static lock), so CI's run can be read where its logger prints nothing for a passing row.
  - **`R10_25_TheHighEntropyRuleReportsTheValueNotTheKeyword`**, in `DetectorTests`. For `"x aws_secret_access_key = " + 40 base64 characters`, the `ApiKey` flag's offset equals the index of the first value character, and its length is 40.
- [ ] **Step 3.** Run. Expected: the engine fact is RED, naming 15 rules. The timing theory is RED on at least `"ai"`+spaces. **Do not wait for that row:** wrap each row in a `CancellationTokenSource(BudgetMilliseconds * 5)` and `Task.Run`, and fail on timeout, so the red run ends in seconds rather than an hour. The value fact is GREEN, since it pins current behaviour. Run this red run filtered to `ScreeningCostTests` alone (`--filter FullyQualifiedName~ScreeningCostTests`): a timed-out row's regex keeps running on its thread until the process ends.
- [ ] **Step 4. Implementation.**
  - Add `| RegexOptions.NonBacktracking` to every `[GeneratedRegex]` options argument in `SecretScanner.cs` (`:165`, `:170`, `:175`, `:178`, `:182`, `:189`, `:194`), `InjectionDetector.cs` (`:92`, `:97`, `:105`, `:110`, `:118`, `:123`) and `DerivedViews.cs:170`. For `PrivateKeyPem`, `CloudCredential`, `JwtShape` and `EncodedBlock`, whose options are only `CultureInvariant`, the argument becomes `RegexOptions.CultureInvariant | RegexOptions.NonBacktracking`.
  - Replace `HighEntropyAssignment` with the pattern in spec §4.3 (options `IgnoreCase | CultureInvariant | NonBacktracking`). In `Scan` (`SecretScanner.cs:105`–`107`):

    ```csharp
    foreach (var match in HighEntropyAssignment().Matches(derivedCopy).Cast<Match>())
    {
        var value = match.Groups["value"];
        if (LooksHighEntropy(derivedCopy.AsSpan(value.Index, value.Length)))
            yield return new RiskFlag(RiskCategory.ApiKey, value.Index, value.Length, Version);
    }
    ```

    Update the comment above the pattern so it says that the keyword boundary is a consumed non-alphanumeric or the start, because the linear engine has no lookbehind (R10.69).
  - Set `SecretScanner.Version = "secrets/2026-10-06"` and `InjectionDetector.Version = "injection/2026-10-06"`. Each gets a history line. `SecretScanner`'s: "2026-10-06: every rule runs on the non-backtracking engine (R10.69, errata G18); the high-entropy rule's keyword boundary is consumed rather than looked behind. Verdicts on the red-team corpus are unchanged; on other input they may not be." `InjectionDetector`'s, which has no high-entropy rule: "2026-10-06: every rule runs on the non-backtracking engine (R10.69, errata G18). Verdicts on the red-team corpus are unchanged; on other input they may not be."
  - Update every reference to the old versions. List them with `grep -rnE 'secrets/20|injection/20' src/ tests/ conformance/` before and after; the after list holds only the new values, and both lists go in the commit body. This includes `DetectorTests`.
- [ ] **Step 5.** Rebuild. Rerun Step 1's console into `/tmp/screen-after.jsonl`, then require the two files' first lines to differ (the versions moved, which proves the after run read the new build), and require `bash -c 'diff <(tail -n +2 /tmp/screen-before.jsonl) <(tail -n +2 /tmp/screen-after.jsonl)'` to be empty (bash, because this shell is fish). **Otherwise stop and report the differing lines.** Run `dotnet test tests/Curia.Domain.Tests -c Release`. Expected: `Passed!`. Record the slowest timing row. **If it exceeds 500 ms, stop and report**, because the budget's 4× margin assumption has failed. After Step 8's commit, push the branch, run `gh workflow run ci.yml --ref request-fuzzer-gate` (CI runs on pull_request and main only, and no PR exists until A8), dispatch it three times; from each run download the `screen-timings` artifact, written by CI's separate screening-cost step after one untimed warm-up per process. It must hold exactly 20 rows, and the slowest row of each run is the stop's reading. The stop guards the margin where the gate is enforced; it never moves the budget. A missing file, a file of any other length, or a slowest row over 500 ms in any run means stop and report before A4.
- [ ] **Step 6.** Update `conformance/red-team/RESULTS.md`'s detector versions where it names them, with rates unchanged.
- [ ] **Step 7.** Rerun A1's fuzzer with `CURIA_FUZZ_ANSWERS=/tmp/answers-A3.json`. Expected: no budget rows, ledger rows unchanged, and `/tmp/answers-A3.json` identical to `/tmp/answers-A1.json` (screening changed cost, not answers).
  - Recorded as built: Step 1's console references the three Domain assemblies with `Private=false`, loads them from `src/Curia.Domain/bin/Release/net10.0` with `LoadFromAssemblyPath` (a `Resolving` handler for the other two), and reads both versions by reflection (`GetRawConstantValue`), because they are `const` and a compiled-in reference would print the console's build-time value. Each corpus line is screened in the three shapes `RedTeamCorpusTests` uses (bare, enveloped, enveloped after a line): 121 entries, 363 rows. Step 5: the first lines differ (`secrets/2026-09-26`, `injection/2026-09-26` before; `secrets/2026-10-06`, `injection/2026-10-06` after) and the rest is identical.
  - Recorded as built: `RuntimeInformation` has no `ProcessorArchitecture`; the message names `RuntimeInformation.ProcessArchitecture`. Rows are named (`r`, `space`, `tab`, `ai-then-spaces`, `html-comment-open`, `http-scheme`, `a-colon`, `token-assignment`, `ignore-all`, `zero-width-space`) and built inside the test; each ASCII unit is repeated and cut to exactly 262,144 bytes, and U+200B is 87,381 characters (262,143 bytes). The envelope is the canonical `{"body":…}`, built outside the timed call. Each call's time is written to the test output.
  - Step 3 red run, filtered to `ScreeningCostTests`: `Failed: 5, Passed: 6`. The engine fact named all 15 rules. Timing rows red: `r` (ScreenEnvelope 8,100 ms), `http-scheme` and `ai-then-spaces` (timed out at 10,000 ms), `html-comment-open` (3,221 ms). The value fact was green at the base.
  - Recorded as built: `DetectorTests`' remark on U+2060 named the version it joined in. It is history, so it now says "the injection rule set dated 2026-09-26" rather than the version string, and Step 4's after list holds only the new values: `InjectionDetector.cs` `Version`, `SecretScanner.cs` `Version`, and `RESULTS.md`'s detector line (regenerated by `RedTeamCorpusTests`, rates unchanged, which is Step 6).
  - Step 5 timings, Release, run alone (Arm64, 16 processors): slowest row `zero-width-space`, ScreenEnvelope 173 ms; every other row at most 148 ms (`r`'s ScreenEnvelope, the first call, JIT included). Under 500 ms.
  - Step 7: the whole Release suite with `CURIA_FUZZ_ANSWERS=/tmp/answers-A3.json` and `CURIA_FUZZ_FAILURES` set, eleven assemblies `Passed!`. The failures file is empty (no budget rows), `ExpectedFaults` is unchanged, and `/tmp/answers-A3.json` is byte-identical to `/tmp/answers-A1.json` (34,838 keys). The closed pass at b7866d1, before this change, wrote answers identical to `/tmp/answers-A1.json` and failed on 103 budget rows.
  - Review of 1b0d423: the reflection fact read static parameterless methods in Curia.Domain.Screening only, so a `static readonly Regex` field, a `new Regex` local or a static `Regex.IsMatch(input, pattern)` call on the screening path kept it green (a reviewer's probe, `new(@"\bai\s*[,:]?\s*you\b", RegexOptions.CultureInvariant)` in InjectionDetector, passed it and was caught only by the timing row keyed to its anchor word). The IL fact in Curia.Architecture.Tests closes that across every assembly under src/. The generator-emitted sites it allows were observed, in Release and Debug alike, as one type per pattern deriving from Regex, `System.Text.RegularExpressions.Generated.<RegexGenerator_g>{hash}__{Name}_{n}`, each constructing itself in its `.cctor` through `Regex::.ctor(String, RegexOptions)` (15 patterns, n = 0..14); no attributed method holds a site of its own. Each of those types carries `GeneratedCodeAttribute("System.Text.RegularExpressions.Generator", "10.0.14.42308")`, in both configurations, so the allowance requires the attribute as well as the namespace.
  - Review of 1b0d423: InjectionDetector's history line credited it with the high-entropy rule's boundary change, which is SecretScanner's alone; corrected, version unchanged (a comment, no verdict moved).
  - Review of 1b0d423: the budget named only the running machine; it now states the machine it was set on, as Oracle.cs does.
  - Review of 95b38b8 (`curia-architect`'s ruling, 2026-10-06): CI run 37566876224 (X64, 4 processors), the timing theory inside the whole solution's `dotnet test`, read zero-width-space ScreenEnvelope 1,508 ms and tab ScreenEnvelope 1,499 ms, past Step 5's 500 ms stop and under the 2,000 ms budget. Recorded as a fact, never as a basis: the budget stays 2,000 ms and the stop 500 ms.
  - Review of 95b38b8: a scratch console against the Release `Curia.Domain.dll` (Apple M3 Max) timed every slow row from 32 KiB to 512 KiB; each doubles at about 2.0, so R10.69 holds. About 100 ms of the first screening call in a process is building the fifteen non-backtracking automata and JIT, charged to whichever row runs first (tab, on CI). The rest of CI's number is attributed to eleven test assemblies running at once on four cores. That cause is inferred from the artifact's run order, not measured.
  - Review of 95b38b8: the proposal to run hidden-text detection on the original view only was dropped. It is not identical: `DerivedViews.cs` builds the `base64-decoded` view with `Enumerable.Repeat(start, decoded.Length)`, so a U+200B carried inside base64 is flagged only in that view. The flag volume goes with the coalescing decision and D35.
  - Review of 95b38b8: `ScreeningCostTests.WarmUp`, one untimed `ScreenText` and one `ScreenEnvelope` on `"x"` per process, awaited before any row is timed (A1's precedent of excluding the first send); never a full-size row, so no rule runs outside the timeout guard. CI's `Screening cost, alone (R10.69)` step runs `ScreeningCostTests` alone after the whole suite, with `CURIA_SCREEN_TIMINGS` set on that step only and a check that the file holds 20 rows; `CLAUDE.md`'s gate list carries the same command.
  - Review of 95b38b8: `R10_69_ScreeningCostScalesLinearlyToTheStringCap`, the same ten rows at 32,768 and 262,144 bytes (zero-width-space at bytes/3 characters), small and large alternated three times each per call under the 10,000 ms guard, holds min(large) / min(small) <= 24 (linear about 8, quadratic about 64). Green locally (Arm64, 16 processors): ratios 5.79 (zero-width-space ScreenEnvelope) to 8.99 (tab ScreenEnvelope). Red with `| RegexOptions.NonBacktracking` removed from `SecondPersonImperative` alone (F1's anchor): `Failed: 1, Passed: 9`, the `ai-then-spaces` row, `ScreenEnvelope did not finish within 10000 ms`; every other row's ratio between 5.99 and 8.71. Restored with a plain `cp`, rebuilt `--no-incremental`, green unpatched (`Passed: 10`), `git diff --quiet -- src/` clean.
  - Review of e166e59 (`curia-architect`'s ruling with hopper and hardin, 2026-10-07): CI run 37575114798 (X64, 4 processors) went red in the full-solution Test step on `R10_69_ScreeningCostScalesLinearlyToTheStringCap(row: "zero-width-space")`: ScreenEnvelope at 262,144 bytes took 24.8x its time at 32,768 bytes (fastest 1010534351 vs 40780954 Stopwatch ticks of 3), past the ceiling of 24. The isolated step on the same run passed (21 passed); its slowest `screen-timings` row was zero-width-space ScreenText at 311 ms (ScreenEnvelope 309 ms). The run counts toward nothing.
  - Review of e166e59: the scaling fact's premise "holds on any machine" is replaced by "on any machine running this class alone": load from other processes between samples is not linear work. The Test step runs `dotnet test Curia.sln ... --filter "FullyQualifiedName!~ScreeningCostTests"`, so the whole class is judged only in `Screening cost, alone (R10.69)` (the two filters over one name make the split exact; locally Curia.Domain.Tests lists 632 tests unfiltered and 611 filtered, 21 fewer). `CLAUDE.md`'s gate list carries the same filter (D16). The isolated step sets `CURIA_SCREEN_SCALING`, to which `AssertLinear` appends one TSV line per call (row, call, 32768, 262144, fastest small and large ticks, ratio F2, architecture, processors), checks 20 lines, and uploads it as `screen-scaling`. A filter matching nothing leaves both files absent and both 20-row checks red (checked by hand).
  - Review of e166e59: a ratio stop of 16 beside the assertion's ceiling of 24. Step 5's three CI runs on the new head each read, besides the timings stop, a `screen-scaling` artifact of exactly 20 rows with every ratio at most 24 and the largest at most 16; a run past 16 is a stop and report, not a rerun. The ceiling, the budget, the 500 ms stop and how timing is aggregated are unchanged.
  - Review of e166e59: the ratio check falsified on its own, Release, isolated command with `CURIA_SCREEN_SCALING` set, `| RegexOptions.NonBacktracking` removed from `HtmlComment()` alone (`InjectionDetector.cs:125`): `Failed: 3, Passed: 18`. The `html-comment-open` scaling row went red on the ratio, `ScreenEnvelope at 262144 bytes took 60.4x its time at 32768 bytes (fastest 2926787334 vs 48471417 Stopwatch ticks of 3)`, not on the timeout, and its line was in the file (19 lines, so the 20-row check would also go red). N = 60.4 for ScreenEnvelope; ScreenText was not measured, since the row stops at its first failed call. The budget row (ScreenEnvelope 3,038 ms) and the engine fact went red with it. Restored with a plain `cp`, rebuilt `--no-incremental`, green unpatched (`Passed: 21`, 20 scaling lines, ratios 5.34 to 9.52, Arm64, 16 processors), `git diff --quiet -- src/` clean.
  - Review of c31ce24 (`curia-architect`'s ruling, the fourth stop on the zero-width-space row, 2026-10-07): the cause, measured. The row's allocation was linear (ratio 7.98; 106,636,800 bytes at the cap for ScreenEnvelope) and its wall time was not: `InjectionDetector` yielded one `RiskFlag` per hidden character in each of about five derived views, about 437,000 objects at the cap, all held live until `ContentScreener.Conclude` kept the first of each (category, offset). The collector re-traced a live set that grew with the input (gen0/1/2 10/5/1 at the cap), so the work was mildly superlinear and R10.69 did not hold on this path.
  - Review of c31ce24: the cure is `Conclude`'s keep-first rule applied early. `InjectionDetector.Scan` is now `ScanPatterns` followed by `HiddenTextOffsets`, which yields `int`s; `ContentScreener.Detect` runs the two steps itself and allocates a flag only for a (category, offset) it has not seen, and `Conclude` keeps the first of each key with `DistinctBy`. It is output-identical: `Conclude` still keeps the first of each key, so dropping a later member of a group cannot change which flag survives; both offset maps (`DerivedView.ToOriginal`, `CanonicalString.ToCanonical`) send an empty span to (0, 0) and otherwise depend only on its start, which is why a zero-length flag bypasses the seen-set; string tokens are disjoint, so `ScreenEnvelope` needs no second set; within a view the order stays secret flags, pattern flags (`HtmlComment` included), hidden offsets; and the line-joined view still never runs the injection detector. `Scan`'s public output is unchanged. No detector version moved and no errata entry is owed. Proven on the corpus: Step 1's console before (at c31ce24's source) and after wrote byte-identical files, first line included (364 lines); Step 7's answers file, rerun as `/tmp/answers-A3b.json`, is byte-identical to `/tmp/answers-A1.json`.
  - Review of c31ce24, measured (Release, Arm64, 16 processors, the class alone, three runs):

    | zero-width-space at the cap | before keep-first dedup | after |
    |---|---|---|
    | ScreenEnvelope allocated | 106.6 MB | 34.8 MB |
    | gen0/1/2 during the fastest large sample | 10/5/1 | 0/0/0 |
    | wall ratio, 262,144 / 32,768 bytes | about 15 idle, about 20 under load, 24.8 contended on CI | 7.87 to 8.42 |
    | allocation ratio | 7.98 | 8.24 (ScreenEnvelope), 8.39 (ScreenText) |

    Every other row read 7.3 to 8.6, and the slowest budget row 55 ms.
  - Review of c31ce24: the previous ruling's exemption, the zero-width-space row's wall ratio recorded and not asserted under D35 with a premise fact counting more than 10,000 HiddenText flags at the cap, is withdrawn with its premise fact. The fact would have been vacuous: keep-first dedup leaves the published count at 87,381 flags, so it stays green after its cause is gone, which is `R10_11_HomoglyphSubstitutionIsNotYetDetected`'s trap. The exemption had also failed its own falsification: a quadratic CPU spin planted in the hidden-text walk (`i / 4` iterations a hit, no allocation) read a wall ratio of about 50 while the budget row read 1,481 ms and the allocation ratio 7.98, so on a fast machine a CPU-quadratic regression on that path passed every gate. All 20 rows now assert the wall ratio at 24, with A3's reading stop at 16, and the allocation ratio at 9; none of those moved. The trailing asserted/recorded column is gone from `screen-scaling`'s rows. A caveat beside the ceiling: 24 alone would not catch a reversion to the pre-dedup path (about 15 idle, about 20 under load); the stop of 16 does.
  - Review of c31ce24: holding the collector off during a sample (hopper's G) was not adopted. The collections came from a live set the screener chose, about five flags a hit, which is work the server pays on every request, and R10.69 bounds screening work; excluding the collector would redefine the measure rather than discharge the requirement, and would leave a CPU regression on the path to the machine-dependent budget alone, the hole the spin exposed. Coalescing runs of hidden characters (D) changes the published counts and stays with D35; a second, CPU-time measure (T) was not needed.
  - Review of c31ce24: `ScreenerReferenceTests` closes the seam the cure opens. `ContentScreener` now assembles the injection detector's steps itself, so a third step added to `Scan` would silently never reach SCREEN. A reference screener built from public surface only (the screener before keep-first dedup, `GroupBy` and `First` at the end) is compared, outcome, flag sequence and detector versions, for `ScreenText` and `ScreenEnvelope`, on the red-team corpus (four files, each entry alone and after a line), on 2,000 generated inputs (a 64-bit LCG, seed 20261007: base64 carrying hidden characters and phrases, HTML comments, runs of hidden characters, despacing bait, emphasis, homoglyphs, credentials), and on a pin: a U+200B inside base64 flagged at the run's start, the case the corpus lacks.
  - Review of c31ce24, falsified (Release, the cost class or the reference class alone; each restored with a plain `cp`, rebuilt `--no-incremental`, green unpatched):
    - F-cpu, the `i / 4` spin inside `HiddenTextOffsets`: red, zero-width-space ScreenEnvelope wall ratio 75.6 (allocation 8.24), and its budget row 2,559 ms.
    - F-html, `NonBacktracking` removed from `HtmlComment()`: red, html-comment-open ScreenEnvelope 62.2, its budget row 2,950 ms, and the engine fact.
    - F-alloc, a quadratic allocation planted in the hidden-text walk (`new byte[i]` every 64th hit): red, zero-width-space ScreenEnvelope allocation ratio 37.47 (wall 9.89).
    - F-oracle, the hidden-text walk skipped for the base64-decoded view: red, the generated fact (seed 20261007, input 2: one HiddenText flag missing) and the pin.
    - F-dedup (informational), the pre-dedup sources restored: zero-width-space ScreenEnvelope 16.98 (gen 10/5/1, 106.6 MB), ScreenText 8.77; green against 24 and above the stop of 16, as the caveat says.
  - Review of c31ce24, CI evidence (Step 5's three dispatches, head 5d7a00a, X64, 4 processors): runs 37596130961, 37601395004 and 37606333636, every job green, the Test step including `ScreenerReferenceTests`. `screen-timings`: 20 rows each, slowest 111, 117 and 94 ms. `screen-scaling`: 20 rows each; zero-width-space 8.79/8.86, 9.14/9.05 and 9.40/8.81 (ScreenEnvelope/ScreenText); the largest wall ratio per run 8.86, 9.14 and 9.40, under the stop of 16 and the ceiling of 24; every allocation ratio at most 8.39; 0/0/0 collections on every row at the cap. A4 may begin.
- [ ] **Step 8.** Commit: `R10.69: every screening pattern runs on the linear engine, the high-entropy rule rewritten for it (D32)`.

### Task A4: The rationale cap (R10.68)

**Interface:**

```csharp
namespace Curia.Domain.Moderation;
public static class RationaleLimit
{
    /// R10.68 (errata G18). Measured in UTF-8 bytes; checked after authorization, before screening, before the post is looked up, before any write.
    public const int MaxUtf8Bytes = 4_096;
    /// The rationale's UTF-8 length when it is over the cap, or null.
    public static int? Over(string rationale) { var n = Encoding.UTF8.GetByteCount(rationale); return n > MaxUtf8Bytes ? n : null; }
}
```

Errors:
- `FlagErrors.RationaleTooLong(int bytes)` → `new("curia/flag/rationale-too-long", "The flag's rationale is longer than R10.68 permits", $"field=rationale bytes={bytes}: at most {RationaleLimit.MaxUtf8Bytes} UTF-8 bytes")`
- `ModerationRecordErrors.RationaleTooLong(int bytes)` → the same shape with `curia/moderation/rationale-too-long`.

**Red facts first:**

| Fact | Asserts |
|---|---|
| `FlagEndpointTests.R10_68_ARationaleAtTheCapIsAcceptedAndOneByteOverIsRefused` | 4,096 ASCII → 201; 4,097 → 422 `rationale-too-long`, detail `bytes=4097` |
| `FlagEndpointTests.R10_68_TheCapCountsUtf8BytesNotCharacters` | 1,365 × U+4E2D + `"a"` (4,096 bytes) → 201; plus `"ab"` (4,097) → 422 |
| `FlagEndpointTests.R10_68_AnOverlongRationaleIsRefusedBeforeItIsScreenedOrAPostIsRead` | `"ghp_" + 36 alphanumerics` padded with spaces to 5,000 bytes, against a post id that does not exist → `rationale-too-long`, not `rationale-rejected` and not `no-such-post` |
| `OperatorModerationTests.R10_68_AModerationReasonOverTheCapIsRefused` | 4,097 bytes → refused with `rationale-too-long`; the log has no new entry |
| `RationaleLimitTests` (Application) | `Over` returns null at 4,096 and 4,097 at 4,097, for ASCII and for a three-byte character |

**Steps:**
- [ ] Write the facts. Run them. Expected: RED on all but the at-cap rows.
- [ ] In `RaiseFlag.RecordAsync`, insert immediately after `:93` (the `RationaleRequired` return), before `ScreenText`:

  ```csharp
  // R10.68 (errata G18): before screening and before the post's stream is read.
  if (RationaleLimit.Over(rationale) is { } bytes)
      return Result<FlagRaised>.Fail(FlagErrors.RationaleTooLong(bytes));
  ```

  Insert the same in `ApplyModeration.RecordAsync` after `:89`, with `ModerationRecordErrors.RationaleTooLong`.
- [ ] In `ForumEndpoints.cs` (`:853`), add `"curia/flag/rationale-too-long" => StatusCodes.Status422UnprocessableEntity,`. In `src/Curia.Operator/Program.cs`, map the moderation slug wherever `rationale-rejected` is mapped: `grep -n 'rationale-rejected' src/Curia.Operator/Program.cs`.
- [ ] Run Application, Api and Operator facts. Expected: `Passed!`. Rerun the fuzzer. The 422 is carried by A4's facts, not the fuzzer, whose oracle asserts only no 5xx and a problem document. The ledger is unchanged, and the answers file (`CURIA_FUZZ_ANSWERS=/tmp/answers-A4.json`) differs from `/tmp/answers-A3.json` only in rows whose part is a `rationale` and whose variation is `over-cap` or a `long-*` longer than 4,096 bytes.
- [ ] Commit: `R10.68: a flag's or a moderation record's rationale is at most 4,096 UTF-8 bytes, refused before it is screened (D32)`.
  - Red run, Release, Api filtered to `R10_68`: `Failed: 4, Passed: 0`. Each flag fact failed on its over-cap row (`201`), after its at-cap row answered 201; the order fact answered `curia/flag/rationale-rejected`; the operator fact exited `Ok`. `RationaleLimitTests` (five facts) was red by not compiling: `RationaleLimit` did not yet exist.
  - Recorded as built: `src/Curia.Operator/Program.cs` maps no slug. `grep -n 'rationale-rejected'` finds nothing; every refusal leaves through `RefuseAsync` as `ExitCode.Refused`, so `curia/moderation/rationale-too-long` needs no mapping and none was added.
  - Recorded as built: the order fact sends the rationale under `credential_leak`, so the request is one the route would accept but for the rationale. The operator fact aims a 4,097-byte reason at a real post and asserts the post is still served.
  - Rerun: the whole Release suite with `CURIA_FUZZ_ANSWERS=/tmp/answers-A4.json` and `CURIA_FUZZ_FAILURES` set, eleven assemblies `Passed!`. The failures file is empty and `ExpectedFaults` is unchanged. Against `/tmp/answers-A3.json` (34,838 keys in both), seven answers changed, each `201` to `422 curia/flag/rationale-too-long`, each on `POST /v1/posts/{postId}/flags [plain] json:/rationale`: `over-cap`, and `long-comment`, `long-r` and `long-self` at 65,536 and 262,144.

### Task A4b: A flag spends a budget of its own, once per post and type (R7.22, R10.70; D34)

**Interfaces:**

```csharp
// src/Curia.Domain/Authorization/TierPolicy.cs
/// <summary>R7.22 (errata G18): flags per trailing 24 hours, beside the posting budget. Provisional.</summary>
public static int FlagsPerDay(PrincipalTier tier) => tier switch
{
    PrincipalTier.T0 => 10,
    PrincipalTier.T1 => 50,
    PrincipalTier.T2 => 200,
    PrincipalTier.T3 => int.MaxValue,   // "Negotiated", as PostsPerDay
    PrincipalTier.Anonymous => 0,
    _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a Table 11 tier"),
};

// src/Curia.Domain/Authorization/AccessPolicy.cs
public sealed record AuthorizationRequest(
    EvaluatedTier Tier, CredentialState CredentialState, ResourceKind Resource, ActionKind Action,
    int PostsToday = 0,
    int? FlagsToday = null);   // R7.22: required for flag/raise; null there is a failure, never zero

private static bool IsFlagRaise(AuthorizationRequest r) =>
    r.Resource is ResourceKind.Flag && r.Action is ActionKind.Raise;
```

Errors:
- `AuthorizationErrors.FlagCountMissing()` → `new("curia/authz/flag-count-missing", "The flag budget was not counted", "flag/raise was evaluated without FlagsToday (R7.22)")`
- `FlagErrors.AlreadyRaised(FlagKind kind, ServerTimestamp at)` → `new("curia/flag/already-raised", "This agent has already raised a flag of this type against this post", $"kind={FlagKinds.Wire(kind)} raised_at={at.Value:o}")`. Never the rationale.

**Red facts first:**

| Fact | Asserts |
|---|---|
| `AccessPolicyTests.R7_22_AFlagIsNotRefusedForASpentPostingBudget` | T0, `PostsToday: 3`, `FlagsToday: 0`, `flag`/`raise` → allowed |
| `AccessPolicyTests.R7_22_AFlagIsRefusedAtItsOwnBudget` | for T0, T1 and T2: `FlagsToday` = budget − 1 → allowed; = budget → deny `table-11/flag-budget-exhausted` |
| `AccessPolicyTests.R7_22_AFlagWithNoCountIsAFailureNotADecision` | `flag`/`raise` with `FlagsToday: null` → a failed `Result`, `curia/authz/flag-count-missing` |
| `AccessPolicyTests.R7_22_APostSpendsThePostingBudgetAndNotTheFlagBudget` | `question`/`create` at `PostsToday: 3`, `FlagsToday: 0` → `rate-budget-exhausted`; at `PostsToday: 0`, `FlagsToday: 10` → allowed |
| `TierPolicyTests.R7_22_TheFlagBudgetsAreThePublishedOnes` | reads the `**R7.22**` paragraph of `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (found by walking up as `PublishedTable11` finds the white paper, `:69`), takes the three numbers of "10 flags at T0, 50 at T1 and 200 at T2" by regex, and asserts `FlagsPerDay(T0, T1, T2)` equals them. It fails naming the paragraph if it is absent or yields other than three numbers. The first fact to read the errata: the numbers are published there and not yet in the white paper's Table 11 |
| `FlagEndpointTests.R7_22_AnAgentAtItsPostingBudgetMayFlag` | a T0 agent posts three questions, then flags another agent's post → 201 (403 `rate-budget-exhausted` today) |
| `FlagEndpointTests.R7_22_TheEleventhFlagInADayIsRefused` | a T0 agent flags ten distinct posts seeded by other agents → 201 each; the eleventh → 403, detail beginning `table-11/flag-budget-exhausted`; after `forum.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1))` → 201 |
| `FlagEndpointTests.R10_70_ASecondFlagOfOneTypeByOneRaiserOnOnePostIsRefused` | `spam` twice by one agent on one post → 201, then 409 `curia/flag/already-raised`, detail holding `kind=spam` and the first flag's `raised_at` and not the rationale; the log gained one `flag.committed` leaf and the store one row |
| `FlagEndpointTests.R10_70_AnotherTypeOrAnotherRaiserIsNotARepeat` | after `spam` by A: `incorrect` by A → 201; `spam` by B → 201 |
| `RefusalClassificationTests.R7_22_AFlagBudgetRefusalIsABudgetRefusal` (Client) | a 403 whose detail begins `table-11/flag-budget-exhausted` classifies as a budget refusal, not a tier refusal |

**Steps:**
- [ ] Write the facts. Run them. Expected: RED, except `R10_70_AnotherTypeOrAnotherRaiserIsNotARepeat`, which pins today's behaviour; the facts that name `FlagsToday` or `FlagsPerDay` fail to compile until they exist.
- [ ] `TierPolicy.FlagsPerDay`, `IsFlagRaise` and `FlagsToday`, as above.
- [ ] `AccessPolicy`: the posting-budget branch (`:214`–`:218`) becomes `tierDecision.IsPermitted && IsWrite(request.Action) && !IsFlagRaise(request) && request.PostsToday >= …`. Directly after it, if `tierDecision.IsPermitted && IsFlagRaise(request)`: `FlagsToday is null` → `Fail(AuthorizationErrors.FlagCountMissing())`; `FlagsToday >= TierPolicy.FlagsPerDay(tier)` → deny `table-11/flag-budget-exhausted`. Both stay before the quarantine intersection, as the posting budget is. `IsWrite`'s comment names the exception and R7.22.
- [ ] `ForumEndpoints.RaiseFlagAsync` takes `IFlagDetailStore details`. After the posture, `FlagsAsync(log, details, cancellationToken)` (`:1016`); return its problem if any; pass `FlagsToday: FlagsInBudgetWindow(flags, subject, now)`, a new helper beside `PostsInBudgetWindow` counting `RaisedBy` equal to the subject (ordinal) and `At` after `now - TimeSpan.FromDays(1)`. Its comment says why the log alone cannot count (R10.62).
- [ ] `RaiseFlag.RecordAsync`: after the no-such-post return (`:113`–`:114`) and before `_ids.Next()`, read `_events.ReadAllAsync(cancellationToken)` and `_details.ReadAllAsync(cancellationToken)` (each failure returned as it is), take `FlagDirectory.Join(log, details).Flags`, and return `FlagErrors.AlreadyRaised(earlier.Kind, earlier.At)` for the first `earlier` matching `string.Equals(f.PostId, postId, StringComparison.Ordinal) && string.Equals(f.RaisedBy, raisedBy, StringComparison.Ordinal) && f.Kind == kind`.
- [ ] `StatusFor` (`ForumEndpoints.cs:853`): `"curia/flag/already-raised" => StatusCodes.Status409Conflict,`.
- [ ] Client: `ForumClient.cs:456` classifies `table-11/flag-budget-exhausted` beside `rate-budget-exhausted`. Wherever the client or `curia-mcp` words the posting budget's refusal (`grep -rn 'rate-budget-exhausted' src/Curia.Client src/Curia.Client.Cli src/Curia.Mcp`), the flag budget gets its own sentence: today's *flag* budget, a trailing 24 hours.
- [ ] Confirm the fuzzer's flag row is the consumed form (A1's exemplar table). If A1 shipped the plain form, change it here.
- [ ] Run Domain, Application, Api, Client and Mcp tests in Release. Expected: `Passed!`. Rerun the fuzzer with `CURIA_FUZZ_ANSWERS=/tmp/answers-A4b.json`: the ledger is unchanged and the file is identical to `/tmp/answers-A4.json`, because the consumed row reaches neither rule. Otherwise stop and report the differing rows.
- [ ] Commit: `R7.22, R10.70: a flag spends a budget of its own and is raised once per post and type (D34)`.

### Task A5: Acceptance, by reverting D25's fixes (R14.10)

**The runner** is `/tmp/fuzz-accept.py`. It is not committed: after PR B, the fixes it reverts no longer exist. Every replacement carries the marker comment `/* ACCEPTANCE-PATCH */`. The runner refuses to start on a tree whose `src/` differs from HEAD or holds the marker. It sets a timeout on every subprocess, and it ends with a checked rebuild and an unpatched run.

**Before running, fill every empty expected route and part prefix in `CASES`** (cases 2c, 3, 4, 5, 7, 10 and 16) with the exact route and part address the case's D25 commit exercised, read from that commit's own test. No expectation may be empty, and the runner refuses one that is. If a case cannot be stated as a route and a part, stop and report.

```python
#!/usr/bin/env python3
import filecmp, json, os, pathlib, re, shutil, subprocess, sys
REPO = pathlib.Path(sys.argv[1]).resolve()
FILTER = "FullyQualifiedName~Curia.Api.Tests.Fuzz"
CASES = [  # (id, file, anchor, replacement, expected route, expected part prefix)
 ("1",  "src/Curia.Api/ForumEndpoints.cs", "var thread = string.IsNullOrWhiteSpace(rootPostId)", "var thread = rootPostId == \"no-such-root-id\"", "GET /v1/threads/{rootPostId}", "path:rootPostId"),
 ("2a", "src/Curia.Api/Issuer/TokenEndpoint.cs", "if (!http.HasFormContentType)", "if (http.ContentLength == -1)", "POST /oauth/token", "header:Content-Type"),
 ("2b", "src/Curia.Api/Issuer/TokenEndpoint.cs", "catch (InvalidDataException)", "catch (InvalidDataException) when (http.ContentLength == -1)", "POST /oauth/token", "form:"),
 ("2c", "src/Curia.Api/Issuer/TokenEndpoint.cs", "catch (IOException)", "catch (IOException) when (http.ContentLength == -1)", "POST /oauth/token", ""),  # body-truncated, multipart row
 ("3",  "src/Curia.AuthN/Dpop/JwkPublicKey.cs", "catch (CryptographicException)", "catch (CryptographicException) when (x.Length == -1)", "", "jws:proof:header/jwk/"),
 ("4",  "src/Curia.AuthN/Jwt/NumericDate.cs", "if (seconds < MinSeconds || seconds > MaxSeconds)\n            return Result<DateTimeOffset>.Fail", "if (seconds == long.MinValue + 7)\n            return Result<DateTimeOffset>.Fail", "", "jws:"),
 ("5",  "src/Curia.Api/Program.cs", "app.UseUtf8JsonBodies();", "// no charset guard (acceptance case 5)", "", "header:Content-Type;charset"),
 ("6",  "src/Curia.AuthN/Jwt/CompactJws.cs", "return root.ValueKind != JsonValueKind.Object", "return root.ValueKind == JsonValueKind.Undefined", "POST /oauth/token", "jws:proof:header"),
 ("7",  "src/Curia.AuthN/Jwt/CompactJws.cs", "if (!EveryStringDecodes(bytes))", "if (bytes.Length == -1)", "", "jws:"),
 ("8a", "src/Curia.AuthN/Dpop/DpopProof.cs", "if (CompactJws.IdentifierRefusal(jti, \"jti\", CompactJws.MaxJtiUtf8Bytes) is { } jtiError)", "if (jti == \"no-such-jti\" && CompactJws.IdentifierRefusal(jti, \"jti\", CompactJws.MaxJtiUtf8Bytes) is { } jtiError)", "", "jws:proof:claims/jti"),
 ("8b", "src/Curia.AuthN/ClientAssertionClaims.cs", "if (CompactJws.IdentifierRefusal(jti, \"jti\", CompactJws.MaxJtiUtf8Bytes) is { } jtiError)", "if (jti == \"no-such-jti\" && CompactJws.IdentifierRefusal(jti, \"jti\", CompactJws.MaxJtiUtf8Bytes) is { } jtiError)", "POST /oauth/token", "jws:assertion:claims/jti"),
 ("9",  "src/Curia.AuthN/ClientAssertionValidator.cs", "if (CompactJws.IdentifierRefusal(header.Kid, \"kid\", null) is { } kidError)", "if (header.Kid == \"no-such-kid\" && CompactJws.IdentifierRefusal(header.Kid, \"kid\", null) is { } kidError)", "POST /oauth/token", "jws:assertion:header/kid"),
 ("10", "src/Curia.AuthN/AccessTokenValidator.cs", "if (CompactJws.IdentifierRefusal(nonce, \"nonce\", MaxNonceUtf8Bytes) is not null)", "if (nonce == \"no-such-nonce\")", "", "jws:proof:claims/nonce"),
 ("11", "src/Curia.Api/Issuer/TokenEndpoint.cs", "catch (NotSupportedException)", "catch (NotSupportedException) when (http.ContentLength == -1)", "POST /oauth/token", "header:Content-Type;charset"),
 ("12", "src/Curia.Canon/Jws/DetachedJws.cs", "if (!AllStringsDecode(headerBytes))", "if (headerBytes.Length == -1)", "POST /v1/posts", "jws:post:header"),
 ("13", "src/Curia.Application/Ingest/IngestPipeline.cs", "if (string.IsNullOrWhiteSpace(protectedHeader!.Kid))", "if (protectedHeader!.Kid == \"no-such-kid\")", "POST /v1/posts", "jws:post:header/kid"),
 ("14a","src/Curia.Application/Ingest/IngestPipeline.cs", "if (envelope!.Board.Contains('\\0', StringComparison.Ordinal))", "if (envelope!.Board == \"no-such-board\")", "POST /v1/posts", "json:/envelope/board"),
 ("14b","src/Curia.Application/Ingest/IngestPipeline.cs", "if (envelope.Parent is not null && envelope.Parent.Contains('\\0', StringComparison.Ordinal))", "if (envelope.Parent == \"no-such-parent\")", "POST /v1/posts", "json:/envelope/parent"),
 ("15", "src/Curia.Application/Moderation/RaiseFlag.cs", "if (string.IsNullOrWhiteSpace(postId))\n            return Result<FlagRaised>.Fail(FlagErrors.NoSuchPost(postId));", "if (postId == \"no-such-post-id\")\n            return Result<FlagRaised>.Fail(FlagErrors.NoSuchPost(postId));", "POST /v1/posts/{postId}/flags", "path:postId"),
 ("16", "src/Curia.Api/Program.cs", "app.UseUnreadableRequests();", "// no problem documents for unread requests (acceptance case 16)", "", ""),
]
FAIL_FILE = pathlib.Path("/tmp/fuzz-failures.jsonl")
MARK = "/* ACCEPTANCE-PATCH */"
def run(cmd, timeout=5400): return subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, timeout=timeout, env={**os.environ, "CURIA_FUZZ_FAILURES": str(FAIL_FILE)})
def clean_tree(): return run(["git", "diff", "--quiet", "--", "src/"]).returncode == 0 and run(["grep", "-rl", "ACCEPTANCE-PATCH", "src/"]).returncode != 0
if not clean_tree(): sys.exit("tree differs from HEAD under src/ or holds ACCEPTANCE-PATCH: restore from /tmp/accept-*.orig by hand, then rerun")
if run(["dotnet", "build", "Curia.sln", "-c", "Release", "--no-incremental"]).returncode != 0: sys.exit("unpatched build failed")
if "Failed!" in run(["dotnet", "test", "tests/Curia.Api.Tests", "-c", "Release", "--no-build", "--filter", FILTER]).stdout: sys.exit("unpatched fuzz suite is not green")
failures = 0
for cid, rel, anchor, repl, route, part in CASES:
    path = REPO / rel; keep = pathlib.Path(f"/tmp/accept-{cid}.orig"); shutil.copyfile(path, keep)
    text = path.read_text()
    if not route or not part: print(f"{cid}: EXPECTATION EMPTY"); failures += 1; continue
    if text.count(anchor) != 1: print(f"{cid}: ANCHOR MISMATCH ({text.count(anchor)})"); failures += 1; continue
    try:
        path.write_text(text.replace(anchor, MARK + " " + repl))
        FAIL_FILE.unlink(missing_ok=True)
        b = run(["dotnet", "build", "Curia.sln", "-c", "Release", "--no-incremental"])
        if b.returncode != 0 or re.search(r"error CS\d+", b.stdout): print(f"{cid}: BUILD FAILED"); failures += 1; continue
        t = run(["dotnet", "test", "tests/Curia.Api.Tests", "-c", "Release", "--no-build", "--filter", FILTER])
        rows = [json.loads(l) for l in FAIL_FILE.read_text().splitlines() if l.strip()] if FAIL_FILE.exists() else []
        lines = [json.dumps(r) for r in rows if not r["ledgered"] and r["route"] == route and r["part"].startswith(part)]
        red = "Failed!" in t.stdout and bool(lines)
        print(f"{cid}: {'RED' if red else 'GREEN — the fuzzer missed it'}" + (f"  e.g. {lines[0].strip()[:200]}" if lines else ""))
        failures += 0 if red else 1
    finally:
        shutil.copyfile(keep, path)
        if not filecmp.cmp(keep, path, shallow=False) or run(["git", "diff", "--quiet", "--", rel]).returncode != 0:
            print(f"{cid}: DIRTY RESTORE"); failures += 1
fb = run(["dotnet", "build", "Curia.sln", "-c", "Release", "--no-incremental"])
final = run(["dotnet", "test", "tests/Curia.Api.Tests", "-c", "Release", "--no-build", "--filter", FILTER])
if fb.returncode != 0 or "Failed!" in final.stdout or not clean_tree():
    print("acceptance: FINAL STATE NOT CLEAN (rebuild, unpatched run, src/ diff or marker)"); failures += 1
print(f"acceptance: {len(CASES)} cases, {failures} not RED"); sys.exit(1 if failures else 0)
```

Before running, re-read each anchor at the commit. For case 4, read `NumericDate.cs:30`–`32`, whose two lines must match exactly. For case 3, if the variable named `x` is not in scope at `:68`, use `parameters.Q.X!.Length == -1`.

The failures file is JSONL (one FailureRow per line, Task A1). Every pass in FILTER appends to it, so it is read line by line and deleted before each case's run (review of 6cbfa9f). Checked once at the review of 6cbfa9f against files in a scratch directory: a two-row file reads 2 rows, a one-row file 1, and an absent file 0; the `json.loads` read it replaces raised `Extra data` on the two-row file.

- [ ] Run `python3 /tmp/fuzz-accept.py .` in the background, with output to `/tmp/accept.log`. Expected: `acceptance: 20 cases, 0 not RED`.
- [ ] **If a case is GREEN, the only allowed remedy is the one in spec §4.11.** Add a position-independent variation class to `Variations.Closed`, rerun the whole acceptance, and record in D33 the class, the case that forced it, and that case's red line. Never add a route- or part-specific row. If a third remedy round is needed, stop and report.
- [ ] Record all 20 result lines in the register (A7).
- [ ] Commit any closed-set additions: `R14.10: acceptance — the fuzzer alone goes red on every fix D25 recorded` (or with no code change, only the register record goes into A7).

### Task A6: Falsify every new gate in PR A

Use the same runner as A5, with `CASES` replaced. The filter and expected name are per case.

| # | File | Anchor → replacement | Must go red |
|---|---|---|---|
| F1 | `InjectionDetector.cs` (SecondPersonImperative attribute) | `RegexOptions.IgnoreCase \| RegexOptions.CultureInvariant \| RegexOptions.NonBacktracking)]` (the `:106` one only; make the anchor include the pattern's last line) → without `\| RegexOptions.NonBacktracking` | `R10_69_EveryScreeningPatternRunsOnTheLinearEngine`, the `"ai"`+spaces row, and `R10_69_ScreeningCostScalesLinearlyToTheStringCap`'s `ai-then-spaces` row |
| F2 | `SecretScanner.cs` (`ConnectionStringUriPassword` attribute) | drop `NonBacktracking` | engine fact; the fuzzer's `long-r-262144` budget row on `POST /v1/posts` |
| F3 | `SecretScanner.cs` | `var value = match.Groups["value"];` → `var value = match;` | `R10_25_TheHighEntropyRuleReportsTheValueNotTheKeyword` |
| F4 | `RationaleLimit.cs` | `MaxUtf8Bytes = 4_096` → `MaxUtf8Bytes = 4_097` | `R10_68_ARationaleAtTheCapIs…` |
| F5 | `RationaleLimit.cs` | `Encoding.UTF8.GetByteCount(rationale)` → `rationale.Length` | `R10_68_TheCapCountsUtf8BytesNotCharacters` |
| F6 | `RaiseFlag.cs` | `if (RationaleLimit.Over(rationale) is { } bytes)` → `if (rationale == "no-such-rationale" && RationaleLimit.Over(rationale) is { } bytes)` | `R10_68_AnOverlongRationaleIsRefusedBefore…` |
| F7 | `ApplyModeration.cs` | same as F6 | `R10_68_AModerationReasonOverTheCapIsRefused` |
| F8 | `ForumEndpoints.cs` | `app.MapGet(ReaderContract.WellKnownPath, GetReaderContract)` → `app.MapGet(ReaderContract.WellKnownPath + "-moved", GetReaderContract)` | `R14_10_EveryRegisteredRouteHasAnExemplarAndEveryExemplarARoute` |
| F9 | `ForumEndpoints.cs` | `app.MapGet("/health", () => Results.Ok(new { status = "ok" }))` → `app.MapGet("/health", () => Results.Problem(statusCode: 409))` | closed pass, naming "exemplar" |
| F10 | `Fuzz/Exemplars.cs` | remove `If-None-Match` from the `GET /v1/posts/{postId}` row | `R14_10_EveryHeaderAHandlerReadsIsVaried` |
| F11 | `Fuzz/Exemplars.cs` | remove `limit` from `/v1/search`'s query | `R14_10_EveryQueryParameterARouteReadsHasAnExemplarValue` |
| F12 | `Fuzz/ExpectedFaults.cs` | add `new("GET /health", "", "query:none", "nul", "plain", "D33-999")` | closed pass, naming "stale ledger row" |
| F13 | `SecretScanner.cs` (`HighEntropyAssignment`'s pattern) | `(?:^|[^A-Za-z0-9])(?:secret` → `(?<![A-Za-z0-9])(?:secret` | `R10_69_EveryScreeningPatternRunsOnTheLinearEngine`, on the generated type's `TypeInitializationException`. If the build fails instead, stop and report: that contradicts spec §9's scratch measurement, and G18's falsification line must change |

| F14 | `AccessPolicy.cs` | `&& !IsFlagRaise(request)` → `&& !(IsFlagRaise(request) && request.PostsToday == int.MinValue)` | `R7_22_AFlagIsNotRefusedForASpentPostingBudget`, `R7_22_AnAgentAtItsPostingBudgetMayFlag` |
| F15 | `TierPolicy.cs` | `PrincipalTier.T0 => 10,` → `PrincipalTier.T0 => 11,` | `R7_22_TheFlagBudgetsAreThePublishedOnes`, `R7_22_TheEleventhFlagInADayIsRefused` |
| F16 | `RaiseFlag.cs` | `&& f.Kind == kind` → `&& (f.Kind == kind \|\| f.FlagId != "no-such-flag")` | `R10_70_AnotherTypeOrAnotherRaiserIsNotARepeat` |
| F17 | `src/Curia.Api/ForumEndpoints.cs` (the inbox handler) | `Cursor: SearchCursor.Decode(http.Query["cursor"].ToString()),` → `Cursor: SearchCursor.Decode(string.Empty),` | `R14_10_NoVariationOfAnyPartIsAServerFaultAProblemlessRefusalOrOverBudget`, naming `coverage: RequestReadQuery lists GET /v1/inbox cursor`. GREEN on 7bf1160, because `/v1/search` reads `cursor` and the check compared names only: that is the discrimination |
| F18 | `tests/Curia.Api.Tests/Fuzz/RequestModel.cs` | `: throw new NotReSignableException();` → `: original;` | `R14_10_AnEnvelopeThatCannotBeCanonicalizedHasNoReSignedCopy` and `R14_10_OnlyAVariationWithNoCanonicalFormIsSuperseded`; in the same run, the closed pass names `superseded: no re-signed copy was superseded` |
| F19 | `tests/Curia.Api.Tests/Fuzz/Exemplars.cs` | the line `                envelope["code_blocks"] = new JsonArray(new JsonObject { ["language"] = "text", ["source"] = "x", ["license"] = "CC0-1.0" });` → an empty string | `R14_10_EveryEnvelopeMemberAndBodyPropertyIsAPart`, naming `envelope: code_blocks/*/language` |
| F20 | `src/Curia.Api/ForumEndpoints.cs` (the board listing) | `        return Results.Ok(posts\n            .Where(p => p.Board == board && servable(p.PostId) && Discussion(p))` → the same two lines, preceded by the line `        _ = http.Query["zzz-unlisted"].ToString();` | `R14_10_NoVariationOfAnyPartIsAServerFaultAProblemlessRefusalOrOverBudget`, naming `coverage: GET /v1/boards/{board}/posts reads the query parameter zzz-unlisted`. GREEN on 7bf1160 |
| F21 | `tests/Curia.Api.Tests/Fuzz/FuzzRun.cs` | `!failure.Budget && !string.Equals(failure.Part, "exemplar", StringComparison.Ordinal);` → `!failure.Budget;` | `R14_10_AnExemplarFailureIsNeverLedgerable` |
| F22 | `src/Curia.Api/ForumEndpoints.cs` | `foreach (var unsupported in (string[])["verification", "environment_version"])` → `foreach (var unsupported in (string[])["verification"])` | `R14_10_EveryRefusedQueryParameterIsRefusedByItsRoute`, naming environment_version. In a run that includes the closed pass, that pass also names `coverage: RefusedQuery lists GET /v1/search environment_version` |
| F23 | `tests/Curia.Api.Tests/Fuzz/SurfaceInventory.cs` | the line `        ("GET", "/v1/search", "environment_version", "curia/search/unsupported-filter"),` → an empty string | `R14_10_NoVariationOfAnyPartIsAServerFaultAProblemlessRefusalOrOverBudget`, naming `coverage: GET /v1/search reads the query parameter environment_version, and no exemplar of that route sends it`. This is the line the unpatched tree printed before `RefusedQuery` existed |
| F24 | `tests/Curia.Api.Tests/Fuzz/RequestModel.cs` | `return Canonical(variedEnvelope) is { } canonical ? JwsBuilder.Detached(header, canonical, entry.Signer) : throw new NotReSignableException();` → `return Canonical(variedEnvelope) is { } canonical && value is not VariedValue.Removed ? JwsBuilder.Detached(header, canonical, entry.Signer) : throw new NotReSignableException();` | `R14_10_OnlyAVariationWithNoCanonicalFormIsSuperseded`, naming `removed` on an envelope member other than `json:/envelope`. GREEN on 0277b97: that is the discrimination |
| F25 | `src/Curia.Api/ForumEndpoints.cs` (`ListBoardAsync`) | the two lines `        string board, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)\n    {` (the runner checks the anchor occurs exactly once) → the same two lines, followed by `        if (board.Any(c => c == '%' \|\| c > (char)0x7E)) throw new InvalidOperationException("falsification F25");` | Filter `FullyQualifiedName~RequestFuzzKestrelTests`, with `CURIA_FUZZ_FAILURES` set and deleted first. `R14_10_NoRawPathByteIsAServerFault`, naming `GET /v1/boards/{board}/posts path:board %FF: 500`. In the same run, the output must NOT contain `GET /v1/boards/{board}/posts: Kestrel refused every row`: that absence is the discrimination, since on 9411deb that line (then worded `Kestrel refused everything`) was printed, falsely. The failures file holds a row with route `GET /v1/boards/{board}/posts`, variant `kestrel`, part `path:board`, variation `%FF` |
| F26 | `tests/Curia.Api.Tests/Fuzz/RequestFuzzRandomTests.cs` | the line `                : thrown is nameof(TaskCanceledException) ? Oracle.Budget(stopwatch.Elapsed) + " (the client's 120 s timeout)" : "the send threw " + thrown;` → the same line, followed by `            reason ??= draw == 0 ? "planted (falsification F26)" : null;` (a null verdict at draw 0 becomes a planted failure; the existing `if (reason is null) continue;` is left as it is) | Filter `FullyQualifiedName~RequestFuzzRandomTests`, with `CURIA_FUZZ_FAILURES` set and deleted first. `R14_10_NoRandomStringInAnyPartIsAServerFault`, and the file holds exactly one row, with variation `random-0` and ledgered false. On 9411deb the file is absent: that is the discrimination |
| F27 | `tests/Curia.Api.Tests/Fuzz/RequestFuzzRandomTests.cs` | `Gen.Frequency((4, CleanStrings), (1, DecodingHostileStrings))` → `Gen.Frequency((4, DecodingHostileStrings), (1, DecodingHostileStrings))` | Filter `FullyQualifiedName~RequestFuzzRandomTests`. `R14_10_NoRandomStringInAnyPartIsAServerFault`, naming `got past the first parser`. On 9411deb's generator the same floor is red: that is the discrimination |
| F28 | `tests/Curia.Api.Tests/Fuzz/ExpectedFaults.cs` | `    internal static readonly ImmutableArray<FaultRow> Rows = [];` → `    internal static readonly ImmutableArray<FaultRow> Rows = [new("GET /v1/search", "plain", "query:q", "random-99999", "plain", "D33-998")];` (`GET /v1/search`'s exemplar variant is `plain`, and `query:q` is one of its parts; only the variation matters) | Filter `FullyQualifiedName~Curia.Api.Tests.Fuzz`. `R14_10_NoRandomStringInAnyPartIsAServerFault`, naming `D33-998 ... was not observed failing in the random pass (stale)`. In the same run, the closed-pass fact's output must NOT name `D33-998`. On 9411deb the random fact is green and the closed pass names it stale: both halves discriminate |
| F29 | `tests/Curia.Api.Tests/Fuzz/RequestFuzzRandomTests.cs` | the line `    private static readonly Gen<Unit> CleanUnitGen = Gen.Frequency(` → the same line, followed by `        (40, CodePoints(Gen.Int[0xFDD0, 0xFDEF])),` | Filter `FullyQualifiedName~RequestFuzzRandomTests`. `R14_10_NoRandomStringInAnyPartIsAServerFault`, naming `envelope draws got past ADMIT`. On 6cbfa9f's oracle the same patch leaves every floor green, because curia/admit/noncharacter counted as past the parser. That is the discrimination. Run at the review of 6cbfa9f: 5 of 132 envelope draws past ADMIT, while the whole-pass floor held (534 of 1,217) |
| F30 | `src/Curia.Api/ForumEndpoints.cs` (`ListBoardAsync`) | the same two-line anchor as F25 → the same two lines, followed by `        if (board.Any(c => c == '%' \|\| c > (char)0x7E)) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);` (without `ConfigureAwait`, CA2007 fails the build) | Filter `FullyQualifiedName~RequestFuzzKestrelTests`, with `CURIA_FUZZ_FAILURES` set and deleted first. `R14_10_NoRawPathByteIsAServerFault`, naming `GET /v1/boards/{board}/posts path:board %FF` and `the send threw`. The failures file holds a row with route `GET /v1/boards/{board}/posts`, variant `kestrel`, part `path:board`, variation `%FF` and status 0. On 6cbfa9f the fact fails with an OperationCanceledException and the file holds no such row. That is the discrimination. Expect about 30 s per hanging row: run at the review of 6cbfa9f, three rows hung (`%FF`, `%C0%80`, `%ED%A0%80`), each recorded with status 0 and `the send threw OperationCanceledException` |
| F31 | `src/Curia.Domain/Screening/InjectionDetector.cs` | the line `    private static readonly (Regex Pattern, RiskCategory Category)[] Rules =` → `    private static readonly Regex FalsificationProbe = new(@"\bai\s*[,:]?\s*you\b", RegexOptions.CultureInvariant);`, a blank line, then the anchor unchanged; and a second anchor, the two lines `    public static IEnumerable<RiskFlag> Scan(string derivedCopy)` and `    {` → the same two lines, followed by `        _ = FalsificationProbe.IsMatch("");` (without it, CA1823 fails the build on the unused field) | Filter `FullyQualifiedName~Curia.Architecture.Tests`. `R10_69_EveryRegexInShippedCodeIsAGeneratedPatternOnTheLinearEngine`, naming `InjectionDetector::.cctor`. GREEN on 1b0d423's `R10_69_EveryScreeningPatternRunsOnTheLinearEngine`: that is the discrimination. Run at the review of 1b0d423: RED, and the Domain fact GREEN under the same patch |
| F32 | `src/Curia.Domain/Screening/SecretScanner.cs` | the line `foreach (var match in HighEntropyAssignment().Matches(derivedCopy).Cast<Match>())` → `        _ = Regex.IsMatch(derivedCopy, "(a+)+$");`, then the anchor unchanged | Filter `FullyQualifiedName~Curia.Architecture.Tests`. The same fact, naming `Regex::IsMatch` (the site is `SecretScanner/<Scan>d__2::MoveNext`, `Scan` being an iterator). GREEN on the Domain reflection fact, `R10_69_EveryScreeningPatternRunsOnTheLinearEngine`: that is the discrimination. Run at the review of 1b0d423: RED, and the Domain fact GREEN under the same patch |
| F33 | `src/Curia.Api/ServerFault.cs` | the two lines `internal sealed partial class ServerFault(int status, Error error) : IResult` and `{` → the same two lines, followed by `    [System.Text.RegularExpressions.GeneratedRegex("a+b", System.Text.RegularExpressions.RegexOptions.CultureInvariant)] private static partial System.Text.RegularExpressions.Regex FalsificationProbe();` | Filter `FullyQualifiedName~Curia.Architecture.Tests`. The same fact, through (d), naming `ServerFault.FalsificationProbe`, and through (e), with a count of 16 (the fact checks every part before it asserts, so one message names both). The (c) allowance still passes the generated type the probe adds. Run at the review of 1b0d423: RED on (d) and (e) alone, no offender from (b) |

- [ ] **The runner, extended for F25–F30 (reviews of 9411deb and 6cbfa9f).** A case may set `CURIA_FUZZ_FAILURES` (F25, F26 and F30), and the runner deletes the file before the case's run, as A5's does. A case may name text that must be absent from `t.stdout`, and the runner counts the case not RED if it is present: F25's `GET /v1/boards/{board}/posts: Kestrel refused every row`, and F28's `D33-998` on the closed-pass fact's lines. A case may name rows the failures file must hold, and the runner counts the case not RED if it does not: F25's row, F26's single row and F30's row, as their table rows state. The failures file is read as A5's is, line by line (JSONL); F25's, F26's and F30's expected rows are matched on route, variant, part, variation and, for F26 and F30, ledgered false or status 0, as their table rows state.
- [ ] **The runner, extended for F31–F33 (review of 1b0d423).** A case may carry a second anchor, applied after the first in the same file (F31's). For F31 and F32 the runner also runs `--filter FullyQualifiedName~R10_69_EveryScreeningPatternRunsOnTheLinearEngine` in `Curia.Domain.Tests` under the patch and counts the case not RED unless that run is GREEN: the discrimination is the point of the case.
- [ ] Run. Expected: `falsify: 33 cases, 0 not RED`.
- [ ] **By hand, beside the runner (review of 95b38b8).** CI's `Screening cost, alone (R10.69)` step, run with a filter that matches nothing (`--filter "FullyQualifiedName~NoSuchScreeningCostTests"`) and `CURIA_SCREEN_TIMINGS` pointing at a deleted file: the 20-row check must go red. No source is patched, which is why it is not a runner case.
- [ ] **An observation, not a gate (review of 95b38b8).** Remove the `await WarmUp.Value…` lines from `ScreeningCostTests`, run the class alone in Release, and record the first timed row's jump against its warmed time. Nothing is required to go red: the warm-up's effect is recorded, not asserted.
- [ ] **Not falsified, and recorded as owed:** the restart clause (no fault that breaks only a restart could be planted without also breaking reads), and the Kestrel pass. Whether any path byte gets past Kestrel's parser is what A2's guard reports, and F25 and F30 falsify that guard's diagnostic and its failures-file row, for a fault and for a hang. A patch that makes a handler throw on a raw byte would also be found by the closed pass's percent-encoded rows, so the pass has no fault of its own to plant. The oracle's classification is held by OracleTests (ADMIT slugs reflected from CanonErrors, and the token endpoint's live proof refusals, missing and unreadable) as well as by F29.

### Task A7: The register and the documents (PR A)

- [ ] In `IMPLEMENTATION_PLAN.md`:
  - **D32** gets "Closed by the stage's PR A", with the A3 timings (the local reading and the CI reading, A3 Step 5), the corpus diff (empty), the versions, R10.68, and the facts and falsification cases, and the IL-wide engine fact (review of 1b0d423), falsified by F31-F33. The amplification paragraph stays open.
  - **D33** gets the red baseline (A1, A2) as instances `D33-1…n`, the acceptance's 20 lines, the closed-set additions if any, and "the ledger holds n rows; PR B empties it"; re-read the red-baseline bullet against Task A1 Step 6; they must state the same numbers. The ledger's stale rule is partitioned: `random-N` rows by the random pass, all others by the closed pass (review of 9411deb).
  - **D33** also records, as observed and not acted on (the first two from the review of 7bf1160, the third from the review of 9411deb, the fourth from the review of 6cbfa9f), these four texts verbatim; their file:line references are at 7bf1160, so re-read them against the tree when recording (after the review round, the header loop is `FuzzRun.cs:148`–`:152`):
    - "No variation adds an unknown envelope member. ADMIT accepts one, VERIFY signs it, SCREEN screens its name and PERSIST stores it (CanonicalStrings.cs:49; ContentScreener.cs:77, :92), and JsonReader.cs:357-378 caps a member name at 262,144 bytes, as it caps a value. Spec §4.10's closed set has no add-member class, and §4.11 admits one only to remedy an acceptance miss. The screening-cost exposure of a long author-chosen member name is covered structurally by R10.69 (every screening pattern on the linear engine), not by the fuzzer. Revisit if an acceptance case ever needs an inserted member."
    - "Header-read coverage is checked by name across the whole pass (FuzzRun.cs:139-143): a header read on route X is satisfied by an exemplar of route Y that varies it. The recorder now records header reads by route as well (`HeaderReads`), so a per-route check is one loop. It was not turned on in PR A because middleware reads headers such as `Authorization` and `DPoP` on anonymous routes, and per-route coverage would add those parts to every anonymous exemplar, which changes the plan's send counts and the A1 baseline. The next fuzzer change should measure the per-route difference from `HeaderReads` first, then decide."
    - "The Kestrel pass sends no credential and an empty body, so on five of its eight routes (POST /v1/posts/{postId}/accept, GET and POST /v1/posts/{postId}/flags, and both /v1/log/…/{index:long} routes) authentication, the body binder or routing answers before the path parser is reached. Raw 0xFF and 0x00 are refused by Kestrel on every route. The path parsers on those routes are reached only by the closed pass's authenticated, percent-encoded path variations (clause 5), over TestServer rather than Kestrel. An authenticated Kestrel pass needs a DPoP proof whose htu binds the raw request line. Revisit if Kestrel's percent-decoding is ever suspected to differ from TestServer's."
    - "The random pass's whole-pass floor counts a 2xx as past the first parser, and on POST /oauth/token a hostile string in a DPoP proof claim answers 200 because the endpoint reads no claim of the proof (D29): about 40 of 1,151 JSON and JWS draws at seed 0000001diyh1 (review of 6cbfa9f). Those draws reached nothing. The floor that carries information about ADMIT is EnvelopeDivisor on POST /v1/posts json:/envelope/*. When D29 closes, these draws become refusals, and Divisor should be re-measured."
  - **D34** gets "Closed by the stage's PR A, Task A4b", with R7.22 and R10.70, the facts, F14–F16, and the residuals (D7's fleet of identities; two identical flags raised at once).
  - **Traps** gain "27. A fuzzer derived from the request surface cannot find a cost keyed to a detector's anchor word; linearity is structural (R10.69)".
  - **What comes next** says PR B is next.
- [ ] Update `CLAUDE.md` and `README.md` where they list what works: the fuzzer, the screener and the cap.
- [ ] Re-read every file:line you wrote against the tree. Scan added lines for invisible characters (Global Constraints).
- [ ] Before committing: `git diff --quiet -- src/` succeeds, `grep -rn ACCEPTANCE-PATCH src/ tests/` prints nothing, and no `/tmp/accept-*.orig` differs from the file it was copied from. Otherwise stop: a reverted fix must never reach a commit.
- [ ] Commit: `Register: D32 closed, D33's base recorded and ledgered, acceptance recorded`.

### Task A8: Every gate, then PR A

- [ ] Run every command in `CLAUDE.md`'s gate list, in order, including `Curia.Architecture.Tests` in Debug after a Debug build, cargo, and the differential with `--fail-on-divergence`. Expected: every gate green, and eleven test assemblies.
- [ ] `git status --porcelain` must be empty after `but commit`, and `grep -rn ACCEPTANCE-PATCH src/ tests/` must print nothing.
- [ ] (PR A only) From the PR head's CI run, read the `CURIA_FUZZ_TIMINGS` artifact. If any route's slowest send exceeds 500 ms (a quarter of the 2,000 ms budget, which was measured on one machine), stop and report before merging. From the PR head's CI run, the separate step's artifact (`screen-timings`, written by `Screening cost, alone (R10.69)`), under the same rule: a missing file or one not holding exactly 20 rows is a stop, and a slowest row over 500 ms means stop and report before merging.
- [ ] `but push request-fuzzer-gate`, then open a PR with `gh pr create --base main`. The body names G18's R7.22, R10.68, R10.69, R10.70 and R14.10, the ledger size, and the acceptance result, and ends with the attribution lines.

---

## PR B — the cure (branch `boundary-types-fence`, from `main` after A merges)

### Task B1: The rule and the types (R11.34)

- [ ] **Before any change**, on the new branch at merged `main`: run the closed pass with `CURIA_FUZZ_ANSWERS=/tmp/answers-before-B.json`. This is the wire snapshot B3 compares against.

**Interface** (`src/Curia.Domain.Primitives/Boundary/StorableText.cs`):

```csharp
namespace Curia.Domain.Primitives.Boundary;

public static class StorableText
{
    public const string AbsentType = "curia/input/absent", BlankType = "curia/input/blank", NulType = "curia/input/nul",
        UnpairedSurrogateType = "curia/input/unpaired-surrogate", TooLongType = "curia/input/too-long";

    /// R11.34 (errata G18): the one rule. Order: absent, blank (when names), nul, unpaired surrogate, too long.
    public static Result<string> Check(string? value, string field, int maxUtf8Bytes, bool names)
    {
        if (value is null) return Fail(AbsentType, field);
        if (names && string.IsNullOrWhiteSpace(value)) return Fail(BlankType, field);
        if (value.Contains('\0', StringComparison.Ordinal)) return Fail(NulType, field);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c)) { if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; continue; } return Fail(UnpairedSurrogateType, field); }
            if (char.IsLowSurrogate(c)) return Fail(UnpairedSurrogateType, field);
        }
        var bytes = Encoding.UTF8.GetByteCount(value);
        return bytes > maxUtf8Bytes
            ? Result<string>.Fail(new Error(TooLongType, "The value is longer than its store holds", $"field={field} bytes={bytes}: at most {maxUtf8Bytes} UTF-8 bytes"))
            : Result<string>.Ok(value);
    }

    private static Result<string> Fail(string type, string field) =>
        Result<string>.Fail(new Error(type, "The value cannot be stored", "field=" + field));
}
```

**The type template.** Every type follows this, with its `Name`, `MaxUtf8Bytes`, `names` flag and default field taken from spec §4.6:

```csharp
public sealed record AgentId
{
    public const int MaxUtf8Bytes = 1_024;
    public string Value { get; }
    private AgentId(string value) => Value = value;
    public static Result<AgentId> Parse(string? value, string field = "agent_id") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new AgentId(v));
    public override string ToString() => Value;
}
```

`PostId.Parse` differs:

```csharp
public static Result<PostId> Parse(string? value, string field = "post_id") =>
    StorableText.Check(value, field, 26, names: true).Bind(v =>
        Ulid.Parse(v).TryGetValue(out var u, out _) && string.Equals(u.ToString(), v, StringComparison.Ordinal)
            ? Result<PostId>.Ok(new PostId(v))
            : Result<PostId>.Fail(new Error("curia/input/not-a-post-id", "The value is not a post id", "field=" + field)));
```

`Rationale` (in `Curia.Domain.Moderation`) uses `RationaleLimit.MaxUtf8Bytes` with `names: true`. Its `too-long` maps to A4's slugs.

**Red facts first** (`tests/Curia.Domain.Primitives.Tests/Boundary/StorableTextTests.cs`):

| Fact | Asserts |
|---|---|
| `R11_34_TheRuleRefusesInItsOrder` | theory over `(null, "", " ", "a\0b", "\uD800" (as `(char)0xD800 + ""`), "\uDC00x", cap+1)` → the five types in order |
| `R11_34_APairedSurrogateIsWellFormed` | U+1F600 accepted; counted as 4 bytes |
| `R11_34_TheCapIsUtf8BytesAndInclusive` | at the cap OK, cap+1 refused, for ASCII and for a three-byte character |
| `R11_34_ANonBlankRuleAcceptsWhiteSpace` | `names: false` accepts `" "` |
| `R11_34_ARefusalNeverEchoesTheValue` | CsCheck over strings: no `Detail` contains the value when its length ≥ 4 |
| per type | `Parse` caps, and `PostId` refuses lowercase, 25 or 27 characters, and an `I`/`L`/`O`/`U` |

- [ ] Run. Expected: compile errors (the types do not exist yet). Write the types, then run. Expected: `Passed!`.
- [ ] Commit: `R11.34: one rule for a caller's string, and the types a port takes`.

### Task B2: AuthN and its adapters take types

**Signatures that change:**

| Before | After |
|---|---|
| `IReplayCache.TryInsertAsync(string jti, DateTimeOffset, CT)` | `TryInsertAsync(Jti jti, DateTimeOffset, CT)` |
| `IDpopNonceStore.IssueAsync(CT) : Result<DpopNonce>` | `: Result<IssuedDpopNonce>` |
| `IDpopNonceStore.IsCurrentAsync(string nonce, CT)` | `IsCurrentAsync(DpopNonce nonce, CT)` |
| `record DpopNonce(string Value, DateTimeOffset ExpiresAt)` (AuthN.Ports) | `record IssuedDpopNonce(DpopNonce Value, DateTimeOffset ExpiresAt)` |
| `IAgentKeyResolver.ResolveAsync(string agentId, string kid, ServerTimestamp, CT)` | `ResolveAsync(AgentId agentId, Kid kid, ServerTimestamp, CT)` |
| `IJwsKeyResolver.ResolveAsync(string kid, CT)` | `ResolveAsync(Kid kid, CT)` |
| `JwkPublicKey.ToPublicKeyMaterial(string kid)` | `ToPublicKeyMaterial(Kid kid)` (or keep `string` with a row if `kid` is the Forum's own; read the callers) |
| `DpopProofClaims(… string Jti, … string? Nonce)` | `(… Jti Jti, … DpopNonce? Nonce)` |
| `ClientAssertionClaims(string Iss, string Sub, string Aud, string Jti)` | `(…, Jti Jti)` |
| `AccessTokenClaims(…, string Jti, …)` | `(…, Jti Jti, …)` |
| `ClientAssertionValidationContext(string TokenEndpoint, string ExpectedSubject)` | `(string TokenEndpoint, AgentId ExpectedSubject)` |

**Steps:**
- [ ] Delete `CompactJws.IdentifierRefusal` and `MaxJtiUtf8Bytes`. At each former call site (`DpopProof.cs:55`, `ClientAssertionClaims.cs:30`, `ClientAssertionValidator.cs:60`, `AccessTokenValidator.cs:187`), call `Jti.Parse`, `Kid.Parse` or `DpopNonce.Parse`. Map a failure with:

  ```csharp
  internal static Error AsMalformed(Error primitive, string name, int? max) =>
      AuthNErrors.Malformed($"'{name}' must be a non-empty string{(max is { } b ? $" of at most {b} UTF-8 bytes" : "")} holding no U+0000");
  ```

  This goes in `CompactJws`, and its text is byte-identical to the old one. The nonce maps to `AuthNErrors.NonceStale()` as before. A `kid` maps with `max: null`, as `IdentifierRefusal(header.Kid, "kid", null)` did, and its `too-long` is answered exactly as an unregistered `kid` is answered today (spec §4.7).
- [ ] Infrastructure: in `PostgresReplayCache`, `PostgresDpopNonceStore` and `PostgresAgentKeyStore`, take the types, pass `.Value` to Npgsql, and **delete** each `ArgumentException.ThrowIf*` whose parameter is now typed. List them with `grep -n 'ArgumentException.ThrowIf' src/Curia.Infrastructure/*.cs` before and after, and put both lists in the commit body. Do the same for the in-memory adapters.
- [ ] Run AuthN, Infrastructure and Api tests. Expected: `Passed!`. Every existing jti, kid and nonce detail assertion passes unchanged. If one fails, the mapping text is wrong; do not edit the test.
- [ ] Commit: `R11.34: a jti, a kid and a nonce reach AuthN's ports as types, and IdentifierRefusal is gone`.

### Task B3: Application, ingest, use cases and hosts take types

**Signatures that change:**

| Before | After |
|---|---|
| `IAuthorKeyResolver.ResolveAsync(string agentId, string kid, …)` | `(AgentId, Kid, …)` |
| `IAuthorKeyRegistry.EnrollAsync(string agentId, …)`, `KeysForAsync(string agentId, CT)` | `AgentId` |
| `IIngestPipeline.VerifyAsync(…, string principalAgentId, …)` | `AgentId principalAgentId` |
| `IVectorIndex.UpsertAsync(string digest, string postId, …)` | unchanged; `digest` and `postId` both get rows (Forum-minted and stored: `EmbeddingIndexer.cs:52` passes a projected post's id, and read-back is never parsed; spec §4.8, precedence) |
| `FlagDetail(string EventId, string PostId, string RaisedBy, string Rationale, string Salt)` | `(string EventId, PostId PostId, AgentId RaisedBy, string Rationale, string Salt)`; three rows |
| `RaiseFlag.RecordAsync(string postId, string raisedBy, FlagKind, string rationale, CT)` | `(PostId, AgentId, FlagKind, Rationale, CT)` |
| `ApplyModeration.RecordAsync(string postId, ModerationEffect, FlagKind, string rationale, ActorId, CT)` | `(PostId, …, Rationale, ActorId, CT)` |
| `AcceptAnswer.RecordAsync(string threadRoot, string answerId, string acceptedBy, …)` | `(PostId, PostId, AgentId, …)` |
| `AttestOwner.RecordAsync(string agentId, …, string reason)` | `(AgentId, …, string reason)`; `reason` gets a row |
| `EnrollAgent.RecordAsync(string agentId, …)`, `EnrollIdentity.EnrollAsync(string agentId, …)` | `AgentId` (and `Kid` where a `kid` is passed) |
| `LogBoundKeys.ResolveAsync(string, string)`, `KeySetAsync(string)` | `(AgentId, Kid)`, `(AgentId)` |
| `EnrollmentBinding.Find(string agentId)`, `For(string kid)` | `AgentId`, `Kid` |
| `KeyEnrollment.Decide(…, string agentId, …)`, `ReservedIdentifiers.IsReserved(string)` | `AgentId` |
| `PostProjector.Thread(ImmutableArray<PostView>, string rootPostId)` | `PostId` |
| `PostureQuery.Of`, `InboxSelector.Select`, `AgentStandingProjector.PostureOf`, `VerificationProjector.VerifiedFindingsBy` | `AgentId` |
| `SearchQuery(string Text, string? Board, string? Author, …)` | `(string Text, BoardName? Board, AgentId? Author, …)`; `Text` gets a row |
| `PostEnvelope` members `Board` and `Parent` as read at ingest | unchanged in Domain; `IngestPipeline.VerifyAsync` parses `BoardName` and `ParentReference` instead of `:100`/`:102` |

**Steps:**
- [ ] Ingest: replace `IngestPipeline.cs:100`–`:103` with `BoardName.Parse(envelope.Board, "board")` and `ParentReference.Parse(envelope.Parent, "parent")` (when non-null), mapping any failure to `IngestErrors.UnstorableMember(member)`. Replace `:121`'s blank check with `Kid.Parse(protectedHeader.Kid, "kid")`, mapped to `AuthorKeyErrors.NotRegisteredToAgent(envelope.Author, protectedHeader.Kid)`.
- [ ] `RaiseFlag`: delete `:83`, `:84` and `:89`–`:90`; `postId` is typed now. `Rationale` replaces `RationaleRequired` and the A4 length check, mapped as follows: `blank`/`absent` → `RationaleRequired`, `too-long` → `RationaleTooLong`, `nul` → `FlagDetailRules.Unstorable()`. The order stays: rationale before screening, before any read.
- [ ] Hosts (`ForumEndpoints`, `TokenEndpoint`, `ActaEndpoints`, `Curia.Operator/Program.cs`): parse where the string is first used, after authentication and authorization, never at the handler's first line, mapping each failure to the response the route already gives (spec §4.7). An unauthenticated request with a malformed path id still answers 401. Enrollment keeps `ForumEndpoints.cs:418`–`:450` unchanged and constructs `AgentId` and `Kid` after them.
- [ ] `GetJwks` (`ForumEndpoints.cs:1292`): bind `string? agent`, parse it as `AgentId`, and answer `absent` and `blank` with the existing `curia/keys/agent-required` 400; every other failure as the route answers an unknown agent today. Red first: `JwksEndpointTests.R11_34_AJwksRequestWithNoAgentIsAskedForOne` — `GET /v1/jwks` with no query → 400 `curia/keys/agent-required` (today `curia/request/unreadable`). This closes the D33 bullet that records it (the exercise's audit, finding 5).
- [ ] Delete every `ArgumentException.ThrowIf*` in `src/Curia.Application` whose parameter is now typed. Record the before and after lists from `grep -rn 'ArgumentException.ThrowIf' src/Curia.Application` in the commit body.
- [ ] Rerun the fuzzer. Every ledger row should now pass and therefore fail as stale. Delete each one, citing the type that closed it. A row that still fails and is not a string reaching a port (for example a framework reader) gets fixed at its reader, as `JsonCharset` was, and is recorded under D33.
- [ ] Run the whole suite in Release. Expected: `Passed!` in all eleven assemblies, and `ExpectedFaults.Rows` is empty.
- [ ] Run the closed pass with `CURIA_FUZZ_ANSWERS=/tmp/answers-after-B.json`. It must equal `/tmp/answers-before-B.json` except in rows that were ledger rows, and in `GET /v1/jwks`'s `query:agent` `removed` rows, which move from `curia/request/unreadable` to `curia/keys/agent-required` (spec §4.7). Any other difference is a changed answer or a changed order of answers: stop and report it.
- [ ] Commit: `R11.34: every port and host-called use case takes a caller's identifier as a type; the ledger is empty (D33)`.

### Task B4: The fence (R11.34)

**Interfaces:** `PortStringFenceTests` and `PortStringAllowlist`, exactly as spec §4.8. The scan scope comes from `IlCallSites.MemberReferencesInto(string hostAssemblyPath, params string[] targetAssemblies)`, which returns `(string TypeFullName, string MemberName)` pairs read through `MetadataReader.MemberReferences`, keeping those whose parent, walked through nested `TypeReference`s to the outermost type and through a `TypeSpecification` to its generic type definition, has a `ResolutionScope` that is an `AssemblyReference` named in `targetAssemblies`. Assert the sentinels of spec §4.8 are in scope.

**Red fact first:**
- [ ] Write the fact with an **empty** allowlist. Run it. Expected: RED, listing every remaining string parameter.
- [ ] Classify each by the rule in spec §4.8. Write one row per row-classified entry, with the reason in the rule's words. **Retype** any entry the rule calls retype, using B3's pattern, and record it in the commit body as a signature that changed. Expected rows include at least:
  - `ITextEmbedder.Embed(text)`
  - `HashedNGramEmbedder.Embed(text)`
  - `IVectorIndex.UpsertAsync(digest)`
  - `FlagDetail..ctor(EventId, Rationale, Salt)`
  - `SearchQuery..ctor(Text)`
  - `AttestOwner.RecordAsync(reason)`
  - `CitationCheck.IsDigest(value)` and `Resolve(requested)`
  - `VerificationFold.LevelOf` and `StateOf(digest)`
  - `ActaLog.IndexOf(eventId)`
  - `ClientAssertionValidator.ValidateAsync(assertion)`
  - `CompactJws.Split(compact)`
  - `IncomingRequest..ctor(Authorization, DpopProof, HttpMethod, CanonicalUrl)`
  - `AccessTokenValidationContext..ctor(ConfiguredIssuer, ResourceServer)`
  - `ClientAssertionValidationContext..ctor(TokenEndpoint)`
  - `"*"` rows for each `*Errors` type the hosts call
- [ ] Run. Expected: `Passed!`.
- [ ] Commit: `R11.34: a port's string parameter fails the build without a reason`.

### Task B5: The JSON reader rule (R11.34)

**Interface:** `IlCallSites.CallSites(string assemblyPath)` returns `(string CallerType, string CalleeType, string CalleeMember)`. It walks every `MethodDefinition` with a body, decodes IL with `BlobReader` and `ILOpCode`, reads the 4-byte token operand of `call`/`callvirt`/`newobj`, and resolves `MemberReference`/`MethodDefinition`/`MethodSpecification` to the callee's declaring type and name. It skips every other operand by its encoded size, including the `switch` table, and asserts that each body's reader ends exactly at the body's length (a decoder that loses its place would otherwise read nothing and pass). `CallerType` is the outermost declaring type.

**Steps:**
- [ ] Write `R11_34_ACallersJsonIsReadOnlyThroughAGuardedReader` with an empty allowlist. Run. Expected: RED, listing the call sites, which should be those in `CompactJws`, `DetachedJws`, `JsonReader`, `AccessTokenClaims`, `DpopProof` and `ActaEndpoints`.
- [ ] Add the rows from spec §4.9, one per (type, calling method, callee), each with its reason. Include the extended banned callees from spec §4.9.
- [ ] Write `R11_34_EveryBodyAHostBindsIsAListedType` (spec §4.9): the `IAcceptsMetadata.RequestType` set over the host's endpoints equals `{EnrollRequest, FlagRequest, BatchRequest}`, each with its reason. Falsify it in B6 (FB8).
- [ ] Any other site found: route it through `CompactJws`, `DetachedJws` or `JsonReader`, never through a new row.
- [ ] Non-vacuity: assert the scan read at least one call site in `Curia.AuthN`.
- [ ] Run in Release **and** in Debug after a Debug build, because IL differs between them (D16).
- [ ] Commit: `R11.34: a caller's JSON is read only through a guarded reader`.

### Task B6: Falsify PR B

| # | Anchor → replacement | Must go red |
|---|---|---|
| FB1 | `IReplayCache`: `Task<Result<bool>> TryInsertAsync(Jti jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);` → add `string? note,` before `CancellationToken` (fix both adapters, or the build fails, which counts as BUILD FAILED and not RED; patch the adapters in the same case) | fence |
| FB2 | `PortStringAllowlist.cs`: the first row's member name + `"X"` | fence (stale row) |
| FB3 | `ForumEndpoints.GetJwks` first line: insert `_ = System.Text.Json.JsonDocument.Parse("{}");` | JSON rule |
| FB4 | `StorableText`: `if (char.IsLowSurrogate(c)) return Fail(UnpairedSurrogateType, field);` → `if (char.IsLowSurrogate(c) && field == "no-such-field") …` | `R11_34_TheRuleRefusesInItsOrder` |
| FB5 | `StorableText`: `if (value.Contains('\0', StringComparison.Ordinal))` → `if (value == "no-such-value")` | primitives fact; fuzzer (`jti` `nul` → 22021 → 500) |
| FB6 | `Jti.MaxUtf8Bytes = 256` → `= 1_000_000` | fuzzer (`jws:proof:claims/jti` `long-*-65536` → 54000 → 500) |
| FB7 | `PostId.Parse`: `string.Equals(u.ToString(), v, StringComparison.Ordinal)` → `true \|\| string.Equals(…)` is a constant expression and is forbidden; use `string.Equals(u.ToString(), v, StringComparison.OrdinalIgnoreCase)` | `PostId` lowercase fact |

Add **FB8**: in `ForumEndpoints.cs`, add a route whose handler takes a new body record (for example `app.MapPost("/v1/fb8", (Fb8Request r) => Results.Ok())` with `public sealed record Fb8Request(string X);`). It must turn `R11_34_EveryBodyAHostBindsIsAListedType` red, and it must also turn the fuzzer's route fact red.

- [ ] Run. Expected: `falsify: 8 cases, 0 not RED`.

### Task B7: The register and the documents (PR B)

- [ ] **D33:** closed by PR B. Record the types, the fence, the JSON rule, the empty ledger, the falsification cases, and the before/after `ThrowIf*` lists.
- [ ] **D25's closure note:** the class is closed by R11.34 and gated by R14.10.
- [ ] **What comes next:** rotation, with D29 and M5. Rotation's `kid` ports take `Kid` from their first line.
- [ ] `CLAUDE.md` loses "CS-8 has never been built". Add `Curia.Domain.Primitives/Boundary` to the topology.
- [ ] Scans: invisible characters; re-read every file:line you wrote.
- [ ] Commit: `Register: D33 closed by R11.34; the ledger is empty`.

### Task B8: Every gate, then PR B

- [ ] Same as A8, for branch `boundary-types-fence`. The PR body names G18's R11.34 and the signatures that changed (B2 and B3 tables).
