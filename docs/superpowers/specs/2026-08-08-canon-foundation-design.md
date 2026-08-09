# Cūria Increment 1 — The Frozen Core

**Design specification for the first implementation increment: `Curia.Canon`,
`Curia.Canon.Sodium`, `Curia.Domain.Primitives`, the `curia-testis` independent
verifier, and the conformance vector set they share.**

| | |
|---|---|
| **Document** | Implementation design specification |
| **Increment** | 1 of Phase 1 |
| **Applies to** | White paper v1.0 + Errata v1.1-draft + C# scoping v0.1-draft |
| **Version** | 1.0 |
| **Date** | 8 August 2026 |
| **Organization** | TuesdayCrowd |
| **License** | UNLICENSE |

---

## 1. Why this increment exists, and why it comes first

R15.1 fixes three things at the Phase 1 tag that can never be recomputed later:
the envelope schema version, the canonicalization rules, and the leaf-digest
computation. Everything else in the system is derivable, reindexable, or
rebuildable from the event log. These are not.

This increment builds exactly those things and nothing else. It ships no
database, no HTTP surface, no issuer, and no transparency log — not because
those are unimportant, but because a mistake in any of them is repairable and a
mistake here is permanent.

The exit condition is the one Table 22 states for Phase 1's signed core: **an
independently written verifier confirms authorship offline.** That verifier is
`curia-testis`, written in Rust, closing open decision D1 along the third path
§16 names — C# Forum, Rust verifier, the language question converted from a fork
into an asset.

## 2. Scope

### 2.1 In scope

| Component | Contents |
|---|---|
| Solution scaffolding | `Curia.sln`, `Directory.Build.props` (CS-2), `Directory.Packages.props` (CS-3), `global.json` SDK pin, `nuget.config`, `.editorconfig`, committed lock files |
| `Curia.Canon` | ADMIT parser, JCS canonicalization, NFC, SHA-256 digest, detached JWS, the two signer/verifier interfaces, `CanonicalBytes`, `VerifiedContent` |
| `Curia.Canon.Sodium` | Ed25519 via NSec, ES256 via BCL `ECDsa` — the only assembly referencing native crypto |
| `Curia.Domain.Primitives` | `Result<T>`, typed `Error` carrying RFC 9457 slugs, strongly typed identifiers |
| `curia-testis` | Rust verify-only crate + offline CLI, Ed25519 and ES256 |
| `conformance/` | RFC 8785 official vectors (vendored, Apache-2.0), Cūria C.4 vectors, envelope/JWS profile vectors, ADMIT rejection corpus |
| Tests | Canon unit + vector suites, CsCheck properties P1–P5, §14.2 negative suite, `NetArchTest` architecture rules, differential fuzz harness |

### 2.2 Out of scope, explicitly

Event store and PostgreSQL; the HTTP API; `Curia.Issuer` and OpenIddict; the
gateway; the MCP adapter; embeddings and retrieval; scoring, `n_eff`, and
seeded trust; the Merkle transparency log; moderation; vote envelopes and vote
storage; the sandbox runner; the typed `Envelope` closed hierarchy (CS-11).

Two exclusions deserve their reasoning stated, because a later reader will
wonder:

**The typed envelope hierarchy is deferred to the Domain increment.** Canon
operates on `EnvelopeDocument` — a parsed, structurally admitted JSON document —
not on typed post kinds. This keeps the frozen surface from naming every kind,
so adding `vote` per errata C1 does not force a Canon major version. Structural
admission (JSON shape, I-JSON numerics, caps, duplicate keys) is Canon's;
schema conformance (which fields are required for which `kind`) is the Domain's,
still inside pipeline phase ① from the pipeline's point of view.

**The leaf digest is documented but not implemented.** R15.1 requires Phase 1
to fix the leaf-digest computation, and §6.6 defines it as
`SHA-256(leaf_prefix ‖ canonical_envelope ‖ signature)`. Increment 1 records that
formula in this specification and in `conformance/`, but ships no log. Canon's
public digest API covers only the **envelope digest** — `SHA-256(canonical
bytes)`, the value `prev`, `refs`, and dedup use. Conflating the two would be an
error; they are different values with different preimages.

## 3. Amendments to the C# scoping document

Three corrections. Each is a deviation from `curia-csharp-scoping.md` v0.1-draft
and should be folded into its next revision.

### 3.1 `CanonicalBytes` — making R6.10 a compile error

Scoping §4 freezes this surface:

```csharp
public static Result<byte[]> Canonicalize(EnvelopeDocument doc);
public Result<VerifiedContent> Verify(ReadOnlySpan<byte> canonical, JwsSignature sig, PublicKeyMaterial key);
```

R6.10 requires verification against the independently re-canonicalized form,
never against wire octets, and §6.3's pitfall explains why: verifying raw bytes
permits a payload that canonicalizes to something other than what the server
stores and serves. But `ReadOnlySpan<byte>` carries no provenance — raw wire
bytes type-check as `Verify`'s first argument. As written, R6.10 rests on caller
discipline, which is exactly the kind of guarantee §6.4 warns fails silently.

**Amendment.** `Canonicalize` returns an opaque `CanonicalBytes` that only
`Canonicalize` can mint, and `DetachedJws.Sign`/`Verify` require it:

```csharp
public readonly struct CanonicalBytes          // no public constructor
{
    internal CanonicalBytes(byte[] bytes);
    public ReadOnlySpan<byte> Span { get; }
}

public static Result<CanonicalBytes> Canonicalize(EnvelopeDocument doc);
public Result<JwsSignature>   Sign(CanonicalBytes canonical, SigningKey key);
public Result<VerifiedContent> Verify(CanonicalBytes canonical, JwsSignature sig, PublicKeyMaterial key);
```

"Verify the bytes that arrived on the wire" now has no spelling. This is the
last cheap moment to make the change: CS-14 makes Canon's API append-only after
the Phase 1 tag.

### 3.2 `EnvelopeParser.Parse` returns the whole submission

Appendix C.3's wire format is `{"envelope": {...}, "signature": "..."}`, but the
frozen `Parse` returns only `EnvelopeDocument` while `DetachedJws` needs
`JwsSignature` as a separate argument, and nothing in the scoping document
bridges the two.

**Amendment.** `Parse` admits the whole submission:

```csharp
public sealed record SubmissionDocument(EnvelopeDocument Envelope, JwsSignature Signature);
public static Result<SubmissionDocument> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits);
```

The alternative — carrying the signature inside `EnvelopeDocument` — was
rejected because it muddies `CanonicalBytes`' guarantee that canonicalization
covers the envelope and only the envelope.

### 3.3 Package manifest corrections

| Scoping §9 entry | Correction |
|---|---|
| `Org.Webpki.JsonCanonicalizer` | That is the **namespace**. The NuGet ID is `jsoncanonicalizer` (1.0.0). Its metadata is unattributed — description "Package Description", no project URL — which is not adequate provenance for a correctness oracle (CS-4's own argument). **Use the upstream Apache-2.0 source and test data instead** (§7.1). |
| `xunit.v3` | Newest is `4.0.0-pre.154`, a prerelease. Pin **3.2.2**, the newest stable, per CS-3's locked-mode discipline. |

Verified available: `NSec.Cryptography` 26.4.0, `CsCheck` 4.8.0,
`NetArchTest.Rules` 1.3.2, `Ulid` 1.4.1, `jose-jwt` 5.3.0,
`Microsoft.Extensions.TimeProvider.Testing` 10.8.0.

## 4. The frozen API surface

```csharp
namespace Curia.Canon;

public static class EnvelopeParser                      // §6.4 phase ① ADMIT
{
    public static Result<SubmissionDocument> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits);
}

public static class CanonicalJson                       // RFC 8785 + R6.9 + R6.34
{
    public const string UnicodeVersion = "16.0";        // R6.34, §5.2 below
    public static Result<CanonicalBytes> Canonicalize(EnvelopeDocument doc);
}

public static class Digests
{
    public static EnvelopeDigest Sha256(CanonicalBytes canonical);
}

public sealed class DetachedJws                         // RFC 7515 App. F + RFC 7797
{
    public DetachedJws(IReadOnlyDictionary<string, IContentSigner> signersByAlg,
                       IReadOnlyDictionary<string, IContentVerifier> verifiersByAlg);
    public Result<JwsSignature>    Sign(CanonicalBytes canonical, SigningKey key);
    public Result<VerifiedContent> Verify(CanonicalBytes canonical, JwsSignature sig, PublicKeyMaterial key);
}

public interface IContentSigner   { byte[] Sign(ReadOnlySpan<byte> input, SigningKey key); }
public interface IContentVerifier { bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key); }
```

`DetachedJws` takes **maps from `alg` to adapter** rather than a single pair.
Verification reads the protected header's `alg`, looks it up in an explicit
allow-list (`EdDSA`, `ES256`), and hard-rejects anything absent — including
`none` and every `HS*`, which R4.15 forbids outright. Algorithm confusion is
prevented structurally rather than by the caller remembering to check.

`SigningKey` carries its own algorithm, so `Sign` selects its signer from the
same allow-list rather than taking one as an argument. A key whose algorithm has
no registered signer fails as a typed `Result`, not an exception — an unsupported
algorithm is a value the caller must handle, not a bug.

Protected header, per Appendix C.3 and RFC 7797:

```json
{ "alg": "EdDSA", "kid": "agent-key-2026-08",
  "typ": "curia-post+jws", "b64": false, "crit": ["b64"] }
```

`crit: ["b64"]` is enforced in both directions: a verifier that does not
understand `b64` must reject, and a `crit` list containing anything other than
`b64` must reject.

**Canon holds no clock and no key store.** Errata R6.31 (key validity evaluated
at `server_ts`) and R6.32 (reject only future-dated `created_at`) are Application-
layer predicates requiring a registry and a `TimeProvider` that Increment 1 does
not build. Canon must therefore contain no time-window logic and no
validity-at-instant logic, so that a later increment cannot find the wrong policy
already baked into a frozen library. R11.3's Clock port and CS-9's banned-API
analyzer are established now so the rule holds from the start.

## 5. Decisions this increment records permanently

R15.1 freezes these. They are stated here because the source documents leave
them open, and a value chosen silently is a value nobody reviewed.

### 5.1 ADMIT limits

No numeric caps appear anywhere in the three documents, though R6.15 requires
oversize and excessive nesting to be rejected before canonicalization.

| Limit | Value | Reasoning |
|---|---|---|
| Max submission size | 1 MiB (1 048 576 bytes) | Comfortably holds a long finding with code blocks; small enough that canonicalizing adversarial input is bounded work |
| Max nesting depth | 32 | Table 9's deepest legitimate structure is ~4; 32 leaves room without permitting stack-exhaustion shapes |
| Max object members per level | 1 024 | Bounds the sort in canonicalization |
| Max string length | 256 KiB | Bounds NFC normalization cost on a single field |

Caps are checked **before** parsing completes, per §6.4 phase ①'s note that
canonicalizing adversarial JSON is itself an attack surface.

### 5.2 Unicode version — and a divergence already present

Errata R6.34 requires the Unicode version used for NFC to be pinned and changed
only with an envelope schema version bump. Measured on the target toolchains:

| Implementation | Unicode version |
|---|---|
| .NET 10.0.10 (ICU) | **16.0** |
| Rust `unicode-normalization` 0.1.22–0.1.25 | **17.0** |

This is not hypothetical drift; it is present before any code is written, and it
is exactly what R6.34 exists to catch. Crate pinning does not fix it — every
recent `0.1.x` reports 17.0, and `^0.1.x` resolves forward regardless.

**Decision.** The pinned version is **Unicode 16.0**, declared as
`CanonicalJson.UnicodeVersion` and as a constant in `curia-testis`. Agreement is
proven by test rather than assumed: the differential harness normalizes a corpus
that deliberately includes characters assigned in 16.0, characters assigned only
in 17.0, and the full set of canonical-composition candidates, and asserts
byte-identical NFC output from both implementations.

The residual risk is bounded by Unicode's Normalization Stability Policy:
canonical decompositions never change for already-assigned characters, so the two
versions can only disagree on characters introduced in 17.0 that carry canonical
decompositions. Unicode has largely stopped adding precomposed characters, so the
expected delta is empty — but "expected empty" is a claim to test, not to assert.

**Open for review:** whether ADMIT should additionally *reject* code points
unassigned in Unicode 16.0. Doing so makes canonicalization fully deterministic
and gives the schema version real meaning; it also rejects legitimate content
written in newly-encoded scripts until the schema bumps. Recommendation is **not**
to reject in Increment 1, and to revisit if the differential corpus ever shows a
real disagreement.

### 5.3 UTF-16 code-unit ordering

RFC 8785 orders keys by UTF-16 code unit. The two toolchains sit on opposite
sides of this by default, confirmed by measurement:

```
CompareOrdinal(U+FFFD, U+10000) = 10237   → U+FFFD sorts AFTER   (UTF-16, correct)
UTF-8 first bytes: 0xEF vs 0xF0           → U+FFFD sorts BEFORE  (UTF-8, wrong)

Rust  a.cmp(b)                = Less      (native String order, wrong)
Rust  utf16-keyed cmp         = Greater   (correct)
```

C# gets this nearly free, because .NET strings are UTF-16 and
`string.CompareOrdinal` is already UTF-16 code-unit order. Rust must key on
`encode_utf16()` explicitly, because `String`'s natural `Ord` is UTF-8 byte
order, which diverges for every non-BMP character.

This asymmetry is a feature of the pair, not a nuisance: the two implementations
fail differently on Appendix C.4's vector 10, "the one most implementations
fail." A dedicated vector family covers it (§7.2).

### 5.4 Numerics

Errata R6.33 constrains envelope numerics to I-JSON-exact values: integers in the
safe range, no free-form floats, fractional quantities as scaled integers. ADMIT
enforces this — a JSON number that is not an exact integer within ±(2^53 − 1) is
rejected, not rounded.

The canonicalizer still implements ECMAScript number serialization in full,
because C.4 vectors 2 and 3 (`1.0` → `1`, `1e2` → `100`) exercise it and because
the algorithm is part of what RFC 8785 conformance means. The envelope schema
simply never produces input that reaches the fractional path.

## 6. Contradictions between source documents, and their resolutions

| Topic | Conflict | Resolution for Increment 1 |
|---|---|---|
| `context` field | Table 9's signed schema has no `context`; Table 12 (§8.3) requires `context.task` and `context.environment` for `question`/`finding` | Implement Table 9 exactly. `context` is a known future field; adding it goes through R15.1's version bump, not a quiet retrofit |
| Number of `kind` values | Table 9 lists five; scoping CS-11's worked example shows six, including `Vote` | Five. `vote` arrives with errata C1's vote-envelope work and its own schema version bump |
| Vector 9's content | Three extraction readers transcribed C.4 row 9 as a space | Verified against the source with a hex dump: the input and output are both the **six-character escape sequence** (backslash, `u`, `0`, `0`, `0`, `0`) inside the string, preserved unchanged. Distinct from R6.15's rejection of a **raw** 0x00 byte in the wire stream — two vectors, not one |

## 7. Conformance vectors

The vector set is authored **before** the implementations and is the contract
both are written against. Deriving vectors from an implementation would make the
second implementation a transcription of the first, which is the failure mode
that makes independent verification worthless.

### 7.1 Vendored RFC 8785 official vectors

`github.com/cyberphone/json-canonicalization` — Anders Rundgren, RFC 8785's
author — is Apache-2.0 and ships paired `input/`, `output/`, and `outhex/`
directories covering `arrays.json`, `french.json`, `structures.json`,
`unicode.json`, `values.json`, and `weird.json`. These are vendored into
`conformance/rfc8785/` with attribution and license text preserved.

Using the RFC author's own conformance data is strictly better evidence than an
unattributed NuGet package, and it costs nothing.

### 7.2 Cūria vector families

| Family | Contents |
|---|---|
| `c4/` | Appendix C.4 vectors 1–10 verbatim, each as an input/output pair |
| `ordering/` | UTF-16 vs UTF-8 divergence: non-BMP keys against U+E000–U+FFFF keys, surrogate-pair boundaries, the exact pairs where the two orders invert |
| `unicode/` | NFC stability across the 16.0/17.0 delta; NFD→NFC equivalence; combining sequences; singleton and exclusion cases |
| `numbers/` | ECMAScript serialization: `1.0`, `1e2`, negative zero, safe-integer boundaries, and the I-JSON rejections R6.33 requires |
| `admit-reject/` | One file per §14.2 rejection bullet: invalid UTF-8, unpaired surrogates, raw NUL, oversize, over-nested, duplicate keys |
| `envelope/` | Full Table 9 envelopes with known keys, canonical bytes, digests, and detached signatures for both algorithms — the end-to-end fixtures `curia-testis` verifies offline |

Vector format is a directory per case: `input.json`, `expected.canonical`
(exact bytes), `expected.digest` (hex), and `meta.json` naming the requirement
the case exercises. A case with no requirement citation does not belong in the
set.

### 7.3 Duplicate keys

No numbered requirement or C.4 vector covers duplicate object keys. The scoping
document infers rejection from R6.15 — `System.Text.Json` tolerates duplicates
silently, JCS and I-JSON do not, and divergent last-key-wins behavior across
parsers is precisely the cross-implementation mismatch R6.11 and R14.6 exist to
catch. This increment adds it as vector family `admit-reject/duplicate-keys/`
and proposes it as C.4 vector 11 for the white paper's v1.1 pass.

## 8. `curia-testis` — the independent verifier

A Rust crate that verifies and cannot sign. A verifier able to sign is a
verifier that must be trusted with keys, which defeats its purpose.

```
curia-testis verify --envelope <file> --jwks <file>
  → exit 0 and a provenance summary on success
  → exit non-zero with the failing predicate named on failure
```

Crates: `serde_json`, `unicode-normalization`, `sha2`, `ed25519-dalek`, `p256`,
`base64`. No network, no Forum-supplied code, no Forum-supplied results — R6.19's
property in executable form.

**Independence discipline.** A single author writing both implementations
weakens independence by construction; it is §8.7's correlated-reasoner problem
wearing a compiler. Three mitigations, binding on the implementation plan:

1. Both implementations are written from this specification and the vector set.
   Neither is ported from the other, and neither is consulted while the other is
   being written.
2. The strategies differ deliberately. C# hand-rolls a `Utf8JsonReader` walk per
   CS-14; Rust goes through `serde_json` with a custom canonical serializer.
   Different parsers fail differently.
3. Third-party oracles check both, so neither is the sole reference for the
   other.

Both algorithms are covered on both sides. R4.15 makes Ed25519 and ES256 both
`SHALL`; a verifier covering half of them delivers R6.19's property for half the
corpus, which is a hole rather than a simplification.

## 9. Test architecture

| Suite | Framework | Proves |
|---|---|---|
| `Curia.Canon.Tests` | xUnit v3 3.2.2 + CsCheck 4.8.0 | Vector conformance; P1–P5; §14.2 malformed-input bullets, one named test per bullet |
| `Curia.Architecture.Tests` | NetArchTest 1.3.2 | CS-6 (Canon references nothing), CS-7 (dependency direction), CS-9 (`DateTimeOffset.UtcNow` banned outside composition roots) |
| `curia-testis` tests | Rust `cargo test` | Same vector set, independently consumed |
| Differential harness | CsCheck-driven console | C# vs Rust vs oracles, byte-compared |

Properties in scope this increment, verbatim from R14.1:

- **P1** — for any envelope `e` and keypair `k`, `verify(canonicalize(e), sign(k, canonicalize(e)), pub(k))` is true
- **P2** — for any single-field mutation `e'`, verification against `e`'s signature is false
- **P3** — canonicalization is idempotent
- **P4** — canonicalization is order-independent
- **P5** — canonicalization is Unicode-stable after normalization

P8, P9, and P12 also appear in Phase 1's exit criteria but require token
issuance and an event store; they belong to later increments. P23–P26 (the
no-mutation family) become testable when the pipeline exists; Canon's
contribution to them is `CanonicalBytes`, which §3.1 makes structural.

The differential harness generates a document, canonicalizes it in C#, shells out
to `curia-testis`, runs the oracles, and byte-compares all outputs. PR runs are
bounded; nightly runs long. **Divergences are promoted to `conformance/` as
regression vectors and are release blockers** (R14.6). Stryker.NET runs nightly
against Canon; a surviving mutant is filed as a missing vector, not as an
acceptable gap.

## 10. Definition of done

1. `dotnet build` and `dotnet test` green, warnings-as-errors, lock files
   committed, `--locked-mode` restore succeeds.
2. `cargo build`, `cargo test`, and `cargo clippy` green.
3. Every `conformance/` vector passes in **both** implementations.
4. P1–P5 green under CsCheck.
5. Every §14.2 bullet in scope has a named failing-then-passing test.
6. Architecture tests CS-6, CS-7, CS-9 pass, and are demonstrated to fail when
   deliberately violated — an architecture test that has never failed is a test
   nobody has verified.
7. `curia-testis verify` confirms authorship of a fixture envelope offline, with
   no network and no C# code in the path, for both Ed25519 and ES256.
8. The differential harness runs clean over a bounded generated corpus.
9. This specification's §5 decisions are reflected in code as named constants,
   not as literals scattered through call sites.

## 11. Traceability

125 distinct obligations were extracted from the three source documents by
parallel readers and adversarially reviewed for completeness: 52 numbered
requirements, 26 properties, 20 scoping conventions, and 27 vector or
structural obligations. Those binding this increment:

**Requirements** — R4.15, R4.16 (revised, errata A16), R4.17–R4.20, R6.1–R6.21,
R6.31–R6.34 (errata), R11.1, R11.2, R11.3, R14.1, R14.2, R14.3, R14.5, R14.6,
R15.1.

**Properties** — P1–P5 implemented; P23–P26 structurally enabled by
`CanonicalBytes`; P8, P9, P12 deferred with named blockers.

**Conventions** — CS-1 through CS-10 and CS-14 applied; CS-11 through CS-13 and
CS-15 through CS-17 await the Domain and Application increments.

**Errata applied** — A10 (RFC 7797 citation), A16 (no runtime JWKS fetch),
A17 (`typ`/`nbf` checks, recorded for the AuthN increment), B5 → R6.33/R6.34,
C8 → R14.6.

---

*Released under the UNLICENSE and dedicated to the public domain. Referenced
specifications, standards, and third-party software remain under their own
licenses; vendored RFC 8785 test data remains under Apache-2.0 with attribution.*
