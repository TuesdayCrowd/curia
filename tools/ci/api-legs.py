#!/usr/bin/env python3
"""The Api suite's legs: one definition, the check that every test runs in exactly one, and the gate's accounting.

The legs are whole classes, defined once in tools/ci/api-legs.json. `ci.yml` asks for a leg's filter by
name (`filter`), preflight reads the same file, and `check` proves the partition against the test
runner's own discovery (`dotnet test --list-tests`), never against the filter text it is checking.
`rest` is the complement of the named legs, so a class renamed out of a named leg runs in `rest`
instead of in no leg; what `check` adds is the opposite hazard, a named leg that selects nothing, and
a test that two legs both select.

  filter LEG                  print the leg's `dotnet test --filter` expression
  check [--out F] [--legs F]  list the whole assembly and each leg; exit 1 unless every test is in exactly one leg
  account --partition F --trx DIR
                              the gate's step: each leg's TRX ran what the partition lists, and all passed

Stdlib only. Exit 0 ok, 1 a failed check, 2 a usage error.
"""
import argparse
import json
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_LEGS = Path(__file__).resolve().with_name("api-legs.json")
NAMESPACE = "Curia.Api.Tests."
TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# `dotnet test --list-tests` prints two header lines and then one test per line, indented four spaces:
# `Namespace.Class.Method` for a fact, `Namespace.Class.Method(arg: value, ...)` for a theory row.
TEST_LINE = re.compile(r"^    (?P<name>[A-Za-z_][\w.`+]*\.[A-Za-z_]\w*(?:\(.*\))?)$")
LISTING_HEADER = "The following Tests are available:"
SKIPPED_LINES = ("Test run for ", "A total of ")
NO_MATCH = "No test matches the given testcase filter"
SHOWN = 5


def class_of(name):
    """`Ns.Class.Method(args)` -> `Ns.Class`: the part before the first `(`, minus its last dotted piece."""
    return name.split("(", 1)[0].rsplit(".", 1)[0]


def load_legs(path):
    doc = json.loads(Path(path).read_text(encoding="utf-8"))
    for key in ("project", "legs"):
        if key not in doc:
            raise SystemExit(f"api-legs: {path} has no {key!r}")
    return doc


def filter_for(doc, leg):
    spec = doc["legs"].get(leg)
    if spec is None:
        raise SystemExit(f"api-legs: no leg named {leg!r}; the file has {', '.join(doc['legs'])}")
    if isinstance(spec, str):
        return spec
    negations = []
    for other in spec["complement_of"]:
        named = doc["legs"][other]
        if not isinstance(named, str) or not named.startswith("FullyQualifiedName~"):
            raise SystemExit(f"api-legs: leg {other!r} is not a FullyQualifiedName~ filter, so the complement of it is not defined")
        negations.append("FullyQualifiedName!~" + named[len("FullyQualifiedName~"):])
    return "&".join(negations)


def parse_listing(text):
    """(tests, errors) from `dotnet test --list-tests` output. A line it cannot classify is an error, never skipped."""
    tests, errors, listing, matched_nothing = [], [], False, False
    for number, line in enumerate(text.splitlines(), 1):
        if not listing:
            if line.strip() == LISTING_HEADER:
                listing = True
            elif line.startswith(NO_MATCH):
                matched_nothing = True
            elif line.strip() and not line.startswith(SKIPPED_LINES):
                errors.append(f"cannot classify output line {number} before the listing: {line[:160]!r}")
            continue
        if not line.strip():
            continue
        if line.startswith(NO_MATCH):
            matched_nothing = True  # `--list-tests` prints the header and then this line, with no test between
            continue
        found = TEST_LINE.match(line)
        if found:
            tests.append(found.group("name"))
        else:
            errors.append(f"cannot classify listing line {number}: {line[:160]!r}")
    if not listing and not matched_nothing:
        errors.append("the output is neither a test listing nor a no-match line")
    return tests, errors


def _shown(names):
    ordered = sorted(names)
    more = f" (and {len(ordered) - SHOWN} more)" if len(ordered) > SHOWN else ""
    return "; ".join(ordered[:SHOWN]) + more


def judge(whole, legs):
    """Errors for a partition of `whole` (a list of full test names, repeats kept) into `legs` ({leg: list}). [] when exact."""
    errors = []
    if not whole:
        errors.append("the whole listing lists no test")
    for leg, tests in legs.items():
        if not tests:
            errors.append(f"leg {leg} lists no test")
    whole_count = Counter(whole)
    leg_counts = {leg: Counter(tests) for leg, tests in legs.items()}
    in_two, in_none, foreign, too_many, too_few = {}, [], [], [], []
    for name in sorted(set(whole_count) | {n for c in leg_counts.values() for n in c}):
        holders = [leg for leg, c in leg_counts.items() if c[name]]
        listed = sum(c[name] for c in leg_counts.values())
        if name not in whole_count:
            foreign.append(name)
        elif not holders:
            in_none.append(name)
        elif len(holders) > 1:
            in_two[name] = holders
        elif listed > whole_count[name]:
            too_many.append(f"{name}: listed {listed} times by its leg but {whole_count[name]} times in the whole listing")
        elif listed < whole_count[name]:
            too_few.append(f"{name}: listed {whole_count[name]} times in the whole listing but {listed} times in its legs")
    for pair in sorted({", ".join(h) for h in in_two.values()}):
        names = [n for n, h in in_two.items() if ", ".join(h) == pair]
        errors.append(f"{len(names)} test(s) in two legs: {pair}: {_shown(names)}")
    if in_none:
        errors.append(f"{len(in_none)} test(s) in the whole listing and in no leg: {_shown(in_none)}")
    if foreign:
        errors.append(f"{len(foreign)} test(s) a leg lists and the whole listing lacks: {_shown(foreign)}")
    errors += [f"a theory row {m}" for m in too_many[:SHOWN]]
    errors += [f"a theory row {m}" for m in too_few[:SHOWN]]
    return errors


def read_trx(path):
    """[(testName, outcome)] from one TRX file."""
    root = ET.parse(path).getroot()
    return [(r.get("testName"), r.get("outcome")) for r in root.findall(".//t:UnitTestResult", TRX_NS)]


def account(partition, trx):
    """Errors for TRX results ({leg: [(name, outcome)]}) against the partition ({leg: {class: count}}). [] when exact."""
    errors = []
    for leg in trx:
        if leg not in partition:
            errors.append(f"TRX for leg {leg}, which the partition does not have")
    for leg, classes in partition.items():
        if leg not in trx:
            errors.append(f"no TRX for leg {leg}")
            continue
        results = trx[leg]
        executed = Counter(class_of(name) for name, _ in results)
        for cls in sorted(set(classes) | set(executed)):
            if cls not in classes:
                errors.append(f"class {cls} ran in leg {leg} and is not in the partition")
            elif executed[cls] != classes[cls]:
                errors.append(f"class {cls} in leg {leg}: executed {executed[cls]}, partition lists {classes[cls]}")
        bad = [f"{name} ({outcome})" for name, outcome in results if outcome != "Passed"]
        if bad:
            errors.append(f"{len(bad)} test(s) did not pass in leg {leg}: {_shown(bad)}")
    return errors


def list_tests(project, filter_text):
    """(tests, errors) from the runner's own discovery. A nonzero dotnet exit is an error."""
    command = ["dotnet", "test", project, "--no-build", "--configuration", "Release", "--list-tests"]
    if filter_text:
        command += ["--filter", filter_text]
    done = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", check=False)
    if done.returncode != 0:
        return [], [f"dotnet exited {done.returncode} listing {filter_text or 'the whole assembly'}: {(done.stdout + done.stderr)[-400:]!r}"]
    return parse_listing(done.stdout)


def cmd_filter(args):
    print(filter_for(load_legs(args.legs), args.leg))
    return 0


def cmd_check(args):
    started = time.monotonic()
    doc = load_legs(args.legs)
    problems = []
    whole, errors = list_tests(doc["project"], "")
    problems += [f"whole listing: {e}" for e in errors]
    legs = {}
    for leg in doc["legs"]:
        tests, errors = list_tests(doc["project"], filter_for(doc, leg))
        problems += [f"leg {leg}: {e}" for e in errors]
        legs[leg] = tests
    if not problems:
        outside = sorted({n for n in whole if not n.startswith(NAMESPACE)})
        if outside:
            problems.append(f"{len(outside)} test(s) outside {NAMESPACE}*, which the core job's exclusion does not match: {_shown(outside)}")
        problems += judge(whole, legs)
    print(f"api-legs check: whole listing {len(whole)} tests, {len({class_of(n) for n in whole})} classes")
    for leg, tests in legs.items():
        print(f"  leg {leg}: {len(tests)} tests, {len({class_of(n) for n in tests})} classes")
    print(f"  sum of legs {sum(len(t) for t in legs.values())}; {time.monotonic() - started:.1f}s")
    if args.out and not problems:
        partition = {leg: dict(Counter(class_of(n) for n in tests)) for leg, tests in legs.items()}
        Path(args.out).write_text(json.dumps(partition, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    for problem in problems:
        print("api-legs check: FAILED: " + problem)
    if not problems:
        print("api-legs check: every test is in exactly one leg")
    return 1 if problems else 0


def cmd_account(args):
    partition = json.loads(Path(args.partition).read_text(encoding="utf-8"))
    trx = {}
    for path in sorted(Path(args.trx).glob("*.trx")):
        trx[path.stem] = read_trx(path)
    errors = account(partition, trx)
    total = sum(len(v) for v in trx.values())
    print(f"api-legs account: {len(trx)} TRX file(s), {total} result(s), partition lists {sum(sum(c.values()) for c in partition.values())}")
    for error in errors:
        print("api-legs account: FAILED: " + error)
    if not errors:
        print("api-legs account: every leg ran what the partition lists, and all passed")
    return 1 if errors else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("filter")
    p.add_argument("leg")
    p.add_argument("--legs", type=Path, default=DEFAULT_LEGS)
    p.set_defaults(run=cmd_filter)
    p = sub.add_parser("check")
    p.add_argument("--out")
    p.add_argument("--legs", type=Path, default=DEFAULT_LEGS)
    p.set_defaults(run=cmd_check)
    p = sub.add_parser("account")
    p.add_argument("--partition", required=True)
    p.add_argument("--trx", required=True)
    p.set_defaults(run=cmd_account)
    args = parser.parse_args()
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())
