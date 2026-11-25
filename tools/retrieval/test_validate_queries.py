import copy
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "validate-queries.py")
LINE = re.compile(r"^(PASS [a-z0-9-]+|FAIL [a-z0-9-]+|PASS|FAIL)$")
CHECKS = ["json-lines", "utf8", "line-count", "ids", "schema", "lengths", "distinct",
          "no-search-equals-title", "no-hit-member"]


def w(tag, n):
    return " ".join(f"{tag}{j}z" for j in range(n))


def good_records():
    r = []
    for p, cls, n in (("kt", "target", 60), ("kh", "hazard", 20)):
        for i in range(1, n + 1):
            k = f"{p}{i}"
            r.append({"id": f"{p}-{i:02d}", "class": cls,
                      "post": {"title": f"title {k}", "body": f"body {k} text"},
                      "terse": w(f"te{k}", 3), "sentence": w(f"se{k}", 8)})
    for i in range(1, 31):
        r.append({"id": f"ko-{i:02d}", "class": "off-topic", "query": w(f"of{i}", 6)})
    for i in range(1, 21):
        r.append({"id": f"kv-{i:02d}", "class": "vague", "query": w(f"va{i}", 3)})
    return r


def dump(records):
    return "".join(json.dumps(x) + "\n" for x in records).encode("utf-8")


def run(records=None, raw=None):
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "f.jsonl")
        with open(p, "wb") as f:
            f.write(raw if raw is not None else dump(records))
        res = subprocess.run([sys.executable, "-I", SCRIPT, p], capture_output=True, text=True)
    return res.returncode, res.stdout


def failed_checks(out):
    return {l[5:] for l in out.splitlines() if l.startswith("FAIL ")}


class ValidateQueries(unittest.TestCase):
    def only(self, name, records=None, raw=None):
        code, out = run(records, raw)
        self.assertEqual(code, 1)
        self.assertEqual(failed_checks(out), {name})
        self.assertEqual(out.splitlines()[-1], "FAIL")

    def mutate(self, fn):
        recs = good_records()
        fn(recs)
        return recs

    def test_good_file_passes(self):
        code, out = run(good_records())
        self.assertEqual(code, 0)
        self.assertEqual(out.splitlines(), [f"PASS {c}" for c in CHECKS] + ["PASS"])

    def test_output_is_leak_free(self):
        recs = self.mutate(lambda r: r[0]["post"].update(title="x" * 101))
        recs[1]["score"] = 3
        recs[5]["id"] = "kt-secret"
        for c, out in (run(recs), run(good_records())):
            for line in out.splitlines():
                self.assertRegex(line, LINE)
            self.assertNotRegex(out.replace("utf8", ""), r"\d")
            self.assertNotIn("secret", out)
            self.assertNotIn("kt-", out)

    def test_unreadable_file_fails_everything(self):
        code, out = subprocess.run([sys.executable, "-I", SCRIPT, "/nonexistent/q.jsonl"],
                                   capture_output=True, text=True).returncode, None
        self.assertEqual(code, 1)

    def test_no_argument_prints_nothing_on_stdout(self):
        res = subprocess.run([sys.executable, "-I", SCRIPT], capture_output=True, text=True)
        self.assertEqual((res.returncode, res.stdout), (2, ""))

    def test_blank_line_fails_json_lines_and_line_count(self):
        raw = dump(good_records()) + b"\n"
        code, out = run(raw=raw)
        self.assertEqual(failed_checks(out), {"json-lines", "line-count"})

    def test_unparseable_line_fails_json_lines_only_when_count_holds(self):
        lines = dump(good_records()).split(b"\n")
        lines[3] = b"{not json"
        code, out = run(raw=b"\n".join(lines))
        self.assertIn("json-lines", failed_checks(out))
        self.assertIn("ids", failed_checks(out))  # the record is missing too

    def test_array_line_fails_json_lines(self):
        lines = dump(good_records()).split(b"\n")
        lines[0] = b"[1]"
        code, out = run(raw=b"\n".join(lines))
        self.assertIn("json-lines", failed_checks(out))

    def test_duplicate_member_fails_json_lines(self):
        lines = dump(good_records()).decode().split("\n")
        lines[0] = lines[0][:-1] + ', "terse": "again"}'
        code, out = run(raw="\n".join(lines).encode())
        self.assertIn("json-lines", failed_checks(out))

    def test_invalid_utf8_fails_utf8(self):
        recs = good_records()
        recs[0]["post"]["body"] = "body QQQ text"
        raw = dump(recs).replace(b"QQQ", b"\xff\xfe")
        code, out = run(raw=raw)
        self.assertIn("utf8", failed_checks(out))
        self.assertNotIn("line-count", failed_checks(out))

    def test_too_few_lines_fails_line_count(self):
        code, out = run(good_records()[:-1])
        self.assertIn("line-count", failed_checks(out))

    def test_missing_id_fails_ids(self):
        recs = self.mutate(lambda r: r[0].update(id="kt-61"))
        self.only("ids", recs)

    def test_duplicate_id_fails_ids(self):
        recs = self.mutate(lambda r: r[1].update(id="kt-01"))
        self.only("ids", recs)

    def test_wrong_class_fails_ids(self):
        recs = self.mutate(lambda r: r[0].update({"class": "hazard"}))
        self.only("ids", recs)

    def test_extra_member_fails_schema(self):
        recs = self.mutate(lambda r: r[0].update(note="x"))
        self.only("schema", recs)

    def test_missing_member_fails_schema(self):
        recs = self.mutate(lambda r: r[0].pop("terse"))
        self.assertIn("schema", failed_checks(run(recs)[1]))

    def test_extra_post_member_fails_schema(self):
        recs = self.mutate(lambda r: r[0]["post"].update(tags="x"))
        self.only("schema", recs)

    def test_non_string_fails_schema(self):
        recs = self.mutate(lambda r: r[0].update(terse=5))
        code, out = run(recs)
        self.assertIn("schema", failed_checks(out))

    def test_query_on_a_target_fails_schema(self):
        recs = self.mutate(lambda r: r[0].update(query="x y z w"))
        self.only("schema", recs)

    def test_post_on_a_vague_fails_schema(self):
        recs = self.mutate(lambda r: r[-1].update(post={"title": "t", "body": "b"}))
        self.only("schema", recs)

    def test_title_101_chars_fails_and_100_passes(self):
        recs = self.mutate(lambda r: r[0]["post"].update(title="a" * 101))
        self.only("lengths", recs)
        recs = self.mutate(lambda r: r[0]["post"].update(title="a" * 100))
        self.assertEqual(run(recs)[0], 0)

    def test_title_counts_code_points_not_bytes(self):
        recs = self.mutate(lambda r: r[0]["post"].update(title="é" * 100))
        self.assertEqual(run(recs)[0], 0)

    def test_title_with_newline_fails_lengths(self):
        recs = self.mutate(lambda r: r[0]["post"].update(title="two\nlines"))
        self.only("lengths", recs)

    def test_empty_title_fails_lengths(self):
        recs = self.mutate(lambda r: r[0]["post"].update(title=""))
        self.only("lengths", recs)

    def test_body_601_fails_and_600_passes(self):
        recs = self.mutate(lambda r: r[0]["post"].update(body="b" * 601))
        self.only("lengths", recs)
        recs = self.mutate(lambda r: r[0]["post"].update(body="b" * 600))
        self.assertEqual(run(recs)[0], 0)

    def test_terse_word_bounds(self):
        for n, ok in ((1, False), (2, True), (5, True), (6, False)):
            recs = self.mutate(lambda r: r[0].update(terse=w("tt", n)))
            self.assertEqual(run(recs)[0] == 0, ok, n)

    def test_sentence_word_bounds(self):
        for n, ok in ((5, False), (6, True), (14, True), (15, False)):
            recs = self.mutate(lambda r: r[0].update(sentence=w("ss", n)))
            self.assertEqual(run(recs)[0] == 0, ok, n)

    def test_off_topic_word_bounds(self):
        idx = 80
        for n, ok in ((3, False), (4, True), (12, True), (13, False)):
            recs = self.mutate(lambda r: r[idx].update(query=w("oo", n)))
            self.assertEqual(run(recs)[0] == 0, ok, n)

    def test_vague_word_bounds(self):
        idx = 110
        for n, ok in ((1, False), (2, True), (6, True), (7, False)):
            recs = self.mutate(lambda r: r[idx].update(query=w("vv", n)))
            self.assertEqual(run(recs)[0] == 0, ok, n)

    def test_words_split_on_any_whitespace(self):
        recs = self.mutate(lambda r: r[0].update(terse="one\ttwo three"))
        self.assertEqual(run(recs)[0], 0)

    def test_duplicate_body_fails_distinct(self):
        recs = self.mutate(lambda r: r[1]["post"].update(body=r[0]["post"]["body"]))
        self.only("distinct", recs)

    def test_duplicate_search_fails_distinct(self):
        recs = self.mutate(lambda r: r[85].update(query=r[84]["query"]))
        self.only("distinct", recs)

    def test_duplicate_differing_in_case_and_space_fails_distinct(self):
        recs = self.mutate(lambda r: r[85].update(query="  " + r[84]["query"].upper() + " "))
        self.only("distinct", recs)

    def test_search_equal_to_a_title_fails_that_check_alone(self):
        recs = self.mutate(lambda r: r[85].update(query="title kt1"))
        recs[85]["query"] = "title kt1 a b"  # keep word bounds, then set a title to match
        recs[0]["post"]["title"] = recs[85]["query"]
        self.only("no-search-equals-title", recs)

    def test_own_terse_equal_to_own_title_fails_that_check_alone(self):
        recs = self.mutate(lambda r: r[0]["post"].update(title=r[0]["terse"]))
        self.only("no-search-equals-title", recs)

    def test_hit_member_fails_that_check_alone(self):
        for name in ("score", "hit_count", "rank", "cosine"):
            recs = self.mutate(lambda r: r[0].update({name: 1}))
            self.only("no-hit-member", recs)

    def test_hit_member_nested_fails(self):
        recs = self.mutate(lambda r: r[0]["post"].update(overlap=1))
        self.only("no-hit-member", recs)


if __name__ == "__main__":
    unittest.main()
