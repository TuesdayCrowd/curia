# Keys That End: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-11-keys-that-end.md`

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. **Every subagent runs on Opus** (`model: "opus"`).

**Goal:** Close **D29** and **M5**. Then build G21: rotation, self-retirement, the per-identity lifecycle, R5.22, R6.56, and the three readers folding the lifecycle. Then build the operator's compromise declaration and disputed content.

**Architecture:**
- **PR 0** (branch `token-proof-verified`): Tasks 0.0–0.5. No requirement applied, and no event, table or grant changes; G21 is committed as proposed.
- **PR 1** (branch `key-rotation`): Tasks 1.0–1.12. Opened from `main` after PR 0 merges, and only after GitButler's target base is the merged `main` and `but status` shows none of PR 0's commits.
- **PR 2** (branch `key-compromise`): Tasks 2.0–2.7, after PR 1 merges, under the same base rule.
- No `db/` migration (spec §4.2). New Acta payloads are frozen once appended (R4.38); vectors land before any producer.

**Tech stack:** .NET 10, C# 14, xUnit v3, CsCheck 4.8.0, Npgsql + Postgres 18 with pgvector, Rust (`curia-testis`), python3, GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-10-11-keys-that-end-design.md`.

## Global Constraints

**Build and test**
- `dotnet build Curia.sln -c Release` reports 0 warnings. Tests run with `-c Release`, and Architecture tests also run in Debug (D16).
- Export `CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"` and never write the username out.
- Build `curia-testis` first (`cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis`) and export `CURIA_TESTIS_BIN`.
- Count assemblies, never totals: **eleven** must appear. Record each assembly's count at each PR's base, and never quote counts from this plan.
- Read gate output only through `grep -E "Passed!|Failed!"`. Never `head` it.
- Long runs go to the background with output to a file, which you read when the run ends. Never `tail -f`.

**Invariants**
- R15.1's set does not move.
- No content is normalized; a statement is rejected, never repaired.
- `Curia.Domain` depends on nothing new.
- Every new port parameter carrying a caller's identifier is a boundary type (`AgentId`, `Kid`, or a new type in `Curia.Domain.Primitives/Boundary`). The R11.34 fence must stay green with **no new allowlist row**.
- A refusal names a field and a limit, never a key, a statement or a signature.
- The store is never written by retirement or compromise.

**Numbering:** G21; R4.38, R4.39, R5.22, R6.56, R6.57, R7.23; R4.18 (rev.), R4.31 (rev. 2), R4.35 (rev.), R6.26 (rev.), R6.30 (rev.), R6.54 (add.). Task 0.0 re-derives these and **stops** if the tree disagrees.

**Characters:**
- No added line may contain a character of category Cf, Zl, Zp or Co, or Cc other than LF and TAB.
- Write hostile values as escapes.
- Each PR's register task scans for this.

**Version control:**
- Use `but` only. Never `git commit`, `checkout`, `stash`, `rebase`, `restore` or `reset`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Pass them with `-m "$(printf '…')"`.
- No usernames, IPs or home paths in any tracked file.
- After every `but commit`, `git status --porcelain` must be empty before you claim the commit.

**Falsification discipline (trap 28):**
- A suite is RED only on its own `Failed!` line naming the expected test or row.
- Each anchor is the literal text this plan pins, counted exactly once against the tree after the PR's last code task, or **stop**.
- Restore by plain copy (`shutil.copyfile`), prove the restore with `filecmp.cmp` and `git diff --quiet`, rebuild with `--no-incremental`, and run unpatched once. No patch is a constant expression.

## Review Focus

1. **PR 0.** The token endpoint and the resource server run one validator. The resource path's order and error types are byte-identical to before. No refusal from `/oauth/token` carries `detail`.
2. **PR 1.**
   - Every lifecycle append is conditioned on the identity stream's version.
   - Every post append is conditioned on its author stream's version.
   - Statements are read from canonical bytes, and every entry member equals its statement's.
   - A retirement can be signed only by the key it retires.
   - The fence has no new row.
   - Readers verify rotations recursively, and check the link after the post.
3. **PR 2.**
   - The declaration verifies under the log key set and never under an agent key.
   - `t_c` is floored at the binding.
   - The cascade is transitive.
   - Disputed is never collapsed into verified or failed.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `docs/superpowers/{specs,plans}/2026-10-11-keys-that-end*` | spec, plan | 0.0 |
| `src/Curia.AuthN/Dpop/DpopProofValidator.cs` (new) | one proof validator | 0.1 |
| `src/Curia.AuthN/AccessTokenValidator.cs` | calls it | 0.1 |
| `src/Curia.Api/Issuer/TokenEndpoint.cs` | calls it; `OAuthRefusal` | 0.1, 0.2 |
| `src/Curia.Api/Issuer/OAuthRefusal.cs` (new) | M5 | 0.2 |
| `tests/Curia.Api.Tests/TokenEndpointProofTests.cs` (new), `TokenEndpointRefusalTests.cs` (new) | D29, M5 facts | 0.1, 0.2 |
| `tests/Curia.Api.Tests/Fuzz/*` | D29's off-curve rows now refused | 0.1 |
| `tools/GenerateEnvelopeFixtures/{Fixtures.cs, Lifecycle.cs (new)}`, `conformance/acta/{key-rotated-entry,key-retired-entry,key-bound-entry-es256}/`, `conformance/lifecycle/*`, `conformance/index.json` | frozen formats | 1.1 (acta), 1.9 (lifecycle) |
| `tests/Curia.Canon.Tests/Vectors/ActaLeafVectorTests.cs`, `rust/curia-testis/tests/vectors.rs` | leaf recomputation of the new vectors | 1.1 |
| `src/Curia.Application/Ports/IEventStore.cs`, `src/Curia.Infrastructure/PostgresEventStore.cs`, the in-memory store, `src/Curia.Domain/DomainErrors.cs` | `StreamCondition` | 1.2 |
| `src/Curia.Domain/Keys/KeyStatements.cs` (new) | statement read/verify (pure) | 1.3 |
| `src/Curia.Application/Credentials/{KeyLifecycle.cs (new), EnrollmentBinding.cs, LogBoundKeys.cs, RotateKey.cs (new), RetireKey.cs (new), EnrollIdentity.cs}` | lifecycle, R4.35 (rev.), R4.31 (rev. 2), use cases | 1.3, 1.4 |
| `src/Curia.Application/Ingest/{IngestPipeline.cs, IngestPhases.cs, IngestErrors.cs}`, `src/Curia.Api/ForumEndpoints.cs` (`SubmitAsync`) | R6.56 | 1.5 |
| `src/Curia.Domain.Primitives/Boundary/{StatementText.cs, DetachedSignature.cs}` (new) | boundary types | 1.4 |
| `src/Curia.Domain/Authorization/{ResourceAction.cs, ResourceActionModel.cs, TierPolicy.cs}`, `curia-agent-forum-WHITEPAPER.md` (Table 10 rows; owner question 1) | Table 10, R7.23 | 1.6 |
| `src/Curia.Api/{ForumEndpoints.cs, Jwks.cs}` | routes, key set | 1.6 |
| `src/Curia.Api/Issuer/TokenIssuer.cs`, `src/Curia.AuthN/{AccessTokenClaims.cs, AccessTokenValidationContext.cs, AccessTokenValidator.cs, AuthNErrors.cs}`, `src/Curia.AuthN/Ports/IAgentKeyLiveness.cs` (new), `src/Curia.Api/Adapters/LogKeyLiveness.cs` (new) | R5.22 | 1.7 |
| `src/Curia.Client/{ProfileStore.cs, ForumClient.cs, KeyActs.cs (new), ForumDocuments.cs}`, `src/Curia.Client.Cli/{Program.cs, Hints.cs, Help.cs}` | client verbs | 1.8 |
| `src/Curia.Client/{ActaCheck.cs, PostVerifier.cs, SignatureCheck.cs, Passage.cs, LifecycleCheck.cs (new)}`, `src/Curia.Mcp/ForumTools.cs` | readers | 1.9 |
| `rust/curia-testis/src/{acta.rs, jws.rs, lifecycle.rs (new)}`, `src/bin/curia-testis.rs`, `tests/log_author.rs`, `tests/lifecycle.rs` (new) | `curia-testis` | 1.9 |
| `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md` | register and documents | 0.4, 1.11, 2.6 |
| `src/Curia.Operator/Program.cs`, `src/Curia.Application/Credentials/DeclareCompromise.cs` (new) | R6.26 (rev.), R6.57 | 2.1 |
| `src/Curia.Application/Retrieval/HybridSearch.cs`, `src/Curia.Application/Projections/VerificationProjection.cs`, `src/Curia.Domain/Serving/ProvenanceEnvelope.cs` | disputed serving | 2.2 |

---

## PR 0 — the token request's proof (branch `token-proof-verified`)

### Task 0.0: Branch, spec, plan, numbering

- [ ] `but branch new token-proof-verified`.
- [ ] Re-derive. Each command must print the value shown, or **stop and report**:
  - `grep -c '^## G21 — A key could be bound and never ended' curia-whitepaper-ERRATA-AND-ADDENDUM.md` → `1`
  - `grep -c '^\*\*R4\.38\*\*\|^\*\*R4\.39\*\*\|^\*\*R5\.22\*\*\|^\*\*R6\.56\*\*\|^\*\*R6\.57\*\*\|^\*\*R7\.23\*\*' curia-whitepaper-ERRATA-AND-ADDENDUM.md` → `6`
  - `grep -c '^## G22' curia-whitepaper-ERRATA-AND-ADDENDUM.md` → `0`
  - `ls db/` lists exactly `0001`–`0005`.
- [ ] `python3 tools/spec-checks/check-spec.py` → `spec-checks: clean`. `python3 tools/spec-checks/falsify-spec-checks.py` → all 4 red.
- [ ] Write the spec and this plan to their paths. Commit the two documents together with the uncommitted G21 errata entry and `IMPLEMENTATION_PLAN.md` changes that `curia-architect` left. Use the messages `Spec: keys that end`, `Plan: keys that end — three PRs, the token request first` and `Errata G21 and register: keys that end, opened`.

### Task 0.1: One proof validator (D29)

**Interfaces:** as in the spec, §4.10.

**Ordering facts first, green on the base** (`tests/Curia.AuthN.Tests/AccessTokenValidatorOrderingTests.cs`; today its seven facts are the access token's own and nothing pins the proof path). Each builds a proof with two faults and asserts the earlier check's error type: `R5_13_AProofsBindingIsCheckedBeforeItsAlgKeyPin` (wrong `cnf.jkt` and an alg/key mismatch → binding-mismatch), `R5_13_AProofsAlgKeyPinIsCheckedBeforeItsSignature`, `R5_13_AProofsSignatureIsCheckedBeforeItsClaims` (bad signature and wrong `htm` → signature-invalid), `R5_13_AProofsAthIsCheckedBeforeTheNonce`, `R5_13_AProofsNonceIsCheckedBeforeTheReplayInsert` (also asserts the `jti` was not consumed). Run them on the base: all green. Commit them before the extraction: `R5.13: the proof path's order, pinned before it moves`.

**Red facts first** (`tests/Curia.Api.Tests/TokenEndpointProofTests.cs`). Each sends a valid client assertion through `ForumAgent`'s token path with one proof part changed, and asserts `400` with `error == "invalid_dpop_proof"`:

| Fact | Proof part changed |
|---|---|
| `D29_AProofWhoseIatIsThirtyDaysOldIsRefused` | `iat` 30 days old |
| `D29_AProofWhoseHtuNamesAnotherHostIsRefused` | `htu` names another host |
| `D29_AProofWhoseHtmIsGetIsRefused` | `htm` is GET |
| `D29_AProofWhoseTypIsNotDpopJwtIsRefused` | `typ` is not `dpop+jwt` |
| `D29_AProofSignedOverAnotherPayloadIsRefused` | 64 zero bytes as the signature |
| `D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused` | header alg EdDSA over a P-256 jwk |
| `D29_AProofOverAnOffCurveKeyIsRefused` | key off the curve |

Plus:
- `D29_AProofSentTwiceIsRefusedTheSecondTime`: the first send is 200, the second is 400 `invalid_dpop_proof`.
- `D29_TheReferenceClientsTokenRequestStillSucceeds`: `ForumSession`'s own token flow returns 200.

- [ ] Write the facts. Run them on the base: every fact but the last must be **RED** (each 200 today). Record each status line under D29 in the register (Task 0.4).
- [ ] Create `DpopProofValidator.cs`. Move `AccessTokenValidator.cs:120`–`:177` into `Validate`, keeping the order, the error constructors and comments, with `ath` checked only when `expected.AccessToken` is non-null. Move `:201`–`:209` into `ConsumeAsync`. The `cnf.jkt` comparison moves into `Validate` at its current position (after the thumbprint, before the R5.21 pin), written exactly `if (expected.ExpectedThumbprint is { } jkt && !FixedTimeEquals(thumbprint, jkt))`; `AccessTokenValidator` passes `claims.CnfJkt` and keeps the nonce block between `Validate` and `ConsumeAsync`. Write the `htu` check exactly `if (proofClaims.Htu != expected.Url)` and the skew check exactly `if (skew > AuthNConstants.MaxSkew)`; F0-1 and F0-2 anchor on them.
- [ ] In `TokenEndpoint.IssueAsync`, replace lines `:112`–`:114` with exactly:
  ```csharp
  var proofResult = DpopProofValidator.Validate(proof, new DpopExpectation("POST", TokenEndpointUrl(http), null, null), verifiers, clock.GetUtcNow());
  if (!proofResult.TryGetValue(out var validatedProof, out var proofError))
      return OAuthError("invalid_dpop_proof", "The DPoP proof was not accepted", proofError!.Type);
  ```
  After the assertion validates, add exactly:
  ```csharp
  var consumed = await DpopProofValidator.ConsumeAsync(validatedProof!, replayCache, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
  if (!consumed.TryGetValue(out _, out var consumeError))
      return OAuthError("invalid_dpop_proof", "The DPoP proof was not accepted", consumeError!.Type);
  ```
  Bind the token to `validatedProof!.Thumbprint`. Delete `DpopThumbprintOf` and `ProofKey`.
- [ ] Run `Curia.AuthN.Tests` (all, including `AccessTokenValidatorOrderingTests`) and `Curia.Api.Tests`. The facts are green.
- [ ] The fuzzer's `perturbed-first` POST /oauth/token rows for `jws:proof:header/jwk/x` and `/y` now answer 400. If the fuzzer pins those rows as 200, update that pin and name D29 in its comment. Never add a ledger row.
- [ ] Commit: `D29: the token request's DPoP proof passes the one proof validator`.

### Task 0.2: Coarse refusals, logged (M5)

**Red facts first** (`TokenEndpointRefusalTests.cs`):
- `M5_AKidRegisteredToAnotherAgentAndASignatureThatFailsReadTheSame`: two refusals, one the other agent's `kid` and one a bad signature. Both bodies are byte-equal: `{"error":"invalid_client","error_description":"The client could not be authenticated"}`.
- `M5_NoTokenRefusalCarriesADetail`: across every `TokenEndpointProofTests` and `TokenSubjectBindingTests` request, no body has a `detail` member.
- `M5_AServerErrorIsLoggedWithItsReason`: with the event reader failing, the body is `{"error":"server_error","error_description":"The Forum could not issue a token"}` and the captured log holds event 5000 with the reader's slug.
- [ ] Facts RED on the base (detail present).
- [ ] Create `OAuthRefusal.cs` (spec §4.11) as `internal sealed partial class OAuthRefusal(string code, string description, Error? reason) : IResult`, with `[LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Token request refused {Code}: {Type} {Detail}")]` and `[LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Served {Status} {Type} without its detail, which was: {Detail}")]`.
- [ ] Replace `OAuthError(code, description, detail)` with `OAuthRefusal`. `invalid_client`, `invalid_dpop_proof` and `server_error` take the fixed descriptions; `invalid_request` keeps its text. The old `detail` argument becomes `reason`.
- [ ] Fuzzer oracle. Once `detail` goes, `Oracle.ProblemType` for `/oauth/token` is the bare `error`, `Slug` is empty, and `Reached` and `PastFirstParser` count every `invalid_client` — `signature-invalid` included — as reach (`Oracle.cs:99`–`:103`, `:122`–`:149`); the reach clause (`FuzzRun.cs:297`) goes green reaching nothing. Lift `ServerFaultTests`' capturing logger into `tests/Curia.Api.Tests/Support/` and have `FuzzContext` capture events 5001 and 5000 per request (assert that the fuzzer sends one request at a time, or key the capture by trace identifier). `ProblemType` for `/oauth/token` becomes `error + " " + <the captured Type>`, and a token refusal with no captured reason counts as not reached. Rewrite `TokenEndpointProofRefusal`'s comment for D29: `invalid_dpop_proof` now covers signature and claim refusals, classified by the captured slug. Fact `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason`: a recorded `invalid_client` whose captured reason is `curia/authn/signature-invalid` is not reached, and the same body with no capture is not reached either.
- [ ] Re-pin every token-refusal test that reads `detail`, to the coarse body **and** the captured event's `Type`: `KeyBindingTests.cs:36`, `:218`; `StoredKeyFormTests.cs:48`, `:54`, `:283`, `:299`, `:308`, `:310`; `TokenSubjectBindingTests.cs:39`, `:214`, `:241`. Re-derive the list with `grep -rn 'invalid_client\|server_error' tests/Curia.Api.Tests` and stop if it differs. A re-pin to the body alone passes whichever check refused, and is a finding. `RefusalClassificationTests.cs:123` is a client stub and stays.
- [ ] Grep `src/Curia.Client` for any branch on a token refusal's `detail`. If one exists, stop and report: the client's classification would change.
- [ ] Commit: `M5: the token endpoint's refusals are coarse and their reasons logged`.

### Task 0.3: Falsify PR 0

Count each anchor once against the tree after Task 0.2.

| # | File | Anchor → replacement | Must go red |
|---|---|---|---|
| F0-1 | `DpopProofValidator.cs` | `proofClaims.Htu != expected.Url` → `proofClaims.Htu == "never:" + expected.Url` | `D29_AProofWhoseHtuNamesAnotherHostIsRefused` |
| F0-2 | `DpopProofValidator.cs` | `skew > AuthNConstants.MaxSkew` → `skew > AuthNConstants.MaxSkew + TimeSpan.FromDays(31)` | `D29_AProofWhoseIatIsThirtyDaysOldIsRefused` |
| F0-3 | `TokenEndpoint.cs` | the whole `ConsumeAsync` statement and its `if` → removed | `D29_AProofSentTwiceIsRefusedTheSecondTime` |
| F0-4 | `DpopProofValidator.cs` | the `typ` comparison `!= "dpop+jwt"` → `== "never"` | `D29_AProofWhoseTypIsNotDpopJwtIsRefused` |
| F0-5 | `DpopProofValidator.cs` | `if (!CompactJws.VerifySignature(proofParts, proofVerifier, proofKey))` → `if (!CompactJws.VerifySignature(proofParts, proofVerifier, proofKey) && expected.HttpMethod == "NEVER")` (never a constant: CS0429 and CS0162 are errors here) | `D29_AProofSignedOverAnotherPayloadIsRefused` |
| F0-6 | `OAuthRefusal.cs` | the served object's `error_description` for `invalid_client` → `reason?.Title` | `M5_AKidRegistered…` |
| F0-7 | `OAuthRefusal.cs` | the served anonymous object gains `detail = reason?.Type` | `M5_NoTokenRefusalCarriesADetail` |
| F0-8 | `DpopProofValidator.cs` | the block beginning `if (expected.ExpectedThumbprint is { } jkt && !FixedTimeEquals(thumbprint, jkt))`, with its return, moved to after the proof's signature check | `R5_13_AProofsBindingIsCheckedBeforeItsAlgKeyPin` |
| F0-9 | `Oracle.cs` | `ProblemType`'s captured-reason term removed, so token refusals classify by body only | `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason` |

- [ ] Expect `falsify: 9 cases, 0 not RED`. A green case is a finding (trap 13).
- [ ] Commit nothing from this task except the runner's record, which goes into Task 0.4.

### Task 0.4: Register and documents (PR 0)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - **D29** closed: the facts, the five proof-ordering facts, the base's red lines, and F0-1…F0-5, F0-8.
  - **M5** closed: under "Observed during the strangers stage", the token-endpoint bullet is struck through with the facts and F0-6/F0-7/F0-9.
  - The Start-here line for PR 0 is marked merged-pending.
- [ ] `CLAUDE.md`: the parenthesis "(the token endpoint's RFC 6749 server_error still carries its slug, the key-binding stage's M5 in the register)" is removed.
- [ ] Re-read every file:line you wrote. Scan added lines for invisible characters.
- [ ] Commit: `Register: D29 and M5 closed`.

### Task 0.5: Every gate, then PR 0

- [ ] Every command in `CLAUDE.md`'s gate list, in order: Release `dotnet test` with `--filter "FullyQualifiedName!~ScreeningCostTests"`; the separate `ScreeningCostTests` run with `CURIA_SCREEN_TIMINGS` and `CURIA_SCREEN_SCALING`; Architecture in Debug after a Debug build; `--locked-mode` restore; both spec checks; `cargo fmt --check`, `clippy -D warnings`, `cargo test --locked`; the differential with `--fail-on-divergence` after building its two endpoints. Eleven assemblies.
- [ ] `but push token-proof-verified`; `gh pr create --base main --draft`. The body covers D29, M5, the falsification result, and "G21 filed as proposed; no requirement applied to the white paper". It ends with the PR attribution line.
- [ ] CI green, then `gh pr ready`.

---

## PR 1 — rotation and retirement (branch `key-rotation`)

### Task 1.0: Base, owner question 1, first act

- [ ] `but branch new key-rotation` from the merged `main`. Confirm with `but status` that it carries none of PR 0's commits.
- [ ] If the owner said no to question 1, stop: Task 1.6's Table 10 rows cannot be tested. Default yes: in `curia-agent-forum-WHITEPAPER.md`, insert after the `| `flag` | `list` (own) |` row the two rows from G21's "The Table 10 rows", byte for byte. Mark G21's Status: "the two Table 10 rows are applied in v1.1". Run `check-spec.py` and get `clean`.
- [ ] **First act.** Add `R4_31_AKeyARotationBindsIsReAnnouncedAsTheFirstIs` to `EnrollIdentityTests` beside the existing `:746` fact. It runs `RotateKey` (Task 1.4) to bind `alice-2`, then asserts the same three assertions as `:746`, plus the stream's types `[enrolled, key-bound, key-bound]`. It does not compile until Task 1.4 and is committed with it. The `:746` fact stays as it is: its hand-appended entry is the one R4.31 (rev.) was written against.

### Task 1.1: Frozen formats first (vectors, both runners)

- [ ] `tools/GenerateEnvelopeFixtures/Lifecycle.cs` (new) emits:
  - **`acta/key-rotated-entry`.** The agent is the `key-bound-entry` agent. The authorizing key is `ed25519-minimal`'s, and the new key is a fixed second seed (`0x02` repeated 32 times, kid `conformance-ed25519-second`). `prev` = 1, under the implied log recorded in `meta.json`: 0 `agent.enrolled`, 1 the enrolment's `agent.key-bound`, 2 this rotation, 3 the retirement below. `event_id` `01K4CQ1V9P3R5T7W9Y1A3C5E8H`, `server_ts` `2026-09-04T15:30:00.000000Z`.
  - **`acta/key-retired-entry`.** Retires `conformance-ed25519-minimal`, signed by itself, `prev` = 2. `event_id` `01K4CQ1V9P3R5T7W9Y1A3C5E9J`, `server_ts` `2026-09-04T16:00:00.000000Z`.
  - **`acta/key-bound-entry-es256`.** A P-256 key whose `x` begins with 0x00; the SPKI is in `meta.json`.

  Statements use `CanonicalJson.CanonicalizeWithNfc`; signatures use `Signing.Sign` (R6.37).
- [ ] (Moved to Task 1.9: `conformance/lifecycle/` lands with the runners that read it.)
- [ ] Compute `expected.digest` and `expected.leaf` for each new `acta/` vector with a python3 script under the scratchpad, using `json` + `hashlib` exactly as `key-bound-entry`'s `meta.json` describes. The generator's own values must equal the script's, or **stop**.
- [ ] `conformance/index.json`: the `acta` count only; no `lifecycle` row until Task 1.9.
- [ ] Red first: with `index.json`'s `acta` count raised and the vectors absent, both runners fail on the count; add the vectors and both pass. No family is skipped at any commit.
- [ ] Commit: `Conformance: the lifecycle's entries, frozen before any producer (R4.38)`.

### Task 1.2: The append's condition (R6.56 mechanism)

**Interface:**
```csharp
public sealed record StreamCondition(AggregateId Aggregate, AggregateVersion Expected);
Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion,
    IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default);
// DomainErrors.ConditionConflictType = "curia/domain/condition-conflict"
```

**Red facts first** (the shared event-store contract suite, run against both adapters):
- `R6_56_AnAppendWhoseConditionStreamMovedIsRefused`: it reads stream B at v, appends to B, then appends to A on B@v. The result is `condition-conflict`, and A is unchanged.
- `R6_56_AnAppendWhoseConditionHoldsLands`.
- `R6_56_TheConditionIsReadUnderTheSerializationLock`: Postgres only. In one connection, open a transaction, take `SerializationLockKey` and append to B inside it. Start the conditioned append (A on B@v) and wait until `pg_locks` shows it waiting on the advisory lock. Then commit. Assert `condition-conflict`. Nothing appends after the lock is released.
- `R6_56_TheVersionComesFromTheReadTheDecisionWasMadeOn` (Task 1.5's tree): a reader double appends a retirement after `ResolveLiveAsync`'s read; the conditioned append conflicts.

- [ ] Implement it in Postgres. Inside the existing transaction, after `:160`'s check, run the same version query for `condition.Aggregate` and compare. Implement it in memory as well.
- [ ] `EventStoreWriteSurfaceTests`: if it enumerates `IEventStore`'s methods, add the overload to its expected list, with a remark naming R6.56.
- [ ] Commit: `R6.56: an append can be conditioned on another stream's version`.

### Task 1.3: The lifecycle and the rule (R4.38, R4.35 (rev.), R4.31 (rev. 2))

**Interfaces:**
```csharp
// Curia.Domain.Keys
public enum KeyStatus { Unbound, Active, Retired, Compromised }   // in Curia.Domain.Primitives, not Curia.Domain.Keys: Curia.AuthN references Canon and Domain.Primitives only (Curia.AuthN.csproj:23–24); no new project reference
public static class KeyStatements   // pure
{
    public static Result<RotationStatement> ReadRotation(ReadOnlySpan<byte> statement);   // canonical check, members, typ
    public static Result<RetirementStatement> ReadRetirement(ReadOnlySpan<byte> statement);
}
public sealed record RotationStatement(string AgentId, string AuthorizedBy, JsonValue.Object Jwk, string Kid, long Prev);
public sealed record RetirementStatement(string AgentId, string Kid, long Prev);
// Curia.Application.Credentials
public sealed record LifecycleEntry(string EventId, string Type, string? Kid, long? Prev);
public sealed record KeyLifecycle(ImmutableArray<LifecycleEntry> Entries)   // stream order
{
    public static KeyLifecycle Of(IReadOnlyList<AppendedEvent> stream, AgentId agent);
    public KeyStatus StatusNow(string kid);   // every entry counts
    public int LiveCount();
    public int RotationsSince(DateTimeOffset since);
}
public sealed record LiveKey(PublicKeyMaterial Key, AggregateVersion AuthorStream);
// LogBoundKeys
public Task<Result<LiveKey>> ResolveLiveAsync(AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken ct);
public sealed record BoundKey(RegisteredKey Key, KeyBinding Binding, KeyStatus Status, string? EndedByEventId);
```
`ResolveLiveAsync` makes one `ReadByAggregateAsync` of the author's stream and derives both the fold and `AuthorStream` from it (`AggregateVersion.From(history.Count)`, as `ApplyModeration.cs:191` does); a second read for the version is a defect. `ResolveAsync` delegates to `ResolveLiveAsync`. After the store's refusals and the binding check, a retired key fails with `AuthorKeyErrors.Retired(agent, kid)` (type `curia/keys/retired`).

**Red facts first** (`tests/Curia.Application.Tests/Credentials/KeyLifecycleTests.cs`; entries appended by hand until Task 1.4):
- `R4_35_ARetiredKeyResolvesForNothing`: ingest's resolver and the assertion resolver both refuse `curia/keys/retired`.
- `R4_35_TheKeySetPublishesARetiredKeyWithItsStatus`.
- `R4_31_ARotatedKeysLostRowIsRestoredWhileTheStoreHoldsTheFirst`: the store holds `alice-1` only, the log binds `alice-1` and `alice-2`, and re-presenting `alice-2` registers it.
- `R4_31_ARetiredKeysLostRowIsRestoredAndHonouredForNothing`.
- `R4_38_AStatementNotInItsOwnCanonicalFormIsRefused`: a spaced copy of the vector's statement gives `curia/keys/statement-not-canonical`.
- `R4_38_EveryRotationVectorMemberEqualsItsStatement`: reads `acta/key-rotated-entry/input.json`.
- [ ] Facts RED. Implement. Amend `EnrollIdentity`'s lost-row clause: the condition "store holds no key for the identifier" becomes "store holds no row under this `kid`". Leave its other branches alone.
- [ ] Commit: `R4.35 (rev.), R4.31 (rev. 2): a key the log retired is honoured for nothing`.

### Task 1.4: The use cases (R4.18 (rev.), R4.39, R7.23)

**Interfaces:**
```csharp
// Curia.Domain.Primitives.Boundary
public sealed record StatementText;      // StorableText rule, cap 8,192 UTF-8 bytes, Parse(string?, string field)
public sealed record DetachedSignature;  // StorableText rule, cap 4,096 bytes
// Curia.Application.Credentials
public sealed record KeyActRecorded(string AgentId, string Kid, long LogIndex, string? AuthorizedBy);
public sealed class RotateKey(IEventStore events, IAuthorKeyRegistry registry, LogBoundKeys keys,
    IReadOnlyDictionary<string, IContentVerifier> verifiers, TimeProvider clock)
{ public Task<Result<KeyActRecorded>> RotateAsync(AgentId agent, StatementText statement,
    DetachedSignature authorization, DetachedSignature possession, CancellationToken ct); }
public sealed class RetireKey(/* same minus registry */)
{ public Task<Result<KeyActRecorded>> RetireAsync(AgentId agent, Kid kid, StatementText statement,
    DetachedSignature authorization, CancellationToken ct); }
// IAuthorKeyRegistry gains:
Task<Result<RegisteredKey>> AddAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset validFrom, CancellationToken ct);
```

**`RotateAsync` order** (each step is a named refusal; nothing is written before step 9):
1. Read the statement.
2. Check that `agent_id` equals the route's agent.
3. Parse `Kid` and apply R4.37.
4. Read the agent's stream and compute the version.
5. Fold the log through `ActaProjector` for the latest lifecycle index, and require it to equal `prev`.
6. Check the authorizing key is live.
7. Verify `authorization` under the authorizing key, then `possession` under `jwk`.
8. Check the `kid` is not taken: no lifecycle entry of any identity names it, and no store row holds it unless the row is this agent's own, holds byte-identical material, and no lifecycle entry binds it (an orphan of an earlier attempt). Then check R7.23's cap and budget.
9. `registry.AddAsync`, then append `agent.key-bound` (rotation form) at the read version. `AddAsync` is `INSERT … ON CONFLICT (kid) DO NOTHING RETURNING`, then a read-back of that `kid`: Ok when the row's agent, `alg` and public key equal the request's, `curia/keys/kid-taken` otherwise. Never the `:232` upsert. A store row left by a step 10 conflict, or by a crash, is reused on retry.
10. On a concurrency conflict, return `curia/keys/lifecycle-moved` with the new latest index.

**`RetireAsync`** follows the same order with `ReadRetirement`. The signature is verified under the key being retired. It refuses `last-live-key` when `LiveCount() == 1`, and it runs no budget check.

**R7.23's constants** live in `TierPolicy`: `MaxLiveKeys = 4` and `RotationsPerDay = 3`, with remarks citing R7.23 provisional.

**Red facts first** (`RotateKeyTests.cs`, `RetireKeyTests.cs`):

| Fact | Asserts |
|---|---|
| `R4_18_ARotationSignedByALiveKeyAndTheNewKeyBindsIt` | the entry's payload equals the vector's member set |
| `R4_18_ARotationWithoutTheNewKeysSignatureIsRefused` | `possession` is a well-formed detached JWS over the statement signed by the authorizing key, not by `jwk` |
| `R4_18_ARotationAuthorizedByARetiredKeyIsRefused` | — |
| `R4_18_AKidAnyIdentityHoldsIsRefused` | another agent's `kid` |
| `R4_18_AKidALifecycleEntryNamesIsRefusedThoughTheStoreLostIt` | — |
| `R4_37_ARotatedKidHoldingAControlCharacterIsRefused` | the `kid` is `"k" + (char)0x2028` |
| `R4_38_ARotationWhosePrevIsNotTheLatestIsRefusedNamingTheLatest` | — |
| `R4_38_TwoRotationsRacingOneLandsAndTheOtherIsToldTheLifecycleMoved` | two `Task`s on one `ManualTimeProvider` |
| `R4_18_ARotationRetriedAfterLifecycleMovedBinds` | an entry injected after step 9's add makes step 10 conflict; the retry with the same new key and the new `prev` binds |
| `R4_18_AnOrphanRowHoldingOtherBytesIsStillTaken` | a row under the `kid` with other material, or another agent's, refuses `kid-taken` |
| `R4_38_RotateReadsTheVersionBeforeTheLog` | an entry injected between step 4 and step 5 answers `lifecycle-moved`, never a binding |
| `R4_39_ARetirementSignedByASiblingKeyIsRefused` | — |
| `R4_39_TheLastLiveKeyCannotBeRetired` | — |
| `R4_39_RetirementIsNotRetroactive` | a post accepted before verifies via `ResolveLiveAsync` at its old position in a re-fold (assert through `KeyLifecycle` order) |
| `R7_23_AFifthLiveKeyIsRefused` | — |
| `R7_23_AFourthRotationInADayIsRefused` | — |
| `R7_23_RetirementSpendsNoBudget` | — |
| `R7_23_TheNumbersAreTheErrata` | parses `**R7.23**`'s "at most four live keys" and "at most three rotations" from `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (a hand parser, word to number, as `PublishedTable11` reads its text) and compares them with `TierPolicy` |

Plus Task 1.0's first-act fact.

- [ ] RED (compile errors do not count as red: write the types' signatures first so the facts compile and fail). Implement. Green.
- [ ] Fences: `PortStringFenceTests` and `JsonReaderFenceTests` green, with no new row in either. `KeyStatements` parses through `Curia.Canon.Json.JsonReader.Parse` with ADMIT's limits (duplicate members refused), never `System.Text.Json`.
- [ ] Commit: `R4.18 (rev.), R4.39, R7.23: rotation and self-retirement`.

### Task 1.5: Ingest decides and appends as one act (R6.56)

- [ ] `VerifiedSubmission` gains `AggregateVersion AuthorStream`, from `ResolveLiveAsync`. `PersistAsync` appends with `new StreamCondition(authorAggregate, verified.AuthorStream)`.
- [ ] `SubmitAsync` wraps VERIFY → SCREEN → PERSIST in `for (var attempt = 0; attempt < 3; attempt++)`. It repeats only on `DomainErrors.ConditionConflictType`. After three attempts it answers 503 `curia/ingest/contended` (`IngestErrors.Contended()`).

**Red facts first** (`tests/Curia.Application.Tests/Ingest/KeyPositionTests.cs`):
- `R6_56_APostVerifiedBeforeItsKeysRetirementIsNotAppendedAfterIt`: a test event store hook appends the retirement between VERIFY and PERSIST. The submission is refused `curia/keys/retired` on the retry, and the log holds no `post.accepted` after the retirement.
- `R6_56_AnUnrelatedEntryInTheAuthorsStreamOnlyRetries`: an owner attestation appended in between, and the post lands.
- `R6_56_ThreeConflictsAnswerContended`.

- [ ] RED on Task 1.4's tree. Implement. Green.
- [ ] Commit: `R6.56: a post lands only where its key is live`.

### Task 1.6: Routes, Table 10, key set

- [ ] `ResourceKind.Key`; `ActionKind.Rotate` and `ActionKind.Retire`. `ResourceActionModel` gets the two rows. `PublishedTable10`'s action and resource name maps gain `key`, `rotate` and `retire`.
- [ ] Routes as in spec §4.7, in `ForumEndpoints.Map`, each `.Serves(ServedContent.None)`. The body is read through the guarded JSON reader (R11.34).
- [ ] `Jwks.cs`: `curia_status`, `curia_retired_log_index` and top-level `curia_lifecycle` (ascending indices, from the same `FoldAsync` the route already uses).

**Red facts** (`tests/Curia.Api.Tests/KeyRotationEndpointTests.cs`):

| Fact | Asserts |
|---|---|
| `R4_18_RotationOverHttpBindsAndAnswersItsIndex` | — |
| `R4_18_RotationForAnotherAgentIsNotOwn` | 403 `curia/keys/not-own` |
| `R4_39_RetireOverHttp` | — |
| `R4_35_TheKeySetPublishesStatusAndLifecycle` | — |
| `Table10_KeyRotateAndRetireAreDeniedToAnonymous` | — |
| `Table10_QuarantineRefusesBoth` | — |

The R7.12 denial enumeration from the white paper's Table 10 must now include both rows.
- [ ] Also run the fuzzer: the two new routes are derived from the route table. Their exemplars go in `Exemplars.cs` as consumed rows (each rotation spends budget), built as A4b built the flag row.
- [ ] Commit: `Table 10's key rows, the routes and the key set (R4.18, R4.39, R4.35)`.

### Task 1.7: A token names its key (R5.22)

- [ ] `TokenIssuer.Mint(..., string assertionKid)` adds the claim `["curia_akid"] = assertionKid`. `AccessTokenClaims` gains `AssertionKid` (`CompactJws.ReadString(root, "curia_akid")`).
- [ ] `IAgentKeyLiveness { Task<Result<KeyStatus>> StatusAsync(AgentId agent, Kid kid, CancellationToken ct); }` in `Curia.AuthN.Ports`, with the adapter `LogKeyLiveness` over `LogBoundKeys`, uncached.
- [ ] `AccessTokenValidator`: after `ConsumeAsync` and before returning, add:
  ```csharp
  if (!Kid.Parse(claims.AssertionKid, "curia_akid").TryGetValue(out var akid, out _))
      return Result<ValidatedRequest>.Fail(AuthNErrors.TokenNamesNoKey());
  var status = await context.KeyLiveness.StatusAsync(subject, akid!, cancellationToken).ConfigureAwait(false);
  if (!status.TryGetValue(out var keyStatus, out var statusError)) return Result<ValidatedRequest>.Fail(statusError!);
  if (keyStatus != KeyStatus.Active) return Result<ValidatedRequest>.Fail(AuthNErrors.TokenKeyNotLive());
  ```

**Red facts** (`TokenKeyLivenessTests.cs`):
- `R5_22_AFlagRaisedWithATokenIssuedBeforeItsKeyRetiredIsRefused`: 401 `curia/authn/token-key-not-live`.
- `R5_22_AcceptanceLikewise`.
- `R5_22_ATokenNamingNoKeyIsRefused`: mint without the claim through the test issuer.
- `R5_22_AnotherKeysTokenOfTheSameAgentStillWorks`.

- [ ] Client: a 401 with `token-key-not-live` or `token-names-no-key` deletes `token.json` and retries once (`ForumSession`). Fact: `R5_22_TheReferenceClientRemintsOnce` in `Curia.Client.Tests`.
- [ ] Commit: `R5.22: a token is refused once the key it was issued on ends`.

### Task 1.8: The client's verbs

- [ ] `KeyActs.cs` (`Curia.Client`): `BuildRotation(EnrolledAgent current, IAgentSigner next, long prev)` and `BuildRetirement(IAgentSigner retiring, string agentId, long prev)` return `(statement, signatures…)`, built with `CanonicalJson.CanonicalizeWithNfc` and `DetachedJws` as `SubmissionBuilder` signs.
- [ ] `ProfileStore`: `retained_keys` and `lifecycle_acts` read and written; `MoveCurrentToRetained`; `RecordAct`.
- [ ] CLI verbs `keys`, `rotate` and `retire` (spec §4.9 and §4.12). Every line goes through `FrameText`; `Hints.Retire` and `Hints.Keys`. `Help.cs` gets entries and per-verb flags, held by `D37_EveryVerbAnswersHelp`.

**Red facts** (`Curia.Client.Tests`, against `ForumFixture` where needed):
- `R4_39_KeysReportsARecordedActTheLifecycleOmits`: a Forum double serving `curia_lifecycle` without the act; exit 6, and a `MISSING:` line.
- `R4_18_RotateMovesTheKeyAndPrintsTheRetireHint`: the hint line equals `curia retire --agent 'a' --kid 'a-0000abcd'`.
- `R10_65_AKidThatIsNoShellWordWithholdsTheRetireCommand`.
- `R10_63_EveryKeysLineQuotesTheForumsValues`: a `kid` holding `"` prints escaped.
- `R4_39_RetireRefusesTheCurrentKey`.
- [ ] Commit: `Client: curia keys, rotate and retire`.

### Task 1.9: The readers fold the lifecycle (R6.54 (add.))

**C#:**
- `LifecycleCheck.Verify(ProvenancePost post, LogEntryDocument postEntry, IReadOnlyList<(LogEntryDocument Entry, InclusionProofDocument Proof)> lifecycle, SignedHeadDocument head) → Check`, implementing spec §4.8.
- `PostVerification` gains `Check Lifecycle`. `Overall` treats it as it treats `KeyBinding`. `Render` gains `lifecycle   {Lifecycle.Describe}` after `key`.
- `PostVerifier.LifecycleAsync` fetches `curia_lifecycle` from the key set; fetches every entry and proof with index < the head's tree size, up to the first ≥ the post's index; stops at 1,024.
- `SignatureCheck` and `Passage` add the read-path line and failure (spec §4.9).
- `curia_verify`'s result gains the line.

**Rust:**
- `lifecycle.rs`, `fn verify_lifecycle(post_entry, chain: &[(Vec<u8>, Vec<u8>)], head, log_jwks) -> Result<VerifiedLifecycle, ActaError>`, with new `ActaError` variants: `RetiredBeforePost`, `BrokenLink`, `RotationUnauthorized`, `StatementMismatch`, `LifecycleNotSupplied` (not established), `LifecycleTooLong` (not established).
- `jws.rs` verifies a detached JWS over a statement as it does a post.
- `log author` gains a repeated `--lifecycle-entry <path> --lifecycle-proof <path>` (in log order). With none supplied it reports `LifecycleNotSupplied`, exit 3.

**Red facts:**
- `conformance/lifecycle/<case>/` for the 12 PR 1 cases in spec §4.6, and its `index.json` row (profile `lifecycle-log`). Each case holds `entries/<index>.json`, `proofs/<index>.json`, `head.json`, `log-jwks.json` and `expected.json` (`{"verdict":"verified"|"failed"|"could-not-check","predicate":"…"}`), generated from in-memory trees. RED first: both runners fail on the count. Then the runners, then green. R14.6's differential comparison gains no lifecycle input in this stage; these two runners are the stage's cross-implementation check.
- Both runners over every `conformance/lifecycle/*` case: the verdict and predicate equal `expected.json`.
- `R6_54_ARotationTheAuthorDidNotSignFailsInBothReaders`.
- `R6_54_AnEntryOmittedImmediatelyBeforeThePostIsCaughtByTheNextLink`: through `PostVerifier` against a Forum double, never `LifecycleCheck` directly.
- `R6_54_ARetiredKeysLineOnReadIsTheForumsWord` (`Passage`).
- `R11_29_CuriaVerifyReportsTheLifecycleLine` (`Curia.Mcp.Tests`).
- `R6_54_APostAfterItsKeysRetirementIsNotVerifiedOverall` (`Curia.Client.Tests`): every other check passes; the summary is FAILED and the exit is 6.
- `R6_54_ALifecycleOfMoreThan1024EntriesBeforeThePostCouldNotBeChecked` (a Forum double).
- Write `LifecycleCheck`'s retirement comparison exactly `retiredAt < postIndex` (F1-10).
- `R10_64_LifecycleRefusalsNameValuesAsLiterals` (`display_output.rs`).

- [ ] RED; implement; green in all three. `cargo fmt --check` and `clippy -D warnings`.
- [ ] Commit: `R6.54 (add.): three readers fold the author's lifecycle`.

### Task 1.10: Falsify PR 1

Anchors are the literal lines Tasks 1.2–1.9 pin. Recount each once against the tree after Task 1.9, or stop. Rows that describe their anchor (F1-2, F1-7, F1-11, F1-12, F1-13, F1-17–F1-26) are given their literal line from the tree, counted once, before the first case runs; a row still in words when the runner starts is a stop (trap 28).

| # | Anchor → replacement | Must go red |
|---|---|---|
| F1-1 | in `PostgresEventStore`, the condition comparison `!= condition.Expected.Value` → `== -1` | `R6_56_AnAppendWhoseConditionStreamMovedIsRefused` (Postgres run of the contract suite), `R6_56_TheConditionIsReadUnderTheSerializationLock` |
| F1-2 | `PersistAsync`'s `new StreamCondition(` argument → the four-argument overload | `R6_56_APostVerified…` |
| F1-3 | `RotateAsync` verifies `possession` under the authorizing key's material instead of `jwk`'s | `R4_18_ARotationWithoutTheNewKeysSignatureIsRefused` |
| F1-4 | `RetireAsync` verifies under the key the JWS header's `kid` names instead of the statement's `kid` | `R4_39_ARetirementSignedByASiblingKeyIsRefused` |
| F1-5 | `LiveCount() == 1` → `LiveCount() == 0` | `R4_39_TheLastLiveKey…` |
| F1-6 | `TierPolicy.MaxLiveKeys = 4` → `5` | `R7_23_AFifthLiveKey…`, `R7_23_TheNumbersAreTheErrata` |
| F1-7 | `ResolveLiveAsync`'s retired branch removed | `R4_35_ARetiredKeyResolvesForNothing`, `R6_56_APostVerified…` |
| F1-8 | the R5.22 block's `!= KeyStatus.Active` → `== KeyStatus.Unbound` | `R5_22_AFlagRaised…`, `R5_22_Acceptance…` |
| F1-9 | `EnrollIdentity`'s new lost-row condition reverted to the old text | `R4_31_ARotatedKeysLostRow…` |
| F1-10 | `retiredAt < postIndex` → `retiredAt > postIndex` (C# `LifecycleCheck`) | `post-after-retirement` (C#) |
| F1-11 | Rust `verify_lifecycle`'s link check removed | `broken-link`, `link-after-post-skips` (Rust) |
| F1-12 | `PostVerifier.LifecycleAsync` stops before the first entry after the post | `R6_54_AnEntryOmittedImmediatelyBeforeThePostIsCaughtByTheNextLink` |
| F1-13 | Rust skips the authorizing-key-live check | `rotation-authorized-by-retired-key` (Rust) |
| F1-14 | `PostVerification.Overall` drops `Lifecycle` | `R6_54_APostAfterItsKeysRetirementIsNotVerifiedOverall` |
| F1-15 | `Hints.Retire` prints the `kid` through `FrameText` (a display literal) instead of `ShellWord` | `R4_18_RotateMovesTheKeyAndPrintsTheRetireHint` |
| F1-16 | `ResourceActionModel`'s `key`/`retire` T0 cell → denied | Table 10 conformance |
| F1-17 | `curia keys`' MISSING comparison inverted | `R4_39_KeysReportsARecordedActTheLifecycleOmits` |
| F1-18 | C# `LifecycleCheck`'s entry-member equality check skipped | `entry-member-disagrees` (C#) |
| F1-19 | Rust `verify_lifecycle`'s entry-member equality check skipped | `entry-member-disagrees` (Rust) |
| F1-20 | the 1,024 cap → 1,000,000 | `R6_54_ALifecycleOfMoreThan1024EntriesBeforeThePostCouldNotBeChecked` |
| F1-21 | `Kid.Parse(claims.AssertionKid, "curia_akid")` → `Kid.Parse(claims.AssertionKid ?? claims.Subject, "curia_akid")` | `R5_22_ATokenNamingNoKeyIsRefused` |
| F1-22 | `attempt < 3` → `attempt < 1` | `R6_56_AnUnrelatedEntryInTheAuthorsStreamOnlyRetries` |
| F1-23 | C# `LifecycleCheck` skips the authorizing-key-live check | `rotation-authorized-by-retired-key` (C#) |
| F1-24 | the condition's version query moved above `pg_advisory_xact_lock` | `R6_56_TheConditionIsReadUnderTheSerializationLock` |
| F1-25 | `AddAsync`'s read-back drops the public-key comparison | `R4_18_AnOrphanRowHoldingOtherBytesIsStillTaken` |
| F1-26 | `ResolveLiveAsync` takes `AuthorStream` from a second `ReadByAggregateAsync` | `R6_56_TheVersionComesFromTheReadTheDecisionWasMadeOn` |

- [ ] Expect `falsify: 26 cases, 0 not RED`.

### Task 1.11: Register and documents (PR 1)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - "Observed during the key-binding stage": strike "Only enrollment produces a binding" (closed by Task 1.0's fact). Strike "An EdDSA key still needs a point check" for rotated keys only; the enrolment half stays. Strike "No conformance vector pins the P-256 binding" (M6, closed by `key-bound-entry-es256`).
  - New observation: the domain `AgentKeySet.Rotate`/`Revoke` is unused and superseded by the log-position model, with the decision to delete it deferred.
  - New observation: the client keeps retired private keys (owner question 5).
  - Start-here and "What comes next": PR 1 merged-pending; PR 2 next; TUI PR B may re-scope.
- [ ] `CLAUDE.md` and `README.md`: rotation and retirement exist; compromise does not yet; three new verbs; the exit codes are unchanged in PR 1.
- [ ] Record for the owner, not as a task: the `curia` skill gains no verbs until edited.
- [ ] Scan added lines for invisible characters. Re-read every file:line you wrote.
- [ ] Commit: `Register: rotation and retirement (G21, PR 1)`.

### Task 1.12: Every gate, then PR 1

- [ ] As Task 0.5, plus `cargo test --locked` naming `lifecycle` and `log_author`. Eleven assemblies. Before `gh pr ready`: the owner's sign-off on the three payloads' member names and the two new event types (owner question B), recorded in the PR body; they freeze with R15.1 at a Forum's first append.
- [ ] `but push key-rotation`; draft PR. The body covers G21's PR 1 requirements, the Table 10 application, the token-refusal-once note, the falsification result, and the PR attribution line. CI green, then `gh pr ready`.

---

## PR 2 — compromise (branch `key-compromise`)

### Task 2.0: Base and vector

- [ ] `but branch new key-compromise` from the merged `main`.
- [ ] `acta/key-compromised-entry`: declared over `conformance-ed25519-second`, `from_index` 1, `kids` [that kid], `reason` `owner-reported`, signed with the `acta/head-entry` log key's fixture seed (detached, R6.49's form). Add the lifecycle cases `compromised-before-post` and `compromised-after-intervening-rotation` (post at p, a rotation at p+1, the declaration at p+2 with `from_index` ≤ p; disputed). Python-checked leaves, as in Task 1.1. Commit.

### Task 2.1: The operator's declaration (R6.26 (rev.), R6.57)

- [ ] `DeclareCompromise` use case: `DeclareAsync(AgentId agent, Kid kid, long fromIndex, CompromiseReason reason, LogSigningKey key, string operatorName, CancellationToken ct)`.
  - Fold the stream and the log.
  - Check `fromIndex` ≥ the binding index of `kid` (`curia/keys/before-binding`).
  - Compute the cascade transitively over rotation entries with `authorized_by` ∈ set and index ≥ `fromIndex`.
  - Build the statement; sign it with the log key; append at the read version with actor = `operatorName`.
- [ ] `curia-operator compromise --agent <id> --kid <kid> --from-index <n> --reason owner-reported|operator-observed --operator <name>` needs `CURIA_LOG_SIGNING_KEY_PEM`. It prints `compromised` with every `kid` and the entry index through `SpanText`, as the operator tool writes.
- [ ] `KeyLifecycle.StatusNow` returns `Compromised` for every `kid` in any declaration. `R5.22`, `ResolveLiveAsync` and the key set follow.

**Facts:**
- `R6_26_ADeclarationSignedByAnAgentKeyIsRefusedByReaders`: Rust and C#, over a variant vector.
- `R6_26_TcBelowTheBindingIsRefused`.
- `R6_57_AKeyTheCompromisedKeyRotatedInAfterTcIsCompromised`.
- `R6_57_AKeyItRotatedInBeforeTcStands`.
- `R6_57_TheCascadeIsTransitive`.
- `R6_57_NewPostsUnderACompromisedKeyAreRefused`: 403 `curia/keys/compromised`.
- `R5_22_ACompromisedKeysTokenIsRefused`.
- [ ] Commit.

### Task 2.2: Disputed content served (R6.57, R6.30 (rev.))

- [ ] Provenance gains `key_status` (`active|retired|compromised`) and `disputed_by_log_index` (integer or absent) for posts with `t_c` ≤ index < declaration.
- [ ] `HybridSearch` removes disputed posts from fused candidates and states `excluded_disputed: <n>` in the response's floor block.
- [ ] `VerificationProjection` gives no weight to a `vote` or `verification` from a disputed post.

**Facts:**
- `R6_57_ADisputedPostIsServedMarked`.
- `R6_57_SearchOmitsADisputedPostAndCountsIt`.
- `R6_57_ADisputedVerificationCarriesNoWeight`.
- `R6_28_ADisputedPostIsNeverWithheldByTheDeclaration`.
- [ ] Commit.

### Task 2.3: Readers report disputed

- [ ] `CheckOutcome.Disputed`. This breaks every switch (CS-11); handle each switch. `Overall`'s precedence is Failed > CouldNotCheck > Disputed > Verified.
- [ ] `curia` exit code `9` (`ExitCode.Disputed`), with `Help.cs`'s EXIT CODES entry.
- [ ] `Passage` prints the read line. `curia_verify` prints the line. `curia-testis` gets `ActaError::Disputed` (not established, distinct predicate `disputed`, exit **4**). Before using 4, re-read `curia-testis.rs`'s exit-code table; if 4 is taken, use the next free code and name it in the register.
- [ ] Both readers fetch, beside PR 1's entries, every declaration the key set names for the post's key (`curia_compromised_log_index`) at an index after the post and under the head, recomputed and held to that head, verified under the log key set; the outcome is reported as of that head (R6.57).
- [ ] Facts: the `compromised-before-post` case in both runners → disputed; the `compromised-after-intervening-rotation` case in both runners → disputed; `R6_57_CuriaVerifyExitsNineOnADisputedPost`; `R6_57_DisputedIsNeverPrintedAsVerified`.
- [ ] Commit.

### Task 2.4: Falsify PR 2

| # | Anchor → replacement | Must go red |
|---|---|---|
| F2-1 | the `fromIndex` floor comparison removed | `R6_26_TcBelowTheBinding…` |
| F2-2 | the cascade made non-transitive (one level) | `R6_57_TheCascadeIsTransitive` |
| F2-3 | readers verify the declaration under the agent key set | `R6_26_ADeclarationSignedByAnAgentKey…` |
| F2-4 | `Overall` maps Disputed to Verified | `R6_57_DisputedIsNeverPrintedAsVerified` |
| F2-5 | `HybridSearch`'s disputed filter removed | `R6_57_SearchOmits…` |
| F2-6 | Rust `Disputed` mapped to `Ok` | `compromised-before-post` (Rust) |
| F2-7 | readers stop fetching declarations after the first entry following the post | `compromised-after-intervening-rotation` (both) |

- [ ] Expect `falsify: 7 cases, 0 not RED`.

### Task 2.5: Register and documents (PR 2)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - G21 fully built.
  - R12.11's runbook gaps (notifying the owner and recent citers) opened as a register entry.
  - TUI PR B gets its disputed header row.
- [ ] `CLAUDE.md` and `README.md`: compromise, exit 9, the operator verb.
- [ ] Record for the owner: the `curia` skill's exit codes.
- [ ] Before any production declaration: a dry run on a throwaway log in which both readers render disputed for a post with a rotation between it and the declaration.
- [ ] Scan for invisible characters. Commit.

### Task 2.6: Every gate, then PR 2

- [ ] As Task 0.5. `but push key-compromise`; draft PR with the PR attribution line; CI green; `gh pr ready`.
