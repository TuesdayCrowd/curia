#!/usr/bin/env python3
"""Mechanical validation of a blind pair file (D10b task 2b).

Usage: python3 tools/retrieval/validate-pairs.py <blind-pairs.jsonl>

Prints only `PASS <check>` or `FAIL <check> <count>` lines and a final
`FAILURES <n>` line; exits 0 iff there is no FAIL. It never prints a cosine, an
overlap, a score, a text, an id or a pair. Check 8 (at least fifteen distinct
topics) cannot be machine-checked and is the labeller's and the author's to meet.

Tokenizer: mirrors Curia.Domain.Search.LexicalSearch.Tokenize
(src/Curia.Domain/Search/LexicalSearch.cs): maximal runs of char.IsLetterOrDigit
UTF-16 code units, lower-cased invariantly; a code unit outside the BMP's letters
(a surrogate) is a separator. Overlap is Jaccard over term sets, as
LexicalOverlap.Jaccard in src/Curia.Domain/Search/DuplicatePolicy.cs.
"""
import json
import os
import statistics
import sys
import unicodedata

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
LEGACY_PAIRS = os.path.join(ROOT, "conformance", "retrieval", "dedupe-pairs.jsonl")
LEGACY_CORPUS = os.path.join(ROOT, "conformance", "retrieval", "corpus.jsonl")

CLASSES = {"bp": ("paraphrase", 36), "bn": ("near-miss", 36), "bc": ("near-copy", 18),
           "qo": ("off-topic", 12), "qm": ("made-up", 12)}
BANNED = ("cos", "sim", "score", "overlap")


def tokenize(text):
    if not text or not text.strip():
        return []
    out, cur = [], []
    for ch in text:
        if ord(ch) > 0xFFFF:  # two surrogate code units in C#; neither is a letter
            if cur:
                out.append("".join(cur)); cur = []
            continue
        cat = unicodedata.category(ch)
        if cat[0] == "L" or cat == "Nd":
            low = ch.lower()
            cur.append(low if len(low) == 1 else ch)
        elif cur:
            out.append("".join(cur)); cur = []
    if cur:
        out.append("".join(cur))
    return out


def jaccard(a, b):
    ta, tb = set(tokenize(a)), set(tokenize(b))
    if not ta and not tb:
        return 1.0
    return len(ta & tb) / len(ta | tb)


def is_str(x):
    return isinstance(x, str)


def load_jsonl_texts(path, pair_file):
    texts, words = set(), set()
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                o = json.loads(line)
                sides = [o.get("a"), o.get("b")] if pair_file else [o]
                for s in sides:
                    if isinstance(s, dict) and (s.get("title") is not None or s.get("body") is not None):
                        t, b = s.get("title") or "", s.get("body") or ""
                        texts.add((t, b))
                        words.update(tokenize(t + " " + b))
    except (OSError, ValueError):
        pass
    return texts, words


def sides_of(rec):
    """Every (title, body) text a record carries, as a list of tuples."""
    if not isinstance(rec, dict):
        return []
    if isinstance(rec.get("a"), dict) and isinstance(rec.get("b"), dict):
        return [(s.get("title"), s.get("body")) for s in (rec["a"], rec["b"])]
    if "text" in rec:
        return [(None, rec.get("text"))]
    return [(rec.get("title"), rec.get("body"))]


def has_banned_name(obj):
    if isinstance(obj, dict):
        for k, v in obj.items():
            if any(w in str(k).lower() for w in BANNED) or has_banned_name(v):
                return True
    return False


def main(argv):
    if len(argv) != 2:
        print("usage: validate-pairs.py <blind-pairs.jsonl>")
        return 2
    results = []

    def report(name, count):
        results.append((name, count))

    try:
        raw = open(argv[1], "rb").read()
    except OSError:
        print("FAIL readable 1")
        print("FAILURES 1")
        return 1

    # 1. format
    bad = 0
    recs = []
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("utf-8", errors="replace")
        bad += 1
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    for line in lines:
        if not line.strip():
            bad += 1
            continue
        try:
            o = json.loads(line)
        except ValueError:
            bad += 1
            continue
        if not isinstance(o, dict):
            bad += 1
            continue
        recs.append(o)
    report("format", bad)

    # 2. ids and classes
    expected = {f"{p}-{i:02d}": c for p, (c, n) in CLASSES.items() for i in range(1, n + 1)}
    seen, bad = {}, 0
    for r in recs:
        i = r.get("id")
        if not is_str(i) or i not in expected:
            bad += 1
            continue
        seen[i] = seen.get(i, 0) + 1
        if r.get("class") != expected[i]:
            bad += 1
    bad += sum(1 for i in expected if seen.get(i, 0) == 0)
    bad += sum(1 for n in seen.values() if n > 1)
    report("ids-classes", bad)

    # 3. schema
    bad = 0
    for r in recs:
        i = r.get("id") if is_str(r.get("id")) else ""
        p = i[:2]
        if has_banned_name(r):
            bad += 1
            continue
        keys = set(r)
        if p in ("bp", "bn", "bc"):
            ok = keys == {"id", "class", "a", "b", "reason"} and is_str(r["reason"])
            for s in (r.get("a"), r.get("b")):
                ok = ok and isinstance(s, dict) and set(s) == {"title", "body"} \
                    and is_str(s["title"]) and is_str(s["body"])
        elif p == "qo":
            ok = keys == {"id", "class", "title", "body"} and is_str(r["title"]) and is_str(r["body"])
        elif p == "qm":
            ok = keys == {"id", "class", "text"} and is_str(r["text"])
        else:
            ok = False
        bad += 0 if ok else 1
    report("schema", bad)

    # 4. lengths
    bad = 0
    for r in recs:
        for t, b in sides_of(r):
            if r.get("id", "")[:2] == "qm":
                toks = b.split() if is_str(b) else []
                if not (2 <= len(toks) <= 4) or "```" in b:
                    bad += 1
                continue
            if not (is_str(t) and is_str(b)):
                bad += 1
                continue
            if not (1 <= len(t) <= 100) or "\n" in t or "\r" in t \
                    or not (1 <= len(b) <= 600) or "```" in t or "```" in b:
                bad += 1
    report("lengths", bad)

    # 5. duplicates
    legacy_pairs, pair_words = load_jsonl_texts(LEGACY_PAIRS, True)
    legacy_corpus, corpus_words = load_jsonl_texts(LEGACY_CORPUS, False)
    legacy = legacy_pairs | legacy_corpus
    bad, counts = 0, {}
    for r in recs:
        sides = sides_of(r)
        if len(sides) == 2 and sides[0] == sides[1]:
            bad += 1
        for t, b in sides:
            if not is_str(b):
                continue
            key = (t, b) if t is not None else (None, b)
            counts[key] = counts.get(key, 0) + 1
            if (t, b) in legacy or (t is None and any(b == lb for _, lb in legacy)):
                bad += 1
    bad += sum(1 for n in counts.values() if n > 1)
    report("distinct-texts", bad)

    # 6. made-up tokens
    # The legacy corpus vocabulary only (Amendment 2): the host's system word lists hold obscure
    # real words, so a well-invented string can hit them by accident.
    vocab = pair_words | corpus_words
    bad = 0
    for r in recs:
        if is_str(r.get("id")) and r["id"].startswith("qm") and is_str(r.get("text")):
            bad += sum(1 for tok in r["text"].split() if tok.lower() in vocab
                       or any(t in vocab for t in tokenize(tok)))
    report("made-up-vocabulary", bad)

    # 7. overlap
    by = {"paraphrase": [], "near-miss": [], "near-copy": []}
    for r in recs:
        if isinstance(r.get("a"), dict) and isinstance(r.get("b"), dict) and r.get("class") in by:
            a, b = r["a"], r["b"]
            if all(is_str(x.get("title")) and is_str(x.get("body")) for x in (a, b)):
                by[r["class"]].append(jaccard(a["title"] + " " + a["body"], b["title"] + " " + b["body"]))
    med_bad = 0
    for cls, ok in (("paraphrase", lambda m: m <= 0.30), ("near-copy", lambda m: m >= 0.60)):
        if not by[cls] or not ok(statistics.median(by[cls])):
            med_bad += 1
    report("overlap-median", med_bad)
    # Amendment 2: the rule uses near-misses whose overlap reaches the floor O (at least 0.25),
    # so at least 12 of the 36 must reach 0.25; the near-miss median is not bounded.
    report("overlap-near-miss", max(0, 12 - sum(1 for v in by["near-miss"] if v >= 0.25)))
    report("overlap-pair-bounds",
           sum(1 for v in by["paraphrase"] if v > 0.60) + sum(1 for v in by["near-copy"] if v < 0.40))

    fails = 0
    for name, count in results:
        if count:
            print(f"FAIL {name} {count}")
            fails += 1
        else:
            print(f"PASS {name}")
    print(f"FAILURES {fails}")
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
