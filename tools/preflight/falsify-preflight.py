#!/usr/bin/env python3
"""Falsify tools/preflight/preflight.py's reading of a workflow: fanned-out jobs, plain jobs, and the shape guard.

CI fans a job out with `strategy:` (a matrix) or `needs:` (an aggregate job). Its steps hold expressions
such as `${{ matrix.leg }}` that no developer's machine can fill, and running a matrix job's steps once
here would not mirror three runs there. So preflight lists such a job as left out and goes on; it must
neither run it nor die on it. The first row is the control: against a preflight that reads every job
alike it dies with `an expression it cannot fill`, which is the failure this change exists to prevent.

Synthetic workflows go in a temporary directory; nothing in the tree is touched.

Usage:  python3 tools/preflight/falsify-preflight.py
Exit:   0 every row went as specified, 1 one or more did not.
"""
import importlib.util
import sys
import tempfile
from pathlib import Path

sys.dont_write_bytecode = True  # compiled Python embeds the absolute path it was built from

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("preflight", HERE / "preflight.py")
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)

FANNED = """\
name: CI
on: push
jobs:
  plain:
    name: Plain job
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Say hello
        run: echo hello
  api:
    name: Api (${{ matrix.leg }})
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        include:
          - {leg: fuzz}
    steps:
      - name: Test (Api, ${{ matrix.leg }})
        run: echo "${{ matrix.leg }}"
  gate:
    name: Gate
    needs: [plain, api]
    if: ${{ always() }}
    runs-on: ubuntu-latest
    steps:
      - name: Judge
        env:
          CORE: ${{ needs.plain.result }}
        run: test "$CORE" = success
"""

NO_RUN = """\
name: CI
on: push
jobs:
  only:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
"""


def read(text):
    """parse_workflow's answer as (steps, left_out), whichever shape the script under test returns."""
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "wf.yml"
        path.write_text(text, encoding="utf-8")
        parsed = preflight.parse_workflow(path)
    return parsed if isinstance(parsed, tuple) else (parsed, [])


def fill_every_step(steps):
    """What main() does before it runs a step: fill the expressions it knows, die on the rest."""
    return [preflight.substitute(step.run, "/tmp/runner-temp") for step in steps]


rows = []


def row(name):
    def register(fn):
        rows.append((name, fn))
        return fn
    return register


@row("(i) a strategy job and a needs job do not stop the parse and every step it keeps can be filled")
def _():
    try:
        steps, _ = read(FANNED)
        fill_every_step(steps)
    except SystemExit as stop:
        return False, str(stop)
    return True, ""


@row("(i) a strategy job and a needs job are listed as left out, with the reason")
def _():
    try:
        _, left = read(FANNED)
    except SystemExit as stop:
        return False, str(stop)
    named = {job: reason for job, reason in left}
    ok = set(named) == {"api", "gate"} and "strategy" in named.get("api", "") and "needs" in named.get("gate", "")
    return ok, left


@row("(i) a fanned-out job's steps are not among the steps to run")
def _():
    try:
        steps, _ = read(FANNED)
    except SystemExit as stop:
        return False, str(stop)
    return {s.job for s in steps} == {"plain"}, sorted({s.job for s in steps})


@row("(ii) a plain job's run steps are listed")
def _():
    try:
        steps, _ = read(FANNED)
    except SystemExit as stop:
        return False, str(stop)
    return [(s.job, s.name, s.run) for s in steps] == [("plain", "Say hello", "echo hello")], [(s.job, s.name) for s in steps]


@row("(iii) a workflow with no run: step still stops with the shape message")
def _():
    try:
        read(NO_RUN)
    except SystemExit as stop:
        return "the workflow's shape changed" in str(stop), str(stop)
    return False, "no SystemExit"


@row("(iv) the real workflow still parses and its core steps are run steps")
def _():
    try:
        steps, _ = read(preflight.ROOT.joinpath(".github/workflows/ci.yml").read_text(encoding="utf-8"))
    except SystemExit as stop:
        return False, str(stop)
    return any(s.job == "dotnet" for s in steps) and any(s.job == "spec" for s in steps), sorted({s.job for s in steps})


def main():
    bad = 0
    for name, fn in rows:
        ok, shown = fn()
        print(("ok     " if ok else "FAILED ") + name)
        if not ok:
            print(f"       got: {shown}")
            bad += 1
    print(f"falsify-preflight: {len(rows) - bad} of {len(rows)} rows as specified")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
