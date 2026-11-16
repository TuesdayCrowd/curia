# Preflight

Run it before every push:

```bash
python3 tools/preflight/preflight.py
```

It runs CI's own steps on this machine, read from `.github/workflows/ci.yml` and never copied, so a step
added to CI runs here with no edit. A job CI **fans out** is left out: one whose block carries `strategy:`
(a matrix, run once per entry) or `needs:` (joins other jobs). Its steps hold expressions only CI can fill
(`${{ matrix.leg }}`, `${{ needs.x.result }}`), and a single local run would not mirror its several. The
summary lists each such job as left out, and `--list` prints what would run and what would not, running
nothing. Today those are the Api suite's three legs (`api`, one job per leg named in
`tools/ci/api-legs.json`: `fuzz`, `bursts`, `rest`) and the `gate` that joins the core job and the legs.
In their place preflight runs the Api test classes this branch changed. The core job's `Api legs: every
class in exactly one leg` step is an ordinary step, so it runs here. Preflight then checks added lines for
machine paths and invisible characters, and that the run left `conformance/` unchanged. It needs what the
suites need: Postgres with pgvector (`CURIA_TEST_POSTGRES`, or the local server), `python3`, `cargo` and `node`.

Steps that use an action (`uses:`) are neither run nor listed in the summary, so a step of that kind added to the workflow is not exercised by a local run.

`--api "<filter>"` runs a chosen part of the Api suite. `--leg NAME` runs one whole leg of it, with the
filter CI uses for that leg (`python3 tools/ci/api-legs.py filter NAME`, read from the same file), after a
Release build of `tests/Curia.Api.Tests`; `--leg fuzz` is about an hour and a half. `--no-api` runs none of
it. `--list` prints each job's run steps and the jobs left out, and exits. `--base <ref>` compares against
something other than `origin/main`.

`python3 tools/preflight/falsify-preflight.py` shows the reading of the workflow going red on a matrix job
and a `needs` job when they are not left out; CI runs it in the `spec` job.

## What it cannot catch

- **An Api test this branch's change breaks in a class it did not touch.** That needs the whole Api suite:
  `dotnet test tests/Curia.Api.Tests --no-build -c Release`.
- **What differs on Linux or under CI's load.** It runs on this machine, so a race that only appears on four
  cores with eleven test hosts, or a behaviour of Debian's `psql` wrapper, still reaches CI first. Each of
  #146's three CI failures was of this kind.
- **Anything in the steps it leaves out.** The summary lists them, so a passing run says what it did not run.
- **A change to a leg's own steps or to the gate's.** Fanned-out jobs are never run here, so an edit to the
  `api` job (its build, its filter, its logger, its artifact) or to `gate` is read only by `--list` and by
  hand. `--leg NAME` runs the leg's tests the way the job does, but not its restore, build or upload. The
  first CI run of the change is the check.
