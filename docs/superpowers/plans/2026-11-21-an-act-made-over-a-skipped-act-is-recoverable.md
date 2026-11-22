# An Act Made Over a Skipped Act Is Recoverable: Implementation Plan (register item 323; errata G75)

**Slug:** `an-act-made-over-a-skipped-act-is-recoverable`. Branch: `undone-key-act`. Task records: `.superpowers/sdd/2026-11-21-an-act-made-over-a-skipped-act-is-recoverable/`.

Written 2026-11-21 by `curia-architect` against `origin/main` `27746ffb` (#167, errata G74). Gate: **before the guard's lift**; no owner decision needed. Touches no `.github/`, `db/`, `conformance/`, `rust/`, `src/Curia.Api/`, `src/Curia.Application/` and no wire; the approval record stays `{"version":1,"approved":[]}`. The controller's merge on green.

**Identifiers, re-grepped 2026-11-21.** Errata highest `## G74`, R4 `R4.44`, R11 `R11.100` on `origin/main` (`git show origin/main:curia-whitepaper-ERRATA-AND-ADDENDUM.md | grep -o 'R11\.1[0-9][0-9]\|R11\.\(99\|100\)'`). Register: `origin/main` stops at item 317 and trap 285; `origin/api-suite-in-legs` (#161) holds items 318 to 321 and trap 286; the working tree's held `IMPLEMENTATION_PLAN.md` holds 322 to 328 and traps 287 to 289; `.superpowers/sdd/2026-11-21-unheard-key-act/impl-plan-322.md` files item 329, trap 290 and states "First free: D125, item 330, trap 291, G75, R4.45, R11.101". No other `origin/*` ref holds G75, R4.45, R11.101. So first free: **G75, R11.101, item 330, trap 291**. **R4.45 is not allocated** (the server does not change). Prefixes `UDO1…`, `FUDO1…`, `LV1…`, `CR-a…`: `git grep -nE '\bF?UDO[0-9]'` finds none. The verb name `use-key` is free (`git grep use-key origin/main -- src`: none). **Task 1 and Task 5 re-grep all of it immediately before writing and stop if one is taken.**

## What this is

Item 323 as filed: the fold decides a row against the honoured prefix (`LifecycleFold.cs` `:233`, `:264`: `statement.Prev != _previous` is `broken-link`). An act A the Forum skipped (answered `not-honoured`, recorded not in force) is invisible to a later act B, which links from the last honoured entry P. If the fold later honours A (a defect fixed, the case G73 was written for), A has the lower index and `prev` P, B also names P, so B is skipped by R6.64's own rule. **That rule is right and does not change.** What breaks is the owner: the profile signs with B's key K2, which the Forum no longer binds, so every authenticated verb fails at the token with `invalid_client`. The recovery G73 prints cannot run: `NOW IN FORCE` says "end this one and rotate again" and both verbs need a token signed by K2. **The keys are not lost, the identity is locked out:** the Forum binds K0 (the key B was authorized by; a rotation does not end it) and K1 (A's key, kept by the profile as `RotationInForce: false`), and the profile holds both private halves (`keys/`). Nothing today lets the profile sign with either, and no word says why the token fails beyond the footnote `ForeignAfterAsync` already prints (`SKIPPED: this profile's ... at log index b ... what it did is not in force`).

**Not measured, and Task 0 does it first (the register's own caveat, still true):** no run has changed the fold over a later act. This plan is derived from `LifecycleFold.cs`, `LifecycleWatch.cs`, `KeysReport.cs`, `ProfileStore.cs`, `Program.cs` `ForeignAfterAsync` read at `origin/main`. Every "red on the tree" below is a prediction.

## The design question, decided

**Prevention is not available and not wanted.** (a) Making the fold stable is the fold: a skipped row moves nothing (R6.64), and a deploy that changes the fold changes every decision; an epoch or fold-version would be a new frozen format for a case that needs a defect to arise. (b) Refusing `rotate`/`retire` while an act is not in force was ruled out in 290 (ruling 8: an owner may need to rotate a compromised key now, and the declaration is not theirs to make). (c) The R11.98 words already say to report before acting again. **So the fix is recovery from the lockout, client-side only.** The Forum and `curia-testis` already agree on B's fate; there is nothing for them to learn.

**Three parts, all in `Curia.Client`, `Curia.Client.Cli`, `Curia.Mcp`:**

1. **Name the state, wherever the owner meets it.** A signing key whose kid the key set does not serve `active` (unbound, retired, compromised, absent) is `SIGNING KEY NOT LIVE`. `curia keys` and `curia_keys` print one line for it (exit 6), `ForeignAfterAsync` prints it after an `invalid_client`/`token-key-not-live`/`keys/retired` refusal (the push exists; this extends it), and each lists the **held keys the Forum does bind live with the same public half** (kid and where bound), and the command. `NOW IN FORCE`'s "end this one and rotate again" is replaced by the same line when the signing key is not live, since that remedy cannot run.
2. **One verb that makes the profile truthful and makes no act: `curia use-key --agent <slug> --kid <kid>`.** Preconditions, decided: the kid is a key the profile holds with its private half readable (a retained key, a kept not-in-force key, or an external signer's command) **and** the key set serves that kid `active` with `kty`, `crv`, `x`, `y` equal to the held key's (R11.99's comparison, reused through `KeyActSettling.Holds`: a kid alone could be a stranger's) **and** the profile's current signing key is not live. It reads the key set (anonymous, public), signs nothing, sends nothing, appends no row, requests no token. It writes `identity.json`: the chosen key becomes the signing key (its file becomes `signing-key.pem`, the previous signing key moves to `keys/`), the cached token is deleted, any act of the profile the key set does not hold at its index (B) is recorded **not in force** (`InForce: false`; its generated key kept as `RotationInForce: false`, 290's state, so nothing is deleted), and a kept not-in-force rotation whose key is chosen (A) is recorded in force at the key set's index, as `SettleRotation` does for a pending act. Refusals: key not held or not matching the key set: exit 6, nothing written (a check ran and did not hold); key set unreadable: exit 8; signing key live (nothing to do): exit 1 with the words, nothing written; usage: exit 1. Done: exit 0.
3. **The words are gated on what the reader would do and why** (trap 289): a cold read, below.

**Why a verb, and why this one.** An agent that cannot get a token cannot run `rotate`, `retire` or any write; the only artifact it still has is its own files. A recovery that needs hand edits of `identity.json` is no recovery. Adopting K1 (the highest-index rotation the Forum honours) leaves the chain as the Forum sees it and ends 290's `NOW IN FORCE` line; adopting K0 is allowed and leaves that line standing, correctly. **Item 326 (adopt a kept key and retire the old one in one act) stays open:** that signs, and this verb does not. The precondition "the signing key is not live" is what keeps `use-key` from becoming 326 by the back door (an owner moving between two live keys is making an R4.39 decision this verb does not take).

**Considered and not chosen.** (i) `curia keys` repairs the profile (a read that mutates; 290 ruling 15, 322's FNHR20). (ii) `curia rotate` recovers (it needs a token). (iii) A new Forum route that authenticates by a retained key (a second door onto R5; the key set is already the Forum's word and the client can read it). (iv) Remembering B's `prev` in the profile to warn before B is made (a warning an agent that must rotate now cannot act on; B is right when made).

**Capability monotonicity and zero trust, checked.** The verb widens no capability: it can only select a key the Forum's own key set serves live with identical public bytes, which the profile already held. A stranger who rewrites `identity.json` gains nothing the key files did not already give. The key set is public and anonymous; trusting its `active` status for a choice of *which of the owner's own keys to sign with* costs nothing when wrong (the token endpoint decides: a wrong choice is `invalid_client` again).

## The four states

| Requirement | Specified | Implemented | Discharged | Falsified |
|---|---|---|---|---|
| R11.101 (new): `SIGNING KEY NOT LIVE` line, its exit, its held-key list, the refusal footnote | no (Task 1 pastes) | no | no | no |
| R11.101 (new): `curia use-key` | no | no | no | no |
| R11.96 (add.) (rev.): `NOW IN FORCE` rotation words do not offer a remedy that cannot run | no | no | NHR's pages stay green (control); the new words are UDO's oracle | no |
| R6.64 (`broken-link`, one fork, first in log order wins) | yes | yes (C# `LifecycleFold`, Rust `lifecycle.rs`) | **partly**: `conformance/lifecycle/broken-link` pins a link to a non-honoured *predecessor*; Task 0 M2 checks whether any vector has two rows naming one `prev` with the earlier honoured; if none, one vector is proposed (data, both implementations, not required by this PR) | no |

## Rulings (decided, not asked)

1. **No change to the fold, the wire, `curia-testis` or `conformance/`.** The scene is R6.64 working. Rust and the vectors do not move; the differential gate is expected green and recorded.
2. **`curia use-key` signs nothing, requests no token, makes no act.** A write to the profile only.
3. **The oracle for "live" is the key set's served key (public bytes), never the profile.** For `use-key`'s probes, the oracle for "the Forum binds it" is the Forum's `events` rows or the double's scripted key set, a different artifact than the profile.
4. **`curia keys` writes nothing** (290 ruling 15). `ForeignAfterAsync` reads one public GET it already makes.
5. **`curia-mcp` moves to 0.12.3** (`curia_keys` gains a line kind and its description a sentence; no tool added, no schema change). No MCP tool runs `use-key`: item 325's shape, said in the description. Task 0 M6 lists every `0\.12\.2` pin (`ToolCatalogue.cs:44`, README `:1280`, `:1295`, tests).
6. **A cold read is required; a live run is required.**

## The errata entry (G75), shape (Task 1 writes the final words from the ones the cold read will gate)

Paste after G74 at the end of Part G, status "specified; not built"; re-read first and stop if `G75` or `R11.101` is taken. **Location:** R6.64, R11.98, R11.96 (add.), R4.39, R11.99 (settling), G73 non-property (c), item 323. **How it surfaced:** G73's Task 4 (the nearest observation: an honest rotation under a skip is skipped with its predecessor and the profile then holds an unknown signing key, `401 invalid_client`). **Class:** a remedy printed for a state in which it cannot run; a rule whose conclusion is right and whose consequence for the owner is a lockout nobody names.

```
**R11.101** When the key set the Forum serves does not serve the profile's signing kid `active`, `curia keys` and `curia_keys` SHALL print `SIGNING KEY NOT LIVE`, naming the kid, the status the Forum serves for it (a display literal), and each key this profile holds whose kid the key set serves `active` with the held key's `kty`, `crv`, `x` and `y`, and `curia keys` SHALL exit 6; a client that reads the key set after `invalid_client`, `curia/authn/token-key-not-live` or `curia/keys/retired` (R4.39) SHALL print the same line. `curia use-key --agent <slug> --kid <kid>` SHALL, only when the profile's signing key is so not live and the named kid is held with its private half and so served, make that key the one the profile signs with, delete the cached token, record each act of the profile that the key set does not hold at its index as not in force with its key kept, and record a kept rotation whose key is chosen as in force at the index the key set names; it SHALL sign nothing, request no token, send nothing and append no row. It SHALL refuse, writing nothing, otherwise (exit 6 for a key not held or not so served, 8 for an unreadable key set, 1 for a signing key that is live). The `NOW IN FORCE` rotation line SHALL NOT offer to end the key and rotate again while the signing key is not live. `curia-mcp` is 0.12.3. The reason: a rotation made over a row the Forum skipped is skipped in its turn when the Forum later acts on that row (R6.64), so the owner holds keys the Forum binds and signs with one it does not; every verb needs a token that key cannot sign, so the remedy G73 prints cannot run, and the profile's own files are the one thing left to act on.
```

Non-properties to state: (a) a profile that signs with a key the Forum serves `active` is not touched, whatever else is wrong; (b) when the Forum binds none of the held keys, `use-key` has nothing to adopt and the line says so (there is no re-enrolment of a held identity: R5/G14); (c) 326's general adoption is open; (d) no MCP tool runs the command (item 325); (e) an act made after a skipped act and before the fold changes is right when made and is undone by the fold, not by the client.

Index rows (Task 2 first step): `R11.101`, `R11.96 (add.) (rev.)`.

## Probes and predicted reds (not run)

Every words probe reads its words from the errata at test time (`NotInForceReport`'s pattern). "Control" = green on the unmodified tree by design.

- **UDO1** *(Client, `KeyActDouble` key sets before and after the fold change)* the scene: profile with A not in force (K1 kept, index a), B in force (K2, index b, signing). Key set after: K0, K1 `active`, K2 `unbound`, `curia_skipped` lists b `broken-link`, lifecycle holds a. `curia keys`: `SIGNING KEY NOT LIVE` naming K2, listing K0 and K1 (not K2), exit 6, **byte-identical profile**. Red: `Expected: SIGNING KEY NOT LIVE  Actual: SKIPPED line and NOW IN FORCE ... "end this one and rotate again"`.
- **UDO2** the refusal footnote: a token refusal `invalid_client` in the same scene prints the line after the refusal; a refusal of another type (`400`) prints none (control).
- **UDO3** *(control, vacuity row)* a profile whose signing key the key set serves `active` and that holds a kept K1: no line, exit as 290 (`NOW IN FORCE`, 6) with 290's words unchanged (NHR goldens unedited). Green on the base for the wrong reason (the line does not exist); carries information through FUDO2.
- **UDO4** `use-key --kid K1`: exit 0, `signing-key.pem` equals K1's file, `kid` K1, `token.json` deleted, B recorded `in_force: false` with K2 kept under `keys/` at 0600, A recorded in force at a; **the double saw no POST and no token request** (the oracle for "signs nothing"); then `curia keys` exits 0 except for no stranger. `--kid K0`: exit 0, A still `NOW IN FORCE` with the remedy now runnable.
- **UDO5** refusals, each byte-identical: kid not held; kid held and served `active` with a **different** public half (the kid-reuse case, red for a kid-only reading); kid held but served `retired`/`compromised`; signing key live (exit 1); key set unreadable (exit 8).
- **UDO6** *(Api, in process, real client and a Forum whose fold is switched between two requests through a test-only seam over the **same** store; Task 0 M1 decides the seam, `FailReadsAfterFirstAppend`'s sibling)* the full scene end to end: A answered `not-honoured`, B `201`, fold changed, token `invalid_client`, `curia keys`, `use-key K1`, then an ordinary post verifies under K1. Oracle for what the Forum binds: the store's `events`, never the profile.
- **UDO7** profile members round-trip and a 290/322 profile renders byte-identically (goldens unedited); **UDO8** `curia help use-key`, the `curia keys` clause and the `NOW IN FORCE` words equal the errata's, read at test time; **UDO9** (`Curia.Mcp.Tests`) `curia_keys` equals `curia keys` byte for byte for the new line, `initialize` reports `0.12.3`.

## Falsification rows (the first form is the harm)

Patch in the tree, run the named probes, restore from a kept copy by plain copy (mtime), `--no-incremental`, run unpatched once; record `red` / `masked` / `green` / `red, for another reason`.

| # | Patch | Must go red | First form |
|---|---|---|---|
| FUDO1 | the line is printed only when the status is `unbound` | UDO1 (retired/compromised row) | a retired signing key reads as clean |
| FUDO2 | the line is never printed after a refusal | UDO2, UDO3 split | the owner meets only `invalid_client` |
| FUDO3 | `use-key` compares the kid only | UDO5 | a stranger's key bound at a held kid is adopted |
| FUDO4 | `use-key` proceeds when the signing key is live | UDO5 | a verb that moves an owner between two live keys (326 by the back door) |
| FUDO5 | `use-key` requests a token or signs | UDO4 | the recovery needs the thing it recovers |
| FUDO6 | B is left `in_force: true` | UDO4, then `curia keys` | the profile still says B is the Forum's |
| FUDO7 | the old signing key's file is deleted | UDO4 | a key nobody holds, again |
| FUDO8 | `curia keys` runs the adoption | UDO1 (byte-identical) | a read that mutates |
| FUDO9 | `NOW IN FORCE` keeps "end this one and rotate again" when the signing key is not live | UDO1 | a remedy that cannot run |
| FUDO10 | `curia_keys` omits the line | UDO9 | two readers, two stories |

Record counts by outcome; no control red; unpatched run green after each.

## Tasks

**Task 0: operate the unmodified tree** *(Sonnet implementer; controller reads)*. Export of `origin/main`, no tracked file changed; `source` the session's `env.sh` (the dispatch gives its path). **M1** build the scene once for real: two Forum builds over one throwaway database (build 1 seeded to skip a genuine rotation at the link step `:233`, per 290's Task 4 patch; build 2 unmodified): `curia rotate` (exit 6, not in force), `curia rotate` again (B, `201`), restart under build 2, then `curia keys` and any authenticated verb. Record the printed words, B's fate in `curia_skipped`, and the token answer. **If B is honoured or the token is granted, the plan stops.** Also decide the seam for UDO6. **M2** `conformance/lifecycle`: is there a vector with two rows naming one `prev`? Does `lifecycle.rs` decide it as `LifecycleFold`? **M3** the key set's `status` values for K2 in that scene (`unbound`? absent?), and `keys[]` members. **M4** `ForeignAfterAsync`'s three triggers: which refusal type the scene yields. **M5** readers of `AgentProfile.Acts`/`Retained` that `use-key` must keep consistent (`whoami`, `KeysReport`, `LifecycleWatch`). **M6** every `0\.12\.2` pin, `ci.yml` pins, `preflight.py --list`. **M7** `curia --help` and `curia keys --help` verbatim (not `curia help <verb>`), and the CLI's exit table for the refusal rows. Stop rules: M1 contradicts the scene; M3 serves K2 `active`; M4's refusal triggers no footnote and has no other hook (then the footnote is a new trigger and Task 2 says so).

**Task 1: errata and probes, red first** *(Sonnet; controller reviews each red text)*. Re-read, paste G75, write UDO1 to UDO9 against the unmodified code and see each predicted red by its own assertion. Probes red and recorded; the fix is a second brief after the reds are read.

**Task 2: the fix** *(Sonnet; controller gates)*. Index rows; the line (`KeysReport`, `LifecycleLineWords`); the footnote (`ForeignAfterAsync`); `ProfileStore.AdoptKey` (one write of `identity.json`, key files in place before the record names them, so a crash leaves an orphan file and never a record whose key is gone: `BeginRotation`'s rule); `curia use-key` and its help, `Help.cs`'s `keys` clause; `NOW IN FORCE` rotation words; `curia-mcp` 0.12.3 and pins. Build 0 warnings; UDO green; `Curia.Client.Tests`, `Curia.Mcp.Tests`, `Curia.Application.Tests` (Credentials), the Api classes of Task 0's set, Architecture in Release and Debug; **290's and 322's probes (NHR1 to NHR15c, UKA1 to UKA12) pass unedited**; any other test red is a stop.

**Task 3: falsification FUDO1 to FUDO10**, one row at a time, never beside Task 4.

**Task 4: live run** *(Sonnet operates; controller reads and decides)*. Warranted: the harm needs a fold that changes under a running Forum and a real token refusal. Throwaway databases `curia_item323_*`; exports `c` (`main`) and `t` (tip), approval record lifted in the exports only; no hostname, user or path in a record. **LV1** M1's scene on `c`: locked out, the words as they are (the harm). **LV2** on `t`: `curia keys` and the refusal footnote print the line; `curia use-key --kid K1` exit 0 with `events` count unchanged (`psql`, a different artifact) and no token request in the Forum's log; an ordinary post then verifies. **LV3** `use-key --kid K0` and a kid-reuse refusal. **LV4** an honest rotation on both builds: stdout, exit, profile equal. **LV5** `curia-mcp`: `curia_keys` equals `curia keys`; `initialize` 0.12.3.

**Task 4b: cold read** *(controller writes prompts; fresh Sonnet readers, none told what is tested, three a page)*. **Gates are what the reader would DO and why** (trap 289), and the page is `curia keys --help` / the captured output, not a recitation. **CR-a** (the line, after a token refusal): would run `curia use-key` with a kid from the list and says why that key (the Forum binds it, with the same bytes); would not edit `identity.json`; none reads the exit as an attack or the Forum as down. **CR-b** (`use-key`'s result): says nothing was sent or signed and which key now signs. **CR-c** (the rotation `NOW IN FORCE` line, signing key not live): would not run "end this one and rotate again", and says why. **CR-d** (no held key is served): would stop and report to the Forum's operator, not retry. **Controls:** `c`'s pages, recorded, not gated. Two rounds, then stop and report to the owner.

**Task 5: register and docs** *(controller; Sonnet may draft)*. Errata Status line from the run; `curia-mcp` sentence. `IMPLEMENTATION_PLAN.md` only after #161 merges and 322/290's register patches landed: close item 323 in place; update Current work, Next stage and the first-free line (**item 330, trap 291** unless something landed first); file leads; item 326 gains "the lockout case is closed by item 323, the general case is open"; trap 291 proposed: **a recovery printed for a state is a claim that it can run in that state: probe the remedy from the state it names, not from the one the author had** (check 287 to 290 for a prior statement). `CLAUDE.md` one clause; `README.md` one sentence plus the `0.12.2` moves in Task 2. No `ci.yml`, no guard.

**Task 6: PR** *(controller)*. `python3 tools/preflight/preflight.py --api "<new and changed Api classes>"`, CI green, merge on green; stop and report if the core job's class check demands a `ci.yml` entry (the owner's).

## Gates

`dotnet build Curia.sln` (0 warnings); Release build then `dotnet test Curia.sln --no-build --configuration Release`; Debug build then `dotnet test tests/Curia.Architecture.Tests --no-build --configuration Debug`; `dotnet restore Curia.sln --locked-mode`; `check-spec.py` and `falsify-spec-checks.py`; the three `cargo` commands and the differential gate (expected green, recorded; nothing server-side or on the wire changed); `python3 tools/ci/api-legs.py check`; the closed fuzz. `git diff --stat origin/main` shows no `.github/`, `db/`, `conformance/`, `rust/`, `src/Curia.Api/`, `src/Curia.Application/`, `ForumFixture.cs`.

## Open risks

1. **The scene was never produced.** Task 0 M1 is the first thing that can falsify this plan.
2. **`use-key` is a signing-key change by a verb outside `rotate`.** The precondition "signing key not live" and the identical-public-bytes test are what bound it (UDO5, FUDO3, FUDO4).
3. **A key set that serves `active` for a key the token endpoint still refuses** (clock, `jti`) leaves the line silent; the refusal's own words stand.
4. **No MCP-only recovery** (item 325): an agent whose host holds only `curia-mcp` is told the command is for whoever holds a shell.
5. **The plan was not run.** Not opened: `Help.cs` beyond line 194, `LifecycleLineWords.cs`, `KeyActSettling.cs` bodies, `RawStubForum`, `EventStoreFault`, the Api tests' seam. Not run: no build, no suite.

## What the PR body must contain

1. "Touches no `.github/`, no `db/`, no `conformance/`, no `rust/`, no `src/Curia.Api/`, no wire; the approval record is `{"version":1,"approved":[]}`; the guard is not lifted."
2. LV1 beside LV2: the base's lockout and unrunnable remedy; the tip's line, adoption, unchanged `events` count.
3. The four states for R11.101 and `R11.96 (add.) (rev.)`, from the run; falsification counts by outcome.
4. The cold read by page and gate (what the reader would do and why), controls' answers.
5. `curia-mcp` 0.12.2 to 0.12.3.
6. Non-properties (a) to (e), so the PR is not read as closing item 326 or 325.
