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
- Every new port parameter carrying a caller's identifier is a boundary type (`AgentId`, `Kid`, or a new type in `Curia.Domain.Primitives/Boundary`), and never an allowlist row. A string-like parameter the R11.34 fence newly reaches that is not a caller's identifier takes a row only under the one-part-at-a-time spec §4.8's classification rule, with the reason that rule gives and an existing row as precedent, and the task that adds it lists it. Retyping or moving a type to keep it out of the fence's reach is not a fix. The R11.34 fence stays green. (Amended at Task 0.1 by the architect's ruling: §4.10's types bring `DpopProofValidator.Validate`, `DpopExpectation`, `ValidatedProof` and `DpopProofClaims` into the fence's scope through `TokenEndpoint`, and none of their eleven string-like parameters is a caller's identifier.)
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
| `tests/Curia.Api.Tests/TokenEndpointProofTests.cs` (new), `TokenEndpointProofRetentionTests.cs` (new), `TokenEndpointRefusalTests.cs` (new), `RequestSurfaceTests.cs` | D29, R5.14's retention, M5 facts | 0.1, 0.2 |
| `tests/Curia.Api.Tests/Support/` (the capturing logger, lifted) | events 5001 and 5000, captured | 0.2 |
| `tests/Curia.AuthN.Tests/AccessTokenValidatorOrderingTests.cs`, `tests/Curia.AuthN.Tests/DpopProofRetentionTests.cs` (new), `tests/Curia.AuthN.Tests/AccessTokenValidatorDpopTests.cs` | the proof path's order, R5.14's retention on both paths, D29's tense | 0.1 |
| `src/Curia.AuthN/Dpop/JwkPublicKey.cs` | D29's tense | 0.1 |
| `tests/Curia.Architecture.Tests/PortStringAllowlist.cs` | R11.34 rows for §4.10's types; five stale rows removed | 0.1 |
| `tests/Curia.Api.Tests/DpopClient.cs` | a header-editing Proof overload; RequestTokenWithProofAsync, which sends a proof as written | 0.1 |
| `tests/Curia.Api.Tests/{KeyBindingTests.cs, StoredKeyFormTests.cs, TokenSubjectBindingTests.cs, ServerFaultTests.cs}` | M5 re-pins; the capturing logger lifted to Support/ | 0.2 |
| `tests/Curia.Api.Tests/Fuzz/{RequestModel.cs, Oracle.cs, OracleTests.cs, FuzzContext.cs, FuzzRun.cs, RequestFuzzRandomTests.cs}` | D29's off-curve rows; the oracle's captured reason; Divisor | 0.1, 0.2 |
| `src/Curia.Client/ForumClient.cs` (ReadProblem's summary) | M5's wire shape | 0.2 |
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` | G21 committed; the R5.14 sentence in the §5.5, §5.6 row | 0.0, 0.4 |
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

**Base observations, before any source change.** Task 0.1's source changes remove the base, and it cannot be run again without `git checkout`, which is banned. Capture these first, on the base, and keep them for Task 0.4:
- the status lines of the red run below, including `D29_AProofSentTwiceIsRefusedTheSecondTime`'s second send (200 on the base);
- the `detail` the base serves for each of `R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown`'s five `inAssertion` rows (`RequestSurfaceTests.cs:465`). Read them from one base run that writes each body to the test output, and remove that write before any commit. These five slugs are the captured Types Task 0.2 re-pins them to. After D29 the off-curve proof stops those rows before the assertion, so this run is the only source.
- the red line of `R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew` (below), which compiles on the base and records the base's `now + MaxSkew`.
Every other "red on the base" line in PR 0 is likewise run before the source change it precedes.

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
- `D29_AProofSentTwiceIsRefusedTheSecondTime`: one proof and two client assertions, each with its own jti. The first send (proof + assertion 1) is 200. The second (the same proof + assertion 2) is 400 `invalid_dpop_proof`, and after M5 its captured 5001 Type is the replay slug. Resending the identical request is refused 401 `invalid_client` by the assertion's jti, on the base and after the fix alike, because ClientAssertionValidator shares the replay cache and inserts first. So it would test nothing, and loosening the fact to 'is refused' makes F0-3 vacuous. The fact asserts the status and the error code exactly, never 'not 200'.
- `D29_TheReferenceClientsTokenRequestStillSucceeds`: `ForumSession`'s own token flow returns 200.

**R5.14's retention.** These belong to the `TokenEndpointProofTests` list, but live in their own class, `tests/Curia.Api.Tests/TokenEndpointProofRetentionTests.cs`, with its own `IClassFixture<ForumFixture>`. Their 35 s and 60 s clock advances then cannot age another fact's token, nonce or assertion, as `DedupeEndpointTests` and `InboxEndpointTests` already arrange.
- `R5_14_AProofDatedAheadIsNotAcceptedAgainOnceTheOldRetentionLapses`:
  - Build one proof with `iat = forum.Now + 20 s`.
  - Send it with a fresh client assertion: expect 200.
  - `forum.Clock.Advance(TimeSpan.FromSeconds(35))`.
  - Send the same proof with a new client assertion (new jti, iat at the new now): expect 400 `invalid_dpop_proof`, and after M5 a captured Type of `curia/authn/replay` (the slug `AuthNErrors.Replay()` carries).
- `R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain`: as the fact above, but with `iat = forum.Now + 30 s` and an advance of exactly 60 s.

Both are RED against the verbatim move, and green once the expiry line is written (see the `DpopProofValidator.cs` step).

And in `tests/Curia.AuthN.Tests/DpopProofRetentionTests.cs` (new), `R5_14_ConsumeHoldsAProofUntilItsIatPlusSkew`. It uses an in-memory `IReplayCache` that records the expiry it is given. Call `ConsumeAsync` at `now` with `Claims.Iat = now + 20 s`. Assert that the recorded expiry is `>= iat + MaxSkew` and `> iat + MaxSkew` (strict). In the same file, `R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew`. Build the access token and proof as `AccessTokenValidatorReplayTests` builds them, with the proof's `iat = now + 20 s`, and run them through `AccessTokenValidator`'s validation with the recording `IReplayCache`. Assert that the request is accepted, and that the expiry recorded for the proof's `jti` is `> iat + MaxSkew` (strict). Assert by the proof's `jti`, never by the only or the last insert. This fact is what shows the resource path calls `ConsumeAsync` rather than keeping its own insert.
- Build them through `AccessTokenValidatorReplayTests`' scenario helper, `AccessTokenScenario` (`tests/Curia.AuthN.Tests/Support/AccessTokenScenario.cs`): `scenario.SignAccessToken()`, then `scenario.SignDpopProof(token, payload: p)` where `p` is `scenario.ValidDpopPayload(token)` with `p["iat"] = TestJwt.ToUnixSeconds(scenario.Iat + TimeSpan.FromSeconds(20))`, sent as `scenario.ValidRequest(token, proof)` under `scenario.Context with { ReplayCache = recording }`. Do not hand-roll a second builder.
- The recording `IReplayCache` delegates every insert to a real `InMemoryReplayCache` and returns its result, so the request is accepted because the insert succeeded, not because the fake said `true`.
- Before the expiry comparison, assert that exactly one insert was recorded for the proof's `jti`. A missing record fails there, never passes the comparison vacuously.
- This fact compiles on the base. Run it on the unextracted base before any source change (its red line is the base's `now + MaxSkew`), and again against the verbatim move below. Record both red lines under D29.

- [ ] Write the facts. Run them on the base: every fact but the last must be **RED** (each 200 today). Record each status line under D29 in the register (Task 0.4). `D29_AProofSentTwiceIsRefusedTheSecondTime`'s second send is 200 on the base. Record it. The R5.14 facts' recorded red lines are the ones against the verbatim move, below.
- [ ] Create `DpopProofValidator.cs`. Move `AccessTokenValidator.cs:120`–`:177` into `Validate`, keeping the order, the error constructors and comments, with `ath` checked only when `expected.AccessToken` is non-null. Move `:201`–`:209` into `ConsumeAsync`, changing only the expiry. Write it exactly `var proofExpiry = (proof.Claims.Iat > now ? proof.Claims.Iat : now) + AuthNConstants.MaxSkew + TimeSpan.FromSeconds(1);` with the comment `// R5.14: a proof is acceptable until iat + MaxSkew (Validate's skew test is |iat - now| <= MaxSkew), so its jti is held at least that long. The second is because PostgresReplayCache readmits a jti whose expires_at <= now, and at exactly iat + MaxSkew Validate still accepts.` This is the one deliberate change in the move. F0-11 anchors on it. The `cnf.jkt` comparison moves into `Validate` at its current position (after the thumbprint, before the R5.21 pin), written exactly `if (expected.ExpectedThumbprint is { } jkt && !FixedTimeEquals(thumbprint, jkt))`; `AccessTokenValidator` passes `claims.CnfJkt` and keeps the nonce block between `Validate` and `ConsumeAsync`. It binds `Validate`'s result as `validatedProof` (`if (!proofResult.TryGetValue(out var validatedProof, out var proofError))`), and replaces the base's `:201`–`:209` with exactly:
  ```csharp
  var consumed = await DpopProofValidator.ConsumeAsync(validatedProof!, context.ReplayCache, now, cancellationToken).ConfigureAwait(false);
  if (!consumed.TryGetValue(out _, out var consumeError))
      return Result<ValidatedRequest>.Fail(consumeError!);
  ```
  Nothing after these three lines reads `consumed` or `consumeError`. F0-13 anchors on them. Write the `htu` check exactly `if (proofClaims.Htu != expected.Url)` and the skew check exactly `if (skew > AuthNConstants.MaxSkew)`; F0-1 and F0-2 anchor on them.
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
- [ ] R11.34's fence (`PortStringFenceTests.R11_34_…`) now goes red: TokenEndpoint reaches §4.10's types, and the deleted `DpopThumbprintOf`/`ProofKey` were the only host references to `AuthNErrors`, `CompactJws.Split` and `CompactJwsParts`. Record its red lines verbatim under D29. In `tests/Curia.Architecture.Tests/PortStringAllowlist.cs`, delete the five rows it reports stale and add these eleven, with Member strings copied from the fence's output: `DpopProofValidator.Validate(proof, verifiers)`, `DpopExpectation..ctor(HttpMethod, Url, AccessToken, ExpectedThumbprint)`, `ValidatedProof..ctor(Thumbprint)`, `DpopProofClaims..ctor(Htm, Htu, Ath, Nonce)`, each with the reason the ruling gives. If the fence names any other parameter or stale row, stop. Green in Release, and in Debug after a Debug solution build.
- [ ] Before writing the expiry line, with the move still verbatim (`now + AuthNConstants.MaxSkew`), keep a copy of that `DpopProofValidator.cs` outside the tree. Run the two R5.14 Api facts and the two AuthN facts against it, and record their red lines under D29. Then write the expiry line. Never go back with `git checkout --`.
- [ ] Run `Curia.AuthN.Tests` (all, including `AccessTokenValidatorOrderingTests`) and `Curia.Api.Tests`. Every test is green, OracleTests included after its re-pin below.
- [ ] The fuzzer's `perturbed-first` POST /oauth/token rows for `jws:proof:header/jwk/x` and `/y` now answer 400. If the fuzzer pins those rows as 200, update that pin and name D29 in its comment. Never add a ledger row.
- [ ] `RequestSurfaceTests` sends `OffCurveProof` to /oauth/token twice. Once the proof is validated before the assertion, both change. Neither may be re-pinned to the new status.
  1. `R11_33_NoHeaderARouteCannotReadIsAnsweredAsAServerFault` (base :279–:297).
     - Its first half becomes an assertion that the off-curve proof is refused: 400 with `error == "invalid_dpop_proof"`.
     - Its second half still needs a token bound to the off-curve key, because the resource path's key build (`JwkPublicKey`, `AccessTokenValidator`'s R11.33 refusal) stays as defence in depth. Mint that token another way:
       - Obtain a normal token through `agent.AuthenticateAsync`.
       - Keep its header segment byte for byte.
       - Parse its payload and set `cnf.jkt` to the RFC 7638 thumbprint of `OffCurveProof`'s jwk, computed with the same AuthN thumbprint helper the Forum uses (`JwkThumbprint.Compute`).
       - Re-sign `header.payload` with ES256 (IEEE P1363) using an `ECDsa` imported from `forum.IssuerSigningKeyPem`, as `Fuzz/FuzzContext.cs:47-48` and `:183` do.
     - Reword the failure message so it no longer anticipates D29.
     - Its last check, `read > 0`, counts any 401. A mis-minted token (wrong kid, a DER signature, a re-serialized payload) is refused as `signature-invalid` by every route, still 401, and would pass without reaching the key build. So also assert that at least one route's 401 body has `type == "curia/authn/malformed-jwk"` (resource 401s are problem documents carrying `error.Type`, `ForumEndpoints.cs:2278`).
     - Add a control: the same re-signing with `cnf.jkt` left as issued, sent with the agent's own on-curve proof, gets a non-401 answer on one authenticated read route.
  2. `R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown`'s five `inAssertion` rows (base :465).
     - Replace `OffCurveProof("POST", TokenEndpoint, forum.Now)` with a valid on-curve proof from a `ForumAgent`'s `dpop.Proof("POST", TokenEndpoint, forum.Now)`, with a fresh jti per row.
     - The rows keep 401 `invalid_client`.
     - Add a sentence to the summary: the proof must pass so that the assertion, and PostgresAgentKeyStore's blank-kid path, is reached at all.
  Run both on the post-D29 tree once with `OffCurveProof` still in place and record that they go red (six tests). Then make the change and record them green.
- [ ] `tests/Curia.Api.Tests/Fuzz/OracleTests.cs` `R14_10_TheTokenEndpointsProofRefusalsStopAtTheFirstParser`.
  - Its `sendProof: true` row sends the proof `a.b.c`. After D29 that proof is refused with a detail, so the row's exact match against `Oracle.TokenEndpointProofRefusal` goes red. Run it on the post-D29 tree before changing it, and record the red line under D29. That red line is this row's falsification: it shows the row sees a detail being added.
  - That run must show the body carrying a `detail`, i.e. a `problemType` other than `Oracle.TokenEndpointProofRefusal`. If it does not, the D29 red line was not observed: stop and report.
  - Then re-pin the `sendProof: true` row:
    - Assert `Oracle.Slug(problemType)` is a member of `Oracle.ParserRefusals`.
    - Assert `problemType == "invalid_dpop_proof " + <the slug that run served>`, written as a literal.
    - Keep `Assert.False(Oracle.PastFirstParser(...))`.
    - If the served slug is not in `ParserRefusals`, stop and report. An unreadable header refused by a later check would mean `Validate` reads past it.
  - The `sendProof: false` row is unchanged. A missing proof still serves no detail.
  - Re-pinning this row to the bare constant, or to "starts with invalid_dpop_proof", is a finding.
- [ ] Measure the random pass's floor (the first of two measurements; the second is in Task 0.2). Run `RequestFuzzRandomTests` at seed `0000001diyh1`, the seed of the 6cbfa9f measurement. Read the fraction of JSON and JWS draws past the first parser from the output line `RequestFuzzRandomTests.cs:203` writes. Do not record a prediction of which of the ~40 token-endpoint draws leave the count. After D29, an `htm` or `htu` draw answers `invalid_dpop_proof` with `curia/authn/method-mismatch` or `url-mismatch`, and `Oracle.PastFirstParser` still counts those. A non-numeric `iat` answers `curia/authn/malformed`, which it does not count. A `jti` draw that `Jti.Parse` accepts still answers 200. Record only the measured fraction. Record it under D29 as the runner's output line, verbatim (the count over the total), not a paraphrase. `Divisor` is set from Task 0.2's measurement, not this one, because Task 0.2's oracle change moves the fraction again.
- [ ] D29's old behaviour is cited as current in code comments and a test message. Reword each to the past tense, keeping its reason:
  - `tests/Curia.Api.Tests/Fuzz/RequestModel.cs:317`: the rebinding probe now exercises the resource path's key build as defence in depth. The token endpoint has refused an off-curve proof since D29 closed, and the harness mints the rebound token itself.
  - `src/Curia.AuthN/AccessTokenValidator.cs:152-153`. This moves with the extraction into `DpopProofValidator.cs`. Reword it there: until D29 closed, the token endpoint bound a token to a key it never built, and this refusal remains for any token so bound before then.
  - `src/Curia.AuthN/Dpop/JwkPublicKey.cs:36-38`: same tense change.
  - `tests/Curia.AuthN.Tests/AccessTokenValidatorDpopTests.cs:86`: same tense change.
  - `tests/Curia.Api.Tests/RequestSurfaceTests.cs:220`, the doc comment "…without building it (register D29)…": same tense change, and it describes the token the fact now mints itself.
  - `tests/Curia.Api.Tests/RequestSurfaceTests.cs:297`, the failure message, is reworded by the `RequestSurfaceTests` step above.
  Then grep `grep -rn 'register D29\|(D29)' src tests`, excluding the files this task creates. Every hit must be one of `Fuzz/RequestModel.cs`, `DpopProofValidator.cs`, `JwkPublicKey.cs`, `AccessTokenValidatorDpopTests.cs` and `RequestSurfaceTests.cs` at the places above, and none may be in `AccessTokenValidator.cs`. Stop if any hit is not one of these.
- [ ] Commit: `D29: the token request's DPoP proof passes the one proof validator`.

### Task 0.2: Coarse refusals, logged (M5)

**Red facts first** (`TokenEndpointRefusalTests.cs`):
- `M5_AKidRegisteredToAnotherAgentAndASignatureThatFailsReadTheSame`: two refusals, one the other agent's `kid` and one a bad signature. Both bodies are byte-equal: `{"error":"invalid_client","error_description":"The client could not be authenticated"}`.
- `M5_NoTokenRefusalCarriesADetail`: across every `TokenEndpointProofTests` and `TokenSubjectBindingTests` request, no body has a `detail` member.
- `M5_AServerErrorIsLoggedWithItsReason`: with the event reader failing, the body is `{"error":"server_error","error_description":"The Forum could not issue a token"}` and the captured log holds event 5000 with the reader's slug.
- `M5_ARefusalLogsItsSlugAndNoneOfTheCallersText`: send a token request whose DPoP proof header has `typ` = `"x\r\nfail: forged\u001b[31m\u202E"` followed by 2,000 `a`. In the test source, write `\u001b` and `\u202E` as C# escapes, never as literal characters. Expect 400 `invalid_dpop_proof`. Assert:
  - exactly one 5001 is captured for the request, and its Type is `curia/authn/typ-mismatch` (the slug `TypMismatch` carries);
  - the formatted message and every structured value of the captured event contain none of `\r`, `\n`, `\u001b` and `\u202E`, and neither `forged` nor `aaaa`.
  It is red on the base because no 5001 is logged.
- [ ] Facts RED on the base (detail present; for the log fact, no 5001).
- [ ] The capturing logger. `ServerFaultTests.Captured` keeps only `formatter(state, exception)`. Lift it into `tests/Curia.Api.Tests/Support/` so that each entry records `eventId.Id`, the formatted message, and the state's `IReadOnlyList<KeyValuePair<string, object?>>`. Every "captured Type" in PR 0, F0-10 and the M5 log fact read the `Type` value from that list. Without it, "every structured value contains none of …" checks an empty set and passes vacuously. Each fact and re-pin that reads a captured Type first asserts that **exactly one** 5001 (or 5000) was captured for its request.
- [ ] Create `OAuthRefusal.cs` (spec §4.11) as `internal sealed partial class OAuthRefusal(string code, string description, Error? reason) : IResult`, with `[LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Token request refused {Code}: {Type}")] private static partial void Refused(ILogger logger, string code, string type);` and `[LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Served {Status} {Type} without its detail, which was: {Detail}")]`. It is called exactly as `Refused(logger, code, reason?.Type ?? string.Empty);`. F0-12 anchors on it. A 4xx logs its slug and never `reason.Detail`, which can hold a header value the caller chose (spec §4.11).
- [ ] Replace `OAuthError(code, description, detail)` with `OAuthRefusal`. `invalid_client`, `invalid_dpop_proof` and `server_error` take the fixed descriptions; `invalid_request` keeps its text. The old `detail` argument becomes `reason`.
- [ ] Fuzzer oracle. Once `detail` goes, `Oracle.ProblemType` for `/oauth/token` is the bare `error`, `Slug` is empty, and `Reached` and `PastFirstParser` count every `invalid_client` — `signature-invalid` included — as reach (`Oracle.cs:99`–`:103`, `:122`–`:149`); the reach clause (`FuzzRun.cs:297`) goes green reaching nothing. Lift `ServerFaultTests`' capturing logger into `tests/Curia.Api.Tests/Support/` and have `FuzzContext` capture events 5001 and 5000 per request (assert that the fuzzer sends one request at a time, or key the capture by trace identifier). `ProblemType` for `/oauth/token` becomes `error + " " + <the captured Type>`, and a token refusal with no captured reason counts as not reached. Rewrite `TokenEndpointProofRefusal`'s comment for D29: `invalid_dpop_proof` now covers signature and claim refusals, classified by the captured slug. Fact `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason`: a recorded `invalid_client` whose captured reason is `curia/authn/signature-invalid` is not reached, and the same body with no capture is not reached either.
- [ ] Re-pin every token-refusal assertion to the coarse body **and** the captured event's `Type`. The list is by file and test, with lines as at 3d2cf24. Task 0.1 moves RequestSurfaceTests' lines, so compare by content, not line number.
  - `KeyBindingTests.cs`: :36 (the expected body) and :218 (the `server_error` InlineData; its captured event is 5000). The :210 doc comment is reworded if it names `detail`.
  - `StoredKeyFormTests.cs`: :48, :54, :299.
  - `TokenSubjectBindingTests.cs`: :39, :283, :308, :310 (expected bodies); :214 (`detail ?? error_description` == `curia/keys/not-bound-by-the-log`, so after M5 it compares the fixed description and goes red); :241 (`detail` == `curia/authn/subject-mismatch`).
  - `RequestSurfaceTests.cs`: :473 (the inAssertion rows, per Task 0.1) and :608–:609. :609 asserts `detail == "curia/authn/malformed"` for the nine token jti rows, so re-pin it to the captured 5001 Type == `curia/authn/malformed`.
  - The doc comments at RequestSurfaceTests :438 and :565 stay.
  - `Fuzz/FuzzRun.cs` (:385 at 3d2cf24) and `Fuzz/RequestFuzzRandomTests.cs` (:155): each `Oracle.ProblemType(` call passes the captured Type (gate 3).
  - `Fuzz/OracleTests.cs`.
    - The test runs on a plain `ForumFixture`. Give it the lifted Support/ capturing logger through `forum.WithWebHostBuilder(... ConfigureTestServices ...)`, as `ServerFaultTests` does.
    - Build a separate `WithWebHostBuilder` factory, with its own capture, per row, so the two theory rows never share a logger. If that is not possible, count only the captures recorded after that row's send.
    - Pass the captured Type to `ProblemType` the way `FuzzContext` does.
    - Each row first asserts exactly one 5001 for its request.
    - `sendProof: true`: assert the captured Type is the slug recorded in Task 0.1, and that `problemType == "invalid_dpop_proof " + slug`.
    - `sendProof: false`: the missing-proof refusal keeps a null reason. Assert that the captured Type is empty and that `problemType == Oracle.TokenEndpointProofRefusal` exactly, and keep the PastFirstParser assertion.
    - Reword `TokenEndpointProofRefusal`'s comment: after M5 it matches only a token refusal logged with no reason, which is today the missing proof.
    - Reword OracleTests' summary from "a detail added to them fails here" to "a reason logged for them that is not a parser refusal fails here".
    - Do not give the missing-proof refusal `AuthNErrors.MissingDpopProof()` as its reason in this PR: `curia/authn/missing-dpop-proof` is not in `ParserRefusals`, so it would count as past the first parser and move the floor. Task 0.4 records this under M5 as observed, not acted on.

  The five `inAssertion` rows also assert the captured event 5001's `Type`. That is the assertion-side slug the base serves today as `detail` for each row (the five values recorded from a base run in Task 0.1's base observations), and never a `curia/authn/*` proof slug such as malformed-jwk.

  Gates 1 and 2 must each print exactly the hits named above, or stop and report; gate 3 has its own stop rule. Run gates 1 and 2 with `--exclude` for the files Tasks 0.1 and 0.2 create (`TokenEndpointProofTests.cs`, `TokenEndpointProofRetentionTests.cs`, `TokenEndpointRefusalTests.cs`, and the file holding `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason`), and compare by file and line content, not by line number:
  1. `grep -rn 'invalid_client\|server_error' tests/Curia.Api.Tests`. 14 lines at 3d2cf24: RequestSurfaceTests :438 :473 :565 :608; StoredKeyFormTests :48 :54 :299; KeyBindingTests :36 :210 :218; TokenSubjectBindingTests :39 :283 :308 :310.
  2. `grep -n '\["detail"\]\|GetProperty("detail")' tests/Curia.Api.Tests/KeyBindingTests.cs tests/Curia.Api.Tests/StoredKeyFormTests.cs tests/Curia.Api.Tests/TokenSubjectBindingTests.cs tests/Curia.Api.Tests/RequestSurfaceTests.cs`. At 3d2cf24 this finds TokenSubjectBindingTests :214 and :241, and RequestSurfaceTests :609 and :773. :773 is a /v1 problem document and stays.
  3. `grep -rn 'Oracle.ProblemType(\|TokenEndpointProofRefusal' tests/Curia.Api.Tests`, excluding `Fuzz/Oracle.cs`. At 3d2cf24 this prints FuzzRun.cs:385, RequestFuzzRandomTests.cs:155, OracleTests.cs:62 and OracleTests.cs:63. Line numbers and the hit count move with the re-pin, so judge the hits by call shape after Task 0.2:
     - every `Oracle.ProblemType(` hit passes the captured Type argument;
     - every `TokenEndpointProofRefusal` hit is in OracleTests' `sendProof: false` branch.
     Any other hit is a stop.

  Gate 1 cannot see a `detail` reader that does not also name the code, which is why gate 2 exists. A re-pin to the body alone passes whichever check refused it, and is a finding. `RefusalClassificationTests.cs:123` is a client stub and stays.
- [ ] Re-measure the random pass's floor after the oracle change above, which counts a token refusal with no captured reason as not reached. Run `RequestFuzzRandomTests` at seed `0000001diyh1` and read the fraction from the output line `RequestFuzzRandomTests.cs:203` writes; record that line verbatim (the count over the total). Recompute `Divisor` (`tests/Curia.Api.Tests/Fuzz/RequestFuzzRandomTests.cs:44`) from this second measurement, by the method its doc comment at :36-:43 states: half the measured fraction, rounded so the floor is not raised past the measurement. If the value changes, change it and its comment's numbers. If it does not, record the new fraction in the comment anyway. A floor measured with draws that reached nothing is a floor that gates nothing. Record both fractions (Task 0.1's and this one) and the Divisor under D29.
- [ ] Grep `src/Curia.Client` for any branch on a token refusal's `detail`. If one exists, stop and report: the client's classification would change.
- [ ] `src/Curia.Client/ForumClient.cs`, `ReadProblem`'s summary (:481-:484 at 3d2cf24). Replace "<c>/oauth/token</c> answers RFC 6749's <c>{"error","error_description"}</c> plus a non-standard <c>detail</c> carrying the internal slug." with "<c>/oauth/token</c> answers RFC 6749's <c>{"error","error_description"}</c>. Until M5 closed it also served a non-standard <c>detail</c> carrying the internal slug; a <c>detail</c> is still read when present, so an older Forum's answers classify as before." Change no code (the `detail` read stays, ForumClient.cs:499-503 at 3d2cf24). Then `grep -rn 'non-standard' src/Curia.Client` must print exactly one hit: the reworded line. Stop and report on any other hit.
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
| F0-9 | `Oracle.cs` | `ProblemType`'s captured-reason term removed, so token refusals classify by body only | `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason` and OracleTests' `sendProof: true` row |
| F0-10 | `ClientAssertionValidator.cs` | `CompactJws.AsMalformed(kidError, "kid", null)` → `AuthNErrors.Replay()` | the four kid rows of `R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown` (absent, empty, white space, number), by their captured Type |
| F0-11 | `DpopProofValidator.cs` | `(proof.Claims.Iat > now ? proof.Claims.Iat : now) + AuthNConstants.MaxSkew + TimeSpan.FromSeconds(1)` → `now + AuthNConstants.MaxSkew` | both R5_14 Api facts and both R5_14 AuthN facts |
| F0-11b | `DpopProofValidator.cs` | the same anchor → `(proof.Claims.Iat > now ? proof.Claims.Iat : now) + AuthNConstants.MaxSkew` | `R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain` and both AuthN facts' strict clause |
| F0-12 | `OAuthRefusal.cs` | `Refused(logger, code, reason?.Type ?? string.Empty);` → `Refused(logger, code, (reason?.Type ?? string.Empty) + " " + reason?.Detail);` | `M5_ARefusalLogsItsSlugAndNoneOfTheCallersText` |
| F0-13 | `AccessTokenValidator.cs` | the `ConsumeAsync` call → the base's inline insert with expiry `now + AuthNConstants.MaxSkew` | `R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew` |

- [ ] Before the first patch, keep copies outside the tree of every file a row patches: `DpopProofValidator.cs`, `TokenEndpoint.cs`, `OAuthRefusal.cs`, `Oracle.cs`, `ClientAssertionValidator.cs`, `AccessTokenValidator.cs`.
- [ ] F0-13's anchor is the three lines Task 0.1 pins in `AccessTokenValidator.cs` (`var consumed = await DpopProofValidator.ConsumeAsync(validatedProof!, context.ReplayCache, now, cancellationToken).ConfigureAwait(false);` and its `if` and return). Its replacement is exactly:
  ```csharp
  var proofExpiry = now + AuthNConstants.MaxSkew;
  var insertResult = await context.ReplayCache
      .TryInsertAsync(validatedProof!.Claims.Jti, proofExpiry, cancellationToken)
      .ConfigureAwait(false);
  if (!insertResult.TryGetValue(out var inserted, out var insertError))
      return Result<ValidatedRequest>.Fail(insertError!);

  if (!inserted)
      return Result<ValidatedRequest>.Fail(AuthNErrors.Replay());
  ```
  Build the F0-13 mutant once before counting it. A compile error is not RED: an F0-13 that fails to build tested nothing, so stop and report rather than count it.
- [ ] Restore each case by plain copy (`cp` without `-p`, or `shutil.copyfile`; never `cp -p` or `shutil.copy2`, whose kept mtime lets MSBuild keep the patched DLL), rebuild with `--no-incremental`, and run once unpatched before the next case.
- [ ] Expect `falsify: 14 cases, 0 not RED` (F0-1…F0-9, F0-10, F0-11, F0-11b, F0-12, F0-13). A green case is a finding (trap 13).
- [ ] Commit nothing from this task except the runner's record, which goes into Task 0.4.

### Task 0.4: Register and documents (PR 0)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - **D29** closed: the facts, the five proof-ordering facts, the base's red lines, and F0-1…F0-5, F0-8, F0-10. The two `RequestSurfaceTests` changes: the off-curve proof is refused at the token endpoint and the rebound token is minted by the test, and the `inAssertion` rows send an on-curve proof so the assertion is reached. R5.14's retention corrected: a proof's jti is held until max(now, iat) + MaxSkew + 1 s; F0-11, F0-11b, F0-13. The two random-pass fractions and the Divisor, as the runner's output lines verbatim, with no expected delta.
  - The register note at :5013-:5017 ("…When D29 closes, these draws become refusals, and Divisor should be re-measured") is marked acted on, with the measurements Tasks 0.1 and 0.2 recorded. It also records that its expectation held only in part: `htm` and `htu` draws became refusals that still count past the first parser, and parsable `jti` draws still answer 200. The fraction fell by what the measurement shows, not by ~40 draws.
  - **M5** closed: under "Observed during the strangers stage", the token-endpoint bullet is struck through with the facts and F0-6/F0-7/F0-9/F0-12; a 4xx logs its slug only.
  - Under **M5**, observed, not acted on: the missing-proof refusal still logs no reason. Giving it `AuthNErrors.MissingDpopProof()` would make the fuzz oracle count `curia/authn/missing-dpop-proof`, which is not in `ParserRefusals`, as past the first parser and move the floor, so PR 0 leaves it.
  - The Start-here line for PR 0 is marked merged-pending.
- [ ] `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, G21's applied-changes table, row `| §5.5, §5.6 |` (line 8402 at 3d2cf24). Re-read the errata immediately before the edit. After "as G16 said they would." insert "R5.14 holds for a proof only once its jti is retained strictly beyond the last instant at which `Validate` accepts it, max(consume time, `iat`) + the permitted skew, and not consume time + skew as the resource path retained it before; a proof dated ahead was acceptable again in between. The Forum holds it one second beyond that instant, because its replay cache readmits a jti at its expiry." Before running check-spec, grep the inserted sentence for `strictly beyond`. A bound written without it is F0-11b's mutant stated as normative text. Then run `python3 tools/spec-checks/check-spec.py` and get clean. No new requirement number: this is R5.14 as written, which it now meets.
- [ ] `CLAUDE.md`: the parenthesis "(the token endpoint's RFC 6749 server_error still carries its slug, the key-binding stage's M5 in the register)" is removed.
- [ ] Re-read every file:line you wrote. Scan added lines for invisible characters.
- [ ] Commit: `Register: D29 and M5 closed`.

### Task 0.5: Every gate, then PR 0

- [ ] Every command in `CLAUDE.md`'s gate list, in order: Release `dotnet test` with `--filter "FullyQualifiedName!~ScreeningCostTests"`; the separate `ScreeningCostTests` run with `CURIA_SCREEN_TIMINGS` and `CURIA_SCREEN_SCALING`; Architecture in Debug after a Debug build; `--locked-mode` restore; both spec checks; `cargo fmt --check`, `clippy -D warnings`, `cargo test --locked`; the differential with `--fail-on-divergence` after building its two endpoints. Eleven assemblies.
- [ ] Every file in `git diff --name-only origin/main...`, other than this plan and the spec, falls under a File Structure row whose Task cell names a PR 0 task. Stop on any that does not. The table's own paths are checked against the task blocks with the table excluded (`sed '/^## File Structure/,/^---$/d' <plan> | grep -c '<path>'` ≥ 1 per path), never with `grep -c` over the whole plan, which counts the table row itself.
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
