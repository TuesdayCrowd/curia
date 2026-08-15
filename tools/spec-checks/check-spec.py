#!/usr/bin/env python3
"""Mechanical consistency checks over the specification documents.

Every cross-reference defect this project has found so far was found by a human
happening to notice: R4.21 defined twice across the two documents; Table 6 naming
an `expired` state it never defines; D6 closed by the errata while §16 still
lists it open. That is four for four on luck, and luck has already failed twice
inside a plan whose whole subject was these documents.

These checks are cheap and run in CI. They do not replace reading — they convert
one class of defect from "discovered eventually, by chance" into "cannot be
committed".

Usage:  python3 tools/spec-checks/check-spec.py [--repo PATH]
Exit:   0 clean, 1 findings.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys
from collections import defaultdict

WHITEPAPER = "curia-agent-forum-WHITEPAPER.md"
ERRATA = "curia-whitepaper-ERRATA-AND-ADDENDUM.md"
SCOPING = "curia-csharp-scoping.md"

# A requirement is *defined* where it appears bolded at the start of its text,
# e.g. "**R6.33 (revised)** Envelope numeric values SHALL ...". Merely citing one
# mid-sentence is not a definition, which is the distinction these checks turn on.
DEFINITION = re.compile(r"^\*\*(R\d+\.\d+)([^*]*)\*\*", re.MULTILINE)
CITATION = re.compile(r"\bR(\d+\.\d+)\b")
DECISION_OPEN = re.compile(r"^\*\*(D\d+) — ", re.MULTILINE)
DECISION_CLOSED = re.compile(r"clos\w*\s+(D\d+)|(D\d+)\s+is closed|resolves\s+(D\d+)", re.IGNORECASE)


class Findings:
    def __init__(self) -> None:
        self.items: list[tuple[str, str]] = []

    def add(self, check: str, detail: str) -> None:
        self.items.append((check, detail))

    def report(self) -> int:
        if not self.items:
            print("spec-checks: clean")
            return 0
        by_check: dict[str, list[str]] = defaultdict(list)
        for check, detail in self.items:
            by_check[check].append(detail)
        for check in sorted(by_check):
            print(f"\n{check}")
            for detail in by_check[check]:
                print(f"  {detail}")
        print(f"\nspec-checks: {len(self.items)} finding(s)")
        return 1


def definitions(text: str) -> dict[str, list[str]]:
    """Requirement id -> the qualifiers it was defined with ('', ' (revised)', ...)."""
    found: dict[str, list[str]] = defaultdict(list)
    for rid, qualifier in DEFINITION.findall(text):
        found[rid].append(qualifier.strip())
    return found


def check_no_duplicate_definitions(docs: dict[str, str], f: Findings) -> None:
    """R4.21 was defined in both documents, for two unrelated requirements.

    A qualified redefinition ("(revised)", "(rev. 2)", "(addendum)") is the errata
    amending a v1.0 requirement, which is its job. An *unqualified* redefinition of
    a number the white paper already uses is a collision.
    """
    wp = definitions(docs[WHITEPAPER])
    er = definitions(docs[ERRATA])
    for rid, qualifiers in sorted(er.items()):
        if rid not in wp:
            continue
        unqualified = [q for q in qualifiers if q == ""]
        if unqualified:
            f.add(
                "duplicate requirement definition",
                f"{rid} is defined unqualified in both {WHITEPAPER} and {ERRATA}. "
                f"An errata entry extending a v1.0 requirement must say so "
                f"(e.g. '{rid} (revised)'); an unrelated new requirement needs the "
                f"next free number in its section.",
            )
    for doc, defs in ((WHITEPAPER, wp), (ERRATA, er)):
        for rid, qualifiers in sorted(defs.items()):
            plain = [q for q in qualifiers if q == ""]
            if len(plain) > 1:
                f.add(
                    "duplicate requirement definition",
                    f"{rid} is defined {len(plain)} times unqualified within {doc}.",
                )


# Citations that are deliberately dangling, with the entry that makes them so.
# A8 exists precisely to record that §10's numbering is non-monotonic and that
# R10.7–R10.9 were never defined; it has to name them to say so. Everything else
# that fails to resolve is a real defect, and this list stays short on purpose —
# each entry is a claim that a dangling citation is intentional, which is exactly
# the kind of claim that rots.
DELIBERATELY_DANGLING = {
    "R10.7": "errata A8 — records that §10's R10.7–R10.9 do not exist",
    "R10.8": "errata A8 — records that §10's R10.7–R10.9 do not exist",
    "R10.9": "errata A8 — records that §10's R10.7–R10.9 do not exist",
}


def check_citations_resolve(docs: dict[str, str], f: Findings) -> None:
    """Every R<n>.<m> cited anywhere must be defined somewhere."""
    defined = set(definitions(docs[WHITEPAPER])) | set(definitions(docs[ERRATA]))
    defined |= set(DELIBERATELY_DANGLING)
    for name, text in docs.items():
        cited = {f"R{m}" for m in CITATION.findall(text)}
        for rid in sorted(cited - defined):
            f.add(
                "citation resolves to nothing",
                f"{name} cites {rid}, which is defined in neither document.",
            )


def check_index_matches_bodies(docs: dict[str, str], f: Findings) -> None:
    """The consolidated index must list exactly the requirements the entries propose."""
    errata = docs[ERRATA]
    marker = "# Consolidated proposed-requirements index"
    if marker not in errata:
        f.add("consolidated index", "the index section is missing entirely.")
        return
    body, index = errata.split(marker, 1)
    index = index.split("# Closing note")[0]

    # Only the *first* cell of each row is an ID. The description column routinely
    # cites other requirements in prose — "R8.38 applied to the lane", "R4.19 applied
    # to the log" — and reading the whole row turns every such mention into a phantom
    # orphan. The first version of this check did exactly that and produced three
    # false positives, which is the more dangerous direction: it invites someone to
    # "fix" a specification to satisfy a broken tool.
    indexed: set[str] = set()
    for line in index.splitlines():
        line = line.strip()
        if not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if not cells:
            continue
        cell = cells[0]

        # The ID cell may hold a range — "R8.49–R8.51" covers R8.50, which appears
        # nowhere literally. Expand ranges within one section before comparing, or
        # every interior member reads as an unindexed orphan. (Found by running this
        # check against the real index: R8.50 was reported missing while being fully
        # covered by its range row.)
        for lo_sec, lo_num, hi_sec, hi_num in re.findall(
            r"R(\d+)\.(\d+)\s*[–—-]\s*R?(\d*)\.?(\d+)", cell
        ):
            if hi_sec and hi_sec != lo_sec:
                continue  # a cross-section range is not a thing; leave it to the literal pass
            for n in range(int(lo_num), int(hi_num) + 1):
                indexed.add(f"R{lo_sec}.{n}")

        indexed.update(f"R{m}" for m in CITATION.findall(cell))

    proposed = set(definitions(body))
    for rid in sorted(proposed - indexed):
        f.add(
            "consolidated index",
            f"{rid} is proposed in an entry but absent from the index.",
        )
    for rid in sorted(indexed - proposed):
        f.add(
            "consolidated index",
            f"{rid} is listed in the index but no entry defines it.",
        )


def check_open_decisions(docs: dict[str, str], f: Findings) -> None:
    """A decision listed open in §16 must not be claimed closed elsewhere.

    A13 closes D6; §16 still lists it. The document that closes a decision and
    the document that lists it disagreed, and nothing noticed.
    """
    wp = docs[WHITEPAPER]
    if "## 16. Open design decisions" not in wp:
        return
    section = wp.split("## 16. Open design decisions", 1)[1]
    for stop in ("\n## ", "\n# Appendices"):
        if stop in section:
            section = section.split(stop, 1)[0]

    # A decision stays listed in §16 after it is resolved — §16 asks that each be
    # "closed deliberately and recorded", and deleting the entry would destroy the
    # record of what the fork was. An entry whose body marks itself **CLOSED** is
    # therefore not an open decision, even though its heading looks like one.
    listed_open: set[str] = set()
    entries = re.split(r"(?=^\*\*D\d+ — )", section, flags=re.MULTILINE)
    for entry in entries:
        m = DECISION_OPEN.match(entry)
        if not m:
            continue
        if "**CLOSED**" in entry:
            continue
        listed_open.add(m.group(1))

    claimed_closed: set[str] = set()
    for name, text in docs.items():
        for groups in DECISION_CLOSED.findall(text):
            for did in groups:
                if did:
                    claimed_closed.add(did)

    for did in sorted(listed_open & claimed_closed):
        f.add(
            "decision listed open but claimed closed",
            f"{did} is listed as open in §16 of {WHITEPAPER}, but is claimed "
            f"closed elsewhere. Either the listing is stale or the claim is wrong; "
            f"§16 says each decision 'should be closed deliberately and recorded'.",
        )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=str(pathlib.Path(__file__).resolve().parents[2]))
    args = ap.parse_args()
    repo = pathlib.Path(args.repo)

    docs: dict[str, str] = {}
    for name in (WHITEPAPER, ERRATA, SCOPING):
        path = repo / name
        if not path.exists():
            print(f"spec-checks: cannot find {path}", file=sys.stderr)
            return 2
        docs[name] = path.read_text(encoding="utf-8")

    f = Findings()
    check_no_duplicate_definitions(docs, f)
    check_citations_resolve(docs, f)
    check_index_matches_bodies(docs, f)
    check_open_decisions(docs, f)
    return f.report()


if __name__ == "__main__":
    sys.exit(main())
