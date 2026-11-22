import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "validate-pairs.py")
spec = importlib.util.spec_from_file_location("vp", SCRIPT)
vp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(vp)

WORDS = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
         "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango"]


def uniq(n, k=6):
    """k words unique to n, from a synthetic space of invented stems."""
    return " ".join(f"zq{n}x{j}" for j in range(k))


def pair(pid, cls, n, shared):
    base = [f"sh{j}q" for j in range(shared)]
    a = " ".join(base + [f"ua{n}x{j}" for j in range(10 - shared)])
    b = " ".join(base + [f"ub{n}x{j}" for j in range(10 - shared)])
    return {"id": pid, "class": cls, "a": {"title": f"ta{n}", "body": a},
            "b": {"title": f"tb{n}", "body": b}, "reason": "r"}


def good_records():
    r = []
    for i in range(1, 37):
        r.append(pair(f"bp-{i:02d}", "paraphrase", i, 0))
    for i in range(1, 37):
        r.append(pair(f"bn-{i:02d}", "near-miss", 100 + i, 6))
    for i in range(1, 19):
        r.append(pair(f"bc-{i:02d}", "near-copy", 200 + i, 9))
    for i in range(1, 13):
        r.append({"id": f"qo-{i:02d}", "class": "off-topic", "title": f"ot{i}", "body": f"ob{i} words"})
    for i in range(1, 13):
        r.append({"id": f"qm-{i:02d}", "class": "made-up", "text": f"zzv{i}qq yyw{i}kk"})
    return r


def run(records=None, raw=None):
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "f.jsonl")
        with open(p, "wb") as f:
            f.write(raw if raw is not None else
                    "".join(json.dumps(x) + "\n" for x in records).encode("utf-8"))
        res = subprocess.run([sys.executable, "-I", SCRIPT, p], capture_output=True, text=True)
    return res.returncode, res.stdout


class ValidatePairs(unittest.TestCase):
    def failed(self, out, name):
        return any(l.startswith(f"FAIL {name} ") for l in out.splitlines())

    def test_good_file_passes(self):
        code, out = run(good_records())
        self.assertEqual(code, 0, out)
        self.assertIn("FAILURES 0", out)

    def test_format(self):
        code, out = run(raw=b"[]\n")
        self.assertTrue(self.failed(out, "format"))
        code, out = run(raw=(json.dumps(good_records()[0]) + "\n\n").encode())
        self.assertTrue(self.failed(out, "format"))
        code, out = run(raw=b"\xff\xfe\n")
        self.assertTrue(self.failed(out, "format"))
        self.assertEqual(code, 1)

    def test_ids_classes(self):
        r = good_records(); r.pop()
        self.assertTrue(self.failed(run(r)[1], "ids-classes"))
        r = good_records(); r[0]["class"] = "near-miss"
        self.assertTrue(self.failed(run(r)[1], "ids-classes"))
        r = good_records(); r.append(dict(r[0]))
        self.assertTrue(self.failed(run(r)[1], "ids-classes"))

    def test_schema(self):
        r = good_records(); r[0]["cosine"] = 1
        self.assertTrue(self.failed(run(r)[1], "schema"))
        r = good_records(); r[0]["a"]["score"] = "x"
        self.assertTrue(self.failed(run(r)[1], "schema"))
        r = good_records(); del r[0]["reason"]
        self.assertTrue(self.failed(run(r)[1], "schema"))
        r = good_records(); r[-1]["extra"] = 1
        self.assertTrue(self.failed(run(r)[1], "schema"))

    def test_lengths(self):
        r = good_records(); r[0]["a"]["title"] = "x" * 101
        self.assertTrue(self.failed(run(r)[1], "lengths"))
        r = good_records(); r[0]["a"]["title"] = "two\nlines"
        self.assertTrue(self.failed(run(r)[1], "lengths"))
        r = good_records(); r[0]["a"]["body"] = "x" * 601
        self.assertTrue(self.failed(run(r)[1], "lengths"))
        r = good_records(); r[0]["a"]["body"] = "```code```"
        self.assertTrue(self.failed(run(r)[1], "lengths"))
        r = good_records(); r[-1]["text"] = "one"
        self.assertTrue(self.failed(run(r)[1], "lengths"))
        r = good_records(); r[-1]["text"] = "a b c d e"
        self.assertTrue(self.failed(run(r)[1], "lengths"))

    def test_distinct(self):
        r = good_records(); r[0]["b"] = dict(r[0]["a"])
        self.assertTrue(self.failed(run(r)[1], "distinct-texts"))
        r = good_records(); r[1]["a"] = dict(r[0]["a"])
        self.assertTrue(self.failed(run(r)[1], "distinct-texts"))
        r = good_records()
        r[0]["a"] = {"title": "ECONNRESET from npgsql after the pool idles",
                     "body": "npgsql throws ECONNRESET after the pooler idles the connection; keepalive is off"}
        self.assertTrue(self.failed(run(r)[1], "distinct-texts"))

    def test_made_up(self):
        r = good_records(); r[-1]["text"] = "npgsql zzv9qq"
        self.assertTrue(self.failed(run(r)[1], "made-up-vocabulary"))

    def test_made_up_ignores_system_word_lists(self):
        # Amendment 2: an obscure dictionary word that the legacy corpus does not hold is not a hit.
        r = good_records(); r[-1]["text"] = "zymurgy zzv9qq"
        self.assertFalse(self.failed(run(r)[1], "made-up-vocabulary"))

    def test_near_miss_needs_twelve_at_the_floor(self):
        # Amendment 2: no median bound on near-misses; at least 12 of 36 reach Jaccard 0.25.
        r = good_records()
        for i, x in enumerate(z for z in r if z["id"].startswith("bn")):
            if i >= 12:
                x.update(pair(x["id"], "near-miss", 300 + i, 0))
        out = run(r)[1]
        self.assertFalse(self.failed(out, "overlap-near-miss"), out)
        self.assertFalse(self.failed(out, "overlap-median"), out)
        r = good_records()
        for i, x in enumerate(z for z in r if z["id"].startswith("bn")):
            if i >= 11:
                x.update(pair(x["id"], "near-miss", 300 + i, 0))
        self.assertTrue(self.failed(run(r)[1], "overlap-near-miss"))

    def test_overlap(self):
        r = good_records()
        for x in r:
            if x["id"].startswith("bp"):
                x["b"]["body"] = x["a"]["body"]
        out = run(r)[1]
        self.assertTrue(self.failed(out, "overlap-median"))
        self.assertTrue(self.failed(out, "overlap-pair-bounds"))
        r = good_records()
        for x in r:
            if x["id"].startswith("bc"):
                x["b"]["body"] = "unrelated words only here"
        self.assertTrue(self.failed(run(r)[1], "overlap-median"))

    def test_tokenizer_mirrors_csharp(self):
        self.assertEqual(vp.tokenize("Foo-bar_9 ÉCOLE\U0001F600x"), ["foo", "bar", "9", "école", "x"])
        self.assertEqual(vp.jaccard("", ""), 1.0)

    def test_output_leaks_nothing(self):
        r = good_records()
        r[0]["a"]["title"] = "SECRETTITLE"
        r[0]["a"]["body"] = "SECRETBODY " + "x" * 700
        r[0]["id"] = "bp-01"
        r[0]["cosine"] = 0.987654
        code, out = run(r)
        self.assertEqual(code, 1)
        for leak in ("SECRETTITLE", "SECRETBODY", "bp-01", "0.987", "cosine", "zq"):
            self.assertNotIn(leak, out)
        for line in out.splitlines():
            self.assertRegex(line, r"^(PASS [a-z-]+|FAIL [a-z-]+ \d+|FAILURES \d+)$")


if __name__ == "__main__":
    unittest.main()
