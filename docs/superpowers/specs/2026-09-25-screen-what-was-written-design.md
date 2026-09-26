# Screen what was written: decoded-token screening (D19) and line-joined rejoining (D17)

**Date:** 2026-09-25. **Status:** implemented by
`docs/superpowers/plans/2026-09-25-screen-what-was-written.md`. **Register:** closes D17, opens and
closes D19 (`IMPLEMENTATION_PLAN.md`). **Decisions taken:** one stage, D19 before D17, one PR; D17's
policy is "D", below. Both chosen by the user on 2026-09-25 after a `curia-architect` review.

## 1. The two defects

### D19 — SCREEN reads JSON escapes, not what the author wrote

Ingest (`src/Curia.Application/Ingest/IngestPipeline.cs:119`) and the client's pre-send check
(`src/Curia.Client/SubmissionBuilder.cs:163`) screen the **canonical envelope text**. JCS writes a
line break as the two characters `\n`, a tab as `\t` and a quote as `\"`
(`src/Curia.Canon/Canonical/CanonicalJson.cs:604-612`). Every rule anchored with `\b`, and the
assignment rule's optional quote, then read the escape's letter or backslash instead of the
separator the author typed. Measured on 2026-09-25 with the real `ContentScreener` over envelopes
built by the project's own canonicalizer:

| Body | Bare (what the corpus measures) | JCS envelope (what ingest screens) |
|---|---|---|
| AWS key on line 2, or after a tab | Rejected | **Accepted** |
| JWT on line 2 | Rejected | **Accepted** |
| `api_key = "…"` — published payload `secret-assigned-entropy` | Rejected | **Accepted** |
| `token = …` on line 2 | Rejected | **Accepted** |
| `password=…` on line 2 | Rejected | **Accepted** |
| `ghp_…` / `sk-proj-…` on line 2 | Rejected | Rejected, only via the unanchored rule |
| PEM header on line 2 | Rejected | Rejected |

A false negative writes a live credential into an append-only log permanently — the direction
`SecretScanner`'s own remarks call unrecoverable. The published 41/41 describes the bare shape,
which only `RaiseFlag` (`src/Curia.Application/Moderation/RaiseFlag.cs:80`) screens in production.

### D17 — the unseparated view refuses ordinary prose

As recorded in the register, and wider than recorded: every `-sk` hyphenated word (*risk-, task-,
ask-, disk-, desk-, mask-, flask-*), an envelope whose `author` contains `ask-`, and prose that
names a vendor prefix ("GitHub tokens start with ghp_ and should be rotated") are all hard-rejected
as `ApiKey`. The unseparated view deletes every separator, so a prefix consumes as much following
prose as its length floor needs; no floor fixes that.

### Why one stage, D19 first

Every D17 candidate first proposed (drop the rule; anchor it in the original; require a fragment)
was measured on bare strings. On the shape ingest screens, each one loses the `ghp_`/`sk-` line-2
catches in the table above, because the unanchored rule is the only thing still catching them.
D17's policy can only be judged on the input SCREEN actually receives, so that input is fixed first.

## 2. Increment 1 — screen decoded tokens (D19)

**Entry points.** `ContentScreener.Screen` is removed and replaced by two methods, so that no call
site keeps its old meaning by accident:

- `ScreenEnvelope(ReadOnlySpan<byte> canonicalEnvelope)` — ingest, the client pre-send check.
- `ScreenText(ReadOnlySpan<byte> utf8Text)` — `RaiseFlag`'s rationale, which is bare text.

Both keep the span parameter: it is what makes SCREEN structurally unable to retain content
(`ContentScreener.cs:43-52`), and a struct such as `CanonicalBytes` would give that up.

**The walker.** `CanonicalStrings.Of(string canonicalText)` (new, `src/Curia.Domain/Screening/`)
yields every **string token** in the canonical text — member names *and* values, recursively through
objects and arrays — each decoded, with an index map from every decoded character back into the
canonical text. Member names are included because an unknown member is ignored rather than
rejected (`src/Curia.Domain/Content/PostEnvelope.cs:151`), so a name is author-chosen, signed and
persisted. The walker decodes exactly the escapes JCS emits (`\" \\ \b \f \n \r \t \u00xx`) and
throws `InvalidOperationException` on anything else: SCREEN receives verified canonical bytes, so a
foreign escape means an upstream phase is broken, the same stance as the UTF-8 decode at
`ContentScreener.cs:81-93`.

**Screening.** `ScreenEnvelope` runs today's per-view detection over each decoded token
independently. Two consequences, both intended: a pattern can no longer run from one member into
the next (the `author` → `board` join), and a line break is a line break again.

**Offsets are unchanged in unit.** Each post event persists `risk_flags` with `offset` and `length`
(`IngestPipeline.cs:220-230`), and rejections report `Category@offset`
(`src/Curia.Application/Ingest/IngestErrors.cs:23-26`). Both stay UTF-16 offsets into the canonical
text: a finding maps view → token (`DerivedView.ToOriginal`) → canonical text (the walker's map).
A span covers every canonical character of the escapes it matched, so a finding ending on a decoded
`\n` covers both characters of the escape.

**Versions.** Both `SecretScanner.Version` and `InjectionDetector.Version` are bumped. R10.10
versions detector rules so that a re-run over the archive is attributable to a rule set
(`SecretScanner.cs:27-31` states the convention as "bump this whenever a pattern changes"); here the
verdict for identical content changes although no pattern does, which defeats attribution for the
same reason a pattern change would.

## 3. Increment 2 — policy D (D17)

- **Removed:** the `unseparated` view, `SecretScanner.Scan`'s `relaxWordBoundaries` parameter and
  `ApiKeyPrefixUnanchored`. No rule runs without its leading anchor any more.
- **Added:** a `line-joined` view. It deletes each run matching
  `[ \t]*[\r\n]+[ \t]*(?:[>│|#+][ \t]*)*` — a line break together with the next line's indentation
  and any quote or border gutter — and is scanned by the shape rules only (not the
  injection detector, for the reason the unseparated view was not: joined lines are not a sentence).
- **The threat model this encodes.** §10.8 is about accidental disclosure (white paper, the paragraph
  before R10.25). Accidents split a credential with a line wrap; a split with words between the
  pieces on one line is deliberate, and a deliberate author has encodings no view undoes. So
  `evade-secret-split` (`conformance/red-team/payloads.jsonl:29`) moves to `known-evasions.jsonl` as
  `adaptive`, with that reason, and leaves `detected-baseline.txt` by a deliberate edit.
- **Measured edge, recorded rather than engineered around.** A key whose line *starts* with the
  prefix, after a line ending in a letter or digit, *and* which is wrapped within its first sixteen
  characters, is joined to a word character and fails `\b`. At any real wrap width the first line
  carries more than sixteen key characters and the identity view catches it. The implementation
  tests this shape; if it evades, it goes to `known-evasions.jsonl` with this reason.

**Amended during implementation.** The line-joined view is read by the shape rules only
(`SecretScanner.ScanShapes`), not by the two assignment-style rules — the high-entropy assignment
and the keyword connection-string password — because their open value classes swallowed the joined
next line and hard-rejected placeholder config that each line alone passes
(`API_KEY=changeme⏎DATABASE_URL_FOR_REPLICA=…`, `PWD=/⏎HOME=/root`). The measurement and the cost
are recorded under D17 in `IMPLEMENTATION_PLAN.md`. The final review amended it three times more,
under `secrets/2026-09-26` and `injection/2026-09-26`. The view first deletes the invisible
characters `HiddenCharacters` names — the set the injection detector annotates, now with U+2060 —
because a renderer leaves a soft hyphen or a zero-width break at a wrap, and it composes that map
with the line-break map so an offset still lands on the key. Its pattern became
`[ \t]*[\r\n\u000B\u000C\u0085\u2028\u2029]+[ \t]*(?:(?:[>│|#+*;]|/{2,}|--)[ \t]*)*`, adding the
Unicode line breaks and the ` * `, `; `, `// ` and `-- ` comment gutters, with a key split into
literals, by a concatenation operator or by a shell continuation recorded as authored rather than
wrapped. And the URI connection-string rule left the view as well, because its open classes read a
`host:port` line end and an @-mention, decorator or `@param` on the next line as `user:pass@`; a
connection string wrapped inside its userinfo is a recorded evasion.

## 4. Corpus changes

- **Every entry is screened in three shapes:** bare (`ScreenText`); as the `body` of an envelope
  built by the production canonicalizer (`ScreenEnvelope`); and the same with `"Context:\n"`
  before it, which is the shape that exposed D19. `RESULTS.md` publishes both rates per shape.
  The detection floor and the false-positive ceiling apply to every shape, and the baseline gate
  names the shape a payload regressed in.
- **The shape is self-checked.** A test asserts that the walker, given each enveloped entry,
  returns the entry's content byte-for-byte as the `body` token — so the enveloped shape cannot
  silently stop carrying the content it claims to measure.
- **`benign.jsonl` gains** D17's two sentences; *ask-, disk-, desk-, mask-, flask-*; a prefix named
  in prose (`ghp_`, `sk-`); and the placeholders an agent writes about keys (`sk-proj-...`,
  `ghp_xxxxxxxx`, `sk-ant-api03-...`).
- **`payloads.jsonl` gains** a mid-token wrapped `ghp_`, an `sk-` wrapped at its hyphen, and a wrapped
  key inside a `> ` quote gutter.
- **New part: `known-false-positives.jsonl`**, the mirror of `known-evasions.jsonl`: each entry
  carries `would_flag` and `why`, is excluded from the false-positive denominator, is listed in
  `RESULTS.md`, and has a test asserting it still fires (a stale entry fails the build, exactly as a
  caught evasion does). It is added to the runner's `CorpusFiles` with its own outcome kind, so
  R10.57's evaluator check reaches it. Its first entry is the measured residual of policy D: a
  prefixed identifier at the end of a hard-wrapped line (`Set npm_token⏎environment-specific …`).
  Without this file, a 0 % published rate would be a statement about a set that excludes a known
  false positive.
- **Structural members** — `author`, `board`, a tag, a member name — are exercised by
  `ContentScreenerTests` over built envelopes rather than corpus entries, since the runner varies
  only the body. An `Application.Tests` ingest test proves the pipeline uses `ScreenEnvelope`: an
  author containing `ask-` is admitted, and an AWS key on line 2 of the body is refused.

## 5. Register and documents

- `IMPLEMENTATION_PLAN.md`: open D19 with the table above; at the end, close D17 and D19 with the
  record of what each falsification printed. Update the "What comes next" paragraph.
- Record, not fix: `SG\.` in the removed unanchored rule could never match (the view deleted `.`);
  the comment claims Stripe coverage but Stripe keys are `sk_live_`, which no rule matches; ANSI
  `ESC[…m` codes before a prefix defeat `\b`; rejection details give a canonical offset where an
  agent could act on a member name and an offset within it; a benign set of about 30 is weak
  evidence for a 0 % rate; enrolment's screening of agent identifiers was not traced.
- The next errata pass's queue gains the measurement-shape sentence the review drafted for Part G:
  published rates are measured in the form each production screening path receives, and a rate
  measured over any other form says so. No entry number is allocated here.
- `tests/Curia.Api.Tests/McpWriteEndToEndTests.cs:55-57`: remove the comment pointing at D17.

## 6. Order of work and falsification

Test first in each increment: the enveloped corpus shapes and the ingest test go red on D19 before
the walker exists; the benign additions go red naming each id before policy D exists.

Each of these must be falsified and go red naming the case, and what it printed is recorded:

1. Route ingest back to `ScreenText` → the ingest test fails.
2. Make the walker leave `\n` escaped → the line-2 payloads fail in the third shape.
3. Skip the token → canonical offset map → an offset test fails.
4. Remove the `line-joined` view → the wrapped payloads regress by name.
5. Restore the unseparated view → the D17 benign ids fail the false-positive ceiling.
6. Make the `known-false-positives.jsonl` entry stop firing → the staleness test fails.
7. Build the enveloped shape without the content → the shape self-check fails.

Then every gate in `CLAUDE.md`'s list, with assemblies counted rather than totals summed.

## 7. Out of scope

A member-path location in rejection details (a wire change); new vendor rules such as Stripe's;
ANSI stripping; a semantic classifier for the known evasions; enrolment-side screening.
