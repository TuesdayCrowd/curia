#!/usr/bin/env python3
"""Falsify check-spec.py: break each check and confirm it names the specific cell.

`CLAUDE.md`'s first rule: *a gate that has never failed has not been shown to work.*
`check-spec.py` has four checks, and until now none of them had been watched going
red on purpose. This runs each one against a deliberately broken copy of the
specification and asserts on the message it prints.

**Nothing here touches the working tree.** The three specification documents are
copied to a temporary directory and `check-spec.py --repo <tmp>` is pointed at the
copy, so a falsification cannot escape into the repository and there is no
`git checkout` to get wrong afterwards.

The four directions, one per check in `check-spec.py`:

  citation        a requirement cited in prose that is defined nowhere
  duplicate       a requirement redefined unqualified under a number §11 already uses
  index-orphan    a requirement an entry proposes and the consolidated index omits
  index-phantom   an index row naming a requirement no errata entry defines

Usage:  python3 tools/spec-checks/falsify-spec-checks.py [--repo PATH] [-v]
Exit:   0 every check went red as specified, 1 one or more did not.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

WHITEPAPER = "curia-agent-forum-WHITEPAPER.md"
ERRATA = "curia-whitepaper-ERRATA-AND-ADDENDUM.md"
SCOPING = "curia-csharp-scoping.md"
DOCS = (WHITEPAPER, ERRATA, SCOPING)

INDEX_MARKER = "# Consolidated proposed-requirements index"

# A requirement number the white paper defines and no errata entry proposes, used
# as the phantom index row. Derived rather than hardcoded: see `pick_phantom`.
PHANTOM_FALLBACK = "R6.1"


class Failure(Exception):
    pass


def run_checker(checker: pathlib.Path, repo: pathlib.Path) -> tuple[int, str]:
    proc = subprocess.run(
        [sys.executable, str(checker), "--repo", str(repo)],
        capture_output=True,
        text=True,
    )
    return proc.returncode, proc.stdout + proc.stderr


def definitions(text: str) -> set[str]:
    return {m[0] for m in re.findall(r"^\*\*(R\d+\.\d+)([^*]*)\*\*", text, re.MULTILINE)}


def latest_entry(errata: str) -> tuple[str, str]:
    """The last `## G<n>` entry's heading and body — the entry under test."""
    starts = [(m.start(), m.group(0)) for m in re.finditer(r"^## G\d+ .*$", errata, re.MULTILINE)]
    if not starts:
        raise Failure("no Part G entries found in the errata")
    pos, heading = starts[-1]
    end = errata.find(INDEX_MARKER, pos)
    if end == -1:
        end = len(errata)
    return heading, errata[pos:end]


def pick_phantom(docs: dict[str, str]) -> str:
    """A requirement the white paper defines that no errata entry proposes.

    Using an *undefined* number would trip the citation check too, and a
    falsification that fires two checks does not show which one it proved.
    """
    body = docs[ERRATA].split(INDEX_MARKER, 1)[0]
    proposed = definitions(body)
    for rid in sorted(definitions(docs[WHITEPAPER]), key=lambda r: (int(r[1:].split(".")[0]), int(r.split(".")[1]))):
        if rid not in proposed:
            return rid
    return PHANTOM_FALLBACK


# --- the four falsifications -------------------------------------------------
#
# Each returns (mutated docs, the substring the checker must print).


def falsify_citation(docs: dict[str, str]) -> tuple[dict[str, str], str]:
    heading, entry = latest_entry(docs[ERRATA])
    dangling = "R11.997"
    broken = entry.replace(
        heading,
        heading + f"\n\n*(falsification: this line cites {dangling}, which nothing defines.)*",
        1,
    )
    out = dict(docs)
    out[ERRATA] = docs[ERRATA].replace(entry, broken, 1)
    return out, f"cites {dangling}, which is defined in neither document"


def falsify_duplicate(docs: dict[str, str]) -> tuple[dict[str, str], str]:
    """Redefine a §11.5 requirement, unqualified, inside the newest entry."""
    victim = "R11.16"
    if victim not in definitions(docs[WHITEPAPER]):
        raise Failure(f"{victim} is not defined in the white paper; pick another victim")
    heading, entry = latest_entry(docs[ERRATA])
    broken = entry.replace(
        heading,
        heading + f"\n\n**{victim}** Falsification: an unqualified redefinition of a number the "
        "white paper already uses.",
        1,
    )
    out = dict(docs)
    out[ERRATA] = docs[ERRATA].replace(entry, broken, 1)
    return out, f"{victim} is defined unqualified in both"


def falsify_index_orphan(docs: dict[str, str]) -> tuple[dict[str, str], str]:
    """Delete the newest entry's first index row while the entry still proposes it."""
    _, entry = latest_entry(docs[ERRATA])
    proposed = sorted(
        definitions(entry),
        key=lambda r: (int(r[1:].split(".")[0]), int(r.split(".")[1])),
    )
    if not proposed:
        raise Failure("the newest Part G entry proposes no requirements to orphan")
    victim = proposed[0]

    body, index = docs[ERRATA].split(INDEX_MARKER, 1)
    kept = [
        line
        for line in index.splitlines()
        if not (line.strip().startswith("|") and line.strip("| ").split("|")[0].strip().startswith(victim))
    ]
    if len(kept) == len(index.splitlines()):
        raise Failure(f"no index row found for {victim}; the index may already be wrong")

    out = dict(docs)
    out[ERRATA] = body + INDEX_MARKER + "\n".join(kept)
    return out, f"{victim} is proposed in an entry but absent from the index"


def falsify_index_phantom(docs: dict[str, str]) -> tuple[dict[str, str], str]:
    phantom = pick_phantom(docs)
    body, index = docs[ERRATA].split(INDEX_MARKER, 1)
    lines = index.splitlines()
    for i, line in enumerate(lines):
        if line.strip().startswith("|") and "---" in line:
            lines.insert(i + 1, f"| {phantom} | Falsification: an index row no entry defines | G0 |")
            break
    else:
        raise Failure("could not locate the index table header")

    out = dict(docs)
    out[ERRATA] = body + INDEX_MARKER + "\n".join(lines)
    return out, f"{phantom} is listed in the index but no entry defines it"


FALSIFICATIONS = (
    ("citation", "a requirement cited in prose that is defined nowhere", falsify_citation),
    ("duplicate", "a requirement redefined unqualified under an existing number", falsify_duplicate),
    ("index-orphan", "a proposed requirement the index omits", falsify_index_orphan),
    ("index-phantom", "an index row no entry defines", falsify_index_phantom),
)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=str(pathlib.Path(__file__).resolve().parents[2]))
    ap.add_argument("-v", "--verbose", action="store_true", help="print each checker run in full")
    args = ap.parse_args()

    repo = pathlib.Path(args.repo)
    checker = repo / "tools" / "spec-checks" / "check-spec.py"
    if not checker.exists():
        print(f"falsify: cannot find {checker}", file=sys.stderr)
        return 2

    pristine: dict[str, str] = {}
    for name in DOCS:
        path = repo / name
        if not path.exists():
            print(f"falsify: cannot find {path}", file=sys.stderr)
            return 2
        pristine[name] = path.read_text(encoding="utf-8")

    failures: list[str] = []

    with tempfile.TemporaryDirectory(prefix="curia-falsify-") as tmp:
        sandbox = pathlib.Path(tmp)

        def write(docs: dict[str, str]) -> None:
            for name, text in docs.items():
                (sandbox / name).write_text(text, encoding="utf-8")

        # Baseline. A falsification proves nothing if the checker was already red.
        write(pristine)
        code, out = run_checker(checker, sandbox)
        if code != 0:
            print("BASELINE NOT CLEAN — every result below is uninterpretable:\n")
            print(out)
            return 1
        print("baseline           clean (exit 0)")

        heading, _ = latest_entry(pristine[ERRATA])
        print(f"entry under test   {heading.strip()}\n")

        for name, description, mutate in FALSIFICATIONS:
            try:
                broken, expected = mutate(pristine)
            except Failure as exc:
                failures.append(f"{name}: could not construct the falsification — {exc}")
                print(f"{name:<18} SETUP FAILED — {exc}")
                continue

            # Every run starts from the pristine copy, so falsifications cannot
            # compound and mask one another.
            shutil.rmtree(sandbox, ignore_errors=True)
            sandbox.mkdir(parents=True, exist_ok=True)
            write(broken)

            code, out = run_checker(checker, sandbox)
            if args.verbose:
                print(f"\n--- {name} ---\n{out}")

            if code == 0:
                failures.append(f"{name}: checker stayed GREEN on {description}")
                print(f"{name:<18} STAYED GREEN — {description}")
            elif expected not in out:
                failures.append(
                    f"{name}: checker went red but never printed the specific cell.\n"
                    f"    expected substring: {expected}\n"
                    f"    got:\n{out.strip()}"
                )
                print(f"{name:<18} red, but did not name the cell")
            else:
                print(f"{name:<18} red, named the cell: {expected}")

    # The working tree was never written to; assert it anyway, because "the
    # falsification escaped" is precisely the failure this design exists to rule out.
    for name in DOCS:
        if (repo / name).read_text(encoding="utf-8") != pristine[name]:
            failures.append(f"{name} DIFFERS from its pre-run contents — a falsification escaped")

    print()
    if failures:
        for line in failures:
            print(f"FAIL  {line}")
        print(f"\nfalsify: {len(failures)} check(s) did not behave as specified")
        return 1
    print(f"falsify: all {len(FALSIFICATIONS)} checks went red naming their cell; working tree untouched")
    return 0


if __name__ == "__main__":
    sys.exit(main())
