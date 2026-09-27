# Keys Bound in the Acta — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. On this project every subagent runs on **Opus** (`model: "opus"`), never Sonnet.

**Goal:** Close register **D28** (opened here): the Registrar's key store was the only record of which key an identity held, and every path that honoured a key — ingest, the token endpoint, and the key set every reader verifies with — took the store's word. A key row the log never bound signed posts and minted tokens as the identity it was filed under, and a reader could not tell a key substituted in the store from the identity's own. After this stage:
- every enrollment appends `agent.key-bound`, carrying the key as its public JWK, in the same append as `agent.enrolled` (R4.34);
- the Forum honours a stored key only when the log binds it, and publishes each key with the log index of its binding (R4.35);
- a lost row's recovery registers only the key the log carries; every binding an identity holds counts, so a key R4.18's rotation binds is re-announced as the enrolled one is; and an identifier the log never enrolled is not bound while the store holds more than one key for it (R4.31 revised);
- a client assertion's and a DPoP proof's header `alg` must name its key's algorithm (R5.21, one of D27's two leftovers);
- the reference client and `curia-testis` establish from the log alone that the key behind a post was bound to its author before it (R6.54).

It also carries out **D16**'s decided CI change: the architecture rules run in Debug as well as Release.

**Architecture:**
- **Errata G16 comes first** (Task 2): R4.34, R4.35, R4.31 (revised), R4.16 (rev. 2), R5.21 and R6.54.
- **One rendering of a public key** (Task 3). `Curia.Canon.Jws.PublicJwk.Of` renders a stored key as the JWK the key set publishes; the key set and the log's binding both use it, so the key served and the key bound are one computation.
- **The binding is its own entry** (Task 4). `EnrollAgent` appends `agent.enrolled` and `agent.key-bound` in one append. `EnrollmentBinding` reads every binding an identity holds; a pre-R4.34 identity is bound by its `kid` alone.
- **One rule over the store** (Task 5). `LogBoundKeys` asks the store first, then holds its answer to the log's binding. Ingest, the token endpoint (through a thin adapter) and the key set all read through it.
- **The leaf format does not move.** R6.46's computation is untouched; `agent.key-bound` is a new entry class under G9's one encoding, pinned by `conformance/acta/key-bound-entry` and read by two independent readers (Tasks 7 and 8) before it becomes permanent in anyone's log.

**Tech Stack:** .NET 10, C# 14, xUnit v3, Npgsql + Postgres 18 with pgvector, `curia-testis` (Rust, gains `log author`), GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-09-27-keys-bound-in-the-acta-design.md`. Read it first; this plan argues from it. Commit the spec and this plan on the branch before Task 1, as `Spec: keys bound in the Acta` and `Plan: keys bound in the Acta — eleven tasks, CI line and errata first`. Both are committed; the amendments after the pre-flight scan replace the two files in one further commit before Task 1, and the amendments after Task 2's review in one more, before Task 2's fix round.

**Branch:** `keys-bound-in-the-acta`, opened from `main` at 1dbe0ff (the enrollment stage, PR #79). This plan was build-checked against a `git archive` of the workspace at dc17a3c — 1dbe0ff plus the unmerged SDK pin, which touches `global.json` alone. Every code block below was produced from that build-checked tree, applied in task order to a fresh archive by a script, and compared with it byte for byte; the register and document edits of Task 10 were checked to anchor exactly once. On that fresh archive, step by step, every compile error, red fact and count this plan states for Tasks 2–8 was reproduced by running the step's own command. **Amended after the pre-flight scan** (the spec's §8 lists each finding and what was done with it): every code block the amendments touch was produced from a `git archive` of 1dbe0ff with the workspace's `global.json` copied in, built and run there; the amended plan was then applied in task order to a second fresh archive by an anchor-exact script and compared with that tree byte for byte; and Task 9's runner ran in a git-backed copy (the archive, `git init`, a commit), so its `git diff --quiet` proof ran for real. **Amended again after Task 2's review** (the spec's §9): the entry's text, Task 4's refusal of an identifier the log never enrolled while the store holds several keys for it, and two facts and three falsification cases for gaps the review named. The amended plan was applied to a fresh `git archive` of 38a21fa (Tasks 1 and 2 committed) with bae4ec8's errata restored under it, so that Task 2 applies as written, and the workspace's `global.json` copied in; every step that states an outcome was run there, and Task 9's runner ran in a git-backed copy of that tree. If `main` has moved past 1dbe0ff, an anchor may no longer match exactly once: stop at the first that does not, and report it rather than improvising a match.

## Global Constraints

**Build and test**
- `dotnet build Curia.sln -c Release` must report **0 warnings**. Warnings are errors under `AnalysisLevel latest-all` with `EnforceCodeStyleInBuild`. The analyzers this plan's code had to satisfy while it was built: CA1062 (a parameter dereferenced unchecked — `row is "…" or "…"`, never `row.StartsWith`), CA1508 (a condition the analyzer can prove constant), CA1823 (an unused field), CA1849 (a synchronous read in an async test), CA2007 (`ConfigureAwait(false)` outside a test body).
- Tests run with `-c Release`, which is what CI runs. Before any run touching `Curia.Infrastructure.Tests` or `Curia.Api.Tests`, export `CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"`; never write the username out. Build `curia-testis` first: `cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis`, and export `CURIA_TESTIS_BIN` as that tree's `rust/curia-testis/target/debug/curia-testis`. From Task 7 on, rebuild it whenever `rust/` changes: the Api suite runs whatever binary that path holds.
- Count test assemblies, never totals. **Eleven** must appear. This plan adds no test project.
- A gate's output is read as `grep -E "Passed!|Failed!"` prints it. Never pipe it through anything that strips the status word, and never `head` it.
- Counts quoted below are from the build-checked tree. On `main` at 1dbe0ff they were 15 / 30 / 39 / 66 / 73 / 106 / 203 / 215 / 262 / 279 / 608 (Canon.Sodium, Architecture, Domain.Primitives, AuthN, Mcp, Infrastructure, Client, Api, Canon, Application, Domain), and `curia-testis` 211 tests in 17 binaries.

**Invariants this stage must not break**
- **R15.1's frozen set does not move.** R6.46's leaf computation, the envelope, and canonicalization are untouched. A new event type is a payload decision (G9); `agent.enrolled`'s payload is unchanged.
- **Append-only (R11.6), and R4.32's grant.** No UPDATE is added anywhere; `agent_keys` gains no column. The binding is an event.
- **The domain depends on nothing.** New types live in `Curia.Canon` (BCL only, CS-6), `Curia.Application` or `Curia.Api`. Time enters only through `TimeProvider` (CS-9).
- **Refusals name identifiers, never key material.** `curia/keys/not-bound-by-the-log` names the agent and the `kid`. `curia/authn/alg-key-mismatch` names two algorithms.
- **R5.20's refusal stays indistinguishable.** `LogBoundKeys` asks the store first, so a `kid` registered to another agent still meets the refusal a `kid` registered nowhere meets, byte for byte.

**Lessons the enrollment stage paid for, built in**
- **The falsification runner** (Task 9) calls a suite RED only on its own failure line (`Failed!`, or cargo's `test result: FAILED`), never on an exit code; counts any compiler error as BUILD FAILED; exits non-zero on anything not RED; restores in a `finally` by plain copy (`shutil.copyfile`, never `copy2`, never `git checkout`); and proves each restore twice, by `filecmp.cmp` against the kept copy and by `git diff --quiet`, which must run inside the repository: outside one, `git diff` exits 129 with a usage message, and a runner that reads that as anything but DIRTY proves nothing.
- **No falsification patch is a constant expression.** Where a patch disables a condition, it compares against a value that never occurs (`"no-such-kid"`, `-postIndex - 1`) rather than writing `true` or `false`, so no analyzer can prove it dead and no residue scan can mistake it for code; the other patches change a value, a call, a pattern or a type name.
- **Every claim in the register is verified against the tree** at the moment it is written: each file:line, test name and count is re-read, never carried from a reviewer, the controller, or this plan.
- **Comments state only what the code enforces.** Task 10's scan lists every comment this stage touched; each is re-read against the code under it.
- **Every anonymous route answers no 500.** The key set now reads the log; `R4_35_TheKeySetAnswersEveryAgentWithoutA500` holds it, and falsification case 7 shows the order that keeps it so.
- **Escapes are written so they land as escapes.** Every non-ASCII test character in C# is a `\u` escape; in Python heredocs, `\\n` where the file must hold `\n`. Task 10 scans every added line for invisible characters.
- **The full gate list** in `CLAUDE.md` runs in Task 11, cargo and the differential included, and the architecture project in Debug after a Debug build of the solution.
- **The leaf's content changes, so a conformance vector and a `curia-testis` implementation ship with it** (Tasks 4 and 7).

**Numbering and test data**
- On this reading the entry is **G16**; the requirements are **R4.34**, **R4.35**, **R5.21** and **R6.54**, with **R4.31 (revised)** and **R4.16 (rev. 2)**; the register entry is **D28**. Task 2 re-derives the errata numbers and **stops** if the tree disagrees; Task 10 does the same for D28.
- Test identifiers use `https://agents.example/…`. Key bytes are real P-256 SubjectPublicKeyInfo from `ECDsa`, or the published envelope fixture's Ed25519 key.

**Characters**
- No file this plan writes may contain a character in U+00AD, U+200B–U+200F, U+202A–U+202E, U+2028–U+2029, U+2060–U+2069, U+FEFF or U+FFFE. Task 10 scans for them.

**Version control and privacy**
- Use `but` only, on branch `keys-bound-in-the-acta`. Never `git commit`, `checkout`, `rebase` or `stash`.
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. `but commit` has no `-F`; use `-m "$(printf '…')"`.
- No usernames, private IPs, host names or home paths go in any tracked file.

## Review Focus

1. **A key the store holds and the log never bound** (D28's finding). It must mint no token, sign no question, and not appear in the key set, and each refusal must name `curia/keys/not-bound-by-the-log`. The damage is asserted first. Test: Task 5's `KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing`; falsification cases 5, 6, 8 and 9 (the rule, the key set, each path's wiring).
2. **A lost row's recovery presenting other bytes under the bound `kid`** (G14's fourth cost). It must be refused `curia/keys/material-immutable`, and the victim's own recovery must serve the key set it was served before the loss. Tests: `EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName` and `EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused`; cases 10–12, whose layering is deliberate (each half backs the other; R4.35 still refuses the impostor's token when both are off).
3. **The binding's shape is the vector's shape.** `EnrollIdentityTests.R4_34_AnEnrollmentWritesTheConformanceVectorsPayload` holds the writer to `conformance/acta/key-bound-entry`, so the vector pins what the Forum writes (trap 1); `LogLeafTests`' list of acta vectors must include it.
4. **An identity enrolled before R4.34.** It must still post and obtain tokens, bound by its `kid` alone, and a reader must report its posts as *could not be checked*, never *verified*. Tests: `KeyBindingTests.R4_35_AnIdentityEnrolledBeforeKeyBindingIsHonouredByItsKidAlone`, `LogBoundKeysTests.R4_35_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone`, `PostVerifierTests.R6_54_AKeyTheLogNamesByKidAloneIsNotEstablished`, and the exit-3 half of `ActaEndpointTests.R6_54_TestisEstablishesAuthorshipFromTheLogAlone`; cases 21, 22 and 26.
5. **The seam the enrollment stage left** (its spec's Decision 8). With two bindings, re-announcing the second key must be a re-announcement, as re-announcing the first is. Test: `EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs`; case 4.
6. **A reader verifies under the key the log carries, not the one the key set serves.** When the log bound another key, the signature check (against the key set) verifies and the binding check must fail. Test: `PostVerifierTests.R6_54_ALogThatBoundAnotherKeyFailsTheBindingThoughTheSignatureVerifies`; case 19.
7. **`curia-testis log author` reads no agent key set.** It takes two entries, two proofs, a head and the log's key set, and exits 0, 1 or 3. Tests: `tests/log_author.rs` and the end-to-end Acta fact; cases 24–26.
8. **The renderer and the verifier agree, and the RFCs anchor the renderer.** `PublicJwkTests` derives the expected JWKs from RFC 8037's and RFC 7515's example keys, and holds the renderer to each adapter's rule on every `KeyMaterials` row. Case 18 is the one that shows why the RFC anchor matters: a renderer that swapped a P-256 key's coordinates would agree with itself on both sides of every Forum-side comparison.
9. **R5.21.** A header naming the other allowed algorithm is refused by name at both validators. Tests: the two `R5_21_…` AuthN facts and `StoredKeyFormTests.R5_21_…`; cases 13 and 14.
10. **D16.** A seven-case string switch in `Curia.Domain` fails CS-7 in Debug and passes in Release; the new CI step builds the solution in Debug and runs the architecture rules against it. Case 27, filtered to `LayeringTests` so that its red is CS-7's alone.
11. **A binding after the post is the log's silence, not its contradiction** (the spec's Decision 11, amended). The author's own binding of the post's `kid` at a leaf not before the post must read *could not be checked* in both readers, never *failed* and never *verified*: an identity enrolled before `agent.enrolled` existed gains its first binding after all its history when anyone re-presents its public key. Tests: `PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished`, `log_author.rs`' `r6_54_a_key_bound_after_the_post_is_not_established`, and the `bound-late` control of `ActaEndpointTests.R6_54_TestisEstablishesAuthorshipFromTheLogAlone`; cases 20, 24, 28 and 29.
12. **Each reader compares the binding with the post before it trusts the binding's key.** An entry for another identity or another `kid` fails, whatever its type. Tests: `PostVerifierTests.R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey`, `log_author.rs`' `r6_54_a_binding_for_another_identity_fails` and `r6_54_another_identitys_enrollment_fails`; cases 25 and 30.
13. **An identifier the log never enrolled, whose key store holds several keys** (Task 2's review). A request presenting any of them must be refused `curia/enroll/keys-ambiguous` and write nothing, and with one stored key the same request must enroll the identifier and bind that key. Binding whichever key arrived would let anyone holding one row's public key make that key the identity's, and turn every post signed under its own key into a failure of the signature check: the demotion the pre-flight scan's B1 refused, reached another way. Tests: `EnrollIdentityTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys` and `EnrollmentBindingTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented`; case 31.
14. **The key's binding is proven under the head the client verified, and `curia_verify` reports the check on a line of its own** (Task 2's review). Tests: `PostVerifierTests.R6_54_AKeyBindingProvenUnderAnotherRootFails` and `PropertyP22ToolResultTests.R6_54_TheVerifyToolReportsTheKeyCheckSeparately`; cases 32 and 33.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `.github/workflows/ci.yml` | The Debug architecture step (1); the Rust suite's count (7) | 1, 7 |
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` | Entry G16; six index rows | 2 |
| `src/Curia.Canon/Jws/PublicJwk.cs` (new) | The one rendering of a stored key as its public JWK | 3 |
| `src/Curia.Api/Jwks.cs` | Renders through `PublicJwk` (3); publishes bound keys with `curia_log_index` (5) | 3, 5 |
| `src/Curia.Api/ActaEndpoints.cs` | `ToObject` internal, shared with the key set | 3 |
| `tests/Curia.Canon.Sodium.Tests/PublicJwkTests.cs` (new) | RFC-anchored renderings; renderer against each adapter | 3 |
| `src/Curia.Application/Projections/AgentStandingProjection.cs` | `KeyBoundType`, `JwkField`; the `KeyIdField` remark | 4 |
| `src/Curia.Application/Credentials/EnrollmentBinding.cs` | `KeyBinding` and the set of bindings | 4 |
| `src/Curia.Application/Credentials/EnrollAgent.cs` | Appends the binding; refuses other bytes under a bound `kid`; `EnrollmentErrors.KeysAmbiguous` | 4 |
| `src/Curia.Application/Credentials/EnrollIdentity.cs` | R4.31 (revised) before the store, several stored keys included | 4 |
| `tests/Curia.Application.Tests/TestKeys.cs` (new) | A real P-256 key under a `kid` | 4 |
| `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs` | Seven new facts; counts of a stream's entries | 4 |
| `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, `SearchProjectorTests.cs` | `RecordAsync` takes a key; log counts | 4 |
| `conformance/acta/key-bound-entry/` (new), `conformance/index.json`, `conformance/README.md` | The new entry kind, pinned | 4 |
| `rust/curia-testis/tests/vectors.rs`, `tests/Curia.Domain.Tests/Acta/LogLeafTests.cs` | Vector counts and lists | 4 |
| `tests/Curia.Api.Tests/EnrollmentBindingTests.cs` | R4.31 (revised) over HTTP: the lost row's other bytes, and several stored keys, each refused by name | 4 |
| `tests/Curia.Api.Tests/EnrollmentIdentifierTests.cs`, `FlagPrivacyGateTests.cs` | An enrollment's two entries | 4 |
| `src/Curia.Application/Ports/IAuthorKeyRegistry.cs` | `NotBoundByTheLog` | 5 |
| `src/Curia.Application/Credentials/LogBoundKeys.cs` (new) | R4.35: the resolver and the key set | 5 |
| `tests/Curia.Application.Tests/Credentials/LogBoundKeysTests.cs` (new) | The rule, six facts | 5 |
| `src/Curia.Api/Adapters/LogBoundAgentKeyResolver.cs` (new) | R4.35 through AuthN's port | 5 |
| `src/Curia.Api/Program.cs`, `src/Curia.Api/ForumEndpoints.cs` | The enrollment route's 409 for `curia/enroll/keys-ambiguous` (4); wiring and the key set route (5) | 4, 5 |
| `tests/Curia.Api.Tests/ForumFixture.cs` | `EnrollBeforeKeyBindingAsync` | 5 |
| `tests/Curia.Api.Tests/KeyBindingTests.cs` (new) | R4.35 and R6.54's Forum half, over HTTP | 5 |
| `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, `TokenSubjectBindingTests.cs` | Rows moved to pre-G16 identities; the orphan's refusal | 5 |
| `src/Curia.AuthN/AuthNErrors.cs`, `ClientAssertionValidator.cs`, `AccessTokenValidator.cs` | R5.21 | 6 |
| `tests/Curia.AuthN.Tests/ClientAssertionValidatorTests.cs`, `AccessTokenValidatorDpopTests.cs`, `tests/Curia.Api.Tests/StoredKeyFormTests.cs` | R5.21's facts | 6 |
| `rust/curia-testis/src/acta.rs`, `src/bin/curia-testis.rs`, `tests/log_author.rs` (new) | `verify_author` and `log author` | 7 |
| `tests/Curia.Api.Tests/ActaEndpointTests.cs`, `tests/Curia.Api.Tests/ForumFixture.cs` | `log author` end to end; `BindKeyAfterItsPostsAsync` | 7 |
| `src/Curia.Client/ForumDocuments.cs`, `ActaCheck.cs`, `PostVerifier.cs`; `src/Curia.Client.Cli/Program.cs`; `src/Curia.Mcp/ToolText.cs` | R6.54 in the reference client | 8 |
| `tests/Shared/StubLog.cs`, `tests/Curia.Client.Tests/PostVerifierTests.cs`, `tests/Curia.Mcp.Tests/PropertyP22ToolResultTests.cs`, `tests/Curia.Api.Tests/StubFidelityTests.cs` | The stub's binding and knobs; seven client facts and one adapter fact; the stub held to the Forum's key set | 8 |
| `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md`, the spec | Register, trap 22, what comes next | 10 |

---

### Task 1: The architecture rules in Debug too (register D16, option 1)

**Files:**
- Modify: `.github/workflows/ci.yml`

**Why first.** It is the CI change the moderation stage decided (its spec's Decision 23) and nobody made, and every later task's architecture rules then run in both configurations in CI rather than only on a developer's machine. No task depends on it; it depends on nothing.

- [ ] **Step 1: Confirm the branch**

The branch `keys-bound-in-the-acta` was opened before this task and carries the spec and plan commits. Do not create it again.

```bash
but status
```

Expected: `keys-bound-in-the-acta` is applied, with the spec and plan commits on it.

- [ ] **Step 2: Add the step**

In `.github/workflows/ci.yml`, replace:

```yaml
        run: dotnet test Curia.sln --no-build --configuration Release

  rust:
    name: Rust — curia-testis
```

with:

```yaml
        run: dotnet test Curia.sln --no-build --configuration Release

      # D16, decided as option 1: the architecture rules run in Debug as well. NetArchTest reads IL,
      # and IL differs by configuration: a seven-case string switch in Curia.Domain fails CS-7 in
      # Debug and passes it in Release, so this job, running Release alone, was green while every
      # developer's Debug run was red. The whole solution is built in Debug first: CS-15 reads
      # Curia.Domain.Tests and Curia.Application.Tests from their Debug output, and neither is a
      # reference of the architecture project, so `dotnet test` on that project alone never builds them.
      - name: Architecture rules (Debug)
        run: |
          dotnet build Curia.sln --no-restore --configuration Debug
          dotnet test tests/Curia.Architecture.Tests --no-build --configuration Debug

  rust:
    name: Rust — curia-testis
```

- [ ] **Step 3: Run the new step's command locally**

```bash
dotnet restore Curia.sln --locked-mode
dotnet build Curia.sln --no-restore --configuration Debug --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Architecture.Tests --no-build --configuration Debug --nologo 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `0 Warning(s)`, then `Passed!  - Failed:     0, Passed:    30, …  - Curia.Architecture.Tests.dll (net10.0)`. The solution build is what puts `Curia.Domain.Tests` and `Curia.Application.Tests` where CS-15 reads them: neither is a reference of the architecture project, so `dotnet test` on that project alone never builds them. Without it a fresh checkout, which is CI's state, prints `Failed!  - Failed:     1, Passed:    29` (CS15: `Expected Curia.Domain.Tests.dll at …/bin/Debug/…`), and a used tree reads whatever stale Debug test assemblies are on disk. Task 9's case 27 is the evidence that this step carries information — a seven-case string switch in `Curia.Domain` fails it and passes the Release run above it.

- [ ] **Step 4: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'CI: the architecture rules run in Debug as well (D16, option 1)\n\nNetArchTest reads IL, and IL differs by configuration: a seven-case string\nswitch in Curia.Domain fails CS-7 in Debug and passes it in Release, so CI,\nrunning Release alone, was green while every developer Debug run was red. The\nsolution is built in Debug first: CS-15 reads the Debug output of two test\nassemblies, which testing the architecture project alone never builds.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 2: Errata entry G16

**Files:**
- Modify: `curia-whitepaper-ERRATA-AND-ADDENDUM.md`. Insert the entry immediately before `# Consolidated proposed-requirements index`, and six rows at the end of that index's table, after the `R4.33 | … | G15` row.

**Interfaces:**
- Consumes: nothing.
- Produces: the requirement text every later task implements.
  - **R4.34**: every enrollment the log records binds its key, by an `agent.key-bound` entry carrying the public JWK, in the same append as `agent.enrolled`.
  - **R4.35**: a stored key is honoured, and published, only as the log binds it; each published key names its binding's index.
  - **R4.31 (revised)**: enrollment against the set of bindings; a lost row's recovery registers only the bound key; an identifier the log never enrolled is refused while the store holds several keys for it.
  - **R4.16 (rev. 2)**: the store holds every honoured key; the log decides which it honours.
  - **R5.21**: a header's `alg` is its key's.
  - **R6.54**: a reader's fourth check.

**Fix round after Task 2's review.** 38a21fa installed the first text of this entry, and its review found four Important and eleven Minor defects in it (the spec's §9). On a tree where that text is installed, the round replaces it rather than adding beside it: delete the entry, from its heading `## G16 — The key store was the only record …` through the blank line before `# Consolidated proposed-requirements index`, and the six index rows that end `| G16 |`, which leaves the errata byte-identical to bae4ec8's (check with `git diff bae4ec8 -- curia-whitepaper-ERRATA-AND-ADDENDUM.md`, which must print nothing); then run Steps 1 to 4 as written. Step 1's numbers are those of that restored text. The commit is Step 5's alternative message.

- [ ] **Step 1: Re-derive the numbers, and stop if they moved**

```bash
grep -n "^## G[0-9]" curia-whitepaper-ERRATA-AND-ADDENDUM.md
python3 - <<'EOF'
import re, collections
DEF = re.compile(r"^\*\*(R(\d+)\.(\d+))([^*]*)\*\*", re.MULTILINE)
hi = collections.defaultdict(int)
for f in ("curia-agent-forum-WHITEPAPER.md", "curia-whitepaper-ERRATA-AND-ADDENDUM.md"):
    for _, sec, num, _ in DEF.findall(open(f, encoding="utf-8").read()):
        hi[int(sec)] = max(hi[int(sec)], int(num))
print({k: f"R{k}.{v}" for k, v in sorted(hi.items())})
EOF
python3 tools/spec-checks/check-spec.py
```

Expected:
- The entries are G1–G3 and G5–G15. G4 is reserved for PR #59's Part A, and there is no G16.
- The script prints `4: 'R4.33'`, `5: 'R5.20'` and `6: 'R6.53'`.
- `spec-checks: clean`.

**If any of these differs, stop.** Another writer has been active. Re-derive every number in this plan and report the difference before writing.

- [ ] **Step 2: Write the entry**

Insert the text verbatim. Keep the blank line that separates it from G15's last paragraph, and leave one blank line between the entry's last line and the heading.

In `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, insert before:

```markdown
# Consolidated proposed-requirements index
```

this:

````markdown
## G16 — The key store was the only record of which key an identity held, and every verifier took its word

**Location.** §4.4, R4.16 (A16's R4.16 revised), R4.17–R4.19; §5.2 and this document's R5.20; §5.5,
R5.9, and this document's A17; §6.1, R6.2; §6.5, R6.19; §6.6, R6.46 and this document's R6.52;
§11.5 and this document's R11.29; §3.3, Table 4's first Spoofing row, its first Tampering row and
its Repudiation row; Appendix D's `events` and `agent_keys`; this document's G14 (R4.31, R4.32, its
fourth cost, and its "Resolution is unchanged") and G15 (its "Key transparency"). The code is the
enrollment use case and its binding reader in `src/Curia.Application/Credentials/`, the key store
in `src/Curia.Infrastructure/PostgresAgentKeyStore.cs`, the key set route in
`src/Curia.Api/ForumEndpoints.cs`, the two validators in `src/Curia.AuthN/`, and the reference
client's verifier in `src/Curia.Client/PostVerifier.cs`.
**Class:** one finding from reviewing what was built, at the seam between the Registrar's key store
and the event log; it carries two requirements and revises two. A third requirement closes a
residual the implementation plan's register recorded under D27, and a fourth gives readers the
check the first two make possible.
**Status:** proposed; not applied to the white paper.

**How it surfaced.** `curia-architect`, scoping the stage after G14 and G15, read what each of them
left: G14's fourth cost (a lost key row is bound again on its `kid` alone), its "Resolution is
unchanged", and G15's "Key transparency" — three statements that the key store alone decides which
key an identity holds. The claim was then executed on 2026-09-26 against a `git archive` of the
workspace at dc17a3c (main at 1dbe0ff, plus an unmerged SDK pin touching `global.json` alone),
through the real Forum over Postgres, with this entry's HTTP facts added and no production code
changed. Two clauses of R6.54 were settled after a pre-flight scan applied the implementation plan to
a fresh tree: its first cut read the author's binding after a post as failed, and its independent
reader answered could-not-be-checked for any `agent.enrolled`, whoever's it was. The entry's own
review then found four more, and the text below carries each: R4.34 was keyed on the key store's
registrations rather than on the log's enrollments; R4.31 (revised) claimed that rotation would need
no amendment to it; a re-presentation could still turn an identity's history into failures, through
R6.52's signature check rather than R6.54's; and R11.29 was left without a cross-reference.

### The finding

The event log recorded which `kid` an identity was enrolled with (`agent.enrolled`, R4.31) and never
which key. The key store held the key. Every path that honoured a key — ingest (R6.2), the token
endpoint (R5.20), and the key set every reader verifies with (R4.16) — read the store and nothing
else, and R6.52's first check verifies a post under a key that key set serves. So a key the store
held counted as the identity's key because the store held it.

Two kinds of row the store can hold under an enrolled identity were never bound by any enrollment:
a second key, as errata G14's hole added them and as any store written before G14 may still hold
them (R4.19 forbids deleting one, R4.32 changing it); and other bytes under the identity's own `kid`,
registered by a lost row's recovery (G14's fourth cost). The facts that now refuse both printed, on
the unchanged code:

```
a key the log never bound acted as https://agents.example/bound-16404ed9:
  token request 200, question 201 {"post_id":"01M0572TG068D3GQRVKBD6MSCG", …}
other bytes under victim-f4f953ce were answered 201, and their holder obtained the victim's token
```

The first row is a key the provisioning role wrote beside an enrolled identity's key, as the hole
wrote them: its holder obtained the identity's token and posted as it, and the key set published it
beside the identity's own. The second is an enrollment presenting a victim's `kid` with other bytes
after the victim's row was lost: registered, dated from the victim's enrollment, and the victim's
from then on.

**What a reader could not tell.** R6.52 asks a client to verify a post's signature against a key
valid at its `server_ts`, and the reference client and `curia-testis` both verify against the key set
the Forum serves. Whoever can write the key store — the Forum, its database, its backups — can add a
key under any identity and sign under it, and every reader then verifies the forgery. §6.5 promises a
reader "from cryptography alone" that "a specific accountable identity composed those exact bytes …
It does not need to trust the Forum's operators, its database, its TLS terminator, or its backups."
The Acta commits to every post; nothing committed to the key the post was verified under.

**Why nothing caught it.** Every test that asserted an identity's key was its own enrolled that
identity through the route, so the store never held a row the log had not been told about. The one
record that could contradict the store named a `kid` and no key, and nothing compared them.

### The requirements

**R4.34** An enrollment the event log records SHALL bind the key it enrolls: its `agent.enrolled`
entry SHALL be appended in the same append as an `agent.key-bound` entry, in that identity's stream,
carrying the identity, the key's `kid`, and the key itself as a public JWK in R4.28's forms, with the
`alg` and `kid` the Forum's key set publishes beside them and without the key set's `curia_`
members. A later act that adds a key to an identity (R4.18) SHALL bind that key the same way, in the
append that records the act. A lost row's recovery (R4.31 (revised)) records no enrollment and
appends nothing: it registers the key a binding already carries, or, for an identity enrolled before
R4.34, a key under the `kid` its enrollment names, which no binding carries (this entry's first
cost). The reason: the key store is where keys are resolved, and until this requirement nothing a
reader could check said which key an identity held, so a key counted as the identity's because the
store held it. The log is append-only under R11.6's grant and committed to by signed heads, so a key
it binds can be found by any reader (R6.54) and by the identity itself, and a key the store holds
and the log never bound can be told apart from one it did. The same append, because an enrollment
recorded without its binding would leave an identity bound by its `kid` alone, which is what this
entry exists to end. It is keyed on the log's enrollment and not on the store's registration because
the two are separate writes: a registration whose log append failed leaves a key nothing binds, until
the same request, sent again, records the enrollment and binds the key with it.

**R4.35** The Forum SHALL honour a key the key store holds — to verify a post (R6.2), to authenticate
a client assertion (R5.20), and to publish it in the agent's key set (R4.16) — only when the event
log binds that key to the same identity: an `agent.key-bound` entry of that identity naming the key's
`kid` and carrying exactly that key; or, for an identity enrolled before R4.34 and only while no
`agent.key-bound` entry of that identity names the `kid`, the identity's `agent.enrolled` entry
naming it. A key the store holds and the log does not so bind SHALL be refused by name, as a key the
log does not bind, after every refusal the store itself gives, and SHALL NOT be published. Each key
the key set publishes SHALL name the log index of the entry that binds it, unless the log cannot be
folded into its tree, when the key is published without one, and a reader cannot check its binding
(R6.54). The reason: rows the store holds and no enrollment bound cannot be removed (R4.19) or
repaired (R4.32), so the only place they can stop counting is where a key is read; and a reader
handed a key set needs to know where the log says each key came from, or it has only the key set's
word. The store's own refusals come first so that R5.20's refusal still does not tell a caller whose
a `kid` is.

**R4.31 (revised)** *(replaces R4.31 as G14 wrote it; its reason stands, extended below, and one
sentence of it corrected.)* An enrollment request that carries no re-authorization by the owner of
the identifier it names (R4.10, R4.18) SHALL register a key only for an identifier the key store
holds no key for, and only under a `kid` the store holds for no other identifier. A request
re-presenting a key the store already holds for its identifier — the same `kid`, algorithm and
public key — SHALL succeed and register nothing, unless the event log's clauses below refuse it, and
any other request the first sentence does not permit SHALL be refused by name. An identifier the
event log records no enrollment of, and for which the key store holds more than one key, SHALL be
refused by name, whatever key the request presents. An identifier the event log records as enrolled
SHALL, whatever the key store holds, be enrolled only with a key the event log binds to it (R4.35),
and SHALL gain no second `agent.enrolled` entry: a request presenting a `kid` under which the log
binds no key to the identifier SHALL be refused by name, even one the store holds for it; a request
presenting a bound `kid` whose binding carries a key (R4.34) SHALL be refused by name unless it
presents exactly that key; and an `agent.enrolled` entry that names no `kid` binds none. When the
key store holds no key for such an identifier, because it has lost its rows, a request presenting a
key the log binds SHALL be decided as a first enrollment is, and a key it registers SHALL be valid
from the instant the event log recorded that binding. A refused enrollment SHALL leave both the key
store and the event log unchanged; R4.14's record of every failed attempt is a separate enrollment
log, not built, and this clause does not forbid it. Deciding that an identifier holds no key, and
registering one for it, SHALL be a single act with respect to any concurrent enrollment of the same
identifier. The reason is G14's, with one sentence corrected and three added. G14's "It cannot check
the bytes it registers, because the event log binds the `kid` and not the key" now holds only for an
identifier enrolled before R4.34, for which G14's fourth cost stands: for one enrolled since, the log
carries the key, and a lost row's recovery registers that key and no other bytes. An identifier the
log never enrolled was enrolled before `agent.enrolled` existed, or its enrollment's log append
failed after the store's write. Since G14 the store registers a key only for an identifier holding
none, so the second holds one key, and more than one can be held only where G14's hole wrote them.
Nothing in the log says which of several is the identity's own, and binding whichever a request
presents would let anyone holding one row's public key — which the key set published until R4.35 —
make that key the identity's, refuse the identity its own (R4.35), and turn every post signed under
its own key into a failure of R6.52's signature check: the unilateral demotion R6.54's reading of a
late binding refuses. Such an identity stays unbound until a binding can rest on better evidence than
a request anyone can send, which is R4.18's recovery on its owner's re-authorization, and that waits
on R4.10. And every binding counts, not the first alone, so the key a rotation binds (R4.18) is
re-announced as the enrolled one is. The lost row's clause is written for the one binding an
identity holds today. With several, it would restore only the first bound key to arrive, dated from
its binding and open-ended, and refuse the rest, because the store then holds a key for the
identifier; and it would restore a key a rotation had retired or R4.19 had revoked. The stage that
builds R4.18 and R4.19 amends that clause. An enrollment that carries its owner's re-authorization
is R4.18's recovery, which waits on R4.10: this requirement does not govern it, and R4.32 still
does.

**R4.16 (rev. 2)** *(replaces the first sentence of R4.16 (revised); the rest stands as A16 wrote
it.)* The Registrar's key store, populated exclusively through enrollment (R4.11) and rotation
(R4.18), SHALL hold every agent public key the Forum honours, and the event log SHALL decide which
of the keys it holds the Forum honours, as R4.35 says: by its `agent.key-bound` entries (R4.34) and,
for an identity enrolled before R4.34, by its `agent.enrolled`. The reason: "sole authority" named
the one component that could be written without leaving a trace a reader could find. The store
remains the only place key material is resolved from, and no key is fetched from any URL.

**R5.21** The protected header of a client assertion, and of a DPoP proof, SHALL name the algorithm of
the key its signature is to be verified under — for an assertion the agent key R5.20 resolves, for a
proof its embedded `jwk` — and a header naming any other algorithm SHALL be refused by name before a
verifier is chosen. The reason: both validators chose the verifier by the header's `alg`, so a header
naming the other allowed algorithm handed a key to a verifier it is not a key of. The answer read as a
bad signature; before G15's key rule it was a 500 any caller could cause against any agent. R5.9 pins
the access token's own algorithm, and the Forum's detached-JWS verifier already refuses a post whose
header algorithm is not its key's.

**R6.54** A client that reports a served post as verified SHALL also have established from the log
that the key the post's signature verifies under is the key the log bound to its author before the
post: it SHALL find the entry that binds the post's `kid` — the one the author's key set names
(R4.35), or one it is handed — recompute that entry's leaf (R6.46) and verify its inclusion under the
same signed head as the post's own proof, require an `agent.key-bound` entry of the post's author
naming the post's `kid`, at a lower log index than the post, and verify the post's signature under
the key that entry carries rather than under the key the key set serves. An entry of the post's
author naming the post's `kid` that establishes no key for the post — the author's `agent.enrolled`,
which names the `kid` and carries no key (all an identity enrolled before R4.34 has; a reader cannot
tell when one was made, and reports every such entry so), or the author's binding at a log index not
lower than the post's — SHALL be reported as could not be checked, and never as failed; so SHALL a
key set that names no entry for the post's `kid`, and an entry, proof or signed head that cannot be
fetched. An entry that is not of the post's author and `kid`, whatever its type, an entry of them
that is neither `agent.enrolled` nor `agent.key-bound`, and an `agent.key-bound` entry before the
post that carries no usable key or a key the post does not verify under, SHALL be reported as
failed. R6.52's three outcomes, and its prohibition on collapsing them, govern this check, and a
post whose key's binding is not verified SHALL NOT be reported as verified. The reason: every check
before this one verified the signature under a key the Forum's key set served, which is the Forum's
word, and §6.5's reader "does not need to trust the Forum's operators, its database … or its
backups". A key the store holds and the log binds, and a post signed under it, are both committed to
by a head the operator signs: a key substituted in the store must be bound in the log to be honoured,
in the identity's own stream, where the identity and any monitor can find it. The Forum's key set only
says where to look: one that names no leaf makes the check impossible, one that names a leaf binding
something else makes it fail, and neither makes it pass. One that names the author's `agent.enrolled`
for a key R4.34 also bound makes it impossible too, since a reader cannot tell that enrollment from
one made before R4.34, so the most a lying key set gains there is could not be checked, never
verified. A binding after the post is the log's silence about the key the post was accepted under,
not a contradiction of it. A forger binds first, at no cost; a binding that lands after a history can
be an honest identity's, such as one enrolled before `agent.enrolled` existed, which gains its first
binding when a request re-presents the one key the store holds for it — a request anyone holding its
public key can send, since R4.11's proof of possession is not built. Reporting that as failed would
let any caller turn an identity's whole history into a failure.

### Editorial amendments this entry carries

| where | change |
|---|---|
| G14's fourth cost, "A lost key row is bound again on its `kid` alone" | Annotated. For an identifier enrolled since R4.34, the log carries its key, and R4.31 (revised) registers that key and no other. The cost stands for identifiers enrolled before R4.34, and for a `kid` a new identifier registers after the row is lost, which R4.32 still holds there. G14 named a leaf carrying the key's RFC 7638 thumbprint as the fix; R4.34's leaf carries the JWK instead, so a reader holds the key and not only a way to confirm one it already has (the stage's spec, Decision 3). It closes the first of the two gaps that cost names. |
| G14, "What this deliberately does not change", **Resolution is unchanged**; G15, the same section, **Key transparency** | Annotated. Superseded by R4.35 for every key the log binds or does not bind. "It belongs with rotation" was a scheduling judgement; binding came first because rotation's own keys need the leaf this entry defines. |
| §3.3, Table 4, the first Spoofing row | The vector gains "a key written into the store under A's identifier, by the hole G14 closed or by whoever can write the store". The control gains "a key honoured, and verified by a reader, only as the log binds it (R4.35, R6.54)". |
| §3.3, Table 4, the first Tampering row | The vector gains "a key substituted in the store and a post signed under it". The control gains R6.54. |
| §3.3, Table 4, the Repudiation row | "Owner-visible key lifecycle (§6.6)" is annotated: the lifecycle's first event, a key's binding, is a leaf of the Acta (R4.34); its later events wait on R4.18 and R4.19. |
| §5.5, the validation algorithm | Annotated beside A17's `typ` and `nbf`: the DPoP proof's header `alg` is compared with its embedded `jwk`'s before a verifier is chosen, as a client assertion's is with the resolved key's at §5.2's token endpoint (R5.21). |
| §6.6, R6.46 | Annotated. `agent.key-bound` is a new entry class under G9's one encoding. Nothing about the leaf computation moves (R15.1); `conformance/acta/key-bound-entry` pins the entry and binds `envelope/ed25519-minimal`'s key to that envelope's author, so a tree over it and `acta/content-entry`, the binding first, under a head over that tree, is a log from which R6.54's check succeeds. |
| This document's R6.52 | Cross-referenced. Its three checks are joined by R6.54's, and the overall verdict needs that one verified too. |
| This document's R11.29 | Cross-referenced. `curia_verify` performs R6.54's check beside R6.52's three and reports it distinctly. Its byte-identity rule binds the post's own entry. The key's binding entry cannot share the post's bytes, and is bound to the post instead by R6.54: its identity and `kid` compared with the post's, its proof held to the same signed head, and the post's signature verified under the key it carries. |
| §6.5, R6.19 | Annotated. The reference verifier offers R6.54's check over served documents alone: `curia-testis log author`, which takes the post's entry and proof, the key's binding entry and proof, a signed head and the log's key set, and reads no agent key set. |
| Appendix D, `events` | `agent.key-bound` joins the agent stream's entry types. Its payload is `{ agent_id, kid, jwk }`, the JWK being `{ kty, crv, alg, kid, x[, y] }`: R4.28's forms, with the `alg` and `kid` the key set publishes beside them, and none of the key set's `curia_` members. |
| The agents' key set route (Appendix E; `GET /v1/jwks?agent=` as built) | Each published key gains `curia_log_index`, the index of the leaf that binds it, beside the `curia_not_before` and `curia_not_after` it already carries. A key the log does not bind is not published. |
| `src/Curia.Api/Jwks.cs` | The two JWK renderers move into `Curia.Canon.Jws.PublicJwk`, which the enrollment's binding entry uses too, so the key published and the key bound are one computation. |

### What this costs

1. **An identity enrolled before R4.34 stays bound by its `kid` alone.** Its key is honoured, its lost
   row's recovery registers whatever bytes arrive first, as before, and R6.54 reports every post it
   signed as *could not be checked* and never as *verified*. A binding appended for it now would sit
   after every post it has made, and so establish nothing about them. No deployment is hosted.
2. **Keys the hole added stop working.** A key the store holds and no enrollment bound mints no token,
   signs no post, and is not published. Under an enrolled identity, whose own key is published, every
   post already signed under one therefore fails a reader's signature check; under an identity the log
   never enrolled, the fifth cost says what a reader reports. Those posts were made in another
   identity's name. An honest agent whose key was merged into another identity under the reference
   client's default identifier loses that key; it was never its identity's.
3. **The key set reads the log.** Serving an agent's keys reads that agent's stream and folds the log
   once for the leaf indices: the whole-log read and fold that every route serving a post already
   performs on every request, anonymous ones included, and every Acta route performs too. The
   anonymous key set joins that class and opens none. Resolving a key reads the stream too.
4. **An enrollment appends two entries,** and every count of a stream's entries moves by one.
5. **An identity the log never enrolled is bound only while the store holds one key for it.** Such an
   identity was enrolled before `agent.enrolled` existed, or its enrollment's log append failed after
   the store's write. A request re-presenting the one key the store holds for it succeeds and
   registers nothing, and, since the log then records its first `agent.enrolled`, R4.34 binds that
   key with it, as of that request. For a failed append that is the recovery, and there is no
   history. For an identity with history, the binding sits after all of it, and R6.54 reports that
   history as could not be checked, as it reports a pre-R4.34 identity's, and never as failed; and the
   binding says only that the key is the one the store held, not that it is the one the identity began
   with. Where the store holds more than one key for such an identity, as a store G14's hole reached
   may, R4.31 (revised) refuses every such request: the identity mints no token (G15), nothing binds
   any of its keys, and it has no path back until R4.18's recovery exists. Until it is bound its key
   set is empty, and readers disagree about its posts, which this entry does not settle: `curia_verify`
   reports them as could not be checked, because the Forum published no key for the author, while
   `curia read`, `curia_read`, `curia verify`'s own signature line and `curia-testis verify` report the
   post's key as missing, which they count as a failure. No deployment is hosted; a Forum that holds
   such identities is the owner's question (the stage's spec, §2.1).

### What this deliberately does not change

- **R15.1's frozen set.** No envelope, canonicalization rule or leaf computation moves. A new entry
  class is a payload decision under G9's one encoding, and the payload is new; `agent.enrolled`'s is
  unchanged.
- **No rotation, revocation or compromise.** R4.17–R4.19 and R6.26–R6.30 remain unbuilt; nothing
  appends a second `agent.key-bound` for an identity yet. R4.31 (revised) reads every binding, so
  re-announcing a key a rotation binds needs no amendment to it. Its lost row's clause does (R4.31
  (revised)'s reason), and so do R4.35 and R6.54, which know nothing of a retired or revoked key: each
  is the rotation stage's to amend.
- **No backfill.** No binding is appended for an identity whose `agent.enrolled` predates R4.34, by
  the Forum or by an operator tool. An identity with no `agent.enrolled` at all is enrolled, and
  bound, only as the fifth cost above describes.
- **Who may bind.** A binding is appended by the Forum, not signed by the identity: R4.11's proof of
  possession and R4.18's signed rotation are what would make it the identity's own statement.
  Whoever can append to the log can bind a key under any identity and sign under it, and R6.54 then
  reports the post verified. What changes is where that leaves the forgery: a leaf in the victim's
  own stream, under a head the operator signs, which the identity or a monitor can find and nobody
  can withdraw. It is made detectable, not prevented.
- **No monitor.** Nothing watches an identity's stream for a binding the identity did not make. A
  substituted key is now committed to under a signed head, in the identity's own stream, where it can
  be found; finding it is a monitor's work, and none exists.
- **R4.11's proof of possession, and an EdDSA point check.** A key nobody can sign with is bound as
  readily as any other. It harms only the identity that registered it until an identity can hold two
  keys, which is R4.18's stage.

### A note on the seam this sits on

G14 made the log bind the `kid` so that the store could lose a row without losing the identity. That
made the log the store's backup for one fact and left the store the only record of the other, and
each component was consistent with itself: the store held keys, the log held enrollments. The key a
post was verified under lived in the one of the two that nothing signs.

### Falsified before it was trusted

What can be falsified now is the entry itself: `tools/spec-checks/falsify-spec-checks.py` must go red
on all four of its checks with the entry in place. The probes the requirements need are owed. Each is
named here with the break that must turn it red, and the implementation plan's register records what
each printed.

- **R4.34.** Append the binding under another type. The enrollment facts, and the HTTP fact that
  follows a key set's index to its leaf, must go red.
- **R4.35, at the rule.** Let the resolver honour whatever the store resolves. The HTTP fact in which
  a key the provisioning role added mints a token and signs a question must go red.
- **R4.35, at each path.** Wire the token endpoint, and separately ingest, to the store directly. The
  same fact must go red at the token, and at the question.
- **R4.31 (revised).** Remove both material checks. A lost row's recovery presenting other bytes under
  the victim's `kid` must be registered.
- **R4.31 (revised), every binding.** Read the first binding alone. Re-announcing a key a second
  binding names must be refused.
- **R4.31 (revised), several stored keys.** Count another identifier's keys where the clause counts
  them. A request re-presenting the second key of an identifier the log never enrolled must enroll
  it, and bind that key.
- **R5.21.** Remove either pin. The fact whose assertion, or proof, names the other algorithm must read
  as a bad signature.
- **R6.54.** Verify the post under the key set's key and not the binding's, ignore the leaves' order,
  read a `kid`-only enrollment as a binding, report the author's binding after the post as failed,
  skip comparing the binding with the post's author and `kid`, or hold the binding's proof to its own
  root rather than the signed head's. The client's fact for each case, and `curia-testis`'s where it
  has the case, must go red; and dropping the check's line from `curia_verify`'s result must turn the
  adapter's fact red (R11.29).

````

- [ ] **Step 3: Index the six requirements**

In `curia-whitepaper-ERRATA-AND-ADDENDUM.md`, insert after:

```markdown
| R4.33 | An enrollment is refused by name, before either store is written, when its identifier begins with a prefix the Forum's writers mint aggregates under (`log:`, `flag:`, any later one) or names an aggregate holding events and no enrollment of it | G15 |
```

this:

```markdown
| R4.34 | Every `agent.enrolled` is appended in the same append as an `agent.key-bound` in that identity's stream, carrying the `kid` and the key as a public JWK (R4.28's forms, with the key set's `alg` and `kid`); a later act adding a key binds it in its own append; a lost row's recovery appends nothing | G16 |
| R4.35 | A stored key is honoured — for a post, a client assertion, the key set — only when the log binds it: an `agent.key-bound` of that identity carrying exactly that key, or, before R4.34 and while no `agent.key-bound` of that identity names the `kid`, the `agent.enrolled` naming it; otherwise refused by name after the store's own refusals, and not published; each published key names the index of its binding where the log folds | G16 |
| R4.31 (rev.) | An enrolled identifier is enrolled only with a key the log binds to it: an unbound `kid` is refused, even one the store holds; a bound `kid` whose binding carries a key admits only that key; every binding counts, not the first alone; a lost row's recovery registers the bound key, dated from its binding; an identifier the log never enrolled is refused while the store holds more than one key for it; the rest stands as G14 wrote it | G16 |
| R4.16 (rev. 2) | The store holds every agent key the Forum honours, and the log's bindings (R4.35) decide which of them it honours; no key is fetched from a URL | G16 |
| R5.21 | A client assertion's and a DPoP proof's header `alg` names the algorithm of the key it is verified under (the resolved agent key; the embedded `jwk`), and any other is refused by name before a verifier is chosen | G16 |
| R6.54 | A client reports a post verified only when the key it verifies under is the key an `agent.key-bound` entry of its author, proven under the same signed head at a lower index, carries, and the signature verifies under that key; the author's `agent.enrolled` (all a pre-R4.34 identity has), its binding at an index not lower than the post's, or no entry to check, is could not be checked and never failed; an entry of another author or `kid` fails; R6.52's three outcomes govern | G16 |
```

- [ ] **Step 4: Check the documents, and falsify the checker over the new entry**

```bash
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
```

Expected, as printed when this plan was build-checked:

```
spec-checks: clean
```

```
baseline           clean (exit 0)
entry under test   ## G16 — The key store was the only record of which key an identity held, and every verifier took its word

citation           red, named the cell: cites R11.997, which is defined in neither document
duplicate          red, named the cell: R11.16 is defined unqualified in both
index-orphan       red, named the cell: R4.16 is proposed in an entry but absent from the index
index-phantom      red, named the cell: R4.1 is listed in the index but no entry defines it

falsify: all 4 checks went red naming their cell; working tree untouched
```

The index-orphan line must name **R4.16**: the falsifier removes the newest entry's lowest-numbered requirement from the index, and G16's is R4.16 (rev. 2). If it names another number, the entry was inserted in the wrong place.

On the fix round, also confirm the round changed nothing outside the entry and its rows: `git diff 38a21fa --stat` names `curia-whitepaper-ERRATA-AND-ADDENDUM.md` alone.

- [ ] **Step 5: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'Errata G16: keys bound in the Acta (R4.34, R4.35, R4.31 rev., R4.16 rev. 2, R5.21, R6.54)\n\nThe key store was the only record of which key an identity held, and every\npath that honoured a key, and every reader, took its word. Confirmed by\nexecution: a key row no enrollment bound minted a token and signed a question\nas the identity it was filed under.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

On the fix round, commit with:

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'Errata G16: amended after its review\n\nR4.34 is keyed on the enrollment the log records, not on every key the store\nregisters. R4.31 rev. refuses an identifier the log never enrolled while the\nstore holds several keys for it, and says its lost row clause is rotation to\namend. R11.29 is cross-referenced, and R6.54 says what it reports for an\nenrollment leaf and for an entry it cannot fetch.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 3: One rendering of a public key

**Files:**
- Create: `src/Curia.Canon/Jws/PublicJwk.cs`, `tests/Curia.Canon.Sodium.Tests/PublicJwkTests.cs`
- Modify: `src/Curia.Api/Jwks.cs`, `src/Curia.Api/ActaEndpoints.cs`

**Interfaces:**
- Produces: `Curia.Canon.Jws.PublicJwk.Of(PublicKeyMaterial) : Result<JsonValue.Object>` — the JWK the key set publishes, members `kty, crv, alg, kid, x[, y]`; and `PublicJwk.SameKey(JsonValue.Object, JsonValue.Object) : bool` — canonical-byte equality. Task 4 writes the first into every binding; Task 5 compares with the second.
- Consumes: nothing new. `Curia.Canon` references no package (CS-6); `ECDsa` is BCL.

**Why in Canon.** `Curia.Application` must render the key it binds, and may reference only Domain, Canon and Domain.Primitives (CS-7). The key set, in `Curia.Api`, then renders through the same method, so "published" and "bound" cannot come apart. The rule for what is a key of each algorithm stays with the adapter that verifies with it (the enrollment stage's Decision 20); `PublicJwkTests` holds the renderer to that rule on every material `KeyMaterials` names, so a key the key set would omit is never bound either.

- [ ] **Step 1: Write the failing tests**

Create `tests/Curia.Canon.Sodium.Tests/PublicJwkTests.cs`:

```csharp
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Jws;
using Curia.Tests.Shared;
using NSec.Cryptography;
using Xunit;

namespace Curia.Canon.Sodium.Tests;

/// <summary>
/// R4.28 and R4.34 (errata G16): the public JWK a key is published as, and bound in the log as, is
/// RFC 8037's octet key pair for Ed25519 and RFC 7518's <c>EC</c> form for P-256, derived from the
/// RFCs' own example keys rather than from the renderer; and the renderer refuses exactly the
/// material the verifying adapter refuses, so a key the key set would omit is never bound either.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublicJwkTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("public jwk");

    private static string Canonical(PublicKeyMaterial key) =>
        PublicJwk.Of(key).Match(
            jwk => Encoding.UTF8.GetString(CanonicalJson.Canonicalize(jwk).Match(b => b.ToArray(), e => throw new InvalidOperationException(e.Type))),
            e => "refused " + e.Type);

    /// <summary>
    /// RFC 8037 Appendix A.1's private key, whose public half A.2 prints as
    /// <c>x = 11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo</c>. The public key is derived here from the
    /// private scalar by NSec, so the expectation is the RFC's and not a round trip through the
    /// renderer.
    /// </summary>
    [Fact]
    public void R4_28_AnEd25519KeyIsRenderedAsRfc8037sOctetKeyPair()
    {
        var d = Base64Url.DecodeFromChars("nWGxne_9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A");
        using var key = Key.Import(SignatureAlgorithm.Ed25519, d, KeyBlobFormat.RawPrivateKey);
        var raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        Assert.Equal(
            """{"alg":"EdDSA","crv":"Ed25519","kid":"rfc8037-a","kty":"OKP","x":"11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo"}""",
            Canonical(new PublicKeyMaterial("EdDSA", "rfc8037-a", raw)));
    }

    /// <summary>
    /// RFC 7515 Appendix A.3.1's P-256 key, stored as R4.28 stores an <c>ES256</c> key: its DER
    /// SubjectPublicKeyInfo. The coordinates come back out of the DER exactly as the RFC prints them.
    /// </summary>
    [Fact]
    public void R4_28_AP256KeyIsRenderedAsRfc7518sEcForm()
    {
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64Url.DecodeFromChars("f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU"),
                Y = Base64Url.DecodeFromChars("x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"),
            },
        });

        Assert.Equal(
            """{"alg":"ES256","crv":"P-256","kid":"rfc7515-a3","kty":"EC","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""",
            Canonical(new PublicKeyMaterial("ES256", "rfc7515-a3", ecdsa.ExportSubjectPublicKeyInfo())));
    }

    /// <summary>
    /// The renderer and the rule the verifying adapter owns give one answer on every material
    /// <c>KeyMaterials</c> names, under each algorithm it is asked about. The rows include both
    /// answers for both algorithms, so an agreement that held only because both sides refused
    /// everything would show as a row reading <c>False/False</c> where the positive rows read
    /// <c>True/True</c>.
    /// </summary>
    [Theory]
    [InlineData("ES256", "p256-spki", true)]
    [InlineData("ES256", "empty", false)]
    [InlineData("ES256", "three-zero-bytes", false)]
    [InlineData("ES256", "32-raw-bytes", false)]
    [InlineData("ES256", "rsa-2048-spki", false)]
    [InlineData("ES256", "p384-spki", false)]
    [InlineData("ES256", "p256-spki-and-a-trailing-byte", false)]
    [InlineData("ES256", "brainpoolP256r1-spki", false)]
    [InlineData("EdDSA", "32-raw-bytes", true)]
    [InlineData("EdDSA", "empty", false)]
    [InlineData("EdDSA", "31-bytes", false)]
    [InlineData("EdDSA", "33-bytes", false)]
    [InlineData("EdDSA", "p256-spki", false)]
    public void R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(string alg, string material, bool isKey)
    {
        var bytes = KeyMaterials.Build(material, Message).Material;
        var adapter = alg == "ES256" ? Es256Adapter.IsPublicKey(bytes) : Ed25519Adapter.IsPublicKey(bytes);
        var rendered = PublicJwk.Of(new PublicKeyMaterial(alg, "k", bytes)).IsOk;

        Assert.Equal($"adapter={isKey} rendered={isKey}", $"adapter={adapter} rendered={rendered}");
    }

    /// <summary>Two renderings of one key are the same key, and a different key under the same name is not.</summary>
    [Fact]
    public void R4_34_SameKeyComparesTheKeyNotTheReference()
    {
        var (p256, _) = KeyMaterials.Build("p256-spki", Message);
        var (other, _) = KeyMaterials.Build("p256-spki", Message);

        var first = PublicJwk.Of(new PublicKeyMaterial("ES256", "k", p256)).Match(j => j, e => throw new InvalidOperationException(e.Type));
        var again = PublicJwk.Of(new PublicKeyMaterial("ES256", "k", p256.ToArray())).Match(j => j, e => throw new InvalidOperationException(e.Type));
        var stranger = PublicJwk.Of(new PublicKeyMaterial("ES256", "k", other)).Match(j => j, e => throw new InvalidOperationException(e.Type));

        Assert.Equal("same=True different=False", $"same={PublicJwk.SameKey(first, again)} different={PublicJwk.SameKey(first, stranger)}");
    }
}
```

- [ ] **Step 2: Run them, and see them fail to compile**

```bash
dotnet test tests/Curia.Canon.Sodium.Tests -c Release --nologo 2>&1 | grep -E "error CS|Passed!|Failed!" | head -5
```

Expected: `error CS0103: The name 'PublicJwk' does not exist in the current context`.

- [ ] **Step 3: Write the renderer**

Create `src/Curia.Canon/Jws/PublicJwk.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Jws;

/// <summary>
/// A registered key as the public JWK the Forum publishes (R4.28): the one rendering that the key
/// set serves and that a key-binding entry carries into the log (R4.34, errata G16), so "published"
/// and "bound in the log" are one computation and cannot come apart.
///
/// <para><b>Members, in the order the key set has always served them:</b> <c>kty</c>, <c>crv</c>,
/// <c>alg</c>, <c>kid</c>, <c>x</c>, and for <c>ES256</c> <c>y</c>. RFC 8037 §2 gives Ed25519 the
/// octet-key-pair form with one coordinate; RFC 7518 §6.2.1 gives P-256 the <c>EC</c> form with two.
/// Order carries no meaning once a leaf is canonicalized (R6.46); it is kept so the key set's bytes
/// do not move.</para>
///
/// <para><b>What this does not decide.</b> Whether material is a key of its algorithm is the rule
/// the adapter that verifies with it owns (<c>Es256Adapter.IsPublicKey</c>,
/// <c>Ed25519Adapter.IsPublicKey</c>), and every caller that publishes or binds a key asks that rule
/// first. This method refuses what it cannot render -- anything but 32 bytes under <c>EdDSA</c>,
/// and anything but a whole DER SubjectPublicKeyInfo on the curve named P-256 under <c>ES256</c> --
/// and <c>PublicJwkTests</c> holds the two to the same answer on every material
/// <c>KeyMaterials</c> names.</para>
/// </summary>
public static class PublicJwk
{
    /// <summary>The slug of every refusal here.</summary>
    public const string NotRenderableType = "curia/canon/key-not-renderable";

    /// <summary>
    /// <paramref name="key"/> as the public JWK the key set publishes, or a refusal naming the
    /// algorithm. Never throws for material: a stored row that is not a key is answered, not failed.
    /// </summary>
    public static Result<JsonValue.Object> Of(PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.Alg switch
        {
            "EdDSA" => key.Public.Length == 32
                ? Result<JsonValue.Object>.Ok(Render("OKP", "Ed25519", key, key.Public.Span, null))
                : Result<JsonValue.Object>.Fail(NotRenderable(key.Alg)),
            "ES256" => Es256(key),
            _ => Result<JsonValue.Object>.Fail(NotRenderable(key.Alg)),
        };
    }

    /// <summary>
    /// Whether two public JWKs are the same key under the same name: their canonical forms (RFC 8785)
    /// are byte-identical. Both sides of every comparison in this solution come from <see cref="Of"/>
    /// or from a log entry <see cref="Of"/> wrote, so no member a publisher might add is in either.
    /// </summary>
    public static bool SameKey(JsonValue.Object left, JsonValue.Object right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return CanonicalJson.Canonicalize(left).TryGetValue(out var l, out _)
            && CanonicalJson.Canonicalize(right).TryGetValue(out var r, out _)
            && l.Span.SequenceEqual(r.Span);
    }

    /// <summary>
    /// The coordinates are recovered by importing the SubjectPublicKeyInfo rather than by slicing the
    /// DER by offset, which works until an encoder emits a legal variation and then silently yields a
    /// wrong key. The curve is judged by its OID, as the verifier judges it: brainpoolP256r1 and
    /// secp256k1 have 32-byte coordinates too.
    /// </summary>
    private static Result<JsonValue.Object> Es256(PublicKeyMaterial key)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(key.Public.Span, out var read);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            if (read != key.Public.Length
                || parameters.Curve is not { IsNamed: true } curve
                || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                return Result<JsonValue.Object>.Fail(NotRenderable(key.Alg));

            return Result<JsonValue.Object>.Ok(Render("EC", "P-256", key, parameters.Q.X!, parameters.Q.Y!));
        }
        catch (CryptographicException)
        {
            return Result<JsonValue.Object>.Fail(NotRenderable(key.Alg));
        }
        catch (PlatformNotSupportedException)
        {
            return Result<JsonValue.Object>.Fail(NotRenderable(key.Alg));
        }
    }

    private static JsonValue.Object Render(string kty, string crv, PublicKeyMaterial key, ReadOnlySpan<byte> x, byte[]? y)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("kty", new JsonValue.String(kty)),
            new("crv", new JsonValue.String(crv)),
            new("alg", new JsonValue.String(key.Alg)),
            new("kid", new JsonValue.String(key.Kid)),
            new("x", new JsonValue.String(Base64Url.EncodeToString(x))),
        };

        if (y is not null)
            members.Add(new("y", new JsonValue.String(Base64Url.EncodeToString(y))));

        return new JsonValue.Object([.. members]);
    }

    private static Error NotRenderable(string alg) => new(
        NotRenderableType,
        "The material is not a public key the Forum can publish under its algorithm",
        $"alg={alg}");
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test tests/Curia.Canon.Sodium.Tests -c Release --nologo 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `Passed!  - Failed:     0, Passed:    31, …  - Curia.Canon.Sodium.Tests.dll (net10.0)` — fifteen before, and sixteen here: two RFC facts, thirteen agreement rows and the comparison fact. The RFC facts pass on the first run, which is the point of deriving them from the RFCs: the renderer's output is held to a value the renderer did not produce. Case 18 in Task 9 is their red.

- [ ] **Step 5: Render the key set through it**

`Jwks.ForAgent` keeps its signature here; Task 5 changes it. `ActaEndpoints.ToObject` becomes internal so the key set can convert Canon's JSON as the Acta's routes do.

Replace the contents of `src/Curia.Api/Jwks.cs` with:

```csharp
using System.Text.Json.Nodes;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;

namespace Curia.Api;

/// <summary>
/// The JWKS the Forum serves.
///
/// <para><b>R4.16 rev. (errata A16) is the reason this exists at all:</b> "the Registrar's key
/// store is authoritative and the Forum serves JWKS; no runtime fetch of agent-hosted JWKS." The
/// original design had the Forum fetching an agent's own JWKS at verification time, which was
/// removed as an SSRF and availability surface. Serving instead of fetching means the Forum is the
/// one place a verifier asks, and there is no outbound request anywhere on the ingest path.</para>
///
/// <para><b>Shapes matter more than usual here</b>, because a second implementation has to consume
/// them. RFC 8037 §2 gives Ed25519 an octet-key-pair form (<c>kty: "OKP"</c>, <c>crv:
/// "Ed25519"</c>, single coordinate <c>x</c>); RFC 7518 §6.2.1 gives ES256 the two-coordinate
/// <c>EC</c> form. Reusing <c>EC</c> for an Ed25519 key produces JSON that looks plausible and is
/// wrong -- <c>curia-testis</c>'s own JWK module records that exact trap. Both forms are rendered by
/// <see cref="PublicJwk.Of"/>, which the log's key-binding entries use too (R4.34, errata G16), so
/// the key a reader is served and the key the log bound are one computation.</para>
/// </summary>
public static class Jwks
{
    /// <summary>
    /// Renders one agent's registered keys as an RFC 7517 <c>{"keys": [...]}</c> document, omitting
    /// any stored key it cannot publish (<see cref="CanPublish"/>).
    ///
    /// <para><b>Omitted, not failed.</b> Key rows written before the enrollment route checked a key's
    /// bytes stay in the store for good (R4.19 forbids the delete, R4.32 the repair). Rendering one
    /// threw, so the agent's key set answered 500 forever. Publishing one in a form it is not would
    /// be worse: <c>curia-testis</c> refuses a whole key set over one bad entry, so once an identity
    /// holds two keys (R4.17), one malformed entry would take the good key down with it. A malformed
    /// key in a JWKS is worse than an absent one: absent fails to resolve, malformed fails to verify,
    /// and the second looks like a signature problem. An agent whose only key is omitted gets an
    /// empty set, not a 404, which still means that the store holds no row.</para>
    /// </summary>
    public static JsonObject ForAgent(IReadOnlyList<RegisteredKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var array = new JsonArray();
        foreach (var registered in keys)
        {
            // Two refusals, one outcome: the rule the key's verifier owns, and the renderer the log's
            // key-binding entries use too (R4.34). PublicJwkTests holds them to the same answer on
            // every material KeyMaterials names, so the second omits nothing the first admits there.
            if (!CanPublish(registered.Key.Alg, registered.Key.Public.Span)) continue;
            if (!PublicJwk.Of(registered.Key).TryGetValue(out var jwk, out _)) continue;

            array.Add(Annotate(ActaEndpoints.ToObject(jwk!), registered));
        }

        return new JsonObject { ["keys"] = array };
    }

    /// <summary>
    /// R4.15 and R4.28: whether <paramref name="material"/> is a key of <paramref name="alg"/> in the
    /// form this key set publishes. The single switch from an algorithm to the rule its verifier owns
    /// (<see cref="Ed25519Adapter.IsPublicKey"/>, <see cref="Es256Adapter.IsPublicKey"/>), so a key is
    /// registered, published and verified under one predicate. An algorithm with no published shape
    /// here is not publishable.
    /// </summary>
    public static bool CanPublish(string alg, ReadOnlySpan<byte> material) => alg switch
    {
        "EdDSA" => Ed25519Adapter.IsPublicKey(material),
        "ES256" => Es256Adapter.IsPublicKey(material),
        _ => false,
    };

    /// <summary>
    /// Adds the validity window as non-standard members.
    ///
    /// <para>RFC 7517 defines no validity fields, so these are extensions -- and they are prefixed
    /// so nobody mistakes them for standard ones. They are here because R6.31 makes validity a
    /// function of a post's <c>server_ts</c>, and a consumer that cannot see the window can only
    /// ever ask "is this key valid now", which is the wrong question for any post older than the
    /// last key rotation.</para>
    /// </summary>
    private static JsonObject Annotate(JsonObject jwk, RegisteredKey registered)
    {
        jwk["curia_not_before"] = registered.NotBefore.ToString("O");
        if (registered.NotAfter is { } notAfter) jwk["curia_not_after"] = notAfter.ToString("O");
        return jwk;
    }
}
```

In `src/Curia.Api/ActaEndpoints.cs`, replace:

```csharp
    };

    private static JsonObject ToObject(CanonJson.JsonValue.Object o)
    {
        var node = new JsonObject();
```

with:

```csharp
    };

    /// <summary>Canon's JSON object as a System.Text.Json node, member order preserved. Also renders the agents' key set (<see cref="Jwks"/>).</summary>
    internal static JsonObject ToObject(CanonJson.JsonValue.Object o)
    {
        var node = new JsonObject();
```

- [ ] **Step 6: Hold the key set to its old bytes**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
export CURIA_TESTIS_BIN="$PWD/rust/curia-testis/target/debug/curia-testis"
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `0 Warning(s)`, and `Passed!  - Failed:     0, Passed:   215, …  - Curia.Api.Tests.dll (net10.0)`. The key set's bytes do not move: `EnrollmentBindingTests.R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment` compares a served key set with one served earlier byte for byte, `AssertServesOnlyTheVictimsKey` compares coordinates with the key's own, and `StoredKeyFormTests.R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish` holds the omissions.

- [ ] **Step 7: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'PublicJwk: the one rendering of a stored key as the JWK the key set publishes\n\nThe key set renders through it now, and the log binding of Task 4 will too, so\nthe key a reader is served and the key the log binds are one computation.\nRFC 8037 and RFC 7515 anchor the rendering; each adapter anchors what it refuses.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 4: An enrollment binds its key in the log (R4.34, R4.31 revised)

**Files:**
- Create: `tests/Curia.Application.Tests/TestKeys.cs`, `conformance/acta/key-bound-entry/` (five files, by script)
- Modify: `src/Curia.Application/Projections/AgentStandingProjection.cs`, `src/Curia.Application/Credentials/EnrollmentBinding.cs` (whole file), `src/Curia.Application/Credentials/EnrollAgent.cs`, `src/Curia.Application/Credentials/EnrollIdentity.cs`, `src/Curia.Api/ForumEndpoints.cs` (the enrollment route's 409s)
- Modify (tests): `tests/Curia.Api.Tests/EnrollmentBindingTests.cs`, `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, `tests/Curia.Application.Tests/Projections/SearchProjectorTests.cs`, `tests/Curia.Domain.Tests/Acta/LogLeafTests.cs`, `tests/Curia.Api.Tests/EnrollmentIdentifierTests.cs`, `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs`
- Modify (corpus): `conformance/index.json`, `conformance/README.md`, `rust/curia-testis/tests/vectors.rs`

**Interfaces:**
- Produces:
  - `AgentStandingProjector.KeyBoundType = "agent.key-bound"` and `JwkField = "jwk"`.
  - `KeyBinding(string Kid, JsonValue.Object? Jwk, DateTimeOffset BoundAt, string EventId)` with `Holds(PublicKeyMaterial)`; `EnrollmentBinding(DateTimeOffset EnrolledAt, ImmutableArray<KeyBinding> Keys)` with `Find(history, agentId)` and `For(kid)`. Task 5 reads both.
  - `EnrollAgent.RecordAsync(string agentId, PublicKeyMaterial key, …)` — the key, where it took the `kid`.
  - `EnrollmentErrors.KeysAmbiguous(agentId)`, slug `curia/enroll/keys-ambiguous`, answered 409 by the enrollment route.
- Consumes: `PublicJwk` (Task 3).

**The shape of the binding.** One entry per key, in the identity's own stream: `{ agent_id, kid, jwk }`, the JWK exactly as `PublicJwk.Of` renders it. It is a separate event rather than a member added to `agent.enrolled` so that one event type is the whole key history a reader needs: R4.18's rotation will append the same type for the key it binds, and no reader written against this stage needs to learn a second (the spec's Decision 4).

- [ ] **Step 1: Write the failing tests**

A key where a `kid` was:

Create `tests/Curia.Application.Tests/TestKeys.cs`:

```csharp
using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Application.Tests;

/// <summary>
/// A real P-256 key under a given <c>kid</c>, stored as R4.28 stores an <c>ES256</c> key: its DER
/// SubjectPublicKeyInfo. <c>EnrollAgent</c> records an enrollment's key as its public JWK (R4.34,
/// errata G16), so a test that records one needs material the renderer calls a key, and a fresh one
/// each call, so two calls under one <c>kid</c> are two different keys.
/// </summary>
internal static class TestKeys
{
    internal static PublicKeyMaterial Es256(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }
}
```

The use case's facts: seven new ones — R4.34's two, R4.31 (revised)'s refusal of other bytes at the use case and at the log's record, the pre-R4.34 identity bound by its `kid` alone, the seam the enrollment stage left (its spec's Decision 8), and an identifier the log never enrolled while the store holds several keys for it — and the counts of a stream's entries, which an enrollment now makes two:

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
```

with:

```csharp
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(agentId)), ct).ConfigureAwait(false));

    /// <summary>The <c>kid</c> each <c>agent.enrolled</c> in <paramref name="stream"/> names, in order.</summary>
    private static List<string> EnrolledKids(IReadOnlyList<AppendedEvent> stream) =>
```

with:

```csharp
        Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(agentId)), ct).ConfigureAwait(false));

    /// <summary>What a fresh enrollment appends, in order (R4.34): the record, and the key it binds.</summary>
    private static readonly string[] EnrollmentEntries = [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType];

    /// <summary>The type of each entry in <paramref name="stream"/>, in order.</summary>
    private static List<string> Types(IReadOnlyList<AppendedEvent> stream) => [.. stream.Select(e => e.Event.Type.Value)];

    /// <summary>
    /// An <c>agent.key-bound</c> entry for <paramref name="key"/>, appended to Alice's stream after
    /// whatever it holds -- the entry R4.18's rotation will append, written here by hand because no
    /// writer in this solution produces a second one yet.
    /// </summary>
    private static async Task BindAnotherKeyAsync(InMemoryEventStore events, PublicKeyMaterial key, CancellationToken ct)
    {
        var stream = await StreamAsync(events, Alice, ct).ConfigureAwait(false);
        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(
                Require(EventId.Create("rotation-shaped-binding-" + key.Kid)),
                Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(key.Kid)),
                    new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(key))),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>The <c>kid</c> each <c>agent.enrolled</c> in <paramref name="stream"/> names, in order.</summary>
    private static List<string> EnrolledKids(IReadOnlyList<AppendedEvent> stream) =>
```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        Assert.Equal(Start, again.EnrolledAt);
        Assert.Equal(Start, Assert.Single(await keys.KeysForAsync(Alice, ct)).NotBefore);
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

```

with:

```csharp
        Assert.Equal(Start, again.EnrolledAt);
        Assert.Equal(Start, Assert.Single(await keys.KeysForAsync(Alice, ct)).NotBefore);
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
    }

```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        Assert.Equal("alice-1", rebound.Key.Kid);
        Assert.Equal(Start, rebound.NotBefore);
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

```

with:

```csharp
        Assert.Equal("alice-1", rebound.Key.Kid);
        Assert.Equal(Start, rebound.NotBefore);
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
    }

```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        var unbound = NewKey("mallory-1");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, bound.Kid, ct));
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(bound, Start, null), new RegisteredKey(unbound, Start.AddHours(1), null)]);

```

with:

```csharp
        var unbound = NewKey("mallory-1");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, bound, ct));
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(bound, Start, null), new RegisteredKey(unbound, Start.AddHours(1), null)]);

```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);

        Require(await log.RecordAsync(Alice, "alice-1", ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await log.RecordAsync(Alice, "mallory-1", ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, "alice-1", ct)).WasAlreadyEnrolled);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

```

with:

```csharp
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);
        var alice = NewKey("alice-1");

        Require(await log.RecordAsync(Alice, alice, ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await log.RecordAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, alice, ct)).WasAlreadyEnrolled);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// The log's record on its own, for R4.31 rev.'s second clause (errata G16): under the <c>kid</c>
    /// it bound, it will not report success for other bytes, since the log now carries the key. The
    /// same key, from a fresh byte array, is still a re-announcement.
    /// </summary>
    [Fact]
    public async Task R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);
        var alice = NewKey("alice-1");

        Require(await log.RecordAsync(Alice, alice, ct));

        Assert.Equal("curia/keys/material-immutable", Refusal(await log.RecordAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, new PublicKeyMaterial(alice.Alg, alice.Kid, alice.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
    }

```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        var keys = new InMemoryAuthorKeyRegistry();
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await new EnrollAgent(events, clock).RecordAsync(Alice, "alice-1", ct)).Type);
        Assert.Empty(await keys.KeysForAsync(Alice, ct));
        Assert.Single(await StreamAsync(events, Alice, ct));
```

with:

```csharp
        var keys = new InMemoryAuthorKeyRegistry();
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await new EnrollAgent(events, clock).RecordAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Empty(await keys.KeysForAsync(Alice, ct));
        Assert.Single(await StreamAsync(events, Alice, ct));
```

In `tests/Curia.Application.Tests/Credentials/EnrollIdentityTests.cs`, replace:

```csharp
        var held = Assert.Single(await keys.KeysForAsync(Alice, ct));
        Assert.Equal([held.Key.Kid], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

```

with:

```csharp
        var held = Assert.Single(await keys.KeysForAsync(Alice, ct));
        Assert.Equal([held.Key.Kid], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// R4.34 (errata G16): a fresh enrollment appends the record and the key it binds, in that order,
    /// in one append, and the binding carries the key as RFC 7518's <c>EC</c> JWK. The coordinates
    /// are read out of the key's DER here, independently of the renderer the Forum uses.
    /// </summary>
    [Fact]
    public async Task R4_34_AnEnrollmentBindsItsKeyInTheLogBesideItsRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var key = NewKey("alice-1");

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, key, ct));

        var stream = await StreamAsync(events, Alice, ct);
        Assert.Equal(EnrollmentEntries, Types(stream));
        Assert.Equal(stream[0].ServerTimestamp, stream[1].ServerTimestamp);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.Public.Span, out _);
        var q = ecdsa.ExportParameters(includePrivateParameters: false).Q;
        var payload = (JsonValue.Object)stream[1].Event.Payload;
        var jwk = (JsonValue.Object)payload.Members.Single(m => m.Key == AgentStandingProjector.JwkField).Value;

        Assert.Equal(
            $"agent_id={Alice} kid=alice-1 jwk=alg:ES256,crv:P-256,kid:alice-1,kty:EC,x:{Base64Url.EncodeToString(q.X)},y:{Base64Url.EncodeToString(q.Y)}",
            $"agent_id={((JsonValue.String)payload.Members.Single(m => m.Key == AgentStandingProjector.AgentIdField).Value).Value}"
            + $" kid={((JsonValue.String)payload.Members.Single(m => m.Key == AgentStandingProjector.KeyIdField).Value).Value}"
            + " jwk=" + string.Join(",", jwk.Members.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}:{((JsonValue.String)m.Value).Value}")));
    }

    /// <summary>
    /// The conformance vector <c>acta/key-bound-entry</c> pins the shape of R4.34's entry, and this
    /// holds the writer to it: enrolling the vector's identity with <c>envelope/ed25519-minimal</c>'s
    /// key, under that fixture's <c>kid</c>, writes the vector's payload byte for byte once
    /// canonicalized. Without it the vector could pin a shape the Forum never writes (trap 1).
    /// </summary>
    [Fact]
    public async Task R4_34_AnEnrollmentWritesTheConformanceVectorsPayload()
    {
        const string Scriptor = "agent://curia.example/tuesdaycrowd/scriptor";
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(events, clock).RecordAsync(
            Scriptor,
            new PublicKeyMaterial("EdDSA", "conformance-ed25519-minimal", Base64Url.DecodeFromChars("HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8")),
            ct));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        var vector = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(await File.ReadAllBytesAsync(Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory),
            "conformance", "acta", "key-bound-entry", "input.json"), ct)));
        var expected = vector.Members.Single(m => m.Key == "payload").Value;

        var written = Assert.Single(await StreamAsync(events, Scriptor, ct), e => e.Event.Type.Value == AgentStandingProjector.KeyBoundType);
        Assert.Equal(
            Encoding.UTF8.GetString(Require(CanonicalJson.Canonicalize(expected)).ToArray()),
            Encoding.UTF8.GetString(Require(CanonicalJson.Canonicalize(written.Event.Payload)).ToArray()));
    }

    /// <summary>
    /// R4.31 rev. (errata G16), the residual errata G14's fourth cost named: the store has lost Alice's
    /// row, and a request presenting her bound <c>kid</c> with other bytes is refused by name, because
    /// the log now carries her key. Nothing is registered and nothing is appended. Her own key, over
    /// the same lost store, is then re-registered, so the refusal is about the bytes.
    /// </summary>
    [Fact]
    public async Task R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var key = NewKey("alice-1");

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, key, ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, lost, clock);

        Assert.Equal("curia/keys/material-immutable", Refusal(await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Empty(await lost.KeysForAsync(Alice, ct));
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));

        Assert.True(Require(await enroll.EnrollAsync(Alice, key, ct)).WasAlreadyEnrolled);
        Assert.Equal(Start, Assert.Single(await lost.KeysForAsync(Alice, ct)).NotBefore);
    }

    /// <summary>
    /// An identity enrolled before R4.34 has an <c>agent.enrolled</c> naming its <c>kid</c> and no
    /// <c>agent.key-bound</c>, and the log binds that <c>kid</c> alone: after a lost row, whatever
    /// bytes arrive under it are registered, as errata G14's fourth cost says, while any other
    /// <c>kid</c> is still refused. Pinned so the kid-only branch is a decision a test holds, not an
    /// accident of the code.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("enrolled-before-key-binding")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-1")),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, lost, clock);

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await enroll.EnrollAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.True(Require(await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct)).WasAlreadyEnrolled);
        Assert.Equal(Start, Assert.Single(await lost.KeysForAsync(Alice, ct)).NotBefore);
        Assert.Equal([AgentStandingProjector.EnrolledType], Types(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// The seam R4.31 rev. settles (errata G16): once the log binds a second key to an identity, as
    /// R4.18's rotation will, re-announcing that key is a re-announcement, exactly as re-announcing
    /// the first is, and writes nothing. A <c>kid</c> no entry binds is still refused. The second
    /// binding is appended by hand, since nothing produces one yet; the store holds both keys, as it
    /// will after a rotation.
    /// </summary>
    [Fact]
    public async Task R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var first = NewKey("alice-1");
        var second = NewKey("alice-2");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, first, ct));
        await BindAnotherKeyAsync(events, second, ct);
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(first, Start, null), new RegisteredKey(second, Start, null)]);
        var enroll = Enroll(events, keys, clock);

        Assert.True(Require(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(second.Alg, second.Kid, second.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.True(Require(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(first.Alg, first.Kid, first.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await enroll.EnrollAsync(Alice, NewKey("alice-3"), ct)).Type);
        Assert.Equal(
            [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType, AgentStandingProjector.KeyBoundType],
            Types(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// R4.31 rev. (errata G16, as its review amended it): an identifier the log never enrolled, as
    /// every one enrolled before <c>agent.enrolled</c> existed is, for which the store holds two keys --
    /// its own, and one errata G14's hole wrote beside it. Nothing in the log says which is its own, so
    /// a request presenting either is refused by name before the store is asked to register, and
    /// nothing is appended. Binding whichever arrived would let anyone holding the second key's public
    /// half make it the identity's key and turn its history into failures. With one stored key the
    /// same request enrolls the identifier and binds that key (errata G16's fifth cost), so the refusal
    /// is about the count.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var own = NewKey("alice-1");
        var hole = NewKey("hole-1");
        var several = new PreG14KeyStore(Alice, [new RegisteredKey(own, Start, null), new RegisteredKey(hole, Start.AddHours(1), null)]);
        var enroll = Enroll(events, several, clock);

        Assert.Equal("curia/enroll/keys-ambiguous", Refusal(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(hole.Alg, hole.Kid, hole.Public.ToArray()), ct)).Type);
        Assert.Equal("curia/enroll/keys-ambiguous", Refusal(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(own.Alg, own.Kid, own.Public.ToArray()), ct)).Type);
        Assert.Equal(0, several.Enrollments);
        Assert.Empty(await StreamAsync(events, Alice, ct));

        var one = new PreG14KeyStore(Alice, [new RegisteredKey(own, Start, null)]);
        Assert.False(Require(await Enroll(events, one, clock).EnrollAsync(Alice, new PublicKeyMaterial(own.Alg, own.Kid, own.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
    }

```

R4.31 (revised) over HTTP: after the victim's row is lost, other bytes under its own `kid` are refused by name, and its own recovery serves the key set it was served before the loss; and an identifier the log never enrolled, holding two stored keys, is refused whichever it presents. The damage is asserted first in each:

In `tests/Curia.Api.Tests/EnrollmentBindingTests.cs`, replace:

```csharp

    /// <summary>
    /// Where R4.31's one exception stops. The victim's key row is lost, and before the victim
    /// recovers, a fresh identifier enrolls the victim's <c>kid</c>, which the store no longer holds:
```

with:

```csharp

    /// <summary>
    /// R4.31 rev. (errata G16), the residual errata G14's fourth cost named. The victim's key row is
    /// lost, and a request presents the victim's own <c>kid</c> with other bytes. Before G16 it was
    /// registered, dated from the victim's enrollment, and its sender held the identity: a token, and
    /// every later post. The log now carries the victim's key (R4.34), so the request is refused by
    /// name and registers nothing, and the victim, re-presenting its own key, is served the key set it
    /// was served before the loss, byte for byte.
    /// </summary>
    [Fact]
    public async Task R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);
        var before = await JwksAsync(client, victim.Agent.AgentId, ct);

        await LoseKeyRowAsync(victim.Agent.AgentId, ct);

        var impostor = ForumAgent.Create(victim.Agent.AgentId, victim.Agent.Kid);
        using var refused = await impostor.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(refused, ct);
        var impostorToken = await TokenOrNullAsync(client, impostor, forum.Now, ct);

        Assert.True(
            refused.StatusCode == HttpStatusCode.Conflict && impostorToken is null,
            $"other bytes under {victim.Agent.Kid} were answered {(int)refused.StatusCode}, and their holder {(impostorToken is null ? "obtained no token" : "obtained the victim's token")}");
        Assert.Equal("curia/keys/material-immutable", type);
        Assert.Equal($"kid={victim.Agent.Kid}: nothing was registered. The key registered under a kid never changes (R4.32).", detail);

        using (var recovered = await victim.Agent.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);

        Assert.Equal(before.GetRawText(), (await JwksAsync(client, victim.Agent.AgentId, ct)).GetRawText());
    }

    /// <summary>
    /// R4.31 rev. (errata G16, as its review amended it), at the surface. An identifier the log never
    /// enrolled -- as every one enrolled before <c>agent.enrolled</c> existed is -- whose key store
    /// holds its own key and a second one beside it, as errata G14's hole wrote them. Before this
    /// clause, a request presenting the second key's public half enrolled the identifier and bound that
    /// key: its holder held the identity from then on, and the identity's own key was refused. The
    /// request is now refused by name, whichever key it presents, and nothing is recorded. The damage
    /// is asserted first. An identifier holding one such row is the control: the same request enrolls
    /// it, which is how an enrollment whose log append failed recovers.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var own = ForumAgent.Create($"https://agents.example/never-enrolled-{suffix}", $"own-{suffix}");
        var hole = ForumAgent.Create(own.AgentId, $"hole-{suffix}");
        await WriteKeyRowAsync(own, ct);
        await WriteKeyRowAsync(hole, ct);

        using var holePresented = await hole.EnrollAsync(client, ct);
        using var ownPresented = await own.EnrollAsync(client, ct);
        var (type, _) = await ProblemAsync(holePresented, ct);

        Assert.True(
            holePresented.StatusCode == HttpStatusCode.Conflict,
            $"presenting the second stored key of {own.AgentId} was answered {(int)holePresented.StatusCode}, and the identity's own key then {(int)ownPresented.StatusCode}");
        Assert.Equal("curia/enroll/keys-ambiguous", type);
        Assert.Equal("409 curia/enroll/keys-ambiguous", $"{(int)ownPresented.StatusCode} {(await ProblemAsync(ownPresented, ct)).Type}");
        Assert.Equal(0, await EnrollmentsRecordedAsync(own.AgentId, ct));

        var single = ForumAgent.Create($"https://agents.example/append-failed-{suffix}", $"single-{suffix}");
        await WriteKeyRowAsync(single, ct);
        using (var recovered = await single.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(1, await EnrollmentsRecordedAsync(single.AgentId, ct));
    }

    /// <summary>
    /// A key row holding <paramref name="agent"/>'s key under its identifier, written as the
    /// provisioning role: what errata G14's hole wrote beside an identity's own, or what a store's
    /// write leaves when the log's append after it fails.
    /// </summary>
    private async Task WriteKeyRowAsync(ForumAgent agent, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
            "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
            admin);
        insert.Parameters.AddWithValue("kid", agent.Kid);
        insert.Parameters.AddWithValue("agent", agent.AgentId);
        insert.Parameters.AddWithValue("key", agent.AssertionKey.ExportSubjectPublicKeyInfo());
        insert.Parameters.AddWithValue("from", forum.Now);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
    }

    /// <summary>
    /// Where R4.31's one exception stops. The victim's key row is lost, and before the victim
    /// recovers, a fresh identifier enrolls the victim's <c>kid</c>, which the store no longer holds:
```

The projector tests record enrollments with a key, and count the binding:

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Authorization;
```

with:

```csharp
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
    private const string Kid = "aurelia-1";
    private const string Other = "https://agents.example/other";
    private const string Owner = "owner:example";
    private const string Operator = "operator:reviewer";
```

with:

```csharp
    private const string Kid = "aurelia-1";
    private const string Other = "https://agents.example/other";

    /// <summary>The key <see cref="Agent"/> enrolls with, one instance, so a re-announcement presents the key the log bound.</summary>
    private static readonly PublicKeyMaterial AgentKey = TestKeys.Es256(Kid);
    private const string Owner = "owner:example";
    private const string Operator = "operator:reviewer";
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>
```

with:

```csharp
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>What an enrollment appends, in order (R4.34, errata G16): the record, and the key it binds.</summary>
    private static readonly string[] Enrollment = [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType];

    /// <summary>The type of each entry in <paramref name="log"/>, in order.</summary>
    private static List<string> Types(IReadOnlyList<AppendedEvent> log) => [.. log.Select(e => e.Event.Type.Value)];

    /// <summary>
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        InMemoryEventStore store, ManualTimeProvider clock, string agent, string kid, CancellationToken ct)
    {
        Require(await new EnrollAgent(store, clock).RecordAsync(agent, kid, ct).ConfigureAwait(false));
        Require(await AttestAsync(store, clock, agent, ct).ConfigureAwait(false));
    }
```

with:

```csharp
        InMemoryEventStore store, ManualTimeProvider clock, string agent, string kid, CancellationToken ct)
    {
        Require(await new EnrollAgent(store, clock).RecordAsync(agent, TestKeys.Es256(kid), ct).ConfigureAwait(false));
        Require(await AttestAsync(store, clock, agent, ct).ConfigureAwait(false));
    }
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var enroll = new EnrollAgent(store, clock);

        var recorded = Require(await enroll.RecordAsync(Agent, Kid, ct));

        Assert.Equal(Start, recorded.EnrolledAt);
```

with:

```csharp
        var enroll = new EnrollAgent(store, clock);

        var recorded = Require(await enroll.RecordAsync(Agent, AgentKey, ct));

        Assert.Equal(Start, recorded.EnrolledAt);
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));

        clock.Advance(TimeSpan.FromDays(8));
        var again = Require(await enroll.RecordAsync(Agent, Kid, ct));

        Assert.Equal(Start, again.EnrolledAt);
```

with:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, AgentKey, ct));

        clock.Advance(TimeSpan.FromDays(8));
        var again = Require(await enroll.RecordAsync(Agent, AgentKey, ct));

        Assert.Equal(Start, again.EnrolledAt);
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp

        var log = await LogAsync(store, ct);
        Assert.Single(log);

        var facts = Require(AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), Agent));
```

with:

```csharp

        var log = await LogAsync(store, ct);
        Assert.Equal(Enrollment, Types(log));

        var facts = Require(AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), Agent));
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));

        clock.Advance(TimeSpan.FromDays(1));
```

with:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, AgentKey, ct));

        clock.Advance(TimeSpan.FromDays(1));
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        Assert.Equal(OwnerVerificationMethod.Manual, attested.Method);

        var again = Require(await enroll.RecordAsync(Agent, Kid, ct));
        Assert.True(again.OwnerVerified);
        Assert.Equal(Start, again.EnrolledAt);

        var log = await LogAsync(store, ct);
        Assert.Equal(2, log.Count);

        var standing = AgentStandingProjector.Fold(log)[Agent];
```

with:

```csharp
        Assert.Equal(OwnerVerificationMethod.Manual, attested.Method);

        var again = Require(await enroll.RecordAsync(Agent, AgentKey, ct));
        Assert.True(again.OwnerVerified);
        Assert.Equal(Start, again.EnrolledAt);

        var log = await LogAsync(store, ct);
        Assert.Equal([.. Enrollment, AgentStandingProjector.OwnerAttestedType], Types(log));

        var standing = AgentStandingProjector.Fold(log)[Agent];
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ct));
        Require(await AttestAsync(store, clock, Agent, ct, method: OwnerVerificationMethod.Domain));

        var attestation = (await LogAsync(store, ct))[1];
        var payload = Assert.IsType<JsonValue.Object>(attestation.Event.Payload);
        var members = payload.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
```

with:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, AgentKey, ct));
        Require(await AttestAsync(store, clock, Agent, ct, method: OwnerVerificationMethod.Domain));

        var attestation = (await LogAsync(store, ct))[Enrollment.Length];
        var payload = Assert.IsType<JsonValue.Object>(attestation.Event.Payload);
        var members = payload.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ct));

        var refused = await AttestAsync(store, clock, Agent, ct, by: Agent);
```

with:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, AgentKey, ct));

        var refused = await AttestAsync(store, clock, Agent, ct, by: Agent);
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/self-attestation", error!.Type);
        Assert.Single(await LogAsync(store, ct));
    }

```

with:

```csharp
        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/self-attestation", error!.Type);
        Assert.Equal(Enrollment, Types(await LogAsync(store, ct)));
    }

```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ct));

        var refused = await AttestAsync(store, clock, Agent, ct, by: "operator:   ");
```

with:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, AgentKey, ct));

        var refused = await AttestAsync(store, clock, Agent, ct, by: "operator:   ");
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/blank-operator-name", error!.Type);
        Assert.Single(await LogAsync(store, ct));
    }

```

with:

```csharp
        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/blank-operator-name", error!.Type);
        Assert.Equal(Enrollment, Types(await LogAsync(store, ct)));
    }

```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp

        var aggregate = Require(AggregateId.Create(Agent));
        Require(await store.AppendAsync(aggregate, Require(AggregateVersion.From(2)),
            [new DomainEvent(
                Require(EventId.Create("stray-rehome")),
```

with:

```csharp

        var aggregate = Require(AggregateId.Create(Agent));
        Require(await store.AppendAsync(aggregate, Require(AggregateVersion.From(Enrollment.Length + 1)),
            [new DomainEvent(
                Require(EventId.Create("stray-rehome")),
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        Assert.False(standing.OwnerVerified);
        Assert.Equal(Owner, standing.OwnerId);
        Assert.Equal(3, (await LogAsync(store, ct)).Count);
    }

```

with:

```csharp
        Assert.False(standing.OwnerVerified);
        Assert.Equal(Owner, standing.OwnerId);
        Assert.Equal(
            [.. Enrollment, AgentStandingProjector.OwnerAttestedType, AgentStandingProjector.OwnerAttestedType],
            Types(await LogAsync(store, ct)));
    }

```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));
        Require(await enroll.RecordAsync(Other, "other-1", ct));

        await AcceptPostAsync(store, Agent, "question", "mine-1", ct);
```

with:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, AgentKey, ct));
        Require(await enroll.RecordAsync(Other, TestKeys.Es256("other-1"), ct));

        await AcceptPostAsync(store, Agent, "question", "mine-1", ct);
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        // A second agent, verified only on day twenty: there owner verification is binding, and no
        // amount of later reading moves the answer.
        Require(await enroll.RecordAsync(Other, "other-1", ct));
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Other, "question", $"other-{i}", ct);
```

with:

```csharp
        // A second agent, verified only on day twenty: there owner verification is binding, and no
        // amount of later reading moves the answer.
        Require(await enroll.RecordAsync(Other, TestKeys.Es256("other-1"), ct));
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Other, "question", $"other-{i}", ct);
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        clock.Advance(TimeSpan.FromDays(2));
```

with:

```csharp
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, AgentKey, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        clock.Advance(TimeSpan.FromDays(2));
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));

        Require(await store.AppendAsync(
```

with:

```csharp
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, AgentKey, ct));

        Require(await store.AppendAsync(
```

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        var reversed = (await LogAsync(store, ct)).Reverse().ToArray();
```

with:

```csharp
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, AgentKey, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        var reversed = (await LogAsync(store, ct)).Reverse().ToArray();
```

In `tests/Curia.Application.Tests/Projections/SearchProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Author, "alice-1", ct));
        Require(await new AttestOwner(store, clock).RecordAsync(
            Author,
```

with:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Author, TestKeys.Es256("alice-1"), ct));
        Require(await new AttestOwner(store, clock).RecordAsync(
            Author,
```

In `tests/Curia.Application.Tests/Projections/SearchProjectorTests.cs`, replace:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Author, "alice-1", ct));
        await AcceptAsync(store, "post-unowned", ct);

```

with:

```csharp
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Author, TestKeys.Es256("alice-1"), ct));
        await AcceptAsync(store, "post-unowned", ct);

```

- [ ] **Step 2: Run them, and see them fail to compile**

```bash
dotnet build tests/Curia.Application.Tests -c Release --nologo 2>&1 | grep -E "error CS" | sed -E 's/\[.*//' | sort -u | head -5
```

Expected: `error CS1503: Argument 2: cannot convert from 'Curia.Canon.Jws.PublicKeyMaterial' to 'string'` at the `RecordAsync` calls, and `error CS0117` for `KeyBoundType`.

- [ ] **Step 3: The event type**

In `src/Curia.Application/Projections/AgentStandingProjection.cs`, replace:

```csharp

    /// <summary>
    /// The payload member naming the key the enrollment registered -- the enrollment's binding,
    /// which <c>EnrollmentBinding</c> reads so that a re-enrollment is honoured only for this
    /// <c>kid</c> (R4.31, errata G14). Still not projected into standing: the Registrar's key store
    /// is authoritative for key <i>material</i> (R4.16 rev.), and what this member settles is which
    /// <c>kid</c> an identity began with, which the store can lose and the log cannot.
    /// </summary>
    public const string KeyIdField = "kid";

    /// <summary>
```

with:

```csharp

    /// <summary>
    /// The payload member naming a key's <c>kid</c>: on <see cref="EnrolledType"/>, the key the
    /// enrollment registered; on <see cref="KeyBoundType"/>, the key that entry binds.
    /// <c>EnrollmentBinding</c> reads both (R4.31 rev., R4.34; errata G14, G16). Not projected into
    /// standing: what these members settle is which keys an identity holds, which the key store can
    /// lose and the log cannot.
    /// </summary>
    public const string KeyIdField = "kid";

    /// <summary>
    /// The event <c>EnrollAgent</c> appends beside <see cref="EnrolledType"/>, in the same append:
    /// R4.34's binding of a key to its identity, carrying the key's public JWK as the key set publishes
    /// it (<see cref="JwkField"/>). Not a Table 6 transition, so the fold skips it, as it skips every
    /// type it does not model. Resolvers honour a stored key only when an entry of this type binds it,
    /// or, for an identity enrolled before R4.34, when its <see cref="EnrolledType"/> names the
    /// <c>kid</c> and no entry of this type does (R4.35).
    /// </summary>
    public const string KeyBoundType = "agent.key-bound";

    /// <summary>The payload member of <see cref="KeyBoundType"/> carrying the key as <c>PublicJwk.Of</c> rendered it.</summary>
    public const string JwkField = "jwk";

    /// <summary>
```

- [ ] **Step 4: Every binding an identity holds**

Replace the contents of `src/Curia.Application/Credentials/EnrollmentBinding.cs` with:

```csharp
using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;

namespace Curia.Application.Credentials;

/// <summary>
/// One key the log binds to an identity (R4.34, errata G16): its <c>kid</c>, the public JWK when the
/// binding carries one, when the log recorded it, and the event that did.
/// </summary>
/// <param name="Kid">The bound <c>kid</c>.</param>
/// <param name="Jwk">
/// The key as <see cref="PublicJwk.Of"/> rendered it, from an <c>agent.key-bound</c> entry; or
/// <see langword="null"/> for an identity enrolled before R4.34, whose <c>agent.enrolled</c> names the
/// <c>kid</c> and no key. Such a binding cannot tell two keys under one <c>kid</c> apart, and
/// <see cref="Holds"/> says so by answering on the <c>kid</c> alone.
/// </param>
/// <param name="BoundAt">The instant the log recorded the binding. R4.31 dates a key re-registered after a lost row from here (R6.31).</param>
/// <param name="EventId">The entry that binds it: where a reader finds the binding in the log (R6.54).</param>
public sealed record KeyBinding(string Kid, JsonValue.Object? Jwk, DateTimeOffset BoundAt, string EventId)
{
    /// <summary>
    /// Whether <paramref name="key"/> is the key this binding names: the same <c>kid</c>, and, where
    /// the binding carries the key, the same public key, so material with no public JWK is not it. A
    /// kid-only binding (<see cref="Jwk"/> null) answers on the <c>kid</c> alone, whatever the material.
    /// </summary>
    public bool Holds(PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!string.Equals(Kid, key.Kid, StringComparison.Ordinal)) return false;
        if (Jwk is null) return true;

        return PublicJwk.Of(key).TryGetValue(out var rendered, out _) && PublicJwk.SameKey(Jwk, rendered!);
    }
}

/// <summary>
/// What an identifier's enrollment bound, as the log records it (R4.31 rev., R4.34; errata G14, G16):
/// when the identity was enrolled, and every key the log binds to it.
///
/// <para><b>Two kinds of entry bind a key.</b> Since R4.34 an enrollment appends
/// <c>agent.key-bound</c> beside <c>agent.enrolled</c>, in the same append, carrying the key's public
/// JWK; that entry binds the key itself. An identity enrolled before R4.34 has only
/// <c>agent.enrolled</c>, which names the <c>kid</c>; that binds the <c>kid</c> alone, and only while
/// no <c>agent.key-bound</c> names the same <c>kid</c>.</para>
///
/// <para><b>Why the log and not the key store.</b> The store can lose rows, and a store written before
/// errata G14 can hold keys no enrollment bound. The log is append-only under R11.6's grant and
/// signed into heads, so it is the one record of which keys an identity holds that neither failure can
/// change.</para>
/// </summary>
/// <param name="EnrolledAt">The instant the log recorded the enrollment.</param>
/// <param name="Keys">
/// Every binding, in log order. An enrollment produces one; R4.18's rotation will append more, and
/// <see cref="For"/> already answers for each, so a re-announcement of a rotated key is decided as a
/// re-announcement of the first (R4.31 rev.).
/// </param>
public sealed record EnrollmentBinding(DateTimeOffset EnrolledAt, ImmutableArray<KeyBinding> Keys)
{
    /// <summary>The binding the log holds for <paramref name="agentId"/>, or <see langword="null"/> when it holds no enrollment.</summary>
    public static EnrollmentBinding? Find(IReadOnlyList<AppendedEvent> history, string agentId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        DateTimeOffset? enrolledAt = null;
        KeyBinding? legacy = null;
        var bound = ImmutableArray.CreateBuilder<KeyBinding>();

        foreach (var appended in history)
        {
            if (appended.Event.Payload is not JsonValue.Object payload) continue;
            if (!string.Equals(Text(payload, AgentStandingProjector.AgentIdField), agentId, StringComparison.Ordinal)) continue;

            var type = appended.Event.Type.Value;
            if (string.Equals(type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal) && enrolledAt is null)
            {
                enrolledAt = appended.ServerTimestamp.Value;
                if (Text(payload, AgentStandingProjector.KeyIdField) is { } kid)
                    legacy = new KeyBinding(kid, null, appended.ServerTimestamp.Value, appended.Event.Id.Value);
            }
            else if (string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
                && Text(payload, AgentStandingProjector.KeyIdField) is { } kid
                && Member(payload, AgentStandingProjector.JwkField) is JsonValue.Object jwk)
            {
                bound.Add(new KeyBinding(kid, jwk, appended.ServerTimestamp.Value, appended.Event.Id.Value));
            }
        }

        if (enrolledAt is not { } at) return null;

        // A kid-only binding stands only for a kid no key-binding entry names: once the log carries the
        // key, the kid alone binds nothing.
        if (legacy is not null && !bound.Any(b => string.Equals(b.Kid, legacy.Kid, StringComparison.Ordinal)))
            bound.Insert(0, legacy);

        return new EnrollmentBinding(at, bound.ToImmutable());
    }

    /// <summary>The binding for <paramref name="kid"/>, or <see langword="null"/> when the log binds no key under it to this identity.</summary>
    public KeyBinding? For(string kid) =>
        Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));

    private static string? Text(JsonValue.Object payload, string name) =>
        Member(payload, name) is JsonValue.String text ? text.Value : null;

    private static JsonValue? Member(JsonValue.Object payload, string name)
    {
        foreach (var member in payload.Members)
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
                return member.Value;
        return null;
    }
}
```

- [ ] **Step 5: The log's record appends the binding, and refuses other bytes under a bound `kid`**

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
```

with:

```csharp
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Credentials;
```

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp

    /// <summary>
    /// Records an enrollment, or -- when the log already holds one for <paramref name="keyId"/> --
    /// reports the standing the log holds and appends nothing. When the log holds one for another
    /// <c>kid</c>, refuses (<see cref="AuthorKeyErrors.AlreadyEnrolled"/>) and appends nothing
    /// (R4.31). This is the log's half of enrollment; <see cref="EnrollIdentity"/> is the use case
    /// that puts the key store's half in front of it.
    /// </summary>
    /// <param name="agentId">The enrolling agent; also the aggregate its credential events land in.</param>
    /// <param name="keyId">The <c>kid</c> this enrollment registered, recorded on the event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<AgentEnrollment>> RecordAsync(
        string agentId,
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        // One aggregate per agent, for the reason IngestPipeline gives for one aggregate per post:
```

with:

```csharp

    /// <summary>
    /// Records an enrollment -- <c>agent.enrolled</c> and <c>agent.key-bound</c>, in one append
    /// (R4.34) -- or, when the log already binds <paramref name="key"/> to the identity, reports the
    /// standing the log holds and appends nothing. When the log binds no key under that <c>kid</c>,
    /// refuses (<see cref="AuthorKeyErrors.AlreadyEnrolled"/>); when it binds another key under it,
    /// refuses (<see cref="AuthorKeyErrors.MaterialImmutable"/>); and appends nothing (R4.31 rev.). This
    /// is the log's half of enrollment; <see cref="EnrollIdentity"/> is the use case that puts the key
    /// store's half in front of it.
    /// </summary>
    /// <param name="agentId">The enrolling agent; also the aggregate its credential events land in.</param>
    /// <param name="key">The key this enrollment registered: its <c>kid</c> on <c>agent.enrolled</c>, its public JWK on <c>agent.key-bound</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<AgentEnrollment>> RecordAsync(
        string agentId,
        PublicKeyMaterial key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        // One aggregate per agent, for the reason IngestPipeline gives for one aggregate per post:
```

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
            if (standing?.EnrolledAt is { } enrolledAt)
            {
                // R4.31 (errata G14): a re-announcement is honoured only for the kid this identity's
                // enrollment bound. Reporting "already enrolled" for any other kid is how a second
                // key under an enrolled identity used to be waved through as a success.
                if (EnrollmentBinding.Find(history!, agentId) is not { } binding || !binding.Binds(keyId))
                    return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

                return Result<AgentEnrollment>.Ok(
```

with:

```csharp
            if (standing?.EnrolledAt is { } enrolledAt)
            {
                // R4.31 rev. (errata G14, G16): a re-announcement is honoured only for a key the log
                // binds to this identity. Reporting "already enrolled" for any other kid is how a
                // second key under an enrolled identity used to be waved through as a success; and
                // for other bytes under a bound kid, how a lost row's recovery registered whatever it
                // was sent.
                if (EnrollmentBinding.Find(history!, agentId)?.For(key.Kid) is not { } bound)
                    return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

                if (!bound.Holds(key))
                    return Result<AgentEnrollment>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid));

                return Result<AgentEnrollment>.Ok(
```

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
            }

            var attempted = await AppendEnrollmentAsync(aggregate, actor, agentId, keyId, cancellationToken)
                .ConfigureAwait(false);

```

with:

```csharp
            }

            var attempted = await AppendEnrollmentAsync(aggregate, actor, agentId, key, cancellationToken)
                .ConfigureAwait(false);

```

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
        ActorId actor,
        string agentId,
        string keyId,
        CancellationToken cancellationToken)
    {
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<AgentEnrollment>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<AgentEnrollment>.Fail(eventIdError!);

        if (!EventType.Create(AgentStandingProjector.EnrolledType).TryGetValue(out var type, out var typeError))
            return Result<AgentEnrollment>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
            new(AgentStandingProjector.KeyIdField, new JsonValue.String(keyId)),

            // R4.21's "reason", carried on the event rather than supplied by whatever reads it
            // back. The trigger is Table 6's SuccessfulEnrollment and is implied by the event type;
            // this is the free-text elaboration TransitionReason exists for.
            new(AgentStandingProjector.ReasonField, new JsonValue.String(EnrollmentReason)),
        ]);

        // No server_ts in the payload. The store stamps the event, and R6.5 makes that the Forum's
        // observation; a second instant in the payload would be a claim that could disagree with it.
        var appended = await _events
            .AppendAsync(aggregate, AggregateVersion.New, [new DomainEvent(eventId, type, actor, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new AgentEnrollment(
            events[0].ServerTimestamp.Value, OwnerVerified: false, WasAlreadyEnrolled: false));
    }

```

with:

```csharp
        ActorId actor,
        string agentId,
        PublicKeyMaterial key,
        CancellationToken cancellationToken)
    {
        // R4.34: the key as the key set publishes it. The route admits only a key its verifier calls
        // a key, and that verifier's rule and this renderer agree (PublicJwkTests), so a refusal here
        // is a caller that skipped the route's check; it is reported, and nothing is appended.
        if (!PublicJwk.Of(key).TryGetValue(out var jwk, out var jwkError))
            return Result<AgentEnrollment>.Fail(jwkError!);

        if (!NewEvent(AgentStandingProjector.EnrolledType, actor, new JsonValue.Object(
            [
                new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                new(AgentStandingProjector.KeyIdField, new JsonValue.String(key.Kid)),

                // R4.21's "reason", carried on the event rather than supplied by whatever reads it
                // back. The trigger is Table 6's SuccessfulEnrollment and is implied by the event type;
                // this is the free-text elaboration TransitionReason exists for.
                new(AgentStandingProjector.ReasonField, new JsonValue.String(EnrollmentReason)),
            ])).TryGetValue(out var enrolled, out var enrolledError))
            return Result<AgentEnrollment>.Fail(enrolledError!);

        // R4.34: the key itself, bound to the identity in the log. Appended with the enrollment, in
        // the same call and so the same transaction, so no enrollment is ever recorded whose key the
        // log does not carry: a crash between two appends would leave an identity bound by kid alone.
        if (!NewEvent(AgentStandingProjector.KeyBoundType, actor, new JsonValue.Object(
            [
                new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                new(AgentStandingProjector.KeyIdField, new JsonValue.String(key.Kid)),
                new(AgentStandingProjector.JwkField, jwk!),
            ])).TryGetValue(out var bound, out var boundError))
            return Result<AgentEnrollment>.Fail(boundError!);

        // No server_ts in either payload. The store stamps the events, one instant for the append, and
        // R6.5 makes that the Forum's observation; a second instant in a payload would be a claim that
        // could disagree with it.
        var appended = await _events
            .AppendAsync(aggregate, AggregateVersion.New, [enrolled!, bound!], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new AgentEnrollment(
            events[0].ServerTimestamp.Value, OwnerVerified: false, WasAlreadyEnrolled: false));
    }

    /// <summary>One event of <paramref name="type"/>, under a fresh ULID.</summary>
    private Result<DomainEvent> NewEvent(string type, ActorId actor, JsonValue.Object payload)
    {
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<DomainEvent>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<DomainEvent>.Fail(eventIdError!);

        if (!EventType.Create(type).TryGetValue(out var eventType, out var typeError))
            return Result<DomainEvent>.Fail(typeError!);

        return Result<DomainEvent>.Ok(new DomainEvent(eventId, eventType, actor, payload));
    }

```

- [ ] **Step 6: The use case refuses other bytes before the store is asked, and dates a recovery from its binding**

In `src/Curia.Application/Credentials/EnrollIdentity.cs`, replace:

```csharp
/// <c>agent.enrolled</c>, so any other first event means the log keeps the aggregate for something
/// else, and the record could never be appended to it.</item>
/// <item><b>The log's binding.</b> An identity whose <c>agent.enrolled</c> names another <c>kid</c>
/// is refused before the key store is touched. This is what holds when the store has lost the
/// identity's rows, or was written before G14 and holds keys no enrollment bound.</item>
/// <item><b>The key store's enrollment</b> (<see cref="IAuthorKeyRegistry.EnrollAsync"/>): registers
/// only for an identity holding no key, atomically against a concurrent enrollment of the same
/// identity, and refuses other bytes under a held <c>kid</c>. For an identity the log has already
/// enrolled, "holding no key" means the store lost its row, and the bound <c>kid</c> is registered
/// again from the enrollment's instant -- R4.31's one exception -- unless another identity has
/// registered that <c>kid</c> since, which the store refuses as it refuses any <c>kid</c> held
/// elsewhere.</item>
/// <item><b>The log's record</b> (<see cref="EnrollAgent"/>): appended once, and re-read and
/// reported thereafter. A refusal at any earlier step appends nothing.</item>
```

with:

```csharp
/// <c>agent.enrolled</c>, so any other first event means the log keeps the aggregate for something
/// else, and the record could never be appended to it.</item>
/// <item><b>The log's binding.</b> An enrolled identity is refused, before the key store is touched,
/// any <c>kid</c> the log binds no key under for it, and, under a <c>kid</c> whose binding carries
/// the key (R4.34), any other key. This is what holds when the store has lost the identity's rows,
/// or was written before G14 and holds keys no enrollment bound (R4.31 rev., errata G16). An
/// identifier the log has not enrolled is refused, before the store is asked to register, while the
/// store holds more than one key for it: nothing in the log says which is its own.</item>
/// <item><b>The key store's enrollment</b> (<see cref="IAuthorKeyRegistry.EnrollAsync"/>): registers
/// only for an identity holding no key, atomically against a concurrent enrollment of the same
/// identity, and refuses other bytes under a held <c>kid</c>. For an identity the log has already
/// enrolled, "holding no key" means the store lost its row, and the bound key is registered again
/// from the instant the log bound it -- R4.31's one exception -- unless another identity has
/// registered that <c>kid</c> since, which the store refuses as it refuses any <c>kid</c> held
/// elsewhere. For an identity enrolled before R4.34 the log binds the <c>kid</c> alone, and the
/// exception registers whatever bytes arrive under it.</item>
/// <item><b>The log's record</b> (<see cref="EnrollAgent"/>): appended once, and re-read and
/// reported thereafter. A refusal at any earlier step appends nothing.</item>
```

In `src/Curia.Application/Credentials/EnrollIdentity.cs`, replace:

```csharp
            return Result<AgentEnrollment>.Fail(EnrollmentErrors.IdentifierReserved(agentId));

        if (binding is not null && !binding.Binds(key.Kid))
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

        // A fresh identity's key is valid from now. An enrolled one reaches the store only with its
        // bound kid, and the store registers it only if it lost the row: then the key is dated from
        // the enrollment the log records, or every post signed before the loss would fall outside its
        // window (R6.31). That instant can trail the lost row's start, a clock read taken before its
        // insert, and no post's server_ts precedes it: a post is admitted only once the log holds the
        // enrollment. When the store still holds the key, the date is not read.
        var notBefore = binding?.EnrolledAt ?? _clock.GetUtcNow();

        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
            return Result<AgentEnrollment>.Fail(EnrollmentErrors.IdentifierReserved(agentId));

        // R4.31 rev. (errata G16): an identifier the log records no enrollment of is bound by the first
        // request that re-presents a key the store holds for it, and only while the store holds one.
        // More than one is held only where errata G14's hole wrote them: since G14 the store registers
        // a key only for an identifier holding none, so no enrollment, concurrent or not, raises this
        // count past one, and the read needs no lock. Nothing in the log says which of several is the
        // identity's own. Binding the one a request presents would let anyone holding its public key
        // make it the identity's key, and refuse the identity its own (R4.35).
        if (binding is null && (await _keys.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false)).Count > 1)
            return Result<AgentEnrollment>.Fail(EnrollmentErrors.KeysAmbiguous(agentId));

        var bound = binding?.For(key.Kid);
        if (binding is not null && bound is null)
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

        // R4.31 rev. (errata G16): where the log carries the key, only that key. This is what refuses
        // other bytes under a bound kid after the store lost its row, which the store cannot see.
        if (bound is not null && !bound.Holds(key))
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid));

        // A fresh identity's key is valid from now. An enrolled one reaches the store only with a key
        // the log binds, and the store registers it only if it lost the row: then the key is dated
        // from the instant the log bound it, or every post signed before the loss would fall outside
        // its window (R6.31). That instant can trail the lost row's start, a clock read taken before
        // its insert, and no post's server_ts precedes it: a post is admitted only once the log holds
        // the binding. When the store still holds the key, the date is not read.
        var notBefore = bound?.BoundAt ?? _clock.GetUtcNow();

        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
```

In `src/Curia.Application/Credentials/EnrollIdentity.cs`, replace:

```csharp
            return Result<AgentEnrollment>.Fail(keyError!);

        return await _log.RecordAsync(agentId, key.Kid, cancellationToken).ConfigureAwait(false);
    }
}
```

with:

```csharp
            return Result<AgentEnrollment>.Fail(keyError!);

        return await _log.RecordAsync(agentId, key, cancellationToken).ConfigureAwait(false);
    }
}
```

The refusal of several stored keys, and the route's answer for it, a 409 as for every refusal of the identity or the key:

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
    /// <summary>The slug of <see cref="IdentifierTooLong"/>.</summary>
    public const string IdentifierTooLongType = "curia/enroll/identifier-too-long";
```

with:

```csharp
    /// <summary>The slug of <see cref="IdentifierTooLong"/>.</summary>
    public const string IdentifierTooLongType = "curia/enroll/identifier-too-long";

    /// <summary>The slug of <see cref="KeysAmbiguous"/>, matched by the route's 409 mapping.</summary>
    public const string KeysAmbiguousType = "curia/enroll/keys-ambiguous";
```

In `src/Curia.Application/Credentials/EnrollAgent.cs`, replace:

```csharp
        $"agent={agentId}: nothing was registered. The event log keeps this identifier for its own records; an agent needs an identifier of its own.");
```

with:

```csharp
        $"agent={agentId}: nothing was registered. The event log keeps this identifier for its own records; an agent needs an identifier of its own.");

    /// <summary>
    /// R4.31 rev. (errata G16): the event log records no enrollment of the identifier, and the key
    /// store holds more than one key for it, so nothing says which is its own and no enrollment
    /// request can choose. Names the identifier; never a key, nor how many the store holds.
    /// </summary>
    public static Error KeysAmbiguous(string agentId) => new(
        KeysAmbiguousType,
        "The key store holds several keys for that agent, and the event log binds none of them",
        $"agent={agentId}: nothing was registered or recorded. The event log records no enrollment of this identifier, so nothing says which of the keys the store holds for it is its own, and an enrollment request cannot choose one (R4.31). A new identity needs an agent identifier of its own.");
```

In `src/Curia.Api/ForumEndpoints.cs`, replace:

```csharp
                    or EnrollmentErrors.IdentifierReservedType
                ? Results.Conflict(new Problem(enrollError.Type, enrollError.Title, enrollError.Detail))
```

with:

```csharp
                    or EnrollmentErrors.IdentifierReservedType
                    or EnrollmentErrors.KeysAmbiguousType
                ? Results.Conflict(new Problem(enrollError.Type, enrollError.Title, enrollError.Detail))
```

- [ ] **Step 7: The conformance vector**

Its values come from python3's `json` and `hashlib`, outside both implementations, as `flag-committed-entry`'s did. It binds `envelope/ed25519-minimal`'s key to that envelope's author, an hour before `content-entry`'s `server_ts`, so a tree over the two, the binding first, under a head over that tree, is a log from which Task 7's `log author` succeeds with no agent key set. Run, from the repository root:

```bash
# plan-apply: run
set -e
python3 - <<'EOF'
import hashlib, json, pathlib
d = pathlib.Path("conformance/acta/key-bound-entry")
d.mkdir(parents=True, exist_ok=False)
agent = "agent://curia.example/tuesdaycrowd/scriptor"
entry = {
    "actor_id": agent,
    "aggregate_id": agent,
    "event_id": "01K4CQ1V9P3R5T7W9Y1A3C5E7G",
    "event_type": "agent.key-bound",
    "payload": {
        "agent_id": agent,
        "jwk": {"alg": "EdDSA", "crv": "Ed25519", "kid": "conformance-ed25519-minimal", "kty": "OKP",
                "x": "HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8"},
        "kid": "conformance-ed25519-minimal",
    },
    "server_ts": "2026-09-04T15:00:00.000000Z",
}
# RFC 8785 for an all-ASCII, number-free document: sorted members, no insignificant white space.
canonical = json.dumps(entry, separators=(",", ":"), sort_keys=True, ensure_ascii=False).encode()
(d / "input.json").write_bytes(canonical)
(d / "expected.canonical").write_bytes(canonical)
(d / "expected.digest").write_text(hashlib.sha256(canonical).hexdigest() + "\n")
(d / "expected.leaf").write_text(hashlib.sha256(b"\x00" + canonical).hexdigest() + "\n")
meta = {
    "profile": "acta-leaf",
    "requirement": "R4.34",
    "note": "An agent.key-bound event as an enrollment appends it beside agent.enrolled (R4.34, errata G16): the identity's key, bound in the log as the public JWK the key set publishes for it. The key is envelope/ed25519-minimal's, bound to that envelope's author an hour before content-entry's server_ts, so a tree over this vector and content-entry, the binding first, under a head over that tree, is a log from which R6.54's authorship check succeeds with no agent key set. Expected values were computed with python3's json and hashlib, outside both implementations; the leaf encoding is unchanged, and this vector is the evidence that both runners hash the new entry kind as they hash every other.",
}
(d / "meta.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n")
print(hashlib.sha256(canonical).hexdigest(), hashlib.sha256(b"\x00" + canonical).hexdigest())
EOF
```

Expected: `69eddb7810aff0d7870fb8dbdf3a117d728749d6157faa409ee7859eef3bb5e2 0fc2a06ccdf80bdb89597608d8b3edcc05488e4d2fc7f37b7b6304a0d09c42ca` — the digest and the leaf, as the build-check computed them.

Then the index, the README, the domain's list of vectors, and the Rust suite's hand count, each of which R6.45 or its own test requires:

In `conformance/index.json`, replace:

```json
        "acta-leaf"
      ],
      "count": 6
    },
    {
```

with:

```json
        "acta-leaf"
      ],
      "count": 7
    },
    {
```

In `conformance/README.md`, replace:

```markdown
RFC 8785 for an all-ASCII, number-free entry.

**Four runners consume this family, and the client's is why the pure/NFC vector earns its
place twice over.** `Curia.Canon.Tests` pins the computation; `Curia.Domain.Tests`'
```

with:

```markdown
RFC 8785 for an all-ASCII, number-free entry.

`key-bound-entry` pins the entry kind errata G16 introduced (R4.34): an identity's key, bound in
the log as the public JWK the key set publishes, on the identity's own aggregate. It binds
`envelope/ed25519-minimal`'s key to that envelope's author an hour before `content-entry`'s
`server_ts`, so a tree over the two, the binding first, under a head over that tree, is a log from
which R6.54's authorship check succeeds with no agent key set -- `curia-testis`' `tests/log_author.rs`
builds such a tree from exactly these two, with a filler leaf between them. The encoding is again
unchanged, and its values were computed the same way as `flag-committed-entry`'s.
`Curia.Application.Tests`' `EnrollIdentityTests.R4_34_AnEnrollmentWritesTheConformanceVectorsPayload`
holds the Forum's writer to the vector's payload, so the vector pins a shape the Forum writes.

**Four runners consume this family, and the client's is why the pure/NFC vector earns its
place twice over.** `Curia.Canon.Tests` pins the computation; `Curia.Domain.Tests`'
```

In `tests/Curia.Domain.Tests/Acta/LogLeafTests.cs`, replace:

```csharp
    [InlineData("nfd-payload-stays-nfd")]
    [InlineData("flag-committed-entry")]
    public void R6_46_AnEventRendersToTheConformanceVectorsLeafInputAndLeaf(string vector)
    {
```

with:

```csharp
    [InlineData("nfd-payload-stays-nfd")]
    [InlineData("flag-committed-entry")]
    [InlineData("key-bound-entry")]
    public void R6_46_AnEventRendersToTheConformanceVectorsLeafInputAndLeaf(string vector)
    {
```

In `rust/curia-testis/tests/vectors.rs`, replace:

```rust
/// The literals below are counted from the corpus directory, family by
/// family: admit-accept 5, admit-reject 14, c4 10, numbers 9, ordering 3,
/// unicode 6, envelope 8, merkle 9, acta 6 — 70 vector directories — plus
/// the 6 vendored `rfc8785/` file pairs, 76 in all. (Envelope grew from 6 to
/// 8 with errata G8's `vote-minimal` and `verification-contradicted`; merkle
/// and acta arrived with Phase 3 Stage 4 -- one merkle vector per tree size
/// 0–8, and five acta vectors pinning R6.46's leaf input. Acta grew from 5 to 6
/// with errata G13's `flag-committed-entry`.) (An earlier version of this comment
/// cited "50 vector directories, per CHARTER.md": a count that contradicted
/// the assertion beneath it, and a file that does not exist in this
```

with:

```rust
/// The literals below are counted from the corpus directory, family by
/// family: admit-accept 5, admit-reject 14, c4 10, numbers 9, ordering 3,
/// unicode 6, envelope 8, merkle 9, acta 7 — 71 vector directories — plus
/// the 6 vendored `rfc8785/` file pairs, 77 in all. (Envelope grew from 6 to
/// 8 with errata G8's `vote-minimal` and `verification-contradicted`; merkle
/// and acta arrived with Phase 3 Stage 4 -- one merkle vector per tree size
/// 0–8, and five acta vectors pinning R6.46's leaf input. Acta grew from 5 to 6
/// with errata G13's `flag-committed-entry`, and from 6 to 7 with errata G16's
/// `key-bound-entry`.) (An earlier version of this comment
/// cited "50 vector directories, per CHARTER.md": a count that contradicted
/// the assertion beneath it, and a file that does not exist in this
```

In `rust/curia-testis/tests/vectors.rs`, replace:

```rust
            + c.merkle.len()
            + c.acta.len(),
        70,
        "conformance/ vector directories (c4 + ordering + unicode + numbers \
         + admit-reject + admit-accept + envelope + merkle + acta)"
    );
    assert_eq!(c.total_len(), 76, "every vector in conformance/");

    // `Index::load` already refuses a family entry with no `count`, so
```

with:

```rust
            + c.merkle.len()
            + c.acta.len(),
        71,
        "conformance/ vector directories (c4 + ordering + unicode + numbers \
         + admit-reject + admit-accept + envelope + merkle + acta)"
    );
    assert_eq!(c.total_len(), 77, "every vector in conformance/");

    // `Index::load` already refuses a family entry with no `count`, so
```

In `rust/curia-testis/tests/vectors.rs`, replace:

```rust
        .sum();
    assert_eq!(
        declared, 76,
        "conformance/index.json's declared family counts"
    );
```

with:

```rust
        .sum();
    assert_eq!(
        declared, 77,
        "conformance/index.json's declared family counts"
    );
```

- [ ] **Step 8: Run the Application suite**

```bash
dotnet test tests/Curia.Application.Tests -c Release --nologo 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `Passed!  - Failed:     0, Passed:   286, …  - Curia.Application.Tests.dll (net10.0)`. Seven more than before: the seven facts of Step 1. `R4_34_AnEnrollmentWritesTheConformanceVectorsPayload` reads the vector Step 7 wrote.

- [ ] **Step 9: Two surfaces that count an enrollment's entries**

An enrollment of a 1,024-byte identifier writes two events now:

In `tests/Curia.Api.Tests/EnrollmentIdentifierTests.cs`, replace:

```csharp
        Assert.Equal(
            expected == "201"
                ? "201 enrolled; key rows 1, events 1"
                : $"400 curia/enroll/identifier-too-long field={field} bytes={bytes}: at most 1024 UTF-8 bytes; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
```

with:

```csharp
        Assert.Equal(
            expected == "201"
                ? "201 enrolled; key rows 1, events 2"
                : $"400 curia/enroll/identifier-too-long field={field} bytes={bytes}: at most 1024 UTF-8 bytes; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
```

And the flag privacy gate exempted a raiser's own `agent.enrolled` leaf from "served the raiser's identity"; its `agent.key-bound` leaf is the same kind of fact about the raiser, public by design and saying nothing about a flag. The exemption is exactly the raiser's own stream's two enrollment entries, not a widening to any agent event:

In `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs`, replace:

```csharp
    /// <summary>
    /// What one response discloses, judged leaf by leaf whichever route served it: the rationale or the
    /// salt anywhere; the raiser anywhere but its own <c>agent.enrolled</c> leaf; and the post inside
    /// any flag's own leaf.
    /// </summary>
    private static IEnumerable<string> Disclosures(List<Leaf> leaves, Flagged flagged)
```

with:

```csharp
    /// <summary>
    /// What one response discloses, judged leaf by leaf whichever route served it: the rationale or the
    /// salt anywhere; the raiser anywhere but its own enrollment's two leaves, <c>agent.enrolled</c> and,
    /// since errata G16, <c>agent.key-bound</c> (R4.34), each in the raiser's own stream and neither
    /// saying anything about a flag; and the post inside any flag's own leaf.
    /// </summary>
    private static IEnumerable<string> Disclosures(List<Leaf> leaves, Flagged flagged)
```

In `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs`, replace:

```csharp
                yield return "served the flag's salt";

            var ownEnrolment = string.Equals(leaf.EventType, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)
                && string.Equals(leaf.AggregateId, raiser, StringComparison.Ordinal);
            if (leaf.Mentions(raiser) && !ownEnrolment)
```

with:

```csharp
                yield return "served the flag's salt";

            var ownEnrolment = (string.Equals(leaf.EventType, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)
                    || string.Equals(leaf.EventType, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal))
                && string.Equals(leaf.AggregateId, raiser, StringComparison.Ordinal);
            if (leaf.Mentions(raiser) && !ownEnrolment)
```

- [ ] **Step 10: Run every suite this task touches**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Domain.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Canon.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Client.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked --test vectors 2>&1 | grep -E "^test result"
```

Expected: `0 Warning(s)`; Application 286, Domain 609, Canon 262, Client 204 (its `ActaLeafRecomputationTests` enumerates the directory and gains the new vector's row), Api 217 (the lost-row fact and the several-keys fact); `test result: ok. 13 passed`.

- [ ] **Step 11: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'R4.34: an enrollment binds its key in the log, and R4.31 reads every binding\n\nEnrollAgent appends agent.key-bound, carrying the public JWK, in the same append\nas agent.enrolled. A lost row recovers only the key the log carries, and a key a\nsecond binding names is re-announced as the first is. An identifier the log\nnever enrolled is refused while the store holds several keys for it.\nconformance/acta gains key-bound-entry, pinned in both runners and held to the\nwriter.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 5: The Forum honours a key only as the log binds it (R4.35)

**Files:**
- Create: `src/Curia.Application/Credentials/LogBoundKeys.cs`, `src/Curia.Api/Adapters/LogBoundAgentKeyResolver.cs`, `tests/Curia.Application.Tests/Credentials/LogBoundKeysTests.cs`, `tests/Curia.Api.Tests/KeyBindingTests.cs`
- Modify: `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, `src/Curia.Api/Program.cs`, `src/Curia.Api/ForumEndpoints.cs`, `src/Curia.Api/Jwks.cs`
- Modify (tests): `tests/Curia.Api.Tests/ForumFixture.cs`, `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, `tests/Curia.Api.Tests/TokenSubjectBindingTests.cs`

**Interfaces:**
- Produces:
  - `AuthorKeyErrors.NotBoundByTheLog(agentId, kid)`, slug `curia/keys/not-bound-by-the-log`.
  - `LogBoundKeys : IAuthorKeyResolver` over the store's resolver, the store's registry and `IEventReader`, with `KeySetAsync(agentId) : Result<AgentKeySet>`; `BoundKey(RegisteredKey Key, KeyBinding Binding)`; `AgentKeySet(int Stored, IReadOnlyList<BoundKey> Bound)`.
  - `LogBoundAgentKeyResolver : IAgentKeyResolver`, forwarding to `LogBoundKeys` for the token endpoint.
  - `Jwks.ForAgent(IReadOnlyList<BoundKey>, Func<string, long?> logIndexOf)`; each published key gains `curia_log_index`.
  - `ForumFixture.EnrollBeforeKeyBindingAsync(agentId, kid, publicKey, ct)`: an identity as every enrollment before G16 left it.
- Consumes: `EnrollmentBinding`, `KeyBinding` (Task 4); `PublicJwk` (Task 3).

**Why a rule over the store and not a column in it.** The rows R4.35 stops honouring cannot be deleted (R4.19) or changed (R4.32), and db/0005 grants no UPDATE outside the window; and the store is asked before the log is written (the enrollment stage's Decision 7), so a column set at insert would be set before the binding exists. So the rule sits where keys are read, once, and every path reads through it (the spec's Decision 6). The store is asked first, so every refusal it gave before is unchanged — R5.20's included.

- [ ] **Step 1: Write the failing tests — the rule**

Create `tests/Curia.Application.Tests/Credentials/LogBoundKeysTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.35 (errata G16) at the rule: a key the store resolves is honoured only when the log binds it
/// to the same identity -- the key itself since R4.34, the <c>kid</c> alone before it -- and the
/// key set lists only such keys. The store here is a double that answers whatever it is told to
/// hold, as a store written before errata G14 can; the log is the in-memory event store, written by
/// the same <see cref="EnrollAgent"/> the Forum runs.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LogBoundKeysTests
{
    private const string Alice = "https://agents.example/alice";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly ServerTimestamp At = ServerTimestamp.At(Start.AddHours(1));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>
    /// A key store that holds exactly what it is given, for anyone, and resolves by agent and
    /// <c>kid</c> from each key's <c>NotBefore</c>; no row here has a <c>NotAfter</c>, so it closes no
    /// window. It never checks the log: that is the rule under test.
    /// </summary>
    private sealed class HeldKeys(params (string AgentId, RegisteredKey Key)[] rows) : IAuthorKeyResolver, IAuthorKeyRegistry
    {
        public Task<Result<PublicKeyMaterial>> ResolveAsync(string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
        {
            var row = rows.FirstOrDefault(r => r.AgentId == agentId && r.Key.Key.Kid == kid);
            return Task.FromResult(row.Key is { } held && at.Value >= held.NotBefore
                ? Result<PublicKeyMaterial>.Ok(held.Key)
                : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent(agentId, kid)));
        }

        public Task<Result<RegisteredKey>> EnrollAsync(string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("R4.35 registers nothing");

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RegisteredKey>>([.. rows.Where(r => r.AgentId == agentId).Select(r => r.Key)]);
    }

    private static async Task<(LogBoundKeys Keys, InMemoryEventStore Events)> EnrolledAsync(PublicKeyMaterial enrolled, params (string AgentId, RegisteredKey Key)[] held)
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, enrolled, CancellationToken.None).ConfigureAwait(false));
        var store = new HeldKeys(held);
        return (new LogBoundKeys(store, store, events), events);
    }

    private static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    [Fact]
    public async Task R4_35_AKeyTheLogBindsResolves()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));

        Assert.True((await keys.ResolveAsync(Alice, "alice-1", At, TestContext.Current.CancellationToken)).IsOk);
    }

    /// <summary>A second key the store holds for Alice, as errata G14's hole left them: the log binds no key under its kid.</summary>
    [Fact]
    public async Task R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindIsRefusedByName()
    {
        var key = TestKeys.Es256("alice-1");
        var hole = TestKeys.Es256("hole-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)), (Alice, new RegisteredKey(hole, Start, null)));

        var refused = await keys.ResolveAsync(Alice, "hole-1", At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-bound-by-the-log agent=https://agents.example/alice kid=hole-1", refused.Match(_ => "resolved", e => $"{e.Type} {e.Detail}"));
    }

    /// <summary>Other bytes under the bound kid, as a lost row's recovery registered them before R4.31 rev.: the log carries the key, and it is not this one.</summary>
    [Fact]
    public async Task R4_35_OtherBytesUnderTheBoundKidAreRefusedByName()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(TestKeys.Es256("alice-1"), Start, null)));

        var refused = await keys.ResolveAsync(Alice, "alice-1", At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-bound-by-the-log", refused.Match(_ => "resolved", e => e.Type));
    }

    /// <summary>
    /// An identity enrolled before R4.34: its <c>agent.enrolled</c> names the kid, no entry carries the
    /// key, and the key resolves on the kid alone; a second kid the store holds does not.
    /// </summary>
    [Fact]
    public async Task R4_35_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone()
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("enrolled-before-key-binding")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-1")),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            TestContext.Current.CancellationToken));
        var store = new HeldKeys((Alice, new RegisteredKey(TestKeys.Es256("alice-1"), Start, null)), (Alice, new RegisteredKey(TestKeys.Es256("hole-1"), Start, null)));
        var keys = new LogBoundKeys(store, store, events);

        Assert.Equal(
            "alice-1: resolved; hole-1: curia/keys/not-bound-by-the-log",
            $"alice-1: {(await keys.ResolveAsync(Alice, "alice-1", At, TestContext.Current.CancellationToken)).Match(_ => "resolved", e => e.Type)}; "
            + $"hole-1: {(await keys.ResolveAsync(Alice, "hole-1", At, TestContext.Current.CancellationToken)).Match(_ => "resolved", e => e.Type)}");
    }

    /// <summary>The store's own refusal is passed on unchanged, so R5.20's refusal still does not say whose a kid is.</summary>
    [Fact]
    public async Task R4_35_TheStoresRefusalIsPassedOnUnchanged()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));

        var refused = await keys.ResolveAsync(Alice, "nowhere-1", At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-registered-to-agent", refused.Match(_ => "resolved", e => e.Type));
    }

    /// <summary>
    /// The key set lists what the log binds and nothing else, each with its binding's event, and
    /// still says how many rows the store holds, so an identity whose every row is unbound is told
    /// apart from one the store has never heard of.
    /// </summary>
    [Fact]
    public async Task R4_35_TheKeySetListsOnlyTheKeysTheLogBinds()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, events) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)), (Alice, new RegisteredKey(TestKeys.Es256("hole-1"), Start, null)));

        var set = Require(await keys.KeySetAsync(Alice, TestContext.Current.CancellationToken));
        var bindingEvent = Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(Alice)), TestContext.Current.CancellationToken))
            .Single(e => e.Event.Type.Value == AgentStandingProjector.KeyBoundType).Event.Id.Value;

        Assert.Equal(
            $"stored=2 bound=[alice-1@{bindingEvent}]",
            $"stored={set.Stored} bound=[{string.Join(",", set.Bound.Select(b => $"{b.Key.Key.Kid}@{b.Binding.EventId}"))}]");
    }
}
```

```bash
dotnet build tests/Curia.Application.Tests -c Release --nologo 2>&1 | grep -E "error CS" | sed -E 's/\[.*//' | sort -u | head -3
```

Expected: `error CS0246: The type or namespace name 'LogBoundKeys' could not be found`.

- [ ] **Step 2: Write the failing tests — the surface**

A fixture helper for an identity enrolled before G16, through the host's own event store:

In `tests/Curia.Api.Tests/ForumFixture.cs`, replace:

```csharp
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Domain;
using Curia.Domain.Credentials;
```

with:

```csharp
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
```

In `tests/Curia.Api.Tests/ForumFixture.cs`, replace:

```csharp
    }

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(EnvVarName)
```

with:

```csharp
    }

    /// <summary>
    /// An identity as every enrollment before errata G16 left it: its key row, written as the
    /// provisioning role, and an <c>agent.enrolled</c> naming its <c>kid</c> and no key, appended
    /// through the host's own event store -- and no <c>agent.key-bound</c>. <c>EnrollAgent</c> no longer
    /// writes this shape, so it is built here, with exactly the members <c>EnrollAgent</c> wrote until
    /// G16: <c>agent_id</c>, <c>kid</c> and <c>reason</c>. Such an identity is bound by its <c>kid</c>
    /// alone (R4.35).
    /// </summary>
    internal async Task EnrollBeforeKeyBindingAsync(string agentId, string kid, byte[] publicKey, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
                "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
                admin);
            insert.Parameters.AddWithValue("kid", kid);
            insert.Parameters.AddWithValue("agent", agentId);
            insert.Parameters.AddWithValue("key", publicKey);
            insert.Parameters.AddWithValue("from", Now);
            if (await insert.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException($"the key row for {agentId} was not written");
        }

        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        Require(await store.AppendAsync(
            Require(AggregateId.Create(agentId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Require(new UlidGenerator(Clock).Next()).ToString())),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(agentId)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(kid)),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted: agent key registered with the Registrar")),
                ]))],
            ct));
    }

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(EnvVarName)
```

The facts: a key the provisioning role adds beside an enrolled identity's key, as the hole did (the damage first, then each refusal, then the owner's own key on both paths); each published key's leaf; a pre-G16 identity still honoured; and the key set answering every agent parameter without a 500, now that it reads the log:

Create `tests/Curia.Api.Tests/KeyBindingTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G16 at the surface: every key the Forum honours is a key the event log binds (R4.35), and
/// the key set names, for each key it publishes, the leaf that binds it (R6.54). Before G16 the
/// ingest path, the token endpoint and the key set read the key store alone, so a key the store held
/// and no enrollment had bound -- a row added through errata G14's hole, which R4.19 forbids deleting
/// -- signed posts and minted tokens as the identity it was filed under, and was served to every
/// reader as that identity's key.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyBindingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>What the token endpoint answers a stored key the log does not bind to the agent named.</summary>
    private const string NotBoundByTheLog =
        "{\"error\":\"invalid_client\",\"error_description\":\"The event log binds no such key to that agent\",\"detail\":\"curia/keys/not-bound-by-the-log\"}";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> KeySetAsync(HttpClient client, string agentId, CancellationToken ct) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);

    /// <summary>
    /// R4.35, errata G16's finding. The provisioning role writes a second key row for an enrolled
    /// identity, as errata G14's hole wrote them and as any store written before G14 may still hold
    /// them. Its holder asks for the identity's token with it, and the identity's own token submits a
    /// question signed under it. Before G16 the first was issued and the second accepted; each is now
    /// refused by name, and the key set does not publish the row. The damage is asserted first, so a
    /// regression's first red line says what was done in the identity's name. The identity's own key,
    /// which its enrollment bound, is the positive control, on both paths.
    /// </summary>
    [Fact]
    public async Task R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var owner = ForumAgent.Create($"https://agents.example/bound-{suffix}", $"bound-{suffix}");
        var (dpop, token) = await owner.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var hole = ForumAgent.Create(owner.AgentId, $"hole-{suffix}");
        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
                "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
                admin);
            insert.Parameters.AddWithValue("kid", hole.Kid);
            insert.Parameters.AddWithValue("agent", owner.AgentId);
            insert.Parameters.AddWithValue("key", hole.AssertionKey.ExportSubjectPublicKeyInfo());
            insert.Parameters.AddWithValue("from", forum.Now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        var (tokenStatus, tokenBody) = await DpopClient.For(hole, hole.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, owner.AgentId, ct);
        using var forged = await dpop.PostAsync(
            client, PostsUrl, token,
            hole.SignQuestion("board-" + suffix, "Signed under a key the log never bound.", "Hole " + suffix, forum.Now),
            forum.Now, ct);
        var forgedBody = await forged.Content.ReadAsStringAsync(ct);
        var keySet = (await KeySetAsync(client, owner.AgentId, ct)).GetRawText();

        Assert.True(
            tokenStatus != HttpStatusCode.OK && forged.StatusCode != HttpStatusCode.Created && !keySet.Contains(hole.Kid, StringComparison.Ordinal),
            $"a key the log never bound acted as {owner.AgentId}: token request {(int)tokenStatus}, question {(int)forged.StatusCode} {forgedBody}, key set {keySet}");
        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)tokenStatus} {tokenBody}");
        Assert.Equal(
            "401 curia/keys/not-bound-by-the-log",
            $"{(int)forged.StatusCode} {JsonNode.Parse(forgedBody)!["type"]!.GetValue<string>()}");

        using var own = await dpop.PostAsync(
            client, PostsUrl, token,
            owner.SignQuestion("board-" + suffix, "Signed under the key the log bound.", "Bound " + suffix, forum.Now),
            forum.Now, ct);
        Assert.True(own.StatusCode == HttpStatusCode.Created, "the owner's own key was refused too, so the refusals above prove nothing: " + await own.Content.ReadAsStringAsync(ct));
        Assert.NotNull(await DpopClient.For(owner, owner.AssertionKey).GetTokenAsync(client, TokenEndpoint, forum.Now, ct));
    }

    /// <summary>
    /// R6.54's Forum half: each key the key set publishes names the leaf that binds it, and that leaf,
    /// fetched from the log, is the identity's <c>agent.key-bound</c> entry carrying exactly the key
    /// published beside it. The comparison drops only the key set's own <c>curia_</c> members, which
    /// the log does not carry.
    /// </summary>
    [Fact]
    public async Task R6_54_EachPublishedKeyNamesTheLeafThatBindsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var agent = ForumAgent.Create($"https://agents.example/positioned-{suffix}", $"positioned-{suffix}");
        using (var enrolled = await agent.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var served = Assert.Single((await KeySetAsync(client, agent.AgentId, ct)).GetProperty("keys").EnumerateArray());
        Assert.True(
            served.TryGetProperty("curia_log_index", out var index) && index.ValueKind == JsonValueKind.Number,
            "the key set names no leaf for the key: " + served.GetRawText());

        var entry = (await client.GetFromJsonAsync<JsonElement>($"/v1/log/entries/{index.GetInt64()}", ct)).GetProperty("entry");
        var payload = entry.GetProperty("payload");

        var published = JsonNode.Parse(served.GetRawText())!.AsObject();
        foreach (var name in published.Select(m => m.Key).Where(k => k.StartsWith("curia_", StringComparison.Ordinal)).ToList())
            published.Remove(name);

        var carried = payload.TryGetProperty("jwk", out var jwk) ? JsonNode.Parse(jwk.GetRawText()) : null;

        Assert.Equal(
            $"agent.key-bound {agent.AgentId} {agent.AgentId} {agent.Kid} jwk-equal=True",
            $"{entry.GetProperty("event_type").GetString()} {entry.GetProperty("aggregate_id").GetString()} "
            + $"{payload.GetProperty("agent_id").GetString()} {payload.GetProperty("kid").GetString()} "
            + $"jwk-equal={(carried is null ? "absent" : JsonNode.DeepEquals(published, carried).ToString())}");
    }

    /// <summary>
    /// R4.35's other binding: an identity enrolled before errata G16 has an <c>agent.enrolled</c> that
    /// names its <c>kid</c> and no key-binding entry, and the log binds that <c>kid</c> alone. Its key
    /// still mints its token and signs its posts, and the key set publishes it naming the enrollment's
    /// leaf, where a reader finds a <c>kid</c> and no key (R6.54's "could not be checked").
    /// </summary>
    [Fact]
    public async Task R4_35_AnIdentityEnrolledBeforeKeyBindingIsHonouredByItsKidAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var agent = ForumAgent.Create($"https://agents.example/before-g16-{suffix}", $"before-g16-{suffix}");
        await forum.EnrollBeforeKeyBindingAsync(agent.AgentId, agent.Kid, agent.AssertionKey.ExportSubjectPublicKeyInfo(), ct);

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
        using var asked = await dpop.PostAsync(
            client, PostsUrl, token,
            agent.SignQuestion("board-" + suffix, "Enrolled before the log carried keys.", "Before " + suffix, forum.Now),
            forum.Now, ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, "an identity enrolled before G16 lost its key: " + await asked.Content.ReadAsStringAsync(ct));

        var served = Assert.Single((await KeySetAsync(client, agent.AgentId, ct)).GetProperty("keys").EnumerateArray());
        Assert.True(
            served.TryGetProperty("curia_log_index", out var index) && index.ValueKind == JsonValueKind.Number,
            "the key set names no leaf for the key: " + served.GetRawText());
        var entry = (await client.GetFromJsonAsync<JsonElement>($"/v1/log/entries/{index.GetInt64()}", ct)).GetProperty("entry");

        Assert.Equal(
            $"agent.enrolled {agent.Kid} jwk=absent",
            $"{entry.GetProperty("event_type").GetString()} {entry.GetProperty("payload").GetProperty("kid").GetString()} "
            + $"jwk={(entry.GetProperty("payload").TryGetProperty("jwk", out _) ? "present" : "absent")}");
    }

    /// <summary>
    /// The key set is anonymous, and since G16 it reads the event log as well as the key store. Every
    /// agent parameter is answered, and none with a 500: text Postgres <c>text</c> cannot hold, a
    /// noncharacter, an ill-formed UTF-8 sequence the host decodes, and an identifier longer than any
    /// the Forum stores. Each names an agent the store holds nothing for, so each is a 404.
    /// </summary>
    [Theory]
    [InlineData("%00")]
    [InlineData("https%3A%2F%2Fagents.example%2Fnul%00")]
    [InlineData("%EF%BF%BE")]
    [InlineData("%ED%A0%80")]
    [InlineData("long")]
    public async Task R4_35_TheKeySetAnswersEveryAgentWithoutA500(string encoded)
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = encoded == "long"
            ? Uri.EscapeDataString("https://agents.example/" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(1500)))
            : encoded;

        using var response = await forum.Client.GetAsync(new Uri($"/v1/jwks?agent={agent}", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        string type;
        try
        {
            type = JsonNode.Parse(body)?["type"]?.GetValue<string>() ?? "(no type)";
        }
        catch (JsonException)
        {
            type = body.Split('\n')[0][..Math.Min(body.Split('\n')[0].Length, 120)];
        }

        Assert.Equal("404 curia/keys/unknown-agent", $"{(int)response.StatusCode} {type}");
    }
}
```

The token theory's two replaced rows move to pre-G16 identities, the only ones whose replaced row still reaches the verifier under R4.35, and R4.35's own rows are added for identities enrolled since:

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
/// must omit what it cannot publish, and the token endpoint must answer such a key as it answers
/// any signature that does not verify.</para>
/// </summary>
[SuppressMessage(
```

with:

```csharp
/// must omit what it cannot publish, and the token endpoint must answer such a key as it answers
/// any signature that does not verify.</para>
///
/// <para><b>Since errata G16 the log carries an enrollment's key (R4.34)</b>, and a replaced row is
/// not the key it carries, so for an identity enrolled since then the row is refused before any
/// signature is checked (R4.35), which <see cref="R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken"/>
/// holds. The rows R4.15's rule still decides are those of identities enrolled before G16, whose log
/// binds the <c>kid</c> alone; the token theory's two replaced rows are made under such identities
/// (<see cref="ForumFixture.EnrollBeforeKeyBindingAsync"/>), so the verifier's rule is still what
/// refuses them.</para>
/// </summary>
[SuppressMessage(
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("stored key form");

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<ForumAgent> EnrolledAsync(string name, CancellationToken ct)
```

with:

```csharp
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("stored key form");

    /// <summary>What the token endpoint answers a stored key the log does not bind to the agent named (R4.35).</summary>
    private const string NotBoundByTheLog =
        "{\"error\":\"invalid_client\",\"error_description\":\"The event log binds no such key to that agent\",\"detail\":\"curia/keys/not-bound-by-the-log\"}";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>An identity enrolled as before errata G16: the log binds its <c>kid</c> alone, so a row replaced under it still reaches the verifier.</summary>
    private async Task<ForumAgent> EnrolledBeforeKeyBindingAsync(string name, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{name}", name);
        await forum.EnrollBeforeKeyBindingAsync(agent.AgentId, agent.Kid, agent.AssertionKey.ExportSubjectPublicKeyInfo(), ct);
        return agent;
    }

    private async Task<ForumAgent> EnrolledAsync(string name, CancellationToken ct)
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
    /// a stranger's key, which is a signature that does not verify.
    /// <list type="bullet">
    /// <item><c>es256-32-raw-bytes</c>: the row holds 32 raw bytes. It answered 500.</item>
    /// <item><c>es256-p384-spki</c>: the row holds a P-384 key, and the assertion is genuinely signed
    /// by it under <c>ES256</c>. It was issued a token.</item>
    /// <item><c>eddsa-header-over-an-es256-key</c>: an honest agent's row, and an assertion whose
    /// header says <c>EdDSA</c> over 64 zero bytes. Anyone could send it, naming any agent. It
```

with:

```csharp
    /// a stranger's key, which is a signature that does not verify.
    /// <list type="bullet">
    /// <item><c>es256-32-raw-bytes</c>: the row holds 32 raw bytes, under an identity enrolled before
    /// errata G16. It answered 500.</item>
    /// <item><c>es256-p384-spki</c>: the row holds a P-384 key, under an identity enrolled before errata
    /// G16, and the assertion is genuinely signed by it under <c>ES256</c>. It was issued a token.</item>
    /// <item><c>eddsa-header-over-an-es256-key</c>: an honest agent's row, and an assertion whose
    /// header says <c>EdDSA</c> over 64 zero bytes. Anyone could send it, naming any agent. It
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
        Assert.Equal($"401 {SignatureDoesNotVerify}", $"{(int)controlStatus} {controlBody}");

        var agent = await EnrolledAsync($"stored-{row}-{suffix}", ct);
        HttpStatusCode status;
        string body;
```

with:

```csharp
        Assert.Equal($"401 {SignatureDoesNotVerify}", $"{(int)controlStatus} {controlBody}");

        var agent = row is "es256-32-raw-bytes" or "es256-p384-spki"
            ? await EnrolledBeforeKeyBindingAsync($"stored-{row}-{suffix}", ct)
            : await EnrolledAsync($"stored-{row}-{suffix}", ct);
        HttpStatusCode status;
        string body;
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
        Assert.Equal($"{(int)controlStatus} {controlBody}", $"{(int)status} {line[..Math.Min(line.Length, 200)]}");
    }
}
```

with:

```csharp
        Assert.Equal($"{(int)controlStatus} {controlBody}", $"{(int)status} {line[..Math.Min(line.Length, 200)]}");
    }

    /// <summary>
    /// R4.35 (errata G16): the same replaced rows under an identity enrolled since G16, whose log entry
    /// carries its key (R4.34). The row is not the key the log bound, so the token endpoint refuses it
    /// before any signature is checked, and says why. Before R4.35 the P-384 row was issued a token and
    /// the raw-bytes row answered as a bad signature.
    /// </summary>
    [Theory]
    [InlineData("32-raw-bytes")]
    [InlineData("p384-spki")]
    public async Task R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(string material)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = await EnrolledAsync($"stored-bound-{material}-{Suffix()}", ct);

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var replaced = material == "p384-spki" ? p384.ExportSubjectPublicKeyInfo() : KeyMaterials.Build(material, Message).Material;
        await ReplaceAsync(agent.Kid, "ES256", replaced, ct);

        var (status, body) = await DpopClient.For(agent, material == "p384-spki" ? p384 : agent.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);

        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)status} {body}");
    }
}
```

A key row no enrollment recorded is now refused by R4.35 before the enrollment check that refused it:

In `tests/Curia.Api.Tests/TokenSubjectBindingTests.cs`, replace:

```csharp
    /// on the route still leaving one, which R4.33 forbids. Asserting the victim with that key is
    /// refused as the control is, and asserting the row's own identifier is refused because nothing in
    /// the log enrolled it. So such a row, written before the fix, authenticates nothing.
    /// </summary>
    [Fact]
```

with:

```csharp
    /// on the route still leaving one, which R4.33 forbids. Asserting the victim with that key is
    /// refused as the control is, and asserting the row's own identifier is refused because nothing in
    /// the log binds the key to it (R4.35, errata G16) -- a refusal that, before G16, came one step
    /// later, when the endpoint found no enrollment. So such a row, written before the fix,
    /// authenticates nothing.
    /// </summary>
    [Fact]
```

In `tests/Curia.Api.Tests/TokenSubjectBindingTests.cs`, replace:

```csharp
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.PostId, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, orphanStatus);
        Assert.Equal("That agent is not enrolled", JsonNode.Parse(orphanBody)!["error_description"]!.GetValue<string>());
    }

```

with:

```csharp
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.PostId, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, orphanStatus);
        var orphanRefusal = JsonNode.Parse(orphanBody)!;
        Assert.Equal(
            "curia/keys/not-bound-by-the-log",
            orphanRefusal["detail"]?.GetValue<string>() ?? orphanRefusal["error_description"]?.GetValue<string>());
    }

```

- [ ] **Step 3: Run the surface, and see it red**

```bash
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~KeyBindingTests|FullyQualifiedName~StoredKeyFormTests|FullyQualifiedName~TokenSubjectBindingTests" 2>&1 | grep -E "Passed!|Failed!|^\s+Failed " 
```

Expected red, as the build-check printed it on the unchanged code (abridged): `R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing` — `a key the log never bound acted as https://agents.example/bound-…: token request 200, question 201 {"post_id":…`; `R6_54_EachPublishedKeyNamesTheLeafThatBindsIt` — `the key set names no leaf for the key: {…"curia_not_before":…}`; `R4_35_AnIdentityEnrolledBeforeKeyBindingIsHonouredByItsKidAlone` — `the key set names no leaf for the key`; both `R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken` rows — `Expected: ···"error_description":"The event log binds no su"···`, `Actual: ···"Signature does not verify"···`; and `R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity` — `Expected: "curia/keys/not-bound-by-the-log"`, `Actual: "That agent is not enrolled"`. The five rows of `R4_35_TheKeySetAnswersEveryAgentWithoutA500` pass already, and should: they guard the route this task changes, and case 7 in Task 9 is their red.

- [ ] **Step 4: The refusal**

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp

/// <summary>
/// The key store's refusals, each by name. Three say why a key does not resolve:
/// <see cref="NotRegisteredToAgent"/>, <see cref="NotYetValid"/> and <see cref="NoLongerValid"/>.
/// They are distinct because they mean different things to an operator: a <c>kid</c> that is not the
/// agent's is a possible impersonation attempt; a key outside its window is ordinary lifecycle.
/// Collapsing them would make the first invisible inside the second's noise. Three say why an
/// enrollment registered nothing (R4.31, R4.32): <see cref="KidRegisteredToAnotherAgent"/>,
/// <see cref="AlreadyEnrolled"/> and <see cref="MaterialImmutable"/>.
/// </summary>
public static class AuthorKeyErrors
```

with:

```csharp

/// <summary>
/// The key store's refusals, each by name. Four say why a key does not resolve:
/// <see cref="NotRegisteredToAgent"/>, <see cref="NotYetValid"/>, <see cref="NoLongerValid"/> and,
/// since errata G16, <see cref="NotBoundByTheLog"/>. They are distinct because they mean different
/// things to an operator: a <c>kid</c> that is not the agent's is a possible impersonation attempt; a
/// key outside its window is ordinary lifecycle; a key the store holds and the log does not bind is a
/// store that has diverged from the log. Collapsing them would make the first and the last invisible
/// inside the second's noise. Three say why an enrollment registered nothing (R4.31, R4.32):
/// <see cref="KidRegisteredToAnotherAgent"/>, <see cref="AlreadyEnrolled"/> and
/// <see cref="MaterialImmutable"/>.
/// </summary>
public static class AuthorKeyErrors
```

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp
    public const string MaterialImmutableType = "curia/keys/material-immutable";

    public static Error NotRegisteredToAgent(string agentId, string kid) => new(
        "curia/keys/not-registered-to-agent",
```

with:

```csharp
    public const string MaterialImmutableType = "curia/keys/material-immutable";

    /// <summary>The slug of <see cref="NotBoundByTheLog"/>, for callers that match on it.</summary>
    public const string NotBoundByTheLogType = "curia/keys/not-bound-by-the-log";

    public static Error NotRegisteredToAgent(string agentId, string kid) => new(
        "curia/keys/not-registered-to-agent",
```

In `src/Curia.Application/Ports/IAuthorKeyRegistry.cs`, replace:

```csharp
        "The key was not yet valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    public static Error NoLongerValid(string kid, ServerTimestamp at) => new(
```

with:

```csharp
        "The key was not yet valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    /// <summary>
    /// R4.35 (errata G16): the store holds a key under this <c>kid</c> for this agent, and the event
    /// log binds no such key to it -- a row added through errata G14's hole, or bytes the store holds
    /// that are not the ones the log bound. Names the agent and the <c>kid</c>, never material.
    /// </summary>
    public static Error NotBoundByTheLog(string agentId, string kid) => new(
        NotBoundByTheLogType,
        "The event log binds no such key to that agent",
        $"agent={agentId} kid={kid}");

    public static Error NoLongerValid(string kid, ServerTimestamp at) => new(
```

- [ ] **Step 5: The rule**

Create `src/Curia.Application/Credentials/LogBoundKeys.cs`:

```csharp
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>A key the store holds and the log binds, with the binding (R4.35).</summary>
public sealed record BoundKey(RegisteredKey Key, KeyBinding Binding);

/// <summary>
/// One identity's keys as the Forum publishes them (R4.35): the keys the store holds that the log
/// binds, and how many the store holds at all, so a caller can still tell an identity the store has
/// never heard of from one whose every row the log refuses.
/// </summary>
public sealed record AgentKeySet(int Stored, IReadOnlyList<BoundKey> Bound);

/// <summary>
/// R4.35 (errata G16): the Forum honours a key the Registrar's store holds only when the event log
/// binds it to the same identity -- an <c>agent.key-bound</c> entry carrying exactly that key, or,
/// for an identity enrolled before R4.34, an <c>agent.enrolled</c> naming its <c>kid</c> -- for a
/// post's author (R6.2), for a token's client (R5.20), and in the key set it serves (R4.16 rev.).
///
/// <para><b>Why a rule over the store and not a column in it.</b> The store can hold rows the log
/// never bound: keys added through errata G14's hole before it closed, and bytes a lost row's recovery
/// accepted under a bound <c>kid</c>. Both are rows R4.19 forbids deleting and R4.32 forbids changing,
/// so the only place they can stop counting is where a key is read. The log is append-only under
/// R11.6's grant and committed to by signed heads, so a key it binds is a key any reader can find
/// bound there too (R6.54).</para>
///
/// <para><b>The store is asked first.</b> It refuses a <c>kid</c> registered to another agent, a key
/// outside its window at the instant asked (R6.31), and text no row can hold, before this reads the
/// log at all; what it answers is then held to the log's binding. So every refusal the store gave
/// before R4.35 is unchanged, byte for byte, and R5.20's refusal still does not say whose a
/// <c>kid</c> is.</para>
/// </summary>
public sealed class LogBoundKeys : IAuthorKeyResolver
{
    private readonly IAuthorKeyResolver _store;
    private readonly IAuthorKeyRegistry _registry;
    private readonly IEventReader _events;

    public LogBoundKeys(IAuthorKeyResolver store, IAuthorKeyRegistry registry, IEventReader events)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(events);

        _store = store;
        _registry = registry;
        _events = events;
    }

    /// <inheritdoc/>
    public async Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        var resolved = await _store.ResolveAsync(agentId, kid, at, cancellationToken).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var key, out _)) return resolved;

        var binding = await BindingAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!binding.TryGetValue(out var found, out var readError))
            return Result<PublicKeyMaterial>.Fail(readError!);

        return found?.For(kid) is { } bound && bound.Holds(key!)
            ? resolved
            : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotBoundByTheLog(agentId, kid));
    }

    /// <summary>The keys the store holds for <paramref name="agentId"/> that the log binds to it, each with its binding.</summary>
    public async Task<Result<AgentKeySet>> KeySetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var held = await _registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (held.Count == 0) return Result<AgentKeySet>.Ok(new AgentKeySet(0, []));

        var binding = await BindingAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!binding.TryGetValue(out var found, out var readError))
            return Result<AgentKeySet>.Fail(readError!);

        var bound = new List<BoundKey>();
        foreach (var registered in held)
        {
            if (found?.For(registered.Key.Kid) is { } keyBinding && keyBinding.Holds(registered.Key))
                bound.Add(new BoundKey(registered, keyBinding));
        }

        return Result<AgentKeySet>.Ok(new AgentKeySet(held.Count, bound));
    }

    /// <summary>What the log binds to <paramref name="agentId"/>, or <see langword="null"/> when it records no enrollment of it.</summary>
    private async Task<Result<EnrollmentBinding?>> BindingAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out _))
            return Result<EnrollmentBinding?>.Ok(null);

        var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        return read.Map(history => EnrollmentBinding.Find(history, agentId));
    }
}
```

- [ ] **Step 6: Every path reads through it**

The token endpoint's port is declared in `Curia.AuthN`, which cannot see `Curia.Application` (CS-7); the composition root joins them:

Create `src/Curia.Api/Adapters/LogBoundAgentKeyResolver.cs`:

```csharp
using Curia.Application.Credentials;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Api.Adapters;

/// <summary>
/// The token endpoint's key resolver: R4.35's rule (<see cref="LogBoundKeys"/>), offered through the
/// port <c>Curia.AuthN</c> declares. The two ports have one signature and cannot see each other
/// (CS-7), so this composition root joins them, and the token endpoint honours exactly the keys a
/// post's signature is verified under: no fewer, which would lock out an agent whose posts verify,
/// and no more, which is errata G16's finding.
/// </summary>
public sealed class LogBoundAgentKeyResolver(LogBoundKeys keys) : IAgentKeyResolver
{
    public Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default) =>
        keys.ResolveAsync(agentId, kid, at, cancellationToken);
}
```

In `src/Curia.Api/Program.cs`, replace:

```csharp
        // server_ts, so this adapter has no business knowing what time it is.
        builder.Services.AddSingleton(sp => sp.GetRequiredService<PostgresAdapters>().AgentKeys);
        builder.Services.AddSingleton<IAuthorKeyResolver>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());
        builder.Services.AddSingleton<IAuthorKeyRegistry>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());
        builder.Services.AddSingleton<IAgentKeyResolver>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());

        builder.Services.AddSingleton(sp => sp.GetRequiredService<PostgresAdapters>().EventStore);
```

with:

```csharp
        // server_ts, so this adapter has no business knowing what time it is.
        builder.Services.AddSingleton(sp => sp.GetRequiredService<PostgresAdapters>().AgentKeys);
        builder.Services.AddSingleton<IAuthorKeyRegistry>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());

        // R4.35 (errata G16): every key the Forum honours is a key the event log binds. Ingest, the
        // token endpoint and the key set all read through this one rule; the store alone answers
        // only enrollment, whose own rule (R4.31) reads the log's binding itself.
        builder.Services.AddSingleton(sp => new LogBoundKeys(
            sp.GetRequiredService<PostgresAgentKeyStore>(),
            sp.GetRequiredService<PostgresAgentKeyStore>(),
            sp.GetRequiredService<IEventReader>()));
        builder.Services.AddSingleton<IAuthorKeyResolver>(sp => sp.GetRequiredService<LogBoundKeys>());
        builder.Services.AddSingleton<IAgentKeyResolver>(sp => new LogBoundAgentKeyResolver(sp.GetRequiredService<LogBoundKeys>()));

        builder.Services.AddSingleton(sp => sp.GetRequiredService<PostgresAdapters>().EventStore);
```

The key set publishes what the log binds, each with where:

In `src/Curia.Api/ForumEndpoints.cs`, replace:

```csharp
    /// exactly the kind of routing detail that works locally and 404s in production. A query
    /// parameter has no such ambiguity.</para>
    /// </summary>
    private static async Task<IResult> GetJwks(
        string agent, IAuthorKeyRegistry keys, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agent))
```

with:

```csharp
    /// exactly the kind of routing detail that works locally and 404s in production. A query
    /// parameter has no such ambiguity.</para>
    ///
    /// <para><b>Only the keys the log binds (R4.35, errata G16), each with where (R6.54).</b> A key the
    /// store holds and the log does not bind is honoured nowhere, so it is not published either: a
    /// post signed under one fails a reader's signature check, as it should. Each published key names
    /// the leaf that binds it (<c>curia_log_index</c>), so a reader can check the binding against a
    /// signed head instead of trusting this key set. An identity the store holds no row for is 404, as
    /// before; one whose every row the log refuses gets an empty set.</para>
    /// </summary>
    private static async Task<IResult> GetJwks(
        string agent, LogBoundKeys keys, IEventReader events, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agent))
```

In `src/Curia.Api/ForumEndpoints.cs`, replace:

```csharp

        var agentId = agent;
        var registered = await keys.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);

        return registered.Count == 0
            ? Results.NotFound(new Problem("curia/keys/unknown-agent", "No keys for that agent", agentId))
            : Results.Ok(Jwks.ForAgent(registered));
    }

```

with:

```csharp

        var agentId = agent;
        var read = await keys.KeySetAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var keySet, out var readError))
        {
            return Results.Json(
                new Problem("curia/log/unreadable", "The event log could not be read", readError!.Type),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (keySet!.Stored == 0)
            return Results.NotFound(new Problem("curia/keys/unknown-agent", "No keys for that agent", agentId));

        // The leaf that binds each key, from the Acta folded once, as every route that serves a post
        // or a log document folds it: no index of the key set's own, which would be a second
        // computation of R6.47's leaf index that must agree with the fold forever (the spec's
        // Decision 8). A log that will not fold into a tree publishes the keys without positions
        // rather than no keys: a reader then cannot check the binding, and says so (R6.54's absence
        // is an absence).
        var (acta, _) = await ActaEndpoints.FoldAsync(events, cancellationToken).ConfigureAwait(false);
        return Results.Ok(Jwks.ForAgent(keySet.Bound, acta is null ? _ => null : acta.IndexOf));
    }

```

In `src/Curia.Api/Jwks.cs`, replace:

```csharp
using System.Text.Json.Nodes;
using Curia.Application.Ports;
using Curia.Canon.Jws;
```

with:

```csharp
using System.Text.Json.Nodes;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Jws;
```

In `src/Curia.Api/Jwks.cs`, replace:

```csharp
{
    /// <summary>
    /// Renders one agent's registered keys as an RFC 7517 <c>{"keys": [...]}</c> document, omitting
    /// any stored key it cannot publish (<see cref="CanPublish"/>).
    ///
    /// <para><b>Omitted, not failed.</b> Key rows written before the enrollment route checked a key's
```

with:

```csharp
{
    /// <summary>
    /// Renders one agent's keys the log binds (R4.35) as an RFC 7517 <c>{"keys": [...]}</c> document,
    /// omitting any it cannot publish (<see cref="CanPublish"/>), and naming for each the leaf that
    /// binds it (<paramref name="logIndexOf"/>, R6.54) where it is known.
    ///
    /// <para><b>Omitted, not failed.</b> Key rows written before the enrollment route checked a key's
```

In `src/Curia.Api/Jwks.cs`, replace:

```csharp
    /// empty set, not a 404, which still means that the store holds no row.</para>
    /// </summary>
    public static JsonObject ForAgent(IReadOnlyList<RegisteredKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var array = new JsonArray();
        foreach (var registered in keys)
        {
            // Two refusals, one outcome: the rule the key's verifier owns, and the renderer the log's
            // key-binding entries use too (R4.34). PublicJwkTests holds them to the same answer on
```

with:

```csharp
    /// empty set, not a 404, which still means that the store holds no row.</para>
    /// </summary>
    /// <param name="keys">The keys, each with the binding the log holds for it.</param>
    /// <param name="logIndexOf">The leaf index of an event, by its id; <see langword="null"/> when the log could not say.</param>
    public static JsonObject ForAgent(IReadOnlyList<BoundKey> keys, Func<string, long?> logIndexOf)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logIndexOf);

        var array = new JsonArray();
        foreach (var bound in keys)
        {
            var registered = bound.Key;

            // Two refusals, one outcome: the rule the key's verifier owns, and the renderer the log's
            // key-binding entries use too (R4.34). PublicJwkTests holds them to the same answer on
```

In `src/Curia.Api/Jwks.cs`, replace:

```csharp
            if (!PublicJwk.Of(registered.Key).TryGetValue(out var jwk, out _)) continue;

            array.Add(Annotate(ActaEndpoints.ToObject(jwk!), registered));
        }

```

with:

```csharp
            if (!PublicJwk.Of(registered.Key).TryGetValue(out var jwk, out _)) continue;

            var node = Annotate(ActaEndpoints.ToObject(jwk!), registered);
            if (logIndexOf(bound.Binding.EventId) is { } index) node["curia_log_index"] = index;
            array.Add(node);
        }

```

- [ ] **Step 7: Run everything this task touches**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Infrastructure.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Client.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Mcp.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `0 Warning(s)`; Application 292, Infrastructure 106, Api 227, Client 204, Mcp 73. The client suites run against a stub whose key set does not yet name leaves; nothing in them reads `curia_log_index` until Task 8.

- [ ] **Step 8: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'R4.35: a stored key is honoured, and published, only as the log binds it\n\nLogBoundKeys asks the store first and then holds its answer to the log, for\ningest, for the token endpoint through LogBoundAgentKeyResolver, and for the\nkey set, which now names the binding leaf of each key. A key row no enrollment\nbound signs nothing and mints nothing.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 6: A header's algorithm is its key's (R5.21)

**Files:**
- Modify: `src/Curia.AuthN/AuthNErrors.cs`, `src/Curia.AuthN/ClientAssertionValidator.cs`, `src/Curia.AuthN/AccessTokenValidator.cs`
- Modify (tests): `tests/Curia.AuthN.Tests/ClientAssertionValidatorTests.cs`, `tests/Curia.AuthN.Tests/AccessTokenValidatorDpopTests.cs`, `tests/Curia.Api.Tests/StoredKeyFormTests.cs`

**Interfaces:**
- Produces: `AuthNErrors.AlgKeyMismatch(headerAlg, keyAlg)`, slug `curia/authn/alg-key-mismatch`, title `The header names another algorithm than its key`. The title carries no apostrophe on purpose: the token endpoint serializes it into JSON, and the default encoder writes an apostrophe as `'`, which a byte-for-byte assertion would then have to spell.

**Why now.** D27 left it with rotation, but it has no precondition rotation meets: it pins what both validators choose a verifier by, and this stage rewires the resolver the assertion validator asks. The EdDSA point check, D27's other leftover, does wait for rotation — see the spec's Decision 10.

- [ ] **Step 1: Write the failing tests**

In `tests/Curia.AuthN.Tests/ClientAssertionValidatorTests.cs`, replace:

```csharp
        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    [Fact]
    public async Task AlgIsPinnedBeforeTypEvenWhenTypWouldAlsoFail()
```

with:

```csharp
        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    /// <summary>
    /// R5.21 (errata G16): an assertion whose header names the other allowed algorithm than its key's
    /// -- <c>ES256</c> over the agent's Ed25519 key, genuinely signed by that key -- is refused by name
    /// before any verifier is chosen. Before the pin, the header chose the ES256 verifier, which is
    /// handed an Ed25519 key and answers false, so the refusal read as a bad signature.
    /// </summary>
    [Fact]
    public async Task R5_21_AHeaderNamingAnotherAlgorithmThanTheKeysIsRefusedByName()
    {
        var scenario = new ClientAssertionScenario();
        var assertion = scenario.SignValid(header: scenario.ValidHeader().With("alg", "ES256"));

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-key-mismatch header=ES256 key=EdDSA", $"{error!.Type} {error.Detail}");
    }

    [Fact]
    public async Task AlgIsPinnedBeforeTypEvenWhenTypWouldAlsoFail()
```

In `tests/Curia.AuthN.Tests/AccessTokenValidatorDpopTests.cs`, replace:

```csharp
    }

    [Fact]
    public async Task DpopProofWithTamperedSignatureIsRejected()
```

with:

```csharp
    }

    /// <summary>
    /// R5.21 (errata G16): a proof whose header names <c>ES256</c> over an embedded Ed25519
    /// <c>jwk</c>, genuinely signed by that key, is refused by name. The binding holds -- the jwk is the
    /// token's -- so only the pin can refuse it; before the pin the header chose the ES256 verifier,
    /// and the refusal read as a bad signature.
    /// </summary>
    [Fact]
    public async Task R5_21_AProofWhoseHeaderNamesAnotherAlgorithmThanItsJwkIsRefusedByName()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var proof = scenario.SignDpopProof(token, header: scenario.ValidDpopHeader().With("alg", "ES256"));
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-key-mismatch header=ES256 key=EdDSA", $"{error!.Type} {error.Detail}");
    }

    [Fact]
    public async Task DpopProofWithTamperedSignatureIsRejected()
```

The HTTP row whose header says `EdDSA` over an honest `ES256` key leaves the R4.15 theory, which asserts a bad signature, for a fact of its own:

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
    /// <item><c>es256-p384-spki</c>: the row holds a P-384 key, under an identity enrolled before errata
    /// G16, and the assertion is genuinely signed by it under <c>ES256</c>. It was issued a token.</item>
    /// <item><c>eddsa-header-over-an-es256-key</c>: an honest agent's row, and an assertion whose
    /// header says <c>EdDSA</c> over 64 zero bytes. Anyone could send it, naming any agent. It
    /// answered 500.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("es256-32-raw-bytes")]
    [InlineData("es256-p384-spki")]
    [InlineData("eddsa-header-over-an-es256-key")]
    public async Task R4_15_AStoredKeyThatIsNotAKeyOfItsAlgorithmMintsNoTokenAndIsAnsweredAsABadSignatureIs(string row)
    {
```

with:

```csharp
    /// <item><c>es256-p384-spki</c>: the row holds a P-384 key, under an identity enrolled before errata
    /// G16, and the assertion is genuinely signed by it under <c>ES256</c>. It was issued a token.</item>
    /// </list>
    /// The third row this theory held, an assertion whose header says <c>EdDSA</c> over an honest
    /// <c>ES256</c> key, is refused by name since errata G16 (R5.21), and is
    /// <see cref="R5_21_AnAssertionWhoseHeaderNamesAnotherAlgorithmThanItsKeyIsRefusedByName"/>.
    /// </summary>
    [Theory]
    [InlineData("es256-32-raw-bytes")]
    [InlineData("es256-p384-spki")]
    public async Task R4_15_AStoredKeyThatIsNotAKeyOfItsAlgorithmMintsNoTokenAndIsAnsweredAsABadSignatureIs(string row)
    {
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
        Assert.Equal($"401 {SignatureDoesNotVerify}", $"{(int)controlStatus} {controlBody}");

        var agent = row is "es256-32-raw-bytes" or "es256-p384-spki"
            ? await EnrolledBeforeKeyBindingAsync($"stored-{row}-{suffix}", ct)
            : await EnrolledAsync($"stored-{row}-{suffix}", ct);
        HttpStatusCode status;
        string body;
```

with:

```csharp
        Assert.Equal($"401 {SignatureDoesNotVerify}", $"{(int)controlStatus} {controlBody}");

        var agent = await EnrolledBeforeKeyBindingAsync($"stored-{row}-{suffix}", ct);
        HttpStatusCode status;
        string body;
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
                (status, body) = await DpopClient.For(agent, p384)
                    .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);
                break;
            }

            case "eddsa-header-over-an-es256-key":
            {
                var header = new JsonObject { ["alg"] = "EdDSA", ["kid"] = agent.Kid, ["typ"] = "JWT" };
                var claims = new JsonObject
                {
                    ["iss"] = agent.AgentId,
                    ["sub"] = agent.AgentId,
                    ["aud"] = TokenEndpoint,
                    ["iat"] = forum.Now.ToUnixTimeSeconds(),
                    ["exp"] = forum.Now.AddSeconds(60).ToUnixTimeSeconds(),
                    ["jti"] = Guid.NewGuid().ToString("N"),
                };
                var assertion =
                    Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
                    Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString())) + "." +
                    Base64Url.EncodeToString(new byte[64]);
                (status, body) = await DpopClient.For(agent, agent.AssertionKey)
                    .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, assertion, ct);
                break;
            }
```

with:

```csharp
                (status, body) = await DpopClient.For(agent, p384)
                    .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);
                break;
            }
```

In `tests/Curia.Api.Tests/StoredKeyFormTests.cs`, replace:

```csharp
        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)status} {body}");
    }
}
```

with:

```csharp
        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)status} {body}");
    }

    /// <summary>
    /// R5.21 (errata G16): an honest agent's row, and an assertion whose header says <c>EdDSA</c> over
    /// 64 zero bytes, naming that agent's <c>ES256</c> <c>kid</c>. Anyone could send it, naming any
    /// agent. Before errata G15's key rule it answered 500; after it, 401 as a bad signature, which is
    /// what the header's choice of verifier made it. It is now refused by name before any verifier is
    /// chosen.
    /// </summary>
    [Fact]
    public async Task R5_21_AnAssertionWhoseHeaderNamesAnotherAlgorithmThanItsKeyIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = await EnrolledAsync($"stored-eddsa-header-{Suffix()}", ct);

        var header = new JsonObject { ["alg"] = "EdDSA", ["kid"] = agent.Kid, ["typ"] = "JWT" };
        var claims = new JsonObject
        {
            ["iss"] = agent.AgentId,
            ["sub"] = agent.AgentId,
            ["aud"] = TokenEndpoint,
            ["iat"] = forum.Now.ToUnixTimeSeconds(),
            ["exp"] = forum.Now.AddSeconds(60).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        var assertion =
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString())) + "." +
            Base64Url.EncodeToString(new byte[64]);

        var (status, body) = await DpopClient.For(agent, agent.AssertionKey)
            .RequestTokenAsync(forum.Client, TokenEndpoint, forum.Now, agent.AgentId, assertion, ct);

        Assert.Equal(
            "401 {\"error\":\"invalid_client\",\"error_description\":\"The header names another algorithm than its key\",\"detail\":\"curia/authn/alg-key-mismatch\"}",
            $"{(int)status} {body}");
    }
}
```

- [ ] **Step 2: Run them, and see them red**

```bash
dotnet test tests/Curia.AuthN.Tests -c Release --nologo --filter "FullyQualifiedName~R5_21" 2>&1 | grep -E "Passed!|Failed!|Expected|Actual"
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~R5_21" 2>&1 | grep -E "Passed!|Failed!|Expected|Actual"
```

Expected, as printed on the unchanged validators: `Expected: "curia/authn/alg-key-mismatch header=ES256 key=EdDS"···`, `Actual: "curia/authn/signature-invalid "` for each AuthN fact, and `Actual: ···"error_description":"Signature does not verify"···` for the HTTP fact.

- [ ] **Step 3: The pins**

In `src/Curia.AuthN/AuthNErrors.cs`, replace:

```csharp
        "The resolved key was not valid for this agent at server_ts (R6.31); validity is never evaluated at created_at or submission time",
        $"kid={kid} server_ts={at}");

    public static Error SignatureInvalid() => new(
```

with:

```csharp
        "The resolved key was not valid for this agent at server_ts (R6.31); validity is never evaluated at created_at or submission time",
        $"kid={kid} server_ts={at}");

    /// <summary>
    /// R5.21 (errata G16): the header's <c>alg</c> is not the algorithm of the key the signature is to
    /// be verified under -- the resolved agent key for a client assertion, the embedded <c>jwk</c> for
    /// a DPoP proof. Names both algorithms and nothing else.
    /// </summary>
    public static Error AlgKeyMismatch(string headerAlg, string keyAlg) => new(
        "curia/authn/alg-key-mismatch", "The header names another algorithm than its key", $"header={headerAlg} key={keyAlg}");

    public static Error SignatureInvalid() => new(
```

In `src/Curia.AuthN/ClientAssertionValidator.cs`, replace:

```csharp
            return Result<ClientAssertionClaims>.Fail(keyError!);

        if (!context.VerifiersByAlg.TryGetValue(header.Alg, out var verifier))
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.AlgNotAllowed(header.Alg));
```

with:

```csharp
            return Result<ClientAssertionClaims>.Fail(keyError!);

        // R5.21 (errata G16): the verifier is chosen by the header's alg, so the header must name the
        // resolved key's own algorithm. Without this, a header naming the other algorithm handed an
        // agent's key to a verifier it is not a key of, and the refusal read as a bad signature.
        if (!string.Equals(header.Alg, key!.Alg, StringComparison.Ordinal))
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.AlgKeyMismatch(header.Alg, key.Alg));

        if (!context.VerifiersByAlg.TryGetValue(header.Alg, out var verifier))
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.AlgNotAllowed(header.Alg));
```

In `src/Curia.AuthN/AccessTokenValidator.cs`, replace:

```csharp
            return Result<ValidatedRequest>.Fail(AuthNErrors.BindingMismatch());

        var proofKey = jwk.ToPublicKeyMaterial(kid: "");
        if (!context.VerifiersByAlg.TryGetValue(proofHeader.Alg, out var proofVerifier))
```

with:

```csharp
            return Result<ValidatedRequest>.Fail(AuthNErrors.BindingMismatch());

        // R5.21 (errata G16): the proof's alg names the embedded key's algorithm, which its jwk's type
        // fixes, before any key material is built or any verifier chosen by the header.
        var keyAlg = jwk!.Match(okpEd25519: _ => "EdDSA", ecP256: _ => "ES256");
        if (!string.Equals(proofHeader.Alg, keyAlg, StringComparison.Ordinal))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AlgKeyMismatch(proofHeader.Alg, keyAlg));

        var proofKey = jwk.ToPublicKeyMaterial(kid: "");
        if (!context.VerifiersByAlg.TryGetValue(proofHeader.Alg, out var proofVerifier))
```

- [ ] **Step 4: Run them**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.AuthN.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `0 Warning(s)`; AuthN 68, Api 227.

- [ ] **Step 5: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'R5.21: a client assertion and a DPoP proof name the algorithm of their key\n\nBoth validators chose the verifier by the header, so a header naming the other\nallowed algorithm handed a key to a verifier it is not a key of, and the refusal\nread as a bad signature. Refused by name now, before a verifier is chosen.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 7: `curia-testis log author` (R6.54, the independent reader)

**Files:**
- Create: `rust/curia-testis/tests/log_author.rs`
- Modify: `rust/curia-testis/src/acta.rs`, `rust/curia-testis/src/bin/curia-testis.rs`, `tests/Curia.Api.Tests/ActaEndpointTests.cs`, `.github/workflows/ci.yml`

**Interfaces:**
- Produces: `curia_testis::acta::verify_author(post_entry, post_proof, key_entry, key_proof, &VerifiedHead) -> Result<VerifiedAuthor, ActaError>`, and `curia-testis log author --entry <path> --proof <path> --key-entry <path> --key-proof <path> [--head <path> --log-jwks <path>]`: exit 0 verified, 1 failed, 2 usage, 3 could not be checked (no head; the author's own `agent.enrolled` naming the post's `kid`, which carries no key; or the author's binding of that `kid` at a leaf not before the post).
- Consumes: the served documents alone. No agent key set is an input.

**Why before the reference client.** The leaf's payload becomes permanent in every log that writes it, and `curia-testis` shares no code with the Forum. An independent reader is what shows the shape is readable from its bytes alone, before anything depends on it.

- [ ] **Step 1: Write the failing tests**

The fixture is two published vectors that describe one log: `acta/key-bound-entry` binds `envelope/ed25519-minimal`'s key to that envelope's author, and `acta/content-entry` is the post the envelope became.

Create `rust/curia-testis/tests/log_author.rs`:

```rust
//! R6.54 (errata G16): authorship from the log alone.
//!
//! The fixture is two published vectors that describe one log:
//! `acta/key-bound-entry` binds `envelope/ed25519-minimal`'s key to that
//! envelope's author, and `acta/content-entry` is the post the envelope
//! became. Each test builds a tree over those entries (plus a filler leaf),
//! proves both, and asks [`verify_author`] whether the post was signed by the
//! key its author's binding carries, bound first. The head is constructed, not
//! signed: this crate never signs, and the head's signature is `log head`'s
//! business, which `log_outcomes.rs` and the C# end-to-end test cover.

use std::path::{Path, PathBuf};
use std::process::Command;

use curia_testis::acta::{self, ActaError, VerifiedHead};
use curia_testis::merkle::{self, Hash};

fn conformance_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("CURIA_CONFORMANCE_DIR") {
        return PathBuf::from(dir);
    }
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

fn vector(name: &str) -> String {
    std::fs::read_to_string(conformance_dir().join("acta").join(name).join("input.json"))
        .unwrap_or_else(|e| panic!("conformance/acta/{name}/input.json must be readable: {e}"))
}

/// The key binding, with its text edited by `edit` before it is hashed, so a
/// tampered entry is still a leaf of a tree that proves it.
fn key_entry(edit: impl Fn(String) -> String) -> String {
    edit(vector("key-bound-entry"))
}

fn post_entry() -> String {
    vector("content-entry")
}

fn leaf_of(entry: &str) -> Hash {
    merkle::leaf_hash(
        &curia_testis::canonicalize(entry.as_bytes()).expect("a vector canonicalizes"),
    )
}

/// A tree over `entries` in order (a `None` is a filler leaf), and for each
/// named index its entry document and proof, plus the head over the whole tree.
struct Log {
    documents: Vec<(Vec<u8>, Vec<u8>)>,
    head: VerifiedHead,
}

fn log(entries: &[Option<&str>]) -> Log {
    let leaves: Vec<Hash> = entries
        .iter()
        .enumerate()
        .map(|(i, e)| match e {
            Some(entry) => leaf_of(entry),
            None => merkle::leaf_hash(format!("filler-{i}").as_bytes()),
        })
        .collect();
    let root = merkle::root(&leaves);

    let documents = entries
        .iter()
        .enumerate()
        .map(|(i, e)| {
            let entry = e.unwrap_or("{}");
            let document = format!(r#"{{"log_index":{i},"entry":{entry}}}"#).into_bytes();
            let path: Vec<String> = merkle::inclusion_path(&leaves, i)
                .iter()
                .map(acta::format_digest)
                .collect();
            let proof = serde_json::to_vec(&serde_json::json!({
                "log_index": i,
                "tree_size": leaves.len(),
                "leaf_hash": acta::format_digest(&leaves[i]),
                "audit_path": path,
                "root_hash": acta::format_digest(&root),
                "head_signed": true,
            }))
            .expect("a proof serializes");
            (document, proof)
        })
        .collect();

    Log {
        documents,
        head: VerifiedHead {
            root,
            tree_size: leaves.len() as u64,
            timestamp: "2026-09-04T17:00:00.000000Z".to_string(),
            kid: "log-test".to_string(),
            alg: "ES256".to_string(),
        },
    }
}

fn author(log: &Log, post: usize, key: usize) -> Result<acta::VerifiedAuthor, ActaError> {
    acta::verify_author(
        &log.documents[post].0,
        &log.documents[post].1,
        &log.documents[key].0,
        &log.documents[key].1,
        &log.head,
    )
}

#[test]
fn r6_54_a_post_signed_under_the_key_its_author_bound_before_it_verifies() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let verified = author(&log, 2, 0).expect("the log alone establishes authorship");

    assert_eq!(
        (
            verified.author.as_str(),
            verified.kid.as_str(),
            verified.alg.as_str(),
            verified.key_index,
            verified.post_index
        ),
        (
            "agent://curia.example/tuesdaycrowd/scriptor",
            "conformance-ed25519-minimal",
            "EdDSA",
            0,
            2
        )
    );
}

/// The author's own binding, after the post: the log says nothing about
/// which key was the author's when the post was accepted. R6.54's third
/// outcome, never a failure: an identity whose first binding came after its
/// history (it enrolled before the log recorded enrollments, and anyone
/// re-announced its public key) would otherwise read as forged throughout.
#[test]
fn r6_54_a_key_bound_after_the_post_is_not_established() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&post), None, Some(&key)]);

    let err = author(&log, 0, 2).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/bound-after-post", true),
        "{err}"
    );
}

#[test]
fn r6_54_a_binding_carrying_another_key_fails_at_the_signature() {
    // envelope/ed25519-full's key: a real Ed25519 key, and not the one that signed the post.
    let key = key_entry(|s| {
        s.replace(
            "HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8",
            "8p38omqqbKnPOw4UNosc4kVE9KdjrnDPnl7fCLTv0uo",
        )
    });
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert!(
        matches!(err, ActaError::Author(_)),
        "expected the signature to fail under the bound key, got {err}"
    );
}

#[test]
fn r6_54_a_binding_for_another_identity_fails() {
    let key = key_entry(|s| s.replace("tuesdaycrowd/scriptor", "tuesdaycrowd/someone-else"));
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/binding-mismatch", "{err}");
}

#[test]
fn r6_54_an_enrollment_that_names_the_kid_alone_is_not_checked() {
    let enrolled = r#"{"actor_id":"agent://curia.example/tuesdaycrowd/scriptor","aggregate_id":"agent://curia.example/tuesdaycrowd/scriptor","event_id":"01K4CQ1TZ0M2P4R6T8V0X2Z4B6","event_type":"agent.enrolled","payload":{"agent_id":"agent://curia.example/tuesdaycrowd/scriptor","kid":"conformance-ed25519-minimal","reason":"Enrollment accepted: agent key registered with the Registrar"},"server_ts":"2026-09-04T14:00:00.000000Z"}"#;
    let post = post_entry();
    let log = log(&[Some(enrolled), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/key-not-carried", true),
        "{err}"
    );
}

/// R6.54 reports only the author's own enrollment naming the post's kid as
/// could-not-be-checked; another identity's enrollment, naming the same kid,
/// binds nothing the post names and fails.
#[test]
fn r6_54_another_identitys_enrollment_fails() {
    let enrolled = r#"{"actor_id":"agent://curia.example/tuesdaycrowd/someone-else","aggregate_id":"agent://curia.example/tuesdaycrowd/someone-else","event_id":"01K4CQ1TZ0M2P4R6T8V0X2Z4B6","event_type":"agent.enrolled","payload":{"agent_id":"agent://curia.example/tuesdaycrowd/someone-else","kid":"conformance-ed25519-minimal","reason":"Enrollment accepted: agent key registered with the Registrar"},"server_ts":"2026-09-04T14:00:00.000000Z"}"#;
    let post = post_entry();
    let log = log(&[Some(enrolled), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/binding-mismatch", false),
        "{err}"
    );
}

#[test]
fn r6_54_an_entry_that_is_not_a_post_fails() {
    let key = key_entry(|s| s);
    let log = log(&[Some(&key), None, Some(&key)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/not-a-post", "{err}");
}

#[test]
fn r6_54_a_head_over_another_tree_fails() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let mut log = log(&[Some(&key), None, Some(&post)]);
    log.head.tree_size += 1;

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/head-size-mismatch", "{err}");
}

/// The CLI's third outcome: with no `--head`, nothing anchors the two proofs,
/// and `log author` says so with exit 3 rather than 0.
#[test]
fn r6_54_log_author_without_a_head_is_not_checked() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let dir = std::env::temp_dir().join("curia-log-author-unanchored");
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("the scratch directory must be creatable");
    let files = [
        ("entry.json", &log.documents[2].0),
        ("proof.json", &log.documents[2].1),
        ("key-entry.json", &log.documents[0].0),
        ("key-proof.json", &log.documents[0].1),
    ];
    for (name, bytes) in files {
        std::fs::write(dir.join(name), bytes).expect("a scratch file must be writable");
    }

    let output = Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args([
            "log",
            "author",
            "--entry",
            dir.join("entry.json").to_str().unwrap(),
            "--proof",
            dir.join("proof.json").to_str().unwrap(),
            "--key-entry",
            dir.join("key-entry.json").to_str().unwrap(),
            "--key-proof",
            dir.join("key-proof.json").to_str().unwrap(),
        ])
        .output()
        .expect("failed to spawn the curia-testis binary");

    assert_eq!(
        output.status.code(),
        Some(3),
        "log author with no head must not exit 0.\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}
```

```bash
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked --test log_author 2>&1 | grep -E "^error|cannot find" | head -3
```

Expected: `error[E0425]: cannot find function `verify_author` in module `acta``, with E0425 for `VerifiedAuthor` and E0599 for `ActaError::Author`.

- [ ] **Step 2: `verify_author`**

In `rust/curia-testis/src/acta.rs`, replace:

```rust
//! its own right.
//!
//! Every rejection is a typed [`ActaError`] with a predicate slug; nothing
//! here panics on malformed input.
```

with:

```rust
//! its own right.
//!
//! [`verify_author`] is R6.54 (errata G16): authorship from the log alone.
//! A post's entry carries its canonical envelope and signature; its author's
//! key is carried by an `agent.key-bound` entry (R4.34). Both proven under one
//! signed head, the binding first, and the signature verifying under the key
//! the binding carries: no key set the Forum serves enters the check.
//!
//! Every rejection is a typed [`ActaError`] with a predicate slug; nothing
//! here panics on malformed input.
```

In `rust/curia-testis/src/acta.rs`, replace:

```rust
use serde_json::Value;

use crate::jwk::{JwkError, JwkSet};
use crate::jws::{self, JwsError};
```

with:

```rust
use serde_json::Value;

use crate::envelope::VerifyEnvelopeError;
use crate::jwk::{JwkError, JwkSet};
use crate::jws::{self, JwsError};
```

In `rust/curia-testis/src/acta.rs`, replace:

```rust
/// The `typ` of a signed tree head; a head is not a post and must not verify as one.
pub const HEAD_TYP: &str = "curia-head+jws";

#[derive(Debug)]
```

with:

```rust
/// The `typ` of a signed tree head; a head is not a post and must not verify as one.
pub const HEAD_TYP: &str = "curia-head+jws";

/// The entry a post's acceptance is (R6.46): its payload carries the
/// canonical envelope and the detached signature.
pub const POST_ACCEPTED: &str = "post.accepted";

/// The entry that binds a key to an identity (R4.34): its payload carries
/// `agent_id`, `kid` and the public `jwk`.
pub const KEY_BOUND: &str = "agent.key-bound";

/// The enrollment record. Before R4.34 it was the only entry naming a key,
/// and it names the `kid` alone.
pub const ENROLLED: &str = "agent.enrolled";

#[derive(Debug)]
```

In `rust/curia-testis/src/acta.rs`, replace:

```rust
    /// The head's root is not the proof's root at that size.
    HeadRootMismatch,
}

```

with:

```rust
    /// The head's root is not the proof's root at that size.
    HeadRootMismatch,
    /// The entry offered as a post is not a `post.accepted` entry.
    NotAPost { event_type: String },
    /// The entry offered as the key's binding is not an `agent.key-bound`
    /// entry (nor an `agent.enrolled` one).
    NotAKeyBinding { event_type: String },
    /// The binding does not name the post's author and the post's `kid`.
    BindingMismatch(String),
    /// The author's binding of the post's `kid` is not earlier in the log
    /// than the post, so the log says nothing about which key was the
    /// author's when the post was accepted. Not a failure and not a pass
    /// (exit 3): a forger binds first at no cost, and what lands here is
    /// history older than its binding.
    BoundAfterPost { key: u64, post: u64 },
    /// The post's signature does not verify under the key the binding
    /// carries (the inner predicate says why).
    Author(VerifyEnvelopeError),
    /// The binding is the author's own `agent.enrolled`, earlier than the
    /// post and naming its `kid` (all an identity enrolled before R4.34 has;
    /// a reader cannot tell when one was made): the log names the `kid` and
    /// carries no key, so which key signed cannot be established from the
    /// log. Not a failure and not a pass (exit 3).
    KeyNotCarried { kid: String },
}

```

In `rust/curia-testis/src/acta.rs`, replace:

```rust
            ActaError::HeadSizeMismatch { .. } => "curia/acta/head-size-mismatch",
            ActaError::HeadRootMismatch => "curia/acta/head-root-mismatch",
        }
    }
```

with:

```rust
            ActaError::HeadSizeMismatch { .. } => "curia/acta/head-size-mismatch",
            ActaError::HeadRootMismatch => "curia/acta/head-root-mismatch",
            ActaError::NotAPost { .. } => "curia/acta/not-a-post",
            ActaError::NotAKeyBinding { .. } => "curia/acta/not-a-key-binding",
            ActaError::BindingMismatch(_) => "curia/acta/binding-mismatch",
            ActaError::BoundAfterPost { .. } => "curia/acta/bound-after-post",
            ActaError::Author(err) => err.predicate(),
            ActaError::KeyNotCarried { .. } => "curia/acta/key-not-carried",
        }
    }

    /// R6.54's third outcome: the log carries no key for the post's `kid`
    /// from before the post. Neither a pass nor a failure, and `log author`
    /// exits 3 for exactly these.
    pub fn not_established(&self) -> bool {
        matches!(
            self,
            ActaError::KeyNotCarried { .. } | ActaError::BoundAfterPost { .. }
        )
    }
```

In `rust/curia-testis/src/acta.rs`, replace:

```rust
                )
            }
        }
        .and_then(|()| write!(f, " [{}]", self.predicate()))
```

with:

```rust
                )
            }
            ActaError::NotAPost { event_type } => {
                write!(f, "the post entry is `{event_type}`, not `{POST_ACCEPTED}`")
            }
            ActaError::NotAKeyBinding { event_type } => {
                write!(f, "the key entry is `{event_type}`, not `{KEY_BOUND}`")
            }
            ActaError::BindingMismatch(detail) => {
                write!(f, "the key entry does not bind the post's key: {detail}")
            }
            ActaError::BoundAfterPost { key, post } => write!(
                f,
                "the key is bound at leaf {key}, which is not before the post at leaf {post}"
            ),
            ActaError::Author(err) => {
                write!(f, "the post does not verify under the bound key: {err}")
            }
            ActaError::KeyNotCarried { kid } => write!(
                f,
                "the log names kid `{kid}` only in the author's enrollment, which carries no key"
            ),
        }
        .and_then(|()| write!(f, " [{}]", self.predicate()))
```

In `rust/curia-testis/src/acta.rs`, replace:

```rust
        from_root,
        to_root,
    })
}
```

with:

```rust
        from_root,
        to_root,
    })
}

/// Authorship established from the log alone (R6.54).
#[derive(Debug, Clone)]
pub struct VerifiedAuthor {
    pub author: String,
    pub kid: String,
    pub alg: String,
    pub key_index: u64,
    pub post_index: u64,
}

/// R6.54 (errata G16): the post in `post_entry` was signed by the key its
/// author's `agent.key-bound` entry (`key_entry`) carries, and the log bound
/// that key before it accepted the post, all under `head`.
///
/// Both entries' leaves are recomputed and proven (R6.46, R6.48) and tied to
/// the one head, so their order is the log's order. The key entry must bind
/// the post's own author and `kid`, or it is [`ActaError::BindingMismatch`],
/// whatever its type. The author's binding at a leaf not before the post is
/// [`ActaError::BoundAfterPost`], and the author's own `agent.enrolled`
/// naming it is [`ActaError::KeyNotCarried`]: each says the log holds
/// no key for the post from before it, and neither is a failure
/// ([`ActaError::not_established`]). Only then is the signature checked, over
/// the envelope the post's own entry carries, under a key set holding only
/// the key the binding carries: nothing a key set endpoint serves is read.
pub fn verify_author(
    post_entry: &[u8],
    post_proof: &[u8],
    key_entry: &[u8],
    key_proof: &[u8],
    head: &VerifiedHead,
) -> Result<VerifiedAuthor, ActaError> {
    let post = verify_inclusion(post_entry, post_proof)?;
    head_covers(head, post.tree_size, &post.root)?;
    let key = verify_inclusion(key_entry, key_proof)?;
    head_covers(head, key.tree_size, &key.root)?;

    let post_fields = entry_fields(post_entry)?;
    let post_type = str_at(&post_fields, &["event_type"], "post entry")?;
    if post_type != POST_ACCEPTED {
        return Err(ActaError::NotAPost {
            event_type: post_type.to_string(),
        });
    }
    let canonical = str_at(&post_fields, &["payload", "canonical"], "post entry")?;
    let signature = str_at(&post_fields, &["payload", "signature"], "post entry")?;

    let key_fields = entry_fields(key_entry)?;
    let key_type = str_at(&key_fields, &["event_type"], "key entry")?;
    if key_type != KEY_BOUND && key_type != ENROLLED {
        return Err(ActaError::NotAKeyBinding {
            event_type: key_type.to_string(),
        });
    }
    let bound_kid = str_at(&key_fields, &["payload", "kid"], "key entry")?;
    let bound_agent = str_at(&key_fields, &["payload", "agent_id"], "key entry")?;
    let aggregate = str_at(&key_fields, &["aggregate_id"], "key entry")?;

    // Whose binding, of which kid, before anything about its key: an entry
    // for another identity or another kid binds nothing the post names,
    // whichever type it is.
    let (post_author, post_kid) = post_author_and_kid(canonical, signature)?;
    if aggregate != bound_agent || bound_agent != post_author || bound_kid != post_kid {
        return Err(ActaError::BindingMismatch(format!(
            "the entry binds kid `{bound_kid}` to `{bound_agent}` in stream `{aggregate}`, and the post is `{post_author}`'s under kid `{post_kid}`"
        )));
    }

    // R6.54: a binding after the post is the log's silence about the key
    // the post was accepted under, not a contradiction of it.
    if key.log_index >= post.log_index {
        return Err(ActaError::BoundAfterPost {
            key: key.log_index,
            post: post.log_index,
        });
    }
    if key_type == ENROLLED {
        return Err(ActaError::KeyNotCarried {
            kid: bound_kid.to_string(),
        });
    }
    let jwk = key_fields
        .get("payload")
        .and_then(|p| p.get("jwk"))
        .filter(|j| j.is_object())
        .ok_or(ActaError::MissingField {
            what: "key entry",
            field: "jwk",
        })?;

    // The post's own entry carries the envelope as the Forum persisted it;
    // the submission is rebuilt around it verbatim, never re-encoded.
    let signature_json = serde_json::to_string(signature).map_err(|e| ActaError::Malformed {
        what: "post entry",
        detail: e.to_string(),
    })?;
    let submission = format!("{{\"envelope\":{canonical},\"signature\":{signature_json}}}");
    let jwks = serde_json::to_vec(&serde_json::json!({ "keys": [jwk] })).map_err(|e| {
        ActaError::Malformed {
            what: "key entry",
            detail: e.to_string(),
        }
    })?;
    let provenance =
        crate::verify_envelope(submission.as_bytes(), &jwks).map_err(ActaError::Author)?;

    Ok(VerifiedAuthor {
        author: provenance.author,
        kid: provenance.kid,
        alg: provenance.alg,
        key_index: key.log_index,
        post_index: post.log_index,
    })
}

/// The post's author, from its canonical envelope, and the `kid` its detached
/// signature's protected header names; read before any key is chosen, so the
/// binding can be compared with the post before it is trusted for anything.
/// The signature itself is verified afterwards, over the same bytes.
fn post_author_and_kid(canonical: &str, signature: &str) -> Result<(String, String), ActaError> {
    use base64::Engine;
    let malformed = |detail: String| ActaError::Malformed {
        what: "post entry",
        detail,
    };
    let envelope: Value = serde_json::from_str(canonical).map_err(|e| malformed(e.to_string()))?;
    let author = envelope
        .get("author")
        .and_then(Value::as_str)
        .ok_or_else(|| malformed("the envelope names no author".to_string()))?;
    let header = signature
        .split('.')
        .next()
        .and_then(|h| {
            base64::engine::general_purpose::URL_SAFE_NO_PAD
                .decode(h)
                .ok()
        })
        .and_then(|h| serde_json::from_slice::<Value>(&h).ok())
        .ok_or_else(|| malformed("the signature has no readable protected header".to_string()))?;
    let kid = header
        .get("kid")
        .and_then(Value::as_str)
        .ok_or_else(|| malformed("the signature's header names no kid".to_string()))?;
    Ok((author.to_string(), kid.to_string()))
}

/// The `entry` member of an entry document, parsed.
fn entry_fields(entry_json: &[u8]) -> Result<Value, ActaError> {
    let document: Value = serde_json::from_slice(entry_json).map_err(|e| ActaError::Malformed {
        what: "entry document",
        detail: e.to_string(),
    })?;
    document
        .get("entry")
        .cloned()
        .ok_or(ActaError::MissingField {
            what: "entry document",
            field: "entry",
        })
}

/// The string at `path` inside `value`, or which member is missing.
fn str_at<'a>(
    value: &'a Value,
    path: &[&'static str],
    what: &'static str,
) -> Result<&'a str, ActaError> {
    let mut at = value;
    for field in path {
        at = at
            .get(*field)
            .ok_or(ActaError::MissingField { what, field })?;
    }
    at.as_str().ok_or(ActaError::MissingField {
        what,
        field: path.last().copied().unwrap_or("value"),
    })
}
```

- [ ] **Step 3: The verb**

In `rust/curia-testis/src/bin/curia-testis.rs`, replace:

```rust
    curia-testis log inclusion   --entry <path> --proof <path> [--head <path> --log-jwks <path>]
    curia-testis log consistency --proof <path> [--from-head <path>] [--to-head <path>] [--log-jwks <path>]

    The log verbs take the JSON bodies of GET /v1/log/head, /v1/log/entries/{i},
```

with:

```rust
    curia-testis log inclusion   --entry <path> --proof <path> [--head <path> --log-jwks <path>]
    curia-testis log consistency --proof <path> [--from-head <path>] [--to-head <path>] [--log-jwks <path>]
    curia-testis log author      --entry <path> --proof <path> --key-entry <path> --key-proof <path>
                                 [--head <path> --log-jwks <path>]

    The log verbs take the JSON bodies of GET /v1/log/head, /v1/log/entries/{i},
```

In `rust/curia-testis/src/bin/curia-testis.rs`, replace:

```rust
    size and root the proof verifies against.

EXIT CODES:
    0  verified: every check ran and held
```

with:

```rust
    size and root the proof verifies against.

    log author is R6.54: the post in --entry was signed by the key the
    agent.key-bound entry in --key-entry carries, bound earlier in the log,
    with both proofs under one signed head. No agent key set is read.

EXIT CODES:
    0  verified: every check ran and held
```

In `rust/curia-testis/src/bin/curia-testis.rs`, replace:

```rust
    2  usage error (bad arguments, or a path that could not be read)
    3  could not be checked: the arithmetic held but nothing anchors it to a
       signed head. Pass --head/--log-jwks (inclusion) or --from-head/--to-head
       (consistency) to anchor it. This is not a pass and not a failure.
";

```

with:

```rust
    2  usage error (bad arguments, or a path that could not be read)
    3  could not be checked: the arithmetic held but nothing anchors it to a
       signed head. Pass --head/--log-jwks (inclusion, author) or
       --from-head/--to-head (consistency) to anchor it. For log author, also:
       the log carries no key for the post's kid from before the post -- the
       author's enrollment, which names the kid and no key, or the author's
       binding made after the post. This is not a pass and not a failure.
";

```

In `rust/curia-testis/src/bin/curia-testis.rs`, replace:

```rust
}

/// `curia-testis log <head|inclusion|consistency> --flag <path> ...`
fn run_log(args: &[String]) -> Result<(), CliError> {
    let verb = args.first().map(String::as_str).ok_or_else(|| {
        CliError::Usage("missing log subcommand: head, inclusion or consistency".to_string())
    })?;
    let flags = parse_path_flags(&args[1..])?;
```

with:

```rust
}

/// `curia-testis log <head|inclusion|consistency|author> --flag <path> ...`
fn run_log(args: &[String]) -> Result<(), CliError> {
    let verb = args.first().map(String::as_str).ok_or_else(|| {
        CliError::Usage(
            "missing log subcommand: head, inclusion, consistency or author".to_string(),
        )
    })?;
    let flags = parse_path_flags(&args[1..])?;
```

In `rust/curia-testis/src/bin/curia-testis.rs`, replace:

```rust
            Ok(())
        }
        other => Err(CliError::Usage(format!("unknown log subcommand `{other}`"))),
    }
}

const LOG_FLAGS: [&str; 6] = [
    "--head",
    "--log-jwks",
```

with:

```rust
            Ok(())
        }
        "author" => {
            let entry = read_flag(&flags, "--entry")?;
            let proof = read_flag(&flags, "--proof")?;
            let key_entry = read_flag(&flags, "--key-entry")?;
            let key_proof = read_flag(&flags, "--key-proof")?;
            let Some(head) = optional_head(&flags, "--head")? else {
                println!("head: not checked");
                return Err(CliError::NotAnchored(
                    "no signed head was given, so neither proof is tied to a root the log's key \
                     signed, and the order of the two leaves is the Forum's word. Pass --head and \
                     --log-jwks."
                        .to_string(),
                ));
            };
            match acta::verify_author(&entry, &proof, &key_entry, &key_proof, &head) {
                Ok(verified) => {
                    println!("author: {}", verified.author);
                    println!("kid: {}", verified.kid);
                    println!("alg: {}", verified.alg);
                    println!("key_index: {}", verified.key_index);
                    println!("post_index: {}", verified.post_index);
                    print_head("head", &head);
                    Ok(())
                }
                // R6.54's third outcome: the log carries no key for the post's kid
                // from before the post, and says nothing either way about it.
                Err(err) if err.not_established() => Err(CliError::NotAnchored(format!(
                    "{err}: the log carries no key for this post's kid from before the post, \
                     so which key signed it cannot be established from the log"
                ))),
                Err(err) => Err(CliError::Acta(err)),
            }
        }
        other => Err(CliError::Usage(format!("unknown log subcommand `{other}`"))),
    }
}

const LOG_FLAGS: [&str; 8] = [
    "--head",
    "--log-jwks",
```

In `rust/curia-testis/src/bin/curia-testis.rs`, replace:

```rust
    "--from-head",
    "--to-head",
];

```

with:

```rust
    "--from-head",
    "--to-head",
    "--key-entry",
    "--key-proof",
];

```

- [ ] **Step 4: Run the Rust gates**

```bash
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked 2>&1 | grep -E "^test result" | awk '{p+=$4; f+=$6} END {print "passed", p, "failed", f}'
```

Expected: `fmt` prints nothing; `clippy` finishes with no warning; `passed 220 failed 0`, across eighteen binaries — 211 in seventeen before, and `log_author.rs`'s nine.

The CI job's comment states that count, so it moves with it:

In `.github/workflows/ci.yml`, replace:

```yaml
        run: cargo clippy --all-targets --locked -- -D warnings

      # The independent verifier is the evidence behind Phase 1's exit criterion. Its 211
      # tests across 17 binaries are not a secondary suite.
      #
      # Note what the two steps above are: `cargo fmt --check` and `cargo clippy -- -D warnings`
```

with:

```yaml
        run: cargo clippy --all-targets --locked -- -D warnings

      # The independent verifier is the evidence behind Phase 1's exit criterion. Its 220
      # tests across 18 binaries are not a secondary suite.
      #
      # Note what the two steps above are: `cargo fmt --check` and `cargo clippy -- -D warnings`
```

- [ ] **Step 5: End to end, against the Forum's own documents**

The author's own binding verifies (exit 0); another identity's binding offered as the key's is refused, because it binds another identity's `kid` (exit 1, `curia/acta/binding-mismatch`); a pre-G16 identity is reported as not checked (exit 3), never as verified; and so is a pre-G16 identity whose key the log bound only after its post (exit 3, `curia/acta/bound-after-post`), never as failed. That last needs a binding no route appends for an enrolled identity, so the fixture appends it, with exactly the members `EnrollAgent` writes:

In `tests/Curia.Api.Tests/ForumFixture.cs`, replace:

```csharp
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
```

with:

```csharp
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Credentials;
```

In `tests/Curia.Api.Tests/ForumFixture.cs`, replace:

```csharp
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted: agent key registered with the Registrar")),
                ]))],
            ct));
    }

    private static string AdminConnectionString =>
```

with:

```csharp
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted: agent key registered with the Registrar")),
                ]))],
            ct));
    }

    /// <summary>
    /// Appends an <c>agent.key-bound</c> for <paramref name="agentId"/>'s <c>ES256</c> key at the end of
    /// its stream, now, through the host's own event store, with exactly the members
    /// <c>EnrollAgent</c> writes (R4.34). For an identity that has already posted, this is the binding
    /// that lands after its history: the one an identity enrolled before <c>agent.enrolled</c> existed
    /// receives when anyone re-announces its public key, and the one an operator's binding would append
    /// (the key-binding stage's spec, §2.1). No route appends it for an identity already enrolled.
    /// </summary>
    internal async Task BindKeyAfterItsPostsAsync(string agentId, string kid, byte[] publicKey, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var aggregate = Require(AggregateId.Create(agentId));
        var stream = Require(await store.ReadByAggregateAsync(aggregate, ct));
        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(
                Require(EventId.Create(Require(new UlidGenerator(Clock).Next()).ToString())),
                Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
                Require(ActorId.Create(agentId)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(kid)),
                    new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(new PublicKeyMaterial("ES256", kid, publicKey)))),
                ]))],
            ct));
    }

    private static string AdminConnectionString =>
```

And the fact:

In `tests/Curia.Api.Tests/ActaEndpointTests.cs`, replace:

```csharp

    private static string Scratch() => Directory.CreateTempSubdirectory("curia-acta-").FullName;

    [Fact]
```

with:

```csharp

    private static string Scratch() => Directory.CreateTempSubdirectory("curia-acta-").FullName;

    /// <summary>An agent, enrolled through the route or as before errata G16, that has asked one question; and that question's id.</summary>
    private async Task<(ForumAgent Agent, string PostId)> AskAsync(HttpClient http, string name, bool beforeKeyBinding, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{name}-{suffix}", $"{name}-{suffix}");
        if (beforeKeyBinding)
        {
            await forum.EnrollBeforeKeyBindingAsync(agent.AgentId, agent.Kid, agent.AssertionKey.ExportSubjectPublicKeyInfo(), ct);
        }
        else
        {
            using var enrolled = await agent.EnrollAsync(http, ct);
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        }

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using var posted = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion("board-" + suffix, "Whose key does the log say signed this?", "R6.54 " + suffix, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        return (agent, (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("post_id").GetString()!);
    }

    /// <summary>
    /// R6.54 (errata G16), offline: <c>curia-testis log author</c> establishes from served log documents
    /// alone -- the post's entry and proof, its key's binding entry and proof, one signed head -- that
    /// the post was signed by the key its author's enrollment bound, earlier in the log. No agent key
    /// set is handed to it. Three controls: another identity's binding offered as the key's is refused
    /// (it binds another identity's <c>kid</c>, exit 1); an identity enrolled before G16, whose log names
    /// its <c>kid</c> and no key, is reported as not checked (exit 3), never as verified; and so is a
    /// post whose author's key the log bound only after it (exit 3), never as failed.
    /// </summary>
    [Fact]
    public async Task R6_54_TestisEstablishesAuthorshipFromTheLogAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        var (author, postId) = await AskAsync(http, "author", beforeKeyBinding: false, ct);
        var (other, _) = await AskAsync(http, "other", beforeKeyBinding: false, ct);
        var (before, beforePostId) = await AskAsync(http, "before-g16", beforeKeyBinding: true, ct);
        var (late, latePostId) = await AskAsync(http, "bound-late", beforeKeyBinding: true, ct);
        await forum.BindKeyAfterItsPostsAsync(late.AgentId, late.Kid, late.AssertionKey.ExportSubjectPublicKeyInfo(), ct);
        var (exit, _, stderr) = await SignHeadAsync(ct);
        Assert.True(exit == ExitCode.Ok, stderr);

        async Task<long> KeyIndexAsync(ForumAgent agent) =>
            (await http.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agent.AgentId)}", ct))
                .GetProperty("keys")[0].TryGetProperty("curia_log_index", out var index)
                ? index.GetInt64()
                : throw new InvalidOperationException($"the key set names no leaf for {agent.AgentId}'s key");

        async Task<string> AuthorAsync(string post, ForumAgent keyOf, string label)
        {
            var postIndex = (await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{post}", ct)).GetProperty("log_index").GetInt64();
            var keyIndex = await KeyIndexAsync(keyOf);
            var args =
                $"log author --entry \"{await SaveAsync(http, $"/v1/log/entries/{postIndex}", dir, label + "-entry.json", ct)}\"" +
                $" --proof \"{await SaveAsync(http, $"/v1/log/proof/{postIndex}", dir, label + "-proof.json", ct)}\"" +
                $" --key-entry \"{await SaveAsync(http, $"/v1/log/entries/{keyIndex}", dir, label + "-key-entry.json", ct)}\"" +
                $" --key-proof \"{await SaveAsync(http, $"/v1/log/proof/{keyIndex}", dir, label + "-key-proof.json", ct)}\"" +
                $" --head \"{await SaveAsync(http, "/v1/log/head", dir, label + "-head.json", ct)}\"" +
                $" --log-jwks \"{await SaveAsync(http, "/v1/log/jwks", dir, label + "-log-jwks.json", ct)}\"";
            var (code, stdout, failure) = TestisBinary.Run(TestisBinary.Locate(), args);
            return $"exit {code}: {stdout}{failure}";
        }

        var verified = await AuthorAsync(postId, author, "own");
        Assert.StartsWith("exit 0:", verified, StringComparison.Ordinal);
        Assert.Contains($"author: {author.AgentId}", verified, StringComparison.Ordinal);
        Assert.Contains($"key_index: {await KeyIndexAsync(author)}", verified, StringComparison.Ordinal);

        var refused = await AuthorAsync(postId, other, "other");
        Assert.StartsWith("exit 1:", refused, StringComparison.Ordinal);
        Assert.Contains("curia/acta/binding-mismatch", refused, StringComparison.Ordinal);

        var notChecked = await AuthorAsync(beforePostId, before, "before");
        Assert.StartsWith("exit 3:", notChecked, StringComparison.Ordinal);
        Assert.Contains("carries no key", notChecked, StringComparison.Ordinal);

        var boundLate = await AuthorAsync(latePostId, late, "late");
        Assert.StartsWith("exit 3:", boundLate, StringComparison.Ordinal);
        Assert.Contains("curia/acta/bound-after-post", boundLate, StringComparison.Ordinal);
    }

    [Fact]
```

```bash
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~ActaEndpointTests" 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `Passed!  - Failed:     0, Passed:     7, …`. Before the verb existed the new fact failed at its first `log author`: the binary answered exit 2, `unknown log subcommand`.

- [ ] **Step 6: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'curia-testis log author: authorship from the log alone (R6.54)\n\nTwo entries, two proofs, one signed head, and the post verified under the key\nits binding carries, bound before it. No agent key set is read. Exit 3 for a\nhead not given, and where the log carries no key for the post from before it:\nan enrollment that names the kid alone, or a binding after the post.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 8: The reference client checks the binding (R6.54)

**Files:**
- Modify: `src/Curia.Client/ForumDocuments.cs`, `src/Curia.Client/ActaCheck.cs`, `src/Curia.Client/PostVerifier.cs`, `src/Curia.Client.Cli/Program.cs`, `src/Curia.Mcp/ToolText.cs`
- Modify (tests): `tests/Shared/StubLog.cs`, `tests/Curia.Client.Tests/PostVerifierTests.cs`, `tests/Curia.Mcp.Tests/PropertyP22ToolResultTests.cs`, `tests/Curia.Api.Tests/StubFidelityTests.cs`

**Interfaces:**
- Produces:
  - `ForumJwk` gains `long? LogIndex = null`, read from `curia_log_index`.
  - `ActaCheck.KeyBinding(LogEntryDocument keyEntry, InclusionProofDocument keyProof, ProvenancePost post, long postIndex) : Check` — pure, no I/O; `ActaCheck.Inclusion` and it share one private `ProofHolds`.
  - `PostVerification` gains `Check KeyBinding` (fourth positional member) and renders it as the `key` line; `Overall` is *verified* only when signature, inclusion and key binding all are.
  - `StubLog(treeSize, postIndex, keyIndex = 0)`, with `KeyIndex`, `KeyEntry`, `KeySetNamesNoLogIndex`, `BindAnotherKey()`, `EnrollBeforeKeyBinding()` and `BindToAnotherAgent()`.
- Consumes: `curia_log_index` (Task 5), the binding's shape (Task 4).

**The key set only says where to look.** The check proves the leaf the key set names under the head this client verified, and checks the post under the key that leaf carries. A key set that names no leaf makes the check impossible (*could not be checked*); one that names a leaf binding another agent or another `kid` makes it fail; neither makes it pass. And the author's own binding at a leaf after the post is the log's silence about the key the post was accepted under, not a contradiction of it: *could not be checked*, never *failed* (the spec's Decision 11, amended).

- [ ] **Step 1: Give the stub a real binding, and the knobs that break it**

Every document it serves is still assembled by the computations the Forum uses: the binding's JWK by `PublicJwk.Of`. Its key set gains the `curia_not_before` the Forum has always served, and Step 7 holds it to the Forum's.

In `tests/Shared/StubLog.cs`, replace:

```csharp
    private StubHandler? _handler;

    internal StubLog(int treeSize = 5, int postIndex = 2)
    {
        _root = Directory.CreateTempSubdirectory("curia-stub-log-").FullName;
```

with:

```csharp
    private StubHandler? _handler;

    internal StubLog(int treeSize = 5, int postIndex = 2, int keyIndex = 0)
    {
        _root = Directory.CreateTempSubdirectory("curia-stub-log-").FullName;
```

In `tests/Shared/StubLog.cs`, replace:

```csharp
            AnswerPostId, "answer", PostId, Encoding.UTF8.GetString(Answer.Canonical.Span), Answer.Signature, Answer.PrefixedDigest);

        // The post's leaf is real; the rest are filler, distinct and in no particular relation to
        // it. A verifier only ever fetches the entry it is proving, so the others need only exist.
        var leaves = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(treeSize);
        for (var i = 0; i < treeSize; i++)
```

with:

```csharp
            AnswerPostId, "answer", PostId, Encoding.UTF8.GetString(Answer.Canonical.Span), Answer.Signature, Answer.PrefixedDigest);

        // R4.34's binding of alice's key, as an enrollment appends it: the public JWK the Forum's key
        // set publishes, rendered by the same PublicJwk.Of the Forum uses. By default it sits before the
        // post, as every binding the Forum writes does; a later index is the bound-after-post case.
        if (keyIndex == postIndex || keyIndex == AnswerIndex || keyIndex < 0 || keyIndex >= treeSize)
            throw new ArgumentOutOfRangeException(nameof(keyIndex), "the key's binding needs a leaf of its own");

        KeyIndex = keyIndex;
        KeyEntry = BuildKeyEntry(PublicJwk.Of(new PublicKeyMaterial("ES256", AgentKid, PublicSubjectPublicKeyInfo()))
            .TryGetValue(out var jwk, out _)
            ? jwk!
            : throw new InvalidOperationException("the stub's key has no public JWK"));

        // The post's leaf is real, and so is its key's binding; the rest are filler, distinct and in
        // no particular relation to either. A verifier only ever fetches the entries it is proving, so
        // the others need only exist.
        var leaves = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(treeSize);
        for (var i = 0; i < treeSize; i++)
```

In `tests/Shared/StubLog.cs`, replace:

```csharp
                : i == AnswerIndex
                    ? LeafOf(AnswerEntry)
                    : MerkleTree.LeafHash(Encoding.UTF8.GetBytes($"filler-leaf-{i}")));
        }

```

with:

```csharp
                : i == AnswerIndex
                    ? LeafOf(AnswerEntry)
                    : i == KeyIndex
                        ? LeafOf(KeyEntry)
                        : MerkleTree.LeafHash(Encoding.UTF8.GetBytes($"filler-leaf-{i}")));
        }

```

In `tests/Shared/StubLog.cs`, replace:

```csharp
    internal JsonValue.Object Entry { get; private set; }

    /// <summary>The size the served head covers. Set below the post's index to strand it (R6.48).</summary>
    internal int HeadTreeSize { get; set; }
```

with:

```csharp
    internal JsonValue.Object Entry { get; private set; }

    /// <summary>The leaf that binds alice's key (R4.34), and the one the key set names for it (R6.54).</summary>
    internal int KeyIndex { get; }

    /// <summary>R6.46's six members for alice's <c>agent.key-bound</c>.</summary>
    internal JsonValue.Object KeyEntry { get; private set; }

    /// <summary>The size the served head covers. Set below the post's index to strand it (R6.48).</summary>
    internal int HeadTreeSize { get; set; }
```

In `tests/Shared/StubLog.cs`, replace:

```csharp
    /// <summary>Answer <c>/v1/jwks</c> with a transport failure: the "could not check" case for R6.52's first check.</summary>
    internal bool JwksUnreachable { get; set; }

    /// <summary>Answer <c>/v1/log/head</c> with 404 <c>curia/log/no-head</c>: a log whose operator has never signed.</summary>
```

with:

```csharp
    /// <summary>Answer <c>/v1/jwks</c> with a transport failure: the "could not check" case for R6.52's first check.</summary>
    internal bool JwksUnreachable { get; set; }

    /// <summary>Serve the key set with no <c>curia_log_index</c>: R6.54's check has nowhere to look.</summary>
    internal bool KeySetNamesNoLogIndex { get; set; }

    /// <summary>
    /// Bind another key under alice's <c>kid</c>, and rebuild the tree around it, so the binding is
    /// proven and the post does not verify under what it carries: a key set that served one key while
    /// the log bound another, or a log that bound a substitute.
    /// </summary>
    internal void BindAnotherKey()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        KeyEntry = BuildKeyEntry(PublicJwk.Of(new PublicKeyMaterial("ES256", AgentKid, other.ExportSubjectPublicKeyInfo()))
            .TryGetValue(out var jwk, out _)
            ? jwk!
            : throw new InvalidOperationException("the substitute key has no public JWK"));
        RebuildKey();
    }

    /// <summary>
    /// Replace the binding with a pre-R4.34 <c>agent.enrolled</c> naming alice's <c>kid</c> and no key,
    /// as an identity enrolled before errata G16 has, and rebuild the tree around it.
    /// </summary>
    internal void EnrollBeforeKeyBinding()
    {
        KeyEntry = new JsonValue.Object(
        [
            new(LogLeaf.ActorIdMember, new JsonValue.String(Author)),
            new(LogLeaf.AggregateIdMember, new JsonValue.String(Author)),
            new(LogLeaf.EventIdMember, new JsonValue.String("01TESTENROLLMENT0000000000")),
            new(LogLeaf.EventTypeMember, new JsonValue.String("agent.enrolled")),
            new(LogLeaf.PayloadMember, new JsonValue.Object(
            [
                new("agent_id", new JsonValue.String(Author)),
                new("kid", new JsonValue.String(AgentKid)),
                new("reason", new JsonValue.String("Enrollment accepted: agent key registered with the Registrar")),
            ])),
            new(LogLeaf.ServerTimestampMember, new JsonValue.String("1970-01-01T00:00:00.000000Z")),
        ]);
        RebuildKey();
    }

    /// <summary>
    /// Replace the binding with another agent's <c>agent.key-bound</c>, in that agent's stream, carrying
    /// alice's own key under alice's <c>kid</c>, and rebuild the tree around it: a key set that points
    /// alice's key at a leaf binding it to someone else. The key is the one that signed, so the
    /// signature verifies under what the leaf carries, and only comparing the binding with the post's
    /// author refuses it.
    /// </summary>
    internal void BindToAnotherAgent()
    {
        KeyEntry = BuildKeyEntry(
            PublicJwk.Of(new PublicKeyMaterial("ES256", AgentKid, PublicSubjectPublicKeyInfo()))
                .TryGetValue(out var jwk, out _)
                ? jwk!
                : throw new InvalidOperationException("the stub's key has no public JWK"),
            "https://agents.example/mallory");
        RebuildKey();
    }

    /// <summary>Answer <c>/v1/log/head</c> with 404 <c>curia/log/no-head</c>: a log whose operator has never signed.</summary>
```

In `tests/Shared/StubLog.cs`, replace:

```csharp

        var p = PublicSigningParameters();
        return $$"""
        {"keys":[{"kty":"EC","crv":"P-256","alg":"ES256","kid":"{{AgentKid}}",
        "x":"{{Base64Url.EncodeToString(p.Q.X!)}}","y":"{{Base64Url.EncodeToString(p.Q.Y!)}}"}]}
        """.ReplaceLineEndings(string.Empty);
    }
```

with:

```csharp

        var p = PublicSigningParameters();
        var logIndex = KeySetNamesNoLogIndex ? string.Empty : $$""","curia_log_index":{{KeyIndex}}""";
        return $$"""
        {"keys":[{"kty":"EC","crv":"P-256","alg":"ES256","kid":"{{AgentKid}}",
        "x":"{{Base64Url.EncodeToString(p.Q.X!)}}","y":"{{Base64Url.EncodeToString(p.Q.Y!)}}",
        "curia_not_before":"1970-01-01T00:00:00.0000000+00:00"{{logIndex}}}]}
        """.ReplaceLineEndings(string.Empty);
    }
```

In `tests/Shared/StubLog.cs`, replace:

```csharp
    internal string EntryJson(int index)
    {
        var entry = index == PostIndex ? Entry : index == AnswerIndex ? AnswerEntry : null;
        var body = entry is null
            ? "{\"actor_id\":null,\"aggregate_id\":\"x\",\"event_id\":\"x\",\"event_type\":\"filler\",\"payload\":{},\"server_ts\":\"1970-01-01T00:00:00.000000Z\"}"
```

with:

```csharp
    internal string EntryJson(int index)
    {
        var entry = index == PostIndex ? Entry : index == AnswerIndex ? AnswerEntry : index == KeyIndex ? KeyEntry : null;
        var body = entry is null
            ? "{\"actor_id\":null,\"aggregate_id\":\"x\",\"event_id\":\"x\",\"event_type\":\"filler\",\"payload\":{},\"server_ts\":\"1970-01-01T00:00:00.000000Z\"}"
```

In `tests/Shared/StubLog.cs`, replace:

```csharp
        Leaves = leaves.ToImmutable();
    }

    private static ImmutableArray<byte> LeafOf(JsonValue.Object entry) =>
```

with:

```csharp
        Leaves = leaves.ToImmutable();
    }

    private void RebuildKey()
    {
        var leaves = Leaves.ToBuilder();
        leaves[KeyIndex] = LeafOf(KeyEntry);
        Leaves = leaves.ToImmutable();
    }

    /// <summary>R6.46's six members, as an enrollment appends <paramref name="agent"/>'s <c>agent.key-bound</c> (R4.34): alice's, unless another is named.</summary>
    private static JsonValue.Object BuildKeyEntry(JsonValue.Object jwk, string agent = Author) => new(
    [
        new(LogLeaf.ActorIdMember, new JsonValue.String(agent)),
        new(LogLeaf.AggregateIdMember, new JsonValue.String(agent)),
        new(LogLeaf.EventIdMember, new JsonValue.String("01TESTKEYBINDING0000000000")),
        new(LogLeaf.EventTypeMember, new JsonValue.String("agent.key-bound")),
        new(LogLeaf.PayloadMember, new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("kid", new JsonValue.String(AgentKid)),
            new("jwk", jwk),
        ])),
        new(LogLeaf.ServerTimestampMember, new JsonValue.String("1970-01-01T00:00:00.000000Z")),
    ]);

    private static ImmutableArray<byte> LeafOf(JsonValue.Object entry) =>
```

In `tests/Shared/StubLog.cs`, replace:

```csharp
    }

}
```

with:

```csharp
    }

    /// <summary>The registered key as R4.28 stores an <c>ES256</c> key: its DER SubjectPublicKeyInfo.</summary>
    private byte[] PublicSubjectPublicKeyInfo()
    {
        using var key = _agent.ExportPublicKey();
        return key.ExportSubjectPublicKeyInfo();
    }

}
```

- [ ] **Step 2: Write the failing tests**

In `tests/Curia.Client.Tests/PostVerifierTests.cs`, replace:

```csharp
    }

    private static string ForkRoot(StubLog fork, int treeSize) =>
        Curia.Domain.Acta.LogEntries.Prefixed(fork.RootAt(treeSize));

    private StubLog Log(int treeSize = 5, int postIndex = 2)
    {
        var log = new StubLog(treeSize, postIndex);
        _logs.Add(log);
        return log;
```

with:

```csharp
    }

    /// <summary>
    /// R6.54 (errata G16), the intact case: the key the post names is bound to its author at leaf 0,
    /// before the post at leaf 2, under the head this client verified, and the post verifies under
    /// the key that leaf carries. Asserted on its own for the reason the intact R6.52 case is: every
    /// falsification below changes one thing, and a fixture whose binding did not verify would report
    /// each as caught while catching nothing.
    /// </summary>
    [Fact]
    public async Task R6_54_TheSigningKeyIsTheKeyTheLogBoundBeforeThePost()
    {
        var result = await VerifyAsync(Log());

        Assert.Equal(CheckOutcome.Verified, result.KeyBinding.Outcome);
        Assert.Contains("bound to https://agents.example/alice at leaf 0, before this post at leaf 2", result.KeyBinding.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Verified, result.Overall);
    }

    /// <summary>
    /// A binding the log proves, of another key under the author's <c>kid</c>. The key set still serves
    /// the author's real key, so the signature check verifies; only the check against the key the log
    /// carries can see that the log bound something else. This is the substitution R6.54 exists for,
    /// seen from the other side.
    /// </summary>
    [Fact]
    public async Task R6_54_ALogThatBoundAnotherKeyFailsTheBindingThoughTheSignatureVerifies()
    {
        var log = Log();
        log.BindAnotherKey();

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Failed, result.KeyBinding.Outcome);
        Assert.Contains("does not verify under the key the log bound", result.KeyBinding.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);
    }

    /// <summary>
    /// The author's own key, bound only after the post. The log says nothing about which key was the
    /// author's when the post was accepted, so the post is not established, and not failed either: an
    /// identity whose first binding came after its history -- it enrolled before the log recorded
    /// enrollments, and anyone re-announced its public key -- would otherwise read as forged
    /// throughout, while a forger avoids this case by binding first.
    /// </summary>
    [Fact]
    public async Task R6_54_AKeyBoundAfterThePostIsNotEstablished()
    {
        var result = await VerifyAsync(Log(treeSize: 6, postIndex: 2, keyIndex: 4));

        Assert.Equal(CheckOutcome.CouldNotCheck, result.KeyBinding.Outcome);
        Assert.Contains("at leaf 4, which is not before this post at leaf 2", result.KeyBinding.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    /// <summary>
    /// A binding the log proves, before the post, of the post's own key under its own <c>kid</c> -- to
    /// another agent. The signature verifies under the key the leaf carries, so only the comparison of
    /// the binding with the post's author refuses it; without that, a key set could point a reader at
    /// anyone's binding of the key and be believed.
    /// </summary>
    [Fact]
    public async Task R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey()
    {
        var log = Log();
        log.BindToAnotherAgent();

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Failed, result.KeyBinding.Outcome);
        Assert.Contains("entry for https://agents.example/mallory", result.KeyBinding.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);
    }

    /// <summary>
    /// An identity enrolled before R4.34: the log names its <c>kid</c> and carries no key. Signature and
    /// inclusion both verify, and the post is still not established -- the one outcome R6.54 adds that
    /// a reader must not read as either of the others.
    /// </summary>
    [Fact]
    public async Task R6_54_AKeyTheLogNamesByKidAloneIsNotEstablished()
    {
        var log = Log();
        log.EnrollBeforeKeyBinding();

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.KeyBinding.Outcome);
        Assert.Contains("carries no key", result.KeyBinding.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    /// <summary>A key set that names no leaf for the key leaves the check nowhere to look: not checked, never verified.</summary>
    [Fact]
    public async Task R6_54_AKeySetNamingNoLeafCannotBeChecked()
    {
        var log = Log();
        log.KeySetNamesNoLogIndex = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.KeyBinding.Outcome);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    /// <summary>
    /// R6.54's "under the same signed head as the post's own proof". The binding is intact, before the
    /// post, and carries the key that signed; the head this client verified is signed with the log's
    /// own key over a root that is not this tree's. The key's proof climbs to the tree's real root, so
    /// only comparing that root with the head's refuses it: a check that held the proof to its own
    /// root would call the binding verified under a head that does not contain it.
    /// </summary>
    [Fact]
    public async Task R6_54_AKeyBindingProvenUnderAnotherRootFails()
    {
        var log = Log();
        log.HeadCommitsToTheWrongRoot = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.KeyBinding.Outcome);
        Assert.Contains("the signed head's root is", result.KeyBinding.Detail, StringComparison.Ordinal);
    }

    private static string ForkRoot(StubLog fork, int treeSize) =>
        Curia.Domain.Acta.LogEntries.Prefixed(fork.RootAt(treeSize));

    private StubLog Log(int treeSize = 5, int postIndex = 2, int keyIndex = 0)
    {
        var log = new StubLog(treeSize, postIndex, keyIndex);
        _logs.Add(log);
        return log;
```

And the adapter: R11.29, as errata G16 cross-references it, has `curia_verify` report R6.54's check on a line of its own, and nothing yet holds it to that:

In `tests/Curia.Mcp.Tests/PropertyP22ToolResultTests.cs`, replace:

```csharp
        Assert.Contains("verified:", text, StringComparison.Ordinal);
        Assert.Contains("COULD NOT BE CHECKED:", text, StringComparison.Ordinal);
    }
```

with:

```csharp
        Assert.Contains("verified:", text, StringComparison.Ordinal);
        Assert.Contains("COULD NOT BE CHECKED:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.29 as errata G16 cross-references it: <c>curia_verify</c> performs R6.54's check beside
    /// R6.52's three and reports it on a line of its own. The overall verdict needs that check, so a
    /// result without the line would state a verdict none of its lines explains. Against the intact
    /// stub, whose head covers the post and the key's binding, the check runs and holds.
    /// </summary>
    [Fact]
    public async Task R6_54_TheVerifyToolReportsTheKeyCheckSeparately()
    {
        var text = Flatten(await Verify());

        Assert.Contains("key         verified: ", text, StringComparison.Ordinal);
    }
```

```bash
dotnet build tests/Curia.Client.Tests -c Release --nologo 2>&1 | grep -E "error CS" | sed -E 's/\[.*//' | sort -u | head -3
```

Expected: `error CS1061: 'PostVerification' does not contain a definition for 'KeyBinding'`.

- [ ] **Step 3: The key's log position, as the client reads it**

In `src/Curia.Client/ForumDocuments.cs`, replace:

```csharp
/// most of the archive.
/// </remarks>
public sealed record ForumJwk(
    string Kty, string? Crv, string Alg, string Kid, string X, string? Y,
    string? NotBefore, string? NotAfter);

/// <summary>§10.7's contract as served, clause by clause.</summary>
```

with:

```csharp
/// most of the archive.
/// </remarks>
/// <param name="LogIndex">
/// The leaf that binds the key to its agent (<c>curia_log_index</c>, R6.54, errata G16), where the
/// key set names one: where a reader goes to check the key without trusting this key set. Null when
/// absent, which the check reports as could-not-be-checked.
/// </param>
public sealed record ForumJwk(
    string Kty, string? Crv, string Alg, string Kid, string X, string? Y,
    string? NotBefore, string? NotAfter, long? LogIndex = null);

/// <summary>§10.7's contract as served, clause by clause.</summary>
```

In `src/Curia.Client/ForumDocuments.cs`, replace:

```csharp
            ? new ForumJwk(
                kty, ClientJson.String(k, "crv"), alg, kid, x, ClientJson.String(k, "y"),
                ClientJson.String(k, "curia_not_before"), ClientJson.String(k, "curia_not_after"))
            : null;

```

with:

```csharp
            ? new ForumJwk(
                kty, ClientJson.String(k, "crv"), alg, kid, x, ClientJson.String(k, "y"),
                ClientJson.String(k, "curia_not_before"), ClientJson.String(k, "curia_not_after"),
                ClientJson.WholeNumber(k, "curia_log_index"))
            : null;

```

- [ ] **Step 4: The check, as a pure predicate**

In `src/Curia.Client/ActaCheck.cs`, replace:

```csharp
                "some other leaf (R11.29)");

        if (!RecomputeLeaf(entry.Entry).TryGetValue(out var leaf, out var error))
            return Check.Failed($"the entry has no canonical form: {error!.Type}");
```

with:

```csharp
                "some other leaf (R11.29)");

        return ProofHolds(entry, proof);
    }

    /// <summary>
    /// R6.54 (errata G16): the key that signed <paramref name="post"/> is the key the log bound to
    /// its author, at a leaf before the post's -- checked against the key the binding entry carries,
    /// never against a key a key set serves.
    ///
    /// <para><b>What each outcome means.</b> <i>Verified</i>: <paramref name="keyEntry"/> is an
    /// <c>agent.key-bound</c> entry the proof carries to its root, it binds the post's <c>kid</c> to
    /// the post's author, it sits before <paramref name="postIndex"/>, and the post's signature
    /// verifies under the key it carries. <i>Could not be checked</i>: the entry is the author's
    /// binding of the post's <c>kid</c> and the log holds no key for it from before the post --
    /// the binding sits at or after <paramref name="postIndex"/>, or it is the author's
    /// <c>agent.enrolled</c>, which names the <c>kid</c> and carries no key: all an identity enrolled
    /// before R4.34 has, and a reader cannot tell when one was made. That is the log's
    /// silence about the key the post was accepted under, not a contradiction of it: a forger binds
    /// first at no cost, and what lands here is history older than its binding. <i>Failed</i>:
    /// anything else -- a proof that does not hold, an entry for another agent or another
    /// <c>kid</c>, an entry that binds no key, or a signature that does not verify under the bound
    /// key -- because each is the served material disagreeing with itself. The absent-material cases
    /// are decided above this method, where the fetching happens.</para>
    /// </summary>
    public static Check KeyBinding(LogEntryDocument keyEntry, InclusionProofDocument keyProof, ProvenancePost post, long postIndex)
    {
        ArgumentNullException.ThrowIfNull(keyEntry);
        ArgumentNullException.ThrowIfNull(keyProof);
        ArgumentNullException.ThrowIfNull(post);

        if (keyEntry.LogIndex != keyProof.LogIndex)
            return Check.Failed(Text(
                $"the key's entry is leaf {keyEntry.LogIndex} and its proof is about leaf {keyProof.LogIndex}"));

        var proven = ProofHolds(keyEntry, keyProof);
        if (proven.Outcome is not CheckOutcome.Verified) return proven;

        var author = post.Provenance.Author;
        var signed = SignatureCheck.Verify(post, []).Kid;
        var type = ClientJson.String(keyEntry.Entry, LogLeaf.EventTypeMember);
        var aggregate = ClientJson.String(keyEntry.Entry, LogLeaf.AggregateIdMember);
        var payload = ClientJson.Object(keyEntry.Entry, LogLeaf.PayloadMember);
        var boundAgent = payload is null ? null : ClientJson.String(payload, "agent_id");
        var boundKid = payload is null ? null : ClientJson.String(payload, "kid");

        if (!string.Equals(aggregate, author, StringComparison.Ordinal)
            || !string.Equals(boundAgent, author, StringComparison.Ordinal)
            || !string.Equals(boundKid, signed, StringComparison.Ordinal))
            return Check.Failed(Text(
                $"leaf {keyEntry.LogIndex} is a {type ?? "(untyped)"} entry for {boundAgent ?? "(no agent)"} kid={boundKid ?? "(none)"}, and the post is {author}'s under kid={signed ?? "(unreadable)"}"));

        var enrolled = string.Equals(type, "agent.enrolled", StringComparison.Ordinal);
        if (!enrolled && !string.Equals(type, "agent.key-bound", StringComparison.Ordinal))
            return Check.Failed(Text($"leaf {keyEntry.LogIndex} is a {type ?? "(untyped)"} entry, not a key binding"));

        if (keyEntry.LogIndex >= postIndex)
            return Check.CouldNotCheck(Text(
                $"kid={boundKid} is bound to {author} at leaf {keyEntry.LogIndex}, which is not before this post at leaf {postIndex}, so the log holds no key for it from before the post"));

        if (enrolled)
            return Check.CouldNotCheck(Text(
                $"leaf {keyEntry.LogIndex} is {author}'s enrollment naming kid={boundKid}, which carries no key (all an identity enrolled before R4.34 has), so which key signed cannot be established from the log"));

        if ((payload is null ? null : ClientJson.Object(payload, "jwk")) is not { } jwk
            || ForumDocuments.ReadJwk(jwk) is not { } bound
            || !string.Equals(bound.Kid, boundKid, StringComparison.Ordinal))
            return Check.Failed(Text($"leaf {keyEntry.LogIndex} binds kid={boundKid} and carries no usable key under it"));

        var verdict = SignatureCheck.Verify(post, [bound]);
        return verdict.Verified
            ? Check.Verified(Text(
                $"kid={boundKid} is the key the log bound to {author} at leaf {keyEntry.LogIndex}, before this post at leaf {postIndex}, and the post verifies under the key that leaf carries"))
            : Check.Failed(Text(
                $"the post does not verify under the key the log bound to {author} at leaf {keyEntry.LogIndex}: {verdict.Detail}"));
    }

    /// <summary>
    /// R6.48's audit path, over a leaf recomputed from <paramref name="entry"/>: the leaf the proof
    /// and the entry route each state must be the recomputed one, and the path must carry it to the
    /// proof's root. Shared by the post's check and the key's, so the two cannot come apart.
    /// </summary>
    private static Check ProofHolds(LogEntryDocument entry, InclusionProofDocument proof)
    {
        if (!RecomputeLeaf(entry.Entry).TryGetValue(out var leaf, out var error))
            return Check.Failed($"the entry has no canonical form: {error!.Type}");
```

- [ ] **Step 5: The verifier runs it, and the verdict needs it**

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp

/// <summary>
/// R6.52's three checks over one served post, each reported separately.
/// </summary>
/// <param name="Digest">
```

with:

```csharp

/// <summary>
/// R6.52's three checks over one served post, and R6.54's fourth (errata G16), each reported
/// separately.
/// </summary>
/// <param name="Digest">
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
/// was reached, which is why <see cref="Inclusion"/> then reports that it could not be checked.
/// </param>
public sealed record PostVerification(
    string PostId,
```

with:

```csharp
/// was reached, which is why <see cref="Inclusion"/> then reports that it could not be checked.
/// </param>
/// <param name="KeyBinding">
/// R6.54: whether the key that signed the post is the key the log bound to its author before the
/// post, checked under the key the binding entry carries rather than one a key set serves.
/// </param>
public sealed record PostVerification(
    string PostId,
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
    Check Inclusion,
    Check Consistency,
    string? Digest,
    long? LogIndex,
```

with:

```csharp
    Check Inclusion,
    Check Consistency,
    Check KeyBinding,
    string? Digest,
    long? LogIndex,
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
{
    /// <summary>
    /// One word for the three, for a caller that wants a single answer.
    ///
    /// <para><b>Any failure is a failure.</b> A post whose signature verifies and whose inclusion
    /// proof does not is not partly authentic.</para>
    ///
    /// <para><b>Consistency is conditional and its absence does not deny the rest.</b> R6.52 asks
```

with:

```csharp
{
    /// <summary>
    /// One word for the four, for a caller that wants a single answer.
    ///
    /// <para><b>Any failure is a failure.</b> A post whose signature verifies and whose inclusion
    /// proof does not is not partly authentic.</para>
    ///
    /// <para><b>Verified needs the key's binding as well as the signature (R6.54).</b> The signature
    /// check verifies under a key the Forum's key set serves; the binding check is what shows that key
    /// is the one the log bound to the author before the post. Without it a Forum that substituted a
    /// key, and signed under it, would be verified by every reader. So a post whose key the log names
    /// only by <c>kid</c> -- an identity enrolled before R4.34 -- is not established.</para>
    ///
    /// <para><b>Consistency is conditional and its absence does not deny the rest.</b> R6.52 asks
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
        || Inclusion.Outcome is CheckOutcome.Failed
        || Consistency.Outcome is CheckOutcome.Failed
            ? CheckOutcome.Failed
            : Signature.Outcome is CheckOutcome.Verified && Inclusion.Outcome is CheckOutcome.Verified
                ? CheckOutcome.Verified
                : CheckOutcome.CouldNotCheck;

    /// <summary>
    /// The three checks as three lines, plus the summary.
    ///
    /// <para><b>No agent-authored content appears here, and none may.</b> This renders verdicts
```

with:

```csharp
        || Inclusion.Outcome is CheckOutcome.Failed
        || Consistency.Outcome is CheckOutcome.Failed
        || KeyBinding.Outcome is CheckOutcome.Failed
            ? CheckOutcome.Failed
            : Signature.Outcome is CheckOutcome.Verified
              && Inclusion.Outcome is CheckOutcome.Verified
              && KeyBinding.Outcome is CheckOutcome.Verified
                ? CheckOutcome.Verified
                : CheckOutcome.CouldNotCheck;

    /// <summary>
    /// The four checks as four lines, plus the summary.
    ///
    /// <para><b>No agent-authored content appears here, and none may.</b> This renders verdicts
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
        builder.Append(culture, $"inclusion   {Inclusion.Describe}\n");
        builder.Append(culture, $"consistency {Consistency.Describe}\n");
        builder.Append('\n');

```

with:

```csharp
        builder.Append(culture, $"inclusion   {Inclusion.Describe}\n");
        builder.Append(culture, $"consistency {Consistency.Describe}\n");
        builder.Append(culture, $"key         {KeyBinding.Describe}\n");
        builder.Append('\n');

```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
            CheckOutcome.Verified =>
                "VERIFIED. This client checked the signature over bytes it re-canonicalized itself, "
                + "and recomputed the log leaf from the log's own entry rather than accepting the "
                + "digest the Forum served for it.\n",
            CheckOutcome.Failed =>
                "FAILED. At least one check ran and did not hold. Read the failing line above: it "
```

with:

```csharp
            CheckOutcome.Verified =>
                "VERIFIED. This client checked the signature over bytes it re-canonicalized itself, "
                + "recomputed the log leaf from the log's own entry rather than accepting the "
                + "digest the Forum served for it, and found the signing key bound to the author in "
                + "the log before the post.\n",
            CheckOutcome.Failed =>
                "FAILED. At least one check ran and did not hold. Read the failing line above: it "
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp

/// <summary>
/// R11.29's verification: the three checks of R6.52 run against a post a read has already served.
///
/// <para><b>The subject is a served post, deliberately.</b> Verification is a claim about the thing
```

with:

```csharp

/// <summary>
/// R11.29's verification: the three checks of R6.52, and R6.54's fourth, run against a post a read
/// has already served.
///
/// <para><b>The subject is a served post, deliberately.</b> Verification is a claim about the thing
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp

    /// <summary>
    /// The three checks against a post the caller already holds -- the same post object a read
    /// returned, so no second fetch can substitute a different document for the one being verified.
    /// </summary>
```

with:

```csharp

    /// <summary>
    /// The four checks against a post the caller already holds -- the same post object a read
    /// returned, so no second fetch can substitute a different document for the one being verified.
    /// </summary>
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
        ArgumentNullException.ThrowIfNull(post);

        var (signature, digest) = await SignatureAsync(post, ct).ConfigureAwait(false);

        var head = await AnchorAsync(ct).ConfigureAwait(false);
        var inclusion = await InclusionAsync(post, head, ct).ConfigureAwait(false);
        var consistency = await ConsistencyAsync(head, ct).ConfigureAwait(false);

        return new PostVerification(
```

with:

```csharp
        ArgumentNullException.ThrowIfNull(post);

        var (signature, digest, signingKey) = await SignatureAsync(post, ct).ConfigureAwait(false);

        var head = await AnchorAsync(ct).ConfigureAwait(false);
        var inclusion = await InclusionAsync(post, head, ct).ConfigureAwait(false);
        var consistency = await ConsistencyAsync(head, ct).ConfigureAwait(false);
        var keyBinding = await KeyBindingAsync(post, head, signingKey, ct).ConfigureAwait(false);

        return new PostVerification(
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
            inclusion,
            consistency,
            digest,
            post.LogIndex,
```

with:

```csharp
            inclusion,
            consistency,
            keyBinding,
            digest,
            post.LogIndex,
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
    /// <summary>
    /// R6.52's first check, and the one place the JWKS collapse is closed: a key set this client
    /// could not fetch is reported as unfetched, never as a key that does not exist.
    /// </summary>
    private async Task<(Check Check, string? Digest)> SignatureAsync(ProvenancePost post, CancellationToken ct)
    {
        // Canonicalized once, whatever happens to the key set: the digest is a fact about the
```

with:

```csharp
    /// <summary>
    /// R6.52's first check, and the one place the JWKS collapse is closed: a key set this client
    /// could not fetch is reported as unfetched, never as a key that does not exist. Also returns the
    /// served key the signature names, whose <c>curia_log_index</c> is where R6.54's check looks.
    /// </summary>
    private async Task<(Check Check, string? Digest, ForumJwk? SigningKey)> SignatureAsync(ProvenancePost post, CancellationToken ct)
    {
        // Canonicalized once, whatever happens to the key set: the digest is a fact about the
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
        if (string.IsNullOrEmpty(author))
            return (Check.CouldNotCheck(
                "the provenance envelope names no author, so no key set can be fetched"), unkeyed.PrefixedDigest);

        var fetched = await _forum.GetJwksAsync(author, ct).ConfigureAwait(false);
```

with:

```csharp
        if (string.IsNullOrEmpty(author))
            return (Check.CouldNotCheck(
                "the provenance envelope names no author, so no key set can be fetched"), unkeyed.PrefixedDigest, null);

        var fetched = await _forum.GetJwksAsync(author, ct).ConfigureAwait(false);
```

In `src/Curia.Client/PostVerifier.cs`, replace:

```csharp
            return (Check.CouldNotCheck(
                $"the author's key set could not be fetched ({refusal!.Error.Type}): {refusal.Summary}. "
                + "This is a fault reaching the keys, not a statement about the signature."), unkeyed.PrefixedDigest);

        if (keys.IsDefaultOrEmpty)
            return (Check.CouldNotCheck($"the Forum published no keys at all for {author}"), unkeyed.PrefixedDigest);

        var verdict = SignatureCheck.Verify(post, keys);
        return (verdict.Verified
            ? Check.Verified(verdict.Detail + $" (kid={verdict.Kid})")
            : Check.Failed(verdict.Detail), verdict.PrefixedDigest);
    }

```

with:

```csharp
            return (Check.CouldNotCheck(
                $"the author's key set could not be fetched ({refusal!.Error.Type}): {refusal.Summary}. "
                + "This is a fault reaching the keys, not a statement about the signature."), unkeyed.PrefixedDigest, null);

        if (keys.IsDefaultOrEmpty)
            return (Check.CouldNotCheck($"the Forum published no keys at all for {author}"), unkeyed.PrefixedDigest, null);

        var verdict = SignatureCheck.Verify(post, keys);
        var signingKey = keys.FirstOrDefault(k => string.Equals(k.Kid, verdict.Kid, StringComparison.Ordinal));
        return (verdict.Verified
            ? Check.Verified(verdict.Detail + $" (kid={verdict.Kid})")
            : Check.Failed(verdict.Detail), verdict.PrefixedDigest, signingKey);
    }

    /// <summary>
    /// R6.54's check (errata G16): the key that signed the post, found where the key set says the log
    /// binds it, proven under the same signed head as the post, and before it.
    ///
    /// <para><b>The key set only says where to look.</b> A Forum that names the wrong leaf, or none,
    /// cannot make this pass: the leaf is recomputed from the entry the log serves, proven to the
    /// head this client verified, and the post's signature is checked under the key that leaf
    /// carries. What a lying key set can do is make the check impossible, by naming no leaf, which is
    /// reported as could-not-be-checked, or make it fail, by naming a leaf that binds something
    /// else.</para>
    /// </summary>
    private async Task<Check> KeyBindingAsync(ProvenancePost post, Anchor anchor, ForumJwk? signingKey, CancellationToken ct)
    {
        if (post.LogIndex is not { } postIndex)
            return Check.CouldNotCheck(
                "this Forum served the post with no log index, so its place in the log cannot be compared with its key's");

        if (anchor.Head is not { } head) return anchor.Verdict;

        if (signingKey?.LogIndex is not { } keyIndex)
            return Check.CouldNotCheck(
                "the author's key set names no log leaf for the key this post names, so the key's binding cannot be found (R6.54)");

        if (postIndex >= head.TreeSize)
            return Check.CouldNotCheck(Invariant(
                $"leaf {postIndex} is not covered by the signed head at tree size {head.TreeSize}, so the post and its key's binding cannot be proven under one head yet"));

        var proof = await _forum.GetInclusionProofAsync(keyIndex, head.TreeSize, ct).ConfigureAwait(false);
        if (!proof.TryGetValue(out var keyProof, out var proofRefusal))
            return Check.CouldNotCheck(Invariant(
                $"a proof for the key's binding at leaf {keyIndex} could not be fetched: {proofRefusal!.Summary}"));

        var entry = await _forum.GetLogEntryAsync(keyIndex, ct).ConfigureAwait(false);
        if (!entry.TryGetValue(out var keyEntry, out var entryRefusal))
            return Check.CouldNotCheck(Invariant(
                $"the key's binding entry at leaf {keyIndex} could not be fetched: {entryRefusal!.Summary}"));

        var bound = ActaCheck.KeyBinding(keyEntry!, keyProof!, post, postIndex);
        if (bound.Outcome is not CheckOutcome.Verified) return bound;

        var covers = ActaCheck.HeadCovers(head, keyProof!.TreeSize, keyProof.RootHash);
        return covers.Outcome is CheckOutcome.Verified
            ? Check.Verified($"{bound.Detail}, and {covers.Detail}")
            : covers;
    }

```

- [ ] **Step 6: Say it where a reader is told**

In `src/Curia.Client.Cli/Program.cs`, replace:

```csharp

        // R6.52's other two checks: the leaf recomputed from the log's own entry, and the log's
        // growth since the head this client retains. Run against the post already read rather than
        // one fetched again, so the verdict is about the document above rather than about whatever
        // the Forum would serve on a second request.
```

with:

```csharp

        // R6.52's other two checks: the leaf recomputed from the log's own entry, and the log's
        // growth since the head this client retains; and R6.54's, the signing key bound in the log
        // before the post. Run against the post already read rather than
        // one fetched again, so the verdict is about the document above rather than about whatever
        // the Forum would serve on a second request.
```

In `src/Curia.Client.Cli/Program.cs`, replace:

```csharp
        Output.Line($"inclusion   {acta.Inclusion.Describe}");
        Output.Line($"consistency {acta.Consistency.Describe}");

        return ExitCode.ForOutcomes(local.Outcome, independent.Outcome, acta.Overall);
```

with:

```csharp
        Output.Line($"inclusion   {acta.Inclusion.Describe}");
        Output.Line($"consistency {acta.Consistency.Describe}");
        Output.Line($"key         {acta.KeyBinding.Describe}");

        return ExitCode.ForOutcomes(local.Outcome, independent.Outcome, acta.Overall);
```

In `src/Curia.Mcp/ToolText.cs`, replace:

```csharp
    internal const string VerifyTemplate =
        "Check a Cūria post you have already read: its signature, its place in the Forum's " +
        "append-only log, and whether that log still extends the last state this client saw.\n\n" +
        "Every check runs here, on your operator's host, against material re-derived locally. The " +
        "signature is checked over bytes re-canonicalized from the served document rather than over " +
        "the bytes the Forum labelled canonical; the log leaf is recomputed from the log's own " +
        "entry rather than taken from the digest the Forum published for it; and the entry is tied " +
        "to your post by byte-identity before any proof counts as evidence about it.\n\n" +
        "EACH CHECK REPORTS ONE OF THREE OUTCOMES, AND THEY ARE NOT INTERCHANGEABLE. 'verified' " +
        "means the check ran and held. 'FAILED' means it ran and did not hold — treat the post as " +
```

with:

```csharp
    internal const string VerifyTemplate =
        "Check a Cūria post you have already read: its signature, its place in the Forum's " +
        "append-only log, whether that log still extends the last state this client saw, and " +
        "whether the log bound the signing key to the author before the post.\n\n" +
        "Every check runs here, on your operator's host, against material re-derived locally. The " +
        "signature is checked over bytes re-canonicalized from the served document rather than over " +
        "the bytes the Forum labelled canonical; the log leaf is recomputed from the log's own " +
        "entry rather than taken from the digest the Forum published for it; the entry is tied " +
        "to your post by byte-identity before any proof counts as evidence about it; and the key " +
        "is checked against the one the log's binding entry carries, not the one the Forum's key " +
        "set serves.\n\n" +
        "EACH CHECK REPORTS ONE OF THREE OUTCOMES, AND THEY ARE NOT INTERCHANGEABLE. 'verified' " +
        "means the check ran and held. 'FAILED' means it ran and did not hold — treat the post as " +
```

- [ ] **Step 7: Hold the stub's key set and binding to the Forum's**

In `tests/Curia.Api.Tests/StubFidelityTests.cs`, replace:

```csharp
        Assert.Equal(0, read.UnreadableAnswers);
        Assert.NotEmpty(read.Answers);
    }

```

with:

```csharp
        Assert.Equal(0, read.UnreadableAnswers);
        Assert.NotEmpty(read.Answers);
    }

    /// <summary>
    /// R6.54 (errata G16): the two documents the client's fourth check reads -- an agent's key set,
    /// which names the leaf binding each key, and that leaf's entry -- carry exactly the members the
    /// Forum's do. Until this fact the stub's key set had never been held to the Forum's, and it lacked
    /// a member the Forum has always served (<c>curia_not_before</c>).
    /// </summary>
    [Fact]
    public async Task R6_54_TheStubsKeySetAndKeyBindingHaveTheForumsMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = ForumAgent.Create(Unique("keyset"), "keyset-" + Guid.NewGuid().ToString("N")[..8]);
        using (var enrolled = await agent.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var keySet = await http.GetStringAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agent.AgentId)}", UriKind.Relative), ct);
        var index = JsonNode.Parse(keySet)!["keys"]![0]!["curia_log_index"]!.GetValue<long>();
        var entry = await http.GetStringAsync(new Uri($"/v1/log/entries/{index}", UriKind.Relative), ct);

        AssertSameMembers("the key set", Paths(JsonNode.Parse(keySet)!), Paths(JsonNode.Parse(_stub.JwksJson())!));
        AssertSameMembers("the key-binding entry", Paths(JsonNode.Parse(entry)!), Paths(JsonNode.Parse(_stub.EntryJson(_stub.KeyIndex))!));
    }

```

- [ ] **Step 8: Run every suite that reads a verdict**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Client.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Mcp.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
dotnet test tests/Curia.Api.Tests -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
```

Expected: `0 Warning(s)`; Client 211, Mcp 74, Api 229. Every existing fact that asserts an overall *verified* — against the stub and, in `ClientVerificationTests`, against the Forum — now passes through the fourth check as well, which is how it shows the check verifies what it should and not only refuses.

- [ ] **Step 9: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'R6.54 in the reference client: the key behind a post, bound before it\n\nThe fourth check proves the leaf the key set names under the same head as the\npost and verifies the post under the key that leaf carries. The overall verdict\nneeds it; a pre-G16 identity, or a key bound after the post, is could not be\nchecked. The stub key set is held\nto the Forum key set for the first time.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 9: Falsify every new gate

**Files:**
- Create (in the scratchpad only, never committed): `falsify.py`.
- No tracked file changes. Every patch is restored, and each restore is proved.

**Preconditions:**
- Tasks 1–8 are committed, and `git status --porcelain` is empty.
- `CURIA_TEST_POSTGRES` is exported, and `CURIA_TESTIS_BIN` names this tree's `rust/curia-testis/target/debug/curia-testis`: cases 24–26 and 28 patch `curia-testis` and `cargo test` rebuilds that binary, which the Api runs of cases 26 and 28 then execute.
- The runner restores from a kept copy with a **plain copy** (`shutil.copyfile`, a fresh mtime), never `copy2` and never `git checkout`, in a `finally`, so an exception or an interrupt mid-case never leaves a file patched. It proves each restore twice: the bytes equal the kept copy's, and `git diff --quiet` sees no change.
- A command is RED only when its output holds a test run's own failure line — `Failed!` from `dotnet test`, `test result: FAILED` from cargo. Any compiler error (`: error `, `error[E`, `error: could not compile`) is BUILD FAILED. A non-zero exit with neither is DID NOT RUN. Anything not RED — a mismatched patch, a failed build, a green suite, a suite that did not run, a dirty restore — fails the run, and the last line is the runner's own `runner exit: 0` or `runner exit: 1`.
- No patch is a constant expression: where one disables a condition, it compares against a value that never occurs, so no analyzer rejects it and no residue scan mistakes it for code.
- No case is quoted until the unpatched gates have been rebuilt with `--no-incremental` and run green (Step 3).

- [ ] **Step 1: Write the runner in the scratchpad**

```python
#!/usr/bin/env python3
"""Falsify each gate: patch, run its filter, restore by plain copy, prove the restore clean.

Usage, from the repository root:  python3 falsify.py <keep-dir> [case-id ...]
Scratch only: this file is never committed.

CURIA_TEST_POSTGRES must be exported, and CURIA_TESTIS_BIN must name the debug binary this tree's
cargo builds (rust/curia-testis/target/debug/curia-testis), because cases 24-26 and 28 rebuild it.
"""
import filecmp, os, pathlib, re, shutil, subprocess, sys

ROOT = pathlib.Path.cwd()
KEEP = pathlib.Path(sys.argv[1]); KEEP.mkdir(parents=True, exist_ok=True)
ONLY = set(sys.argv[2:])

def dotnet(project, flt):
    return ["dotnet", "test", project, "-c", "Release", "--nologo", "--filter", flt]

def arch_debug():
    # No --no-build: this compiles the project and the src assemblies it references in Debug.
    # Filtered to LayeringTests, where CS-7 lives: CS-15 reads Curia.Domain.Tests and
    # Curia.Application.Tests from their Debug output, which this command does not build, so
    # unfiltered it goes red on a fresh tree whatever the patch, and reads stale DLLs on a used one.
    return ["dotnet", "test", "tests/Curia.Architecture.Tests", "-c", "Debug", "--nologo",
            "--filter", "FullyQualifiedName~LayeringTests"]

def cargo(test):
    return ["cargo", "test", "--manifest-path", "rust/curia-testis/Cargo.toml", "--locked", "--test", test]

ENROLL_AGENT = "src/Curia.Application/Credentials/EnrollAgent.cs"
ENROLL_IDENTITY = "src/Curia.Application/Credentials/EnrollIdentity.cs"
BINDING = "src/Curia.Application/Credentials/EnrollmentBinding.cs"
BOUND_KEYS = "src/Curia.Application/Credentials/LogBoundKeys.cs"
PROGRAM = "src/Curia.Api/Program.cs"
ASSERTION = "src/Curia.AuthN/ClientAssertionValidator.cs"
ACCESS = "src/Curia.AuthN/AccessTokenValidator.cs"
PUBLIC_JWK = "src/Curia.Canon/Jws/PublicJwk.cs"
ACTA_CHECK = "src/Curia.Client/ActaCheck.cs"
VERIFIER = "src/Curia.Client/PostVerifier.cs"
TESTIS_ACTA = "rust/curia-testis/src/acta.rs"
POST_KIND = "src/Curia.Domain/Content/PostKind.cs"

APP = "tests/Curia.Application.Tests"
API = "tests/Curia.Api.Tests"
AUTHN = "tests/Curia.AuthN.Tests"
SODIUM = "tests/Curia.Canon.Sodium.Tests"
CLIENT = "tests/Curia.Client.Tests"
MCP = "tests/Curia.Mcp.Tests"

HOLDS = "        if (bound is not null && !bound.Holds(key))"
HOLDS_OFF = "        if (bound is not null && string.Equals(bound.Kid, \"no-such-kid\", StringComparison.Ordinal))"
RECORD_HOLDS = "                if (!bound.Holds(key))"
RECORD_HOLDS_OFF = "                if (string.Equals(bound.Kid, \"no-such-kid\", StringComparison.Ordinal))"
KEY_SET_BODY = ("            if (found?.For(registered.Key.Kid) is { } keyBinding && keyBinding.Holds(registered.Key))\n"
                "                bound.Add(new BoundKey(registered, keyBinding));\n")
KEY_SET_EVERY_ROW = (KEY_SET_BODY +
                     "            else if (!string.Equals(registered.Key.Kid, \"no-such-kid\", StringComparison.Ordinal))\n"
                     "                bound.Add(new BoundKey(registered, new KeyBinding(registered.Key.Kid, null, registered.NotBefore, \"unbound\")));\n")
STORE_FIRST = ("        var held = await _registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);\n"
               "        if (held.Count == 0) return Result<AgentKeySet>.Ok(new AgentKeySet(0, []));\n\n"
               "        var binding = await BindingAsync(agentId, cancellationToken).ConfigureAwait(false);\n")
LOG_FIRST = ("        var binding = await BindingAsync(agentId, cancellationToken).ConfigureAwait(false);\n"
             "        var held = await _registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);\n"
             "        if (held.Count == 0) return Result<AgentKeySet>.Ok(new AgentKeySet(0, []));\n\n")
TABLE_PARSE = ("        kind = default;\n"
               "        return wire is not null && ByWire.TryGetValue(wire, out kind);")
SWITCH_PARSE = ("        switch (wire)\n"
                "        {\n"
                "            case \"question\" or \"answer\" or \"comment\" or \"finding\" or \"revision\" or \"vote\" or \"verification\":\n"
                "                return ByWire.TryGetValue(wire, out kind);\n"
                "            default:\n"
                "                kind = default;\n"
                "                return false;\n"
                "        }")

CASES = [
    dict(id="1", what="the enrollment appends its binding under a type no reader reads",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests"),
               dotnet(API, "FullyQualifiedName~KeyBindingTests|FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(ENROLL_AGENT, "NewEvent(AgentStandingProjector.KeyBoundType, actor,",
                               "NewEvent(AgentStandingProjector.KeyBoundType + \"-unread\", actor,")]),
    dict(id="2", what="a binding that carries the key answers on the kid alone",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests|FullyQualifiedName~LogBoundKeysTests"),
               dotnet(API, "FullyQualifiedName~StoredKeyFormTests|FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(BINDING, "        if (Jwk is null) return true;",
                          "        if (Jwk is null || !string.Equals(Kid, \"no-such-kid\", StringComparison.Ordinal)) return true;")]),
    dict(id="3", what="the kid-only binding stands beside a binding that carries the key",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests|FullyQualifiedName~LogBoundKeysTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(BINDING, "!bound.Any(b => string.Equals(b.Kid, legacy.Kid, StringComparison.Ordinal))",
                          "!bound.Any(b => string.Equals(b.Kid, \"no-such-kid\", StringComparison.Ordinal))")]),
    dict(id="4", what="the binding set is its first binding alone",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests")],
         edits=[(BINDING, "        Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));",
                          "        Keys.Take(1).FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));")]),
    dict(id="5", what="the resolver honours whatever the store resolves",
         cmds=[dotnet(APP, "FullyQualifiedName~LogBoundKeysTests"),
               dotnet(API, "FullyQualifiedName~KeyBindingTests|FullyQualifiedName~StoredKeyFormTests|FullyQualifiedName~TokenSubjectBindingTests")],
         edits=[(BOUND_KEYS, "        return found?.For(kid) is { } bound && bound.Holds(key!)",
                             "        return (found?.For(kid) is { } bound && bound.Holds(key!)) || !string.Equals(kid, \"no-such-kid\", StringComparison.Ordinal)")]),
    dict(id="6", what="the key set lists every key the store holds",
         cmds=[dotnet(APP, "FullyQualifiedName~LogBoundKeysTests"),
               dotnet(API, "FullyQualifiedName~KeyBindingTests")],
         edits=[(BOUND_KEYS, KEY_SET_BODY, KEY_SET_EVERY_ROW)]),
    dict(id="7", what="the key set reads the log before the store",
         cmds=[dotnet(API, "FullyQualifiedName~R4_35_TheKeySetAnswersEveryAgentWithoutA500")],
         edits=[(BOUND_KEYS, STORE_FIRST, LOG_FIRST)]),
    dict(id="8", what="the token endpoint resolves through the store alone",
         cmds=[dotnet(API, "FullyQualifiedName~KeyBindingTests|FullyQualifiedName~StoredKeyFormTests|FullyQualifiedName~TokenSubjectBindingTests")],
         edits=[(PROGRAM, "builder.Services.AddSingleton<IAgentKeyResolver>(sp => new LogBoundAgentKeyResolver(sp.GetRequiredService<LogBoundKeys>()));",
                          "builder.Services.AddSingleton<IAgentKeyResolver>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());")]),
    dict(id="9", what="ingest resolves through the store alone",
         cmds=[dotnet(API, "FullyQualifiedName~KeyBindingTests")],
         edits=[(PROGRAM, "builder.Services.AddSingleton<IAuthorKeyResolver>(sp => sp.GetRequiredService<LogBoundKeys>());",
                          "builder.Services.AddSingleton<IAuthorKeyResolver>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());")]),
    dict(id="10", what="the use case's material check off",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests")],
         edits=[(ENROLL_IDENTITY, HOLDS, HOLDS_OFF)]),
    dict(id="11", what="the log's record material check off",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests")],
         edits=[(ENROLL_AGENT, RECORD_HOLDS, RECORD_HOLDS_OFF)]),
    dict(id="12", what="both material checks off",
         cmds=[dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(ENROLL_IDENTITY, HOLDS, HOLDS_OFF), (ENROLL_AGENT, RECORD_HOLDS, RECORD_HOLDS_OFF)]),
    dict(id="13", what="a client assertion's alg is not pinned to its key's",
         cmds=[dotnet(AUTHN, "FullyQualifiedName~ClientAssertionValidatorTests"),
               dotnet(API, "FullyQualifiedName~R5_21")],
         edits=[(ASSERTION, "        if (!string.Equals(header.Alg, key!.Alg, StringComparison.Ordinal))",
                            "        if (string.Equals(header.Alg, \"no-such-alg\", StringComparison.Ordinal))")]),
    dict(id="14", what="a DPoP proof's alg is not pinned to its jwk's",
         cmds=[dotnet(AUTHN, "FullyQualifiedName~AccessTokenValidatorDpopTests")],
         edits=[(ACCESS, "        if (!string.Equals(proofHeader.Alg, keyAlg, StringComparison.Ordinal))",
                         "        if (string.Equals(proofHeader.Alg, \"no-such-alg\", StringComparison.Ordinal))")]),
    dict(id="15", what="the renderer publishes an ES256 key on any named curve",
         cmds=[dotnet(SODIUM, "FullyQualifiedName~PublicJwkTests")],
         edits=[(PUBLIC_JWK, "|| curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)",
                             "|| curve.Oid.Value == \"no-such-oid\")")]),
    dict(id="16", what="the renderer publishes an EdDSA key of any length",
         cmds=[dotnet(SODIUM, "FullyQualifiedName~PublicJwkTests")],
         edits=[(PUBLIC_JWK, "            \"EdDSA\" => key.Public.Length == 32", "            \"EdDSA\" => key.Public.Length > 0")]),
    dict(id="17", what="two public JWKs compared by length",
         cmds=[dotnet(SODIUM, "FullyQualifiedName~PublicJwkTests"),
               dotnet(APP, "FullyQualifiedName~EnrollIdentityTests|FullyQualifiedName~LogBoundKeysTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(PUBLIC_JWK, "            && l.Span.SequenceEqual(r.Span);", "            && l.Length == r.Length;")]),
    dict(id="18", what="the renderer swaps a P-256 key's coordinates",
         cmds=[dotnet(SODIUM, "FullyQualifiedName~PublicJwkTests"),
               dotnet(APP, "FullyQualifiedName~EnrollIdentityTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(PUBLIC_JWK, "Render(\"EC\", \"P-256\", key, parameters.Q.X!, parameters.Q.Y!)",
                             "Render(\"EC\", \"P-256\", key, parameters.Q.Y!, parameters.Q.X!)")]),
    dict(id="19", what="the client's binding check verifies nothing under the bound key",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(ACTA_CHECK, "        var verdict = SignatureCheck.Verify(post, [bound]);",
                             "        var verdict = SignatureCheck.Verify(post, [bound]) with { Verified = !string.Equals(bound.Kid, \"no-such-kid\", StringComparison.Ordinal) };")]),
    dict(id="20", what="the client's binding check ignores the order of the leaves",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(ACTA_CHECK, "        if (keyEntry.LogIndex >= postIndex)", "        if (keyEntry.LogIndex == -postIndex - 1)")]),
    dict(id="21", what="the client reads a kid-only enrollment as a verified binding",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(ACTA_CHECK, "        if (enrolled)\n            return Check.CouldNotCheck(Text(",
                             "        if (enrolled)\n            return Check.Verified(Text(")]),
    dict(id="22", what="the overall verdict ignores the binding",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(VERIFIER, "              && KeyBinding.Outcome is CheckOutcome.Verified\n", "")]),
    dict(id="23", what="a key set naming no leaf is read as a verified binding",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(VERIFIER, "            return Check.CouldNotCheck(\n                \"the author's key set names no log leaf",
                           "            return Check.Verified(\n                \"the author's key set names no log leaf")]),
    dict(id="24", what="curia-testis ignores the order of the leaves",
         cmds=[cargo("log_author")],
         edits=[(TESTIS_ACTA, "    if key.log_index >= post.log_index {", "    if key.log_index >= post.log_index + 1000 {")]),
    dict(id="25", what="curia-testis does not compare the binding with the post",
         cmds=[cargo("log_author")],
         edits=[(TESTIS_ACTA, "    if aggregate != bound_agent || bound_agent != post_author || bound_kid != post_kid {",
                              "    if aggregate == \"no-such-agent\" {")]),
    dict(id="26", what="curia-testis reads a kid-only enrollment as not a binding at all",
         cmds=[cargo("log_author"),
               dotnet(API, "FullyQualifiedName~R6_54_TestisEstablishesAuthorshipFromTheLogAlone")],
         edits=[(TESTIS_ACTA, "    if key_type == ENROLLED {", "    if key_type == \"no-such-type\" {")]),
    dict(id="27", what="Curia.Domain regains a seven-case string switch (D16)",
         cmds=[arch_debug()],
         edits=[(POST_KIND, TABLE_PARSE, SWITCH_PARSE)]),
    dict(id="28", what="curia-testis reports the author's binding after the post as a failure",
         cmds=[cargo("log_author"),
               dotnet(API, "FullyQualifiedName~R6_54_TestisEstablishesAuthorshipFromTheLogAlone")],
         edits=[(TESTIS_ACTA, "            ActaError::KeyNotCarried { .. } | ActaError::BoundAfterPost { .. }",
                              "            ActaError::KeyNotCarried { .. }")]),
    dict(id="29", what="the client reports the author's binding after the post as a failure",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(ACTA_CHECK, "        if (keyEntry.LogIndex >= postIndex)\n            return Check.CouldNotCheck(Text(",
                             "        if (keyEntry.LogIndex >= postIndex)\n            return Check.Failed(Text(")]),
    dict(id="30", what="the client does not compare the binding with the post's author and kid",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(ACTA_CHECK, "        if (!string.Equals(aggregate, author, StringComparison.Ordinal)\n"
                             "            || !string.Equals(boundAgent, author, StringComparison.Ordinal)\n"
                             "            || !string.Equals(boundKid, signed, StringComparison.Ordinal))",
                             "        if (string.Equals(aggregate, \"no-such-agent\", StringComparison.Ordinal))")]),
    dict(id="31", what="an identifier the log never enrolled is bound whatever the store holds for it",
         cmds=[dotnet(APP, "FullyQualifiedName~EnrollIdentityTests"),
               dotnet(API, "FullyQualifiedName~EnrollmentBindingTests")],
         edits=[(ENROLL_IDENTITY, "_keys.KeysForAsync(agentId, cancellationToken)",
                                  "_keys.KeysForAsync(\"no-such-agent\", cancellationToken)")]),
    dict(id="32", what="the client holds the key's proof to its own root, not the signed head's",
         cmds=[dotnet(CLIENT, "FullyQualifiedName~PostVerifierTests")],
         edits=[(VERIFIER, "ActaCheck.HeadCovers(head, keyProof!.TreeSize, keyProof.RootHash)",
                           "ActaCheck.HeadCovers(head, keyProof!.TreeSize, head.RootHash ?? keyProof.RootHash)")]),
    dict(id="33", what="curia_verify's result drops the key check's line",
         cmds=[dotnet(MCP, "FullyQualifiedName~PropertyP22ToolResultTests")],
         edits=[(VERIFIER, "        builder.Append(culture, $\"key         {KeyBinding.Describe}\\n\");\n", "")]),
]

# A case id that names no case would otherwise run nothing and still end "runner exit: 0".
unknown = ONLY - {case["id"] for case in CASES}
if unknown:
    print("unknown case id(s): " + ", ".join(sorted(unknown)) + " -- nothing was run")
    print("runner exit: 1")
    sys.exit(1)

# What a failing test prints about itself, and nothing else. xUnit: its name, then its message block
# up to the stack trace, which carries Expected:/Actual: and exception lines. cargo: its name, then
# the panic line and the assertion's message.
FAILED = re.compile(r"^\s*Failed (.+?) \[[^\]]*\]\s*$")
CARGO_FAILED = re.compile(r"^---- (.+?) stdout ----$")

def failures(out):
    lines, keep = [], False
    for line in out.splitlines():
        if FAILED.match(line):
            lines.append("  FAILED " + FAILED.match(line).group(1)); keep = False; continue
        if CARGO_FAILED.match(line):
            lines.append("  FAILED " + CARGO_FAILED.match(line).group(1)); keep = True; continue
        if line.strip() == "Error Message:":
            keep = True; continue
        if line.strip() in ("Stack Trace:", "failures:") or line.startswith("note: run with"):
            keep = False; continue
        if keep and line.strip():
            lines.append("      " + line.strip()[:240])
    return lines

RED = ("Failed!", "test result: FAILED")
BUILD = (": error ", "error[E", "error: could not compile")

# Every outcome that is not RED -- a patch that mismatched, a build that failed, a suite that stayed
# green, a host that never ran a test, a restore that is dirty -- falsified nothing, and fails the run.
not_red = []
for case in CASES:
    if ONLY and case["id"] not in ONLY:
        continue
    files = sorted({f for f, _, _ in case["edits"]})
    for f in files:
        shutil.copyfile(ROOT / f, KEEP / f.replace("/", "__"))
    clean = True
    try:
        ok = True
        for f, old, new in case["edits"]:
            p = ROOT / f
            s = p.read_text(encoding="utf-8")
            n = s.count(old)
            if n != 1:
                print(f"[{case['id']}] PATCH MISMATCH in {f}: {n} matches -- fix the patch, not the code")
                not_red.append(f"[{case['id']}] PATCH MISMATCH")
                ok = False
                break
            p.write_text(s.replace(old, new), encoding="utf-8")
        if ok:
            print(f"[{case['id']}] {case['what']}")
            for cmd in case["cmds"]:
                r = subprocess.run(cmd, capture_output=True, text=True)
                out = r.stdout + r.stderr
                label = cmd[2] if cmd[0] == "dotnet" else "cargo " + cmd[-1]
                # A build error must never read as RED, and RED needs a test run's own failure line:
                # a non-zero exit without one is a host that never ran a test.
                if any(m in out for m in BUILD):
                    status = "BUILD FAILED"
                elif any(m in l for l in out.splitlines() for m in RED):
                    status = "RED"
                elif r.returncode != 0:
                    status = "DID NOT RUN"
                else:
                    status = "GREEN -- bad patch or a gap"
                if status != "RED":
                    not_red.append(f"[{case['id']}] {label} {status}")
                print(f"[{case['id']}] {label} {status}")
                for line in out.splitlines():
                    if "Passed!" in line or "Failed!" in line or line.startswith("test result:"):
                        print("    " + line.strip())
                for line in failures(out):
                    print(line)
                if status == "BUILD FAILED":
                    for line in out.splitlines():
                        if any(m in line for m in BUILD):
                            print("    " + line.strip()[:240])
                if status == "DID NOT RUN":
                    for line in out.splitlines()[-20:]:
                        print("    " + line.strip()[:240])
    finally:
        # Restored even when the run is interrupted or throws: no file is ever left patched.
        for f in files:
            shutil.copyfile(KEEP / f.replace("/", "__"), ROOT / f)   # plain copy: a fresh mtime (trap 18)
        # Two proofs: the bytes equal the kept copy's, and git sees no change against the index.
        same = [f for f in files if filecmp.cmp(KEEP / f.replace("/", "__"), ROOT / f, shallow=False)]
        quiet = subprocess.run(["git", "diff", "--quiet", "--", *files]).returncode == 0
        clean = len(same) == len(files) and quiet
        print(f"[{case['id']}] restore {'clean' if clean else 'DIRTY -- STOP'}"
              f" (bytes equal to the kept copy: {len(same)}/{len(files)}; git diff --quiet: {'yes' if quiet else 'NO'})")
    if not clean:
        not_red.append(f"[{case['id']}] restore DIRTY")
        break

not_run = [n for n in not_red if n.endswith("DID NOT RUN")]
if not_run:
    print("DID NOT RUN: " + ", ".join(not_run) + " -- no test ran there, so nothing was falsified")
if not_red:
    print("NOT RED: " + ", ".join(not_red) + " -- nothing was falsified there")
    print("runner exit: 1")
    sys.exit(1)
print("runner exit: 0")
```

- [ ] **Step 2: Run it**

From the repository root:

```bash
set -o pipefail
python3 -u <scratchpad>/falsify.py <scratchpad>/falsify-keep 2>&1 | tee <scratchpad>/falsify.log
echo "falsify.py exit ${PIPESTATUS[0]}"   # fish: echo "falsify.py exit $pipestatus[1]"
```

`-u` because a redirected Python buffers its output, and a log that is empty until the run ends looks like a run that has stopped. The log's last line is the runner's own `runner exit: N`.

Each case must print `RED` for every command it runs, then `restore clean`, and the last line must be `runner exit: 0`. There are thirty-three cases in forty-six suite runs. When the plan was amended after Task 2's review, this runner ran exactly as printed here in a git-backed copy of the tree (a `git archive` of 38a21fa with bae4ec8's errata restored under it, Tasks 2–8 applied, `git init`, and one commit): every case printed what the table says, every restore printed `restore clean` with both proofs — the bytes equal to the kept copy, and a real `git diff --quiet` — and the last line was `runner exit: 0`; then Step 3 ran and printed what it states. The second proof is the one that sees a file the runner did not keep:

| Case | Must fail, by name |
|---|---|
| 1 | Eight `EnrollIdentityTests` facts: the five that read an enrollment's entry types (`Expected: "agent.key-bound"`, `Actual: "agent.key-bound-unread"`), `R4_34_AnEnrollmentWritesTheConformanceVectorsPayload` (`Assert.Single()`: no binding), and the two refusals of other bytes (`expected a refusal, got AgentEnrollment { … WasAlreadyEnrolled = True }`: with no binding the kid-only clause decides); `EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName` (`other bytes under victim-… were answered 201, and their holder obtained the victim's token`); `KeyBindingTests.R6_54_EachPublishedKeyNamesTheLeafThatBindsIt` (`Expected: "agent.key-bound https://…"`, `Actual: "agent.enrolled https://…"`: the key set names the enrollment's leaf) |
| 2 | `EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused` and `R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound`; `LogBoundKeysTests.R4_35_OtherBytesUnderTheBoundKidAreRefusedByName` (`Actual: "resolved"`); the lost-row HTTP fact (201, and a token); both `StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken` rows (`Actual: ···"Signature does not verify"···`: the replaced row reached the verifier) |
| 3 | The same three rule-level facts as case 2, and `LogBoundKeysTests.R4_35_TheKeySetListsOnlyTheKeysTheLogBinds` (the binding's event id is the enrollment's, not the key-bound entry's); the lost-row HTTP fact |
| 4 | `EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs` alone (`curia/enroll/already-enrolled`): the seam |
| 5 | `LogBoundKeysTests`' three refusals (`Actual: "resolved"`); `KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing` (`token request 200, question 201`); both `StoredKeyFormTests.R4_35_…` rows; `TokenSubjectBindingTests.R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity` (`Actual: "That agent is not enrolled"`) |
| 6 | `LogBoundKeysTests.R4_35_TheKeySetListsOnlyTheKeysTheLogBinds` (`…,hole-1@unbound]`); the hole-key HTTP fact, at the key set alone (`token request 401, question 401 …`, and the key set holding `hole-…`) |
| 7 | The two U+0000 rows of `KeyBindingTests.R4_35_TheKeySetAnswersEveryAgentWithoutA500` (`Actual: "500 Npgsql.PostgresException (0x80004005): 22021: "···`). The other three rows stay green, and should: Postgres `text` holds a noncharacter, U+FFFD and a long string |
| 8 | The hole-key HTTP fact at the token (`token request 200, question 401 …`); both `StoredKeyFormTests.R4_35_…` rows; the orphan fact (`That agent is not enrolled`). The post path still refuses, which is why case 9 exists (trap 13) |
| 9 | The hole-key HTTP fact at the question alone (`token request 401, question 201 …`) |
| 10 | `EnrollIdentityTests.R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused` alone (`Assert.Empty() Failure`: the store registered the other bytes before the log's record refused them). The HTTP fact stays green, and should: the log's record still refuses, and R4.35 refuses the impostor's token |
| 11 | `EnrollIdentityTests.R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound` alone |
| 12 | The lost-row HTTP fact, at the status (`answered 201, and their holder obtained no token`): both halves of R4.31 (revised) off, and R4.35 still refuses the impostor's token — three layers, each fenced |
| 13 | `ClientAssertionValidatorTests.R5_21_…` (`Actual: "curia/authn/signature-invalid "`); `StoredKeyFormTests.R5_21_…` (`Actual: ···"Signature does not verify"···`) |
| 14 | `AccessTokenValidatorDpopTests.R5_21_…` (`Actual: "curia/authn/signature-invalid "`) |
| 15 | `PublicJwkTests.R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(alg: "ES256", material: "p384-spki", …)` (`Actual: "adapter=False rendered=True"`). The brainpool row stays green on macOS, which cannot import the curve at all; on Linux, where OpenSSL imports it, it is expected red too (traced, not run) |
| 16 | The three EdDSA rows `31-bytes`, `33-bytes` and `p256-spki` (`adapter=False rendered=True`) |
| 17 | `PublicJwkTests.R4_34_SameKeyComparesTheKeyNotTheReference` (`different=True`); the use case's and the log's refusals of other bytes, and `LogBoundKeysTests.R4_35_OtherBytesUnderTheBoundKidAreRefusedByName`; the lost-row HTTP fact. Two P-256 keys render to JWKs of equal length |
| 18 | `PublicJwkTests.R4_28_AP256KeyIsRenderedAsRfc7518sEcForm` (`Actual: ···"x":"x_FEzRu9m36HLN_tue659LNpX"···`: RFC 7515's `y`); `EnrollIdentityTests.R4_34_AnEnrollmentBindsItsKeyInTheLogBesideItsRecord` (its coordinates are read from the DER independently); five `EnrollmentBindingTests` facts at `AssertServesOnlyTheVictimsKey`. No Forum-side comparison could see it: the renderer agrees with itself on both sides of every one |
| 19 | `PostVerifierTests.R6_54_ALogThatBoundAnotherKeyFailsTheBindingThoughTheSignatureVerifies` (`Expected: Failed`, `Actual: Verified`) |
| 20 | `PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished` (`Expected: CouldNotCheck`, `Actual: Verified`) |
| 21 | `PostVerifierTests.R6_54_AKeyTheLogNamesByKidAloneIsNotEstablished` (`Expected: CouldNotCheck`, `Actual: Verified`) |
| 22 | `PostVerifierTests.R6_54_AKeySetNamingNoLeafCannotBeChecked`, `R6_54_AKeyTheLogNamesByKidAloneIsNotEstablished` and `R6_54_AKeyBoundAfterThePostIsNotEstablished`, each at `Overall` (`Actual: Verified`) |
| 23 | `PostVerifierTests.R6_54_AKeySetNamingNoLeafCannotBeChecked` |
| 24 | `log_author.rs`' `r6_54_a_key_bound_after_the_post_is_not_established` (`called Result::unwrap_err() on an Ok value: VerifiedAuthor { … key_index: 2, post_index: 0 }`) |
| 25 | `r6_54_a_binding_for_another_identity_fails` (`Ok` value: the other identity's binding carries the same key) and `r6_54_another_identitys_enrollment_fails` (`left: ("curia/acta/key-not-carried", true)`, `right: ("curia/acta/binding-mismatch", false)`): one comparison, whatever the entry's type |
| 26 | `r6_54_an_enrollment_that_names_the_kid_alone_is_not_checked` (`left: ("curia/acta/missing-field", false)`, `right: ("curia/acta/key-not-carried", true)`); `ActaEndpointTests.R6_54_TestisEstablishesAuthorshipFromTheLogAlone` (``String: "exit 1: error: key entry has no usable `jwk` [curi"···``, `Expected start: "exit 3:"`) |
| 27 | `LayeringTests.CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives`, in Debug (`Failed!  - Failed:     1, Passed:     7`; `Offenders: Curia.Domain.Content.PostKinds`). The same patch passes the Release run (`Passed!  - Failed:     0, Passed:    30`, measured by the pre-flight scan), which is D16 exactly and why Task 1's step exists. The run is filtered to `LayeringTests` because unfiltered, on a tree with no Debug test assemblies, CS15 fails too, patch or no patch |
| 28 | `r6_54_a_key_bound_after_the_post_is_not_established` (`left: ("curia/acta/bound-after-post", false)`, `right: ("curia/acta/bound-after-post", true)`); `ActaEndpointTests.R6_54_TestisEstablishesAuthorshipFromTheLogAlone` at its `bound-late` control (`String: "exit 1: error: the key is bound at leaf …, which "···`, `Expected start: "exit 3:"`) |
| 29 | `PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished` (`Expected: CouldNotCheck`, `Actual: Failed`) |
| 30 | `PostVerifierTests.R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey` (`Expected: Failed`, `Actual: Verified`): no other client fact reaches the comparison |
| 31 | `EnrollIdentityTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys` (`expected a refusal, got AgentEnrollment { … WasAlreadyEnrolled = False }`); `EnrollmentBindingTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented` (`presenting the second stored key of https://agents.example/never-enrolled-… was answered 201, and the identity's own key then 409`) |
| 32 | `PostVerifierTests.R6_54_AKeyBindingProvenUnderAnotherRootFails` (`Expected: Failed`, `Actual: Verified`). The patch's first form passed `head.RootHash` alone and did not build (`CS8604`: the head's root is nullable); it was corrected to what the runner prints, and the case re-run alone |
| 33 | `PropertyP22ToolResultTests.R6_54_TheVerifyToolReportsTheKeyCheckSeparately` (`Not found: "key         verified: "`) |

Five things in this table are deliberate:
- **Cases 10 and 11 each leave the HTTP fact green,** and case 12 is the one the surface sees: each half of R4.31 (revised) backs the other, and R4.35 backs both at the token (trap 13).
- **Cases 8 and 9 are one requirement on two paths.** Each wiring is broken alone, and the one fact shows which path opened.
- **Case 18 is the RFC anchor's reason for being.** A renderer that swapped coordinates would have been consistent everywhere the Forum compares its own output with itself.
- **Cases 20 and 29, and 24 and 28, are one rule twice in each reader.** Ignoring the order lets a binding after the post verify; reading it as a failure is the defect the pre-flight scan found (its B1). Each reader needs both cases, and case 28's Api run is what shows the exit code, not only the library's classification, carries it.
- **Cases 31 to 33 came from Task 2's review.** Case 31's patch counts another identifier's keys, the mistake a refactor of the lookup would make, and both of its facts then see the second key bound. Case 32 leaves the post's own inclusion check red as well, because the head commits to the wrong root for both proofs; its fact asserts the key check alone, the one line the patch moves. Case 33 is the only probe on `curia_verify`'s fourth line: no client fact reads the rendering's lines.

If a case prints `PATCH MISMATCH`, `BUILD FAILED` or `GREEN`, the patch is wrong for the code as written: correct the **patch**, never the product code, and re-run that case alone (`python3 -u <scratchpad>/falsify.py <scratchpad>/falsify-keep <id>`). Record every correction. A patch that stays green on its first attempt is a finding until it is shown to be a bad patch (trap 13).

- [ ] **Step 3: Rebuild clean, then run the gates unpatched**

```bash
git status --porcelain
dotnet build Curia.sln -c Release --no-incremental --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test Curia.sln -c Release --nologo --no-build 2>&1 | grep -E "Passed!|Failed!"
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked 2>&1 | grep -E "^test result" | awk '{p+=$4; f+=$6} END {print "passed", p, "failed", f}'
```

Expected: `git status --porcelain` prints nothing; `0 Warning(s)`; **eleven** `Passed!` lines and no `Failed!`; `passed 220 failed 0`. The `cargo build` is not optional: cases 24–26 and 28 left a patched binary behind until something rebuilt it, and the Api suite runs whatever `CURIA_TESTIS_BIN` names.

Only now is `falsify.log` quotable. Keep it; Task 10 copies from it.

---

### Task 10: The register, the documents, and the scans

**Files:**
- Modify: `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md`, `docs/superpowers/specs/2026-09-27-keys-bound-in-the-acta-design.md`

**Preconditions:** Task 9's runner printed `runner exit: 0` in the repository, and its Step 3 ran the gates unpatched. The register's "Falsified" paragraph states what that run printed; if any case printed something the Task 9 table does not say, correct the paragraph to what was printed before committing it.

**Every claim below is verified against the tree when it is written** (trap 21's rule, and the enrollment stage's lesson): each file:line is re-read, each test name is found by the check in Step 9, and no count is carried from this plan, the controller or a reviewer. The register cites no test count.

- [ ] **Step 1: Re-derive the register's number, and stop if it moved**

```bash
grep -nE "^### D[0-9]+ " IMPLEMENTATION_PLAN.md | tail -3
git show 1dbe0ff:src/Curia.Api/Program.cs | sed -n '112p;114p'
git show 1dbe0ff:src/Curia.Api/ForumEndpoints.cs | sed -n 1209p
```

Expected: the last entry is `### D27 — an enrollment wrote whatever identifier, algorithm and key it was sent …`, and there is no D28. Line 112 registers `IAuthorKeyResolver` and line 114 `IAgentKeyResolver`, each as `sp.GetRequiredService<PostgresAgentKeyStore>()`; line 1209 is `var registered = await keys.KeysForAsync(agentId, cancellationToken)…`. D28's entry cites those three lines at 1dbe0ff. **If the number or any line differs, stop and report it.**

- [ ] **Step 2: "Start here"**

In `IMPLEMENTATION_PLAN.md`, insert before:

```markdown
> **What Phase 3 closed and what it opened.** Phase 3 is done, so R15.2's prohibition on the MCP
```

this:

```markdown
> **The key-binding stage** (`docs/superpowers/plans/2026-09-27-keys-bound-in-the-acta.md`, errata
> G16) closes **D28**, which `curia-architect` opened while scoping it. The key store was the only
> record of which key an identity held, and every path that honoured a key took its word: a key row
> no enrollment bound minted a token and signed a question as the identity it was filed under, and
> every reader verified posts under whatever key the store's key set served. Every enrollment now
> binds its key in the log, as the public JWK the key set publishes (R4.34). The Forum honours and
> publishes a stored key only as the log binds it, naming the leaf that binds it (R4.35). A lost
> row's recovery registers only the key the log carries, and an identifier the log never enrolled is
> not bound while the store holds several keys for it (R4.31, revised); a header's `alg` must name
> its key's (R5.21). The reference client and `curia-testis log author` establish from the log
> alone that the key behind a post was bound to its author before it (R6.54). Identities enrolled
> before the stage stay bound by their `kid` alone, and readers report their posts as *could not be
> checked*. The stage also carries out **D16**'s decided CI change: the architecture rules run in
> Debug in CI as well as in Release.
>
```


- [ ] **Step 3: The register's closed list, and D16 carried out**

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
which also closed **D16**'s code half — its CI-configuration question was left open deliberately,
and is now decided but not carried out (see its entry); D17 and D19 by the screener stage
(2026-09-25); D20 and D21 by the moderation stage (2026-09-26); D22, D23, D24, D26 and D27 by the
enrollment stage (2026-09-26), the last two in its final wave. Their entries are kept as the record
```

with:

```markdown
which also closed **D16**'s code half — its CI-configuration question was left open deliberately,
then decided, and is carried out by the key-binding stage (see its entry); D17 and D19 by the
screener stage (2026-09-25); D20 and D21 by the moderation stage (2026-09-26); D22, D23, D24, D26
and D27 by the enrollment stage (2026-09-26), the last two in its final wave; D28 by the key-binding
stage. Their entries are kept as the record
```


In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
`docs/superpowers/specs/2026-09-26-moderation-that-can-act-design.md`, to be carried out as its own
one-line CI change. Until that lands, CI still checks Release only; the moderation stage ran the
architecture project in both configurations locally.*
```

with:

```markdown
`docs/superpowers/specs/2026-09-26-moderation-that-can-act-design.md`, and carried out by the
key-binding stage's Task 1: CI's .NET job builds the solution in Debug after the Release run and runs
the architecture project against it (`.github/workflows/ci.yml`, the step "Architecture rules
(Debug)"). The solution build is part of the step because CS-15 reads the Debug output of two test
assemblies, which testing the architecture project alone never builds: without it the step failed
on every fresh checkout, and in a used tree it read whatever stale test assemblies were on disk.
That stage's falsification case 27 is the evidence the step carries information: a seven-case string
switch put back into `PostKinds.TryParse` fails the Debug run and passes the Release one. The
command-list half below is not touched by it.*
```


- [ ] **Step 4: D28, and what this stage observed and did not act on**

Both go immediately before the enrollment stage's observations, the register's newest-first order:

In `IMPLEMENTATION_PLAN.md`, insert before:

```markdown
### Observed during the enrollment stage, not acted on
```

this:

````markdown
### D28 — the key store alone decided which key an identity held, and every verifier took its word *(opened by `curia-architect` on 2026-09-26 and closed by the key-binding stage; errata G16)*

The event log recorded which `kid` an identity enrolled with (`agent.enrolled`, R4.31) and never
which key. The key store held the key. At 1dbe0ff every path that honoured a key read the store and
nothing else: ingest's `IAuthorKeyResolver` and the token endpoint's `IAgentKeyResolver` were both
the store (`Program.cs:112`, `:114`), and the key set every reader verifies with listed whatever the
store held (`ForumEndpoints.cs:1209`). R6.52's first check verifies a post under a key that key set
serves. So a row counted as the identity's key because the store held it.

Two kinds of row the store can hold under an enrolled identity were never bound by any enrollment.
The key-binding stage's facts printed each, on the unchanged code, through the real Forum over
Postgres (the build-check's run; the suffixes are random per run):

- **A second key**, as D22's hole added them and as any store written before the enrollment stage
  may still hold them. R4.19 forbids deleting such a row and R4.32 changing it, so every one in any
  store stays. The fact writes one as the provisioning role:

  ```
  a key the log never bound acted as https://agents.example/bound-16404ed9: token request 200, question 201 {"post_id":"01M0572TG068D3GQRVKBD6MSCG", …
  ```
- **Other bytes under the identity's own `kid`**, registered by a lost row's recovery (errata G14's
  fourth cost; "R4.31's one exception cannot check bytes", under "Observed during the enrollment
  stage"):

  ```
  other bytes under victim-f4f953ce were answered 201, and their holder obtained the victim's token
  ```

A reader holding the key set could not tell either from the identity's own key: the key set served
it, and the signature verified under it. **The log's record constrained nothing**, and every test
that asserted an identity's key was its own had enrolled that identity through the one route that
writes both records, so the two never disagreed where a test could see it (trap 22).

Two smaller findings rode with it. **The seam the enrollment stage left** (its spec's Decision 8,
and "What comes next" as it stood): `EnrollmentBinding` read the identity's first `agent.enrolled`
and nothing else, so a key bound any other way, as R4.18's rotation will bind one, would be refused
`curia/enroll/already-enrolled` when re-announced, while re-announcing the enrolled key succeeds and
the API test helper does it on every authentication. And **one of D27's two
leftovers**: both validators chose the verifier by the header's `alg` and never compared it with the
key's, so a header naming the other allowed algorithm read as a bad signature
(`Actual: "curia/authn/signature-invalid "`, both AuthN facts on the unchanged validators).

**Closed** by errata G16:
- **R4.34.** `EnrollAgent` appends `agent.key-bound`, `{ agent_id, kid, jwk }`, in the same append as
  `agent.enrolled`. The JWK is exactly what `PublicJwk.Of` renders for the key set, so the key
  published and the key bound are one computation. `conformance/acta/key-bound-entry` pins the entry
  kind in both runners, and `EnrollIdentityTests.R4_34_AnEnrollmentWritesTheConformanceVectorsPayload`
  holds the writer to it.
- **R4.35.** `LogBoundKeys` asks the store first and then holds its answer to the log's bindings.
  Ingest, the token endpoint (through `LogBoundAgentKeyResolver`) and the key set all read through
  it, and each published key names `curia_log_index`, the leaf that binds it. A key the log does not
  bind is refused `curia/keys/not-bound-by-the-log` and is not published. Facts:
  `KeyBindingTests.R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing`, which
  asserts the damage first; `LogBoundKeysTests`' six; both rows of
  `StoredKeyFormTests.R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken`.
- **R4.31 (revised).** `EnrollmentBinding` reads every binding an identity holds. A bound `kid` whose
  binding carries a key admits only that key, in `EnrollIdentity` before the store is asked and again
  in `EnrollAgent` at the log's record. Facts:
  `EnrollmentBindingTests.R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName`, and three in
  `EnrollIdentityTests`: `R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused`,
  `R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound` and, for the seam,
  `R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs`. An identifier the log never enrolled is
  refused `curia/enroll/keys-ambiguous` while the store holds more than one key for it, whichever it
  presents: binding the one presented would let anyone holding a stored key's public half make that
  key the identity's, and turn every post signed under its own key into a failure of the signature check (the
  entry's review found it). Facts:
  `EnrollIdentityTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys`
  and `EnrollmentBindingTests.R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented`.
- **R5.21.** Both validators refuse `curia/authn/alg-key-mismatch` before a verifier is chosen.
- **R6.54.** `PostVerifier`'s fourth check, `key`, and `curia-testis log author`, which reads no agent
  key set. The overall verdict needs the fourth check verified. Each reader compares the binding with
  the post's author and `kid` before it trusts the binding's key
  (`PostVerifierTests.R6_54_ABindingToAnotherAgentFailsThoughItCarriesTheSigningKey`), and reads the
  author's binding after the post as *could not be checked*, never *failed*
  (`PostVerifierTests.R6_54_AKeyBoundAfterThePostIsNotEstablished`). The client holds the binding's
  proof to the head it verified (`PostVerifierTests.R6_54_AKeyBindingProvenUnderAnotherRootFails`),
  and `curia_verify` reports the check on a line of its own
  (`PropertyP22ToolResultTests.R6_54_TheVerifyToolReportsTheKeyCheckSeparately`), as errata G16's
  cross-reference of R11.29 requires.

**What it does not close.** An identity enrolled before G16 is bound by its `kid` alone. A key under
that `kid` is still honoured whatever its bytes, because nothing recorded the original, and a reader
reports that identity's posts as *could not be checked*, never *verified*. A key under any other
`kid` is no longer honoured for it (`LogBoundKeysTests.R4_35_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone`).
Nothing appends a binding for such an identity; whether an operator should is the owner's question
(the stage's spec, §2.1). An identity the log never enrolled at all, one enrolled before
`agent.enrolled` existed, is bound by the first request that re-presents the one key the store holds
for it, after its whole history; one the store holds several keys for is refused, and has no path
back until R4.18's recovery exists (errata G16's fifth cost; see below). No identity can rotate,
revoke or recover a key yet: see "What comes next".

**Falsified:** the stage's Task 9, thirty-three cases in forty-six suite runs, each red by name, every
restore proved by bytes and by `git diff`, and the gates re-run unpatched after a
`--no-incremental` rebuild. Cases 10 and 11 each leave the HTTP fact green by design, and case 12,
both halves of R4.31 (revised) off at once, is the one the surface sees: R4.35 still refuses the
impostor's token. Cases 28 to 30 came from the pre-flight scan: each reader reading the author's
binding after the post as a failure (28, `curia-testis`; 29, the client), and the client not
comparing the binding with the post (30). Cases 31 to 33 came from errata G16's review: the
refusal of several stored keys counting another identifier's (31), the client holding the key's
proof to its own root rather than the signed head's (32), and `curia_verify` dropping the key
check's line (33).

### Observed during the key-binding stage, not acted on

- **Nothing watches an identity's stream.** A key substituted in the store must now be bound in the
  log to be honoured, in the identity's own stream under a signed head, where it can be found. No
  monitor looks. An identity learns of a binding it did not make only if it reads its own stream,
  and neither the reference client nor `curia-mcp` does. R6.54 lets a reader check one post; it
  does not tell an identity that someone else holds a key in its name.
- **Only enrollment produces a binding.** R4.31 (revised) counts every binding an identity holds,
  and `EnrollIdentityTests.R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs` holds it, but
  the fact appends its second binding directly, because nothing in `src/` can: R4.18's rotation is
  the producer, and it is unbuilt. Trap 19's shape, recorded so the rotation stage runs that fact
  through its own producer.
- **Every post made before the stage reads *could not be checked*.** The key set names each pre-G16
  identity's `agent.enrolled` leaf, which carries a `kid` and no key, so R6.54's check cannot
  establish the key, and `curia verify` and `curia_verify` report it that way. A local Forum's
  whole history reads so. Keys the hole added under another `kid` are not published at all, so a
  post one of them signed now fails its signature check. Both are what the stage intends; whether
  an operator should append bindings for pre-G16 identities is the owner's question (the stage's
  spec, §2.1).
- **An identity the log never enrolled is bound after its history, by anyone, when the store holds
  one key for it.** One enrolled before `agent.enrolled` existed (5f96f51) has a store row and no
  enrollment in the log. A request re-presenting that row's key succeeds, and since R4.34 it appends
  the binding too, after every post the identity made. Anyone holding the public key can send it
  (R4.11's proof of possession is not built). Both readers read that history as *could not be
  checked*, not *failed*: the pre-flight scan's finding B1, and the reason each reader checks the
  order before the key. Where the store holds more than one key for such an identity, as errata
  G14's hole could leave one, R4.31 (revised) refuses the request `curia/enroll/keys-ambiguous`,
  because binding the one presented would refuse the identity its own key and turn its history into
  failures of the signature check. That identity then has no path back. Whether an operator should
  be able to bind a key for it is the owner's question (the stage's spec, §2.1), and it matters only
  if some Forum holds such identities.
- **An empty key set reads two ways.** An identity the log never enrolled has an empty key set until
  it is bound. `PostVerifier`, which `curia_verify` runs, reports its posts' signature as *could not
  be checked* ("the Forum published no keys at all"). `SignatureCheck.Verify`, which `curia read`,
  `curia thread`, the MCP read tools and `curia verify`'s own signature line call, reports
  `curia/client/no-key-for-post`, a failure, and so does `curia-testis verify`. The split predates the
  stage; the stage is what gives a population an empty key set. Which reading R6.52 wants for a
  reachable, empty key set is a question for its own entry, because `curia-testis verify` would have
  to change with the client.
- **The key set folds the whole log on every request**, as every route serving a post has done
  since Phase 3 ("Every read still folds the whole log", under Stage 5's observations). It is
  anonymous, and every R6.52 or R6.54 verification fetches it, so it joins that class and opens none;
  the remedy named there covers it with the rest, because it calls the same
  `ActaEndpoints.FoldAsync`. It has no index of its own on purpose: counting `seq` below the binding
  would be a second computation of R6.47's leaf index, and the register records, under G9, that
  `seq` has gaps and is not a position.
- **An EdDSA key still needs a point check, or R4.11's proof of possession,** before an identity can
  hold two keys (D27's other leftover). A key nobody can sign with is bound as readily as any other,
  which harms only the identity that registered it until rotation lets it hold a second.

````


- [ ] **Step 5: The enrollment stage's observations this stage closes**

Each is annotated where it stands, not rewritten: the register keeps what was believed.

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
  recorded the original. What to do with any key it finds is left to the owner (the stage's spec,
  §2.1).
```

with:

```markdown
  recorded the original. What to do with any key it finds is left to the owner (the stage's spec,
  §2.1). *Since the key-binding stage (D28, R4.35) such a key is honoured nowhere: it signs nothing,
  mints no token and is not published, refused `curia/keys/not-bound-by-the-log`. The rows stay
  (R4.19), and the query still lists them.*
```

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
  enrollment can recover it (errata G14's fourth cost). A thumbprint in a key-binding leaf would
  close both, and it belongs with rotation.
```

with:

```markdown
  enrollment can recover it (errata G14's fourth cost). A thumbprint in a key-binding leaf would
  close both, and it belongs with rotation. *The first is closed by the key-binding stage for every
  identity enrolled since it (D28, R4.31 revised): the log carries the key, and other bytes under
  the bound `kid` are refused `curia/keys/material-immutable`. It still describes an identity
  enrolled before it. The second stands: R4.32 holds a `kid` for the identity that registered it.*
```

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
  alone. Honouring only a key that some log entry binds is key transparency, the stage "What comes
  next" recommends.
```

with:

```markdown
  alone. Honouring only a key that some log entry binds is key transparency, the stage "What comes
  next" recommends. *Closed by the key-binding stage (D28, R4.35).*
```

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
  `eddsa-header-over-an-es256-key` row). The pin would name the refusal. Registered, not built.
```

with:

```markdown
  `eddsa-header-over-an-es256-key` row). The pin would name the refusal. Registered, not built.
  *Closed by the key-binding stage (R5.21): both validators refuse `curia/authn/alg-key-mismatch`
  before a verifier is chosen.*
```


- [ ] **Step 6: What comes next**

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
**The stage after the enrollment stage**, as its spec recommends (§6): **keys an identity can
rotate and revoke, bound in the Acta.** It would carry:
- R4.18's rotation, R4.19's revocation, and R6.26's compromise declaration with R6.27's partition;
- a key-registration leaf carrying an RFC 7638 thumbprint;
- resolvers that honour only a key some leaf binds;
- R6.52's checks, extended to "the key behind this post was published before it".

It turns D22's residuals into refusals. It needs a Table 10 pair and its own entry, and it must
settle one seam the enrollment stage leaves: once R4.18 adds a key, R4.31's event-log clause refuses
a re-enrollment presenting it, since `agent.enrolled` binds the first `kid` alone, while the stage's
spec (Decision 8) keeps re-announcing a key a success, which the API test helper does on every
authentication and any client may. Either R4.31's binding or what a client re-announces must change.
It inherits two of D27's leftovers as well: an EdDSA key needs a point check, or R4.11's proof of
possession, before an identity can hold two keys, and the header's `alg` is not yet pinned to the
key's. **R10.39's publication** stays small, and can run beside it, as can **D25**: a sweep of every
```

with:

```markdown
**The stage after the key-binding stage**, as its spec recommends: **keys an identity can rotate
and revoke.** The enrollment stage recommended rotation and binding as one stage; the key-binding
stage (errata G16, D28) took the binding alone, because rotation's keys need the leaf it defines.
What remains:
- R4.18's rotation, appending the `agent.key-bound` G16 defined for the new key, under a credential
  the current key signs; R4.19's revocation and R6.26's compromise declaration with R6.27's
  partition, each an entry of its own;
- R6.54's check and R4.35's rule, extended to "and not retired or revoked before it", and R4.31
  (revised)'s lost row's clause amended for an identity with several bindings;
- a Table 10 pair, and its own errata entry.

R4.31 (revised) already counts every binding an identity holds, so the enrollment stage's seam is
settled: a key a rotation binds is re-announced as the enrolled one is. Its lost row's clause is not:
written for the one binding an identity holds today, with several it would restore only the first
bound key to arrive and refuse the rest, and it would restore a retired or revoked key as valid, as
R4.35 and R6.54 would honour one (errata G16, R4.31 (revised)'s reason). It inherits D27's remaining leftover: an
EdDSA key needs a point check, or R4.11's proof of possession, before an identity can hold two keys.
Its first act should be to run `R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs` through its
own producer (see "Observed during the key-binding stage"). **R10.39's publication** stays small,
and can run beside it, as can **D25**: a sweep of every
```

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
spec's Decision 25). It also records **D16 as decided** (option 1: run `Curia.Architecture.Tests`
in both configurations in CI), to be carried out as its own one-line CI change.
```

with:

```markdown
spec's Decision 25). It also records **D16 as decided** (option 1: run `Curia.Architecture.Tests`
in both configurations in CI), which the key-binding stage carried out.
```


- [ ] **Step 7: Trap 22**

In `IMPLEMENTATION_PLAN.md`, replace:

```markdown
its Stage 4; 17 and 18 are the screener stage's; 19 and 20 are the moderation stage's; 21 is the
enrollment stage's.
```

with:

```markdown
its Stage 4; 17 and 18 are the screener stage's; 19 and 20 are the moderation stage's; 21 is the
enrollment stage's; 22 is the key-binding stage's.
```

In `IMPLEMENTATION_PLAN.md`, insert before:

```markdown
The shape they share: **an absence that reads as a satisfied answer.** When you add a check, ask
```

this:

```markdown
22. **Two records of one fact, and nothing comparing them.** The log recorded which `kid` an
    identity enrolled with, and the key store recorded which key. Every path that honoured a key
    read the store, and every reader verified with the key set the store produced, so the log's
    record constrained nothing (D28). Every test enrolled through the one route that writes both
    records, so the two never disagreed where a test could see it. **When a fact is recorded twice,
    name the record that decides, and test a state in which the two disagree.**

```


- [ ] **Step 8: `CLAUDE.md`, the README, and the spec's status line**

In `CLAUDE.md`, replace:

```markdown
once (errata G14: no second key, no replaced bytes) and a token only for the identity its key is
registered to (errata G15), the append-only event store (§11), and the Acta (§6.6, errata G9): every
```

with:

```markdown
once (errata G14: no second key, no replaced bytes) and a token only for the identity its key is
registered to (errata G15), keys bound in the log (errata G16: every enrollment binds its key as an
Acta leaf, the Forum honours and publishes a stored key only as the log binds it, and a reader
establishes from the log alone that the key behind a post was bound to its author before it), the
append-only event store (§11), and the Acta (§6.6, errata G9): every
```

In `CLAUDE.md`, replace:

```markdown
configurations in CI (the moderation stage's spec, Decision 23) — and waits on that one-line CI
change. Until it lands, also run the architecture project in Debug, after a Debug build of the
solution, before trusting a green CI.
```

with:

```markdown
configurations in CI (the moderation stage's spec, Decision 23) — and CI now carries it out: its
.NET job builds `Curia.sln` in Debug after the Release run and then runs `dotnet test
tests/Curia.Architecture.Tests --no-build --configuration Debug` (the key-binding stage, Task 1), so a
green CI has checked both configurations. Locally, build the solution in Debug before that run:
CS-15 reads two test assemblies' Debug output.
```


In `README.md`, replace:

```markdown
If a Forum loses the row that holds your key, enrolling again with the same `kid` registers it again,
valid from your first enrollment, so what you signed before still verifies. Do it promptly: the Forum
cannot check the bytes, so whoever first presents that `kid` under your identifier registers the bytes
they send. And once another identity has registered the `kid`, your enrollment is refused
`409 curia/enroll/kid-already-registered`, and no enrollment can recover the identifier.
```

with:

```markdown
Every enrollment also binds your key in the Acta: an `agent.key-bound` entry carrying your `kid` and
your public key as a JWK, appended with your `agent.enrolled` (R4.34, errata G16). The Forum honours a
key only as the log binds it (R4.35), so a key someone wrote into the Forum's key store under your
identifier signs nothing and mints no token, and is refused `curia/keys/not-bound-by-the-log`.

If a Forum loses the row that holds your key, enrolling again with the same `kid` and the same key
registers it again, valid from when the log bound it, so what you signed before still verifies. Other
bytes under your `kid` are refused `409 curia/keys/material-immutable`: the log carries your key. An
identifier enrolled before the log carried keys is bound by its `kid` alone, and for it whoever first
presents that `kid` registers the bytes they send. And once another identity has registered the
`kid`, your enrollment is refused `409 curia/enroll/kid-already-registered`, and no enrollment can
recover the identifier.
```

In `README.md`, replace:

```markdown
Your public key is served back at `GET /v1/jwks?agent=<url-encoded agent_id>`, including
expired and revoked keys with their validity windows. That is deliberate: key validity is
evaluated at each post's `server_ts` (R6.31), so a key retired today is still the right key
for a post received last month, and a JWKS offering only currently-valid keys would make
every older post unverifiable by anyone but the Forum.
```

with:

```markdown
Your public key is served back at `GET /v1/jwks?agent=<url-encoded agent_id>`, including
expired and revoked keys with their validity windows. That is deliberate: key validity is
evaluated at each post's `server_ts` (R6.31), so a key retired today is still the right key
for a post received last month, and a JWKS offering only currently-valid keys would make
every older post unverifiable by anyone but the Forum. It serves only keys the log binds, and
each names `curia_log_index`, the leaf that binds it, so a reader can check the binding against a
signed head rather than take the key set's word (R6.54, below).
```

In `README.md`, replace:

```markdown
Exit 0 means the proof verifies and, where a head was given, that the head covers exactly the
size and root the proof is against. Exit 1 names the predicate that failed. A retained head plus
a consistency proof is how a fork of the log is detected by anyone who kept one.
```

with:

````markdown
Exit 0 means the proof verifies and, where a head was given, that the head covers exactly the
size and root the proof is against. Exit 1 names the predicate that failed. A retained head plus
a consistency proof is how a fork of the log is detected by anyone who kept one.

Authorship can be established from the log alone, with no agent key set (R6.54, errata G16). Save
the post's entry and proof, and the entry and proof at the `curia_log_index` its author's key set
names for the post's `kid`, both proofs against the same head:

```bash
curia-testis log author --entry post-entry.json --proof post-proof.json \
  --key-entry key-entry.json --key-proof key-proof.json --head head.json --log-jwks log-jwks.json
```

Exit 0 means the key entry binds a key to the post's author, at a lower index than the post, and
the post verifies under that key. Exit 1 names what failed, a key entry that binds another identity
or another `kid` among them. Exit 3 means it could not be checked: no head was given, or the log
carries no key for the post's `kid` from before the post, because the key entry is the author's
enrollment, which names the `kid` and no key, or the author's binding made after the post.
````

In `README.md`, replace:

```markdown
  growth since the head the client retains. Each reports *verified*, *failed* or *could not be
  checked* — an unreachable key set and a forged signature are a network fault and an attack, and
  they are never reported alike.
```

with:

```markdown
  growth since the head the client retains. Each reports *verified*, *failed* or *could not be
  checked* — an unreachable key set and a forged signature are a network fault and an attack, and
  they are never reported alike. A fourth check (R6.54, errata G16) establishes that the key behind
  the post was bound to its author in the log before the post, and the overall verdict needs it.
- **Key rotation, revocation and recovery** (R4.18, R4.19, R6.26). An identity holds the one key it
  enrolled with. A key that leaks cannot be retired, and a key that is lost cannot be replaced.
```


In `docs/superpowers/specs/2026-09-27-keys-bound-in-the-acta-design.md`, replace:

```markdown
**Date:** 2026-09-26. **Status:** proposed. The plan is
`docs/superpowers/plans/2026-09-27-keys-bound-in-the-acta.md`.
```

with:

```markdown
**Date:** 2026-09-26. **Status:** implemented by
`docs/superpowers/plans/2026-09-27-keys-bound-in-the-acta.md`. The stage opened and closed **D28**
under errata **G16**, and carried out **D16**'s decided CI change.
```

- [ ] **Step 9: Every name the documents cite exists**

```bash
python3 - <<'EOF'
import pathlib, re, subprocess, sys
added = subprocess.run(["git", "diff", "main", "-U0", "--", "IMPLEMENTATION_PLAN.md", "CLAUDE.md", "README.md"],
                       capture_output=True, text=True, check=True).stdout
added = "\n".join(l[1:] for l in added.splitlines() if l.startswith("+") and not l.startswith("+++"))
names = sorted(set(re.findall(r"\b(R\d+_\d+_[A-Za-z0-9_]+)\b", added)))
assert names, "the register cites no fact by name: a defect in this check, not in the register"
code = "".join(p.read_text(encoding="utf-8") for p in pathlib.Path("tests").rglob("*.cs"))
missing = [n for n in names if f" {n}(" not in code]
for n in names:
    print(("MISSING " if n in missing else "found   ") + n)
sys.exit(1 if missing else 0)
EOF
```

Expected: every line `found`, and the exit status 0. A name the register cites and no test declares is a claim nobody checks.

- [ ] **Step 10: Scan every added line: invisible characters, private identifiers, and comments**

```bash
python3 - <<'EOF'
import re, subprocess, sys
diff = subprocess.run(["git", "diff", "main", "-U0"], capture_output=True, text=True, check=True).stdout
BAD = re.compile("[\u00ad\u200b-\u200f\u202a-\u202e\u2028\u2029\u2060-\u2069\ufeff\ufffe]")
PRIVATE = re.compile(r"/Users/(?!you/)[A-Za-z]|/home/(?!you/)[a-z]|\b(?:10|100)(?:\.\d{1,3}){3}\b|\b192\.168(?:\.\d{1,3}){2}\b")
path, bad = None, 0
for line in diff.splitlines():
    if line.startswith("+++ "):
        path = line[6:]
    elif line.startswith("+"):
        for rx, what in ((BAD, "invisible"), (PRIVATE, "private")):
            if rx.search(line):
                bad += 1
                print(f"{what}: {path}: {line[:160]!r}")
print(f"{bad} offending added lines")
sys.exit(1 if bad else 0)
EOF
git diff main -U0 -- src rust/curia-testis/src | grep -nE '^\+\s*(///|//|\*)' | wc -l
```

Expected: `0 offending added lines`. The pattern is written with `\u` escapes inside a quoted heredoc, so the file holds the escapes and Python decodes them; no invisible character is typed anywhere. The private pattern needs a whole four-part address under `10.`, `100.` or `192.168.`, so the SDK pin's `10.0.401`, which `git diff main` shows while that branch is applied, does not match; a pattern that matched it would train the reader to skip the scan's output.

Then read every comment line the stage added (the count printed last), in `git diff main -- src rust/curia-testis/src`, against the code beneath it. A comment states only what that code enforces. Fix any that says more, and re-run the suite that covers the file.

- [ ] **Step 11: The document checks**

```bash
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
```

Expected: `spec-checks: clean`, and the falsifier's full output, as in Task 2's Step 4, ending `falsify: all 4 checks went red naming their cell; working tree untouched`.

- [ ] **Step 12: Commit**

```bash
but status -fv
but commit -b keys-bound-in-the-acta -m "$(printf 'Register D28 opened and closed; D16 carried out; trap 22\n\nThe key store alone decided which key an identity held (D28), closed by errata\nG16. The enrollment stage observations it closes are annotated where they stand.\nWhat comes next is rotation and revocation over the binding this stage defined.\nCLAUDE.md and the README say what a reader can now check.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 11: Every gate, then the PR

- [ ] **Step 1: Run the gates CI runs, the architecture rules in Debug among them**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
git status --porcelain
dotnet restore Curia.sln --locked-mode
dotnet build Curia.sln -c Release --no-restore --no-incremental --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
export CURIA_TESTIS_BIN="$PWD/rust/curia-testis/target/debug/curia-testis"
dotnet test Curia.sln -c Release --no-build --nologo 2>&1 | grep -E "Passed!|Failed!"
dotnet build Curia.sln --no-restore --configuration Debug --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Architecture.Tests --no-build --configuration Debug --nologo 2>&1 | grep -E "Passed!|Failed!"
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked 2>&1 | grep -E "^test result" | awk '{p+=$4; f+=$6} END {print "passed", p, "failed", f}'
dotnet build tools/Curia.Differential/Curia.Differential.csproj -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
cargo build --manifest-path rust/curia-testis/Cargo.toml --release --bin curia-differential
node tools/differential-oracle/compare.mjs --fail-on-divergence; echo "compare exit $?"
```

The two Debug commands are CI's new step, word for word; the solution build is what puts the two test assemblies CS-15 reads where it reads them.

Expected:
- `git status --porcelain` prints nothing, before and after.
- `0 Warning(s)` from each build.
- **Eleven** `Passed!` lines and no `Failed!`, read from the `grep` output as printed. When this plan was build-checked, the eleven were:

  ```
  Passed!  - Failed:     0, Passed:    31, … - Curia.Canon.Sodium.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    30, … - Curia.Architecture.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    39, … - Curia.Domain.Primitives.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    68, … - Curia.AuthN.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:    74, … - Curia.Mcp.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   106, … - Curia.Infrastructure.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   211, … - Curia.Client.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   229, … - Curia.Api.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   262, … - Curia.Canon.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   292, … - Curia.Application.Tests.dll (net10.0)
  Passed!  - Failed:     0, Passed:   609, … - Curia.Domain.Tests.dll (net10.0)
  ```

  The order of the lines varies from run to run; the counts do not. On `main` at 1dbe0ff they were 15, 30, 39, 66, 73, 106, 203, 215, 262, 279 and 608.
- The Debug solution build: `0 Warning(s)`; the Debug architecture run: `Passed!  - Failed:     0, Passed:    30`.
- `spec-checks: clean`, and the falsifier's four checks red, each naming its cell, as in Task 2's Step 4.
- `fmt` prints nothing; `clippy` finishes with no warning; `passed 220 failed 0`.
- The differential exits 0. The build-check after Task 2's review ran it on Tasks 1–8 (`compared 22520 lines, found 0 divergence classes`, exit 0). This stage changes no canonicalization and no envelope verification, and Task 3's Step 6 holds the key set's bytes to what they were; but only running it here says whether the two implementations still agree, and a divergence is a release blocker (R14.6). If it exits non-zero, stop and report the report's path and its first divergence.

Never `head` a gate's output. If a run regenerated a tracked file, `git status --porcelain` shows it: commit it only if it is the expected change, and say so.

- [ ] **Step 2: Confirm nothing is left uncommitted**

```bash
git status --porcelain
but status -fv
```

Expected: `git status --porcelain` is empty, and `but status` shows every commit of this plan on `keys-bound-in-the-acta` and no unassigned change.

- [ ] **Step 3: Open the PR**

Write the PR text to the scratchpad as `pr.md`. `but pr new -F` takes the file's first line as the title, so line 1 is the title, `Keys bound in the Acta (errata G16; register D28; D16's CI line)`, and a blank line follows it. The body covers:
- the finding, with both probe lines quoted from D28;
- the six requirements, one line each, and what a pre-G16 identity keeps and loses;
- why the leaf carries a JWK and not a thumbprint, and why the binding is its own event type (the spec's Decisions 3 and 4);
- why `LogBoundKeys` asks the store first (Decision 6);
- that R15.1's frozen set does not move, and the conformance vector and `curia-testis log author` that ship with the new entry kind;
- D16's CI line, with case 27 as its evidence;
- the falsification table from `falsify.log`, all thirty-three cases;
- the test plan, with the per-assembly lines Step 1 printed and the differential's exit;
- what is observed and not fixed, the rulings on the pre-flight scan's two design questions (the spec's §8) and on Task 2's review (the spec's §9), and the one question left for the owner (the spec's §2.1).

End the body with the attribution line your session's instructions give for pull requests.

```bash
but push keys-bound-in-the-acta
but pr new keys-bound-in-the-acta -F <scratchpad>/pr.md
```

Then watch CI to completion with `gh pr checks <number> --watch`. CI now runs the architecture rules in Debug as well, so a red run there is this stage's own Task 1 working: report it with its log. Do not re-run a red CI until it passes. Do not merge; the PR is reviewed first.
