#!/usr/bin/env python3
"""D10b's derivation: conformance/retrieval/THRESHOLD-RULE.md applied mechanically to the committed measurements.

Reads only the rule file (its parameter table is parsed at run time; no parameter is copied here), the committed
measurements file (conformance/retrieval/measured/<model>.measurements.jsonl: ids, classes, cosines, overlaps, no text),
and, for --emit-section, the sha256 of the rule and pair files (bytes hashed, never decoded) and the reference file's
numbers. It reads no pair text, no plan, no RESULTS.md and no threshold, and it shares no code with the C# runner
(tests/Curia.Embedding.Tests/RetrievalRule.cs): it is the rule's second implementation.

Arithmetic is exact: a measured float is taken as the decimal its shortest repr prints, as a Fraction; the grids and the
comparisons are Fractions and integers (basis points), never a float compared with a float-derived grid.

    python3 -I tools/retrieval/derive-thresholds.py [--measurements PATH] [--subset all|legacy|blind]
                                                     [--emit-section [--reference PATH]] [--emit-check-subset-sizes]

Every number prints as a line `<name> = <value>   [<rule clause>; from <ids>]`. Ids and numbers only, never a text.
"""
import argparse
import hashlib
import json
import math
import os
import re
import sys
from decimal import Decimal
from fractions import Fraction

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
RETRIEVAL = os.path.join(ROOT, "conformance", "retrieval")
MODEL = "bge-small-en-v1.5@1"
RULE_COMMIT = "7f1b3081"
DEFAULT_MEASUREMENTS = os.path.join(RETRIEVAL, "measured", MODEL + ".measurements.jsonl")

REPEAT = {"literal", "near-copy"}
NON_DUPLICATE = {"related", "near-miss", "distinct"}
RELEVANT = {"paraphrase", "near-copy"}
NOISE = {"off-topic", "made-up"}


# ---------------------------------------------------------------------------------------------- the rule's table


def exact(x):
    """A measured float as the decimal its shortest repr prints."""
    return Fraction(Decimal(repr(x)))


def parse_rule(markdown):
    """The parameter table: `| name | value |` rows, values as exact fractions."""
    table = {}
    for line in markdown.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if line.lstrip().startswith("|") and len(cells) == 2 and re.fullmatch(r"[a-z]+(?:_[a-z]+)+", cells[0]) and re.fullmatch(r"\d+(?:\.\d+)?", cells[1]):
            table[cells[0]] = Fraction(Decimal(cells[1]))
    return table


def load_rule(path=os.path.join(RETRIEVAL, "THRESHOLD-RULE.md")):
    with open(path, encoding="utf-8") as f:
        return parse_rule(f.read())


def bp(x):
    """A fraction as integer basis points; the value must be on the 1/10000 grid."""
    v = x * 10000
    if v.denominator != 1:
        raise ValueError("not on the basis-point grid: %s" % x)
    return int(v)


# ---------------------------------------------------------------------------------------------- the split


def is_derivation(item_id):
    """'in Derivation if the first byte of the SHA-256 of its UTF-8 id is even and in Check otherwise.'"""
    return hashlib.sha256(item_id.encode("utf-8")).digest()[0] % 2 == 0


# ---------------------------------------------------------------------------------------------- grid helpers


def grid_floor(x, g):
    return Fraction(math.floor(x / g)) * g


def grid_ceil(x, g):
    return Fraction(math.ceil(x / g)) * g


def share_ok(count, total, least):
    """count / total >= least, in integers."""
    return count * least.denominator >= least.numerator * total


# ---------------------------------------------------------------------------------------------- the derivation


def derive(rows, P):
    """rows: the Derivation pairs as dicts (id, class, passage, overlap). Returns O, R, A (Fractions) with the ids that fixed them."""
    g, go = P["grid_cosine"], P["grid_overlap"]
    out = {}

    # Overlap floor O.
    repeats = [r for r in rows if r["class"] in REPEAT]
    if repeats:
        least = min(repeats, key=lambda r: (r["overlap"], r["id"]))
        raw = grid_floor(least["overlap"], go)
        o = min(max(raw, P["overlap_min"]), P["overlap_max"])
        out["O"] = (o, [least["id"]])
    else:
        out["O"] = (None, [])
    o = out["O"][0]

    # Refusing cosine R.
    nondup = [r for r in rows if r["class"] in NON_DUPLICATE]
    over = [r for r in nondup if o is not None and r["overlap"] >= o]
    if over:
        top = max(over, key=lambda r: (r["passage"], r["id"]))
        r_val = max(P["refuse_cosine_least"], grid_ceil(top["passage"] + P["refuse_margin"], g))
        out["R"] = (r_val, [top["id"]])
    else:
        r_val = P["refuse_cosine_least"]
        out["R"] = (r_val, [])

    # Annotating cosine A.
    n = len(nondup)
    ceiling = math.floor(P["annotate_false_share_derivation"] * n)
    ordered = sorted(nondup, key=lambda r: (-r["passage"], r["id"]))
    out["ceiling"] = (ceiling, n)
    if n == 0:
        out["A"] = (None, [])
    else:
        if ceiling >= n:
            a_val, fixed = Fraction(0), []
        else:
            blocker = ordered[ceiling]  # the greatest non-duplicate that may not sit at or above A
            a_val = grid_floor(blocker["passage"], g) + g  # the least multiple strictly above it
            fixed = [blocker["id"]] + [r["id"] for r in ordered[:ceiling]]
        if a_val > r_val:
            a_val = r_val
            fixed = fixed + ["capped at R"]
        out["A"] = (a_val, fixed)
    return out


def floor_value(relevant_derivation, P):
    """relevant_derivation: (id, score) pairs. F_keep: largest multiple of the grid not above the 5th percentile, nearest rank."""
    n = len(relevant_derivation)
    if n == 0:
        return None, []
    ordered = sorted(relevant_derivation, key=lambda t: (t[1], t[0]))
    rank = math.ceil(P["floor_relevant_percentile"] * n)  # nearest rank
    pick = ordered[rank - 1]
    keep = grid_floor(pick[1], P["grid_cosine"])
    return max(P["floor_least"], keep), [pick[0]]


def refused(row, o, r):
    return row["passage"] >= r and row["overlap"] >= o


def annotated_or_refused(row, o, r, a):
    return row["passage"] >= a or refused(row, o, r)


def decide_check(check_rows, relevant_check, noise_check, o, r, a, f, P):
    """The five conditions on Check, each with its counts."""
    nondup = [x for x in check_rows if x["class"] in NON_DUPLICATE]
    rep = [x for x in check_rows if x["class"] in REPEAT]
    refused_nd = [x["id"] for x in nondup if refused(x, o, r)]
    annotated_nd = [x["id"] for x in nondup if not refused(x, o, r) and x["passage"] >= a]
    allowed = math.ceil(P["annotate_false_share_check"] * len(nondup))
    caught = [x["id"] for x in rep if annotated_or_refused(x, o, r, a)]
    removed = [i for i, s in noise_check if s < f]
    kept = [i for i, s in relevant_check if s >= f]
    return {
        "no_nondup_refused": (not refused_nd, len(refused_nd), len(nondup), refused_nd),
        "annotated_within_ceiling": (len(annotated_nd) <= allowed, len(annotated_nd), allowed, annotated_nd, len(nondup)),
        "repeats_caught": (share_ok(len(caught), len(rep), P["repeat_recall_check_least"]), len(caught), len(rep), caught),
        "floor_removes": (share_ok(len(removed), len(noise_check), P["floor_removal_least"]), len(removed), len(noise_check), removed),
        "floor_keeps": (share_ok(len(kept), len(relevant_check), P["floor_keep_least"]), len(kept), len(relevant_check), kept),
    }


def recall(rows, classes, a):
    sel = [x for x in rows if x["class"] in classes]
    hit = [x for x in sel if x["passage"] >= a]
    return len(hit), len(sel)


def repeat_recall(rows, o, r, a):
    sel = [x for x in rows if x["class"] in REPEAT]
    hit = [x for x in sel if annotated_or_refused(x, o, r, a)]
    return len(hit), len(sel)


# ---------------------------------------------------------------------------------------------- reading measurements


def read_measurements(path):
    pairs, noise = [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            j = json.loads(line)
            if j["kind"] == "pair":
                pairs.append({"id": j["id"], "class": j["class"], "source": j["source"], "passage": exact(j["passage"]),
                              "query_passage": exact(j["query_passage"]), "overlap": exact(j["overlap"])})
            else:
                noise.append({"id": j["id"], "class": j["class"], "score": exact(j["score"])})
    return pairs, noise


def run(pairs, noise, P, subset="all"):
    if subset != "all":
        pairs = [p for p in pairs if p["source"] == subset]
        if subset == "legacy":
            noise = []
    der = [p for p in pairs if is_derivation(p["id"])]
    chk = [p for p in pairs if not is_derivation(p["id"])]
    d = derive(der, P)
    o, r, a = d["O"][0], d["R"][0], d["A"][0]
    rel = lambda rows: [(p["id"], p["query_passage"]) for p in rows if p["class"] in RELEVANT]
    f, f_from = floor_value(rel(der), P)
    nz_chk = [(n["id"], n["score"]) for n in noise if not is_derivation(n["id"])]
    res = {"derive": d, "F": (f, f_from), "pairs": pairs, "noise": noise, "der": der, "chk": chk}
    res["sizes"] = {
        "nondup": (sum(p["class"] in NON_DUPLICATE for p in der), sum(p["class"] in NON_DUPLICATE for p in chk)),
        "repeat": (sum(p["class"] in REPEAT for p in der), sum(p["class"] in REPEAT for p in chk)),
        "paraphrase": (sum(p["class"] == "paraphrase" for p in der), sum(p["class"] == "paraphrase" for p in chk)),
        "noise": (sum(is_derivation(n["id"]) for n in noise), len(nz_chk)),
    }
    if None in (o, r, a, f):
        res["check"] = None
        return res
    res["check"] = decide_check(chk, rel(chk), nz_chk, o, r, a, f, P)
    res["recall"] = {
        "paraphrase_derivation": recall(der, {"paraphrase"}, a), "paraphrase_check": recall(chk, {"paraphrase"}, a),
        "repeat_derivation": repeat_recall(der, o, r, a), "repeat_check": repeat_recall(chk, o, r, a),
    }
    c = res["check"]
    res["thresholds_ship"] = c["no_nondup_refused"][0] and c["annotated_within_ceiling"][0] and c["repeats_caught"][0]
    res["floor_does_something"] = c["floor_removes"][0] and c["floor_keeps"][0]
    return res


# ---------------------------------------------------------------------------------------------- printing


def pct(num, den):
    return "n/a" if den == 0 else str(round(Fraction(num, den) * 10000))


def ids(lst):
    return ", ".join(lst) if lst else "none"


def lines(res, P):
    """The script's numbers, each with its rule clause and the items that fixed it."""
    d = res["derive"]
    out = []
    o, o_from = d["O"]
    r, r_from = d["R"]
    a, a_from = d["A"]
    f, f_from = res["F"]
    out.append("overlap_floor_bp = %s   [Overlap floor O: \"The largest multiple of 0.05 not above the least overlap of any repeat in Derivation, clamped to 0.25 at the lowest and 0.50 at the highest\"; from the least repeat in Derivation: %s]" % (bp(o) if o is not None else "undefined", ids(o_from)))
    out.append("refuse_cosine_bp = %s   [Refusing cosine R: \"The rule never lowers R below the published 0.94\"; M from the greatest non-duplicate in Derivation with overlap at least O: %s]" % (bp(r) if r is not None else "undefined", ids(r_from)))
    out.append("annotate_cosine_bp = %s   [Annotating cosine A: \"The least multiple of 0.005 at which no more than floor(0.05 x n) of the n non-duplicates in Derivation have cosine at or above it; if that value exceeds R, A is R\"; ceiling floor(0.05 x %d) = %d; from the blocking non-duplicate then those at or above A: %s]" % (bp(a) if a is not None else "undefined", d["ceiling"][1], d["ceiling"][0], ids(a_from)))
    out.append("floor_bp = %s   [Vector floor F: \"F is the larger of 0.2 and F_keep\", F_keep the 5th percentile by nearest rank of the relevant scores in Derivation; from the item at the nearest rank: %s]" % (bp(f) if f is not None else "undefined", ids(f_from)))
    s = res["sizes"]
    out.append("split_nondup_derivation_check = %d/%d   [A pair or query is in Derivation if the first byte of the SHA-256 of its UTF-8 id is even; non-duplicates]" % s["nondup"])
    out.append("split_repeat_derivation_check = %d/%d   [same split; repeats]" % s["repeat"])
    out.append("split_paraphrase_derivation_check = %d/%d   [same split; paraphrases]" % s["paraphrase"])
    out.append("split_noise_derivation_check = %d/%d   [same split; off-topic and made-up queries]" % s["noise"])
    c = res["check"]
    if c is None:
        out.append("check = undefined   [a derivation input is empty in this subset]")
        return out
    ok = lambda b: "pass" if b else "FAIL"
    v = c["no_nondup_refused"]
    out.append("check_no_nondup_refused = %s   [Check: \"no non-duplicate may be refused\"; %d of %d non-duplicates in Check refused; refused: %s]" % (ok(v[0]), v[1], v[2], ids(v[3])))
    v = c["annotated_within_ceiling"]
    out.append("check_annotated_within_ceiling = %s   [Check: \"at most ceil(0.10 x m) of the m non-duplicates in Check may be annotated\"; %d annotated, allowed %d of m = %d; annotated: %s]" % (ok(v[0]), v[1], v[2], v[4], ids(v[3])))
    v = c["repeats_caught"]
    out.append("check_repeats_caught = %s   [Check: \"at least 0.80 of the repeats in Check must be refused or annotated\"; %d of %d caught; caught: %s]" % (ok(v[0]), v[1], v[2], ids(v[3])))
    v = c["floor_removes"]
    out.append("floor_removal_check = %s   [The floor's check: \"the removal is the share of off-topic and made-up queries whose score is below F\", needs at least 0.50; %d of %d below F = %s bp]" % (ok(v[0]), v[1], v[2], pct(v[1], v[2])))
    v = c["floor_keeps"]
    out.append("floor_keep_check = %s   [The floor's check: \"the keep is the share of relevant scores at or above F\", needs at least 0.90; %d of %d at or above F = %s bp]" % (ok(v[0]), v[1], v[2], pct(v[1], v[2])))
    rc = res["recall"]
    out.append("paraphrase_recall_derivation = %d/%d   [What the rule does about paraphrase: \"the share of paraphrases at or above A is published as the paraphrase recall\"; Derivation]" % rc["paraphrase_derivation"])
    out.append("paraphrase_recall_check = %d/%d   [same; Check; under one half means \"does not detect paraphrase\"]" % rc["paraphrase_check"])
    out.append("repeat_recall_derivation = %d/%d   [repeats refused or annotated, Derivation; reported]" % rc["repeat_derivation"])
    out.append("repeat_recall_check = %d/%d   [repeats refused or annotated, Check]" % rc["repeat_check"])
    out.append("thresholds_ship = %s   [Check: \"If any of the three fails, no number ships\"]" % str(res["thresholds_ship"]).lower())
    out.append("floor_does_something = %s   [The floor's check: \"does something if removal is at least 0.50 and keep is at least 0.90\"]" % str(res["floor_does_something"]).lower())
    return out


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        h.update(f.read())
    return h.hexdigest()


def reference_agreement(pairs, noise, reference_path):
    ref = {}
    with open(reference_path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                j = json.loads(line)
                ref[j["id"]] = j
    worst, n = Fraction(0), 0
    for p in pairs:
        for k in ("passage", "query_passage"):
            worst, n = max(worst, abs(p[k] - exact(ref[p["id"]][k]))), n + 1
    for q in noise:
        worst, n = max(worst, abs(q["score"] - exact(ref[q["id"]]["score"]))), n + 1
    return float(worst), n


def emit_section(res, P, reference_path, pairs, noise):
    c = res["check"]
    d = res["derive"]
    ok = lambda b: "pass" if b else "FAIL"
    fm = lambda t: "%d/%d" % t
    rc = res["recall"]
    s = res["sizes"]
    out = ["<!-- %s:begin -->" % MODEL, "", "### Measured under `%s`" % MODEL, ""]
    out.append("- model: %s" % MODEL)
    out.append("- rule_commit: %s" % RULE_COMMIT)
    out.append("- rule_sha256: %s" % sha256_of(os.path.join(RETRIEVAL, "THRESHOLD-RULE.md")))
    out.append("- blind_pairs_sha256: %s" % sha256_of(os.path.join(RETRIEVAL, "blind-pairs.jsonl")))
    out.append("- dedupe_pairs_sha256: %s" % sha256_of(os.path.join(RETRIEVAL, "dedupe-pairs.jsonl")))
    if reference_path:
        worst, n = reference_agreement(pairs, noise, reference_path)
        out.append("- ndt4_agreement: the runner and tools/model/pair-cosines.py agree within %.1e over %d values; python 3.12.9 here; the manifest records 3.12.16" % (worst, n))
    out.append("- split_nondup_derivation_check: %d/%d" % s["nondup"])
    out.append("- split_repeat_derivation_check: %d/%d" % s["repeat"])
    out.append("- split_paraphrase_derivation_check: %d/%d" % s["paraphrase"])
    out.append("- split_noise_derivation_check: %d/%d" % s["noise"])
    out.append("- thresholds_ship: %s" % str(res["thresholds_ship"]).lower())
    if res["thresholds_ship"]:
        out.append("- overlap_floor_bp: %d" % bp(d["O"][0]))
        out.append("- refuse_cosine_bp: %d" % bp(d["R"][0]))
        out.append("- annotate_cosine_bp: %d" % bp(d["A"][0]))
    out.append("- floor_bp: %d" % bp(res["F"][0]))
    v = c["no_nondup_refused"]
    out.append("- check_no_nondup_refused: %s (%d of %d refused)" % (ok(v[0]), v[1], v[2]))
    v = c["annotated_within_ceiling"]
    out.append("- check_annotated_within_ceiling: %s (%d annotated, at most %d of %d)" % (ok(v[0]), v[1], v[2], v[4]))
    v = c["repeats_caught"]
    out.append("- check_repeats_caught: %s (%d of %d)" % (ok(v[0]), v[1], v[2]))
    out.append("- floor_does_something: %s" % str(res["floor_does_something"]).lower())
    v = c["floor_removes"]
    out.append("- floor_removal_bp: %s (%d of %d noise queries in Check below F)" % (pct(v[1], v[2]), v[1], v[2]))
    v = c["floor_keeps"]
    out.append("- floor_keep_bp: %s (%d of %d relevant scores in Check at or above F)" % (pct(v[1], v[2]), v[1], v[2]))
    out.append("- paraphrase_recall_derivation: %s" % fm(rc["paraphrase_derivation"]))
    out.append("- paraphrase_recall_check: %s" % fm(rc["paraphrase_check"]))
    out.append("- repeat_recall_derivation: %s" % fm(rc["repeat_derivation"]))
    out.append("- repeat_recall_check: %s" % fm(rc["repeat_check"]))
    out.append("")
    if not res["thresholds_ship"]:
        failed = [name for name in ("no_nondup_refused", "annotated_within_ceiling", "repeats_caught") if not c[name][0]]
        out.append("The Check failed (%s): no number ships. The rule's only continuation is a further blind set; nothing was retuned. The derived thresholds are recorded outside this file, labelled not published." % ", ".join(failed))
        out.append("")
    p_hit, p_tot = rc["paraphrase_check"]
    if p_tot and p_hit * 2 < p_tot:
        out.append("The paraphrase recall on Check is under one half: the near-duplicate check is a check for repeats that does not detect paraphrase.")
        out.append("")
    out.append("The Derivation ceiling floor(0.05 x %d) = %d is %d non-duplicate's cosine: the numbers rest on few pairs." % (d["ceiling"][1], d["ceiling"][0], d["ceiling"][0]))
    out.append("")
    out.append("These numbers are this model's (`%s`), on this corpus of about 190 posts, and are derived again at the next model id." % MODEL)
    out.append("")
    out.append("<!-- %s:end -->" % MODEL)
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--measurements", default=DEFAULT_MEASUREMENTS)
    ap.add_argument("--subset", choices=["all", "legacy", "blind"], default="all")
    ap.add_argument("--emit-section", action="store_true")
    ap.add_argument("--reference", default=None, help="the pair-cosines.py output, for the section's agreement line")
    ap.add_argument("--emit-check-subset-sizes", action="store_true")
    args = ap.parse_args(argv)
    P = load_rule()
    pairs, noise = read_measurements(args.measurements)
    res = run(pairs, noise, P, args.subset)
    if args.emit_check_subset_sizes:
        print("check subset sizes (non-duplicates, repeats, paraphrases, noise): %d, %d, %d, %d" % (res["sizes"]["nondup"][1], res["sizes"]["repeat"][1], res["sizes"]["paraphrase"][1], res["sizes"]["noise"][1]))
        return 0
    if args.emit_section:
        if res["check"] is None:
            print("cannot emit: a derivation input is empty", file=sys.stderr)
            return 2
        print("\n".join(emit_section(res, P, args.reference, pairs, noise)))
        return 0
    print("subset = %s" % args.subset)
    print("\n".join(lines(res, P)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
