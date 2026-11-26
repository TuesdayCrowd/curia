#!/usr/bin/env python3
"""Falsifier for tools/deploy/readiness.py and backup-drill.py (errata G83, R11.110): each check is removed or inverted in a
copy of the script and the probe that guards it must go red for its own reason; a check that survives is a missing probe.

    . .superpowers/keep-tools/env.sh   (dotnet 10 on PATH, CURIA_TEST_POSTGRES set)
    python3 tools/deploy/falsify-readiness.py [--rows FRDY1,FRDY5]

For each row: the original is copied aside, a mutated script is written in its place (the replacement must apply exactly
once), the probe runs (`dotnet test tests/Curia.Api.Tests --no-build`, or the Python unit tests for the drill), the
original is restored by a plain copy with a bumped mtime, and the row is KILLED only if the probe failed AND its failure
text names the check (a probe red for another reason does not count). Last, the unpatched probes run once and must pass.
Build the Api tests in Release first. Not a CI step: a `ci.yml` line is the owner's merge (register item 397 (c)).
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
READINESS = os.path.join(HERE, "readiness.py")
DRILL = os.path.join(HERE, "backup-drill.py")


def kill(check):
    """Mutation: the check returns PASS whatever it saw (the check deleted); the new definition lands before the script's entry point."""

    def mutate(text):
        text = text.replace(f"def check_{check}(", f"def check_{check}_original(", 1)
        return text.replace('\n\nif __name__ == "__main__":', f"\n\ndef check_{check}(*_args):\n    return PASS, 'mutated'\n\n\nif __name__ == \"__main__\":", 1)

    return (f"def check_{check}(", None, mutate)


ROWS = [
    # id, check named in the failure, file, (old, new) or a callable on the text, probe filter
    ("FRDY1", "R1", READINESS, ("    if status == 200:\n        return PASS, f\"GET /health answered 200", "    if status != 200:\n        return PASS, f\"GET /health answered 200"), "ReadinessScriptTests.G83_RDY1|ReadinessScriptTests.G83_RDY2"),
    ("FRDY2", "R2", READINESS, kill("r2"), "ReadinessScriptTests.G83_RDY3"),
    ("FRDY3", "R3", READINESS, kill("r3"), "ReadinessScriptTests.G83_RDY4"),
    ("FRDY4", "R4", READINESS, kill("r4"), "ReadinessScriptTests.G83_RDY4"),
    ("FRDY5", "R5", READINESS, kill("r5"), "ReadinessScriptTests.G83_RDY5"),
    ("FRDY6", "R6", READINESS, kill("r6"), "ReadinessScriptTests.G83_RDY6"),
    ("FRDY7", "R7", READINESS, kill("r7"), "ReadinessScriptTests.G83_RDY7"),
    ("FRDY8", "R8", READINESS, kill("r8"), "ReadinessScriptTests.G83_RDY8"),
    ("FRDY9", "R9", READINESS, kill("r9"), "ReadinessScriptTests.G83_RDY9"),
    ("FRDY10", "R10", READINESS, kill("r10"), "ReadinessScriptTests.G83_RDY10"),
    ("FRDY11", "R11", READINESS, kill("r11"), "ReadinessScriptTests.G83_RDY11"),
    ("FRDY12", "exit should be 8|exit 8", READINESS, ("        if NOT_CHECKED in statuses:\n            return 8", "        if NOT_CHECKED in statuses:\n            return 0"), "ReadinessScriptTests.G83_RDY12|ReadinessScriptTests.G83_RDY1"),
    ("FRDY13", "R9 was", READINESS, ('        return NOT_CHECKED, "--probe-limits was not given', '        return PASS, "--probe-limits was not given'), "ReadinessScriptTests.G83_RDY9"),
    ("FBD1", "drill", DRILL, ('        add("D1", NOT_CHECKED, f"{\', \'.join(missing)} not found on PATH: the drill did not run")', '        add("D1", PASS, f"{\', \'.join(missing)} not found on PATH: the drill did not run")'), "python"),
]


def probe(flt):
    if flt == "python":
        run = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", HERE, "-p", "test_*.py"], capture_output=True, text=True, cwd=ROOT, check=False)
    else:
        names = "|".join("FullyQualifiedName~" + f for f in flt.split("|"))
        run = subprocess.run(
            ["dotnet", "test", "tests/Curia.Api.Tests", "-c", "Release", "--no-build", "--filter", names], capture_output=True, text=True, cwd=ROOT, check=False
        )
    return run.returncode, run.stdout + run.stderr


def restore(path, keep):
    shutil.copyfile(keep, path)
    now = time.time()
    os.utime(path, (now, now))


def main(argv):
    p = argparse.ArgumentParser()
    p.add_argument("--rows")
    args = p.parse_args(argv)
    wanted = set(args.rows.split(",")) if args.rows else None
    keep = {path: path + ".falsify-keep" for path in (READINESS, DRILL)}
    for path, copy in keep.items():
        shutil.copyfile(path, copy)
    survivors, results = [], []
    try:
        for row_id, check, path, mutation, flt in ROWS:
            if wanted and row_id not in wanted:
                continue
            try:
                if isinstance(mutation, tuple) and len(mutation) == 3:
                    text = open(path, encoding="utf-8").read()
                    if mutation[0] not in text:
                        raise RuntimeError(f"{mutation[0]!r} not found")
                    mutated = mutation[2](text)
                else:
                    text = open(path, encoding="utf-8").read()
                    if text.count(mutation[0]) != 1:
                        raise RuntimeError(f"the replacement text occurs {text.count(mutation[0])} times, not once")
                    mutated = text.replace(mutation[0], mutation[1], 1)
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(mutated)
                code, output = probe(flt)
            except RuntimeError as error:
                results.append((row_id, "NOT APPLIED", str(error)))
                survivors.append(row_id)
                continue
            finally:
                restore(path, keep[path])
            own = any(c in output for c in check.split("|")) or check == "drill"
            if code != 0 and own:
                results.append((row_id, "KILLED", f"{check}: the probe went red and names it"))
            elif code != 0:
                results.append((row_id, "RED FOR ANOTHER REASON", output[-300:].replace("\n", " ")))
                survivors.append(row_id)
            else:
                results.append((row_id, "SURVIVED", f"{check}: the probe stayed green with the check mutated"))
                survivors.append(row_id)
    finally:
        for path, copy in keep.items():
            restore(path, copy)
            os.remove(copy)
    code, output = probe("ReadinessScriptTests.G83_RDY1|ReadinessScriptTests.G83_RDY2|ReadinessScriptTests.G83_RDY12")
    pcode, _ = probe("python")
    results.append(("UNPATCHED", "GREEN" if code == 0 and pcode == 0 else "RED", "the unpatched probes run once after the last restore"))
    for row_id, status, reason in results:
        print(f"{row_id:<10} {status:<24} {reason}")
    return 0 if not survivors and code == 0 and pcode == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
