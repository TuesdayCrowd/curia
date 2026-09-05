# Retrieval query set (§8.5, §9.2, R10.5)

Phase 3's exit criterion says *dedupe measured on a real query set*. This directory is the query
set, authored so that the measurement is reproducible rather than a number in a commit message.
`Curia.Application.Tests/Retrieval/RetrievalQuerySetTests` runs it on every build against the
deployed embedder and thresholds and holds the results to the baselines below.

**It is not a vector family** (`index.json` says so, with the reason): `curia-testis` has no
embedding capability and cannot run it, and a family only one implementation enumerates must say
so rather than look like one both run.

## Files

- `dedupe-pairs.jsonl` — pairs of question texts with an authored verdict, `duplicate`,
  `related` or `distinct`, a `class`, and a one-line reason. **No file here carries an expected
  cosine.** A recorded similarity would be derived from an implementation and wrong the moment
  the model changes, which R9.5 says it will; what is measured is whether a `(model, thresholds)`
  reproduces the labels.
- `corpus.jsonl` and `canaries.jsonl` — R10.5's canary queries: a small authored corpus and
  queries whose correct top result is obvious by construction. Expectations are **authored, never
  recorded from the ranker** -- a recorded expectation would report "no drift" for a ranker that
  was wrong from the first run. Over a fixture corpus this is a ranking-drift regression check,
  not a poisoning detector, and says so.
- `refused-baseline.txt` — ids of `duplicate` pairs the deployed model refuses today. A pair that
  stops being refused fails **by name**; a pair newly refused is a finding, not a break (the
  red-team corpus's precedent, where an aggregate floor alone let three detections vanish silently).
- `known-collisions.jsonl` — pairs a human calls `related` or `distinct` that the deployed model
  refuses, each with its reason. A collision that starts being separated fails the build, so the
  list cannot go stale. Empty is the goal and is checked.
- `RESULTS.md` — the numbers, per model and threshold set, with the caveat R10.11 requires.

## The class that keeps the set honest

`paraphrase` pairs are the same question with almost no shared vocabulary. A semantic embedding
must catch them; a lexical geometry cannot. The deployed embedder is `hashed-ngram@1`, feature-
hashed word and character n-grams, and the measurement records that it **misses every paraphrase
pair** -- which is what makes it a lexical channel wearing a vector's name, and why the plan
records an ONNX model as the intended production embedder. A query set on which the hashed model
and a real model score the same is a query set that is not measuring semantics.

## What is measured

Per `(embedding_model, refuse_cosine, refuse_lexical_overlap, annotate_cosine)`:

- **refusal rate** over `duplicate` pairs, by class, with denominators;
- **false-refusal rate** over `related` and `distinct` pairs — the constraining number, for the
  reason the red-team README gives about false positives: a false refusal costs an author their
  question. It is recoverable here (rephrase, or R8.20's override), so the ceiling is low rather
  than zero, and the ceiling is stated: **0 of 7** today;
- **separation**: the minimum cosine over `literal` duplicates against the maximum over
  `distinct` pairs, and whether the ranges overlap. If they overlap, no threshold works, and the
  honest report is that fact rather than a tuned constant;
- **canaries**: each query's expected post must rank in the top 3; its rank is reported.

R8.21: every number is conditional on the model, and the thresholds are provisional values to be
re-derived from this measurement rather than defended.
