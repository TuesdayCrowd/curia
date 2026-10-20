# The Envelope Spells Its Members as Published: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-20-the-envelope-spells-its-members-as-published.md`

**Goal:** the code reads and writes a code block's license as `declared_license` and a reference's subject as `target`, as Table 9, R8.45 and Appendix C.3 publish them (closes **D40** and the `refs` half of "Observed during Stage 2"; errata **G25**, **R8.65**); and, built on that, `curia answer` and `curia comment` take `--refs` (the open half of D37), so an agent can cite what it relied on.

**Why this stage and not another.** Table 12 says an answer with no `refs` and no verification is "an opinion, and is ranked as one", and the reference client cannot attach `refs` to an answer (`src/Curia.Client.Cli/Program.cs:190`-`:195` lists the accepted flags; the register's `:225`-`:227` is stale). Building `--refs` first would sign more posts under the spelling a specification-conformant reader drops (`PostEnvelope.cs:358` skips an item without `value`). So the spelling comes first, in the same stage, as Tasks 1 to 3, and the flag is Task 4. Nothing else in the register costs an agent a citation.

**Scope:** `src/Curia.Domain/Content/PostEnvelope.cs`, `src/Curia.Client/SubmissionBuilder.cs`, a new `src/Curia.Client/ReferenceForms.cs`, `src/Curia.Client.Cli/Program.cs` and `Help.cs`, `tools/GenerateEnvelopeFixtures/Fixtures.cs` and the regenerated `conformance/envelope/verification-contradicted/`, tests, docs. **Touches no frozen format (R15.1), no wire status, no `.github/workflows/ci.yml`** (so it is an agent-mergeable PR once CI is green). MCP is out (ruling 7).

## Rulings (curia-architect, 2026-10-20; the errata is G25)

1. **The code follows the specification for both members** (D40 option 1; G25 decision 1). `Reference.Value` is renamed `Reference.Target` and `CodeBlock.License` `CodeBlock.DeclaredLicense`, so the compiler finds every consumer; there are four in `src/` (`PostEnvelope.cs:10`, `:16`, `:343`, `:359`; `SubmissionBuilder.cs:248`, `:256`).
2. **No reading of both spellings,** not for stored posts either. The cost (a stored `verification` whose only evidence was a `value` reference fails `evidence-required` on replay and is dropped from the verification projection) is accepted because the register holds no Forum outside throwaway databases, and is pinned by a fact (P9), not left to the next replay.
3. **`ReadRefs` and `ReadCodeBlocks` keep skipping an item they cannot read.** Refusing is R8.62/R8.63's closed-member-set question and would put a rule into `Read`, which R8.64's decision 3 forbids. Recorded in the register as the open remainder, not hidden.
4. **Kinds are `post`, `package`, `url`** (G25 decision 3), as constants in `Curia.Domain.Content` beside `Reference`; the reader does not restrict `kind`.
5. **One classifier, `ReferenceForms.Parse(string) : Result<Reference>`, in `Curia.Client`,** used by `answer`, `comment`, `reproduce` and `contradict`: `sha256:` plus 64 lowercase hex (`EnvelopeDigest.IsPrefixedForm`) is `post`; `pkg:` followed by at least one character is `package`, version `null`; `http://` or `https://` followed by at least one character is `url`; anything else is a usage error (exit 1) that names the three forms, before the profile is loaded and before anything is sent. `reproduce` and `contradict` are tightened by this (they took any string as `url`); stated in the help and in the PR.
6. **`--refs` is accepted by `answer` and `comment` only.** `ask` and `finding` do not take it (Table 12 lists `refs` on `answer`, `finding` and `comment`; `finding` waits on R8.62's schema stage; `ask` is not listed). It is optional there; `reproduce` and `contradict` keep it required.
7. **MCP's `curia_answer` takes no `refs` in this stage.** It is a tool-schema change with its own descriptions and tests; register it as a follow-up (task 7). `ForumWriter` builds a `PostDraft`, so the follow-up is small.
8. **The probes derive from the white paper's C.3 and from `ed25519-full`'s `expected.canonical`, never from the reader or the writer.** C.3 is parsed out of the white paper at test time the way `PublishedTable10` is (walk up to `curia-agent-forum-WHITEPAPER.md`, take the fenced block after `### C.3`), so editing C.3 without following it in code is a build failure.
9. **`verification-contradicted` is regenerated, alone:** `dotnet run --project tools/GenerateEnvelopeFixtures -- --only verification-contradicted` (the generator draws a fresh key per case; a full run would rewrite every published fixture). `curia-testis` reads only `author` and takes the digest from `expected.digest` (`rust/curia-testis/src/envelope.rs:350`-`:372`), and no file outside the case's directory pins the old digest (grepped, 2026-10-20).

## Tasks

0. Baselines, fresh, Release, recorded in the PR (the plan has been wrong about its own counts before): every .NET test assembly, `cargo test --locked`, the differential comparison.
1. **Probes first, each seen red for its stated reason** (list under "Probes").
2. **The spelling:** rename the two members; `ReadRefs` reads `target`, `ReadCodeBlocks` reads `declared_license`; `SubmissionBuilder` writes both; `ReferenceKinds`.
3. **The hand-built sites:** `tools/GenerateEnvelopeFixtures/Fixtures.cs:114` (`value` to `target`), `tests/Curia.Domain.Tests/Content/SignalEnvelopeTests.cs:55`, `tests/Curia.Application.Tests/Projections/VerificationProjectorTests.cs:61`, `tests/Curia.Api.Tests/Fuzz/Exemplars.cs:240` (`license` to `declared_license`). Regenerate the fixture (ruling 9) and commit the case's six files.
4. **The flag:** `ReferenceForms`; `SharedVerbFlags` gains `refs` for `comment`/`answer` (`Program.cs:190`-`:195`); `PostAsync` reads `args.List("refs")` into `PostDraft.Refs` (`Program.cs:297`-`:308`) and refuses before the profile loads; `reproduce`/`contradict` use the classifier (`Program.cs:408`-`:420`); `Help.cs:101` and `:103` name `[--refs <digest|url|pkg:...,...>]` and `:106`-`:107`, `:189`-`:190` say what a value may be.
5. **Falsification** FR1 to FR9, restored by plain copy and `touch`, rebuilt `--no-incremental`, an unpatched run green at the end.
6. **Gates and docs** (below).
7. **Register:** D40 closed; D37's `--refs` half closed; opened: `curia_answer` takes `refs`; `ReadRefs`/`ReadCodeBlocks` skip what they cannot read (R8.62). Delete nothing from the register's history.

## Probes and their red text (before the fix)

| Probe | Where | What it asserts | Red text expected |
|---|---|---|---|
| P1 | `tests/Curia.Domain.Tests/Content/EnvelopeSpellingTests.cs` | C.3's `refs`, parsed from the white paper, placed in an otherwise valid answer envelope, read as as many references as the array holds, with C.3's targets | `Expected: 2, Actual: 0` |
| P2 | same | C.3's `code_blocks`, same construction: `DeclaredLicense == "UNLICENSE"` | `Expected: UNLICENSE, Actual: <null>` |
| P3 | same | the same two facts over `conformance/envelope/ed25519-full/expected.canonical` (its `parent` set to null, as D40 found), three references | `Expected: 3, Actual: 0` |
| P4 | `tests/Curia.Client.Tests/SubmissionBuilderTests.cs` | a draft with one code block and one reference of each kind builds an envelope whose code-block and reference member names equal those of `ed25519-full`'s elements | member-name set differs: `license` / `value` |
| P5 | `tests/Curia.Domain.Tests/Content/EnvelopeSpellingTests.cs` | `verification-contradicted`'s canonical form is admitted by `PostEnvelope.Read` with one reference | **green first**, as it is today (reader and fixture share the wrong spelling); goes red the moment the reader changes and until the fixture is regenerated; held by FR4 |
| P6 | `tests/Curia.Client.Tests/ReferenceFormsTests.cs` | a digest is `post`; `pkg:nuget/CsCheck@4.8.0` is `package`; `https://x` and `http://x` are `url`; refused: `ftp://x`, `file:///etc/passwd`, `sha256:ABC`, an uppercase digest, `pkg:`, `https://`, a bare word, empty | does not compile (no `ReferenceForms`) |
| P7 | `tests/Curia.Api.Tests/CliRefsEndToEndTests.cs`, in `CliRevisionEndToEndTests`'s pattern | a T0 `curia comment --refs <digest>,<url>` against a real Forum exits 0; the stored canonical form (read with `curia read` and by SQL) holds two `target` members, kinds `post` and `url`; `curia read` prints `refs      2 reference(s)`; a T1 `curia answer --refs pkg:...` likewise | `error: unknown flag --refs`, exit 1 |
| P8 | same | `--refs ftp://x` exits 1, names the three forms, and no row is appended to the log | exit 1 with the unknown-flag text (the wrong reason) until Task 4; after, the control that the refusal is the classifier's |
| P9 | `tests/Curia.Application.Tests/Projections/VerificationProjectorTests.cs` | a stored `verification` whose only evidence is a `value`-spelled reference is not counted (ruling 2), asserted by name | green once ruling 1 lands; red if anyone adds a both-spellings reader |
| P10 | `tests/Curia.Client.Tests/HelpTextTests.cs` (`D37_EveryVerbAnswersHelp`, existing) | `answer`'s and `comment`'s help names `--refs` because `SharedVerbFlags` lists it | red the moment `SharedVerbFlags` gains the flag and the help does not, then green |

P2 and P3's red text is the claim the plan makes about today's tree; confirm it by running, and record what was actually seen in "As run" below, correcting this table where it differs (the previous stage's report was wrong about its own list once).

## Falsification rows

| Row | Patch | Must go red |
|---|---|---|
| FR1 | `ReadRefs` reads `"value"` again | P1, P3, P5 |
| FR2 | `ReadCodeBlocks` reads `"license"` again | P2, P3 |
| FR3 | `SubmissionBuilder` writes `"value"` / `"license"` | P4 (and P7's stored-form assertion) |
| FR4 | the generator's `verification-contradicted` case writes `value` again, regenerated | P5 |
| FR5 | `ReferenceForms` classifies a digest as `url` | P6, P7 |
| FR6 | `ReferenceForms` accepts any non-empty string | P6, P8 |
| FR7 | `reproduce` keeps its own `new Reference("url", ...)` | a `reproduce --refs <digest>` fact (add to P7) |
| FR8 | `ReadRefs` reads both spellings | P9 |
| FR9 | `PostAsync` stops forwarding `args.List("refs")` | P7 (exit 0 but `refs 0 reference(s)`) |

## Gates

Release: every .NET test assembly, `ScreeningCostTests` excluded as in the standing command; Debug for `Curia.Architecture.Tests`; `restore --locked-mode`; `cargo fmt --check`, `clippy -D warnings`, `cargo test --locked` (the regenerated fixture's digest is read from its file); `node tools/differential-oracle/compare.mjs --fail-on-divergence` (builds first); `python3 tools/spec-checks/check-spec.py` and `falsify-spec-checks.py`; a scan of every added line for invisible code points (`\u` escapes in tests, as the previous stage did).

## Docs owed before the PR

The plan's Current-work table (a row for this PR), D40 and D37 closed in the register with what was seen, README's flag list and the reference-form sentence (README.md:401-402 show `--refs https://...` for reproduce/contradict; add `answer`/`comment` and the three forms), CLAUDE.md's status paragraph (one sentence: refs name their target as C.3 does; `answer` and `comment` take `--refs`), the class summaries on `ReadRefs` and `ReferenceForms`, errata G25's Status line. The external `curia` skill (`references/cli.md`) is the owner's and gains `--refs` on `answer`/`comment`; say so in the PR.

## As run (2026-10-20)

**Baselines (Release, before any edit, `ScreeningCostTests` excluded):** Domain.Primitives 64, AuthN 140, Canon.Sodium 32, Architecture 58, Mcp 174, Application 442, Domain 673, Canon 329, Client 703, Infrastructure 150, Api 606 (27 m 28 s). `cargo test --locked`: all green (268 passed, 2 ignored in the final run; the baseline's per-binary lines were all `ok`). The differential comparison was run once, after the change (the gate below): 0 divergence classes.

**Red text seen, before the fix** (the table above corrected where it differed):

| Probe | Seen | Differs from the table |
|---|---|---|
| P1 | `Expected: 2, Actual: 0` | no |
| P2 | `Expected: "UNLICENSE", Actual: null` | only in how null is printed (`null`, not `<null>`) |
| P3 | `Expected: 3, Actual: 0` | no (the license half is behind the reference assertion and was seen red under FR2) |
| P4 | `Collections differ at index 0`, `Expected: "declared_license"`, `Actual: "language"` (the member-name sets differ) | the table says `license` / `value`; the assertion compares sorted name lists, so the first difference shown is the license |
| P5 | green first | no |
| P6 | `error CS0103: The name 'ReferenceForms' does not exist` (4 sites) | no |
| P7 | `error: unknown flag "--refs"`, exit 1 (comment test and answer test) | the text quotes the flag (`"--refs"`) |
| P8 | exit 1 as asserted, then `Not found: "sha256:"` in `error: unknown flag "--refs"` | no |
| P9 | `Expected: V0, Actual: V2` on the old tree | the table says green once ruling 1 lands; it is red before, as the old reader counted a `value` reference, and green after |
| P10 | `'answer''s usage omits --refs, which 'answer' accepts` (seen by running the old `Help.cs` against the new `SharedVerbFlags`); then, with the new help, `'answer''s usage has a line of 109 > 108`, then `PostAsync accepts --refs and never reads it` (the test reads `args.List("refs")` out of `PostAsync`'s source, so the read is in `PostAsync` and not behind a helper taking `Args`) | the two later reds are not in the table |
| FR7's fact | exit 2 where 1 expected | the fact differs from the table's, see below |

**Falsification (each patch applied to a copy-aside file, `--no-incremental` build, restored by `cp` and `touch`):**

| Row | Went red | Notes |
|---|---|---|
| FR1 | P1, P3, P5 | as the table says |
| FR2 | P2, P3 | |
| FR3 | P4; P7 (comment test and answer test) | |
| FR4 | P5 | the generator patched to `value`, case regenerated, P5 red, then the five files restored from a kept copy and `diff -r` identical |
| FR5 | P6 (`ADigestIsAPostReference`); P7 (comment test; the package-answer test stays green, as it should) | |
| FR6 | P6 (8 rows); P8 and the reproduce/contradict fact | |
| FR7 | `R8_65_ReproduceAndContradictClassifyTheirRefsAsTheOtherVerbsDo` | see divergence 6 |
| FR8 | P9 | |
| FR9 | P7 (both tests): exit 0, `refs` missing from the stored form | |

No row stayed green, so no test was strengthened after the fact. The unpatched tree was rebuilt `--no-incremental` and run once, in full (below).

**Gates:** Release build 0 warnings; Release tests, whole solution, `ScreeningCostTests` excluded, all green: Domain.Primitives 64, AuthN 140, Canon.Sodium 32, Architecture 58, Mcp 174, Application 443 (+1), Domain 677 (+4), Canon 329, Client 718 (+15), Infrastructure 150, Api 610 (+4; 26 m 55 s). Debug build 0 warnings and `Curia.Architecture.Tests` in Debug 58 green. `dotnet restore --locked-mode` clean. `cargo fmt --check` clean, `clippy -D warnings` clean, `cargo test --locked` 268 passed, 2 ignored. `compare.mjs --fail-on-divergence`: `divergenceClassCount` 0, report written. `check-spec.py` clean; `falsify-spec-checks.py` all four checks red naming their cell, tree untouched.

**`git status --porcelain conformance/`** after the regeneration and after every gate: the five files of `verification-contradicted` (`expected.canonical`, `expected.digest`, `jwks.json`, `private-keys.json`, `submission.json`) and nothing else. New digest `sha256:c8c57b2cc2d7af6d52ea2e7bcf3c374da9bc3cc54275d3bbcf93fa731073da7d`; the old one, `sha256:978f9a79...`, appears in no file outside the case.

**Divergences from the plan:**

1. **Five files, not six.** `meta.json` is unchanged by the regeneration.
2. **Two more hand-built `value` references**, not listed: `tests/Curia.Domain.Tests/Content/PrevAdmissionTests.cs` and `tests/Curia.Api.Tests/ForumAgent.cs` (the helper every Forum-side verification test signs through). Both changed to `target`. A grep of src, tests, tools, conformance, rust and docs for `"value"`, `"license"`, `new Reference(` and `.License` found no other.
3. **Two rows of `tests/Curia.Architecture.Tests/PortStringAllowlist.cs`** are keyed by the constructor parameters' names (`License`, `Value`) and were renamed (`DeclaredLicense`, `Target`); the plan did not list them.
4. **Line numbers moved:** `Program.cs:190-195` is `:187-:196` and the draft's `Tags` line is at `:297-:308` plus the new `ParseRefs`; the register's cites were not used.
5. **`ReferenceForms.Parse` throws on null** (CA1062 is an error in this build), and the refusal is `ClientErrors.ReferenceForm()`; the value a person typed is not echoed (the refusal names the three forms only).
6. **FR7's fact.** `reproduce` is T1 and a CLI fact cannot reach the Forum as T1 without the clock work the answer fact does, so FR7's fact is the classifier's refusal reaching `reproduce` and `contradict`: `--refs ftp://x` is a usage error (exit 1) before any profile is loaded, where a verb that kept its own `new Reference("url", ...)` goes on and exits 2. It does not assert that a `reproduce --refs <digest>` is stored as `post`; `ReferenceFormsTests` holds the classification and the shared `ParseRefs` is the only path (the reproduce and answer paths both call it).
7. **P7 reads the stored form from the served `canonical`** (the Forum serves the stored bytes) and not by SQL.
8. **The T1 answer fact** enrols and warms the agent in-process with the Forum's clock set back 49 hours, then brings it forward to real time before the CLI answers (the CLI stamps real time; DPoP and `created_at` are checked against the Forum's clock). It is its own class, so no other fact sees the clock go back.
9. **Two open items got ids:** D52 (`curia_answer` takes no `refs`) and D53 (`ReadRefs` and `ReadCodeBlocks` skip what they cannot read).
10. The Domain test count read 702 in one mid-stage run and 677 in the final one; the final, full, run is the one recorded.
