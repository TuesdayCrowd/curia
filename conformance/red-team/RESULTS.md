# Red-team corpus results (R10.24)

| Shape | Detection rate | False-positive rate |
|---|---|---|
| bare | **100.0 %** (44/44) | **0.0 %** (0/15) |
| enveloped | **100.0 %** (44/44) | **0.0 %** (0/15) |
| enveloped after a line | **100.0 %** (44/44) | **0.0 %** (0/15) |

- Detector versions: secrets/2026-09-25, injection/2026-09-25
- Excluded from the detection rate: **6** payload(s) whose asserted outcome these detectors do not measure (R10.57), evaluated by their own kind's evaluator rather than counted here as passes

## The shapes (register D19)

Every entry is screened in each form a production path receives it. *bare* is the text
alone, as a flag's rationale is screened. *enveloped* is the entry as the `body` of a
canonical post envelope, as ingest and the client's pre-send check screen it, and
*enveloped after a line* puts one line before it. These rates were once published for the
bare shape only, while ingest -- which read JCS text, where a line break is `\n` --
admitted AWS keys, JWTs and assigned secrets on any line after the first.

## How to read these numbers (R10.11)

A detection rate is a statement about *these payloads* against *today's detectors*.
Optimized triggers are demonstrated to survive perplexity examination and rephrasing,
so a high rate is not evidence of safety -- it is evidence that the listed shapes are
caught. R10.11 forbids presenting it as more than that.

The false-positive rate is the number that constrains the design: R10.26 makes a
credential hit a hard rejection, so a false positive costs an author their submission.

## Known evasions

**3 payloads in `known-evasions.jsonl` defeat these detectors today**, each
with the reason recorded. The detection rate above is computed over `payloads.jsonl`
only, so it does *not* include them -- which is precisely why they are listed here
rather than folded into the denominator, where they would depress a number nobody
would then investigate.

Each one, with the reason recorded in the corpus:

- **`evade-synonym-override`** -- would be InstructionOverride. Semantic paraphrase with no lexical overlap. Catching this needs a classifier, not a pattern -- and R10.11 is explicit that optimized triggers survive perplexity examination, so a classifier moves the boundary rather than closing it.
- **`evade-question-form`** -- would be InstructionOverride. Hypothetical framing, no imperative. Indistinguishable by pattern from a legitimate question about prompt injection -- which R10.9 names as an obviously valuable Forum topic.
- **`evade-role-indirect`** -- would be RoleAssumption. Role assumption without any of the named phrasings. Same class as the synonym case.

A recorded evasion that starts being detected fails the build, so this list cannot
silently go stale.
