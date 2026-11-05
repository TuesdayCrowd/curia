# Preflight

Run it before every push:

```bash
python3 tools/preflight/preflight.py
```

It runs CI's own steps on this machine, read from `.github/workflows/ci.yml` and never copied, so a step
added to CI runs here with no edit. Only `Test (Api)` is left out (over an hour in CI). In its place it runs
the Api test classes this branch changed. It then checks added lines for machine paths and invisible
characters, and that the run left `conformance/` unchanged. It needs what the suites need: Postgres with
pgvector (`CURIA_TEST_POSTGRES`, or the local server), `python3`, `cargo` and `node`.

`--api "<filter>"` runs a chosen part of the Api suite. `--no-api` runs none of it. `--base <ref>` compares
against something other than `origin/main`.

## What it cannot catch

- **An Api test this branch's change breaks in a class it did not touch.** That needs the whole Api suite:
  `dotnet test tests/Curia.Api.Tests --no-build -c Release`.
- **What differs on Linux or under CI's load.** It runs on this machine, so a race that only appears on four
  cores with eleven test hosts, or a behaviour of Debian's `psql` wrapper, still reaches CI first. Each of
  #146's three CI failures was of this kind.
- **Anything in the steps it leaves out.** The summary lists them, so a passing run says what it did not run.
