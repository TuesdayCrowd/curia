# Retrieval query set — results (the hashed test adapter, then the deployed model)

Reproduced by `Curia.Application.Tests/Retrieval/RetrievalQuerySetTests` on every build; the
per-pair report is what `TheMeasurementIsPrinted_ForTheTestAdapter` computes. **The table below is the test adapter's** (`HashedNGramEmbedder`, which the Forum no longer runs: errata G65): it
measures what that adapter does and nothing the deployed model does, and the deployed model's numbers are D10b's (the thirteenth fixture's and these sixteen pairs' raw cosines under
`bge-small-en-v1.5@1` are recorded in the PR that changed the model). Numbers below are for:

| embedding model | refuse cosine | refuse lexical overlap | annotate cosine |
|---|---|---|---|
| `hashed-ngram@1` (feature-hashed word unigrams and character trigrams, 256-d, L2-normalized) | 0.94 (R8.18) | 0.50 (provisional, G10) | 0.85 (provisional, G10) |

## Dedupe (R8.18, R8.21)

| class | pairs | refused | annotated | rate |
|---|---|---|---|---|
| `literal` duplicates | 5 | 5 | 0 | 5/5 refused |
| `paraphrase` duplicates | 4 | 0 | 0 | **0/4 refused** |
| `related` (must not refuse) | 4 | 0 | 0 | 0/4 false refusals |
| `distinct` (must not refuse) | 3 | 0 | 0 | 0/3 false refusals |

- **False-refusal rate: 0 of 7.** The ceiling the README states.
- **Separation:** minimum cosine over `literal` duplicates **0.949**; maximum over `distinct`
  **0.180**; maximum over `related` **0.477**. The ranges do not overlap, so a threshold exists
  for the literal class, and R8.18's 0.94 sits inside the gap with room on both sides.
- **The paraphrase block:** cosines **0.287–0.390**, every one below even the annotation
  threshold. This is the honest reading of the deployed model (R10.11): `hashed-ngram@1` catches
  the case §8.5 opens with -- the same error pasted by many agents -- and does not catch the same
  question asked in other words. A semantic model is the intended production embedder (plan D10);
  when one is configured, `R9_5_TheParaphraseBlockRecordsWhatTheTestAdapterCannotDo_ForTheTestAdapter` is the test
  that flips, and this table with it.
- **Known collisions:** none.

Per pair (cosine, Jaccard overlap over the search tokenizer's terms, verdict):

```
literal-identical          literal    duplicate cosine 1.000 overlap 1.000 -> Refuse
literal-reordered          literal    duplicate cosine 1.000 overlap 1.000 -> Refuse
literal-appended           literal    duplicate cosine 0.949 overlap 0.722 -> Refuse
literal-cased              literal    duplicate cosine 1.000 overlap 1.000 -> Refuse
literal-one-word           literal    duplicate cosine 0.955 overlap 0.867 -> Refuse
paraphrase-reset           paraphrase duplicate cosine 0.390 overlap 0.154 -> None
paraphrase-rotate          paraphrase duplicate cosine 0.287 overlap 0.107 -> None
paraphrase-jcs             paraphrase duplicate cosine 0.296 overlap 0.111 -> None
paraphrase-dpop            paraphrase duplicate cosine 0.328 overlap 0.100 -> None
related-pool-size          related    related   cosine 0.477 overlap 0.190 -> None
related-key-publication    related    related   cosine 0.422 overlap 0.174 -> None
related-jcs-escaping       related    related   cosine 0.295 overlap 0.120 -> None
related-nonce-lifetime     related    related   cosine 0.228 overlap 0.103 -> None
distinct-board             distinct   distinct  cosine 0.145 overlap 0.000 -> None
distinct-flag              distinct   distinct  cosine 0.019 overlap 0.000 -> None
distinct-budget            distinct   distinct  cosine 0.180 overlap 0.069 -> None
```

## Canaries (R10.5)

Six authored queries over the ten-post fixture corpus; every expected post ranks in the top
three under hybrid retrieval with the model above. Over a fixture corpus this is a ranking-drift
regression check, not a poisoning detector, and `R10_5_EveryCanarysExpectedPostRanksInTheTopThree_ForTheTestAdapter`
fails by name when a canary drifts.

## The vector channel's floor (R9.22)

`min_cosine_bp` is 2000 -- a vector neighbour below cosine 0.2 is not a candidate. Measured on
2026-09-05 with `hashed-ngram@1`, to place the floor rather than guess it, and **re-measured on
2026-09-07**, which corrected one row and withdrew the conclusion the table was drawn to support:

| what | cosine |
|---|---|
| each canary query against its expected post, minimum over the six | **0.318** (`canary-jcs`; the others 0.530–0.809, a range that read 0.530–0.816 before the re-measurement below and is not what the floor turns on) |
| a 32-hex-digit query against bodies carrying 32-hex-digit nonces, maximum over 20,000 draws | **0.262**, crossing 0.2 in 0.29 % |
| a letters-only nonsense term against these bodies, maximum over 20,000 draws | **0.354**, crossing 0.2 in **5.97 %** |

**The last row was corrected on 2026-09-07 and previously read `0.149`, maximum over 300 draws.**
That number is not reproducible: re-measured against this corpus with `hashed-ngram@1`, a letters-only
term of any length from 5 to 35 characters, whether one token or three words, clears the floor
between 5.6 % and 10.4 % of the time and reaches 0.33–0.40. The instrument was validated on the rows
either side of it before the row was called wrong — `canary-jcs` re-measures to **0.3182** against a
published 0.318, and the hex row to **0.2620** against a published 0.262, which also identifies what
the hex row depends on: document richness. Against short `"About <nonce>"` bodies the same hex query
reaches 0.308 and crosses the floor 20 % of the time, so that row is a statement about bodies of
roughly 100 characters, which is what this corpus holds.

**What the correction costs is the floor's stated justification.** The claim this section used to
make — that the floor sits between the noise and the weakest canary with a margin on each side — is
false as stated. It holds for the hex case (0.262 < 0.318) and fails for the letters-only case, where
noise reaches **0.354, above `canary-jcs`'s 0.318**. There is no cosine that admits the weakest
canary and excludes nonsense, so no choice of this constant separates them; the floor is doing less
than the sentence claimed. The hex case remains real — an agent will paste a digest or a commit hash
as a query, and it is the case a lexical geometry fuzzily matches by construction (§9.2's "trigram
for fuzzy identifiers") — and 0.2 still keeps that from returning a page of unrelated hashes.
Recorded as plan defect **D14**. Re-derive this table when the model changes; **nothing re-derives it
automatically**, which is how the wrong row survived.

## What these numbers are not (R10.11)

A statement about **these pairs against today's model and thresholds**. A high literal refusal
rate is evidence that the listed shapes are caught, not that duplicates are; the paraphrase row
is the evidence that they are not. The thresholds are provisional and are to be re-derived from
this set when the model changes (R8.21), not defended.

## The deployed model, `bge-small-en-v1.5@1` (D10b)

Measured under each model by the rule in `THRESHOLD-RULE.md`; the block below is printed by `tools/retrieval/derive-thresholds.py --emit-section` over `measured/bge-small-en-v1.5@1.measurements.jsonl` and pasted unedited.

<!-- bge-small-en-v1.5@1:begin -->

### Measured under `bge-small-en-v1.5@1`

- model: bge-small-en-v1.5@1
- rule_commit: 7f1b3081
- rule_sha256: 7afca984948be0415fe8e014ccad04aee3e90968c4ae3d8450e1eca06e97c0c5
- blind_pairs_sha256: 367a0023429cab749b5c5b78adf5339bde1508f6fc29ba60dac57918e73f69b9
- dedupe_pairs_sha256: b844a4d182304fc0a0b8fe1dc66065aa2bba1e4da349f34bb745e4c5601b05fa
- ndt4_agreement: the runner and tools/model/pair-cosines.py agree within 2.3e-08 over 236 values; python 3.12.9 here; the manifest records 3.12.16
- split_nondup_derivation_check: 23/20
- split_repeat_derivation_check: 9/14
- split_paraphrase_derivation_check: 24/16
- split_noise_derivation_check: 8/16
- thresholds_ship: false
- floor_bp: 6400
- check_no_nondup_refused: pass (0 of 20 refused)
- check_annotated_within_ceiling: FAIL (3 annotated, at most 2 of 20)
- check_repeats_caught: pass (14 of 14)
- floor_does_something: true
- floor_removal_bp: 10000 (16 of 16 noise queries in Check below F)
- floor_keep_bp: 10000 (27 of 27 relevant scores in Check at or above F)
- paraphrase_recall_derivation: 1/24
- paraphrase_recall_check: 0/16
- repeat_recall_derivation: 9/9
- repeat_recall_check: 14/14

The Check failed (annotated_within_ceiling): no number ships. The rule's only continuation is a further blind set; nothing was retuned. The derived thresholds are recorded outside this file, labelled not published.

The paraphrase recall on Check is under one half: the near-duplicate check is a check for repeats that does not detect paraphrase.

The Derivation ceiling floor(0.05 x 23) = 1 is 1 non-duplicate's cosine: the numbers rest on few pairs.

These numbers are this model's (`bge-small-en-v1.5@1`), on this corpus of about 190 posts, and are derived again at the next model id.

<!-- bge-small-en-v1.5@1:end -->

## The keyword channel ignores common words (item 342, errata G81)

The rule is `STOPWORD-RULE.md`; the material is `blind-queries.jsonl` (written blind, never edited); the reference is `measured/lexical-stopwords.reference.jsonl` (Python, byte-identical across runs; the C# runner agrees with it on every integer). Nothing was retuned. The block between the markers is what the runner computes; the MRR fall is its exact fraction, unreduced (its reduced value is -747575202119727174209/6914097838342447552800, a gain of about 0.108 in the mean reciprocal rank).

Parameters of the rule, as applied: list_size 111, removal_page_share_most 0.25, removal_share_least 0.80, removal_improvement_least 0.30, keep_window 25, keep_base_least 30, keep_on_topic_least 0.95, keep_mrr_fall_most 0.02, hazard_lost_share_most 0.20. Corpus: 270 posts.

<!-- lexical-stopwords:begin -->
Removal: 30 of 30 off-topic queries at most a quarter of the corpus with the list, 10 without; ok True.
Keep, on-topic: real 110 of 120, stopword-only 6, all-ignored 0; base 84, kept 84, MRR fall -3567534720137488048770290499397013602555473240421186599422227572399707576353940335147622495944704000000000000000000000000/32995053911330555541242256856029854121719215365239577496406961666156537360143274026097387031756800000000000000000000000000.
Keep, hazard: m 40 of 40, lost 1 (allowed 8), all-ignored 0, with a term of their own 1, stopword-only 1.
Listings excluded: 0.
Verdict: Ships.
<!-- lexical-stopwords:end -->

Each condition, value against threshold: Removal share with the list 30/30 = 1.00 (at least 0.80) and its improvement over without the list 10/30, so +0.667 (at least 0.30); Keep base 84 (at least 30); Keep kept 84 of 84 = 1.00 (at least 0.95); MRR fall about -0.108 (at most 0.02, a gain); hazard lost 1 of m 40 (allowed 8). All hold, so by the rule's outcome the list ships.

Report only, gating nothing. The 30 `ko` off-topic queries list a median of 2.5 posts (0 to 23, so up to 8.5 percent of the corpus) with the list and 136.5 (maximum 234, a median page share of 50.6 percent) without it. The twelve `qo` off-topic posts of `blind-pairs.jsonl`, each taken as title, a line feed and body, list 1 to 60 posts with the list (median 16) and 128 to 252 without it. The vague queries all-ignored: 0 of 20 (no query of the set is all-ignored; no query is a listing). Target rank movement over the 110 real finds: 44 rank higher with the list, 63 unchanged, 3 lower; the largest rise is 130 places and the largest fall 12; none left the 25-result window. Six targets are `stopword-only` (listed only through a listed word), counted and outside the Keep.

The one lost hazard query (`kh-03`, sentence form) had a term of its own, so it is a vocabulary mismatch the list exposed and not an all-ignored query; the listed words it shared with its target were `and`, `in` and `a` (words only).
