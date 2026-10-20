# Canon Refusals Are 400: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-20-canon-refusals-are-400.md`

**Goal:** a VERIFY refusal that says the document cannot be canonicalized answers 400, not 401 (closes **D50 item 2**; errata G24, **R6.58 (rev.)**).

**Scope:** `src/Curia.Api/VerifyRefusals.cs`, tests, docs. No wire change, no frozen format, no client change.

## Rulings (curia-architect, 2026-10-20; the errata amendment is G24's "Amendment, 2026-10-20")

1. `curia/canon/*` and `curia/admit/*` answer 400 by prefix. `PublicJwk.NotRenderableType` is the one named exception, by constant, and is never 400: it never reaches VERIFY (its producer, `PublicJwk.Of`, serves enrollment, the key set and two comparisons), and it is a held key the Forum cannot publish. `curia/content/*` is unchanged.
2. A prefix with a named exception, not an explicit set: an explicit set of two reproduces D50's defect for the next `CanonErrors` entry.
3. `duplicate-normalized-key` is the only one with a wire input (ADMIT compares member names ordinally; NFC collapses them later, at `IngestPipeline.cs:78`). `normalization-failed` and the `curia/admit/*` types are unit-pinned only.
4. `curia/jws/malformed` stays 401 (not decided here). The author mismatch stays 401 (D50 item 3).

## Tasks

0. Baselines as recorded on main (Release): Client 695, Mcp 165, Api 600. Run fresh at the end they were 703, 174 and 606 before this stage's own additions were counted (the recorded figures were stale, not the stage's doing): see the gate counts.
1. Tests first, each seen red for its stated reason, then the implementation:
   - P1 `RefusalStatusTests.R6_58_MemberNamesThatCollideAfterNfcAreABadRequest`, over HTTP, a hand-built submission (member names U+00E9 and U+0065 U+0301, a real signature of another envelope, plain `Canonicalize` so the wire keeps both).
   - P2 `RefusalStatusTests.R6_58_TheSameSubmissionWithoutACollisionFailsAtTheSignature`, the control (401 `curia/jws/signature-invalid`).
   - V4 `VerifyRefusalsTests.R6_58_EveryCanonErrorsRefusalIsBadRequest`, reflection over every `CanonErrors` factory, floor 18.
   - V5 `VerifyRefusalsTests.R6_58_AKeyThatCannotBeRenderedIsNotBadRequest`; and `R6_58_ASignatureFailureStaysUnauthorizedBesideTheCanonArm` (`curia/jws/malformed` stays 401).
   - K3 `RefusalClassificationTests.R6_58_ABadRequestCanonRefusalIsContentAndExitsThree` (no client change; green first, held by FC5).
   - The stale pin at the end of `R6_58_TheOtherVerifyRefusalsKeepTheirStatus` (401 for a canon slug) is removed.
2. Implementation: a second arm in `VerifyRefusals.StatusOf`; the "By prefix" comment.
3. Falsification FC1 to FC5, restored by plain copy and `touch`, rebuilt `--no-incremental`, unpatched run green at the end.
4. Gates and docs (the plan's current-work block, D50 item 2 closed, README, CLAUDE.md, the class summary).

## Probes and their red text (before the fix)

| Probe | Red text |
|---|---|
| P1 | `401: {"type":"curia/canon/duplicate-normalized-key","title":"Two distinct object member names normalize (NFC) to the same string","detail":"café"}` |
| V4 | `InvalidUtf8 (curia/admit/invalid-utf8) answers 401, expected 400` |
| P2, V5, K3 | green on first run, as the report predicted for K3; P2 is the control and V5 the exception, so neither is expected red before the fix |

The probe's name sequence, U+00E9 against U+0065 U+0301, is written as `\u` escapes in the source, and the file was checked for the code points meant (and for no literal non-ASCII in added lines).

## Falsification rows

| Row | Patch | Result |
|---|---|---|
| FC1 | the canon arm's prefix made unmatchable (`CanonTypePrefix + "never/"`) | red: P1 (`401: ... curia/canon/duplicate-normalized-key`) and V4 (`DuplicateNormalizedKey ... answers 401, expected 400`) |
| FC2 | the `NotRenderableType` exception removed | red: V5 alone |
| FC3 | the canon prefix widened to `curia/` | red: five facts: `EveryContentRefusalButTheAuthorMismatchIsBadRequest` (author mismatch 400), `TheAuthorMismatchStaysUnauthorized`, `TheOtherVerifyRefusalsKeepTheirStatus`, `ASignatureFailureStaysUnauthorizedBesideTheCanonArm`, and the P2 control (`400: ... curia/jws/signature-invalid`) |
| FC4 | the admit arm's prefix made unmatchable | red: V4 alone (`InvalidUtf8 ... answers 401, expected 400`) |
| FC5 | `ForumClient` classifies 400 as `Malformed` | red: K3, `R6_58_ABadRequestContentRefusalIsContentAndExitsThree` (K1) and the status Theory's 400 row |

## As run

- **FC1 and FC4** as first written (a literal `"curia/never/"` in place of the prefix) do not compile: CA1823, the prefix constant is then unused. They were run with `Prefix + "never/"` instead.
- FC3's red set differs from the report's list (V2, V3, "the key-refusal rows"): those names are not in this tree; the facts above are the ones that went red.
- The report's claim that P1's construction was untried was borne out: ADMIT accepts the hand-built wire, and VERIFY canonicalizes before the signature, so the real signature of a different envelope is a safe filler. Nothing in the report proved false.

## Gates

Release: Domain 673, Application 442, Client 703, Api 606, Mcp 174, Architecture 58 (Release and Debug), AuthN 140, Canon 329, Canon.Sodium 32, Domain.Primitives 64, Infrastructure 150. Build 0 warnings. `restore --locked-mode` clean. Rust fmt, clippy clean, tests 268 (2 ignored). `curia-differential` 0 divergences. `check-spec` clean; `falsify-spec-checks` red on its own four checks, as designed. `ScreeningCostTests` excluded, as in the standing command.
