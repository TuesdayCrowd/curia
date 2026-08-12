# Increment 1 — Carried Follow-Ups

**Items found during Increment 1, triaged as not blocking integration. Recorded
here because the working ledger they came from is scratch and does not survive.**

| | |
|---|---|
| **Source** | Whole-branch review of the Canon foundation branch |
| **Date** | 10 August 2026 |
| **Status** | Open |

---

## 1. `CS6_CanonReferencesNoPackage` has residual blind spots

`tests/Curia.Architecture.Tests/LayeringTests.cs` reads `Curia.Canon.csproj`'s XML
directly and asserts it declares no `<PackageReference>`. That closed the original
defect — the check previously used `Assembly.GetReferencedAssemblies()`, which
reflects only assemblies whose types appear in IL, so an *unused* package reference
passed silently.

Two ways in remain, both verified by actually getting `CsCheck` into
`Curia.Canon`'s restored graph while the suite stayed green:

- a `<PackageReference>` declared in an imported `.props` file — `XDocument.Load`
  never resolves `<Import>`
- a `<GlobalPackageReference>` in `Directory.Packages.props` — a different item
  type in a different file

**Dormant today:** the repo declares zero `GlobalPackageReference` items. But
Central Package Management is active, so the mechanism is live.

**Robust fix:** assert against the restored graph (`project.assets.json` or
`packages.lock.json`) rather than csproj text. Deferred because this is build
hygiene, not part of the cryptographic surface R15.1 freezes — it can be
strengthened later without invalidating anything already signed.

**Standing lesson:** breaking *one* architecture test does not establish the
others are falsifiable. CS-9's deliberate-failure check was genuine and its output
matched exactly; CS-6 was simply never attempted, and CS-6 was the broken one.

## 2. Base64url whitespace malleability

BCL base64url decoding silently strips embedded ASCII whitespace, so a signature
segment with an inserted space decodes to identical bytes and verifies.

Not forgeable — the decoded signature bytes are unchanged, so the cryptographic
check is unaffected — and not a regression, since it is present in both the
original `Convert.FromBase64String` approach and the shipped `Base64Url` one.
Nothing in this increment treats the compact JWS string as an identifier.

**Consequence to watch:** `JwsSignature.Compact` is *not* a safe exact-match key.
The moment something uses it for deduplication, idempotency, or audit correlation,
distinct strings that verify identically become a real problem. The XML doc on
`JwsSignature` now warns about this.

**Cross-implementation note:** Rust's `base64` crate with `URL_SAFE_NO_PAD`
**rejects** embedded whitespace. So `curia-testis` will refuse a wire JWS that
`DetachedJws.Verify` accepts. The specification does not say which behavior is
normative. Decide before Plan 2, or the differential harness will surface it as a
divergence and someone will "fix" the wrong side.

## 3. `CheckNumerics` defensive-posture inconsistency

`src/Curia.Canon/Envelope/EnvelopeParser.cs` uses a `_ => null` catch-all when
walking `JsonValue` cases, while `CanonicalJson.NormalizeToNfc` in the same
assembly uses an exhaustive switch with a loud `throw` and a comment explaining
that a future case must fail rather than silently pass through unvalidated.

Low practical risk — `JsonValue` is closed to the assembly (CS-11) — but it is an
inconsistency in a validation path that exists specifically to prevent
signed-content bypass.

## 4. Test-coverage nits

- `RejectsUnicodeNoncharacterInAnObjectKey` pins only `U+FFFE` in key position, not
  the astral or `U+FDD0`-block cases the value-position theory parametrizes over.
  Production behavior is correct (keys and values share `ReadStringValue`).
- P2's generator only mutates a `Number` field on a single-member top-level object,
  never `String`/`Bool`/`Null` or nested fields — narrower than "any single-field
  mutation" implies.
- Astral-plane content appears in ~0.09% of P5's generation space, a consequence of
  sampling UTF-16 code units independently. Not a practical gap, since the
  characters with interesting NFC behavior are overwhelmingly BMP.
- `Embedded_nul_byte_is_rejected` maps to no literal §14.2 bullet; the class doc
  has been softened to say so.

## 5. Evidence-quality standard for probabilistic tests

Worth keeping as a habit rather than a task. A property-suite flake was reported as
"ran 4 times, zero flakiness"; the reviewer computed that at the measured ~26%
per-run failure rate, four clean runs had roughly a 30% chance of occurring by
luck. For anything randomized: **pin a seed that reproduces the failure, and state
N** — "it passed N times" is weak evidence unless N is large relative to the
failure rate it is meant to rule out.

---

*Released under the UNLICENSE and dedicated to the public domain.*
