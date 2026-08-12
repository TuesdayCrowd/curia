# Cūria Increment 2 — `curia-testis`, the Independent Verifier

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Rust crate that verifies Cūria post authorship offline, written from the
specification and the published conformance vectors alone, so that agreement between
it and the C# implementation is evidence rather than coincidence.

**Architecture:** Verify-only — the crate can check a signature and cannot produce
one. Vector-driven: every behavior is pinned by a published fixture, and the crate is
correct exactly when the corpus passes. The differential harness that compares it
against the C# implementation is the last task and runs outside the cleanroom.

**Tech Stack:** Rust (stable 1.96), `serde_json`, `unicode-normalization`, `sha2`,
`ed25519-dalek`, `p256`, `base64`.

**Source of truth:** `spec/` and `conformance/` inside the cleanroom. Nothing else.

---

## Why this plan contains no implementation code

Plan 1 gave every task the exact code to write. **This plan deliberately does not**,
and that is not an oversight to correct.

The value of a second implementation is that it was derived independently. If this
plan carried Rust translated from the C# implementation, the two would agree because
they came from one mind, and every differential test would be tautological. The
cleanroom would be theatre — physically isolated from `src/` while the plan smuggled
`src/`'s decisions in as instructions.

So each task states **what done looks like**: the acceptance criteria, the interfaces
at the crate boundary, the exact commands, and which section of the specification to
derive the behavior from. The algorithms are the implementer's to work out from
RFC 8785, RFC 7797, RFC 8037, and the vectors — exactly as a genuine third party
would.

**This means the usual "no placeholders" rule is satisfied differently here.** A step
that says "derive the key-ordering rule from RFC 8785 §3.2.3" is not vague; it names
a specific normative source. A step that said "add appropriate error handling" still
would be, and is still forbidden.

**If you are implementing a cleanroom task:** you have no access to any existing
implementation, and that is intentional. Work from `spec/` and `conformance/`. If
something is genuinely underdetermined by both, **stop and report it** rather than
guessing — an underdetermined behavior is a specification defect worth surfacing, and
several were found that way in Increment 1.

## Global Constraints

- **Verify-only.** The crate SHALL NOT provide a signing function. A verifier able to
  sign is a verifier that must be trusted with keys.
- **Offline.** No network access at any point. No dependency may fetch at runtime.
- **`Result`, not panic.** Malformed, adversarial, or truncated input SHALL produce a
  typed error. A panic on untrusted input is a defect. This mirrors the "reject,
  never repair" discipline the specification states for ADMIT.
- **Rust edition 2021+, stable toolchain.** `cargo clippy` clean; `#![forbid(unsafe_code)]`.
- **Pinned dependency versions**, committed `Cargo.lock`.
- **Unicode 16.0 is the pinned normalization version** (design spec §5.2). The
  `unicode-normalization` crate reports 17.0 at every published `0.1.x`, so the
  version cannot be pinned by crate selection — it must be handled and its residual
  risk stated. See Task 3.
- **Frozen ADMIT values**, from design spec §5.1 and errata D5/D6/D7: max submission
  1 048 576 bytes; max depth 32 counting **container openings only**; max 1 024
  object members per level; max string 262 144 bytes; integers bounded
  `−(2^53 − 1) ≤ n ≤ 2^53 − 1` inclusive.
- **Commit via GitButler**, never `git commit`. Cleanroom tasks commit nothing —
  the controller moves finished work into the repository.

---

## File Structure

```
rust/curia-testis/
  Cargo.toml
  Cargo.lock
  src/
    lib.rs            crate root, error type, public API
    json.rs           JSON value model + ADMIT-phase parsing and limits
    canonical.rs      RFC 8785 canonicalization (pure)
    nfc.rs            the NFC profile layer
    digest.rs         SHA-256 over canonical bytes
    jws.rs            detached JWS verification, protected-header validation
    jwk.rs            JWKS parsing (RFC 8037 OKP, RFC 7518 EC)
    bin/curia-testis.rs   the CLI
  tests/
    vectors.rs        drives every conformance family by profile
    envelope.rs       end-to-end fixture verification
```

---

## Task A — Publish the `envelope/` fixture family *(repo-side, not cleanroom)*

**This task runs in the repository, against the C# implementation, before any
cleanroom work begins.** It exists because the design spec §7.2 promises an
`envelope/` family that Increment 1 never built — the gap that makes
`curia-testis verify` untestable.

The fixtures must be produced by the **signer**, because the verifier cannot sign.
A verifier checking signatures the signer actually emitted is the whole point.

**Files:**
- Create: `conformance/envelope/<case>/` directories
- Create: `tools/GenerateEnvelopeFixtures/` (a small console project, or a test-only generator)

**Acceptance criteria:**

- [ ] **Step 1: Decide and document the fixture format**, then write it into
      `conformance/README.md` alongside the existing format contract. Each case
      directory holds: `submission.json` (the full `{envelope, signature}` wire
      object), `jwks.json` (the public key set that verifies it), `expected.canonical`
      (the canonical bytes of the envelope), `expected.digest` (lowercase hex
      SHA-256), and `meta.json` with `profile: "envelope"`, a `requirement`, an `alg`,
      and a `note`.
- [ ] **Step 2: Generate at least these cases**, all with committed, published private
      keys — these are test fixtures and their keys are worthless by construction, so
      say so in the README:
      - `ed25519-minimal` — smallest valid envelope, `alg: EdDSA`
      - `ed25519-full` — every Table 9 field populated, including `code_blocks`, `refs`, `tags`
      - `ed25519-unicode` — content requiring NFC composition in both a key and a value
      - `es256-minimal` — same shape as the first, `alg: ES256`
      - `tampered-body` — a valid fixture whose body was altered after signing; `meta.json` records `expect-verify-failure`
      - `wrong-key` — a valid signature checked against a different published key; `expect-verify-failure`
- [ ] **Step 3: Prove the fixtures are self-consistent** by verifying every one with
      the C# implementation and recording the output in the task report. A fixture the
      signer cannot verify is worse than no fixture.
- [ ] **Step 4: Confirm the private keys are published** and the README states plainly
      that they are compromised by design and must never be used for anything.
- [ ] **Step 5: Commit** with `but commit -b testis-fixtures -m "..."`.

**Interfaces produced:** `conformance/envelope/` — the fixture family every later task
verifies against.

---

## Task 1 — Crate scaffolding, vector loader, failing CLI

**Files:** `rust/curia-testis/Cargo.toml`, `src/lib.rs`, `src/bin/curia-testis.rs`, `tests/vectors.rs`

**Interfaces produced:**
- CLI: `curia-testis verify --envelope <path> --jwks <path>`
- Exit `0` on successful verification, non-zero otherwise
- On success, prints a provenance summary to stdout: the author, the `kid`, the algorithm, and the envelope digest in `sha256:<hex>` form
- On failure, prints the failing predicate to stderr — naming *which* check failed, not merely that verification failed

**Acceptance criteria:**

- [ ] **Step 1: Scaffold the crate** with the six pinned dependencies, `#![forbid(unsafe_code)]`, and a committed `Cargo.lock`.
- [ ] **Step 2: Write the vector loader.** Read `conformance/README.md` first — it
      states the directory format and, in its "Which function a vector constrains"
      section, the `profile` field that partitions the corpus. The loader must expose
      every vector with its profile, requirement, input bytes, and either its expected
      canonical bytes and digest or its `expect-reject` slug.
- [ ] **Step 3: Write the harness that runs every family and asserts.** It must fail
      loudly at this point — nothing is implemented. Record the failure count; it is
      the baseline the following tasks drive to zero.
- [ ] **Step 4: `cargo test`** — expect failures, no panics in the loader itself.
- [ ] **Step 5: `cargo clippy` clean.**

---

## Task 2 — Pure RFC 8785 canonicalization

**Files:** `src/json.rs`, `src/canonical.rs`

**Derive from:** RFC 8785 (the normative algorithm), and `conformance/rfc8785/` — the
RFC author's own vectors, vendored unmodified. Errata **D1** explains why this
function performs **no** Unicode normalization; do not add any.

**Acceptance criteria:**

- [ ] **Step 1: All six `conformance/rfc8785/` vectors pass**, byte-for-byte. They are
      `input-<name>.json`/`output-<name>.json` file pairs rather than directories.
- [ ] **Step 2: The `ordering/` family passes.** Read errata D1 and the design spec
      §5.3 on why key ordering is not the obvious thing in Rust — the language's
      natural string ordering is not the ordering RFC 8785 specifies, and the family
      exists to prove it.
- [ ] **Step 3: The `numbers/` family passes.** RFC 8785 §3.2.2.3 specifies number
      serialization normatively by reference to ECMA-262. It is more load-bearing than
      it looks; `node` is available and `String(x)` is the oracle.
- [ ] **Step 4: `cargo test`** — the two families and the official vectors green.
- [ ] **Step 5: Report** which vectors, if any, required an interpretation the
      documents did not settle.

---

## Task 3 — The NFC profile

**Files:** `src/nfc.rs`

**Derive from:** errata **D1** (revised R6.9), which is normative about **order of
operations** — normalize first, building a normalized tree, then canonicalize —
because normalization can change a key's sort position.

**Acceptance criteria:**

- [ ] **Step 1: The `unicode/` and `c4/` families pass.**
- [ ] **Step 2: Normalization covers object member names as well as string values**,
      recursively. The corpus contains a vector that fails if only values are
      normalized; find it and say which it is in your report.
- [ ] **Step 3: Confirm the pure function from Task 2 is unchanged** and the official
      RFC 8785 vectors still pass against it. If adding NFC broke them, the layering
      is wrong — see errata D1.
- [ ] **Step 4: Record the Unicode version** the crate actually normalizes with, and
      compare it to the pinned 16.0 from design spec §5.2. State the residual risk
      plainly; do not paper over a mismatch.
- [ ] **Step 5: `cargo test`.**

---

## Task 4 — ADMIT rules

**Files:** `src/json.rs`

**Derive from:** errata **D5**, **D6**, **D7**, and `conformance/admit-reject/`. Each
vector's `expect-reject` names the slug its input must produce.

**Acceptance criteria:**

- [ ] **Step 1: Every `admit-reject/` vector is rejected with its declared slug** — not
      merely rejected. A generic failure where a specific one is named is a defect.
- [ ] **Step 2: The depth boundary is exact.** Errata D6 states the counting
      convention; pin both sides, since the published vector only pins one.
- [ ] **Step 3: The numeric bounds are exact and symmetric**, per errata D5, including
      the behavior at exactly `2^53`.
- [ ] **Step 4: No input causes a panic.** Fuzz or property-test the parser against
      arbitrary bytes; report what you ran and for how long.
- [ ] **Step 5: `cargo test`.**

---

## Task 5 — Digest, JWKS, and detached JWS verification

**Files:** `src/digest.rs`, `src/jwk.rs`, `src/jws.rs`

**Derive from:** errata **D3** — which corrects the specification's own account of
what "detached" means, and is the single highest-consequence detail in this task —
and errata **D4** for the Ed25519 JWK shape (RFC 8037 `OKP`), plus RFC 7518 for
`ES256`'s `EC` shape.

**Acceptance criteria:**

- [ ] **Step 1: The signing input is constructed per errata D3 / RFC 7797.** Read D3
      before writing anything here: the specification's References entry describes a
      *different* mechanism, and following it produces signatures that verify only
      against themselves.
- [ ] **Step 2: Protected-header validation rejects** a `typ` other than
      `curia-post+jws`, `b64: true`, and any `crit` other than exactly `["b64"]`.
- [ ] **Step 3: The algorithm allow-list is exactly `EdDSA` and `ES256`.** Everything
      else — including `none` and every `HS*` — is rejected **before** any
      cryptographic operation runs. Prove the ordering, not just the outcome.
- [ ] **Step 4: ES256 signatures are the fixed-width R‖S form of RFC 7518 §3.4**, 64
      bytes, not DER. A DER signature over the same content must be rejected.
- [ ] **Step 5: JWKS parsing handles both key types** per errata D4.
- [ ] **Step 6: `cargo test`.**

---

## Task 6 — The CLI, and the offline authorship claim

**Files:** `src/bin/curia-testis.rs`, `tests/envelope.rs`

**Acceptance criteria:**

- [ ] **Step 1: Every `conformance/envelope/` fixture verifies**, and every fixture
      marked `expect-verify-failure` fails with the specific reason named.
- [ ] **Step 2: `curia-testis verify` exits 0 and prints the provenance summary** for a
      good fixture, and exits non-zero naming the failing predicate for a bad one.
- [ ] **Step 3: Prove the offline claim.** Run the verification with no network
      available and record it. This is the property the whole crate exists to deliver
      — R6.19's "confirm authorship without executing Forum-supplied code and without
      trusting Forum-supplied results" — so demonstrate it rather than asserting it.
- [ ] **Step 4: The whole corpus passes**: every family, every profile. Report the
      count by family.
- [ ] **Step 5: `cargo clippy` clean, `cargo test` green.**

---

## Task 7 — The differential harness *(repo-side, not cleanroom)*

**This task runs in the repository, after the crate is moved in from the cleanroom.**
It is the join between the two implementations and is the first moment either sees the
other.

**Files:** `tests/Curia.Differential/` (or a console harness), CI wiring.

**Acceptance criteria:**

- [ ] **Step 1: Generate documents and compare all implementations byte-for-byte** —
      C# `Canonicalize`, `curia-testis`'s pure function, and `node`'s `String(x)` for
      the number path. Property-driven generation, not a fixed list.
- [ ] **Step 2: Compare the NFC profile across both implementations.** Expect the
      Unicode 16.0/17.0 delta to surface here if anywhere; design spec §5.2 predicted
      it.
- [ ] **Step 3: Compare ADMIT accept/reject decisions** across the whole generated
      corpus. Both must agree on *which* slug, not merely on rejection.
- [ ] **Step 4: Every divergence is promoted to a `conformance/` vector** and is a
      release blocker per R14.6. A divergence resolved by changing one side without
      adding a vector is a divergence that will return.
- [ ] **Step 5: Report the corpus size and the divergence count.** Zero is the
      expected outcome; a non-zero count is a finding, not a failure of the exercise.

---

## Definition of Done

1. Every published conformance vector passes in `curia-testis`, by profile.
2. `curia-testis verify` confirms authorship of an `envelope/` fixture offline, for
   both `EdDSA` and `ES256`, with no network and no C# code in the path.
3. `cargo clippy` clean, `#![forbid(unsafe_code)]`, `Cargo.lock` committed.
4. No input causes a panic.
5. The differential harness runs clean, or every divergence is a committed vector.
6. **Phase 1's exit criterion is met:** an independently written verifier confirms
   authorship offline.

## Self-Review

**Spec coverage.** Design spec §8 (`curia-testis`) → Tasks 1–6; §7.2's `envelope/`
family → Task A, which Increment 1 omitted; §9's differential harness and R14.6 →
Task 7. Errata D1 → Tasks 2 and 3; D2 → Task 1's loader; D3 → Task 5; D4 → Task 5;
D5 and D6 and D7 → Task 4.

**Placeholder scan.** No "TBD" or "handle edge cases". Every task names either an
exact acceptance criterion or a specific normative source. The absence of
implementation code is the deliberate deviation argued at the top, not an omission.

**Interface consistency.** The CLI contract in Task 1 is the same one Task 6 tests.
The vector `profile` values match those stamped in `conformance/*/meta.json`
(`rfc8785`, `canonicalize-with-nfc`, `admit`) plus `envelope`, introduced by Task A.

**Known risk carried deliberately.** Task A runs against the C# implementation, so its
fixtures inherit whatever that implementation does. If both implementations are wrong
in the same way, these fixtures will not reveal it — only the RFC author's vendored
vectors and `node` can, which is why Task 2 leads with those and Task 7 keeps `node`
in the comparison.
