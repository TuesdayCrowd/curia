**Stopword rule: the keyword channel ignores a published list of common English words in a query.** Committed before the material it is applied to exists. Amended once, 2026-11-25, before the brief was dispatched (plan Amendment 1, from Task 0): the Keep's baseline and its two parameters.

**Inputs.** The **list** below, fixed here before any query exists: lower-case ASCII, one word once, equal to `stopwords.txt` (sorted, one word per line, no trailing blank line). The **material**: `conformance/retrieval/blind-queries.jsonl`, written by a fresh blind agent from `QUERY-BRIEF.md`, never edited by anyone, validated by a script that prints PASS or FAIL and nothing else. The **corpus**, in this order: the ten posts of `corpus.jsonl` in file order (an answer has an empty title; tags as that file gives them), then both sides of every pair of `blind-pairs.jsonl` that has sides, in file order, `a` then `b` (title and body; no tags), then the `post` object of each of the material's 80 `target` and `hazard` records in id order (`kt-01` to `kt-60`, then `kh-01` to `kh-20`; no tags). A post's text is its title, a line feed, its body; a post whose text equals an earlier one's is not added again and keeps the earlier one's position. The order is the post's **sequence**, and ties break by it. The 16 pairs of `dedupe-pairs.jsonl` are not in the corpus: the live run posts this corpus and no other. A **term** is a token of `LexicalSearch.Tokenize` (maximal runs of letters and digits, lower-cased); **with the list** a query's terms are its tokens not on the list, and **without the list** all of them. A query is **a listing** if it has no token; it is **all-ignored** if it has a token and every token is on the list, and it then lists nothing. A post is **listed** by a query when `LexicalSearch.Rank` over the corpus, with no filter and **no limit** (an uncapped count, not a page), gives it a score above 0: five for each occurrence of a term in the title, three in a tag, one in the body. The **page share** of a query is the number listed over the corpus size. The **rank** of a post is its 1-based position in the order score descending, ties by sequence ascending. No query of the material is a listing; if one is, it is reported and excluded from every measure.

**Nothing is fitted.** The list is a prior and **the whole material is Check**: no word is added, removed or reordered because of what a query did. The measurement may ship the list or not; it may not change it.

**Removal.** Over the 30 `off-topic` queries: the share whose page share is at most `removal_page_share_most` **with** the list must be at least `removal_share_least`, and must exceed the same share **without** the list by at least `removal_improvement_least`. (The `vague` queries are reported and do not gate: a query made only of listed words has page share 0 whatever the list, so counting it would make the condition true of the list's own words.)

**Keep, on-topic.** Over the 120 `terse` and `sentence` queries of the 60 `target` posts. A target is a **real find** of its query if the query lists it **with** the list, that is if they share at least one term the list left. A target listed only **without** the list shares only listed words with its query, which is exactly what the list removes: it is not a find, is counted and published as `stopword-only`, and does not enter the Keep. (If it entered, the Keep would measure how far the material's author reused the post's vocabulary, which the brief tells the author not to do, and not the list. And a Keep counted over real finds by being listed would be satisfied by construction, since a real find is listed with the list; so the Keep looks at where the real find stands, not whether it is listed.) Let *real* be the queries whose target is a real find; *base* the queries of *real* whose target's rank **without** the list is at most `keep_window`; *kept* the queries of *base* whose target's rank **with** the list is at most `keep_window`. Required, all three: *base* is at least `keep_base_least` (below it the Keep is **undecided**, which is a failure); *kept* is at least `keep_on_topic_least` of *base*; and the mean over *real* of the reciprocal rank of the target (the full rank, no window) does not fall, from without the list to with it, by more than `keep_mrr_fall_most`. Published beside it, and gating nothing: the number of `stopword-only` queries, the number of queries all-ignored, and *reach* (the share of the 120 that are real finds).

**Keep, hazard.** Over the 40 queries of the 20 `hazard` posts (a post whose subject is a word of a language or a command that is also an everyday word): let *m* be those whose target is listed **without** the list; a query is **lost** if its target is listed without the list and not with it, **an all-ignored query counting as lost**. At most `ceil(hazard_lost_share_most x m)` may be lost. Here a listed word can be the post's subject, so a match through a listed word is not assumed accidental. Published beside it: *m*, the number lost, how many lost were all-ignored, how many lost had a term of their own (a vocabulary mismatch the list exposed; they are counted lost all the same, and the cost of that is a failure that does not ship the list, the safe direction), and how many of *m* were `stopword-only`.

**What decides.** If Removal, both Keeps hold, the list ships: it enters the code, the gloss and `ModelWording` are restated, and the measurement is published. **If any fails, or the on-topic Keep is undecided, no list ships**: the failure is reported with the numbers, nothing is retuned, item 342 closes as a finding (the long pages come from content words common on the board, or the list costs findability the rule does not allow), and the only continuation is a further blind set or a differently reasoned list **under a new rule**, never an edit to this one.

**Two artifacts.** Every number above is computed twice by artifacts that share nothing but the committed files: `LexicalSearch.Rank` and `SearchTerms` in C#, and `tools/retrieval/lexical-measure.py`, a Python re-implementation of the tokenizer, the count-and-weight ranking and the list read from this file. They must agree on **every integer** (terms, ignored words, hit sets, scores, ranks); there is no tolerance because there is no float. The C# test runs the Python through a process and compares (plan KWC2, KWC14). **Report only, never gating:** the page share of the twelve `qo` off-topic queries of `blind-pairs.jsonl`, the vague queries' count of all-ignored, and, in the live run, the recall at 10 of the 120 targets under the real model on `c` and `t` (a fall is reported with the targets named by id and read by the owner).

| parameter | value |
|---|---|
| list_size | 111 |
| removal_page_share_most | 0.25 |
| removal_share_least | 0.80 |
| removal_improvement_least | 0.30 |
| keep_window | 25 |
| keep_base_least | 30 |
| keep_on_topic_least | 0.95 |
| keep_mrr_fall_most | 0.02 |
| hazard_lost_share_most | 0.20 |

`keep_window` is `LexicalSearch.DefaultLimit`, the page the channel serves when no limit is named. **The values were sized by Task 0 on material that is not the blind set** (PR 8's twenty queries and D10b's pairs, whose page lengths are already published) and are final when this file is committed; a change after the brief is dispatched is an amendment recorded before any blind text is read.

**The list (111).** One word per line in `stopwords.txt`, sorted; grouped here for reading only.
- determiners (7): `a an the this that these those`
- pronouns (18): `i me my we us our you your he him his she her it its they them their`
- be, have, do (14): `am is are was were be been being do does did have has had`
- modals (10): `can cannot could will would should shall may might must`
- prepositions (12): `of to in on at by for with from into about as`
- conjunctions and negation (10): `and or but if so than then because not no`
- wh-words (9): `how what why when where which who whom whose`
- deictic and adverbs (7): `there here also just very too such`
- courtesy (6): `hi hello hey thanks thank please`
- contraction tails (18): `s t ll ve don doesn didn isn aren wasn weren hasn haven hadn won wouldn couldn shouldn`

The sorted block's SHA-256, over its words joined by one line feed with no trailing one, is `738e8e0ef8c1077fcf81eda461be245fd9c7e777ca2da5f5ed8a05dcceaafaa6`. Task 0 recomputed it over the list loaded from the plan's grouped block and found it equal; Task 1 recomputes it over the committed file.
