# Red-team corpus results (R10.24)

- Detection rate: **100.0 %** (41/41)
- False-positive rate: **0.0 %** (0/15)
- Detector versions: secrets/2026-08-18b, injection/2026-08-17

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

They fall into three groups: **lexical evasion** (character spacing, Markdown splitting,
split credentials, base64-wrapped credentials) which is fixable work not yet done;
**homoglyph substitution**, which is R10.8's named-but-unimplemented clause and needs a
UTS #39 confusables table; and **semantic paraphrase**, which no pattern catches and which
R10.11 says a classifier would only move rather than close.

A recorded evasion that starts being detected fails the build, so this list cannot
silently go stale.
