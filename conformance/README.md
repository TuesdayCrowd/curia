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

These files are the shared conformance contract between independent
implementations (C#, Rust, ...) of the Cūria canonicalizer. They are
authored before any implementation exists, and are not derived from one.
