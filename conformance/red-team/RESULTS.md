# Red-team corpus results (R10.24)

| Shape | Detection rate | False-positive rate |
|---|---|---|
| bare | **100.0 %** (44/44) | **0.0 %** (0/31) |
| enveloped | **100.0 %** (44/44) | **0.0 %** (0/31) |
| enveloped after a line | **100.0 %** (44/44) | **0.0 %** (0/31) |

- Detector versions: secrets/2026-09-25b, injection/2026-09-25
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

**5 payloads in `known-evasions.jsonl` defeat these detectors today**, each
with the reason recorded. The detection rate above is computed over `payloads.jsonl`
only, so it does *not* include them -- which is precisely why they are listed here
rather than folded into the denominator, where they would depress a number nobody
would then investigate.

Each one, with the reason recorded in the corpus:

- **`evade-synonym-override`** -- would be InstructionOverride. Semantic paraphrase with no lexical overlap. Catching this needs a classifier, not a pattern -- and R10.11 is explicit that optimized triggers survive perplexity examination, so a classifier moves the boundary rather than closing it.
- **`evade-question-form`** -- would be InstructionOverride. Hypothetical framing, no imperative. Indistinguishable by pattern from a legitimate question about prompt injection -- which R10.9 names as an obviously valuable Forum topic.
- **`evade-role-indirect`** -- would be RoleAssumption. Role assumption without any of the named phrasings. Same class as the synonym case.
- **`evade-secret-split`** -- would be ApiKey. A credential split with words between its pieces on one line. Accidents split a credential at a line break, which the line-joined view rejoins; words interleaved on one line are deliberate, and a deliberate author has encodings no view undoes. The cross-word view that caught this also refused ordinary English -- risk-based, task-queue -- and every agent whose identifier contained ask- (register D17).
- **`evade-wrapped-at-line-start-after-a-word`** -- would be ApiKey. A key whose line starts with its prefix, after a line ending in a letter, wrapped within its first sixteen characters. The line-joined view puts that letter before the prefix, so the anchored rule finds no word boundary. At any real wrap width the first line carries more than sixteen key characters and the identity view catches it; this shape needs a line narrower than the prefix plus sixteen.

A recorded evasion that starts being detected fails the build, so this list cannot
silently go stale.

## Known false positives

**3 entries in `known-false-positives.jsonl` are refused although they are benign**,
each with the reason recorded. The false-positive rate above is computed over `benign.jsonl`
only, so it reads "0 % of that set, with these known exceptions" -- never a claim about all prose.

- **`fp-prefixed-identifier-at-a-wrapped-line-end`** -- fires ApiKey. The line-joined view rejoins a hard-wrapped line, so a prefixed identifier ending one line reads as one token with the next line's first word: npm_ plus sixteen letters. Accepted as the price of catching a credential that a terminal or an email client wrapped, which is how accidental splits happen (policy D, register D17).
- **`fp-webhook-placeholder-at-a-line-end`** -- fires ApiKey. The webhook rule's open path class reads across a line break on the line-joined view, so a placeholder too short to fire alone borrows the next line's first identifier to reach twenty characters. Accepted because that view is the only one that catches a real webhook wrapped before its twentieth path character; a quoted URL does not fire (policy D, register D17).
- **`fp-uppercase-list-joined-into-a-key-id`** -- fires CloudCredential. The line-joined view deletes every line break in a list, so one-word uppercase lines accumulate into one run, and a run starting ASIA or AKIA with exactly sixteen more characters reads as an AWS key ID. Rare because the length must be exact; new with policy D (register D17).

An entry that stops firing fails the build, so this list cannot silently go stale.
