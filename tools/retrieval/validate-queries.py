#!/usr/bin/env python3
"""Mechanical validation of the blind query set (item 342, Task 2a).

Usage: python3 tools/retrieval/validate-queries.py <blind-queries.jsonl>

Output is one line per check, `PASS <check>` or `FAIL <check>`, then a last line
`PASS` or `FAIL`; the exit code is 0 or 1 (2 for a wrong command line, with no
output on stdout). It prints no id, no text, no term and no count of anything
(plan Amendment 1, ruling 0): a count of failing records is information about
the text. A check that cannot run because the file is damaged reports FAIL.

Checks, in this fixed public order:
  json-lines             every line is one JSON object (no blank line, no duplicate
                         member, no NaN or Infinity, no array or scalar)
  utf8                   the file decodes as UTF-8
  line-count             exactly 130 lines (60 + 20 + 30 + 20)
  ids                    kt-01..kt-60, kh-01..kh-20, ko-01..ko-30, kv-01..kv-20,
                         each once, each with its class (target, hazard,
                         off-topic, vague)
  schema                 per class: kt/kh {id, class, post{title, body}, terse,
                         sentence}; ko/kv {id, class, query}; strings only; no
                         other member (a member named like a hit count is left
                         to no-hit-member)
  lengths                title 1-100 code points on one line; body 1-600; terse
                         2-5 words; sentence 6-14; off-topic 4-12; vague 2-6;
                         words are whitespace-separated (str.split)
  distinct               no text appears twice anywhere (titles, bodies, searches),
                         compared after NFC, case folding and whitespace
                         collapse; a search equal to a title is left to the next
                         check
  no-search-equals-title no terse, sentence or query equals any post's title
  no-hit-member          no member, at any depth, is named like a hit count, rank
                         or score (hit, count, cos, sim, score, rank, overlap,
                         page, share, mrr, found)

NOT checked (judgements, plan Amendment 1 ruling 0): that the 80 posts cover at
least twenty topics with at most four on one; that a body is one to four
sentences; that vague searches hold no technical term; that off-topic searches
are in fact off topic; that a search does not copy phrases from its post; that
terse and sentence are the same person's search for the same post.
"""
import json
import re
import sys
import unicodedata

CLASSES = {"kt": ("target", 60), "kh": ("hazard", 20), "ko": ("off-topic", 30), "kv": ("vague", 20)}
EXPECTED = {f"{p}-{i:02d}": c for p, (c, n) in CLASSES.items() for i in range(1, n + 1)}
BANNED = ("hit", "count", "cos", "sim", "score", "rank", "overlap", "page", "share", "mrr", "found")
CHECKS = ("json-lines", "utf8", "line-count", "ids", "schema", "lengths", "distinct",
          "no-search-equals-title", "no-hit-member")


def is_str(x):
    return isinstance(x, str)


def no_dup_pairs(pairs):
    d = {}
    for k, v in pairs:
        if k in d:
            raise ValueError("duplicate member")
        d[k] = v
    return d


def no_constant(_):
    raise ValueError("non-finite number")


def norm(s):
    return " ".join(unicodedata.normalize("NFC", s).casefold().split())


def banned_name(k):
    return any(w in str(k).lower() for w in BANNED)


def has_banned(obj):
    if isinstance(obj, dict):
        return any(banned_name(k) or has_banned(v) for k, v in obj.items())
    if isinstance(obj, list):
        return any(has_banned(v) for v in obj)
    return False


def prefix(r):
    i = r.get("id")
    return i[:2] if is_str(i) else ""


def post_of(r):
    p = r.get("post")
    return p if isinstance(p, dict) else {}


def searches(r):
    if prefix(r) in ("kt", "kh"):
        return [r.get("terse"), r.get("sentence")]
    return [r.get("query")]


def check_ids(recs):
    seen = {}
    for r in recs:
        i = r.get("id")
        if not is_str(i) or i not in EXPECTED or r.get("class") != EXPECTED[i]:
            return False
        if i in seen:
            return False
        seen[i] = 1
    return len(seen) == len(EXPECTED)


def check_schema(recs):
    for r in recs:
        p = prefix(r)
        keys = {k for k in r if not banned_name(k)}
        if p in ("kt", "kh"):
            post = r.get("post")
            if keys != {"id", "class", "post", "terse", "sentence"} or not isinstance(post, dict):
                return False
            if {k for k in post if not banned_name(k)} != {"title", "body"}:
                return False
            if not all(is_str(x) for x in (post["title"], post["body"], r["terse"], r["sentence"])):
                return False
        elif p in ("ko", "kv"):
            if keys != {"id", "class", "query"} or not is_str(r["query"]):
                return False
        else:
            return False
        if not is_str(r.get("id")) or not is_str(r.get("class")):
            return False
    return True


def words_ok(s, lo, hi):
    return is_str(s) and lo <= len(s.split()) <= hi


def check_lengths(recs):
    for r in recs:
        p = prefix(r)
        if p in ("kt", "kh"):
            t, b = post_of(r).get("title"), post_of(r).get("body")
            if not (is_str(t) and 1 <= len(t) <= 100 and "\n" not in t and "\r" not in t):
                return False
            if not (is_str(b) and 1 <= len(b) <= 600):
                return False
            if not (words_ok(r.get("terse"), 2, 5) and words_ok(r.get("sentence"), 6, 14)):
                return False
        elif p == "ko":
            if not words_ok(r.get("query"), 4, 12):
                return False
        elif p == "kv":
            if not words_ok(r.get("query"), 2, 6):
                return False
        else:
            return False
    return True


def entries(recs):
    """(kind, normalized text) for every text; kind is title, body or search."""
    out = []
    for r in recs:
        if prefix(r) in ("kt", "kh"):
            post = post_of(r)
            out += [("title", post.get("title")), ("body", post.get("body"))]
        out += [("search", s) for s in searches(r)]
    return [(k, norm(t)) for k, t in out if is_str(t)]


def check_distinct(recs):
    groups = {}
    for kind, t in entries(recs):
        groups.setdefault(t, []).append(kind)
    for kinds in groups.values():
        for i in range(len(kinds)):
            for j in range(i + 1, len(kinds)):
                if {kinds[i], kinds[j]} != {"title", "search"}:
                    return False
    return True


def check_search_not_title(recs):
    titles = {norm(post_of(r)["title"]) for r in recs
              if prefix(r) in ("kt", "kh") and is_str(post_of(r).get("title"))}
    return not any(is_str(s) and norm(s) in titles for r in recs for s in searches(r))


def run(path):
    results = dict.fromkeys(CHECKS, False)
    try:
        raw = open(path, "rb").read()
    except OSError:
        return results
    try:
        text = raw.decode("utf-8")
        results["utf8"] = True
    except UnicodeDecodeError:
        text = raw.decode("utf-8", errors="replace")
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    results["line-count"] = len(lines) == len(EXPECTED)
    recs, bad = [], False
    for line in lines:
        try:
            if not line.strip() or line.endswith("\r"):
                raise ValueError("blank or CR")
            o = json.loads(line, object_pairs_hook=no_dup_pairs, parse_constant=no_constant)
            if not isinstance(o, dict):
                raise ValueError("not an object")
            recs.append(o)
        except (ValueError, RecursionError):
            bad = True
    results["json-lines"] = not bad
    for name, fn in (("ids", check_ids), ("schema", check_schema), ("lengths", check_lengths),
                     ("distinct", check_distinct), ("no-search-equals-title", check_search_not_title)):
        try:
            results[name] = fn(recs)
        except Exception:
            results[name] = False
    results["no-hit-member"] = not any(has_banned(r) for r in recs)
    return results


def main(argv):
    if len(argv) != 2:
        sys.stderr.write("usage: validate-queries.py <blind-queries.jsonl>\n")
        return 2
    try:
        results = run(argv[1])
    except Exception:
        results = dict.fromkeys(CHECKS, False)
    for name in CHECKS:
        print(("PASS " if results[name] else "FAIL ") + name)
    ok = all(results.values())
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
