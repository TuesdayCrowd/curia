**Inputs.** Pairs: the legacy `dedupe-pairs.jsonl` (classes `literal`, `paraphrase`, `related`, `distinct`) and the blind file `blind-pairs.jsonl` (classes `paraphrase`, `near-miss`, `near-copy`); queries: the blind file's `off-topic` and `made-up` records. A **repeat** is a `literal` or `near-copy` pair; a **non-duplicate** is a `related`, `near-miss` or `distinct` pair. Cosine is that of the two texts' vectors in the passage role (what the near-duplicate check compares: R9.63), texts joined as title, newline, body; overlap is Jaccard over the search tokenizer's terms (R8.61). A pair or query is in **Derivation** if the first byte of the SHA-256 of its UTF-8 id is even and in **Check** otherwise. Thresholds are computed from Derivation only. Check may confirm or fail a result; it may never change one.

**Grid.** Cosines are multiples of 0.005, overlaps multiples of 0.05; all published as integers (basis points: R6.33, R9.25).

**Overlap floor O.** The largest multiple of 0.05 not above the least overlap of any repeat in Derivation, clamped to 0.25 at the lowest and 0.50 at the highest.

**Refusing cosine R.** Let M be the greatest cosine of any non-duplicate in Derivation whose overlap is at least O. R is the larger of 0.94 and the least multiple of 0.005 that is at least M + 0.01; if no non-duplicate has overlap at least O, R is 0.94. The rule never lowers R below the published 0.94: a refusal costs an author their question, and the rule may move it only toward fewer.

**Annotating cosine A.** The least multiple of 0.005 at which no more than floor(0.05 × n) of the n non-duplicates in Derivation have cosine at or above it; if that value exceeds R, A is R.

**What the rule does about paraphrase.** It does not try to separate paraphrases from related questions. Whether or not their cosines overlap, A is set by the ceiling on non-duplicates alone, and the share of paraphrases at or above A is published as the paraphrase recall, for Derivation and for Check separately. If the paraphrase recall on Check is under one half, the near-duplicate check is described, everywhere it is described, as a check for repeats that does not detect paraphrase.

**Check.** On Check, with these R, O and A: no non-duplicate may be refused; at most ceil(0.10 × m) of the m non-duplicates in Check may be annotated; and at least 0.80 of the repeats in Check must be refused or annotated. If any of the three fails, no number ships: the failure is reported, nothing is retuned, and the only continuation is a further blind set.

**Vector floor F.** For each paraphrase and near-copy pair in Derivation, the cosine of its `b` text in the query role against its `a` text in the passage role is a relevant score. F_keep is the largest multiple of 0.005 not above the 5th percentile (nearest rank) of the relevant scores; F is the larger of 0.2 and F_keep. The post set for the floor's check is every `a` and `b` text of every blind pair and the ten posts of `corpus.jsonl`, in the passage role; a noise query is embedded in the query role (an off-topic query as its title, newline, body; a made-up query as its `text`), and its score is its greatest cosine to any post. Near-miss pairs are neither relevant nor noise.

**The floor's check.** On Check: the **removal** is the share of off-topic and made-up queries whose score is below F; the **keep** is the share of relevant scores at or above F. The floor *does something* if removal is at least 0.50 and keep is at least 0.90; then it ships. Otherwise F is still published, the Forum says its floor removes little or nothing under this model (R11.93's clause stays, carrying the measured removal), and the cosine floor is recorded as unable to do what it was published to do.

| parameter | value |
|---|---|
| grid_cosine | 0.005 |
| grid_overlap | 0.05 |
| overlap_min | 0.25 |
| overlap_max | 0.50 |
| refuse_cosine_least | 0.94 |
| refuse_margin | 0.01 |
| annotate_false_share_derivation | 0.05 |
| annotate_false_share_check | 0.10 |
| repeat_recall_check_least | 0.80 |
| paraphrase_recall_claim_least | 0.50 |
| floor_least | 0.20 |
| floor_relevant_percentile | 0.05 |
| floor_removal_least | 0.50 |
| floor_keep_least | 0.90 |
