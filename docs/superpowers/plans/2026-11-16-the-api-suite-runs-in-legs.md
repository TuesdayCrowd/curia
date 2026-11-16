# The Api Suite Runs in Legs: Implementation Plan (PR CI-1; register item only, no errata entry)

**Slug:** `the-api-suite-runs-in-legs`. The controller installs this file as `docs/superpowers/plans/2026-11-16-the-api-suite-runs-in-legs.md`. Branch name: `api-suite-in-legs`. Task briefs and reports live under `.superpowers/sdd/2026-11-16-ci-api-legs/`. Decision record: `.superpowers/sdd/2026-11-15-ci-time/` (`reconciled.md`, `hopper-decision.md`, `hardin-decision.md`, `api-class-times.tsv`, `two-classes.txt`, `artifacts/fuzz-timings/`).

Written 2026-11-16 against `main` `cce6f034` (#158). PR #159 (item 288) is open and touches no CI file; this plan shares no file with it except the register's "first free" sentence (see Identifiers).

**Owner's merge: YES, the whole PR.** It touches `.github/workflows/ci.yml` (CLAUDE.md: a PR touching it is the owner's to merge). The last commit is `ci.yml` alone, so the owner can read the workflow change by itself and the controller never merges it.

## Why this PR first

Both doctrine agents rank it first and both say it does not wait on anything. The Api step is 112 to 134 CI minutes on one runner; one test of 3,019 local seconds is 64% of the suite's summed time and `AgentWriteGateEndpointTests` is a further 23%. Running them on separate runners removes no test, no row and no repetition, loses no case, and reverts by reverting one file. Nothing in CI-2 or CI-3 is needed for it to pay.

## The four states

| Requirement | Specified | Implemented | Discharged | Falsified |
|---|---|---|---|---|
| Every Api test class runs in exactly one leg (hopper H4, hardin D2) | no normative text; a plan ruling | not yet | not yet: `check` lists the whole assembly and each leg's filter with `dotnet test --list-tests` and compares multisets; the listing comes from the test runner's discovery, not from the filter text it checks (vacuity question 4) | not yet: FCI1 to FCI4 |
| A red leg, a cancelled leg and a missing leg each turn the required check `.NET — build and test` red | no | not yet | not yet: probe branch, FCI5 to FCI7 | not yet |
| CI and the developer run the same steps (D16's first half) | register D16 | preflight reads `ci.yml` and today **dies** on a matrix expression | not yet: `--list` over the draft workflow names every job as run or left out | not yet: FCI8 |
| The closed fuzz stays on every PR (owner, 2026-11-04) | owner's word | yes: the fuzz leg runs on every PR | the fuzz leg's TRX is accounted by the gate | n/a |

No requirement of the white paper or errata is touched. R14.10 says what the fuzzer does, not where it runs; D16 is a register entry about CI and the developer running the same steps, and this PR keeps its promise rather than changing it. **Errata: none.** Register: one new item (see Identifiers).

## The decisions

### 1. How the legs share the build: they do not. Each leg builds what it needs.

Cost, from the artifacts in hand:

| Option | Wall time added to the critical leg | Runner minutes added | New moving part |
|---|---|---|---|
| Rebuild per leg | about 3 to 4 (restore and build of `Curia.Api.Tests` and its five project references; no model fetch, no Architecture build) | about 3 to 4 per leg, so 9 to 12 for three legs | none: the leg is the existing steps, run on another runner |
| Build job, upload `tests/Curia.Api.Tests/bin/Release/net10.0` (292 MB uncompressed here) plus the Rust binary, legs download | about 4 (build) + 0.5 to 1 (upload) + 0.5 to 1 per leg (download), so no better and usually worse | about 4 once, plus transfer | an artifact whose `--no-build` run depends on `obj/` assets and absolute paths matching; a failure mode that only appears in CI |
| `actions/cache` of `obj/` and `bin/` keyed on the tree hash | a miss on every PR (the tree is new each push), so it is the rebuild plus the cache write | more | a cache that never hits |

**Choice: rebuild per leg.** The repository is public (`gh repo view` says `PUBLIC`), so hosted-runner minutes are not a cost, and the wall time is set by the longest leg, which rebuilding does not lengthen. The artifact route adds a hop to the critical path and a failure mode, to save minutes nobody pays for. **Revisit when** CI-2 lands and a leg's build is more than about a quarter of its own wall time on the critical path (a shard of about 20 CI minutes carries about 4 of build), or if the free tier's 20 concurrent jobs is ever approached (it would be 9 jobs after CI-2). Both are measurements the first CI run of the new shape gives.

A leg builds **the Api test project's closure only** (`dotnet restore tests/Curia.Api.Tests --locked-mode`, `dotnet build tests/Curia.Api.Tests --no-restore -c Release`), not `Curia.sln`: the whole-solution locked restore and build stay in the core job, which is where CS-3 is checked. Task 1's step 3 proves the closure suffices (the `rest` leg run from an export with only that build). If it does not, the leg builds `Curia.sln` and the plan's cost line moves by about a minute.

A leg does **not** fetch the embedding model: the Api suite replaces the embedder with the hashed adapter through `ForumFixture`'s door, the current `Test (Api)` step sets no `CURIA_EMBEDDING_MODEL_DIR`, and `Curia.Embedding.Tests` is in the core job. Task 1 runs the `rest` leg with the variable unset and records it green.

### 2. The legs, whole classes only, one source of truth

`tools/ci/api-legs.json`:

| Leg | Selects | Why |
|---|---|---|
| `fuzz` | `FullyQualifiedName~Curia.Api.Tests.Fuzz.` | the namespace, with the trailing dot, so a class named `FuzzSomething` elsewhere is not captured; holds R14.10's closed pass (3,019 s local) and the other fuzz classes (about 40 s) |
| `bursts` | `FullyQualifiedName~Curia.Api.Tests.AgentWriteGateEndpointTests.` | 1,085 s local, 22.8%; BRB18, BRB19, BRB20 are 966 s of it. Split further in CI-2 |
| `rest` | **the complement**: `FullyQualifiedName!~<fuzz>&FullyQualifiedName!~<bursts>` | about 600 s local over about 200 classes |

`rest` is the complement **by construction**, so a class renamed out of a named leg is run in `rest` rather than in no leg. That is the hazard hopper named (a class renamed so that no filter matches it runs nowhere and the suite stays green), closed by the shape and not by remembering. What remains is the opposite hazard, a **named leg that selects nothing** (the rename the other way: `bursts` renamed away leaves the leg empty, and `dotnet test` over a filter that matches nothing is expected to exit 0; Task 1 step 0 runs it and records the exit code, because the whole plan's emptiness check rests on it). `check` fails on an empty leg.

The filters live in `api-legs.json` and `ci.yml` asks for them by leg name (`python3 tools/ci/api-legs.py filter <leg>`), so the partition has one definition: the checker, preflight and CI read the same file.

### 3. The checker, and what "exactly once" is compared against

`tools/ci/api-legs.py` (stdlib only):

- `filter <leg>` prints the leg's filter (the complement leg prints the conjunction of negations).
- `check [--out FILE]` runs `dotnet test tests/Curia.Api.Tests --no-build -c Release --list-tests` once with no filter and once per leg, and fails on: a leg that lists no test; a test listed by two legs (multiset, so repeated theory rows are not folded); a test in the whole listing that no leg lists; a test a leg lists that the whole listing does not. Prints the whole count and each leg's. `--out` writes `{leg: {class: count}}`.
- `judge(whole, legs)` is a pure function; `check` calls it, and so does the falsifier below.
- `account --partition FILE --trx DIR` (the gate's step): every leg in the partition has exactly one TRX; each TRX's executed count **per class** equals the partition's; no TRX names a class the partition does not, and none reports a failed or not-executed test. Per class, not per full name, because the listing's and the TRX's display names for a theory row may differ in format (Task 1 compares them and tightens to full names if they are equal).

`tools/ci/falsify-api-legs.py` runs `judge` on synthetic listings and asserts on the message, the way `tools/spec-checks/falsify-spec-checks.py` does: a test in two legs, a test in none, an empty leg, a leg test the whole listing lacks, and `account` with a missing TRX, an extra class and a failed test. It needs no toolchain, so it runs in the `spec` job beside `falsify check-spec`.

### 4. The gate keeps the required name

The ruleset `protect-main` requires four contexts: `Specification checks`, `Rust — curia-testis`, `Differential — C# vs curia-testis vs oracle` and `.NET — build and test`. A new `api` job would not be required, so a red leg could merge. **A job skipped because a `needs` failed is reported skipped, and a skipped required check does not block.** So:

- `dotnet` (id kept; `name:` becomes `.NET — core (build, architecture, all but Api)`) keeps its steps minus `Test (Api)` and the fuzz-timings upload, and gains the `check` step and the upload of the partition.
- `api` is the matrix job (`strategy.fail-fast: false`, `leg: [fuzz, bursts, rest]`, `name: .NET — Api (${{ matrix.leg }})`).
- `gate` has `name: .NET — build and test` (the required context, unchanged), `needs: [dotnet, api]`, **`if: ${{ always() }}`**, fails unless both results are `success`, then downloads the partition and the TRX files and runs `account`. No ruleset edit is needed, which is the point: the ruleset is the owner's and a name left alone cannot drift from it.

### 5. What preflight must change to keep mirroring CI

Read against `tools/preflight/preflight.py` as it stands:

1. `parse_workflow` takes every job with a `run:` step. A matrix job's steps hold `${{ matrix.leg }}` and the gate's hold `${{ needs.* }}`, and `substitute` ends the run with `an expression it cannot fill`. **Preflight would exit on the first run after the workflow lands**, which is also its red-first probe (FCI8 control).
2. It must also not run a matrix job's steps three times. Rule: a job whose block carries `needs:` or `strategy:` is **fanned out by CI** and is listed under "left out" with the reason, never run and never an error. The core job, `spec`, `rust` and `differential` run as before.
3. `SLOW_STEPS = {("dotnet","Test (Api)")}` names a step that no longer exists in `dotnet`. It becomes an empty set (kept: a future slow step is added there), and the Api suite is, as before, represented by the classes this branch changed.
4. The `check` step is in `dotnet`, so preflight runs it automatically (no edit: this is the mechanism working). It needs the Release build the job already made and no Postgres.
5. New: `--list` prints, per job, its run steps and its left-out jobs and exits without running (the probe for item 8 below). New: `--leg NAME` runs the Api suite with that leg's filter from `api-legs.json`, replacing a hand-typed `--api`.
6. `tools/preflight/README.md`: "Only `Test (Api)` is left out" is replaced by the fanned-out rule; the legs are named; `--leg` and `--list` documented.
7. `tools/preflight/falsify-preflight.py` (new, run in the `spec` job): over synthetic workflows, a matrix job and a `needs` job are left out and listed; a plain job's steps are listed; a workflow with no `run:` step still raises `SystemExit` with the shape message. Red before the change (the matrix workflow raises), green after.

What preflight still cannot catch, to be added to its README: a change to a **leg's own steps** (they are fanned out, so not run). The `--leg` flag and the draft workflow's review cover it by hand; the first CI run covers it for real.

### 6. Timeouts, timings, artifacts

- `timeout-minutes`: fuzz 150, bursts 90, rest 90, core 60, gate 10. A leg that hangs now fails inside the hour it would have wasted.
- `CURIA_FUZZ_TIMINGS` is exported in the fuzz leg only (`export` in the step's shell: an empty `env:` value would be the empty string, which the fuzzer's `File.AppendAllTextAsync` rejects). The `fuzz-timings` artifact is uploaded from the fuzz leg with the same name, so CI-2 and CI-3 read it as before.
- The screening-timings uploads and their line-count pins stay in the core job unmoved: `ReferencePasswordTests` reads `ci.yml` for `test "$(wc -l < "$CURIA_SCREEN_TIMINGS")" -eq 28` as a substring; the new workflow must still contain both pins verbatim. Task 1's draft is checked by running that fact against it.

## Tasks

Each task: probe red first, then the change, then green; a build with 0 warnings; the privacy and invisible-character scans; no version-control writes by the implementer (the controller commits).

**Task 1. The checker, the falsifiers, the preflight change and the draft workflow** (`task-1-ci1-brief.md`). One implementer. Step 0 measures: the filter-matches-nothing exit code; the `--list-tests` output shape and time with and without Postgres reachable; closure-only build sufficiency; the `rest` leg without the model variable; TRX name format. Then `tools/ci/api-legs.json`, `api-legs.py`, `falsify-api-legs.py`, preflight and its falsifier, README, and a draft `ci.yml` in the scratch directory that preflight `--workflow` and `--list` read. **Not in this task:** `ci.yml` itself, any doc. Reports FCI1 to FCI4 and FCI8 counted by script.

**Task 2. Documents** (controller or architect, after Task 1 is committed): `CLAUDE.md`'s Build and test block (the `check` command joins the list CI runs; "CI runs the Api suite in three legs" in one clause; the preflight sentence); `IMPLEMENTATION_PLAN.md`'s Current work and Next stage, one register item (below), and a trap **only if Task 3's FCI6 shows it**. Re-read both files immediately before the edit and recompute the identifiers then.

**Task 3. `ci.yml`, the last commit, alone, and the probe branches.** The controller copies the reviewed draft into `.github/workflows/ci.yml` as the PR's final commit (nothing else in the commit), opens the PR, and **before asking the owner to merge** runs the probe branches below. The PR's own CI run is the first real run of the new shape and is read for numbers (times per leg and per step, the count the gate accounts, the artifact sizes).

## Probes and falsification

| ID | What | Expected | Where |
|---|---|---|---|
| FCI1 | `bursts` filter misspelled (`AgentWriteGateEndpointTest5.`) | `check` red: "leg bursts lists no test"; and `rest` (the complement of a filter that matches nothing) lists the whole class, so nothing is lost, only the leg is empty | local, real `dotnet` |
| FCI2 | `fuzz` filter widened to `Curia.Api.Tests.` | red: tests "in two legs: fuzz, bursts" (the complement is of both named filters, so only named overlap is red) | local |
| FCI3 | `rest` given a fixed filter in place of the complement, missing one class | red: "in the whole listing and in no leg" | local |
| FCI4 | `bursts` filter widened to `~AgentWrite` (over-captures another class) | **green by design**: the class runs once, in `bursts`. Recorded to show the check is "exactly once", not "in the intended leg". A bursts leg that grew is a timing matter read in the PR's run | local |
| FCI5 | a throwaway branch from the PR tip with one failing fact in a `rest` class; the probe's workflow runs only the `rest` leg and core | the leg red, `gate` red, `.NET — build and test` red; the PR shows merging blocked | CI, a draft PR never merged (precedent: the `ci-falsification-probe` branch) |
| FCI6 | same, with `if: always()` removed from `gate` and the core job made to fail at its first step | `gate` reported **skipped**; read whether the PR is mergeable. If it is, that is trap-worthy (a skipped required check passes) and is the reason the `always()` line exists | CI, same draft PR style |
| FCI7 | same, with the TRX upload removed from one leg | `account` red: "no TRX for leg rest"; the gate red | CI |
| FCI8 | preflight over the draft workflow: **control** on the unmodified `preflight.py` (it must exit with `an expression it cannot fill`), then on the changed one | red, then `--list` showing `api` and `gate` left out and `dotnet`, `spec`, `rust`, `differential` run | local |
| P-count | `check` over the real build | whole count N; legs sum to N; no class in two legs; each leg non-empty. N and the three counts are recorded, and compared with the last main run's Api total in the PR description | local, then the gate on the PR's run |
| P-run | the gate's `account` over the PR's real TRX files | per-class executed equals listed; none failed | CI |

FCI1 to FCI3 and FCI8 are scripted rows (a patch applied to a copy, a run, a restore by plain copy so the mtime moves, a rebuild `--no-incremental`, a run unpatched once; memory: falsify restores must bump mtime). FCI5 to FCI7 are branches nobody merges; each is deleted after its result is read, and each costs one run of the **short** workflow the probe commit leaves (the probe disables the fuzz leg, so it is minutes, not an hour and a half).

**Discharged is not falsified:** until FCI1 to FCI3 are seen red on the real runner's listing, `check` is implemented and quiet.

## Expected CI minutes (estimates; the PR's run replaces them)

Basis: the 112 to 134 minute Api step; the CI to local ratio of 1.5 to 1.7 the controller measured on the fuzz and burst classes; local class times from `api-class-times.tsv`.

| | Now | After CI-1 |
|---|---|---|
| Core job (build 4.5, Architecture 3, all but Api 3.5, screening, setup) | about 15 | about 15, on its own runner, in parallel |
| `fuzz` leg: setup, Rust cache hit, closure restore and build 6 to 7; 3,070 local s of tests = 77 to 87 | | **84 to 94** |
| `bursts` leg: 6 to 7 + 1,085 local s = 27 to 31 | | 33 to 38 |
| `rest` leg: 6 to 7 + about 600 local s = 15 to 17 | | 21 to 24 |
| `gate`: checkout, download, `account` | | 1 to 2 |
| **PR wall time** (critical path) | **about 127 to 150** (15 + 112 to 134) | **about 86 to 96** |
| Runner minutes billed | about 127 to 150 | about 160 to 190 (public repository: not a cost) |

Saves about **40 to 55 CI minutes of wall time**. The reconciled note's "55 to 80" is the Api step's own saving taken before the core job's 15 minutes overlap it; the figure to hold the PR to is the wall time on the PR's run, and the decision does not turn on the difference. The fuzz leg is the critical path until CI-2 and will be at 90 minutes, which is why CI-2 is next and why this PR is not the end.

## Identifiers (re-grepped 2026-11-16 against `main` `cce6f034` and the working tree of PR #159's branch)

- Errata: highest `## G72`; G64 and G66 reserved; first unnamed G73. **None allocated.** R14.14 is the first free R14 number (R14.13 is the highest); **not allocated here** (CI-2's outline proposes it).
- Register: D125 free. Item 318 and trap 286 are free **if #159 merges first**; #159's tree already holds traps 284 and 285 and says "first free D125, item 318, trap 286". If this PR reaches Task 2 before #159 merges, re-read `origin/main`, take the numbers after whatever it holds, and say in the PR what they were computed against. Never take a number from this paragraph.
- Item (proposed, **unfiled**): *The Api step is 112 to 134 CI minutes because one runner runs 204 classes in series* (found by the CI-time review, 2026-11-15; closed by this PR for the leg split, continued by CI-2 and CI-3). Trap (proposed, **unfiled, only if FCI6 shows it**): *a required check that is skipped counts as passing, so an aggregate job that `needs` others must run `always()` and test their results*; and, if Task 1's step 0 confirms it, *`dotnet test --filter` over a filter that matches nothing exits 0*.

## What this plan did not check

No test was run to write it: the Api listing time, the closure-only build, the TRX name format, the filter-matches-nothing exit code, and the `--logger trx;LogFileName` path behaviour are Task 1's step 0. The estimates for minutes are the doctrine agents' ratios applied, not a run. The ruleset was read (required contexts above) and not changed. `docs/phase-2-record.md` was not opened.
