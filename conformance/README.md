# Conformance vectors

Each vector is a directory. `input.json` holds the raw input bytes exactly as a
client would send them. A vector that must canonicalize successfully also has
`expected.canonical` (the exact canonical bytes, no trailing newline) and
`expected.digest` (lowercase hex SHA-256 of those bytes). A vector that must be
rejected instead has `expect-reject` containing the RFC 9457 error slug.
Every vector has `meta.json` with `{"profile": "...", "requirement": "R6.8",
"note": "..."}`. A vector citing no requirement does not belong in the set.

**`expect-reject` is not confined to the `admit` profile.** It originally was, on
the assumption that canonicalization either succeeds or is never reached. That
assumption is false: normalizing two distinct member names can make them equal,
and ADMIT cannot catch it because the input genuinely has no duplicate — so
`CanonicalizeWithNfc` itself has to reject, and a vector has to be able to say
so. A vector under any profile may therefore carry `expect-reject` instead of
`expected.canonical`/`expected.digest`, meaning *the function this profile names
must fail with this slug*. `unicode/duplicate-normalized-key/` is the first such
vector.

## Saying that a document must be admitted

For most of this corpus's life there was no way to write it. The `admit` profile means
*"must be rejected with this slug"*, and the expectation files offered exactly two shapes —
`expected.canonical` + `expected.digest`, or `expect-reject`. So R6.39's second sentence,
*"Published vectors SHALL exercise both sides of each of the four boundaries — the value at
the limit (accepted) and one past it (rejected)"*, was **unsatisfiable from the day it was
written**, and nothing about the corpus said so. Zero of the four boundaries had an
accepting-side vector. See errata G2.

`admit-accept` is that missing half. A vector declaring it carries the same four files an
ordinary canonicalizing vector does, plus one key:

```json
{
  "profile": "admit-accept",
  "requirement": "R6.39",
  "pairs-with": "admit-reject/string-over-cap",
  "note": "..."
}
```

A runner SHALL:

1. feed `input.json` to the ADMIT phase **unmodified**, as for every other profile;
2. fail if ADMIT rejects it, **naming the slug ADMIT produced** — otherwise a cap set one
   byte too tight is indistinguishable in the log from a broken harness;
3. require that the same bytes canonicalize under `CanonicalizeWithNfc` to exactly
   `expected.canonical`, whose SHA-256 is `expected.digest`.

**Step 3 is what stops the profile being vacuous.** Acceptance alone asserts almost nothing:
an ADMIT phase that admits everything passes a bare accept vector, and so does one that
admits the document and then canonicalizes it wrongly. Making the expectation a byte-level
statement costs nothing — both runners already have the comparison — and turns "it was
admitted" into a claim that can fail.

**The profile declares acceptance, never the absence of a file.** A vector whose
`expect-reject` failed to be committed must fail, not silently become an accept vector.
For the same reason a runner encountering a `profile` value it does not recognize SHALL
fail rather than skip, and SHALL route every vector **by its declared profile** rather than
by the directory it occupies.

**`pairs-with` names the rejecting-side twin**, and a runner SHALL fail when the named
vector is absent. An accepting-side vector cannot detect a cap that is too generous and a
rejecting-side vector cannot detect one that is too strict; only the pair locates a
boundary. Without the link, a corpus that shipped only the easy half reports exactly what a
complete one reports.

### How the R6.39 boundary vectors are constructed

Each accepting-side document is built so that **only the cap under test can decide it** —
no other cap is within reach. That is not fastidiousness: `curia-testis`'s own
submission-size fuzz sweep never once straddled 1 MiB, because three of its four cases were
being decided by the 256 KiB string cap instead, and reading the fixture made the arithmetic
look right.

| Vector | Construction |
|---|---|
| `depth-at-cap` | 32 container openings, one key per level, innermost value `0` — 193 bytes |
| `members-at-cap` | 1,024 members at depth 1, names `"k0000"`–`"k1023"` — fixed width, so lexicographic order is numeric order and the document is already canonically sorted; every name is 5 bytes |
| `size-at-cap` | exactly 1,048,576 bytes over sixteen members of ≈ 65,528 bytes each, so every string sits near a quarter of the string cap |
| `string-at-cap-escaped` | 131,072 `\u00e9` escapes — 262,144 **decoded** bytes, exactly the cap, in 786,440 bytes of source. Its `expected.canonical` carries U+00E9 literally, because RFC 8785 escapes only `"`, `\` and C0, and U+00E9 is already NFC |
| `member-name-at-cap` | an object member name of exactly 262,144 bytes |

**Every accepting-side document except `string-at-cap-escaped` is already in canonical form**,
so its `expected.canonical` is byte-identical to its `input.json`. That is deliberate: it
lets the expectation be checked by construction rather than produced by an implementation,
which is what "authored before any implementation exists, and not derived from one" requires
of this corpus. `string-at-cap-escaped` is the exception on purpose — the difference between
its input and its canonical form *is* the thing that separates a decoded-basis cap from a
source-span one, and its canonical form is likewise constructed rather than derived.

**A generator is not committed.** R6.11 makes the vector's *bytes* the specification, and a
generator run at test time is a second implementation of the vector — the exact substitution
errata E6 found had already hollowed out two published vectors once. The table above is the
construction rule; the bytes on disk are the contract.

## `index.json` — every directory accounted for

`conformance/index.json` names **every** top-level directory, says whether it is a vector
family, and for each family gives the profiles its vectors may declare and its vector count.
Every runner loads it and fails when it disagrees with what is on disk (R6.45).

This exists because both runners hard-code their family lists in source, so a family added
to the corpus is invisible to each implementation until that implementation is separately
edited — and its absence looks, in a passing test-run log, exactly like a family that ran.
That was not hypothetical when the requirement was written: `red-team/` was on disk, absent
from this README's own "Families" list, and loaded by neither runner. It is legitimately not
a vector family, which is the point — **nothing on disk distinguished that case from a lost
one.** It is now listed with `"family": false` and a reason, so the decision is recorded
rather than merely true.

**A vector family a runner does not enumerate contributes no assurance.** Adding a directory
without adding it here fails; adding it here without teaching a runner to load it fails
there. Either way the omission is loud.

**A vector's `input.json` is fed to the function under test verbatim.** A harness
that wraps, unwraps, re-encodes, or otherwise transforms those bytes first is not
running that vector — it is running a different one it invented, and the
published vector then constrains nothing. This was not hypothetical: one
implementation satisfied two `admit-reject/` vectors by wrapping each bare
document in a synthetic `{"envelope": ..., "signature": ...}` shell, and the
divergence that hid went unnoticed until a differential run fed the published
bytes as published. See errata E6. Corollary: a behaviour only reachable by
bypassing an earlier phase is **not expressible as a vector** — the precedence
rule between a raw duplicate member and a normalization-induced one lives behind
ADMIT, so it is pinned by unit tests in both implementations rather than here.

## Which function a vector constrains

**Read this before implementing.** The corpus is partitioned across two
canonicalization functions, and trying to satisfy all of it with one function is
the mistake this section exists to prevent.

| `profile` | Function under test | Behavior |
|---|---|---|
| `rfc8785` | `Canonicalize` | Pure RFC 8785. Performs **no** Unicode normalization. |
| `canonicalize-with-nfc` | `CanonicalizeWithNfc` | NFC every object key and string value, recursively, **then** canonicalize. |
| `admit` | the ADMIT phase | Input must be **rejected** with the slug in `expect-reject`; canonicalization is never reached. |
| `admit-accept` | the ADMIT phase, then `CanonicalizeWithNfc` | Input must be **admitted**, and the same bytes must then canonicalize to `expected.canonical` (digest `expected.digest`). See "Saying that a document must be admitted" above. |
| `envelope` | `CanonicalizeEnvelope` + `Digests.Sha256` + `DetachedJws.Verify` | End-to-end: canonicalize a full Table 9 envelope, digest it, and verify its detached JWS. See "The `envelope/` family" below — its directory shape is different from every other family's. |
| `merkle-tree` | `MerkleTree` (RFC 9162 §2.1) | Hash the given leaves, build the tree, and reproduce every audit path and consistency proof in `expected.json`; then verify each with the RFC's verification procedures. See "The `merkle/` family" below.
| `acta-leaf` | `Canonicalize` (pure RFC 8785), then `MerkleTree.LeafHash` | The input is a log entry document (R6.46); it must canonicalize to `expected.canonical` (digest `expected.digest`), and `SHA-256(0x00 ‖ canonical)` must equal `expected.leaf`. **Pure** canonicalization, never the NFC profile: hashing is not signing. See "The `acta/` family" below.

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
  one per admission-rule bullet, plus the rejecting side of each R6.39 boundary.
- `admit-accept/` — inputs that must be **admitted** and then canonicalize to a
  pinned form: the accepting side of each R6.39 boundary. See above.
- `envelope/` — end-to-end signed fixtures: a full Table 9 envelope, its wire
  submission, its verification keys, and its canonical form. See below.
- `merkle/` — the Acta's hash tree (§6.6, R6.23): RFC 9162's Merkle Tree Hash,
  audit paths and consistency proofs over the Certificate Transparency reference
  leaves, one vector per tree size 0–8. See below.
- `acta/` — the Acta's leaf input (R6.46, frozen by R15.1): a log entry document
  in, its canonical form and leaf hash out. The Cūria-specific half of the log;
  `merkle/` is the RFC's half. See below.

`red-team/` is **not** a vector family — it is the detector corpus behind R10.11's
measurement (Appendix L), and `index.json` records that with a reason. Neither is
`retrieval/`: it is the held-out query set behind Phase 3's *dedupe measured on a real query
set* and R10.5's canaries, run by `Curia.Application.Tests` alone because `curia-testis` has
no embedding capability; its README says what each file is and what the numbers mean.

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

## The `merkle/` family

The transparency log (§6.6) is a Merkle tree on the Certificate Transparency model,
and a reader that checks an inclusion or consistency proof (R6.23) is running
RFC 9162 §2.1 verbatim: leaf `SHA-256(0x00 ‖ input)`, node
`SHA-256(0x01 ‖ left ‖ right)`, empty tree `SHA-256()`, every split at the largest
power of two strictly below the size. This family pins that arithmetic. What a
leaf's *input* is — the bytes of Figure 7's `entry_i` — is a separate question
with its own vectors; these vectors take the leaf inputs as given.

### Directory shape

`shape: "merkle"` in `index.json`. One directory per tree size, `size-0/` to
`size-8/`, each holding three files:

- `input.json` — `{"leaves": [<hex>, ...]}`: the first *n* leaf inputs as
  lowercase hex, because the reference inputs include the empty string and a
  lone `0x00`, which JSON strings cannot carry faithfully.
- `expected.json` — `root` (hex), `leaf_hashes` (hex, one per leaf), `inclusion`
  (`{index, path}` for every leaf of the tree) and `consistency` (`{from, path}`
  for every earlier size 1..*n*, with `from == n` carrying the empty path).
- `meta.json` — `{"profile": "merkle-tree", "requirement": "R6.23", "note": ...}`.

A runner SHALL, for each vector: hash the leaves and compare each to
`leaf_hashes`; build the tree and compare its root to `root`; for each
`inclusion` entry, compute the audit path, compare it node for node, and verify it
with RFC 9162 §2.1.3.2 against the root; for each `consistency` entry, compute the
proof from the first `from` leaves to all *n*, compare it node for node, and verify
it with §2.1.4.2 against the root of the first `from` leaves and the root of all
*n*. Comparing the *path* and not only its verdict is what makes the family bite: a
prover that emits a differently shaped but internally consistent proof would verify
against its own verifier and interoperate with nobody.

### Provenance

The eight leaf inputs are the Certificate Transparency reference implementation's
test inputs (`""`, `00`, `10`, `2021`, `3031`, `40414243`, `5051525354555657`,
`606162636465666768696a6b6c6d6e6f`), and `size-8/`'s root
`5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328` is the value
every RFC 6962 / RFC 9162 implementation is checked against. Every root, path and
proof in the family was computed by an oracle written from the RFC's recursive
definitions alone (`MTH`, `PATH`, `SUBPROOF`), before either of this repository's
implementations existed, and the published CT values agree with it. Neither
implementation produced any expected value here.

## The `acta/` family

`merkle/` pins the tree and says nothing about what a leaf's input is. This family pins
that: R6.46's leaf is one event of the append-only store, rendered as
`{actor_id, aggregate_id, event_id, event_type, payload, server_ts}` -- the `events` row
minus `seq` -- canonicalized under **pure** RFC 8785, and hashed as
`SHA-256(0x00 ‖ leaf_input)`. R15.1 froze this computation in Phase 1 without anyone
having written it down (errata G9); these vectors are where it is written down.

Ordinary directory shape, ordinary files, plus one: `expected.leaf` holds the lowercase hex
leaf hash. `expected.digest` keeps its usual meaning -- SHA-256 of `expected.canonical`,
with no leaf prefix -- so that a runner cannot pass by confusing the two.

A runner SHALL parse `input.json` **without ADMIT's caps** (an entry wraps a whole
canonical envelope, so a real one can exceed the submission cap), canonicalize it with the
pure profile, compare the bytes and their digest, and then compare the prefixed hash to
`expected.leaf`. Without the pure/NFC distinction the family is vacuous: every vector but
`nfd-payload-stays-nfd` is already NFC. That vector is the one that fails an implementation
using the wrong function.

Every value here was computed by a twelve-line JCS written for the purpose, from the
documents as authored; neither implementation produced any expected file. `content-entry`
wraps `envelope/ed25519-minimal`'s published canonical form and signature verbatim, so the
two families describe the same post.

**Three runners consume this family, and the third is why the pure/NFC vector earns its
place twice over.** `Curia.Canon.Tests` pins the computation; `curia-testis` pins the Rust
side; and since the MCP adapter's Stage 3, `Curia.Client.Tests` pins `ActaCheck.RecomputeLeaf`
-- the client's own recomputation, which is a *second* spelling of a frozen encoding and
therefore exactly the class of defect §6 exists to prevent. A falsification run established
that it needed pinning: swapping the client's `Canonicalize` for `CanonicalizeWithNfc` left
the whole end-to-end suite green, because every fixture on that path is ASCII.
