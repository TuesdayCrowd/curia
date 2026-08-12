# Conformance vectors

Each vector is a directory. `input.json` holds the raw input bytes exactly as a
client would send them. A vector that must canonicalize successfully also has
`expected.canonical` (the exact canonical bytes, no trailing newline) and
`expected.digest` (lowercase hex SHA-256 of those bytes). A vector that must be
rejected instead has `expect-reject` containing the RFC 9457 error slug.
Every vector has `meta.json` with `{"requirement": "R6.8", "note": "..."}`.
A vector citing no requirement does not belong in the set.

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
