#!/usr/bin/env python3
"""Run CI's own steps on this machine before a push, in minutes, leaving out only the slow Api suite.

The commands are read from .github/workflows/ci.yml and never copied here, so a step added to CI
runs here with no edit. That is the point: twice a branch passed every command a developer was told
to run and went red in CI (D16, and the cost step's line-count pins on #147), and each red costs a
run of over an hour. What this cannot catch is named in tools/preflight/README.md.

Usage: python3 tools/preflight/preflight.py [--workflow PATH] [--api FILTER | --no-api] [--base REF]
Exit 0 when every step ran and passed, 1 otherwise.
"""
import argparse
import getpass
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# The one step left out by default, by job and name. Anything else in the workflow runs.
SLOW_STEPS = {("dotnet", "Test (Api)")}

# Added lines may name these placeholders from the privacy rule; anything else under a home is a leak.
HOME_PATH = re.compile(r"/Users/(?!you\b|<|\$)[A-Za-z0-9_.-]+|/home/(?!you\b|u\b|runner\b|<|\$)[A-Za-z0-9_.-]+|/private/tmp/")


@dataclass
class Step:
    job: str
    name: str
    run: str
    env: dict = field(default_factory=dict)
    cwd: str = ""


def indent(line):
    return len(line) - len(line.lstrip(" "))


def block(lines, start, parent_indent):
    """The lines after `start` indented deeper than parent_indent, blank lines kept."""
    out = []
    i = start
    while i < len(lines) and (not lines[i].strip() or indent(lines[i]) > parent_indent):
        out.append(lines[i])
        i += 1
    return out, i


def parse_run(lines, i, key_indent, value):
    """The command of a `run:` key: one line, a literal block (|) or a folded block (>-)."""
    if value and value[0] not in "|>":
        return value, i + 1
    body, end = block(lines, i + 1, key_indent)
    text = [line[indent(line):] if line.strip() else "" for line in body]
    if value.startswith(">"):
        return " ".join(t for t in text if t), end
    return "\n".join(text).rstrip(), end


def parse_workflow(path):
    """The steps with a `run:`, in file order. Not a YAML parser: it reads the shape this file has, and fails if it finds none."""
    lines = path.read_text(encoding="utf-8").splitlines()
    steps, job, job_cwd = [], None, ""
    in_jobs = False
    i = 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("jobs:"):
            in_jobs = True
        elif in_jobs and re.fullmatch(r"  [a-z_-]+:", line):
            job, job_cwd = line.strip().rstrip(":"), ""
        elif job and re.fullmatch(r"\s+working-directory: .*", line) and indent(line) == 8:
            job_cwd = line.split(":", 1)[1].strip()
        elif job and re.match(r"      - ", line):
            body, end = block(lines, i + 1, 6)
            step_lines = ["        " + line[8:]] + body
            name, run, env, cwd, j = "", None, {}, job_cwd, 0
            while j < len(step_lines):
                s = step_lines[j]
                if indent(s) == 8 and ":" in s:
                    key, value = s.strip().split(":", 1)
                    value = value.strip()
                    if key == "name":
                        name = value
                    elif key == "working-directory":
                        cwd = value
                    elif key == "run":
                        run, j = parse_run(step_lines, j, 8, value)
                        continue
                    elif key == "env":
                        env_lines, after = block(step_lines, j + 1, 8)
                        for e in env_lines:
                            if e.strip() and not e.strip().startswith("#"):
                                k, v = e.strip().split(":", 1)
                                env[k] = v.strip()
                        j = after
                        continue
                j += 1
            if run is not None:
                steps.append(Step(job, name or run.split("\n")[0], run, env, cwd))
            i = end
            continue
        i += 1
    if not steps:
        raise SystemExit(f"preflight: read no step with a run: from {path}; the workflow's shape changed, so this script is wrong")
    return steps


def substitute(text, temp):
    text = text.replace("${{ runner.temp }}", temp).replace("${{ github.workspace }}", str(ROOT))
    if "${{" in text:
        raise SystemExit(f"preflight: an expression it cannot fill: {text}")
    return text


def step_env(step, temp):
    env = dict(os.environ, RUNNER_TEMP=temp)
    for key, value in step.env.items():
        env[key] = substitute(value, temp)
    # CI's service has its own password; a developer's server is named by the variable or is the local one.
    if "CURIA_TEST_POSTGRES" in step.env or step.job == "dotnet":
        env["CURIA_TEST_POSTGRES"] = os.environ.get("CURIA_TEST_POSTGRES") or f"Host=localhost;Port=5432;Username={getpass.getuser()};Database=postgres"
    return env


def run_step(label, command, env, cwd, logs):
    """One step under `bash -e`, as CI runs it, and it names the command that stopped it: `test` prints nothing when it fails."""
    log = logs / (re.sub(r"[^A-Za-z0-9]+", "-", label) + ".log")
    script = "set -E\ntrap 'echo \"preflight: this command failed: $BASH_COMMAND\"' ERR\n" + command
    started = time.monotonic()
    print(f"-- {label} ...", flush=True)
    with log.open("w", encoding="utf-8") as out:
        code = subprocess.run(["bash", "-e", "-c", script], cwd=cwd, env=env, stdout=out, stderr=subprocess.STDOUT, check=False).returncode
    elapsed = time.monotonic() - started
    if code != 0:
        print(f"   FAILED (exit {code}), last lines of {log}:")
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines()[-25:]:
            print("   | " + line[:220])
    return code == 0, elapsed


def added_lines(base):
    diff = subprocess.run(["git", "diff", "-U0", f"{base}...HEAD"], cwd=ROOT, capture_output=True, text=True, check=False)
    worktree = subprocess.run(["git", "diff", "-U0"], cwd=ROOT, capture_output=True, text=True, check=False)
    path = ""
    for line in (diff.stdout + "\n" + worktree.stdout).splitlines():
        if line.startswith("+++ "):
            path = line[6:] if line.startswith("+++ b/") else ""
        elif line.startswith("+") and path:
            yield path, line[1:]


def lint(base):
    """The privacy rule's paths and the invisible characters an escape in a tool call can become, over added lines."""
    problems = []
    # Compiled Python embeds the absolute path it was compiled from, and a binary has no added lines to read.
    tracked = subprocess.run(["git", "diff", "--name-only", f"{base}...HEAD"], cwd=ROOT, capture_output=True, text=True, check=False).stdout
    problems += [f"{name}: compiled Python holds a machine path" for name in tracked.splitlines() if "__pycache__" in name or name.endswith(".pyc")]
    for path, line in added_lines(base):
        if path.startswith("tools/preflight/"):
            continue
        if HOME_PATH.search(line):
            problems.append(f"{path}: a machine path: {line.strip()[:120]}")
        hidden = {c for c in line if unicodedata.category(c) in ("Cf", "Co") and c != "﻿"}
        if hidden:
            problems.append(f"{path}: invisible {sorted(hex(ord(c)) for c in hidden)}: {line.strip()[:80]!r}")
    return problems


def changed_api_classes(base):
    names = subprocess.run(["git", "diff", "--name-only", f"{base}...HEAD"], cwd=ROOT, capture_output=True, text=True, check=False).stdout
    return sorted({Path(n).stem for n in names.splitlines() if n.startswith("tests/Curia.Api.Tests/") and n.endswith("Tests.cs")})


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--workflow", type=Path, default=ROOT / ".github/workflows/ci.yml")
    parser.add_argument("--api", help="a dotnet test filter for the Api suite; default: the classes this branch changed")
    parser.add_argument("--no-api", action="store_true", help="run no Api test at all")
    parser.add_argument("--base", default="origin/main")
    args = parser.parse_args()

    temp = tempfile.mkdtemp(prefix="curia-preflight-")
    logs = Path(temp) / "logs"
    logs.mkdir()
    results, left_out = [], []
    failed_jobs = set()

    for step in parse_workflow(args.workflow):
        label = f"{step.job}/{step.name}"
        if (step.job, step.name) in SLOW_STEPS:
            left_out.append(label)
            continue
        if step.job in failed_jobs:
            results.append((label, None, 0.0))
            continue
        cwd = ROOT / step.cwd if step.cwd else ROOT
        ok, elapsed = run_step(label, substitute(step.run, temp), step_env(step, temp), cwd, logs)
        results.append((label, ok, elapsed))
        if not ok:
            failed_jobs.add(step.job)

    if not args.no_api and "dotnet" not in failed_jobs:
        classes = changed_api_classes(args.base)
        api = args.api or "|".join(f"FullyQualifiedName~{c}" for c in classes)
        if api:
            env = dict(os.environ, CURIA_TEST_POSTGRES=step_env(Step("dotnet", "", ""), temp)["CURIA_TEST_POSTGRES"])
            env["CURIA_TESTIS_BIN"] = str(ROOT / "rust/curia-testis/target/debug/curia-testis")
            ok, elapsed = run_step(f"api/{api[:70]}", f'dotnet test tests/Curia.Api.Tests --no-build --configuration Release --filter "{api}"', env, ROOT, logs)
            results.append(("api/the classes this branch changed" if not args.api else "api/--api filter", ok, elapsed))

    problems = lint(args.base)
    for problem in problems:
        print("   LINT " + problem)
    results.append(("lint/added lines", not problems, 0.0))

    # A green run must not have written to the tree: a test that rewrites a tracked file fails nothing else.
    dirty = subprocess.run(["git", "status", "--porcelain", "conformance/"], cwd=ROOT, capture_output=True, text=True, check=False).stdout.strip()
    if dirty:
        print("   the run changed tracked files under conformance/:\n" + dirty)
    results.append(("tree/conformance unchanged", not dirty, 0.0))

    print("\npreflight summary")
    for label, ok, elapsed in results:
        state = "not run" if ok is None else "ok" if ok else "FAILED"
        print(f"  {state:8s}{elapsed:7.0f}s  {label}")
    for label in left_out:
        print(f"  left out         {label} (CI runs it; see tools/preflight/README.md)")

    passed = all(ok for _, ok, _ in results)
    if passed:
        shutil.rmtree(temp, ignore_errors=True)
    else:
        print(f"logs: {logs}")
    print("preflight: " + ("passed" if passed else "FAILED"))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
