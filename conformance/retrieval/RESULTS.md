# Retrieval query set — results

Reproduced by `Curia.Application.Tests/Retrieval/RetrievalQuerySetTests` on every build; the
per-pair report is what `TheMeasurementIsPrinted` computes. Numbers below are for:

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

## What these numbers are not (R10.11)

A statement about **these pairs against today's model and thresholds**. A high literal refusal
rate is evidence that the listed shapes are caught, not that duplicates are; the paraphrase row
is the evidence that they are not. The thresholds are provisional and are to be re-derived from
this set when the model changes (R8.21), not defended.
