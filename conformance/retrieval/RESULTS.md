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
