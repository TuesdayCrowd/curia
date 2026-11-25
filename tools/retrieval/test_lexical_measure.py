"""By-hand tests of tools/retrieval/lexical-measure.py on synthetic material only (item 342, errata G81).

Run: python3 -m unittest tools/retrieval/test_lexical_measure.py   (from the repository root)

Like test_validate_queries.py and the two D10b tests, this is run by hand: nothing in CI runs it.
What the build depends on, the agreement of this script with LexicalSearch, is asserted by
tests/Curia.Application.Tests/Retrieval/StopwordRuleTests.cs (KWC2, KWC14), which runs the script.
It reads no blind text.
"""
import hashlib
import importlib.util
import io
import contextlib
import os
import tempfile
import unittest
from fractions import Fraction

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("lexical_measure", os.path.join(HERE, "lexical-measure.py"))
lm = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lm)

STOP = frozenset(lm.load_list())


def post(seq, title, body, tags=()):
    return {"seq": seq, "id": f"p{seq}", "title": title, "body": body, "tags": list(tags)}


def cache_of(posts):
    return {p["seq"]: (lm.tokenize(p["title"]), lm.tokenize(p["body"])) for p in posts}


def listed(posts, text, use_list):
    terms, _, had = lm.search_terms(text, STOP)
    use = terms if use_list else lm.tokenize(text)
    return lm.rank(posts, use, had, cache_of(posts))


class TheList(unittest.TestCase):
    def test_it_is_the_rule_files_block(self):
        words = lm.load_list()
        self.assertEqual(111, len(words))
        self.assertEqual(sorted(set(words)), words)
        self.assertEqual("738e8e0ef8c1077fcf81eda461be245fd9c7e777ca2da5f5ed8a05dcceaafaa6", lm.list_digest(words))

    def test_it_equals_stopwords_txt(self):
        with open(os.path.join(lm.RETRIEVAL, "stopwords.txt"), encoding="utf-8") as f:
            self.assertEqual(f.read(), "\n".join(lm.load_list()))

    def test_the_parameter_table_has_nine_rows(self):
        params = lm.load_params()
        self.assertEqual(9, len(params))
        self.assertEqual(Fraction(1, 4), params["removal_page_share_most"])
        self.assertEqual(25, params["keep_window"])


class TheRuleFlag(unittest.TestCase):
    def _print_list(self, *args):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(0, lm.main(["--print-list", *args]))
        return out.getvalue().split("\n")

    def test_the_default_prints_the_committed_list(self):
        self.assertEqual(lm.load_list(), self._print_list())

    def test_a_rule_file_with_a_word_removed_prints_one_word_fewer(self):
        with open(lm.RULE, encoding="utf-8") as f:
            text = f.read()
        edited = text.replace("- determiners (7): `a an the this that these those`",
                              "- determiners (6): `a an this that these those`", 1)
        self.assertNotEqual(text, edited)
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "rule.md")
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(edited)
            words = self._print_list("--rule", path)
        self.assertEqual(110, len(words))
        self.assertNotIn("the", words)
        self.assertIn("an", words)


class TheTokenizer(unittest.TestCase):
    def test_runs_of_letters_and_digits_are_lower_cased(self):
        self.assertEqual(["how", "do", "i", "rotate", "the", "key"], lm.tokenize("How do I rotate the KEY?"))

    def test_a_contraction_is_cut_in_two(self):
        self.assertEqual(["don", "t", "panic"], lm.tokenize("don't panic"))

    def test_non_ascii_letters_are_terms(self):
        self.assertEqual(["f\u00fcr", "\u00e7a"], lm.tokenize("F\u00dcR \u00e7a"))

    def test_a_surrogate_pair_is_a_separator(self):
        self.assertEqual(["a", "b"], lm.tokenize("a\U0001F600b"))

    def test_no_text_is_no_token(self):
        for text in (None, "", "  ", "???"):
            self.assertEqual([], lm.tokenize(text))


class SearchTerms(unittest.TestCase):
    def test_the_hand_table(self):
        self.assertEqual((["rotate", "key"], ["how", "do", "i", "the"], True), lm.search_terms("How do I rotate the key?", STOP))
        self.assertEqual((["panic"], ["don", "t"], True), lm.search_terms("don't panic", STOP))
        self.assertEqual(([], ["the"], True), lm.search_terms("THE the The", STOP))
        self.assertEqual(([], [], False), lm.search_terms("???", STOP))

    def test_a_repeated_term_is_kept_and_a_repeated_ignored_word_is_not(self):
        self.assertEqual((["pool", "pool"], ["the"], True), lm.search_terms("the pool the pool", STOP))


class TheThreeStates(unittest.TestCase):
    corpus = [post(0, "a", "the thing"), post(1, "b", "the pool"), post(2, "c", "other")]

    def test_no_token_lists_nothing_here_because_the_listing_is_the_callers(self):
        self.assertEqual([], listed(self.corpus, "???", True))

    def test_all_ignored_lists_nothing_with_the_list_and_what_holds_the_word_without_it(self):
        self.assertEqual([], listed(self.corpus, "the", True))
        self.assertEqual([0, 1, 1, 1], listed(self.corpus, "the", False))

    def test_a_remaining_term_ranks_over_the_remaining_terms(self):
        self.assertEqual([1, 1], listed(self.corpus, "the pool", True))


class TheArithmetic(unittest.TestCase):
    def test_the_list_moves_the_order_where_the_rule_predicts(self):
        corpus = [post(0, "x", "the " * 30 + "pool"), post(1, "y", "pool pool")]
        self.assertEqual([0, 31, 1, 2], listed(corpus, "the pool", False))
        self.assertEqual([1, 2, 0, 1], listed(corpus, "the pool", True))

    def test_a_title_counts_five_a_tag_three_a_body_one_and_ties_break_by_sequence(self):
        corpus = [post(0, "pool", "x"), post(1, "x", "x", ["Pool"]), post(2, "x", "pool"), post(3, "x", "pool")]
        self.assertEqual([0, 5, 1, 3, 2, 1, 3, 1], listed(corpus, "pool", True))

    def test_a_post_that_says_the_still_holds_it(self):
        corpus = [post(0, "the", "the")]
        self.assertEqual([0, 6], listed(corpus, "the", False))


def record(qid, cls, with_hits, without_hits, target=None, terms=("x",), had=True, all_ignored=False):
    return {"kind": "query", "id": qid, "class": cls, "form": "query", "tokens": [], "terms": list(terms), "ignored": [],
            "had_token": had, "all_ignored": all_ignored, "target_seq": target, "with": with_hits, "without": without_hits}


def hits(*seqs):
    flat = []
    for s in seqs:
        flat += [s, 1]
    return flat


class TheMeasures(unittest.TestCase):
    params = lm.load_params()
    header = {"kind": "corpus", "size": 100}

    def on_topic(self, kept, lost, stopword_only):
        recs = []
        for i in range(kept):
            recs.append(record(f"k{i}", "target", hits(5), hits(5), target=5))
        for i in range(lost):                      # first page without the list, past the window with it
            recs.append(record(f"l{i}", "target", hits(*range(10, 50), 5), hits(5), target=5))
        for i in range(stopword_only):
            recs.append(record(f"s{i}", "target", [], hits(5), target=5, terms=(), all_ignored=True))
        return recs

    def off_topic(self, narrow):
        return [record(f"o{i}", "off-topic", hits(1), hits(*range(60))) for i in range(narrow)]

    def test_a_base_under_the_floor_is_undecided(self):
        m = lm.measures([self.header] + self.on_topic(10, 0, 3) + self.off_topic(30), self.params)
        self.assertEqual("undecided", m["verdict"])
        self.assertEqual(3, m["keep_on_topic"]["stopword_only"])
        self.assertEqual(10, m["keep_on_topic"]["base"])

    def test_a_stopword_only_target_is_not_in_the_base(self):
        m = lm.measures([self.header] + self.on_topic(30, 0, 5) + self.off_topic(30), self.params)
        self.assertEqual(30, m["keep_on_topic"]["base"])
        self.assertEqual(5, m["keep_on_topic"]["all_ignored"])
        self.assertEqual("ships", m["verdict"])

    def test_a_loss_over_five_per_cent_is_no(self):
        m = lm.measures([self.header] + self.on_topic(37, 3, 0) + self.off_topic(30), self.params)
        self.assertEqual((40, 37), (m["keep_on_topic"]["base"], m["keep_on_topic"]["kept"]))
        self.assertFalse(m["keep_on_topic"]["kept_ok"])
        self.assertEqual("no", m["verdict"])

    def test_two_losses_in_forty_are_allowed_by_the_ratio(self):
        m = lm.measures([self.header] + self.on_topic(38, 2, 0) + self.off_topic(30), self.params)
        self.assertTrue(m["keep_on_topic"]["kept_ok"])

    def test_removal_fails_when_the_page_stays_wide(self):
        wide = [record(f"w{i}", "off-topic", hits(*range(60)), hits(*range(60))) for i in range(30)]
        m = lm.measures([self.header] + self.on_topic(30, 0, 0) + wide, self.params)
        self.assertFalse(m["removal"]["ok"])
        self.assertEqual("no", m["verdict"])

    def test_a_listing_is_reported_and_excluded(self):
        listing = record("ko-x", "off-topic", [], [], terms=(), had=False)
        m = lm.measures([self.header] + self.on_topic(30, 0, 0) + self.off_topic(30) + [listing], self.params)
        self.assertEqual(["ko-x"], m["listings"])
        self.assertEqual(30, m["removal"]["queries"])

    def test_the_hazard_loss_counts_an_all_ignored_query(self):
        haz = [record(f"h{i}", "hazard", [], hits(7), target=7, terms=(), all_ignored=True) for i in range(5)]
        haz += [record(f"g{i}", "hazard", hits(7), hits(7), target=7) for i in range(15)]
        m = lm.measures([self.header] + self.on_topic(30, 0, 0) + self.off_topic(30) + haz, self.params)
        self.assertEqual((20, 5, 4, 5), (m["keep_hazard"]["m"], m["keep_hazard"]["lost"], m["keep_hazard"]["allowed"], m["keep_hazard"]["lost_all_ignored"]))
        self.assertFalse(m["keep_hazard"]["ok"])
        self.assertEqual("no", m["verdict"])


class TheOutputIsDeterministic(unittest.TestCase):
    def test_two_runs_over_synthetic_material_are_equal(self):
        import json
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            q = os.path.join(d, "q.jsonl")
            rows = [{"id": "kt-01", "class": "target", "post": {"title": "pool sizing", "body": "how big a pool"}, "terse": "pool", "sentence": "how do i size the pool"},
                    {"id": "ko-01", "class": "off-topic", "query": "the best way to bake bread"},
                    {"id": "kv-01", "class": "vague", "query": "how to"}]
            with open(q, "w", encoding="utf-8") as f:
                f.write("\n".join(json.dumps(r) for r in rows) + "\n")
            a = lm.run(queries_path=q)
            b = lm.run(queries_path=q)
            self.assertEqual(a, b)
            self.assertEqual(hashlib.sha256(json.dumps(a).encode()).hexdigest(), hashlib.sha256(json.dumps(b).encode()).hexdigest())
            vague = [r for r in a if r.get("id") == "kv-01"][0]
            self.assertTrue(vague["all_ignored"])
            self.assertEqual([], vague["with"])


if __name__ == "__main__":
    unittest.main()
