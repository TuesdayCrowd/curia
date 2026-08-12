# Conformance vectors

Each vector is a directory. `input.json` holds the raw input bytes exactly as a
client would send them. A vector that must canonicalize successfully also has
`expected.canonical` (the exact canonical bytes, no trailing newline) and
`expected.digest` (lowercase hex SHA-256 of those bytes). A vector that must be
rejected instead has `expect-reject` containing the RFC 9457 error slug.
Every vector has `meta.json` with `{"profile": "...", "requirement": "R6.8",
"note": "..."}`. A vector citing no requirement does not belong in the set.

## Which function a vector constrains

**Read this before implementing.** The corpus is partitioned across two
canonicalization functions, and trying to satisfy all of it with one function is
the mistake this section exists to prevent.

| `profile` | Function under test | Behavior |
|---|---|---|
| `rfc8785` | `Canonicalize` | Pure RFC 8785. Performs **no** Unicode normalization. |
| `canonicalize-with-nfc` | `CanonicalizeWithNfc` | NFC every object key and string value, recursively, **then** canonicalize. |
| `admit` | the ADMIT phase | Input must be rejected with the slug in `expect-reject`; canonicalization is never reached. |
| `envelope` | `CanonicalizeEnvelope` + `Digests.Sha256` + `DetachedJws.Verify` | End-to-end: canonicalize a full Table 9 envelope, digest it, and verify its detached JWS. See "The `envelope/` family" below — its directory shape is different from every other family's. |

The `rfc8785/` family carries the `rfc8785` profile implicitly — it is the RFC
author's own data, vendored unmodified as input/output file pairs rather than as
directories, so it has no `meta.json`.

**Why the partition exists.** RFC 8785 deliberately performs no normalization, and
two of its six official vectors exist to prove it — `unicode.json` preserves an NFD
combining sequence untouched, and `weird.json` uses `U+FB33` as an object key.
`U+FB33` is on Unicode's Composition Exclusion list, so NFC decomposes it and never
recomposes, changing its leading UTF-16 code unit and therefore **where it sorts**.
Cūria requires NFC (R6.9) and RFC 8785 forbids it; the two cannot be one function.
See errata D1 and D2.

**The trap.** Weakening or removing NFC makes strictly more vectors pass. An
implementation that folds the two functions together and then drops normalization to
get the count up will look like it is converging. It is not.

## Families

- `rfc8785/` — the official RFC 8785 test vectors, vendored unmodified from
  the reference implementation. See `rfc8785/ATTRIBUTION.md`.
- `c4/` — Appendix C.4 vectors 1-10, transcribed exactly.
- `ordering/` — key-ordering edge cases (UTF-16 code-unit order vs. UTF-8
  byte order, ASCII case ordering).
- `unicode/` — NFC normalization behaviour.
- `numbers/` — ECMAScript number serialization.
- `admit-reject/` — inputs that must be rejected rather than canonicalized,
  one per admission-rule bullet.
- `envelope/` — end-to-end signed fixtures: a full Table 9 envelope, its wire
  submission, its verification keys, and its canonical form. See below.

These files are the shared conformance contract between independent
implementations (C#, Rust, ...) of the Cūria canonicalizer. They are
authored before any implementation exists, and are not derived from one.

## The `envelope/` family

Every other family pins one function (canonicalize, or ADMIT accept/reject).
`envelope/` pins the whole signed-content pipeline design spec §7.2 promises:
canonicalize a real Table 9 envelope, digest it, and verify a detached JWS over
it — exactly the surface `curia-testis verify --envelope <file> --jwks <file>`
exists to exercise offline. **Because a verifier cannot sign, these fixtures are
produced by the C# signer** (`tools/GenerateEnvelopeFixtures/`) rather than
hand-authored like the other families, and every one of them is verified by that
same C# implementation before being committed — see "Self-consistency" below.

### Directory shape

Each `envelope/<case>/` holds six files, not the four other families use:

| File | Contents |
|---|---|
| `submission.json` | The full `{"envelope": ..., "signature": ...}` wire object, exactly as a Forum would receive it (§6.2, Appendix C.3). Pretty-printed for readability — it is **not** required to be in canonical form, only `expected.canonical` is. |
| `jwks.json` | A standard JWKS (`{"keys": [...]}`) of **public** keys: everything a verifier needs, and nothing more. |
| `private-keys.json` | The same key(s), in the private JWK form (RFC 8037 §2 / RFC 7518 §6.2.2's `d`). See "Private keys are published on purpose" below. Some entries carry a non-standard `role` string disambiguating which key is which — used only by `wrong-key`, where two keys share one `kid`. |
| `expected.canonical` | The exact canonical bytes (RFC 8785 + NFC, R6.9) of the envelope **as published in `submission.json`** — no trailing newline. For `tampered-body`, this is the canonical form of the *tampered* envelope, not the one that was actually signed: it is what a verifier re-canonicalizing the received bytes actually computes, which is the point of that fixture. |
| `expected.digest` | Lowercase hex SHA-256 of `expected.canonical` — no trailing newline. |
| `meta.json` | `{"profile": "envelope", "requirement": "...", "alg": "EdDSA"\|"ES256", "note": "...", "expect-verify-failure": "..."}`. The last key is present only on the two negative cases (see below) and names the RFC 9457 slug (from `Curia.Canon.Jws.JwsErrors` today; the same vocabulary `curia-testis` is expected to produce) that verification must fail with. |

### JWK shapes (errata D4)

`jwks.json`/`private-keys.json` are a verifier's *only* way to obtain keys, so
getting these shapes right is load-bearing. Errata D4 corrects a gap the
original corpus left open — Ed25519 has no JWK form under RFC 7517/7518 at all;
guessing the `EC` shape (`x`/`y`) for it parses but never verifies anything:

- **Ed25519 (`alg: "EdDSA"`)** uses the RFC 8037 octet-key-pair form:
  `kty: "OKP"`, `crv: "Ed25519"`, `x` = base64url(32-byte public key, no
  padding). The private form adds `d` = base64url(32-byte seed).
- **ES256 (`alg: "ES256"`)** uses the RFC 7518 `EC` form: `kty: "EC"`,
  `crv: "P-256"`, `x`/`y` = base64url(32-byte coordinate) each. The private
  form adds `d` = base64url(32-byte scalar).

### Cases

- `ed25519-minimal` / `es256-minimal` — the smallest valid envelope for each
  required algorithm (R4.15): every optional Table 9 field present as `null`
  or `[]`, nothing omitted.
- `ed25519-full` — every Table 9 field populated, including a `code_blocks`
  entry, three `refs` (`post`/`package`/`url`), and multiple `tags`.
- `ed25519-unicode` — content requiring NFC composition (R6.9) in **both** a
  key and a value: a real field (`title`) carries NFD text, and one extra
  field outside Table 9's fixed vocabulary has an NFD key *and* NFD value, so
  canonicalizing it exercises key normalization, not just value
  normalization. Distinct from `unicode/nfd-key-composes-to-nfc/`, which uses
  a different word, so this is not that vector duplicated under a new name.
- `tampered-body` — a validly signed envelope republished with `body` changed
  after signing. `jwks.json` has the *correct* key; verification must still
  fail, because canonicalizing the received bytes does not reproduce what was
  signed.
- `wrong-key` — a valid signature checked against a public key that did not
  produce it. `jwks.json`'s only entry for the signature's `kid` is a
  different keypair; `private-keys.json` discloses both the actual signer and
  the published-but-wrong key, each labeled by `role`, so nothing here is a
  secret even though `jwks.json` alone does not reveal which key really
  signed.

`tampered-body` and `wrong-key` both fail with `curia/jws/signature-invalid`
today — a *different* published key or a mismatched body produce the same
predicate from `DetachedJws.Verify`'s point of view, since both mean "the
signature does not check out." Their `meta.json` and their `note` field
distinguish which cause is under test even though the slug is shared.

### Private keys are published on purpose

Every private key in `envelope/*/private-keys.json` was generated solely to
sign that one fixture and is committed to this public repository. **These
keys are compromised by construction.** Anyone with read access to this repo
has them. Do not reuse them for any real agent identity, any real Forum
account, or anything outside this conformance corpus — that is the entire
reason they are allowed to be published at all.

### Self-consistency

Every fixture in this family is generated **and then independently
re-verified** — reloaded from the files just written, parsed through the real
`EnvelopeParser`/`CanonicalJson`/`DetachedJws` path, and checked against
`expected.canonical`/`expected.digest`/`meta.json` — by
`tools/GenerateEnvelopeFixtures` in the same run that produces them. The two
`expect-verify-failure` cases are confirmed to fail for the *declared* reason,
not merely to fail. A fixture the signer itself cannot verify (or that fails
for the wrong reason) is worse than no fixture, and is never committed. Run
`dotnet run --project tools/GenerateEnvelopeFixtures` to regenerate and
re-verify the whole family; its console output is the evidence recorded in
this task's report.
