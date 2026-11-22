"""Tests of derive-thresholds.py on synthetic fixtures; each quotes the rule's sentence beside it.

    python3 -I -m unittest tools/retrieval/test_derive_thresholds.py   (from the repository root)
"""
import hashlib
import importlib.util
import os
import unittest
from fractions import Fraction as F

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("derive_thresholds", os.path.join(HERE, "derive-thresholds.py"))
dt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dt)

P = dt.load_rule()


def row(cls, id_, passage, overlap, query_passage=0.5, source="blind"):
    return {"id": id_, "class": cls, "source": source, "passage": dt.exact(passage), "overlap": dt.exact(overlap), "query_passage": dt.exact(query_passage)}


def ids_where(n, want_derivation, prefix="t"):
    out, i = [], 0
    while len(out) < n:
        c = "%s%d" % (prefix, i)
        i += 1
        if dt.is_derivation(c) == want_derivation:
            out.append(c)
    return out


class RuleTable(unittest.TestCase):
    def test_the_table_is_parsed_from_the_rule_file(self):
        self.assertEqual(P["grid_cosine"], F(5, 1000))
        self.assertEqual(P["refuse_cosine_least"], F(94, 100))
        self.assertEqual(len(P), 14)


class Split(unittest.TestCase):
    def test_first_byte_of_sha256_even_is_derivation(self):
        # "in Derivation if the first byte of the SHA-256 of its UTF-8 id is even and in Check otherwise."
        self.assertTrue(dt.is_derivation("bp-01"))        # 76
        self.assertFalse(dt.is_derivation("bn-01"))       # 7
        self.assertFalse(dt.is_derivation("literal-identical"))  # 219
        for i in ("x", "yy", "zzz"):
            self.assertEqual(dt.is_derivation(i), hashlib.sha256(i.encode()).digest()[0] % 2 == 0)


class Overlap(unittest.TestCase):
    def o(self, least):
        # "The largest multiple of 0.05 not above the least overlap of any repeat in Derivation, clamped to 0.25 at the lowest and 0.50 at the highest."
        return dt.derive([row("near-copy", "a", 0.99, least), row("literal", "b", 0.99, 0.9)], P)["O"][0]

    def test_clamps_and_grid(self):
        self.assertEqual(self.o(0.12), F(1, 4))
        self.assertEqual(self.o(0.83), F(1, 2))
        self.assertEqual(self.o(0.37), F(35, 100))
        self.assertEqual(self.o(0.40), F(40, 100))


class Refuse(unittest.TestCase):
    def r(self, *nd):
        # "R is the larger of 0.94 and the least multiple of 0.005 that is at least M + 0.01; if no non-duplicate has overlap at least O, R is 0.94."
        rows = [row("near-copy", "r1", 0.99, 0.30), row("literal", "r2", 0.99, 0.9), *nd]
        return dt.derive(rows, P)["R"][0]

    def test_rises_only_with_an_overlapping_nonduplicate(self):
        self.assertEqual(self.r(row("related", "n1", 0.953, 0.45), row("near-miss", "n2", 0.99, 0.20)), F(965, 1000))
        self.assertEqual(self.r(row("related", "n1", 0.955, 0.45)), F(965, 1000))  # on the grid
        self.assertEqual(self.r(row("related", "n1", 0.93, 0.45)), F(94, 100))
        self.assertEqual(self.r(row("related", "n1", 0.80, 0.45)), F(94, 100))
        self.assertEqual(self.r(row("near-miss", "n1", 0.99, 0.20)), F(94, 100))  # overlap below O
        self.assertEqual(self.r(), F(94, 100))


class Annotate(unittest.TestCase):
    def table(self, second, top):
        rows = [row("related", "n%d" % i, 0.30 + i * 0.01, 0.10) for i in range(21)]
        rows += [row("near-miss", "n21", second, 0.10), row("distinct", "n22", top, 0.10), row("near-copy", "r1", 0.99, 0.60)]
        return rows

    def test_ceiling_on_nonduplicates(self):
        # "The least multiple of 0.005 at which no more than floor(0.05 x n) of the n non-duplicates in Derivation have cosine at or above it; if that value exceeds R, A is R."
        self.assertEqual(dt.derive(self.table(0.72, 0.80), P)["A"][0], F(725, 1000))
        self.assertEqual(dt.derive(self.table(0.72, 0.80), P)["ceiling"], (1, 23))
        self.assertEqual(dt.derive([r for r in self.table(0.72, 0.80) if r["id"] not in ("n0", "n1", "n2", "n3")], P)["A"][0], F(805, 1000))
        capped = dt.derive(self.table(0.98, 0.99), P)
        self.assertEqual((capped["R"][0], capped["A"][0]), (F(94, 100), F(94, 100)))

    def test_a_paraphrase_never_moves_A(self):
        # "It does not try to separate paraphrases from related questions."
        base = self.table(0.72, 0.80)
        a = dt.derive(base + [row("paraphrase", "p%d" % i, 0.95, 0.2) for i in range(4)], P)
        b = dt.derive(base + [row("paraphrase", "p%d" % i, 0.40, 0.2) for i in range(4)], P)
        self.assertEqual((a["O"][0], a["R"][0], a["A"][0]), (b["O"][0], b["R"][0], b["A"][0]))


class Floor(unittest.TestCase):
    def f(self, scores):
        return dt.floor_value([("s%d" % i, dt.exact(s)) for i, s in enumerate(scores)], P)[0]

    def test_percentile_by_nearest_rank(self):
        # "F_keep is the largest multiple of 0.005 not above the 5th percentile (nearest rank) of the relevant scores; F is the larger of 0.2 and F_keep."
        high = [0.60 + i * 0.01 for i in range(19)]
        self.assertEqual(self.f(high + [0.5033, 0.3012]), F(5, 10))   # N=21: rank 2
        self.assertEqual(self.f(high[1:] + [0.4172, 0.9]), F(415, 1000))  # N=20: rank 1
        self.assertEqual(self.f(high[1:] + [0.12, 0.9]), F(2, 10))
        self.assertEqual(self.f(high[1:] + [0.45, 0.9]), F(45, 100))


def check(nd=(), rep=(), relevant=(), noise=(), o=F(3, 10), r=F(94, 100), a=F(85, 100), f=F(3, 10)):
    return dt.decide_check(list(nd) + list(rep), [("v%d" % i, dt.exact(s)) for i, s in enumerate(relevant)], [("q%d" % i, dt.exact(s)) for i, s in enumerate(noise)], o, r, a, f, P)


class CheckConditions(unittest.TestCase):
    def nd(self, annotated, m=10):
        return [row("related", "n%d" % i, 0.86 if i < annotated else 0.30, 0.10) for i in range(m)]

    def test_annotated_at_the_ceiling(self):
        # "at most ceil(0.10 x m) of the m non-duplicates in Check may be annotated": m = 10, exactly 1 passes, 2 fails; m = 11 allows 2.
        self.assertTrue(check(nd=self.nd(1))["annotated_within_ceiling"][0])
        self.assertFalse(check(nd=self.nd(2))["annotated_within_ceiling"][0])
        self.assertTrue(check(nd=self.nd(2, 11))["annotated_within_ceiling"][0])
        self.assertFalse(check(nd=self.nd(3, 11))["annotated_within_ceiling"][0])

    def test_a_refused_nonduplicate_fails(self):
        # "no non-duplicate may be refused"; refusal needs the cosine at least R and the overlap at least O.
        self.assertFalse(check(nd=[row("near-miss", "x", 0.95, 0.60)])["no_nondup_refused"][0])
        self.assertTrue(check(nd=[row("near-miss", "x", 0.95, 0.10)])["no_nondup_refused"][0])

    def test_repeat_recall_at_exactly_080(self):
        # "at least 0.80 of the repeats in Check must be refused or annotated": 4 of 5 passes, 3 of 5 fails.
        rep = lambda n: [row("near-copy", "c%d" % i, 0.99 if i < n else 0.5, 0.9) for i in range(5)]
        self.assertTrue(check(rep=rep(4))["repeats_caught"][0])
        self.assertFalse(check(rep=rep(3))["repeats_caught"][0])

    def test_floor_removal_and_keep_at_the_boundary(self):
        # "removal is at least 0.50 and keep is at least 0.90": 5 of 10 and 9 of 10 pass; 4 of 10 and 8 of 10 fail.
        noise = lambda below: [0.1] * below + [0.4] * (10 - below)
        rel = lambda kept: [0.6] * kept + [0.25] * (10 - kept)
        self.assertTrue(check(noise=noise(5))["floor_removes"][0])
        self.assertFalse(check(noise=noise(4))["floor_removes"][0])
        self.assertTrue(check(relevant=rel(9))["floor_keeps"][0])
        self.assertFalse(check(relevant=rel(8))["floor_keeps"][0])


class CheckNeverChangesANumber(unittest.TestCase):
    def table(self, perturb_check):
        d, c = ids_where(60, True), ids_where(60, False)
        pairs = [row("related", d[i], 0.30 + i * 0.01, 0.10) for i in range(23)] + [row("near-copy", d[23], 0.99, 0.60, 0.7)]
        pairs += [row("paraphrase", d[24 + i], 0.5, 0.1, 0.6 + i * 0.01) for i in range(20)]
        pairs += [row("near-miss", c[i], perturb_check(0.30 + i * 0.01), 0.10) for i in range(20)]
        pairs += [row("paraphrase", c[20 + i], perturb_check(0.5), 0.1, perturb_check(0.6)) for i in range(10)]
        noise = [{"id": d[50 + i], "class": "made-up", "score": dt.exact(0.2)} for i in range(5)]
        noise += [{"id": c[50 + i], "class": "made-up", "score": dt.exact(perturb_check(0.2))} for i in range(5)]
        return pairs, noise

    def test_changing_every_check_item_moves_no_threshold(self):
        # "Thresholds are computed from Derivation only. Check may confirm or fail a result; it may never change one."
        base = dt.run(*self.table(lambda v: v), P)
        moved = dt.run(*self.table(lambda v: 0.99), P)
        key = lambda r: ([r["derive"][k][0] for k in ("O", "R", "A")], r["F"][0])
        self.assertEqual(key(base), key(moved))
        self.assertNotEqual(base["check"]["annotated_within_ceiling"][0], moved["check"]["annotated_within_ceiling"][0])  # the verdict, not the number, moved

    def test_an_extreme_derivation_item_moves_A(self):
        pairs, noise = self.table(lambda v: v)
        top = max((p for p in pairs if p["class"] == "related" and dt.is_derivation(p["id"])), key=lambda p: p["passage"])
        lowered = [dict(p, passage=dt.exact(0.10)) if p is top else p for p in pairs]
        self.assertNotEqual(dt.run(pairs, noise, P)["derive"]["A"][0], dt.run(lowered, noise, P)["derive"]["A"][0])


class Ship(unittest.TestCase):
    def test_ship_needs_the_three_threshold_conditions_not_the_floor(self):
        pairs, noise = CheckNeverChangesANumber().table(lambda v: v)
        res = dt.run(pairs, noise, P)
        c = res["check"]
        self.assertEqual(res["thresholds_ship"], c["no_nondup_refused"][0] and c["annotated_within_ceiling"][0] and c["repeats_caught"][0])


if __name__ == "__main__":
    unittest.main()
