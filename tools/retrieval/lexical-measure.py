#!/usr/bin/env python3
"""The Python reference of the stopword rule (item 342, errata G81): conformance/retrieval/STOPWORD-RULE.md.

Usage:
  python3 tools/retrieval/lexical-measure.py --print-list
  python3 tools/retrieval/lexical-measure.py [--queries FILE] [--out FILE]
  python3 tools/retrieval/lexical-measure.py --measures [--queries FILE]

It re-implements, sharing nothing with the C# but the committed files:
  * LexicalSearch.Tokenize (src/Curia.Domain/Search/LexicalSearch.cs): maximal runs of
    char.IsLetterOrDigit UTF-16 code units, lower-cased invariantly; a surrogate is a separator;
  * the count-and-weight ranking of LexicalSearch.Rank: five for each occurrence of a term in the
    title, three in a tag (whole tag, case-insensitive), one in the body; a post is listed when its
    score is above 0; the order is score descending, ties by sequence ascending; and
  * the list, read from the grouped block of STOPWORD-RULE.md (never from stopwords.txt and never
    from the C#: the C# test compares the three).

Default mode writes one JSON line per measured query (every integer the rule's two artifacts must
agree on: tokens, terms, ignored words, hits and scores, with the list and without it) to --out or
to standard output. The file is a function of the committed material alone and is deterministic, so
the committed reference can be compared with fresh output byte for byte.

--measures computes the rule's numbers and its verdict from those records. It is not run on the
blind set before Task 4 (plan Amendment 1); tools/retrieval/test_lexical_measure.py runs it on
synthetic material only.
"""
import argparse
import hashlib
import json
import math
import os
import re
import sys
import unicodedata
from fractions import Fraction

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
RETRIEVAL = os.path.join(ROOT, "conformance", "retrieval")
RULE = os.path.join(RETRIEVAL, "STOPWORD-RULE.md")
QUERIES = os.path.join(RETRIEVAL, "blind-queries.jsonl")

TITLE_WEIGHT, TAG_WEIGHT, BODY_WEIGHT = 5, 3, 1
CLASS_ORDER = {"target": 0, "hazard": 1, "off-topic": 2, "vague": 3}


# ----- the tokenizer: LexicalSearch.Tokenize -----

def _lower(unit):
    low = unit.lower()
    return low if len(low) == 1 else unit


def tokenize(text):
    if text is None or text.strip() == "":
        return []
    out, cur = [], []
    data = text.encode("utf-16-le", "surrogatepass")
    for i in range(0, len(data), 2):
        unit = chr(int.from_bytes(data[i:i + 2], "little"))
        cat = unicodedata.category(unit)
        if cat[0] == "L" or cat == "Nd":
            cur.append(_lower(unit))
        elif cur:
            out.append("".join(cur))
            cur = []
    if cur:
        out.append("".join(cur))
    return out


# ----- the rule file: the list and the parameter table -----

GROUP = re.compile(r"^- [^()]*\((\d+)\): (.*)$")


def load_list(rule_path=RULE):
    """The grouped block under 'The list', sorted; each group's stated size must match its words."""
    words, in_list = [], False
    with open(rule_path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("**The list ("):
                in_list = True
                continue
            if not in_list:
                continue
            m = GROUP.match(line)
            if m:
                group = [w for span in re.findall(r"`([^`]+)`", m.group(2)) for w in span.split()]
                if len(group) != int(m.group(1)):
                    raise SystemExit(f"a group of the list states {m.group(1)} words and holds {len(group)}")
                words.extend(group)
            elif words and not line.startswith("- "):
                break
    if len(words) != len(set(words)):
        raise SystemExit("the list holds a word twice")
    return sorted(words)


def list_digest(words):
    return hashlib.sha256("\n".join(words).encode("utf-8")).hexdigest()


def load_params(rule_path=RULE):
    """The parameter table, values as exact fractions (no float anywhere)."""
    params, in_table = {}, False
    with open(rule_path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line == "| parameter | value |":
                in_table = True
                continue
            if in_table:
                if not line.startswith("|"):
                    break
                cells = [c.strip() for c in line.strip("|").split("|")]
                if cells[0].startswith("-"):
                    continue
                params[cells[0]] = Fraction(cells[1])
    return params


# ----- the corpus -----

def _jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def build_corpus(retrieval_dir, query_records):
    """The rule's corpus, in the rule's order; each distinct text once. Returns posts (seq, id, title, body, tags)
    and a map from a material post's id to its sequence."""
    posts, by_text, material_seq = [], {}, {}

    def add(post_id, title, body, tags):
        text = (title or "") + "\n" + body
        if text in by_text:
            return by_text[text]
        seq = len(posts)
        posts.append({"seq": seq, "id": post_id, "title": title, "body": body, "tags": list(tags)})
        by_text[text] = seq
        return seq

    for p in _jsonl(os.path.join(retrieval_dir, "corpus.jsonl")):
        add("corpus:" + p["id"], p["title"], p["body"], p["tags"])
    for r in _jsonl(os.path.join(retrieval_dir, "blind-pairs.jsonl")):
        if "a" in r and "b" in r:
            for side in ("a", "b"):
                add(r["id"] + ":" + side, r[side].get("title"), r[side]["body"], [])
    posted = [r for r in query_records if r["class"] in ("target", "hazard")]
    posted.sort(key=lambda r: (CLASS_ORDER[r["class"]], r["id"]))
    for r in posted:
        material_seq[r["id"]] = add(r["id"], r["post"]["title"], r["post"]["body"], [])
    return posts, material_seq


# ----- the ranking: LexicalSearch.Rank -----

def search_terms(text, stop):
    """(terms, ignored, had_token): the terms keep the query's order and repeats; ignored is first occurrence only."""
    tokens = tokenize(text)
    terms = [t for t in tokens if t not in stop]
    ignored = []
    for t in tokens:
        if t in stop and t not in ignored:
            ignored.append(t)
    return terms, ignored, len(tokens) > 0


def score(post, terms, title_tokens, body_tokens):
    if not terms:
        return 0
    tags = [g.upper() for g in post["tags"]]
    total = 0
    for t in terms:
        total += TITLE_WEIGHT * title_tokens.count(t) + BODY_WEIGHT * body_tokens.count(t) + TAG_WEIGHT * tags.count(t.upper())
    return total


def rank(posts, terms, had_token, cache):
    """Flat [seq, score, seq, score, ...], best first. The switch is had_token, never len(terms)."""
    if not had_token or not terms:
        return []
    hits = []
    for p in posts:
        title_tokens, body_tokens = cache[p["seq"]]
        s = score(p, terms, title_tokens, body_tokens)
        if s > 0:
            hits.append((-s, p["seq"], s))
    hits.sort()
    flat = []
    for _, seq, s in hits:
        flat += [seq, s]
    return flat


def measured_queries(query_records):
    """(id, class, form, text, target_post_id) in the rule's order: targets, hazards, off-topic, vague, by id; terse then sentence."""
    out = []
    for r in sorted(query_records, key=lambda r: (CLASS_ORDER[r["class"]], r["id"])):
        if r["class"] in ("target", "hazard"):
            for form in ("terse", "sentence"):
                out.append((r["id"], r["class"], form, r[form], r["id"]))
        else:
            out.append((r["id"], r["class"], "query", r["query"], None))
    return out


def run(retrieval_dir=RETRIEVAL, queries_path=QUERIES, rule_path=RULE):
    stop = frozenset(load_list(rule_path))
    query_records = _jsonl(queries_path)
    posts, material_seq = build_corpus(retrieval_dir, query_records)
    cache = {p["seq"]: (tokenize(p["title"]), tokenize(p["body"])) for p in posts}

    header = {"kind": "corpus", "size": len(posts), "post_ids": [p["id"] for p in posts],
              "list_size": len(stop), "list_sha256": list_digest(sorted(stop))}
    records = [header]
    for qid, cls, form, text, target in measured_queries(query_records):
        tokens = tokenize(text)
        terms, ignored, had_token = search_terms(text, stop)
        all_tokens = tokens
        rec = {"kind": "query", "id": qid, "class": cls, "form": form,
               "tokens": tokens, "terms": terms, "ignored": ignored,
               "had_token": had_token, "all_ignored": had_token and not terms,
               "target_seq": material_seq[target] if target else None,
               "without": rank(posts, all_tokens, had_token, cache),
               "with": rank(posts, terms, had_token, cache)}
        records.append(rec)
    return records


def write(records, out):
    for r in records:
        out.write(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n")


# ----- the rule's numbers -----

def _rank_of(flat, seq):
    for i in range(0, len(flat), 2):
        if flat[i] == seq:
            return i // 2 + 1
    return None


def measures(records, params):
    """Removal, both Keeps and the verdict, from the per-query records alone. Exact arithmetic throughout."""
    header = records[0]
    n = header["size"]
    queries = [r for r in records[1:] if r["had_token"]]       # a listing is excluded from every measure
    listings = [r["id"] for r in records[1:] if not r["had_token"]]

    # Removal
    off = [r for r in queries if r["class"] == "off-topic"]
    thr = params["removal_page_share_most"]
    narrow_with = sum(1 for r in off if Fraction(len(r["with"]) // 2, n) <= thr)
    narrow_without = sum(1 for r in off if Fraction(len(r["without"]) // 2, n) <= thr)
    share_with = Fraction(narrow_with, len(off)) if off else Fraction(0)
    share_without = Fraction(narrow_without, len(off)) if off else Fraction(0)
    removal_ok = bool(off) and share_with >= params["removal_share_least"] and share_with - share_without >= params["removal_improvement_least"]

    # Keep, on-topic
    window = int(params["keep_window"])
    on = [r for r in queries if r["class"] == "target"]
    stopword_only = real = base = kept = 0
    rr_without = rr_with = Fraction(0)
    for r in on:
        r_with, r_without = _rank_of(r["with"], r["target_seq"]), _rank_of(r["without"], r["target_seq"])
        if r_with is None:
            if r_without is not None:
                stopword_only += 1
            continue
        real += 1
        rr_without += Fraction(1, r_without)
        rr_with += Fraction(1, r_with)
        if r_without <= window:
            base += 1
            if r_with <= window:
                kept += 1
    mrr_fall = (rr_without - rr_with) / real if real else Fraction(0)
    base_ok = base >= params["keep_base_least"]
    kept_ok = base > 0 and Fraction(kept, base) >= params["keep_on_topic_least"]
    mrr_ok = mrr_fall <= params["keep_mrr_fall_most"]
    keep_ok = base_ok and kept_ok and mrr_ok

    # Keep, hazard
    haz = [r for r in queries if r["class"] == "hazard"]
    m = lost = lost_all_ignored = lost_own_term = haz_stopword_only = 0
    for r in haz:
        listed_without = _rank_of(r["without"], r["target_seq"]) is not None
        listed_with = _rank_of(r["with"], r["target_seq"]) is not None
        if not listed_without:
            continue
        m += 1
        if not listed_with:
            lost += 1
            haz_stopword_only += 1
            if r["all_ignored"]:
                lost_all_ignored += 1
            elif r["terms"]:
                lost_own_term += 1
    allowed = math.ceil(params["hazard_lost_share_most"] * m)
    hazard_ok = lost <= allowed

    if not base_ok:
        verdict = "undecided"
    elif removal_ok and keep_ok and hazard_ok:
        verdict = "ships"
    else:
        verdict = "no"
    return {
        "listings": listings,
        "removal": {"queries": len(off), "with": narrow_with, "without": narrow_without, "ok": removal_ok},
        "keep_on_topic": {"queries": len(on), "real": real, "stopword_only": stopword_only, "base": base, "kept": kept,
                          "all_ignored": sum(1 for r in on if r["all_ignored"]),
                          "mrr_fall": [mrr_fall.numerator, mrr_fall.denominator],
                          "base_ok": base_ok, "kept_ok": kept_ok, "mrr_ok": mrr_ok},
        "keep_hazard": {"queries": len(haz), "m": m, "lost": lost, "allowed": allowed,
                        "lost_all_ignored": lost_all_ignored, "lost_with_own_term": lost_own_term,
                        "stopword_only": haz_stopword_only, "ok": hazard_ok},
        "verdict": verdict,
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--print-list", action="store_true", help="print the list read from the rule file, one word per line")
    ap.add_argument("--measures", action="store_true", help="print the rule's numbers and verdict as JSON")
    ap.add_argument("--queries", default=QUERIES)
    ap.add_argument("--out")
    args = ap.parse_args(argv)

    if args.print_list:
        sys.stdout.write("\n".join(load_list()))
        return 0
    records = run(queries_path=args.queries)
    if args.measures:
        print(json.dumps(measures(records, load_params()), sort_keys=True))
        return 0
    if args.out:
        with open(args.out, "w", encoding="utf-8", newline="\n") as f:
            write(records, f)
    else:
        write(records, sys.stdout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
