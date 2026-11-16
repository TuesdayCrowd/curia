#!/usr/bin/env python3
"""Falsify tools/ci/api-legs.py: break each check on a synthetic listing and assert on the message.

A gate that has never failed has not been shown to work. `judge` (is every test in exactly one leg?)
and `account` (did every leg's TRX run what the partition lists, and pass?) are pure functions, so
this needs no toolchain and runs in the `spec` job beside `falsify check-spec`.

Each row names the message the check must print, not only that it exits non-zero. The green controls
(a clean partition, a clean account) are rows too: a check that is red on everything is also broken.

Usage:  python3 tools/ci/falsify-api-legs.py
Exit:   0 every row went as specified, 1 one or more did not.
"""
import importlib.util
import sys
from pathlib import Path

sys.dont_write_bytecode = True  # compiled Python embeds the absolute path it was built from

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("api_legs", HERE / "api-legs.py")
api_legs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(api_legs)
judge, account, parse_listing = api_legs.judge, api_legs.account, api_legs.parse_listing

A, B, C = "Curia.Api.Tests.Fuzz.AFuzz.One", "Curia.Api.Tests.AgentWriteGateEndpointTests.Two", "Curia.Api.Tests.Other.Three"
ROW = 'Curia.Api.Tests.Other.Theory(x: "same")'

rows = []


def row(name):
    def register(fn):
        rows.append((name, fn))
        return fn
    return register


def has(errors, text):
    return any(text in e for e in errors)


@row("judge: a clean partition is green")
def _():
    return judge([A, B, C], {"fuzz": [A], "bursts": [B], "rest": [C]}) == [], "expected no error"


@row("judge: a clean partition holding a theory row twice is green")
def _():
    return judge([A, ROW, ROW], {"fuzz": [A], "rest": [ROW, ROW]}) == [], "expected no error"


@row("judge: a test listed by two legs")
def _():
    errors = judge([A, B], {"fuzz": [A], "bursts": [A, B]})
    return has(errors, "in two legs: fuzz, bursts"), errors


@row("judge: a test in the whole listing and in no leg")
def _():
    errors = judge([A, B, C], {"fuzz": [A], "bursts": [B]})
    return has(errors, "in the whole listing and in no leg") and has(errors, C), errors


@row("judge: a leg that lists nothing")
def _():
    errors = judge([A, B], {"fuzz": [A, B], "bursts": []})
    return has(errors, "leg bursts lists no test"), errors


@row("judge: a test a leg lists and the whole listing lacks")
def _():
    errors = judge([A], {"fuzz": [A, B]})
    return has(errors, "a leg lists and the whole listing lacks") and has(errors, B), errors


@row("judge: a repeated theory row counted by multiplicity (leg twice, whole once)")
def _():
    errors = judge([A, ROW], {"fuzz": [A], "rest": [ROW, ROW]})
    return has(errors, "listed 2 times by its leg but 1 times in the whole listing"), errors


@row("judge: a repeated theory row with a copy in no leg (whole twice, leg once)")
def _():
    errors = judge([A, ROW, ROW], {"fuzz": [A], "rest": [ROW]})
    return has(errors, "listed 2 times in the whole listing but 1 times in its legs"), errors


@row("judge: an empty whole listing fails closed")
def _():
    errors = judge([], {"fuzz": []})
    return has(errors, "the whole listing lists no test"), errors


PARTITION = {"fuzz": {"Curia.Api.Tests.Fuzz.AFuzz": 2}, "rest": {"Curia.Api.Tests.Other": 1}}
TRX = {
    "fuzz": [("Curia.Api.Tests.Fuzz.AFuzz.One", "Passed"), ('Curia.Api.Tests.Fuzz.AFuzz.Two(x: "a")', "Passed")],
    "rest": [("Curia.Api.Tests.Other.Three", "Passed")],
}


def trx(**changes):
    out = {leg: list(results) for leg, results in TRX.items()}
    out.update(changes)
    return out


@row("account: a clean account is green")
def _():
    return account(PARTITION, trx()) == [], "expected no error"


@row("account: a missing TRX")
def _():
    errors = account(PARTITION, {"fuzz": TRX["fuzz"]})
    return has(errors, "no TRX for leg rest"), errors


@row("account: an extra class no leg lists")
def _():
    errors = account(PARTITION, trx(rest=TRX["rest"] + [("Curia.Api.Tests.Stray.Four", "Passed")]))
    return has(errors, "class Curia.Api.Tests.Stray ran in leg rest and is not in the partition"), errors


@row("account: a per-class count that differs")
def _():
    errors = account(PARTITION, trx(fuzz=TRX["fuzz"][:1]))
    return has(errors, "class Curia.Api.Tests.Fuzz.AFuzz in leg fuzz: executed 1, partition lists 2"), errors


@row("account: a class the partition lists that never ran")
def _():
    errors = account(PARTITION, trx(rest=[]))
    return has(errors, "class Curia.Api.Tests.Other in leg rest: executed 0, partition lists 1"), errors


@row("account: a failed test")
def _():
    errors = account(PARTITION, trx(rest=[("Curia.Api.Tests.Other.Three", "Failed")]))
    return has(errors, "did not pass in leg rest") and has(errors, "Failed"), errors


@row("account: a TRX for a leg the partition does not have")
def _():
    errors = account(PARTITION, trx(ghost=[("Curia.Api.Tests.Other.Three", "Passed")]))
    return has(errors, "TRX for leg ghost, which the partition does not have"), errors


@row("parse_listing: a line of an unknown shape is an error, not an empty listing")
def _():
    text = "Test run for x.dll (.NETCoreApp,Version=v10.0)\nThe following Tests are available:\n    Curia.Api.Tests.A.B\n  oddly indented\n"
    tests, errors = parse_listing(text)
    return has(errors, "cannot classify") and has(errors, "oddly indented"), errors


@row("parse_listing: output with no listing header and no no-match line is an error")
def _():
    tests, errors = parse_listing("Build FAILED.\n")
    return has(errors, "neither a test listing nor a no-match line"), errors


@row("parse_listing: a filter that matches nothing is an empty listing, not an error")
def _():
    text = "Test run for x.dll\nThe following Tests are available:\nNo test matches the given testcase filter `FullyQualifiedName~Nope.` in x.dll\n"
    tests, errors = parse_listing(text)
    return tests == [] and errors == [], (tests, errors)


@row("parse_listing: a theory row keeps its arguments and its class is the part before the method")
def _():
    text = "Test run for x.dll\nThe following Tests are available:\n    " + ROW + "\n"
    tests, errors = parse_listing(text)
    return tests == [ROW] and api_legs.class_of(ROW) == "Curia.Api.Tests.Other" and errors == [], (tests, errors)


@row("filter: the complement leg is the conjunction of negations of the named filters")
def _():
    legs = {"legs": {"a": "FullyQualifiedName~X.", "b": "FullyQualifiedName~Y.", "rest": {"complement_of": ["a", "b"]}}}
    return api_legs.filter_for(legs, "rest") == "FullyQualifiedName!~X.&FullyQualifiedName!~Y.", api_legs.filter_for(legs, "rest")


def main():
    bad = 0
    for name, fn in rows:
        result = fn()
        ok, shown = result if isinstance(result, tuple) else (result, "")
        print(("ok     " if ok else "FAILED ") + name)
        if not ok:
            print(f"       the check printed: {shown}")
            bad += 1
    print(f"falsify-api-legs: {len(rows) - bad} of {len(rows)} rows as specified")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
