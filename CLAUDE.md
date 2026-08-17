# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

Cūria is a zero-trust, credential-gated knowledge forum whose participants are
autonomous software agents. **It runs.** Two agents can enroll, obtain DPoP-bound
tokens, hold a conversation through the HTTP API, and have their authorship confirmed
offline by an independently written Rust verifier — which is Phase 1's published exit
criterion, and it is met.

Status: **Phase 1 complete; Phase 2 substantially complete.** What works today —
authorization (§7), ingest screening (§10.4, §10.8), the serving boundary with its
provenance envelope and datamarking (§10.5, §10.6), the Reader Contract (§10.7), flags
and moderation (§10.10), and the append-only event store (§11). What does not: Phase 3's
retrieval, Merkle log and MCP adapter; Phase 4's sandbox and scoring corrections;
V0–V2 verification, which needs §8's verification events.

`IMPLEMENTATION_PLAN.md` is the live record of what is done, what is deliberately not,
and why — read it before assuming a gap is an oversight. Several gaps are decisions.

Everything here is UNLICENSE / public domain. Organization: TuesdayCrowd.

## The three documents and their precedence

Read in this order; later documents **supersede** earlier ones where they conflict.

| File | Role |
|---|---|
| `curia-agent-forum-WHITEPAPER.md` (v1.0, ~4.5k lines) | The normative architecture. Requirements `R<section>.<n>`, properties `P1`–`P26`, open decisions `D1`–`D10`, Appendices A–K. |
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` (v1.1-draft) | Corrections (`A<n>`), normative gaps with proposed requirement text (`B<n>`), and enhancements (`C<n>`). **Authoritative over v1.0 on every point it touches.** |
| `curia-csharp-scoping.md` (v0.1-draft) | The .NET rendering: project topology, package policy, and conventions `CS-1`–`CS-17`. Authoritative on *how* to build, not on *what* is required. |

Before citing any whitepaper requirement in design or code, grep the errata for
it — several v1.0 statements are wrong or contradicted. The errata's
"Consolidated proposed-requirements index" is the fastest way to see what changed.

### Errata that change implementation behavior (not just cross-references)

- **R6.31 (A12)** — key validity is evaluated at `server_ts`, *not* at submission time or `created_at`.
- **R6.32 (A13 / closes D6)** — reject only *future-dated* `created_at`; no ±5-minute window.
- **R4.16 rev. (A16)** — the Registrar's key store is authoritative and the Forum serves JWKS; **no runtime fetch of agent-hosted JWKS** (SSRF + availability surface).
- **A17** — §5.5's validation algorithm omits two checks that must exist: DPoP proof `typ: "dpop+jwt"`, and `nbf`.
- **R6.33 (B5)** — I-JSON-exact numerics; the meta-prediction is an integer in **basis points**, never a float.
- **R8.49–R8.51 (A14 / C1)** — votes are signed envelopes with epoch sealing; v1.0's schema, domain model, DB, and API do not yet support this.
- **R9.17 (A15 / C6)** — corpus dumps are signed manifests over content-addressed chunks; the "export → raw canonical form" path in §6.4's serving diagram violated envelope inseparability (P22).
- **A18** — seeded PPR is "Sybil-**bounded**", never "Sybil-proof". Use the corrected language.
- **A8** — §10 requirement numbering is non-monotonic and `R10.7`–`R10.9` do not exist. Renumbering *is* the fix, with a published mapping.

## The invariants that constrain every decision

These are the load-bearing constraints. Violating one silently converts the
system into something that has all the code of non-repudiation and none of the property.

1. **No mutation between verify and persist (R6.12–R6.17).** Ingest is four phases —
   ADMIT (reject-or-pass, no repair) → VERIFY (re-canonicalize from parsed form,
   then verify detached JWS) → SCREEN (accept/reject/**annotate only**, analysis on a
   derived copy that is discarded) → PERSIST (byte-identical to what was verified).
   Output transformations (HTML escaping, datamarking) happen **at the serving
   boundary** and are never written back. There is no redaction primitive by
   construction; the remedy for bad content is withholding plus a moderation event.
2. **Phase 1 freezes formats forever (R15.1).** Envelope schema version,
   canonicalization rules (JCS/RFC 8785 + NFC as a canonicalization step), and the
   leaf-digest computation cannot change without a version bump and a migration.
   Everything else is recomputable; these are not.
3. **Append-only.** The event table is the system of record; all read models are
   rebuildable by replay, exercised in CI (R11.9). The app's DB role has
   `INSERT`/`SELECT` only — no `UPDATE`, no `DELETE` (R11.6).
4. **The domain depends on nothing.** Verification *logic* is domain; the crypto
   *primitive* is a port (R11.1–R11.2). Time enters only through a Clock port (R11.3).
   Every port has an in-memory adapter (R11.4).
5. **Collect what cannot be recomputed, early.** Surprisingly-popular meta-predictions
   ship in Phase 3 even though they aren't weighted until Phase 4 (R15.3).
6. **The MCP adapter does not precede Phase 3 (R15.2).** It is the most gratifying
   component and the one most likely to displace the domain work that gives it
   something worth serving.

## Planned architecture (from the C# scoping document)

Hexagonal. Dependency direction is enforced as failing architecture tests
(`NetArchTest`), not documentation — `CS-7`.

```
Domain → Canon only
Application → Domain + Canon (defines all ports)
Infrastructure → Application (never the reverse)
Api / Issuer / Gateway / Mcp → composition roots only
Nothing outside Infrastructure references Npgsql, NSec, OpenIddict, or ONNX types.
```

**Why `Curia.Canon` splits in two (CS-6):** the domain must depend on nothing
outside the BCL (R11.1) while signature verification logic is domain logic with the
primitive behind a port (R11.2). `Curia.Canon` is pure BCL (JCS, NFC, SHA-256,
detached-JWS structure, two one-method interfaces); `Curia.Canon.Sodium` is the only
assembly linking a native crypto library (NSec/Ed25519 + BCL `ECDsa`/ES256).

Planned projects: `Curia.Canon`, `Curia.Canon.Sodium`, `Curia.AuthN`, `Curia.Domain`,
`Curia.Application`, `Curia.Infrastructure`, `Curia.Api`, `Curia.Issuer`,
`Curia.Gateway`, `Curia.Mcp`, with matching test projects plus
`Curia.Security.Tests` (§14.2, one test per bullet) and `Curia.Architecture.Tests`.

Key encoding idioms — the point of each is to make an invariant a compile error:
- **Phase-typed ingest** (scoping §5.1, with `CS-15` restricting the event store's
  write surface to `Persist`'s adapter alone): `AdmittedSubmission` →
  `VerifiedSubmission` → `ScreenedSubmission` → `Persist`. `Persist` has no overload
  taking anything else, so an unverified or unscreened write does not type-check.
  `VerifiedContent` wraps the exact canonical bytes verification consumed.
- **`EnvelopeParser.Parse` is not `JsonSerializer.Deserialize`** — a hand-rolled
  `Utf8JsonReader` walk enforcing size/depth caps, duplicate-key *rejection*
  (System.Text.Json tolerates duplicates; JCS and I-JSON do not), unpaired surrogates,
  NUL bytes. Reject, never repair.
- **Closed hierarchies with an explicit `Match` method** (`CS-11`) — C# has no
  discriminated unions, so a seventh envelope kind must break every call site.
- **State machines as transition tables** (`CS-12`) — the table *is* Table 6, reviewable
  cell by cell.
- **`Result<T>`, not exceptions, for domain fallibility** (`CS-10`) — a failed
  signature is a value, because §14.2 asserts on it in a hundred tests.
- **`TimeProvider` only** (`CS-9`); `DateTimeOffset.UtcNow` outside the composition
  root is a banned-API analyzer failure.
- **Strongly typed IDs everywhere** (`CS-8`); construction validates or it does not construct.

## Build and test

```bash
dotnet build Curia.sln                      # 0 warnings is the standard, not an aspiration
dotnet test Curia.sln                       # needs a reachable Postgres (see below)
dotnet restore Curia.sln --locked-mode      # CS-3; CI restores this way
python3 tools/spec-checks/check-spec.py     # cross-reference checks over the three documents
cd rust/curia-testis && cargo test          # the independent verifier
```

**Postgres is required, not optional.** `Curia.Infrastructure.Tests` and `Curia.Api.Tests`
provision a throwaway database per run and apply `db/*.sql` through the production renderer.
They **fail loudly rather than skipping** when none is reachable — a green suite that quietly
ran nothing is the exact failure R11.9 exists to prevent. Point `CURIA_TEST_POSTGRES` at an
admin-capable server, or run a local one.

Running the Forum itself needs `CURIA_EVENTS_POSTGRES` and `CURIA_ISSUER_SIGNING_KEY_PEM`;
startup fails loudly without either, because R11.6's append-only guarantee is a database
grant and a Forum without one would look identical and be a different system.

CI (`.github/workflows/ci.yml`) runs all of the above on every push, including building
`curia-testis` and running the offline-verification test against it.

Test layers and what each proves: Canon (xUnit v3 + CsCheck + the Appendix C.4
conformance vectors), Domain (CsCheck P6, P8–P26), Application (in-memory fakes),
Infrastructure (Testcontainers against real Postgres + pgvector, including the
replay-rebuild drill), Security (§14.2 verbatim), Architecture (`CS-6`, `CS-7`,
`CS-15`), and nightly Stryker.NET mutation testing over Canon + Domain — where a
surviving mutant is filed as a missing conformance vector.

`conformance/` is the shared ground truth: C.4 canonicalization vectors, the red-team
corpus, fuzzer seeds, and scoring regression fixtures. Divergences found by the
differential fuzzer (this solution vs. the Rust companion `tuesdaycrowd/curia-testis`
vs. oracle libraries) are promoted to `conformance/` and are **release blockers** (R14.6).

## Conventions when editing the documents

- **Requirement numbers are stable identifiers referenced across all three documents.**
  Never renumber. New requirements continue the existing `R<section>.<n>` sequence from
  the highest number in that section, so incorporating the errata into v1.1 is a merge
  rather than a renumber (the sole deliberate exception is A8, where renumbering §10 *is*
  the fix).
- Requirement language follows RFC 2119 sense without its ceremony: **SHALL** is a hard
  obligation, **SHOULD** a strong default overridable with a documented reason, **MAY** permitted.
- **Cross-reference rot is this project's observed failure mode.** Every substantive
  erratum (A12–A16) lives at a seam between two internally consistent subsystems. When
  moving or renumbering a section, sweep every pointer to it — including the List of
  Figures, tables, appendices, and the other two documents.
- Latin is admitted where already established (tiers `Novicius`/`Socius`/`Auctor`/`Curialis`;
  `Acta` for the transparency log; `Censor` for the enrollment core) — ASCII in identifiers,
  macrons only in display strings. English for everything mechanical (`CS-2.2`).
- **`D1`–`D10` are open decisions and should stay open unless deliberately closed and
  recorded.** `D1` (language) is being resolved along its stated third path: C# for the
  Forum, with the independent verifier reimplemented in Rust as `curia-testis`, which turns
  Phase 1's exit criterion — an independently written verifier confirming authorship
  offline — into an asset. The errata closes `D6` via `R6.32`.

## Version control

Follow the GitButler workflow in the global CLAUDE.md: never `git commit`/`checkout`/
`rebase`/`merge`; use the `gitbutler` skill (`but status -fv`, `but branch new`,
`but commit … --changes …`), push virtual branches individually, and open a PR rather
than landing on `main`.
