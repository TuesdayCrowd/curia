# Keys That End: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-11-keys-that-end.md`

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax. **Every subagent runs on Opus** (`model: "opus"`).

**Goal:** Close **D29** and **M5**. Then build G21: rotation, self-retirement, the per-identity lifecycle, R5.22, R6.56, and the three readers folding the lifecycle. Then build the operator's compromise declaration and disputed content.

**Architecture:**
- **PR 0** (branch `token-proof-verified`): Tasks 0.0–0.5. No requirement applied, and no event, table or grant changes; G21 is committed as proposed.
- **PR 0.5** (branch `lifecycle-guard`): Tasks L.0–L.5. The lifecycle guard (spec §4.13, R4.38 (add.)), alone, merged before PR 1 starts, under the same base rule. Its body names the NEEDS item.
- **PR 1** (branch `key-rotation`): Tasks 1.0–1.12. Opened from `main` after PR 0.5 merges, and only after GitButler's target base is the merged `main` and `but status` shows none of PR 0.5's commits.
- **PR 2** (branch `key-compromise`): Tasks 2.0–2.6, right after PR 1 merges (not deferred to the lift; spec §4.1, amended), under the same base rule. The five-agent exercise and TUI work may run between PR 1 and PR 2, with the guard on.
- No `db/` migration (spec §4.2). **No producer appends to a retained log until the lift. Vectors are proposals until the lift record pins their digests.** Vectors still land before any producer. Lifting the guard is the owner's act and is in no PR of this plan.

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
- **The guard (R4.38 (add.)).** No task appends a lifecycle entry beyond an enrolment's pair to a retained log: the local Forum database used for the `curia` skill and the five-agent exercise, or any `CURIA_EVENTS_POSTGRES` database. `CURIA_TEST_POSTGRES` is an admin connection to the same server, so a test composition pointed at a retained database is a path to the freeze: every fixture that injects the permissive gate refuses a database whose name does not carry the prefix it generated in the same run (`curia_e2e_` for `ForumFixture`, `curia_test_` for `PostgresDatabaseFixture`; `R4_38_APermissiveCompositionRefusesADatabaseItDidNotCreate`).
- No task edits `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json` after PR 0.5 creates it empty, or guard fact GF6, `R4_38_TheApprovedSetIsEmpty` (the guard facts GF1–GF10 are spec §4.13's falsifiers, named apart from the errata's entries G1–G21). Any diff that does is a stop. This freezes the JSON file and GF6's file only, not the C# type `LifecycleApprovalRecord` (Task L.1), whose members PR 0.5 defines.
- Every lifecycle signature carries its kind's header `typ` (spec §4.5, KF1). No task signs a statement as `SubmissionBuilder` signs a post or as R6.49 signs a head.

**Checkpoints (hopper-doctrine).**
- **CP-A** (end of Task L.2): run the production composition (`GuardedForumFixture`, Task L.2); assert the resolved gate's type is `EmbeddedLifecycleAppendGate`, the approved set is empty, and (once Task 1.6 lands) the key set's `curia_lifecycle_kinds` is `{"approved":[],"enabled":[]}`.
- **CP-B** (before each PR leaves draft, and before merge): GF6 green in Release and Debug; `git diff origin/main...HEAD --name-only` names neither the approval record nor GF6's file (PR 0.5 excepted, where it creates both, the record empty); the falsify output includes the PR's GF rows with `0 not RED`; the PR body says "merging does not freeze; the lift is the owner's separate act". Decision agents may merge green PRs, so CP-B is the only gate against an agent merging a lift before NEEDS is met.
- **CP-C** (any local Forum run, the five-agent exercise included): the guard is on, and the count of lifecycle-kind entries beyond enrolment pairs in the retained log is 0 before and after (event 7006 is the detector). Nonzero is a divergence: halt and report.
- **CP-D** (Task 2.5's dry run): the database name came from that run's own create step, checked before any append.

**Naming.** The owner's "Q1: Yes." (2026-10-11) accepted G21's cost item 9, the takeover. It is not Task 1.0's "owner question 1" (the Table 10 rows), which proceeds on the default yes curia-architect keeps as spec authority; that is not an owner answer.

**Numbering:** G21; R4.38, R4.39, R5.22, R6.56, R6.57, R7.23; R4.18 (rev.), R4.31 (rev. 2), R4.35 (rev.), R6.26 (rev.), R6.30 (rev.), R6.54 (add.), R4.38 (add.). Task 0.0 re-derives these and **stops** if the tree disagrees; Task L.0 re-derives R4.38 (add.).

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
- Every mutant must compile under the repository's analyzer settings; a mutant that fails to build is a broken row, not a red one. (`Directory.Build.props` sets `AnalysisLevel` `latest-all` and `TreatWarningsAsErrors`, so a string call without an ordinal or invariant argument, such as `StartsWith(string)`, fails the build.)

## Review Focus

1. **PR 0.** The token endpoint and the resource server run one validator. The resource path's order and error types are byte-identical to before. No refusal from `/oauth/token` carries `detail`.
2. **PR 0.5.**
   - Every INSERT into the events table passes the gated core; the exact-pair rule admits `[agent.enrolled, agent.key-bound]` at `AggregateVersion.New` and nothing else of the three shapes.
   - The approval record is embedded, empty, and fails closed when missing.
   - The permissive gate is reachable from no `src/` or `tools/` assembly.
3. **PR 1.**
   - Every lifecycle route's gate is its handler's first act, committed with the route.
   - Every lifecycle signature names its kind's `typ`; header `kid`s are their roles' members.
   - Every lifecycle append is conditioned on the identity stream's version.
   - Every post append is conditioned on its author stream's version.
   - Statements are read from canonical bytes, and every entry member equals its statement's.
   - A retirement can be signed only by the key it retires.
   - The fence has no new row.
   - Readers verify rotations recursively, and check the link after the post.
4. **PR 2.**
   - The operator verb asks the gate before it reads the log key.
   - The declaration is signed under `curia-key-compromise+jws`, and readers recompute its cascade (KF3).
   - The declaration verifies under the log key set and never under an agent key.
   - `t_c` is floored at the binding.
   - A declaration's log key was published before it, in both readers, and the operator refuses an unpublished one.
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
| `src/Curia.Application/Ports/ILifecycleAppendGate.cs` (new), `src/Curia.Infrastructure/Lifecycle/{EmbeddedLifecycleAppendGate.cs, LifecycleApprovalRecord.cs, lifecycle-approval.json}` (new), `src/Curia.Infrastructure/{PostgresEventStore.cs, PostgresAdapters.cs, Curia.Infrastructure.csproj}`, `src/Curia.Domain/DomainErrors.cs`, `src/Curia.Api/Program.cs`, `src/Curia.Operator/Program.cs` | the guard, the chokepoint, lock 2, event 7006 | L.1, L.2 |
| `tests/Shared/PermissiveLifecycleAppendGate.cs` (new, `internal sealed`), linked by `<Compile Include="../Shared/PermissiveLifecycleAppendGate.cs" Link="Shared/PermissiveLifecycleAppendGate.cs" />` in six project files: `tests/Curia.Application.Tests/Curia.Application.Tests.csproj`, `tests/Curia.Infrastructure.Tests/Curia.Infrastructure.Tests.csproj`, `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`, `tests/Curia.Client.Tests/Curia.Client.Tests.csproj`, `tests/Curia.Mcp.Tests/Curia.Mcp.Tests.csproj` and `tests/Curia.Architecture.Tests/Curia.Architecture.Tests.csproj`; `tests/Curia.Application.Tests/InMemory/InMemoryEventStore.cs`, `tests/Curia.Application.Tests/InMemoryEventStoreTests.cs`, `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, every `new PostgresEventStore(` and `new InMemoryEventStore(` in `tests/`, `tests/Curia.Infrastructure.Tests/LifecycleGuardTests.cs` (new; every fact touching `LifecycleApprovalRecord` or `EmbeddedLifecycleAppendGate`, and GF3's and GF4's production-gate runs), `tests/Curia.Api.Tests/LifecycleGuardStartupTests.cs` (new), `tests/Curia.Architecture.Tests/LifecycleGateFenceTests.cs` (new) | GF3–GF8, GF10, fixtures | L.1, L.2, L.3 |
| `tools/GenerateEnvelopeFixtures/{Fixtures.cs, Lifecycle.cs (new)}`, `conformance/acta/{key-rotated-entry,key-retired-entry,key-bound-entry-es256}/`, `conformance/lifecycle/*`, `conformance/index.json` | proposed formats | 1.1 (acta), 1.9 (lifecycle), 2.0 (compromise) |
| `tests/Curia.Canon.Tests/Jws/DetachedJwsTests.cs`, `rust/curia-testis/src/jws.rs` (tests), `src/Curia.Client/ActaCheck.cs` (head verifier) | each lifecycle `typ` through `b64`/`crit`; head refuses lifecycle `typ`s | 1.9, 2.3 |
| `tests/Shared/test-signer.py`, `tests/Shared/TestSigner.cs`, `src/Curia.Client/AgentSigners.cs` (`ExternalSigner`: the `sign-lifecycle` verb, and its doc list of the properties that matter) | signer verb split (R4.39, R11.20) | 1.8 |
| `tests/Curia.Canon.Tests/Vectors/ActaLeafVectorTests.cs`, `rust/curia-testis/tests/vectors.rs` | leaf recomputation of the new vectors | 1.1 |
| `tests/Curia.Api.Tests/ForumFixture.cs` (composes the permissive gate by default; the `curia_e2e_` prefix check), `tests/Curia.Api.Tests/GuardedForumFixture.cs` (new; re-registers the embedded gate), `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs` (the `curia_test_` prefix check), `tests/Curia.Application.Tests/EventStorePortContractTests.cs`, `tests/Curia.Infrastructure.Tests/PostgresEventStoreContractTests.cs` | fixtures and the contract suite | L.2 |
| `tests/Curia.Architecture.Tests/EventStoreWriteSurfaceTests.cs`, the contract suite above | the conditioned overload | 1.2 |
| `tests/Curia.Application.Tests/Credentials/{KeyLifecycleTests.cs, RotateKeyTests.cs, RetireKeyTests.cs}` (new), `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, `tests/Curia.Application.Tests/InMemory/InMemoryAuthorKeyRegistry.cs` | lifecycle and use-case facts; `AddAsync` | 1.3, 1.4 |
| `tests/Curia.Application.Tests/Ingest/KeyPositionTests.cs` (new) | R6.56 at ingest | 1.5 |
| `src/Curia.Domain/Authorization/AccessPolicy.cs`, `tests/Curia.Domain.Tests/Authorization/{PublishedTable10.cs, AccessPolicyTests.cs}`, `tests/Curia.Api.Tests/KeyRotationEndpointTests.cs` (new), `tests/Curia.Api.Tests/Fuzz/Exemplars.cs` | quarantine, budgets, route facts, fuzzer exemplars | 1.6 |
| `tests/Curia.Api.Tests/TokenKeyLivenessTests.cs` (new), `src/Curia.Client/ForumSession.cs` | R5.22's facts; the client's re-mint | 1.7 |
| the test files Tasks 1.8, 1.9 and 2.1–2.3 name, in `Curia.Client.Tests`, `Curia.Mcp.Tests`, `Curia.Architecture.Tests` (the `curia-mcp` fence), `Curia.Application.Tests`, `Curia.Api.Tests` and `rust/curia-testis/tests/` | client, reader, operator and disputed facts | 1.8, 1.9, 2.1, 2.2, 2.3 |
| `src/Curia.Application/Ports/IEventStore.cs`, `src/Curia.Infrastructure/PostgresEventStore.cs`, the in-memory store, `src/Curia.Domain/DomainErrors.cs` | `StreamCondition` | 1.2 |
| `src/Curia.Domain/Keys/KeyStatements.cs` (new) | statement read/verify (pure) | 1.3 |
| `src/Curia.Application/Credentials/{KeyLifecycle.cs (new), EnrollmentBinding.cs, LogBoundKeys.cs, RotateKey.cs (new), RetireKey.cs (new), EnrollIdentity.cs}` | lifecycle, R4.35 (rev.), R4.31 (rev. 2), use cases | 1.3, 1.4 |
| `src/Curia.Application/Ingest/{IngestPipeline.cs, IngestPhases.cs, IngestErrors.cs}`, `src/Curia.Api/ForumEndpoints.cs` (`SubmitAsync`) | R6.56 | 1.5 |
| `src/Curia.Domain.Primitives/Boundary/{StatementText.cs, DetachedSignature.cs}` (new) | boundary types | 1.4 |
| `src/Curia.Domain/Authorization/{ResourceAction.cs, ResourceActionModel.cs, TierPolicy.cs}`, `curia-agent-forum-WHITEPAPER.md` (Table 10 rows; owner question 1) | Table 10, R7.23 | 1.6 |
| `src/Curia.Api/{ForumEndpoints.cs, Jwks.cs}` | routes, key set | 1.6 |
| `src/Curia.Api/Issuer/TokenIssuer.cs`, `src/Curia.AuthN/{AccessTokenClaims.cs, AccessTokenValidationContext.cs, AccessTokenValidator.cs, AuthNErrors.cs}`, `src/Curia.AuthN/Ports/IAgentKeyLiveness.cs` (new), `src/Curia.Api/Adapters/LogKeyLiveness.cs` (new) | R5.22 | 1.7 |
| `src/Curia.Client/{ProfileStore.cs, ForumClient.cs, KeyActs.cs (new), ForumDocuments.cs}`, `src/Curia.Client.Cli/{Program.cs, Hints.cs, Help.cs}` | client verbs; PR 2's exit 9 | 1.8, 2.3 |
| `tests/Shared/LifecycleRefusals.cs` (new, `internal static`), linked by `<Compile Include="../Shared/LifecycleRefusals.cs" Link="Shared/LifecycleRefusals.cs" />` in `tests/Curia.Api.Tests/Curia.Api.Tests.csproj` and `tests/Curia.Client.Tests/Curia.Client.Tests.csproj`; `tests/Shared/StubLog.cs` (the opt-in guarded mode) | GF9's stand-in and the fact that holds it to the guarded Forum | 1.8 |
| `src/Curia.Client/{ActaCheck.cs, PostVerifier.cs, SignatureCheck.cs, Passage.cs, LifecycleCheck.cs (new)}`, `src/Curia.Mcp/ForumTools.cs` | readers | 1.9, 2.3 |
| `rust/curia-testis/src/{acta.rs, jws.rs, lifecycle.rs (new)}`, `src/bin/curia-testis.rs`, `tests/log_author.rs`, `tests/lifecycle.rs` (new) | `curia-testis` | 1.9, 2.3 |
| `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md` | register and documents | 0.4, L.4, 1.11, 2.5 |
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

**Red facts first** (`tests/Curia.Api.Tests/TokenEndpointProofTests.cs`). Each sends a valid client assertion through `ForumAgent`'s token path with one proof part changed, and asserts `400`, `error == "invalid_dpop_proof"`, and the served `detail` equal to the slug of the check the changed part targets (table column added). Each slug is written as a literal and was confirmed by a run; a fact whose served slug differs is a stop, never an adopted value, because a mismatch means the fault is caught by some other check:

| Fact | Proof part changed | Slug |
|---|---|---|
| `D29_AProofWhoseIatIsThirtyDaysOldIsRefused` | `iat` 30 days old | `curia/authn/proof-window-exceeded` |
| `D29_AProofWhoseHtuNamesAnotherHostIsRefused` | `htu` names another host than the request's `Host` | `curia/authn/url-mismatch` |
| `D29_AProofWhoseHtmIsGetIsRefused` | `htm` is GET | `curia/authn/method-mismatch` |
| `D29_AProofWhoseTypIsNotDpopJwtIsRefused` | `typ` is not `dpop+jwt` | `curia/authn/typ-mismatch` |
| `D29_AProofSignedOverAnotherPayloadIsRefused` | 64 zero bytes as the signature | `curia/authn/signature-invalid` |
| `D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused` | header alg EdDSA over a P-256 jwk | `curia/authn/alg-key-mismatch` |
| `D29_AProofOverAnOffCurveKeyIsRefused` | key off the curve | `curia/authn/malformed-jwk` |

`D29_AProofSentTwiceIsRefusedTheSecondTime`'s second send is pinned the same way, to `curia/authn/replay`.

Plus:
- `D29_AProofSentTwiceIsRefusedTheSecondTime`: one proof and two client assertions, each with its own jti. The first send (proof + assertion 1) is 200. The second (the same proof + assertion 2) is 400 `invalid_dpop_proof`, and after M5 its captured 5001 Type is the replay slug. Resending the identical request is refused 401 `invalid_client` by the assertion's jti, on the base and after the fix alike, because ClientAssertionValidator shares the replay cache and inserts first. So it would test nothing, and loosening the fact to 'is refused' makes F0-3 vacuous. The fact asserts the status and the error code exactly, never 'not 200'.
- `D29_AProofRefusedForItsAssertionIsNotConsumed` (directly after the fact above): one proof, sent first with a client assertion whose signature is 64 zero bytes, then with a fresh valid assertion. The first send is 401, `error` is not `invalid_dpop_proof` and is exactly `invalid_client`, and (until M5) the served `detail` is `curia/authn/signature-invalid`, so the refusal came from the assertion's signature and not from the proof. The second send is 200, with the failure message `the proof was consumed by a request refused before authentication: {status} {body}`. It pins R5.13 with spec §4.10's order: a proof's jti is recorded only after the client assertion validates, so a caller with no credential writes no replay row (and so runs no prune; `PostgresReplayCache` prunes on insert), and a request refused for its assertion does not burn its proof. Neither assertion is ever loosened to 'not 200' or 'is refused'. It is green on the base and after D29 alike: its information is in its F0 row (F0-14), not in a red line.
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
     - Add a sentence to the summary: the proof must pass so that the assertion is read at all, and for the last four rows its kid check (Kid.Parse, before any key is resolved) is reached; the store's blank-kid throw is the history these rows guard against, not a path they reach. (The first row, `{"alg":"\ud800","typ":"JWT"}`, is refused when the assertion header is read, before Kid.Parse.)
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
  - `src/Curia.AuthN/Dpop/DpopProof.cs:34-36` (DpopProofClaims summary): names the type's one constructor, which the extraction moved; it carries no D29 marker, so the grep below cannot find it. It now reads that `ath` is read from every proof, a token request's included, and checked only when the expectation carries an access token, which `AccessTokenValidator` passes and the token endpoint does not; and that the type is today constructed only in `DpopProofClaims.Parse`, which only `DpopProofValidator.Validate` calls, a convention the public positional constructor does not enforce. `ValidatedProof`'s summary says the same of its constructor and of `ConsumeAsync`.
  Then grep `grep -rn 'register D29\|(D29)' src tests`, excluding the files this task creates. Every hit must be one of `Fuzz/RequestModel.cs`, `DpopProofValidator.cs`, `JwkPublicKey.cs`, `AccessTokenValidatorDpopTests.cs` and `RequestSurfaceTests.cs` at the places above, and none may be in `AccessTokenValidator.cs`. Stop if any hit is not one of these. The grep finds only comments that name D29. Also read every doc comment in `src/Curia.AuthN/Dpop/` and `AccessTokenValidator.cs` that names `AccessTokenValidator`, and check it against the move; `grep -rn 'only place this type is constructed\|AccessTokenValidator.s Phase 4\|every check a resource request\|only present (and only checked)\|constructed only through' src tests` must print nothing. The DpopProofValidator class summary names what the token request's proof is held to and what it is not: no ath, no cnf.jkt binding, no nonce.
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
- [ ] Fuzzer oracle. Once `detail` goes, `Oracle.ProblemType` for `/oauth/token` is the bare `error`, `Slug` is empty, and `Reached` and `PastFirstParser` count every `invalid_client` — `signature-invalid` included — as reach (`Oracle.cs:99`–`:103`, `:122`–`:149`); the reach clause (`FuzzRun.cs:297`) goes green reaching nothing. Lift `ServerFaultTests`' capturing logger into `tests/Curia.Api.Tests/Support/` and have `FuzzContext` capture events 5001 and 5000 per request (assert that the fuzzer sends one request at a time, or key the capture by trace identifier). `ProblemType` for `/oauth/token` becomes `error + " " + <the captured Type>`, and a token refusal with no captured reason counts as not reached, except a bare `invalid_request` (`Oracle.TokenEndpointFormRefusal`): the form checks' refusal (TokenEndpoint's not-a-form, unreadable-form, `client_assertion_type` and missing-field checks), which logs no reason because nothing past the form reader was parsed. It is a parser refusal, and D33 counts a parser refusal as reach, as the base did. *Ruled 2026-10-12 (curia-architect, on the implementer's stop): the unqualified rule turned five /oauth/token parts that only reach the form checks into reach failures in the closed pass ([urlencoded] form:client_assertion_type; [multipart] header:Content-Type, header:Content-Type;boundary, form:, form:client_assertion_type). Ledgering them is banned, and giving the form refusals a reason changes source and the floor. The exemption is exact equality on the bare code, so bare `invalid_client`, `invalid_dpop_proof` and `server_error` stay not-reach.* Rewrite `TokenEndpointProofRefusal`'s comment for D29: `invalid_dpop_proof` now covers signature and claim refusals, classified by the captured slug. Fact `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason`: a recorded `invalid_client` whose captured reason is `curia/authn/signature-invalid` is not reached, and the same body with no capture is not reached either; a bare `invalid_request` is reached, a bare `invalid_dpop_proof` is not. Fact `R14_10_TheTokenEndpointsFormRefusalIsLoggedWithNoReasonAndIsReach`, on a host and capture of its own: a non-form token request (`application/json`, body `{}`) is answered 400 `invalid_request` with exactly one 5001 whose Type is empty, its problem type equals `Oracle.TokenEndpointFormRefusal`, it is reached, and, as on main, it is past the first parser (ruling correction under Task 0.4).
- [ ] Re-pin every token-refusal assertion to the coarse body **and** the captured event's `Type`. The list is by file and test, with lines as at 3d2cf24. Task 0.1 moves RequestSurfaceTests' lines, so compare by content, not line number.
  - `KeyBindingTests.cs`: :36 (the expected body) and :218 (the `server_error` InlineData; its captured event is 5000). The :210 doc comment is reworded if it names `detail`.
  - `StoredKeyFormTests.cs`: :48, :54, :299.
  - `TokenSubjectBindingTests.cs`: :39, :283, :308, :310 (expected bodies); :214 (`detail ?? error_description` == `curia/keys/not-bound-by-the-log`, so after M5 it compares the fixed description and goes red); :241 (`detail` == `curia/authn/subject-mismatch`).
  - `RequestSurfaceTests.cs`: :473 (the inAssertion rows, per Task 0.1) and :608–:609. :609 asserts `detail == "curia/authn/malformed"` for the nine token jti rows, so re-pin it to the captured 5001 Type == `curia/authn/malformed`.
  - The doc comments at RequestSurfaceTests :438 and :565 stay.
  - `TokenEndpointProofTests.cs`: both helpers that read the served `detail` are re-pinned, `AssertInvalidDpopProof` (every proof-refusal fact, and `D29_AProofSentTwiceIsRefusedTheSecondTime`'s second send) and `D29_AProofRefusedForItsAssertionIsNotConsumed`'s first send. Each asserts exactly one captured 5001 for the request and that its Type equals the slug it pins today (Task 0.1's Slug column; `curia/authn/signature-invalid` for the assertion refusal). They no longer read `detail`, whose `GetProperty("detail")` throws `KeyNotFoundException` once M5 lands: that is the intended loud failure, and deleting the read without the captured-Type pin is a finding, as is re-pinning to the body alone. After M5 a 500 captures no 5001, so "exactly one 5001" is what turns a mutant answered 500 red.
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
- [ ] Re-measure the random pass's floor after the oracle change above, which counts a token refusal with no captured reason as not reached. Run `RequestFuzzRandomTests` at seed `0000001diyh1` and read the fraction from the output line `RequestFuzzRandomTests.cs:203` writes; record that line verbatim (the count over the total). Recompute `Divisor` (`tests/Curia.Api.Tests/Fuzz/RequestFuzzRandomTests.cs:44`) from this second measurement, by the method its doc comment at :36-:43 states: half the measured fraction, rounded so the floor is not raised past the measurement. If the value changes, change it and its comment's numbers. If it does not, record the new fraction in the comment anyway. A floor measured with draws that reached nothing is a floor that gates nothing. Record both fractions (Task 0.1's and this one) and the Divisor under D29. The form-refusal exemption (ruled above) changes `Oracle.Reached` only. The random pass reads `Oracle.PastFirstParser` alone (`RequestFuzzRandomTests.cs:162`), so the exemption does not move this measurement. The measurement already taken (629 of 1217, Divisor 4) stands.
- [ ] Grep `src/Curia.Client` for any branch on a token refusal's `detail`. If one exists, stop and report: the client's classification would change.
- [ ] `src/Curia.Client/ForumClient.cs`, `ReadProblem`'s summary (:481-:484 at 3d2cf24). Replace "<c>/oauth/token</c> answers RFC 6749's <c>{"error","error_description"}</c> plus a non-standard <c>detail</c> carrying the internal slug." with "<c>/oauth/token</c> answers RFC 6749's <c>{"error","error_description"}</c>. Until M5 closed it also served a non-standard <c>detail</c> carrying the internal slug; a <c>detail</c> is still read when present, so an older Forum's answers classify as before." Change no code (the `detail` read stays, ForumClient.cs:499-503 at 3d2cf24). Then `grep -rn 'non-standard' src/Curia.Client` must print exactly one hit: the reworded line. Stop and report on any other hit.
- [ ] Commit: `M5: the token endpoint's refusals are coarse and their reasons logged`.

### Task 0.3: Falsify PR 0

Count each anchor once against the tree after Task 0.2 and its review follow-ups, including the Task 0.3 review's comment fix in ClientAssertionValidator.cs; take the kept copies after that commit, from `git show <commit>:<path>`. That commit's diff contains no F0 anchor, so it does not change the anchor counts.

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
| F0-14 | `TokenEndpoint.cs` | the three lines from `var consumed = await DpopProofValidator.ConsumeAsync(validatedProof!, replayCache, ...)` through its return, moved to immediately after the `proofResult` refusal, before the line `// R5.20 (errata G15): the assertion's key is resolved for client_id, and its sub must name it.` (that full comment is the anchor; the bare `// R5.20` is not unique) | `D29_AProofRefusedForItsAssertionIsNotConsumed` |
| F0-15 | `DpopProofValidator.cs` | the R5.21 condition `!string.Equals(proofHeader.Alg, keyAlg, StringComparison.Ordinal)` → the same `&& expected.HttpMethod == "NEVER"` | `D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused` (by its captured Type; the AuthN R5_13 facts going red do not count for this row) |
| F0-16 | `Oracle.cs` | `&& !string.Equals(problemType, TokenEndpointFormRefusal, StringComparison.Ordinal)` → removed | `R14_10_TheTokenEndpointsFormRefusalIsLoggedWithNoReasonAndIsReach` and the bare-`invalid_request` assertion of `M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason` |

- [ ] Before the first patch, keep copies outside the tree of every file a row patches: `DpopProofValidator.cs`, `TokenEndpoint.cs`, `OAuthRefusal.cs`, `Oracle.cs`, `ClientAssertionValidator.cs`, `AccessTokenValidator.cs`. Take each copy from the commit, not the working tree (`git show <commit>:<path> > <kept>`, the commit being the Task 0.3 review's comment fix or later), and check that the kept `ClientAssertionValidator.cs` does not contain "Title and Type". Restore by plain `cp` (never `cp -p`), rebuild with `--no-incremental`, and finish with one unpatched green run.
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
- [ ] Build the F0-14 and F0-15 mutants once before counting them, by the same rule. For F0-15, record whether the fact went red by a different captured Type or by a 500 (after M5 a 500 captures no 5001, and the "exactly one 5001" assertion is what turns it red); a row red for an unknown reason is not counted. Probed on the Task 0.1 tree with the served `detail`, in Release: F0-14 compiled and its fact went red, the second send 400 `invalid_dpop_proof` with `curia/authn/replay`; F0-15 compiled and its fact went red by a different slug, 400 with `curia/authn/signature-invalid` in place of `curia/authn/alg-key-mismatch`.
- [ ] Restore each case by plain copy (`cp` without `-p`, or `shutil.copyfile`; never `cp -p` or `shutil.copy2`, whose kept mtime lets MSBuild keep the patched DLL), rebuild with `--no-incremental`, and run once unpatched before the next case.
- [ ] Expect `falsify: 17 cases, 0 not RED` (F0-1…F0-9, F0-10, F0-11, F0-11b, F0-12, F0-13, F0-14, F0-15, F0-16). A green case is a finding (trap 13).
- [ ] Commit nothing from this task except the runner's record, which goes into Task 0.4.

### Task 0.4: Register and documents (PR 0)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - **D29** closed: the facts (with `D29_AProofRefusedForItsAssertionIsNotConsumed`: a proof is consumed only after the assertion authenticates), each proof fact pinned to the slug of the check that refused it, the five proof-ordering facts, the base's red lines, and F0-1…F0-5, F0-8, F0-10, F0-14, F0-15. The two `RequestSurfaceTests` changes: the off-curve proof is refused at the token endpoint and the rebound token is minted by the test, and the `inAssertion` rows send an on-curve proof so the assertion is read and, for the last four rows, its kid check is reached. Observed: with the off-curve key-build refusal made unreachable (`if (!jwk.ToPublicKeyMaterial(kid: "").TryGetValue(out var proofKey, out var proofKeyError) && expected.HttpMethod == "NEVER")`, which compiles), `D29_AProofOverAnOffCurveKeyIsRefused` went red by a 500, an `ArgumentNullException` (parameter `key`) from the signature check given a null key, not by a different slug. R5.14's retention corrected: a proof's jti is held until max(now, iat) + MaxSkew + 1 s; F0-11, F0-11b, F0-13. The two random-pass fractions and the Divisor, as the runner's output lines verbatim, with no expected delta.
  - The register note at :5013-:5017 ("…When D29 closes, these draws become refusals, and Divisor should be re-measured") is marked acted on, with the measurements Tasks 0.1 and 0.2 recorded. It also records that its expectation held only in part: `htm` and `htu` draws became refusals that still count past the first parser, and parsable `jti` draws still answer 200. The fraction fell by what the measurement shows, not by ~40 draws.
  - **M5** closed: under "Observed during the strangers stage", the token-endpoint bullet is struck through with the facts and F0-6/F0-7/F0-9/F0-12; a 4xx logs its slug only.
  - Under **M5**, ruled: the oracle's no-reason rule exempts a bare `invalid_request` (the form checks). The closed pass before the exemption, with the 5 reach lines verbatim, is that exemption's falsification on the full pass. F0-16 is its standing falsification. Also observed: a bare `invalid_client` ('not enrolled', TokenEndpoint.cs:154,167, reason null) was reach on the base and is not reach after M5. No part depended on it. Whether a form-check refusal should carry a reason is D33's existing 'parser refusal counts as reach' observation, not a new entry. If it is acted on, that is option (a) with its own floor re-measure.
  - Under **M5**, accepted as built (curia-architect, 2026-10-12): the capture is per-host, not fixture-wide. The plan requires a per-row factory for OracleTests and does not require a fixture-wide capture for the re-pins. Task 0.5's File Structure check would refuse ForumFixture.cs. Each re-pin asserts exactly one 5001 per request on its own host, which is the information the plan wanted.
  - Under **M5**, accepted as built (curia-architect, 2026-10-12): the missing-proof refusal now serves 'The DPoP proof was not accepted'. The plan says invalid_dpop_proof takes the fixed description, and an invalid_dpop_proof that is not missing already serves that text, so the two being indistinguishable is M5's purpose. The reason stays null, as Task 0.2 requires.
  - Under **M5**, ruling correction (curia-architect, 2026-10-12): `R14_10_TheTokenEndpointsFormRefusalIsLoggedWithNoReasonAndIsReach` asserts `Oracle.PastFirstParser(400, "invalid_request")` is true, not false. `PastFirstParser` is unchanged from main, where the form refusals served `detail` as JSON null, which main's `ProblemType` did not append, so this refusal's problem type was already the bare `invalid_request` and already counted past the first parser. The test pins that value, and a `PastFirstParser` that excluded it turns the test red, which is what "the floor is untouched" means; making the floor exclude the form refusal would change it, and is out of M5's scope.
  - Under **M5**, observed, not acted on: the missing-proof refusal still logs no reason. Giving it `AuthNErrors.MissingDpopProof()` would make the fuzz oracle count `curia/authn/missing-dpop-proof`, which is not in `ParserRefusals`, as past the first parser and move the floor, so PR 0 leaves it.
  - Under **M5**, observed, not acted on (review of Task 0.2, 2026-10-12): `invalid_client`'s two null-reason refusals ('not enrolled', `TokenEndpoint`'s two `new OAuthRefusal("invalid_client", OAuthRefusal.ClientNotAuthenticated, null)` sites) log no slug, which departs from the letter of spec §4.11 ("R5.12's specific reason is the slug"). Giving them slugs needs new error slugs in the source, and would make two bare `invalid_client` refusals count as reach and as past the first parser, which changes the oracle and possibly the floor, so PR 0 leaves it, as it leaves the missing-proof refusal above.
  - Under **M5**, observed, not acted on (review of Task 0.2, 2026-10-12; one entry): the coarse body makes a `kid` registered to another agent and a failing signature equal in bytes, not in timing (`LogBoundKeys`' read, by reading the code, not measured); that leaks nothing, because the Forum serves the kid-to-agent mapping. An anonymous caller can force one event-log read per `/oauth/token` request with a public agent id and kid, which predates D29. `/oauth/token` has no rate limiter, and the register has no entry for one. Event 5001 carries no correlation id.
  - Under **M5**, deferred (review of Task 0.2, 2026-10-12): tightening `ProblemShape.NotAProblem` to the exact member set `{error, error_description}` for `/oauth/token`. It changes clause 2 of the closed fuzz pass and needs its own adequacy argument; the review checked neither whether any middleware under `/oauth/token` answers without `OAuthRefusal`. `M5_NoTokenRefusalCarriesADetail` holds the exact member set and each fixed description over the bodies its facts record.
  - Under **D33** (or the random pass's floor), observed, not acted on (review of Task 0.2, 2026-10-12): the floor counts the form checks' bare `invalid_request` as past the first parser, although nothing past the form reader ran. That is the same as main, where the form refusals served `detail` as JSON null and their problem type was the same bare code, and it was ruled untouched by M5. It is a known inflation of the floor (vacuity question 2), to be settled when the floor's definition is next opened.
  - Under **M5**, fixed in the review of Task 0.2: the fuzzer keeps a logged reason only by event id and logger category (5001 from `OAuthRefusal`; 5000 from it or `ServerFault`), falsified with a stray 5001 under another category; `M5_NoTokenRefusalCarriesADetail` pins every refusal's members and each fixed description, falsified with main's `detail = null` shape and with a slug appended to `invalid_dpop_proof`'s description; and `b51896f` pinned a problem type, `invalid_request` with the form refusal's text appended, that main never produced (main served the form refusals' `detail` as JSON null, so its problem type was the bare code), so that assertion was removed.
  - Under **M5**, fixed in the review of Task 0.3 (curia-architect, 2026-10-12): ClientAssertionValidator.cs's comment beside subjectText said TokenEndpoint serves an error's Title and Type. That was true when e083d6d (#84) wrote it and false after M5, where the token endpoint serves only the code and a constant description (fixed per code, except invalid_request's form-check texts; UnreadableRequests' bodies (400, 405, 413, 415) carry the status title) and OAuthRefusal logs a 4xx's code and slug only. Rewritten, comment only, and corrected in the review of 2882b63, which had called every description fixed, contrary to OAuthRefusal's summary. No fact or falsification row, because a comment has no behaviour to pin; F0-6 and F0-7 are what hold the served body, and F0-12 what holds the logged slug.
  - Under **D29**, observed, not acted on: `DpopProofClaims` and `ValidatedProof` are public positional records, so Parse-only and Validate-only construction is a convention, not a type fence, and the public `DpopProofValidator.ConsumeAsync` accepts a `ValidatedProof` that never passed `Validate`. The PortStringAllowlist rows for `DpopProofClaims..ctor` (Ath, Htm, Htu, Nonce) justify their raw strings by the same convention. Today the only construction sites are the `new DpopProofClaims(` in `DpopProofClaims.Parse` and the `new ValidatedProof(` in `DpopProofValidator.Validate`. Fencing them (internal constructors, with the allowlist rows and `Curia.Api`'s access adjusted) is a change to the R11.34 boundary-types work (D33), not to D29, so PR 0 leaves it and the two summaries now say it is a convention.
  - The Start-here line for PR 0 is marked merged-pending.
  - Every other pointer PR 0 made false is closed or rewritten in place: D25's closure (`server_error` no longer serves its slug) (its two-item list restored at the review of 013e951), D28's R5.21 item and the enrollment stage's R5.21 closure note (the token request's proof is now verified and pinned), and M5's origin bullet under the enrollment stage, and the strangers stage's timing note corrected for an unbound row. Found by the review of 91fe326 with `grep -nE '\bM5\b|\(D29\)'`, and re-swept at the review of 013e951 with `grep -nE '\bD29\b|\bM5\b'` over the plan, the errata, the white paper, the scoping document and CLAUDE.md, because the first regex missed bare and bold mentions. The white paper, the scoping document and CLAUDE.md have none. In the live plan, the 'So can **D29**' sentence is rewritten as closed, and the strangers-stage paragraph's 'can ride with it' gains a closure note. The plan's :5648 'M5' is the reader stage spec's critique label, a different namespace, and is left alone. Errata :6879 (G16), :7378, :7559 and :7562 (G17) and :7835 (G18) are left as dated record that G21's §5.5/§5.6 row supersedes. G21's own hits (:8125, :8126, :8164, :8167, :8170, :8215, :8465, :8468) are its findings, ordering and falsification steps, live as written.
  - D29's closure and G21's §5.5/§5.6 row say `htu` is built from the request, not 'the endpoint's own'; **D41** is opened for the Host-derived origin (found by the review of 91fe326; relay probe 200 under `Host: another.example`). PR 0 does not fix it. D41 is added to the register's **Open:** index, and 'What comes next' places it as its own PR after PR 0 and outside PR 1 (corrected at the review of 013e951, which opened D41 at :224 and in its entry but in neither index). D41's entry names the existing `Curia:Issuer`/`Curia:Audience` origin and its silent default, and `AllowedHosts` as today's configuration-only mitigation. Its next action reuses that origin rather than adding a second (corrected at the review of 013e951).
- [ ] `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, G21's applied-changes table, row `| §5.5, §5.6 |` (line 8402 at 3d2cf24). Re-read the errata immediately before the edit. In the row's second sentence, replace "with the token endpoint's own `htu`" with "with the `htu` the token endpoint builds from the request it received". After "as G16 said they would." insert "R5.14's 'maximum token lifetime' is read, for each cache, as the lifetime of the JWT whose `jti` is cached, as the client-assertion half has always read it (its `exp` + the permitted skew) and as RFC 9449 §11.1 reads it for a proof; the access token's lifetime governs the access token, not the proofs presented with it. A proof is acceptable until `iat` + the permitted skew (`Validate` refuses only when `iat` is more than the skew from now), so R5.14 holds for a proof only once its jti is retained strictly beyond that instant. The resource path retained it until consume time + skew, which is earlier for a proof dated ahead, so such a proof was acceptable again in between. The Forum now retains it until max(consume time, `iat`) + the permitted skew + one second, which is never earlier than `iat` + skew, being the later of the two; the second is because its replay cache readmits a jti at its expiry." Then append, as the sentence after it: "The token endpoint takes that `htu`'s host, and the host of the `aud` it expects of the client assertion, from the request's `Host` header, so both bind to the host the caller named rather than to a configured origin; that weakness predates D29 and is the register's D41." (Added at the review of 91fe326 and given its antecedent at the review of 013e951.) (Corrected at the review of 91fe326: the first wording named max(consume, `iat`) + skew as the last accepting instant, which is the retention, not the instant, and read R5.14's 'token lifetime' for a proof without saying so.) Before running check-spec, grep the inserted sentence for `strictly beyond`. A bound written without it is F0-11b's mutant stated as normative text. Then run `python3 tools/spec-checks/check-spec.py` and get clean. No new requirement number: this is R5.14 as written, which it now meets.
- [ ] `CLAUDE.md`: the parenthesis "(the token endpoint's RFC 6749 server_error still carries its slug, the key-binding stage's M5 in the register)" is removed.
- [ ] Re-read every file:line you wrote. Scan added lines for invisible characters.
- [ ] Commit: `Register: D29 and M5 closed`.

### Task 0.5: Every gate, then PR 0

- [ ] Every command in `CLAUDE.md`'s gate list, in order: Release `dotnet test` with `--filter "FullyQualifiedName!~ScreeningCostTests"`; the separate `ScreeningCostTests` run with `CURIA_SCREEN_TIMINGS` and `CURIA_SCREEN_SCALING`; Architecture in Debug after a Debug build; `--locked-mode` restore; both spec checks; `cargo fmt --check`, `clippy -D warnings`, `cargo test --locked`; the differential with `--fail-on-divergence` after building its two endpoints. Eleven assemblies.
- [ ] Every file in `git diff --name-only origin/main...`, other than this plan and the spec, falls under a File Structure row whose Task cell names a PR 0 task. Stop on any that does not. The table's own paths are checked against the task blocks with the table excluded (`sed '/^## File Structure/,/^---$/d' <plan> | grep -c '<path>'` ≥ 1 per path), never with `grep -c` over the whole plan, which counts the table row itself.
- [ ] `but push token-proof-verified`; `gh pr create --base main --draft`. The body covers D29, M5, the falsification result, and "G21 filed as proposed; no requirement applied to the white paper". It ends with the PR attribution line.
- [ ] CI green, then `gh pr ready`.

---

## PR 0.5 — the lifecycle guard (branch `lifecycle-guard`)

The owner answered "Q2: No." on 2026-10-11: the lifecycle formats are not approved to freeze. This PR puts the chokepoint on `main` before any lifecycle producer exists on any branch (spec §4.1 amended, §4.13). It refuses nothing `main` appends today; GF4 proves that.

### Task L.0: Base and re-derivation

- [ ] `but branch new lifecycle-guard` from the merged `main` (PR 0 merged, 007f4f4 or later, and the `keys-that-end-rulings` documents merged). Confirm with `but status` that it carries none of PR 0's commits.
- [ ] Re-derive, or **stop**: `grep -c '^\*\*R4\.38 (addendum)\*\*' curia-whitepaper-ERRATA-AND-ADDENDUM.md` → `1`; `grep -c '^### 4.13 The guard' docs/superpowers/specs/2026-10-11-keys-that-end-design.md` → `1`; `grep -rn 'lifecycle-not-enabled\|lifecycle-append-refused\|EventId = 7006' src tests` → nothing; `grep -n 'public const int' src/Curia.Operator/Program.cs` → exactly 0–3.
- [ ] Record each assembly's test count at this base.

### Task L.1: The port, the record, the opt-in

**Interfaces:**
```csharp
// Curia.Application.Ports
public enum LifecycleKind { Rotation, Retirement, Compromise }
public interface ILifecycleAppendGate { bool Enables(LifecycleKind kind); }
// Curia.Infrastructure.Lifecycle
// The record's contents are spec §4.13 "Lifting it" and the cybersecurity rulings' Report 2, Lock 1 (a)-(e):
// (a) Decision, (b) the kinds in Approved, (c) each ApprovedKind's digests, (d) MemberNameSignOff, (e) SecuritySignOff.
public sealed record LifecycleApprovalRecord(int Version, ImmutableArray<ApprovedKind> Approved,
    OwnerDecision? Decision, SignOff? MemberNameSignOff, SignOff? SecuritySignOff)
{
    public static Result<LifecycleApprovalRecord> Embedded();             // the manifest resource; missing or unparsable fails
    public static Result<LifecycleApprovalRecord> Parse(ReadOnlySpan<byte> json);
    public Result<Unit> VerifyDigests(string actaVectorsRoot);           // recomputes (c); GF7 runs this, not a copy of it
}
public sealed record ApprovedKind(LifecycleKind Kind, string EventType, string CanonicalSha256, string LeafSha256);
// (a): the owner's words verbatim, the ISO 8601 date, and where the decision was given; all three required, non-empty.
public sealed record OwnerDecision(string Words, string Date, string Place);
// (d), owner question B: the owner's sign-off on the payloads' member names and event types.
// (e): the cybersecurity agent's sign-off on the lifecycle formats (its report's §6), at confidence `high`, and
//      hopper-doctrine's gate on the first append, both named in Statement.
// Every member required and non-empty.
public sealed record SignOff(string By, string Date, string Confidence, string Statement);
public sealed class EmbeddedLifecycleAppendGate : ILifecycleAppendGate
{
    public static Result<EmbeddedLifecycleAppendGate> Create(LifecycleApprovalRecord record, string? optIn); // lock 2
}
// DomainErrors.LifecycleAppendRefusedType = "curia/domain/lifecycle-append-refused"
```
- `lifecycle-approval.json` is `{"version":1,"approved":[]}`, an `EmbeddedResource` of `Curia.Infrastructure`, read through the guarded JSON reader (R11.34), never `System.Text.Json`. That empty record is valid with `Decision`, `MemberNameSignOff` and `SecuritySignOff` all absent.
- `Parse` refuses, with a named error naming the item, a record whose `approved` is non-empty but which lacks (a) `Decision`, (d) `MemberNameSignOff` or (e) `SecuritySignOff`, or any of their members, or an empty member; and one whose `SecuritySignOff.Confidence` is not `high`. The global constraint against editing the record (above) freezes the JSON file and GF6's file, not this type.
- `VerifyDigests` passes an approved kind only when a vector directory under `actaVectorsRoot` whose `input.json` names the kind's `EventType` holds an `expected.canonical` and an `expected.leaf` whose sha256 equal the kind's pinned digests. When no vector directory names that `EventType`, it fails naming the kind, so an unmatched digest cannot pass because nothing was found.
- `Create` refuses, by name: an opt-in naming a kind the record does not approve; an opt-in enabling `rotation` or `retirement` without `compromise`; an opt-in that is not `none` or a comma list over the three kinds. `null` is `none`. The record itself refuses approving rotation or retirement without compromise.
- `tests/Shared/PermissiveLifecycleAppendGate.cs`: an `internal sealed class` whose `Enables` returns true for all three. Nowhere else. It is linked, not referenced: each of six project files gains `<Compile Include="../Shared/PermissiveLifecycleAppendGate.cs" Link="Shared/PermissiveLifecycleAppendGate.cs" />`, as `StubLog.cs` is linked today: `tests/Curia.Application.Tests/Curia.Application.Tests.csproj`, `tests/Curia.Infrastructure.Tests/Curia.Infrastructure.Tests.csproj`, `tests/Curia.Api.Tests/Curia.Api.Tests.csproj`, `tests/Curia.Client.Tests/Curia.Client.Tests.csproj`, `tests/Curia.Mcp.Tests/Curia.Mcp.Tests.csproj` and `tests/Curia.Architecture.Tests/Curia.Architecture.Tests.csproj`. Being internal, each copy is visible only in its own assembly, so `Curia.Infrastructure.Tests`, which references `Curia.Application.Tests`, does not see two (CS0436). Before committing, check that no `InternalsVisibleTo` exposes one of these assemblies' copy to another: `grep -rn 'InternalsVisibleTo' tests --include='*.csproj'` prints nothing at 007f4f4, and no test assembly grants one.

**Red facts first** (`tests/Curia.Infrastructure.Tests/LifecycleGuardTests.cs` unless named). Every fact that touches `LifecycleApprovalRecord` or `EmbeddedLifecycleAppendGate` lives there: `Curia.Infrastructure.Tests` references `Curia.Infrastructure` (`Curia.Infrastructure.Tests.csproj:25`), and `Curia.Application.Tests` references neither. `Curia.Application.Tests` keeps only facts that use the `ILifecycleAppendGate` port alone, and no L.1 fact does, so `tests/Curia.Application.Tests/LifecycleGuardTests.cs` is not created.
- `R4_38_TheApprovedSetIsEmpty` (GF6; `tests/Curia.Architecture.Tests/LifecycleGateFenceTests.cs`): reads `LifecycleApprovalRecord.Embedded()` from the production assembly; asserts it is present, parses, and approves nothing. Red when a kind is added (deliberately, only in the lift PR).
- `R4_38_TheEmbeddedRecordsDigestsMatchConformance`: calls `LifecycleApprovalRecord.Embedded()`, asserts Ok, then runs `.VerifyDigests(<repo root>/conformance/acta)` against the repository's real conformance tree and asserts Ok. Its doc comment says: it is vacuously green while `approved` is empty; it binds at the lift, when the record first approves a kind; and it is Report 2 Lock 1's "build-time check" (spec §4.13, "Lifting it"; errata G21, "The guard"). It is not part of GF6's file, which CP-B forbids later PRs to touch; it is in `tests/Curia.Infrastructure.Tests/LifecycleGuardTests.cs`. Its body is one call to a private helper of the test class that takes the `Result<LifecycleApprovalRecord>` and runs the two assertions, passed `LifecycleApprovalRecord.Embedded()`; FL-14 feeds the helper a test-only record in that argument's place. Red when a pinned digest disagrees with its vector (FL-14).
- `R4_38_ANonEmptyApprovalWithoutItsDecisionOrSignOffsIsRefused`: three sub-cases over test-only records approving `{compromise}` with every item present but one: without (a) `Decision`, without (d) `MemberNameSignOff`, without (e) `SecuritySignOff`. Each `Parse` fails naming its missing item. A fourth sub-case: (e) present with `Confidence` `medium` fails naming the confidence. Control: the same record with all three present, (e) at `high`, parses; and `{"version":1,"approved":[]}` parses with all three absent. Red when the check is deleted (FL-15).
- `R4_38_TheApprovalRecordRefusesRotationOrRetirementWithoutCompromise` (guard fact GF8, the record's half): `LifecycleApprovalRecord.Parse` over a test-only record approving `{rotation}` fails, and over one approving `{retirement}` fails, each naming the compromise-order rule. Control: a record approving `{compromise, rotation}` (with (a), (d) and (e) present) parses. Red when the record-level order check is removed (FL-13). Cybersecurity Report 2's Lock 2 and its Q3 both state the rule, so it is not optional.
- `R4_38_AMissingApprovalRecordFailsClosed`: `Parse` over empty bytes and over `{}` fails; `Create` given that failure fails; a missing resource is never read as empty. Red when a missing record is treated as `approved: []`.
- `R4_38_TheApprovalDigestCheckFailsOnAOneByteChange` (GF7): copy `conformance/acta/key-bound-entry/` to a temporary directory; build a test-only record approving `rotation` with `EventType` `agent.key-bound` and digests are the sha256 of that vector's `expected.canonical` and `expected.leaf` (recomputed in the test with `SHA256.HashData`, not by `VerifyDigests`); assert `VerifyDigests(copy)` is Ok; change one byte of the copy's `expected.leaf`; assert it fails naming the kind. Also assert that a record approving a kind whose `EventType` no copied vector names fails naming the kind. Red when the comparison is skipped.
- `R4_38_StartupRefusesAnUnapprovedKind`, `R4_38_StartupRefusesRotationWithoutCompromise`, `R4_38_StartupRefusesAnUnreadableOptIn` (GF8; `tests/Curia.Api.Tests/LifecycleGuardStartupTests.cs`): the host's start throws with a named message; logging and continuing does not count. Control `R4_38_StartupWithOptInNoneStarts`. Red when either check is removed, or when the opt-in parse check is removed (FL-16).

- [ ] Facts RED (compile the signatures first). Implement. Green.
- [ ] Commit: `R4.38 (add.): the lifecycle gate, an empty approval record, and the opt-in`.

### Task L.2: The chokepoint, in both adapters

- [ ] `PostgresEventStore(NpgsqlDataSource, TimeProvider, ILifecycleAppendGate gate, …)`: required, no default. Every INSERT into the events table goes through one private core that classifies the append before the transaction writes: contains `agent.key-retired` → `Retirement`; contains `agent.key-compromised` → `Compromise`; contains `agent.key-bound` and is not exactly `[agent.enrolled, agent.key-bound]` at `AggregateVersion.New` → `Rotation`. If any classified kind is not enabled, return `DomainErrors.LifecycleAppendRefused(kind)` and write nothing. Type and shape only, never a payload. `PostgresAdapters` takes the gate and passes it. The Api and `curia-operator` compose `EmbeddedLifecycleAppendGate.Create(LifecycleApprovalRecord.Embedded(), configuration["Curia:Lifecycle:Enabled"])` and fail startup on its error (lock 2).
- [ ] `InMemoryEventStore(TimeProvider clock, ILifecycleAppendGate gate)` (today's constructor, `tests/Curia.Application.Tests/InMemory/InMemoryEventStore.cs:29`, takes the clock alone): the gate is required, no default, same classification. Every `new InMemoryEventStore(` and `new PostgresEventStore(` in `tests/` passes a gate explicitly.
- [ ] Where the production-gate runs live. GF3's and GF4's production-gate cases run for both adapters from `Curia.Infrastructure.Tests` (`tests/Curia.Infrastructure.Tests/LifecycleGuardTests.cs`), which references `Curia.Infrastructure` (`Curia.Infrastructure.Tests.csproj:25`) and `Curia.Application.Tests` (`:35`). `InMemoryEventStore` is `internal sealed` (`InMemoryEventStore.cs:22`), `InMemoryEventStoreTests` is `public sealed` (`tests/Curia.Application.Tests/InMemoryEventStoreTests.cs:15`), and no test assembly grants `InternalsVisibleTo`, so `Curia.Application.Tests` provides the in-memory runs of GF3 and of GF4's use-case half as public abstract classes that build the internal store inside `Curia.Application.Tests` and take the gate from an abstract member. `Curia.Infrastructure.Tests` subclasses each with `EmbeddedLifecycleAppendGate`, beside its Postgres runs. `Curia.Application.Tests` keeps only its in-memory runs under `PermissiveLifecycleAppendGate`, including `InMemoryEventStoreTests` and the permissive positive control below.
- [ ] Hand-appended bindings. Before editing, run `git grep -n 'KeyBoundType\|agent.key-bound' 007f4f4 -- tests/` and classify every hit. It prints 24 hits at 007f4f4; a count other than 24 is a stop. Running it against 007f4f4, not the working tree, keeps earlier tasks' line shifts from firing the stop. Classification is by member; the grep only finds candidates. A hit counts as a named member's hit when it lies in that member's body or in the `///` doc block immediately preceding that member's attributes and signature: a doc comment cannot append, so it is classified with the member it documents. Separately, run the same grep on the working tree (`grep -rn 'KeyBoundType\|agent.key-bound' tests/`): every hit beyond those 24 must lie in a file this stage's file map lists as new (for example `tests/Curia.Infrastructure.Tests/LifecycleGuardTests.cs`). Any other hit is a stop. At 007f4f4 the 24 hits classify as:
  - `EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs` (`:746`; its hit `:763`, the expected stream types), through the helper `BindAnotherKeyAsync` (`:173`; its hits `:169`, the helper's doc, and `:181`): a lone `agent.key-bound` on an existing stream, rotation-shaped. Injects `PermissiveLifecycleAppendGate` and says why in one line.
  - `EnrollIdentityTests.R4_34_OnlyTheIdentitysOwnStreamBindsItsKeys` (`:558`; its hit `:573`): a lone `agent.key-bound` planted in another identity's stream at version 2, rotation-shaped; the fact is about stream ownership, not the guard. Injects `PermissiveLifecycleAppendGate` and says why in one line.
  - `EnrollIdentityTests.R4_31_AKeyBindingThatCarriesNoKeyBindsNoBytesUnderItsKid` (`:658`; its hits `:651`, the fact's doc, and `:680`): `[agent.enrolled, agent.key-bound]` at `AggregateVersion.New`, the exact pair; the production gate admits it, so it takes a refusing gate. `Curia.Application.Tests` cannot reach `EmbeddedLifecycleAppendGate`, so here that is a gate implementing the port that enables no kind, declared in the test file; GF4 holds the same shape against the embedded gate from `Curia.Infrastructure.Tests`.
  - `ForumFixture.BindKeyAfterItsPostsAsync` (`tests/Curia.Api.Tests/ForumFixture.cs:192`, used by `ActaEndpointTests.cs:109`; its hits `:185`, the method's doc, and `:206`): a lone `agent.key-bound` after an identity's posts, rotation-shaped. Covered by `ForumFixture`'s default permissive composition below.
  - Those four bullets cover 8 hits. Not appends, unaffected (16 hits):
    - `tests/Shared/StubLog.cs` `:187`, `:353` and `:939` (doc comments of documents the double serves) and `:945` (the `"agent.key-bound"` literal inside `BuildKeyEntry`, a document the double serves, not an append).
    - `FlagPrivacyGateTests.cs:269` (an assertion) and `:253` (the doc of the member holding `:269`).
    - `LogBoundKeysTests.cs:161` (a read).
    - `KeyBindingTests.cs:135` (an assertion string) and `:106` (the doc of the member holding `:135`).
    - `AgentStandingProjectorTests.cs:58` (the `Enrollment` expected-types field). No direct append in `AgentStandingProjectorTests` (`:108`, `:169`, `:183`, `:389`, `:586`, and `AppendToPostAsync`, `:669`) carries an `agent.key-bound`.
    - `EnrollIdentityTests.cs:163` (the `EnrollmentEntries` expected-types field), `:527` (an assertion string), `:616` (a read), and `:701`, the doc of `R4_31_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone` (`:707`), which appends a lone `agent.enrolled` at `AggregateVersion.New` that the chokepoint does not classify; its `new InMemoryEventStore(` takes a gate under the constructor bullet above.
    - `ForumFixture.cs:127`, the doc of `EnrollBeforeKeyBindingAsync` (`:132`), which writes the pre-G16 shape, a key row and a lone `agent.enrolled`, with no `agent.key-bound`; and `:173`, the doc of `EnrollPastTheRouteAsync` (`:176`), which runs the enrolment use case and so writes the exact pair, which both gates admit.
  - 8 + 16 = 24.
- [ ] The Api test composition's DI override (moved here from Task 1.6). From this PR on, `ForumFixture` composes `PermissiveLifecycleAppendGate` by default through its `ConfigureWebHost` (`ForumFixture.cs:281`), so `BindKeyAfterItsPostsAsync` and `ActaEndpointTests` stay green. `GuardedForumFixture` (new, `tests/Curia.Api.Tests/GuardedForumFixture.cs`) subclasses `ForumFixture`, as `FuzzForumFixture` and `KestrelForumFixture` already do, and re-registers `EmbeddedLifecycleAppendGate` built from the embedded record and the opt-in. Every production-gate fact over HTTP runs on `GuardedForumFixture`: GF1 and the key set's `curia_lifecycle_kinds` fact (Task 1.6), GF4's HTTP half, GF10, and `R4_38_TheGuardedRefusalIsTheDocumentTheClientStandInServes` (Task 1.8). GF9's two client facts run in `Curia.Client.Tests` on `StubLog`'s guarded mode, which serves the refusal that last fact holds to the guarded Forum (Task 1.8).
- [ ] Startup scan (GF10): the Api reads the log at start and counts entries of the three guarded shapes by event type and stream shape, logging the named Error event 7006 `LifecycleEntriesUnderGuard` with the count when it is nonzero, and starts. (No lifecycle fold exists in PR 0.5; from PR 1 the guarded Forum also folds such an entry as readers do, Task 1.3.)
- [ ] Throwaway guard: every test fixture that carries the permissive gate (`ForumFixture` by default, and `PostgresDatabaseFixture` wherever an Infrastructure test injects it) checks that the database name carries the prefix it generated in this run (`curia_e2e_` for `ForumFixture`, `ForumFixture.cs:56`; `curia_test_` for `PostgresDatabaseFixture`, `PostgresDatabaseFixture.cs:62`), or throws before connecting.

**Red facts first:**
- `R4_38_TheChokepointRefusesEachLifecycleShape` (GF3; both adapters, production gate, run from `Curia.Infrastructure.Tests` as above): theory over `[key-retired]`, `[key-compromised]`, `[key-bound]` on an existing stream, `[owner-attested, key-bound]`, and `[enrolled, key-bound, key-bound]`; each answers exactly `curia/domain/lifecycle-append-refused`, and the stream is unchanged. Red when the check is removed from either adapter (two F rows, each red on its own adapter's line).
- `R4_38_TheChokepointAdmitsEachShapeUnderThePermissiveGate` (positive control): the same five appends land.
- `R4_38_EnrollmentStillAppendsItsPairUnderTheGuard` (GF4): `EnrollAgent` through the use case (both adapters, `EmbeddedLifecycleAppendGate` over the empty record, run from `Curia.Infrastructure.Tests` as above) and over HTTP (`GuardedForumFixture`) both append `[agent.enrolled, agent.key-bound]`. Red when the chokepoint refuses every `key-bound`.
- `R4_38_NoProductionAssemblyReferencesThePermissiveGate` (GF5; `LifecycleGateFenceTests`): over every assembly built from `src/` and `tools/`, no reference to any test assembly, and no type implementing `typeof(ILifecycleAppendGate)` other than `EmbeddedLifecycleAppendGate` (`R4_38_OneLifecycleGateImplementerInSrcAndTools`; red when a second implementer is added, FL-18). The implementer check is matched by `typeof(ILifecycleAppendGate)` and `IsAssignableFrom`, never a name string. `tests/Shared` files are linked per assembly by `<Compile Include=… Link=…>`, not compiled into every test assembly: `PermissiveLifecycleAppendGate` is compiled into exactly the six assemblies that link it (Task L.1), each holding its own internal copy, so there is no one `typeof(PermissiveLifecycleAppendGate)`. The fence therefore finds the type by its full name in each of those six test assemblies (a link removed from one of them fails here, so the fence cannot pass by finding nothing), and asserts that no assembly built from `src/` or `tools/` contains a type of that name; the implementer check is what finds a renamed copy in `src/`. Run in Release and Debug (D16). Red when a reference or a second implementer is added.
- `R4_38_PostgresEventStoreIsTheOnlyEventStoreInSrc` (hopper): no other `IEventStore` implementer in `src/` or `tools/`. Red when one is added (FL-17).
- `R4_38_AGuardedForumOverALifecycleEntryStartsAndSaysSo` (GF10; `LifecycleGuardStartupTests`): seed one `agent.key-retired` through the test composition into a throwaway database, start `GuardedForumFixture` over it: it starts, and exactly one 7006 is captured with count 1 (capture by event id and logger category, as M5's capture does). Control `R4_38_AGuardedForumOverNoLifecycleEntrySaysNothing`. Red when the scan is removed.
- `R4_38_APermissiveCompositionRefusesADatabaseItDidNotCreate`: pointed at a database name without this run's prefix, the fixture throws before connecting. Red when the check is removed.

- [ ] RED; implement; green in all eleven assemblies.
- [ ] **CP-A.**
- [ ] Commit: `R4.38 (add.): the event store refuses a lifecycle append the gate does not enable`.

### Task L.3: Falsify PR 0.5

Anchors are the literal lines Tasks L.1–L.2 pin, counted once against the tree after Task L.2, or stop. Same discipline as Task 0.3 (kept copies from `git show <commit>:<path>`, plain-copy restore, `--no-incremental`, one unpatched run).

| # | Anchor → replacement | Must go red |
|---|---|---|
| FL-1 | `PostgresEventStore`'s gate check removed | `R4_38_TheChokepointRefusesEachLifecycleShape` (Postgres rows) |
| FL-2 | `InMemoryEventStore`'s gate check removed | `R4_38_TheChokepointRefusesEachLifecycleShape` (in-memory rows) |
| FL-3 | the classification refuses every `agent.key-bound` | `R4_38_EnrollmentStillAppendsItsPairUnderTheGuard` (both halves) |
| FL-4 | the exact-pair rule relaxed to "begins with `agent.enrolled`" | the `[enrolled, key-bound, key-bound]` row of `R4_38_TheChokepointRefusesEachLifecycleShape` |
| FL-5 | a `src/` project gains a reference to the permissive gate's assembly and uses the type | `R4_38_NoProductionAssemblyReferencesThePermissiveGate` (Release and Debug) |
| FL-6 | `lifecycle-approval.json` approves `compromise` | `R4_38_TheApprovedSetIsEmpty` |
| FL-7 | `VerifyDigests`' comparison removed | `R4_38_TheApprovalDigestCheckFailsOnAOneByteChange` |
| FL-8 | `Create`'s unapproved-kind check removed | `R4_38_StartupRefusesAnUnapprovedKind` |
| FL-9 | `Create`'s compromise-order check removed | `R4_38_StartupRefusesRotationWithoutCompromise` |
| FL-10 | the startup scan removed | `R4_38_AGuardedForumOverALifecycleEntryStartsAndSaysSo` |
| FL-11 | `Embedded()` answers an empty record when the resource is missing | `R4_38_AMissingApprovalRecordFailsClosed` |
| FL-12 | the fixture's prefix check removed | `R4_38_APermissiveCompositionRefusesADatabaseItDidNotCreate` |
| FL-13 | `LifecycleApprovalRecord.Parse`'s compromise-order check removed (the record's, not `Create`'s) | `R4_38_TheApprovalRecordRefusesRotationOrRetirementWithoutCompromise` |
| FL-14 | in `R4_38_TheEmbeddedRecordsDigestsMatchConformance`, the helper's argument `LifecycleApprovalRecord.Embedded()` → an Ok result holding a test-only record built by its constructor (not by `Parse`, whose compromise-order rule would refuse a rotation-only record for a reason that is not this row's), approving `rotation` with `EventType` `agent.key-bound`, `Decision`, `MemberNameSignOff` and `SecuritySignOff` present, and the sha256 digests of `conformance/acta/key-bound-entry`'s `expected.canonical` and `expected.leaf`, one hex digit of `CanonicalSha256` changed. Control, run once with the same substitution but the digit unchanged: green, so the red is the digit | `R4_38_TheEmbeddedRecordsDigestsMatchConformance` |
| FL-15 | `Parse`'s check that a non-empty `approved` carries (a), (d) and (e), and (e) at `high`, deleted | `R4_38_ANonEmptyApprovalWithoutItsDecisionOrSignOffsIsRefused` (each sub-case) |
| FL-16 | `Create`'s opt-in parse check removed (an opt-in that is not `none` or a comma list over the three kinds is read as `none`) | `R4_38_StartupRefusesAnUnreadableOptIn` |
| FL-17 | a second `IEventStore` implementation added in `src/` (a class in `Curia.Infrastructure` implementing it) | `R4_38_PostgresEventStoreIsTheOnlyEventStoreInSrc` (Release and Debug) |
| FL-18 | a second `ILifecycleAppendGate` implementer added in `src/` (a class in `Curia.Infrastructure` implementing it) | `R4_38_OneLifecycleGateImplementerInSrcAndTools` (Release and Debug) |

- [ ] Expect `falsify: 18 cases, 0 not RED`.

### Task L.4: Register and documents (PR 0.5)

- [ ] `IMPLEMENTATION_PLAN.md`: a register entry for the guard (opened, built; lifted: no), with FL-1…FL-18 and CP-A's output; a known residual, not a defect to close in this stage: the guard is adapter-level, in both event-store adapters, and a database-level refusal (a trigger, or a grant per event type) is not built, so a holder of the application role's INSERT grant can write a lifecycle row by SQL, which readers still fold on its merits and the guarded Forum's startup event names (GF10); **NEEDS (human only, open)**: branch protection on `main` requiring code-owner review, and a `CODEOWNERS` entry naming the owner for `src/Curia.Infrastructure/Lifecycle/lifecycle-approval.json`. It gates the lift, not the build.
- [ ] `IMPLEMENTATION_PLAN.md`, the guard's entry: it names KF6's register entry, **D43** (filed by the rulings fold of review round 2, on the `keys-that-end-rulings` branch). Re-read the register and confirm D43 is present and open, with the trigger "before `curia-operator sign-head` runs unattended or from any automated process, compromise declarations move off the log key to a separate operator declaration key (R6.26 (rev.), KF6; cybersecurity Report B6)" and the status "open, by decision, not a defect in current behaviour". If it is absent, file it with that trigger and status under the next free D-number, taken only after re-reading the register immediately before the edit.
- [ ] `IMPLEMENTATION_PLAN.md`, the start-here paragraph on the rotation stage ("Four PRs, in order"): re-read it immediately before the edit and keep it current: PR 0.5 merged-pending, the guard built and not lifted, and PR 1 and PR 2 still building behind it. Keep its closing "read this line for what is scheduled, never for what is built". This is done here, not left until Task 1.11.
- [ ] `CLAUDE.md`: one clause: lifecycle kinds are guarded until the owner lifts the guard.
- [ ] Scan added lines for invisible characters. Commit: `Register: the lifecycle guard (G21, PR 0.5)`.

### Task L.5: Every gate, then PR 0.5

- [ ] As Task 0.5. **CP-B.**
- [ ] `but push lifecycle-guard`; draft PR. The body covers R4.38 (add.), the falsification result, "merging does not freeze; the lift is the owner's separate act", and the NEEDS item as an open, human-only item. PR attribution line. CI green, then `gh pr ready`.

---

## PR 1 — rotation and retirement (branch `key-rotation`)

### Task 1.0: Base, owner question 1, first act

- [ ] `but branch new key-rotation` from the merged `main` (PR 0.5 merged). Confirm with `but status` that it carries none of PR 0's or PR 0.5's commits, and that `R4_38_TheApprovedSetIsEmpty` is on `main`.
- [ ] Owner question 1 (spec §7 q1, the Table 10 rows) proceeds on its default yes: the cybersecurity report leaves it to the spec authority and recommends yes; curia-architect, as spec authority, keeps yes (2026-10-12). This is not an owner answer, and the plan stops if the owner answers no. The owner's "Q1: Yes." of 2026-10-11 answered the takeover question, not this one. The guard does not change it: tests run the rows behind the permissive gate. If the owner says no to question 1, stop: Task 1.6's Table 10 rows cannot be tested. Default yes: in `curia-agent-forum-WHITEPAPER.md`, insert after the `| `flag` | `list` (own) |` row the two rows from G21's "The Table 10 rows", byte for byte. Mark G21's Status: "the two Table 10 rows are applied in v1.1". Run `check-spec.py` and get `clean`.
- [ ] **First act.** Add `R4_31_AKeyARotationBindsIsReAnnouncedAsTheFirstIs` to `EnrollIdentityTests` beside the existing `:746` fact. It runs `RotateKey` (Task 1.4) to bind `alice-2`, then asserts the same three assertions as `:746`, plus the stream's types `[enrolled, key-bound, key-bound]`. It does not compile until Task 1.4 and is committed with it. The `:746` fact stays as it is: its hand-appended entry is the one R4.31 (rev.) was written against.

### Task 1.1: Proposed formats first (vectors, both runners)

- [ ] `tools/GenerateEnvelopeFixtures/Lifecycle.cs` (new) emits:
  - **`acta/key-rotated-entry`.** The agent is the `key-bound-entry` agent. The authorizing key is `ed25519-minimal`'s, and the new key is a fixed second seed (`0x02` repeated 32 times, kid `conformance-ed25519-second`). `prev` = 1, under the implied log recorded in `meta.json`: 0 `agent.enrolled`, 1 the enrolment's `agent.key-bound`, 2 this rotation, 3 the retirement below. `event_id` `01K4CQ1V9P3R5T7W9Y1A3C5E8H`, `server_ts` `2026-09-04T15:30:00.000000Z`.
  - **`acta/key-retired-entry`.** Retires `conformance-ed25519-minimal`, signed by itself, `prev` = 2. `event_id` `01K4CQ1V9P3R5T7W9Y1A3C5E9J`, `server_ts` `2026-09-04T16:00:00.000000Z`.
  - **`acta/key-bound-entry-es256`.** A P-256 key whose `x` begins with 0x00; the SPKI is in `meta.json`.

  Statements use `CanonicalJson.CanonicalizeWithNfc`; signatures use `Signing.Sign` (R6.37) with the kind's header `typ` (`curia-key-rotation+jws`, `curia-key-retirement+jws`; spec §4.5, KF1) and the role's `kid` in the header (KF2). If `Signing.Sign` cannot take a `typ`, add the parameter; never sign a lifecycle statement under `curia-post+jws`.
  These vectors are proposals: they may be regenerated until the lift record pins their digests. `rotation-es256-then-post` is not generated here; it lands in Task 1.9's commit where both runners verify it.
- [ ] (Moved to Task 1.9: `conformance/lifecycle/` lands with the runners that read it.)
- [ ] Compute `expected.digest` and `expected.leaf` for each new `acta/` vector with a python3 script under the scratchpad, using `json` + `hashlib` exactly as `key-bound-entry`'s `meta.json` describes. The generator's own values must equal the script's, or **stop**.
- [ ] `conformance/index.json`: the `acta` count only; no `lifecycle` row until Task 1.9.
- [ ] Red first: with `index.json`'s `acta` count raised and the vectors absent, both runners fail on the count; add the vectors and both pass. No family is skipped at any commit.
- [ ] Commit: `Conformance: the lifecycle's entries, proposed before any producer (R4.38)`.

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

- [ ] Implement it in Postgres. Inside the existing transaction, after `:160`'s check, run the same version query for `condition.Aggregate` and compare. Implement it in memory as well. The overload goes through PR 0.5's gated core; it adds no second INSERT path.
- [ ] Fact `R4_38_TheConditionedOverloadPassesTheChokepoint` (contract suite, both adapters, production gate): an `agent.key-retired` appended through the conditioned overload is refused `curia/domain/lifecycle-append-refused`. Red when the overload writes outside the gated core (F1-48).
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
- `R4_38_AStatementWithAnExtraMemberIsRefused` (KF4): the vector's statement plus one member, canonical → `curia/keys/statement-mismatch`. Red when the member set is opened (F1-35's Forum half).
- `R4_38_AnEntryWithAnExtraMemberIsRefused` (KF4, R6.54 (add.)): an otherwise-valid rotation entry, the vector's, carrying one undeclared member beside `statement`, is refused by the fold → `curia/keys/statement-mismatch`. Red when the entry member set is opened (F1-67's Forum half).
- `R4_38_AnUnknownStatementTypIsRefused` (KF4): `curia.key-rotation/2` → `curia/keys/statement-unreadable`. Red when an unknown `typ` is read as `/1` (F1-57).
- `KeyStatements` checks the header `typ` and `kid` of each detached JWS against the statement (KF1, KF2), the `jwk.alg` derivation and `authorized_by` ≠ `kid` (KF5); Task 1.4's facts pin the refusals.
- [ ] Facts RED. Implement. Amend `EnrollIdentity`'s lost-row clause: the condition "store holds no key for the identifier" becomes "store holds no row under this `kid`". Leave its other branches alone.
- [ ] From this task on, `KeyLifecycle` is on the Forum's read path, so the guarded Forum also folds a lifecycle-kind entry it finds in its log as readers do (R6.54 (add.)); Task L.2's startup scan still counts it and logs event 7006.
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
8. Check the `kid` is not taken: no lifecycle entry of any identity names it, and no store row holds it unless the row is this agent's own, holds byte-identical material, and no lifecycle entry binds it (an orphan of an earlier attempt). Then check R7.23's cap and budget, then its lifetime ceiling: 511 rotations in the identity's lifecycle refuses `curia/keys/rotation-ceiling-reached` (409).
9. `registry.AddAsync`, then append `agent.key-bound` (rotation form) at the read version. `AddAsync` is `INSERT … ON CONFLICT (kid) DO NOTHING RETURNING`, then a read-back of that `kid`: Ok when the row's agent, `alg` and public key equal the request's, `curia/keys/kid-taken` otherwise. Never the `:232` upsert. A store row left by a step 10 conflict, or by a crash, is reused on retry.
10. On a concurrency conflict, return `curia/keys/lifecycle-moved` with the new latest index.

**`RetireAsync`** follows the same order with `ReadRetirement`. The signature is verified under the key being retired, its header `typ` `curia-key-retirement+jws` and its header `kid` the statement's. It refuses `last-live-key` when `LiveCount() == 1`, and it runs no budget check and no ceiling check.

Step 7 refuses `curia/keys/signature-invalid` for a header `typ` other than the kind's or a header `kid` other than the role's member; step 1 refuses `curia/keys/jwk-alg-mismatch` and `curia/keys/self-authorized` (KF5). The use-case tests compose the in-memory store with `PermissiveLifecycleAppendGate`.

**R7.23's constants** live in `TierPolicy`: `MaxLiveKeys = 4` and `RotationsPerDay = 3`, with remarks citing R7.23 provisional, and `MaxLifetimeRotations = 511`, with a remark citing R7.23's ceiling and R6.54 (add.)'s 1,024.

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
| `R7_23_TheNumbersAreTheErrata` | parses `**R7.23**`'s "at most four live keys", "at most three rotations" and "at most 511 rotations over its lifetime" from `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (a hand parser, word to number, as `PublishedTable11` reads its text) and compares them with `TierPolicy`. The parser first collapses every whitespace run, newlines included, to one space, because the errata wraps the phrase across lines |
| `R7_23_ARotationPastTheLifetimeCeilingIsRefusedBeforeAnythingIsWritten` | setup: hand-append, behind the permissive gate, 511 rotations, each followed by a retirement of the prior key, so live keys stay at most 2; every entry's timestamp is more than 24 hours before the fake `TimeProvider`'s now, so R7.23's live-key cap and daily budget both pass. The 512th rotation answers `curia/keys/rotation-ceiling-reached`, and neither the store nor the log gains a row. Control: with 510 rotations so set up, the next rotation is accepted. Red when the ceiling moves (F1-38) |
| `R7_23_TheCeilingNeverRefusesARetirement` | its own setup: hand-append, behind the permissive gate, 511 rotations, each followed by a retirement of the prior key except the last, so that two keys are live at the ceiling (the 510th rotation's key and the 511th's); every entry's timestamp is more than 24 hours before the fake `TimeProvider`'s now. The lifecycle is then the enrolment's pair, 511 rotations and 510 retirements, 1,023 entries, and 1,024 after the retirement under test, within R6.54 (add.)'s 1,024 (R7.23 counts an enrollment's pair, 511 rotations and 511 retirements as 1,024). Retiring the 510th rotation's key binds: `RetireAsync` is Ok and the stream gains its `agent.key-retired` entry; asserting only 'not ceiling-reached' is not enough. Its doc comment says the last-live-key refusal (R4.39) was deliberately avoided, by leaving two keys live, so that it cannot hide F1-39. Red when the ceiling is applied to retirement (F1-39; the 511 rotations meet the ceiling, so that check refuses this retirement) |
| `R4_18_ARotationWhoseAuthorizationTypIsAPostsIsRefused` | `authorization` valid but under `curia-post+jws` → `signature-invalid`, detail `authorization`. Red when the `typ` check is skipped (F1-27) |
| `R4_18_ARotationWhoseHeaderKidIsNotItsRoleIsRefused` | `authorization`'s header `kid` names the new key → `signature-invalid`. Red when the header-`kid` check is skipped (F1-28) |
| `R4_18_ARotationWhoseJwkAlgDisagreesIsRefused` | an Ed25519 `jwk` with `alg` `ES256` → `jwk-alg-mismatch`. Red when the derivation is skipped (F1-29) |
| `R4_18_ASelfAuthorizedRotationIsRefused` | `authorized_by` = `kid` → `self-authorized`. Red when the check is skipped (F1-30) |
| `R4_39_ARetirementSignedUnderARotationTypIsRefused` | a valid retirement signature under `curia-key-rotation+jws` → `signature-invalid`. Red when a rotation `typ` is accepted on a retirement (F1-70) |

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
- [ ] Routes as in spec §4.7, in `ForumEndpoints.Map`, each `.Serves(ServedContent.None)`. **The gate is each handler's first statement**, before authentication, any token or proof is consumed, the body is read, the store is written or the log is read: when `ILifecycleAppendGate.Enables(kind)` is false, answer 403 `curia/keys/lifecycle-not-enabled` with the fixed detail `key rotation is not enabled on this Forum` or `key retirement is not enabled on this Forum`. The route and its gate are committed together (GF1). The body is read through the guarded JSON reader (R11.34).
- [ ] `Jwks.cs`: `curia_status`, `curia_retired_log_index`, top-level `curia_lifecycle` (ascending indices, from the same `FoldAsync` the route already uses), and top-level `curia_lifecycle_kinds` `{"approved":[…],"enabled":[…]}` from the gate and the record.
- [ ] Quarantine: the intersection in `AccessPolicy` becomes the reads plus `key`/`retire` (R4.39). Retirement never spends the posting budget, and rotation spends R7.23's budget only: exclude both from `IsWrite`'s budget beside `IsFlagRaise`, with a remark citing R7.23. Required for retirement: R7.23 states it as a SHALL NOT. For rotation, the Table 10 paragraph says rotation has R7.23's budget and not the posting budget; this is descriptive text, not a SHALL. The reason: `IsWrite` is the complement of reads, so without this both would spend the posting budget, and a thief could spend a T0 identity's three daily posts to stop the owner retiring the stolen key.
- [ ] Api tests and the fuzzer run behind `PermissiveLifecycleAppendGate`, as composed in L.2 (`ForumFixture`'s default); GF1 and the key-set fact below run on `GuardedForumFixture` (Task L.2).

**Red facts** (`tests/Curia.Api.Tests/KeyRotationEndpointTests.cs`):

| Fact | Asserts |
|---|---|
| `R4_18_RotationOverHttpBindsAndAnswersItsIndex` | — |
| `R4_18_RotationForAnotherAgentIsNotOwn` | 403 `curia/keys/not-own` |
| `R4_39_RetireOverHttp` | — |
| `R4_35_TheKeySetPublishesStatusAndLifecycle` | — |
| `Table10_KeyRotateAndRetireAreDeniedToAnonymous` | — |
| `Table10_QuarantineRefusesRotationAndPermitsRetirement` | the PDP: quarantined → `rotate` denied, `retire` permitted; replaces `Table10_QuarantineRefusesBoth` (owner question 2 changed by `cybersecurity`) |
| `R4_39_AQuarantinedAgentRetires` | over HTTP, a quarantined agent's retirement answers 201. Red when quarantine's intersection drops `retire` (F1-37) |
| `R7_23_KeyActsSpendNoPostingBudget` | a T0 agent that has spent its three posts still retires (201), and still rotates within R7.23. Red when `IsWrite`'s exclusion is removed (F1-49) |
| `R4_38_EachLifecycleRouteRefusesBeforeAnythingElse` (GF1) | `GuardedForumFixture`: for each route, a fully valid DPoP-bound request with valid signatures answers 403 `curia/keys/lifecycle-not-enabled`; then, read directly: the proof's `jti` is absent from the replay cache, `agent_keys` has no new row, the log's size is unchanged. Red when the gate is removed, moved after authentication, or moved after the store write (F1-43, F1-44, F1-45, each on its own line) |
| `R4_38_EachLifecycleRouteSucceedsUnderThePermissiveGate` | positive control for GF1: the same requests answer 201 |
| `R4_38_TheKeySetStatesNoKindApprovedOrEnabled` | `GuardedForumFixture`: `curia_lifecycle_kinds` is `{"approved":[],"enabled":[]}` (CP-A's key-set half) |

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
- `R5_22_ALivenessReadThatFailsRefusesTheRequest`: an `IAgentKeyLiveness` that returns an error; the request is refused with that error, never served. Red when a failed read is treated as `Active` (F1-50).

- [ ] Client: a 401 with `token-key-not-live` or `token-names-no-key` deletes `token.json` and retries once (`ForumSession`). Fact: `R5_22_TheReferenceClientRemintsOnce` in `Curia.Client.Tests`.
- [ ] Commit: `R5.22: a token is refused once the key it was issued on ends`.

### Task 1.8: The client's verbs

- [ ] `KeyActs.cs` (`Curia.Client`): `BuildRotation(EnrolledAgent current, IAgentSigner next, long prev)` and `BuildRetirement(IAgentSigner retiring, string agentId, long prev)` return `(statement, signatures…)`, built with `CanonicalJson.CanonicalizeWithNfc` and `DetachedJws` in R6.37's form, each header carrying its kind's `typ` (`curia-key-rotation+jws`, `curia-key-retirement+jws`) and its role's `kid` (spec §4.5). Never `SubmissionBuilder`'s `curia-post+jws`. An external signer is asked through `sign-lifecycle`.
- [ ] Signer verb split (R4.39, R11.20): `tests/Shared/test-signer.py` and the documented signer protocol refuse a lifecycle `typ` on `sign` and accept one only on `sign-lifecycle`; `Curia.Client`'s external signer (`ExternalSigner`, `src/Curia.Client/AgentSigners.cs`) gains the verb; `Curia.Mcp` never calls it. In `AgentSigners.cs`'s doc list of "the properties that matter", which any replacement must keep, add a fourth: a lifecycle statement is signed only through an operation of its own, never through the one a post is signed with, which the MCP adapter never calls (R4.39 makes this a property of every conforming external signer, not of this protocol alone; a profile holding its key in-process is outside it).
- [ ] FOREIGN detector (R4.39): on every key-set read and on every refusal `invalid_client`, `curia/authn/token-key-not-live` or `curia/keys/retired`, compare `curia_lifecycle` (after enrolment) with `lifecycle_acts`; print `FOREIGN: …` through `FrameText` (spec §4.9) and exit 6.
- [ ] `rotate`'s retry rule (spec §4.12): retry on `lifecycle-moved` only when every entry after its `prev` is in `lifecycle_acts`; otherwise FOREIGN, no retry, exit 6.
- [ ] Guarded refusal (GF9): on `curia/keys/lifecycle-not-enabled`, render the refusal (spec §4.9), exit 4, leave `identity.json`, `signing-key.pem` and `keys/` byte-identical and discard the generated key; the new key reaches disk only after a 201. Committed with the verbs.
- [ ] `ProfileStore`: `retained_keys` and `lifecycle_acts` read and written; `MoveCurrentToRetained`; `RecordAct`.
- [ ] CLI verbs `keys`, `rotate` and `retire` (spec §4.9 and §4.12). Every line goes through `FrameText`; `Hints.Retire` and `Hints.Keys`. `Help.cs` gets entries and per-verb flags, held by `D37_EveryVerbAnswersHelp`.
- [ ] How the verbs are reached. `Program` is `internal static` (`src/Curia.Client.Cli/Program.cs:21`), `Main` (`:25`) and `HttpFor` (`:1194`) are private, and `Curia.Client.Cli.csproj:30` grants `InternalsVisibleTo` to `Curia.Client.Tests` alone. `rotate` and `retire` are therefore `internal static` methods of `Curia.Client.Cli`, each taking a `ForumSession` (or `ForumClient`) and the profile directory, and `Main`'s `rotate` and `retire` cases are each a single call to one of them: the pattern by which `RefusedAsync` (`:940`), `SendDraftAsync` (`:381`) and `VerifyAllAsync` (`:890`) are reached today. Every HTTP call the verbs make goes through a `Curia.Client` library method, and that method is exercised end to end against `ForumFixture` in `Curia.Api.Tests` by this PR's own route facts (Task 1.6), so the stand-in below never has to be trusted on paths or wire shape. Not done: a `ProjectReference` from `Curia.Api.Tests` to `Curia.Client.Cli`; an `InternalsVisibleTo` from `Curia.Client.Cli` to `Curia.Api.Tests` (CS-5 permits only the matching test assembly, and `Curia.Mcp.csproj:39` is an existing exception to it, not a precedent to extend); a public `HttpClient`-taking entry point on an exe.
- [ ] The guarded stand-in (GF9). `tests/Shared/LifecycleRefusals.cs` (new), an `internal static class` holding the two refusal problem documents, `rotation` and `retirement`, as the guarded Forum serves them: type `curia/keys/lifecycle-not-enabled`, status 403, and the fixed detail naming the kind (spec §4.7, §4.9; Task 1.6's texts). It is linked into `Curia.Api.Tests` and `Curia.Client.Tests` with `<Compile Include="../Shared/LifecycleRefusals.cs" Link="Shared/LifecycleRefusals.cs" />` in `tests/Curia.Api.Tests/Curia.Api.Tests.csproj` and `tests/Curia.Client.Tests/Curia.Client.Tests.csproj`. `tests/Shared/StubLog.cs`'s handler gains an opt-in guarded mode, off by default, in which a POST to either lifecycle route answers 403 with the shared document for its kind and is recorded in `Requests`.
- [ ] Not taken, recorded rather than silent: a subprocess run of the built `curia.dll` against `KestrelForumFixture` would be the higher-fidelity probe of GF9. It would couple the facts to a build-output path, and so to D16's configuration split, so the verbs are driven in-process as above.

**Red facts** (each in `Curia.Client.Tests` against a Forum double, unless it names another assembly; the two GF9 facts run in `Curia.Client.Tests` on `StubLog`'s guarded mode, not on `GuardedForumFixture`; `R4_38_TheGuardedRefusalIsTheDocumentTheClientStandInServes` runs in `Curia.Api.Tests` on `GuardedForumFixture`):
- `R4_39_KeysReportsARecordedActTheLifecycleOmits`: a Forum double serving `curia_lifecycle` without the act; exit 6, and a `MISSING:` line.
- `R4_18_RotateMovesTheKeyAndPrintsTheRetireHint`: the hint line equals `curia retire --agent 'a' --kid 'a-0000abcd'`.
- `R10_65_AKidThatIsNoShellWordWithholdsTheRetireCommand`.
- `R10_63_EveryKeysLineQuotesTheForumsValues`: a `kid` holding `"` prints escaped.
- `R4_39_RetireRefusesTheCurrentKey`.
- `R4_39_KeysReportsAForeignRotation`: a Forum double serving a rotation at index 7 not in `lifecycle_acts`; exit 6 and the line `FOREIGN: a rotation at log index 7 that this profile did not make`. Red when the comparison is inverted (F1-40).
- `R4_39_AnInvalidClientRefusalRunsTheForeignCheck`: a token request refused `invalid_client` against a double whose key set holds a foreign retirement; the FOREIGN line follows the refusal, exit 6, and the operator report is named. Red when the refusal path skips the check (F1-60).
- `R4_39_ATokenKeyNotLiveRefusalRunsTheForeignCheck`: a Forum double whose lifecycle holds an act absent from the profile's `lifecycle_acts` refuses a request `curia/authn/token-key-not-live`; the FOREIGN line follows the refusal, exit 6, and the operator report is named. Red when that trigger is removed (F1-61).
- `R4_39_ARetiredRefusalRunsTheForeignCheck`: as above, the refusal `curia/keys/retired` (a post refused at ingest). Red when that trigger is removed (F1-62).
- `R4_39_AKeySetReadByAnotherVerbRunsTheForeignCheck`: `curia read --agent <slug> <post-id>` over a post the profile's identity authored, which reads that identity's key set through `VerifyAllAsync` (`src/Curia.Client.Cli/Program.cs:909`), against a double whose lifecycle holds a foreign rotation; the FOREIGN line is printed, exit 6, and the operator report is named. `curia verify` reads the key set too (`Program.cs:996`) but takes no `--agent` and so has no `lifecycle_acts` to compare. Red when the non-`keys` key-set-read trigger is removed (F1-63).
- `R4_39_RotateRetriesOverItsOwnActOnly`: `lifecycle-moved`, and the entry after `prev` is this profile's recorded retirement; the retry binds.
- `R4_39_RotateDoesNotRetryOverAForeignAct`: `lifecycle-moved`, and the entry after `prev` is not recorded; no second POST is sent (count requests at the double), FOREIGN, exit 6. Red when the client retries unconditionally (F1-41).
- `R11_20_TheReferenceSignerRefusesALifecycleTypOnSign` and `R11_20_TheReferenceSignerSignsALifecycleStatementOnSignLifecycle` (`Curia.Client.Tests`, the real `test-signer.py` in another process). Red when `sign` accepts a lifecycle `typ` (F1-42).
- `R11_20_CuriaMcpNeverCallsSignLifecycle` (`Curia.Architecture.Tests`): no method in `Curia.Mcp`'s IL reaches the client's `sign-lifecycle` call or `KeyActs`. Red when a reference is added (F1-64, in Release and Debug).
- `R4_38_TheGuardedRefusalIsTheDocumentTheClientStandInServes` (`Curia.Api.Tests`, on `GuardedForumFixture`): for each lifecycle route, the served problem document equals `LifecycleRefusals`' document member for member. If the served body carries per-request members, the fact names them and excludes exactly those; every other member must be equal, and a member present on one side only fails. This is what stops the stand-in from being a second reading of spec §4.7: it serves an artifact the real guarded Forum is checked against. Red when the route's refusal detail text changes (F1-69).
- `R4_38_RotateAgainstAGuardedForumLeavesTheProfileByteIdentical` and `R4_38_RetireAgainstAGuardedForumLeavesTheProfileByteIdentical` (GF9; `Curia.Client.Tests`, on `StubLog`'s guarded mode): each runs the internal verb method against the stub: exit 4, the refusal line spec §4.9 publishes for its kind, exactly one request reached the lifecycle route, and the sha256 of `identity.json`, `signing-key.pem` and the sorted `keys/` listing are unchanged. Positive control in the same class: the same stub with guarded mode off, answering 201, changes the profile, which shows the hash comparison can see a change. Red when the new key is persisted before the response (F1-46).
- [ ] Commit: `Client: curia keys, rotate and retire`.

### Task 1.9: The readers fold the lifecycle (R6.54 (add.))

**C#:**
- `LifecycleCheck.Verify(ProvenancePost post, LogEntryDocument postEntry, IReadOnlyList<(LogEntryDocument Entry, InclusionProofDocument Proof)> lifecycle, SignedHeadDocument head) → Check`, implementing spec §4.8.
- `PostVerification` gains `Check Lifecycle`. `Overall` treats it as it treats `KeyBinding`. `Render` gains `lifecycle   {Lifecycle.Describe}` after `key`.
- `PostVerifier.LifecycleAsync` fetches `curia_lifecycle` from the key set; fetches every entry and proof with index < the head's tree size, up to the first ≥ the post's index; stops at 1,024.
- `SignatureCheck` and `Passage` add the read-path line and failure (spec §4.9).
- `curia_verify`'s result gains the line.

**Rust:**
- `lifecycle.rs`, `fn verify_lifecycle(post_entry, chain: &[(Vec<u8>, Vec<u8>)], head, log_jwks) -> Result<VerifiedLifecycle, ActaError>`, with new `ActaError` variants: `RetiredBeforePost`, `BrokenLink`, `RotationUnauthorized`, `StatementMismatch`, `LifecycleTypMismatch`, `LifecycleUnknownKind` (not established), `LifecycleNotSupplied` (not established), `LifecycleTooLong` (not established).
- Both readers: header `typ` per kind and header `kid` per role (KF1, KF2) → failed; closed member sets (KF4) → failed; `jwk.alg` derivation and `authorized_by` ≠ `kid` (KF5) → failed; an entry type none of the four, an unknown statement `typ` (KF4) → could not be checked, never verified, never skipped. KF4's order (spec §4): each reader determines the entry type, then the statement `typ`, before any member-set check, so an unknown type or `typ` is could not be checked whatever members it carries. No approval check: readers fold on the merits.
- Both head verifiers (`ActaCheck`, `acta.rs`) already refuse a signature whose header names any lifecycle `typ`, by exact `typ` match (`src/Curia.Canon/Jws/DetachedJws.cs:158`, `if (header.Typ != _typ)`; `rust/curia-testis/src/jws.rs:176`, `verify_typed(..., HEAD_TYP)`'s `typ` arm). Nothing is built for it; the regression guards below pin it.
- `jws.rs` verifies a detached JWS over a statement as it does a post.
- `log author` gains a repeated `--lifecycle-entry <path> --lifecycle-proof <path>` (in log order). With none supplied it reports `LifecycleNotSupplied`, exit 3.

**Red facts:**
- `conformance/lifecycle/<case>/` for the 21 PR 1 cases in spec §4.6 (the 12 first listed, plus `rotation-signed-as-post`, `header-kid-disagrees`, `rotation-jwk-alg-mismatch`, `rotation-self-authorized`, `statement-extra-member`, `entry-extra-member`, `unknown-lifecycle-kind`, `unknown-statement-typ`, `unknown-statement-typ-extra-member`), and its `index.json` row (profile `lifecycle-log`). `rotation-es256-then-post` lands in the commit where both runners verify it, never before. Each case holds `entries/<index>.json`, `proofs/<index>.json`, `head.json`, `log-jwks.json` and `expected.json` (`{"verdict":"verified"|"failed"|"could-not-check","predicate":"…"}`), generated from in-memory trees. RED first: both runners fail on the count. Then the runners, then green. R14.6's differential comparison gains no lifecycle input in this stage; these two runners are the stage's cross-implementation check.
- Both runners over every `conformance/lifecycle/*` case: the verdict and predicate equal `expected.json`. Each of `header-kid-disagrees`, `rotation-jwk-alg-mismatch` and `rotation-self-authorized` runs in both runners and is the fact its F1 row turns red (F1-51…F1-56); `unknown-statement-typ` is the fact of F1-58 and F1-59, `unknown-statement-typ-extra-member`, run in both runners, of F1-65 and F1-66, and `entry-extra-member`, run in both runners, of F1-67 and F1-68.
- `R6_54_ARotationTheAuthorDidNotSignFailsInBothReaders`.
- `R6_54_AnEntryOmittedImmediatelyBeforeThePostIsCaughtByTheNextLink`: through `PostVerifier` against a Forum double, never `LifecycleCheck` directly.
- `R6_54_ARetiredKeysLineOnReadIsTheForumsWord` (`Passage`).
- `R11_29_CuriaVerifyReportsTheLifecycleLine` (`Curia.Mcp.Tests`).
- `R6_54_APostAfterItsKeysRetirementIsNotVerifiedOverall` (`Curia.Client.Tests`): every other check passes; the summary is FAILED and the exit is 6.
- `R6_54_ALifecycleOfMoreThan1024EntriesBeforeThePostCouldNotBeChecked` (a Forum double).
- Write `LifecycleCheck`'s retirement comparison exactly `retiredAt < postIndex` (F1-10).
- `R10_64_LifecycleRefusalsNameValuesAsLiterals` (`display_output.rs`).
- `R4_18_EachLifecycleTypMeetsTheB64AndCritNegatives` (`DetachedJwsTests.cs`) and its Rust twin: the existing `b64`/`crit` negatives, run with each new `typ`, refuse as they do for posts. Red when the `b64` check (F1-71) or the `crit` check (F1-72) is dropped.

**Regression guards (green on today's tree):**
- `R6_49_TheHeadVerifierRefusesALifecycleTyp` (C#, `Curia.Client.Tests`) and `head_verifier_refuses_a_lifecycle_typ` (Rust): a valid log-key signature over a head's bytes under each of the two PR 1 `typ`s is refused. They pass before work starts, because both head verifiers already refuse any `typ` but `curia-head+jws`, and they pin that behaviour against a future relaxation. Red when the shared exact-`typ` comparison is relaxed for lifecycle `typ`s (F1-47).

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
| F1-27 | `KeyStatements`' header-`typ` comparison removed | `R4_18_ARotationWhoseAuthorizationTypIsAPostsIsRefused` |
| F1-28 | `KeyStatements`' header-`kid` comparison removed | `R4_18_ARotationWhoseHeaderKidIsNotItsRoleIsRefused` |
| F1-29 | the `jwk.alg` derivation check removed | `R4_18_ARotationWhoseJwkAlgDisagreesIsRefused` |
| F1-30 | the `authorized_by` ≠ `kid` check removed | `R4_18_ASelfAuthorizedRotationIsRefused` |
| F1-31 | C# `LifecycleCheck`'s `typ` check removed | `rotation-signed-as-post` (C#) |
| F1-32 | Rust `verify_lifecycle`'s `typ` check removed | `rotation-signed-as-post` (Rust) |
| F1-33 | C# `LifecycleCheck` skips an unknown entry type | `unknown-lifecycle-kind` (C#) |
| F1-34 | Rust skips an unknown entry type | `unknown-lifecycle-kind` (Rust) |
| F1-35 | C# closed member set opened | `statement-extra-member` (C#), `R4_38_AStatementWithAnExtraMemberIsRefused` |
| F1-36 | Rust closed member set opened | `statement-extra-member` (Rust) |
| F1-37 | quarantine's intersection drops `key`/`retire` | `R4_39_AQuarantinedAgentRetires` |
| F1-38 | `MaxLifetimeRotations = 511` → `512` | `R7_23_TheNumbersAreTheErrata`, `R7_23_ARotationPastTheLifetimeCeiling…` |
| F1-39 | `RetireAsync` runs the ceiling check | `R7_23_TheCeilingNeverRefusesARetirement` |
| F1-40 | the FOREIGN comparison inverted | `R4_39_KeysReportsAForeignRotation` |
| F1-41 | `rotate` retries on `lifecycle-moved` unconditionally | `R4_39_RotateDoesNotRetryOverAForeignAct` |
| F1-42 | `test-signer.py`'s `sign` accepts a lifecycle `typ` | `R11_20_TheReferenceSignerRefusesALifecycleTypOnSign` |
| F1-43 (GF1a) | the rotation route's gate statement removed | `R4_38_EachLifecycleRouteRefusesBeforeAnythingElse` (rotation row) |
| F1-44 (GF1b) | the gate moved after the authentication call | the same fact, on its `jti` assertion |
| F1-45 (GF1c) | the gate moved after `registry.AddAsync` (via the use case) | the same fact, on its store-row assertion |
| F1-46 (GF9) | `rotate` writes the new key file before the POST | `R4_38_RotateAgainstAGuardedForumLeavesTheProfileByteIdentical` |
| F1-47 | `src/Curia.Canon/Jws/DetachedJws.cs:158`, the shared comparison `if (header.Typ != _typ)` → `if (header.Typ != _typ && !header.Typ.StartsWith("curia-key-", StringComparison.Ordinal))`, relaxing it for lifecycle `typ`s only | `R6_49_TheHeadVerifierRefusesALifecycleTyp` (C#) |
| F1-48 | the conditioned overload inserts outside the gated core | `R4_38_TheConditionedOverloadPassesTheChokepoint` |
| F1-49 | `IsWrite`'s key-action exclusion removed | `R7_23_KeyActsSpendNoPostingBudget` |
| F1-50 | `R5.22`'s failed liveness read treated as `KeyStatus.Active` | `R5_22_ALivenessReadThatFailsRefusesTheRequest` |
| F1-51 | C# `LifecycleCheck`'s header-`kid`-equals-role-member comparison removed, so the authorization verifies under its header `kid` rather than `authorized_by` | `header-kid-disagrees` (C#), from failed to verified |
| F1-52 | Rust `verify_lifecycle`'s header-`kid`-equals-role-member comparison removed, likewise | `header-kid-disagrees` (Rust), from failed to verified |
| F1-53 | C# `LifecycleCheck`'s `jwk.alg`-equals-derived-alg comparison removed | `rotation-jwk-alg-mismatch` (C#) stops being refused |
| F1-54 | Rust `verify_lifecycle`'s `jwk.alg`-equals-derived-alg comparison removed | `rotation-jwk-alg-mismatch` (Rust) stops being refused |
| F1-55 | C# `LifecycleCheck`'s `authorized_by` != `kid` comparison removed | `rotation-self-authorized` (C#) stops failing |
| F1-56 | Rust `verify_lifecycle`'s `authorized_by` != `kid` comparison removed | `rotation-self-authorized` (Rust) stops failing |
| F1-57 | `KeyStatements` reads an unknown statement `typ` (e.g. `curia.key-rotation/2`) as `/1` | `R4_38_AnUnknownStatementTypIsRefused` |
| F1-58 | C# `LifecycleCheck`'s statement-`typ` version check removed | `unknown-statement-typ` (C#) stops reporting could-not-be-checked |
| F1-59 | Rust `verify_lifecycle`'s statement-`typ` version check removed | `unknown-statement-typ` (Rust) stops reporting could-not-be-checked |
| F1-60 | the client's FOREIGN trigger on an `invalid_client` refusal removed | `R4_39_AnInvalidClientRefusalRunsTheForeignCheck` |
| F1-61 | the client's FOREIGN trigger on a `curia/authn/token-key-not-live` refusal removed | `R4_39_ATokenKeyNotLiveRefusalRunsTheForeignCheck` |
| F1-62 | the client's FOREIGN trigger on a `curia/keys/retired` refusal removed | `R4_39_ARetiredRefusalRunsTheForeignCheck` |
| F1-63 | the client's FOREIGN trigger on a key-set read by a verb other than `keys` (`curia read`'s, through `VerifyAllAsync`) removed | `R4_39_AKeySetReadByAnotherVerbRunsTheForeignCheck` |
| F1-64 | a `Curia.Mcp` method gains a call that reaches `KeyActs` or the `sign-lifecycle` verb (e.g. a static method invoking `ExternalSigner`'s lifecycle entry point) | `R11_20_CuriaMcpNeverCallsSignLifecycle`, run in Debug and in Release (the fences read IL that differs by configuration, register D16) |
| F1-65 | C# `LifecycleCheck`'s member-set check moved ahead of its statement-`typ` check | `unknown-statement-typ-extra-member` (C#) turns failed |
| F1-66 | Rust `verify_lifecycle`'s member-set check moved ahead of its statement-`typ` check | `unknown-statement-typ-extra-member` (Rust) turns failed |
| F1-67 | C# entry member set opened (the closed set of members an entry may carry beside `statement`) | `entry-extra-member` (C#), `R4_38_AnEntryWithAnExtraMemberIsRefused` |
| F1-68 | Rust entry member set opened | `entry-extra-member` (Rust) |
| F1-69 | the lifecycle routes' refusal detail text in `src/Curia.Api` changed (e.g. `key rotation is not enabled on this Forum` → `key rotation is disabled on this Forum`) | `R4_38_TheGuardedRefusalIsTheDocumentTheClientStandInServes` |
| F1-70 | `KeyStatements` accepts a rotation `typ` (`curia-key-rotation+jws`) on a retirement's signature | `R4_39_ARetirementSignedUnderARotationTypIsRefused` |
| F1-71 | the shared `b64` check removed, once in C# (`src/Curia.Canon/Jws/DetachedJws.cs:159`, `if (header.B64) return Result<VerifiedContent>.Fail(JwsErrors.B64MustBeFalse());` → removed) and once in Rust (`rust/curia-testis/src/jws.rs:185`, `_ => return Err(JwsError::B64NotFalse),` → `_ => {}`), one run each, as F2-19 and F2-20 | `R4_18_EachLifecycleTypMeetsTheB64AndCritNegatives` (C#) and its Rust twin |
| F1-72 | the shared `crit` check removed, once in C# (`src/Curia.Canon/Jws/DetachedJws.cs:160`–`:161`, `if (!header.Crit.SequenceEqual(RequiredCrit))` and its return → removed) and once in Rust (`rust/curia-testis/src/jws.rs:192`, `return Err(JwsError::CritInvalid);` → removed, leaving the `if` empty), one run each | `R4_18_EachLifecycleTypMeetsTheB64AndCritNegatives` (C#) and its Rust twin |

Rows F1-27…F1-72 (F1-47, F1-71 and F1-72 excepted, whose literal anchors are pinned above) are given their literal anchors from the tree before the first case runs, as F1-2 and the rest are (trap 28); a row still in words when the runner starts is a stop. F1-43…F1-45 and F1-46 are GF1 and GF9: their facts were written with their surfaces (Tasks 1.6, 1.8) and are first seen red here. Each GF row also needs its positive control green unpatched.
- [ ] Expect `falsify: 72 cases, 0 not RED`.

### Task 1.11: Register and documents (PR 1)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - "Observed during the key-binding stage": strike "Only enrollment produces a binding" (closed by Task 1.0's fact). Strike "An EdDSA key still needs a point check" for rotated keys only; the enrolment half stays. Strike "No conformance vector pins the P-256 binding" (M6, closed by `key-bound-entry-es256`).
  - New observation: the domain `AgentKeySet.Rotate`/`Revoke` is unused and superseded by the log-position model, with the decision to delete it deferred.
  - New observation: the client keeps retired private keys (owner question 5).
  - The guard's entry: PR 1's routes and verbs refuse under it (GF1, GF9); still not lifted; NEEDS still open unless a person has closed it.
  - Start-here and "What comes next": PR 1 merged-pending; PR 2 next; TUI PR B may re-scope.
- [ ] `CLAUDE.md` and `README.md`: rotation and retirement exist; compromise does not yet; three new verbs; the exit codes are unchanged in PR 1.
- [ ] Record for the owner, not as a task: the `curia` skill gains no verbs until edited.
- [ ] Scan added lines for invisible characters. Re-read every file:line you wrote.
- [ ] Commit: `Register: rotation and retirement (G21, PR 1)`.

### Task 1.12: Every gate, then PR 1

- [ ] As Task 0.5, plus `cargo test --locked` naming `lifecycle` and `log_author`. Eleven assemblies. **CP-B.**
- [ ] **Human stop before `gh pr ready`: owner question B** (spec §7 q12), as a merge gate by default: the owner's sign-off on the member names of `agent.key-bound` (rotation form) and `agent.key-retired`, recorded in the PR body. Its binding place is the lift record, item (d), over all three kinds; this sign-off freezes nothing. They freeze with R15.1 at a Forum's first append, which is reachable only after the owner lifts the guard.
- [ ] `but push key-rotation`; draft PR. The body covers G21's PR 1 requirements, the Table 10 application, the token-refusal-once note, the falsification result, "merging does not freeze; the lift is the owner's separate act", and the PR attribution line. CI green, then the human stop above, then `gh pr ready`.

---

## PR 2 — compromise (branch `key-compromise`)

### Task 2.0: Base and vector

- [ ] `but branch new key-compromise` from the merged `main`.
- [ ] `acta/key-compromised-entry`: declared over `conformance-ed25519-second`, `from_index` 2 (the index of the rotation that binds it in Task 1.1's implied log), `prev` 3, `reason` `owner-reported`. Its implied log, recorded in `meta.json`, is Task 1.1's entries 0–3 (0 `agent.enrolled`, 1 the enrolment's `agent.key-bound` of `conformance-ed25519-minimal`, 2 the rotation binding `conformance-ed25519-second` under `conformance-ed25519-minimal`'s authorization, 3 the retirement of `conformance-ed25519-minimal`), then 4 the `log.key` entry publishing the conformance log key below, which signs the declaration, then 5 this declaration. R6.57's cascade over that log from `from_index` 2: no rotation at an index ≥ 2 is authorized by `conformance-ed25519-second`, so `kids` is `["conformance-ed25519-second"]`, the compromised key alone; its binding (2) ≤ `from_index` (2) < the declaration's index (5), and the key is live at 5 (entry 3 retires `conformance-ed25519-minimal`, not it). The statement's members are unchanged by the `log.key` entry (`prev` stays 3, since `log.key` is not in the agent's stream), so the vector's bytes, digest and leaf do not move; only `meta.json` records it. It is needed because Task 2.1's published-key check would otherwise refuse Task 2.3's control `R6_57_DeclareCompromiseAcceptsTheVectorsInputs`. The declaration is signed with that log key, detached in R6.37's form with header `typ` `curia-key-compromise+jws` and the log key's `kid` (spec §4.5, KF1, KF2) — never R6.49's `curia-head+jws`. The actor is a role name (`operator`), never a person's name. Add the lifecycle cases `compromised-before-post`, `compromised-after-intervening-rotation` (post at p, a rotation at p+1, the declaration at p+2 with `from_index` ≤ p; disputed), `declaration-signed-as-head`, `compromise-kids-omits-cascade`, `compromise-from-index-after-declaration`, `compromise-kid-of-other-identity` (each failed) and `compromise-unknown-reason` (could not be checked). Add `compromise-declaration-kid-not-valid-at-position` (failed: the declaring log key's `log.key` entry follows the declaration) and `compromise-declaration-kid-position-unknown` (could not be checked: the log key set gives the declaring key no `log_index`). Each lifecycle case's log key set carries every log key with its `log_index`, and each case's log carries the `log.key` entries those indices name. The not-valid case's declaration is generated by `tools/GenerateEnvelopeFixtures` directly, never through the use case, which refuses it. Python-checked leaves, as in Task 1.1. The vector is a proposal until the lift record pins it.
  - **The conformance log key.** The operator's log key is ES256 only (`src/Curia.Operator/LogSigningKey.cs:54` refuses anything other than P-256), so a compromise declaration is ES256 by construction: no Ed25519 seed, and not the `acta/head-entry` vector's kid. `tools/GenerateEnvelopeFixtures` gains a named, fixed P-256 log-key fixture: a pinned private scalar, with a comment that it is a test key only. Task 2.0 uses it for both the declaration's signature and entry 4's `log.key` entry.
  - **Its kid** is the RFC 7638 JWK thumbprint of its public key, computed as `LogSigningKey.FromPem` computes it (`JwkThumbprint.Compute(new Jwk.EcP256(x, y))`, `LogSigningKey.cs:57`), never a fixed string in the style of the generator's envelope fixtures (`conformance-es256-minimal`, `Fixtures.cs:280`). Production derives a log key's kid and never configures it, and a declaration vector whose kid no real log key could carry tests a shape the system cannot produce; no reader compares a kid with its thumbprint today, so a fixed string would pass silently. The generator references `Curia.Canon`, `Curia.Canon.Sodium` and `Curia.Domain.Primitives` only (`tools/GenerateEnvelopeFixtures/GenerateEnvelopeFixtures.csproj`), not `Curia.AuthN`, which holds `JwkThumbprint`. If it cannot reference that assembly, it computes the RFC 7638 form itself, the kid is pinned as a literal in the vector's `meta.json`, and the generator asserts that the computed value equals the pinned one.
  - **Reproducibility is pinned bytes,** the mechanism of `rotation-es256-then-post` (spec §4.6, 'frozen signature bytes'), not RFC 6979: the generator's ES256 keys are random each run (`Signing.NewEs256`, `tools/GenerateEnvelopeFixtures/Signing.cs:47`) and ECDSA signing is randomized. The pinned private scalar makes the public key and kid reproducible, and the declaration's signature bytes are pinned. The generator never re-signs an existing vector: it re-verifies the pinned signature and keeps it. The check to run: the generator run twice leaves every file byte-identical.
- [ ] Commit.

### Task 2.1: The operator's declaration (R6.26 (rev.), R6.57)

- [ ] `DeclareCompromise` use case: `DeclareAsync(AgentId agent, Kid kid, long fromIndex, CompromiseReason reason, LogSigningKey key, string operatorName, CancellationToken ct)`.
  - Fold the stream and the log.
  - Check `fromIndex` ≥ the binding index of `kid` (`curia/keys/before-binding`).
  - Compute the cascade transitively over rotation entries with `authorized_by` ∈ set and index ≥ `fromIndex`.
  - After the gate and the key read, and before signing, refuse when the signing key is not in the folded log's key set with an equal public JWK (`log.key` already published): `curia/keys/log-key-not-published`, the verb exiting `ExitCode.Refused`, with the log unchanged. The check follows the gate, so GF2's order stands.
  - Build the statement; sign it with the log key; append at the read version with actor = `operatorName`.
- [ ] `curia-operator compromise --agent <id> --kid <kid> --from-index <n> --reason owner-reported|operator-observed --operator <role>` needs `CURIA_LOG_SIGNING_KEY_PEM`. **Its first act after argument parsing is the gate**: when `compromise` is not enabled it prints `curia/keys/lifecycle-not-enabled` and exits `4` (`ExitCode.LifecycleNotEnabled`, new; 0–3 are taken), before `CURIA_LOG_SIGNING_KEY_PEM` is read or anything is signed or appended. Committed with the verb (GF2). `--operator` takes a role name; the help text says never a person's name. It prints `compromised` with every `kid` and the entry index through `SpanText`, as the operator tool writes. The declaration is signed under `curia-key-compromise+jws`.
- [ ] `KeyLifecycle.StatusNow` returns `Compromised` for every `kid` in any declaration. `R5.22`, `ResolveLiveAsync` and the key set follow.

**Facts:**
- `R6_26_ADeclarationSignedByAnAgentKeyIsRefusedByReaders`: Rust and C#, over a variant vector.
- `R6_26_TcBelowTheBindingIsRefused`.
- `R6_57_AKeyTheCompromisedKeyRotatedInAfterTcIsCompromised`.
- `R6_57_AKeyItRotatedInBeforeTcStands`.
- `R6_57_TheCascadeIsTransitive`.
- `R6_26_TheOperatorRefusesALogKeyTheLogHasNotPublished`: behind the permissive gate, with a valid log key the log has not published, the result is a refusal (`curia/keys/log-key-not-published`), and the head's tree size and the row count are unchanged. Positive control: the same call after `sign-head` has published the key appends.
- `R6_57_NewPostsUnderACompromisedKeyAreRefused`: 403 `curia/keys/compromised`.
- `R5_22_ACompromisedKeysTokenIsRefused`.
- `R4_38_TheOperatorVerbRefusesBeforeItReadsTheLogKey` (GF2): the guarded production build, `CURIA_LOG_SIGNING_KEY_PEM` set to a valid key (so a missing-key exit cannot impersonate the guard's), the log-signing seam replaced by one that throws; exit is exactly 4, the head's tree size and the events row count are unchanged, the throwing signer is never reached. Positive control, a separate invocation: under the permissive gate in the test composition, first publish the supplied key with `curia-operator sign-head` (the same setup as `R6_26_TheOperatorRefusesALogKeyTheLogHasNotPublished`'s control), keep the real log-signing seam, not the throwing one, and assert the declaration appends with exit 0. Its setup must satisfy Task 2.1's published-key check (the bullet above beginning 'After the gate and the key read, and before signing'). Red when the gate moves after signing (F2-8).
- [ ] The use-case facts compose the permissive gate; `R6_57_NewPostsUnderACompromisedKeyAreRefused` and the readers' facts seed declarations through the test composition only.
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
- [ ] Control: `DeclareCompromise` accepts exactly `acta/key-compromised-entry`'s inputs (the implied log of Task 2.0, `kid` `conformance-ed25519-second`, `from_index` 2, `reason` `owner-reported`), behind the permissive gate, and the entry it appends equals the vector's payload member for member, so the vector is reachable by the use case, not only by hand (`R6_57_DeclareCompromiseAcceptsTheVectorsInputs`).
- [ ] Both readers hold a declaration to KF3: `kids` equal the cascade they recompute, in order; each kid bound to `agent_id`; binding(`kids[0]`) ≤ `from_index` < the declaration's index; header `typ` `curia-key-compromise+jws` and `kid` in the log key set, valid at its position (KF1, KF2); an unknown `reason` → could not be checked (KF4). Valid at its position means: a declaration at log index d is valid only if its header `kid` names a key in the log key set whose `log_index` is a non-negative I-JSON integer k with k < d, the index and never `valid_from`. An unknown `kid` is failed (F2-3's fact); a `log_index` absent, negative or not an integer is could not be checked, never verified, in both readers alike. In C#, k is could not be checked unless `ClientJson.WholeNumber` returns a value with `k >= 0`; only then is `k < d` compared. (`WholeNumber` returns a negative integer as itself, and `LogJwk` stores an absent one as -1, `src/Curia.Client/ActaDocuments.cs:218`, so the `k >= 0` clause is what refuses both.) The parser's `SafeMaxInteger` (`src/Curia.Canon/Json/JsonReader.cs:50`) already refuses an integer above 2^53-1. Both head verifiers already refuse `curia-key-compromise+jws` by exact `typ` match; nothing is built for it (the regression guard below).
- [ ] `curia-testis` learns a log key's position: a log-key-set reader in `acta.rs`, beside `verify_head`, reusing `jwk.rs`'s key-member parsing (not a second JWK parser) and reading `log_index` as `Option<u64>`, `None` when the member is absent, negative or not an I-JSON integer. `JwkSet::parse`, the agent key sets and `verify_head`'s behaviour are unchanged (the head's own key position is a register entry, not this task).
- [ ] Facts: the `compromised-before-post` case in both runners → disputed; the `compromised-after-intervening-rotation` case in both runners → disputed; the seven PR 2 KF cases in both runners; `R6_57_CuriaVerifyExitsNineOnADisputedPost`; `R6_57_DisputedIsNeverPrintedAsVerified`; `R6_26_TheCompromiseTypMeetsTheB64AndCritNegatives` (`DetachedJwsTests.cs`) and its Rust twin `compromise_typ_meets_the_b64_and_crit_negatives` (`jws.rs`'s tests): the existing `b64`/`crit` negatives, run with `curia-key-compromise+jws`, refuse as they do for posts (F2-19, F2-20); `R6_26_ANegativeLogIndexCouldNotBeChecked` (C#) and its Rust twin `negative_log_index_could_not_be_checked`: the `compromise-declaration-kid-position-unknown` case's log key set with the declaring key's `log_index` set to -1, and again to -5, each reads could not be checked (F2-25, F2-26).
- [ ] Regression guards (green on today's tree): `R6_26_TheHeadVerifierRefusesACompromiseTyp` (C# and Rust): a valid log-key signature over a head's bytes under `curia-key-compromise+jws` is refused. It passes before work starts, because both head verifiers already refuse any `typ` but `curia-head+jws` (exact match), and it pins that behaviour against a future relaxation (F2-18).
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
| F2-8 (GF2) | `compromise`'s gate check moved after the log-key read and signing | `R4_38_TheOperatorVerbRefusesBeforeItReadsTheLogKey` |
| F2-9 | C# declaration `typ` check removed | `declaration-signed-as-head` (C#) |
| F2-10 | Rust declaration `typ` check removed | `declaration-signed-as-head` (Rust) |
| F2-11 | C# takes `kids` as given | `compromise-kids-omits-cascade` (C#) |
| F2-12 | Rust takes `kids` as given | `compromise-kids-omits-cascade` (Rust) |
| F2-13 | C# drops the bound-to-`agent_id` check | `compromise-kid-of-other-identity` (C#) |
| F2-14 | Rust drops the bound-to-`agent_id` check | `compromise-kid-of-other-identity` (Rust) |
| F2-15 | C# drops `from_index` < declaration index | `compromise-from-index-after-declaration` (C#) |
| F2-16 | Rust drops `from_index` < declaration index | `compromise-from-index-after-declaration` (Rust) |
| F2-17 | an unknown `reason` read as verified (both readers, one row each run) | `compromise-unknown-reason` |
| F2-18 | `rust/curia-testis/src/jws.rs:176`, `verify_typed`'s shared `typ` arm `Some(typ) if typ == expected_typ => {}` → `Some(typ) if typ == expected_typ \|\| typ.starts_with("curia-key-") => {}`, relaxing it for lifecycle `typ`s only | `R6_26_TheHeadVerifierRefusesACompromiseTyp` (Rust) |
| F2-19 | `src/Curia.Canon/Jws/DetachedJws.cs:159`, `if (header.B64) return Result<VerifiedContent>.Fail(JwsErrors.B64MustBeFalse());` → removed. This is the shared check: the code has one `b64` refusal for every `typ`, so the row removes it for all of them, and the compromise-`typ` fact is the one it must turn red | `R6_26_TheCompromiseTypMeetsTheB64AndCritNegatives` (C#) |
| F2-20 | `rust/curia-testis/src/jws.rs:185`, `verify_typed`'s `_ => return Err(JwsError::B64NotFalse),` → `_ => {}`. The shared check, as F2-19 | `compromise_typ_meets_the_b64_and_crit_negatives` (Rust) |
| F2-21 | C# reader drops the declaration's key-position comparison (k < d) | `compromise-declaration-kid-not-valid-at-position` (C#) |
| F2-22 | the Rust reader drops it | `compromise-declaration-kid-not-valid-at-position` (Rust) |
| F2-23 | an absent `log_index` read as valid (both readers, one row each run, as F2-17) | `compromise-declaration-kid-position-unknown` |
| F2-24 | `DeclareCompromise`'s published-key check removed | `R6_26_TheOperatorRefusesALogKeyTheLogHasNotPublished` |
| F2-25 | a negative `log_index` (-1, -5) read as valid (both readers, one row each run, as F2-23) | `R6_26_ANegativeLogIndexCouldNotBeChecked` (C#), `negative_log_index_could_not_be_checked` (Rust) |
| F2-26 | C#'s `k >= 0` clause dropped, so a negative k is compared with d | `R6_26_ANegativeLogIndexCouldNotBeChecked` (C#), both its -1 and -5 rows |

Also re-run GF1 and GF9's facts unpatched (they must stay green on PR 2's tree), and F1-43…F1-46 once more if PR 2 touched `ForumEndpoints.cs` or the client.
- [ ] Expect `falsify: 26 cases, 0 not RED`.

### Task 2.5: Register and documents (PR 2)

- [ ] `IMPLEMENTATION_PLAN.md`:
  - G21 fully built.
  - R12.11's runbook gaps (notifying the owner and recent citers) opened as a register entry.
  - TUI PR B gets its disputed header row.
  - KF6's register entry: re-read the register and confirm **D43** (filed by the rulings fold of review round 2) is present and open, with the trigger "before `curia-operator sign-head` runs unattended or from any automated process, compromise declarations move off the log key to a separate operator declaration key (R6.26 (rev.), KF6; cybersecurity Report B6)" and the status "open, by decision, not a defect in current behaviour". PR 2 builds the declaration on the log key, so it is the PR that makes the trigger live. If the entry is absent, file it with that trigger and status under the next free D-number, taken only after re-reading the register immediately before the edit.
- [ ] `CLAUDE.md` and `README.md`: compromise, exit 9, the operator verb.
- [ ] Record for the owner: the `curia` skill's exit codes.
- [ ] A dry run through the **test composition** against a database created and dropped by that run (never the production binary with a setting; **CP-D**: assert the database name came from this run's create step before any append), in which both readers render disputed for a post with a rotation between it and the declaration. There is no production declaration until after the owner's lift.
- [ ] `IMPLEMENTATION_PLAN.md`: the guard's entry gains PR 2's GF2; the lift is still the owner's, with NEEDS still open unless a person has closed it.
- [ ] Scan for invisible characters. Commit.

### Task 2.6: Every gate, then PR 2

- [ ] As Task 0.5. **CP-B.** `but push key-compromise`; draft PR with "merging does not freeze; the lift is the owner's separate act" and the PR attribution line; CI green.
- [ ] **Human stop before `gh pr ready`: owner question B for `agent.key-compromised`** (merge gate by default; binding place the lift record (d)). Then `gh pr ready`.
