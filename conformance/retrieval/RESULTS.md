# Retrieval query set — results (the hashed test adapter, then the deployed model)

Reproduced by `Curia.Application.Tests/Retrieval/RetrievalQuerySetTests` on every build; the
per-pair report is what `TheMeasurementIsPrinted` computes. **The table below is the test adapter's** (`HashedNGramEmbedder`, which the Forum no longer runs: errata G65): it
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
  when one is configured, `R9_5_TheParaphraseBlockRecordsWhatTheDeployedModelCannotDo` is the test
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
regression check, not a poisoning detector, and `R10_5_EveryCanarysExpectedPostRanksInTheTopThree`
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
