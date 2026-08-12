# Handoff — `curia-testis` cleanroom execution

**For the session that picks up Increment 2's cleanroom work. Read this first; it is
short on purpose, and one rule in it matters more than everything else.**

| | |
|---|---|
| **Written** | 11 August 2026 |
| **Hands off** | Plan 2, Tasks 1–6 (cleanroom) and Task 7 (repo-side) |
| **Plan** | `docs/superpowers/plans/2026-08-11-canon-testis.md` |
| **Prerequisite** | Task A complete — `conformance/envelope/` exists |

---

## The one rule

**Do not read `src/` or `tests/` until Task 7.** Not the controller, not any subagent,
not "just to check an interface."

Cūria's §6 layer already has a complete C# implementation. Increment 2 builds a second
implementation in Rust whose *only* value is that it was derived independently. If
either implementation informs the other, they agree because they came from one mind,
and every differential test becomes tautological — a very expensive way to learn
nothing.

The previous session's controller had read the entire C# implementation. That is why
the cleanroom exists, and why this work was deliberately handed to a fresh session:
**you have not read it, and if you keep it that way the independence claim is real
rather than mitigated.**

This is not a purity ritual. Phase 1's exit criterion is "an independently written
verifier confirms authorship offline." A verifier written with one eye on the
reference implementation does not satisfy it, and nobody downstream can tell the
difference by looking at the result.

Task 7 is the join, and is the first moment either side may see the other.

---

## Where things stand

**Merged to `main`:**

- Increment 1 — `Curia.Canon`, `Curia.Canon.Sodium`, `Curia.Domain.Primitives`, and the conformance corpus. 145 tests pass.
- Errata **Part D** — nine entries recording what building Increment 1 proved about the source documents. D1–D6 are the ones a second implementation would otherwise diverge on. **Read Part D before Tasks 2, 3, and 5.**

**On branches awaiting merge** (both should land before cleanroom work begins):

- `testis-plan` — Plan 2 and this handoff
- `testis-fixtures` — Task A's `conformance/envelope/` family

**Not started:** Plan 2 Tasks 1–6 (the crate) and Task 7 (the differential harness).

---

## The cleanroom

A prepared directory containing everything the work needs and nothing it must not see:

```
<scratch>/cleanroom/
  spec/          the white paper, errata (incl. Part D), C# scoping doc,
                 Increment 1 design spec, follow-ups, and Plan 2
  conformance/   all 42 vector directories including envelope/
  rust/          empty — the crate goes here
```

The previous session's copy is at
`/private/tmp/claude-501/-Users-lawls-Development-TuesdayCrowd-Projects-curia/8e45c852-2053-4ee1-96db-0b8e0a7cd0fd/cleanroom`,
which will not survive. **Rebuild it in your own scratch space** from a merged `main`:

```bash
CR=<your-scratch>/cleanroom
mkdir -p "$CR"/{spec,rust}
cp -R conformance "$CR"/conformance
cp curia-agent-forum-WHITEPAPER.md curia-whitepaper-ERRATA-AND-ADDENDUM.md \
   curia-csharp-scoping.md "$CR"/spec/
cp docs/superpowers/specs/*.md docs/superpowers/plans/2026-08-11-canon-testis.md "$CR"/spec/

# The isolation check. Must print 0.
find "$CR" \( -name '*.cs' -o -name '*.csproj' -o -name '*.sln' \) | wc -l
```

Run that last check after building it and after any refresh. The cleanroom's value is
that it makes the rule structural rather than a matter of subagent compliance — but
only if it actually holds.

Dispatch cleanroom subagents with the cleanroom path as their working directory. They
should never be given a path into the repository.

---

## Mechanics

**Toolchain** (verified present): Rust stable 1.96, `cargo`. All six crates the plan
names resolve and build together: `serde_json`, `unicode-normalization`, `sha2`,
`ed25519-dalek`, `p256`, `base64`. `node` v26.5.1 is available and is the project's
established oracle for ECMAScript number formatting.

**Version control is GitButler, never plain git.** Do not run `git commit`, `git add`,
`git checkout`, or any git write command. Plain `git status` in this repository
reports mass staged deletions alongside matching untracked entries — that is a
**GitButler artifact, not damage**; ignore it and never try to "fix" it. Use
`but status` for the truth and `but commit -b <branch> -m "..."` to commit. End commit
messages with the `Co-Authored-By` trailer the repository uses.

**Cleanroom subagents commit nothing.** They work in the cleanroom; you move finished
work into `rust/curia-testis/` in the repository and commit it there.

**Process:** Plan 2 names `superpowers:subagent-driven-development`. Increment 1 ran
that way across ten tasks and it earned its cost — reviews caught nine defects,
several of which no test would have found. Two habits from it are worth carrying:

- **Verify claims rather than accepting reports.** The reviews that found real defects built harnesses and probed the compiled artifact; the ones that read code and agreed found nothing.
- **Check bytes with `xxd`, not with your eyes.** This repository has been bitten four times by characters that transform silently in transit — NUL bytes, NFD text, and a private-use codepoint that vanished from two files at once. Every instance was caught by byte inspection and missed by reading.

---

## Carried concerns

From Task A, all recorded and none blocking:

- **The fixture generator produces fresh keys on every run**, so re-running it silently replaces committed fixtures with different-but-valid ones. Test data that is supposed to be stable should not move; worth making deterministic. If a fixture ever fails to verify, check whether someone re-ran the generator before assuming the verifier is wrong.
- **`tampered-body` and `wrong-key` both fail with `curia/jws/signature-invalid`.** The signer has no finer predicate. This is defensible — a verifier telling an attacker *why* a signature failed is a disclosure — so do not hold the Rust side to distinguishing them.
- **`private-keys.json` is an invented filename** with no standard behind it, documented in `conformance/README.md`. The published private keys are compromised by construction and exist only so fixtures are reproducible.

From Increment 1, relevant to Task 7:

- **Unicode version drift is expected.** The design spec pins 16.0; the Rust crate reports 17.0 at every published `0.1.x`, so it cannot be pinned by crate selection. Design spec §5.2 predicted this. If the differential harness surfaces it, that is the prediction landing, not a new bug.
- **Base64url whitespace handling differs by platform.** Rust's decoder rejects embedded whitespace where .NET's strips it, so the two disagree about whether some wire JWS is valid. The specification does not say which is normative. Decide it during Task 7 rather than "fixing" whichever side is inconvenient.

---

## What done looks like

Plan 2's Definition of Done, restated:

1. Every published conformance vector passes, by profile.
2. `curia-testis verify` confirms authorship of an `envelope/` fixture **offline**, for both `EdDSA` and `ES256`, with no network and no C# code in the path.
3. `cargo clippy` clean, `#![forbid(unsafe_code)]`, `Cargo.lock` committed.
4. No input causes a panic.
5. The differential harness runs clean, or every divergence is a committed conformance vector — divergences are release blockers per R14.6.
6. **Phase 1's exit criterion is met.**

A divergence found in Task 7 is a good outcome, not a failure. It is the entire reason
for building a second implementation, and the corpus gains a vector either way.

---

*Released under the UNLICENSE and dedicated to the public domain.*
