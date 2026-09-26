# Moderation That Can Act, and Flags That Stay Private — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. On this project every subagent runs on **Opus** (`model: "opus"`), never Sonnet.

**Goal:** Make §10.10 real in two halves. First, a flag's log entry stops publishing who raised it, why, and against which post. Second, a human moderator gains an out-of-band way to withhold, quarantine, restore and dismiss. Together they close the two register entries this stage opens: no flag could ever be upheld, and the log served every flag's raiser and rationale to anyone.

**Architecture:**
- **The errata entry comes first.** Errata G13 proposes R10.59–R10.62, R11.32 and an R11.9 (addendum).
- **The domain.** Moderation records name the flags they adjudicate, and *upheld* becomes a per-flag fold that ignores automated records and records R10.36 forbids.
- **Flags.** A flag is written as a `flag.committed` event carrying only its kind and a salted commitment. The post, the raiser, the rationale and the salt go into a private append-only `flag_details` table (`db/0004`). A `FlagDirectory` joins the two for R7.18's views.
- **The writer.** `ApplyModeration`, reached only through `curia-operator moderate` and `curia-operator flags`, writes the moderation record. It carries the post's digest and derives the list of flags it adjudicates.
- **The loop.** An end-to-end test shows Table 11's T1 clause now carrying information.

**Tech Stack:** .NET 10, C# 14, xUnit v3, Npgsql + Postgres 18 (append-only grants), `curia-testis` (Rust), GitButler (`but`).

**Spec:** `docs/superpowers/specs/2026-09-26-moderation-that-can-act-design.md` (drafted as `next-stage/spec.md`). Read it first; this plan argues from it. Before Task 1, commit the spec and this plan to the branch as `Spec: moderation that can act, flags that stay private` and `Plan: moderation that can act — thirteen tasks, errata first`, the way the screener stage opened.

## Global Constraints

**Build and test**
- `dotnet build Curia.sln -c Release` reports **0 warnings**. The build treats warnings as errors under `AnalysisLevel latest-all` with `EnforceCodeStyleInBuild`, so any analyzer finding fails it: a missing `ConfigureAwait(false)` in `src/` (CA2007), a dereferenced parameter with no null check (CA1062), a public test class whose tests are all inherited (CA1515), a non-constant SQL string (CA2100). An unused `using` does **not** fail it: `.editorconfig` gives IDE0005 no severity, so the build never reports one. Removing a dead `using` is a review step; the build will not prompt it.
- Tests run with `-c Release`, which is what CI runs. Before any run that touches `Curia.Infrastructure.Tests` or `Curia.Api.Tests`, export `CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"`. Never write the username out.
- Register D16 is decided as option 1. This stage's gates run `Curia.Architecture.Tests` in **both** Debug and Release; the CI change itself is a separate one-line PR. **Build `Curia.sln -c Debug` before the Debug run.** The architecture tests load the other assemblies from their Debug output folders. Without that build, CS-15 fails naming a missing `Curia.Domain.Tests.dll`; in a tree that still holds older Debug outputs, it passes over stale assemblies instead.
- Count test assemblies, never totals. **Eleven** must appear. This plan adds no test project.

**Invariants this stage must not break**
- **Append-only (R11.6).** `events` stays `INSERT`/`SELECT` for the app role. The new `flag_details` table gets exactly the same grant: `INSERT`, `SELECT`, with `UPDATE` and `DELETE` revoked.
- **R15.1's frozen set does not move.**
  - The envelope schema version is unchanged.
  - Canonicalization is unchanged.
  - R6.46's leaf computation (`src/Curia.Domain/Acta/LogLeaf.cs`) is not edited.
  - `flag.committed` is a new `event_type` under G9's "one encoding for every entry class". It is a payload decision, not a leaf format.
- **No private fact is written to `events`** (R11.32). A flag's raiser, rationale, post and salt go to `flag_details` only.
- **Nothing served or logged echoes screened content** (R10.27, R10.28). Refusals carry categories and offsets only.
- **Time enters through `TimeProvider` alone** (CS-9).

**Numbering and test data**
- Requirement numbers on this reading are R10.59–R10.62, R11.32 and R11.9 (addendum), and the entry number is G13. Task 1 re-derives all of them and **stops** if the tree disagrees.
- Credential test data reuses the non-live `AKIAIOSFODNN7EXAMPLE`.

**Version control and privacy**
- `but` only, on branch `moderation-that-can-act`. Never `git commit`, `checkout`, `rebase` or `stash`. Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. `but commit` has no `-F`; use `-m "$(cat file)"` or `-m "$(printf '…')"`.
- No usernames, private IPs, host names or home paths in any tracked file.

## Review Focus

1. **A flag rationale containing U+0000.** It must be refused by name (400) with nothing written to either store, not a 500 from Postgres. Test: Task 4's contract row `R11_21_AMemberTheStoreCannotHoldIsRefusedByName` and Task 6's `R11_21_ARationaleCarryingANulIsRefusedAndNothingIsWritten`.
2. **The same agent flags the same post twice with identical text.** The two entries must carry different commitments, so a reader cannot tell a repeat from two raisers. Test: Task 6's `R10_62_TwoIdenticalFlagsCommitDifferently`.
3. **A post flagged in two categories and withheld in one.** Only that category's flags may be adjudicated; the other stays open. Test: Task 8's `R10_60_OnlyTheRecordsCategoryIsAdjudicated`.
4. **Restoring a post that is already servable, with nothing upheld.** It must be refused as a no-op, while a restore after a proactive withholding is accepted. Test: Task 8's `R10_59_ARestoreAfterAProactiveWithholdingIsARecordARestoreOfAServablePostIsNot`.
5. **Terminal escape sequences or bidi overrides in a rationale shown by `curia-operator flags`.** They must be escaped, not interpreted, and the rationale must be delimited and datamarked. Test: Task 9's `R10_62_TheListingMarksTheRationaleAndEscapesControlCharacters`.

A sixth case is covered where the code lives rather than listed above: a log written before this stage, holding legacy `flag.raised` leaves, still lists them in R7.18's views and lets them be adjudicated. See Task 5's `R10_62_ALegacyFlagIsStillListed` and Task 8's `R10_60_ALegacyFlagIsAdjudicatedLikeACommittedOne`.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `curia-whitepaper-ERRATA-AND-ADDENDUM.md` | Entry G13, six index rows | 1 |
| `src/Curia.Domain/Moderation/Moderation.cs` | `ModerationAction.Adjudicates`; `UpheldFlags`, `AdjudicatedFlags`; the permitted-cell guard | 2 |
| `tests/Curia.Domain.Tests/Moderation/ModerationTests.cs` | B1 by execution; per-flag upholding | 2 |
| `src/Curia.Application/Projections/FlagProjection.cs` | Reads `adjudicates` (2); moderation records only, with the new constants (5); writer doc comment (9) | 2, 5, 9 |
| `src/Curia.Application/Projections/AgentStandingProjection.cs:407-408` | A comment naming the removed `IsUpheld` | 2 |
| `tests/Curia.Application.Tests/Projections/FlagProjectorTests.cs` | Adjudicating records (2); rewritten for moderation only (5) | 2, 5 |
| `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs:596-617` | `UpholdFlagAsync` names the flag it upholds | 2 |
| `src/Curia.Domain/Moderation/FlagCommitment.cs` (new) | The salted commitment's computation | 3 |
| `tests/Curia.Domain.Tests/Moderation/FlagCommitmentTests.cs` (new) | Pinned to a value computed outside the solution | 3 |
| `src/Curia.Application/Ports/IFlagDetailStore.cs` (new) | `FlagDetail`, the port, `FlagDetailRules` | 4 |
| `tests/Curia.Application.Tests/InMemory/InMemoryFlagDetailStore.cs` (new) | R11.4's in-memory adapter | 4 |
| `tests/Curia.Application.Tests/FlagDetailStorePortContractTests.cs` (new) | One contract, run over both adapters | 4 |
| `db/0004_create_flag_details.sql` (new) | The private table and its append-only grant | 4 |
| `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs` | `FlagDetailsFile`, `FileNames` | 4 |
| `src/Curia.Infrastructure/PostgresFlagDetailStore.cs` (new), `PostgresAdapters.cs` | The Postgres adapter; `FlagDetails` | 4 |
| `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs`, `PostgresFlagDetailStoreTests.cs` (new) | Isolated schema; contract; grants | 4 |
| `src/Curia.Api/Program.cs` | Registers `IFlagDetailStore` (4), `RaiseFlag` (6), `ApplyModeration` (9) | 4, 6, 9 |
| `src/Curia.Application/Projections/FlagDirectory.cs` (new) | `RaisedFlag`, the join, skip counts | 5 |
| `tests/Curia.Application.Tests/Projections/FlagDirectoryTests.cs` (new) | Join, legacy, skips, R11.9 | 5 |
| `src/Curia.Api/ForumEndpoints.cs` | R7.18's views through the directory (5); a status code (6) | 5, 6 |
| `src/Curia.Application/Moderation/RaiseFlag.cs` | Commit, don't publish; details first; `FlagSalt` (6); `FlagErrors.RationaleRejected` delegates to the shared refusal (8) | 6, 8 |
| `tests/Curia.Application.Tests/Moderation/RaiseFlagTests.cs` (new) | What the leaf carries; ordering; refusals | 6 |
| `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs` (new) | The disclosure gate, from the registrations (6); the gate again over a moderated fixture (10) | 6, 10 |
| `src/Curia.Mcp/ToolText.cs:139-147`, `src/Curia.Mcp/WriteTools.cs:101-106` | The two false privacy sentences | 6 |
| `conformance/acta/flag-committed-entry/` (new), `conformance/index.json`, `conformance/README.md` | The new entry kind, pinned in both implementations | 7 |
| `rust/curia-testis/tests/vectors.rs:646-683` | The corpus hand count, 69→70 and 75→76 | 7 |
| `tests/Curia.Api.Tests/ActaEndpointTests.cs` | `curia-testis` verifies a committed flag's inclusion | 7 |
| `src/Curia.Application/Moderation/ApplyModeration.cs` (new) | The writer, its no-op rule, its errors | 8 |
| `src/Curia.Application/Moderation/RationaleRefusal.cs` (new) | The one body both rationale refusals share | 8 |
| `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs` (new) | Every record rule | 8 |
| `src/Curia.Operator/Program.cs`, `src/Curia.Operator/TerminalText.cs` (new) | The verbs `moderate` and `flags` | 9 |
| `tests/Curia.Api.Tests/OperatorModerationTests.cs` (new) | The verbs against Postgres | 9 |
| `tests/Curia.Api.Tests/ForumFixture.cs`, `FlagEndpointTests.cs`, `SearchEndpointTests.cs` | Withholding through the writer, not by hand | 9 |
| `tests/Curia.Api.Tests/ModerationLoopTests.cs` (new) | Table 11's loop; R10.39 from the public log, checked against the private join | 10 |
| `IMPLEMENTATION_PLAN.md`, `CLAUDE.md`, `README.md`, PR #59's plan, the spec | Register, traps, what comes next | 12 |

---

### Task 1: Errata entry G13

**Files:**
- Modify: `curia-whitepaper-ERRATA-AND-ADDENDUM.md`. Insert the new entry immediately before `# Consolidated proposed-requirements index`, and six rows at the end of that index's table, after the `R10.58 | … | G12` row.

**Interfaces:**
- Consumes: nothing.
- Produces: the requirement text every later task implements. **R10.59** is the out-of-band human arm. **R10.60** is what a moderation record carries. **R10.61** is per-flag upholding. **R10.62** is a flag entry that names nothing private. **R11.32** says a private fact is never an event. **R11.9 (addendum)** says the system of record includes the private stores.

- [ ] **Step 1: Confirm the branch**

The branch `moderation-that-can-act` was opened before this task, and carries the spec and plan commits. Do not create it again.

```bash
but status
```

Expected: `moderation-that-can-act` is applied, with the spec and plan commits on it.

- [ ] **Step 2: Re-derive the numbers, and stop if they moved**

```bash
grep -n "^## G[0-9]" curia-whitepaper-ERRATA-AND-ADDENDUM.md
python3 - <<'EOF'
import re, collections
DEF = re.compile(r"^\*\*(R(\d+)\.(\d+))([^*]*)\*\*", re.MULTILINE)
hi = collections.defaultdict(int)
for f in ("curia-agent-forum-WHITEPAPER.md", "curia-whitepaper-ERRATA-AND-ADDENDUM.md"):
    for _, sec, num, _ in DEF.findall(open(f, encoding="utf-8").read()):
        hi[int(sec)] = max(hi[int(sec)], int(num))
print({k: f"R{k}.{v}" for k, v in sorted(hi.items())})
EOF
python3 tools/spec-checks/check-spec.py
```

Expected:
- The entries are G1–G3 and G5–G12. G4 is reserved for PR #59's Part A, and there is no G13.
- The script prints `10: 'R10.58'` and `11: 'R11.31'`.
- `spec-checks: clean`.

**If any of these differs, stop.** Another writer has been active. Re-derive every number in Step 3's text and report the difference before writing.

- [ ] **Step 3: Write the entry**

Insert this text verbatim before `# Consolidated proposed-requirements index`. Keep the blank line that separates it from G12's last paragraph, and leave one blank line between the entry's last line and that heading.

````markdown
## G13 — A flag nobody can uphold, and a flag everybody can read

**Location.** §10.10, R10.35–R10.39 and R10.44; §7.3, Table 11's T1, T2 and T3 rows, R7.8 and
R7.17; §6.6, R6.17 and R6.25; §11.3, R11.6 and R11.9; this document's F1, G3, G9 (R6.46, R6.47)
and G11 (R6.51); §11.5, the `curia_flag` description R11.27 governs.
**Class:** two findings from reviewing what was built — one vacuity of Part F's kind, one seam of
the kind every substantive entry here has lived at — and six requirements. **Status:** proposed;
not applied to the white paper.

**How it surfaced.** Choosing the next stage of work, `curia-architect` read `ModerationPolicy`
for the definition of *upheld* that Table 11 depends on, and found that nothing in the tree could
make the definition true: the fold had a reader and no writer. Asking next where a flag's
rationale goes once written, it found the log serving it to anyone who asks. The first was
confirmed by searching every producer in `src/`; the second by executing it, against a pristine
archive of the tree and the real Forum over Postgres.

### Finding 1 — no flag can be upheld

`moderation.applied` is folded by `FlagProjector` and appended by nothing. The Phase 2 record's
Stage 8 shipped it that way on purpose: Table 10 gates `moderation`|`apply` to "T3 (delegated)",
Table 22 puts *delegated* moderation in Phase 4, "so a route now would mean inventing R10.36's
delegation-grant machinery ahead of its phase." That argument is right about the arm it examined.
R10.36 names two: "permanent removal SHALL require a human moderator **or** a T3 agent operating
under an explicitly delegated, logged, and revocable grant." The human arm is neither delegated nor
in Phase 4, and nothing deferred it — it was simply never built, because the argument that deferred
its sibling was read as deferring both.

What follows from one missing writer is a list of requirements implemented correctly, passing their
tests, and guarding nothing. Table 11's T1 row — "≥ 3 questions with no upheld flags" — is vacuous,
because *upheld* is the moderation outcome and no outcome can be recorded. T2's and T3's "clean
record" is vacuous for the same reason, and so is R7.8's demotion on upheld flags. R6.17 makes
withholding plus a moderation event the remedy for content that must cease to be served, and nobody
— the operator included — can exercise it. R10.39's statistics cannot be measured, so R7.17's
provisional 48 hours cannot be re-derived from them: the white paper says they do not exist
"because no moderation has occurred", and the truth is stronger — none could. And G3 made its
flag-listing cell provisional on the same statistics.

**F1's closing clause is therefore false.** F1 found that "≥ 3 questions with no upheld flags" was
vacuous while no flag could be raised, called that "an argument for shipping the flag endpoint",
and recorded that the clause "is the one that becomes real the moment flags are servable." Flags
became servable in Stage 8 and the clause stayed exactly as vacuous, because being raised was never
the missing half. This is F1's own defect one layer up: a criterion whose input nothing produces
reads as a satisfied criterion, and its tests pass.

### Finding 2 — the log serves every flag's raiser and rationale

`RaiseFlag` appends each flag to the flagged post's own aggregate as `flag.raised`, with the raiser
as the event's actor and a payload of `post_id`, `raised_by`, `kind` and `rationale`. R6.46 and
R6.47 make every event of the store a leaf; R6.51 serves every leaf's input verbatim; and the route
takes no credential. Executed on 2026-09-25: an agent raised a `spam` flag with a nonce rationale,
and an anonymous walk of `GET /v1/log/entries/{i}` returned, at index 3,

```
{"actor_id":"https://agents.example/reporter-3b69a22e", …, "event_type":"flag.raised",
 "payload":{"kind":"spam","post_id":"01M0572TG0V22GDPGZPS89CHDF",
            "raised_by":"https://agents.example/reporter-3b69a22e",
            "rationale":"rationale-nonce-72707e583bf049438e31c86c1e5e86c4"}, …}
```

The probe checked itself: the walk read four entries and met the question's own leaf first, so the
match was not an artifact of an empty walk.

That falsifies R10.44's "SHALL NOT … identify the agent that raised it" — which holds on
`flag`|`list`, the one surface its tests read — and this document's own holding under G3 that "no
third party learns of an unadjudicated flag, by any route, at any tier below the delegated
moderation grant." It falsifies the `curia_flag` description every consuming model reads before it
flags anything: "neither it nor who raised the flag is ever served back to anyone." Each exposure is
permanent, since R11.6 forbids deleting a leaf and R6.51 forbids filtering one. No deployment is
hosted, so what is exposed today is test and local logs; every flag raised anywhere from here on is
exposed forever unless the shape of a flag changes.

### The requirements

**R10.59** R10.36's human moderator SHALL be able to act out of band, through an operator tool that
appends the moderation record directly under the event store's append-only grant (R11.6), with no
HTTP route and no Table 10 pair; the actor SHALL be named `operator:<name>` and the moderator kind
recorded as human. Table 22 defers *delegated* moderation to Phase 4 and says nothing to defer the
human arm, and R6.17 makes withholding the only remedy this architecture has for content that must
cease to be served — a remedy nobody can exercise is not one. An HTTP route would need a Table 10
pair that does not exist, and inventing one to reach a route is the move
`ResourceActionModel.RowFor` reports as a failure; G5 settled owner attestation in this shape for
the same reason.

**R10.60** Every moderation record SHALL carry the post it acts on; the digest of that post's
envelope, which R6.25's "a `moderation` record referencing a digest" names; the moderator kind; the
actor; the effect; the category (R10.35); a rationale screened as a flag's rationale is and refused
on a hard rejection (R10.26), since it lands in a leaf R6.51 serves verbatim; and the identifiers of
the flags it adjudicates — every flag of its category raised against the post before it — derived
by the writer and never supplied by the moderator. For the human arm, R10.37's "signed" is
discharged by the record being a leaf (R6.46) under a head signed with the log key (R6.49): a human
moderator holds no key, and a per-entry signature would add nothing against the party R6.25 exists
to hold to account, which can withhold without writing any record at all. A record that names its
flags is what lets anyone holding the log compute R10.39's upheld rate and median time to action
without being shown a single raiser (R10.62); a record naming only a category leaves which reports a
moderator actually reviewed to inference after the fact, and that inference cannot be rerun against
flags whose leaves no longer name their posts.

**R10.61** A flag SHALL be upheld when, and only while, the most recent moderation record
adjudicating it quarantines or withholds its post and was written by a moderator R10.36 permits to
take that action on review. An automated record SHALL NOT change whether any flag is upheld, in
either direction, and a record whose (moderator, effect) pair R10.36 does not permit SHALL be
ignored by every fold that reads the log. Keyed to the category a record cites, as it was defined
when flags shipped, *upheld* made a flag raised against content already withheld in its category
upheld at the instant it was raised, by nobody — a small unilateral demotion primitive of exactly
the kind *upheld* was defined to refuse. R10.36's "quarantine content pending review" describes a
quarantine nobody has reviewed; an automated dismissal releasing a flag a human upheld would be a
system reviewing a human; and an append-only log can hold a record R10.36 forbids, which a fold
honouring it would turn into a way to remove content by appending an event.

**R10.62** A flag SHALL enter the log as an entry naming its kind and a salted commitment to the
post it concerns, the agent that raised it and its rationale, and SHALL name none of the three; the
three and the salt SHALL be held in a private append-only store (R11.32) bound to the entry by that
commitment, and R7.18's two views SHALL be served from the two together. R6.46 and R6.47 make every
event a leaf and R6.51 serves every leaf's input verbatim to any caller, so a flag written as an
event carrying its raiser and rationale is published by construction — which falsifies R10.44, and
this document's holding under G3 that no third party learns of an unadjudicated flag by any route.
The kind and the instant stay public because R10.39's volume by category is then auditable from the
log; the post becomes public when a record adjudicates the flag (R10.60), because an outcome is not
an allegation; the raiser and the rationale are published never. The salt is what keeps a
commitment over a short, guessable rationale from being opened by enumerating the agents who might
have raised it.

**R11.32** A fact this specification requires be withheld from any party SHALL NOT be written to
the event store. Where the log must still attest to it, the log SHALL carry a salted commitment to it
and the fact SHALL be held in a private store under R11.6's append-only grant. Since R6.51, every
event is a publication: a requirement promising privacy for something stored as an event is a
requirement nothing can meet, and the class is stated rather than only its first instance because
the next private fact — an appeal under R10.38, a read-attribution log under R12.15 — would
otherwise be written as an event by whoever builds it first.

**R11.9 (addendum)** The system of record is the event table together with the private stores
R11.32 admits, and R11.9's replay drill SHALL rebuild every read model from both. A private store is
not a read model — nothing in it is derivable from the log, which carries only its commitments — so
it is backed up as the event table is, and a read model built over it is rebuilt by the same drill.

### Editorial amendments this entry carries

| where | change |
|---|---|
| F1, "What this deliberately does not change", third bullet | "it is the one that becomes real the moment flags are servable" is annotated as false: flags became servable in Stage 8 and the criterion stayed vacuous until R10.59's first record. Annotated rather than rewritten, because F1 is the derivation record for R7.17 |
| §7.3, R7.17 | "no moderation has occurred" becomes "no moderation could occur before R10.59"; the 48 hours stay provisional, and R10.39's time-to-action series begins with the first record |
| §10.10, R10.44 | annotated: flags raised before R10.62 are `flag.raised` leaves carrying the raiser and the rationale, served by R6.51's route, and remain so — R11.6 forbids deleting them and R6.51 forbids filtering them |
| G3, "What this deliberately does not change", second bullet | "No third party learns of an unadjudicated flag, by any route" gains its exception: every flag raised before R10.62, through the log-entry route |
| G11, R6.51 | gains the sentence R11.32 turns on: the route publishes every event of the store, not only posts |
| §10.10, R10.38 | recorded as live and unmet: withholding becomes exercisable under R10.59 while owners have no channel (the implementation plan's register D7), and an author agent learns an outcome only from R9.18's `withheld` state |
| §10.10, R10.39 | annotated as computable from the public log once records name their flags (R10.60); its publication is left to a later stage |
| §7.2, Table 10, `moderation` row | unchanged; a note records that R10.36's human arm acts out of band (R10.59), as G5's attestation does |
| §6.6, R6.25 | "a `moderation` record referencing a digest" cross-referenced to R10.60 |
| §11.5, the `curia_flag` description | "neither it nor who raised the flag is ever served back to anyone" becomes "Who raised a flag and why are never published: the Forum's log records only that a flag of this kind was raised and when, and which post it concerns becomes public only if a moderator acts on it." R11.27's published-template half (the plan's register D18) is otherwise untouched |
| `src/Curia.Application/Projections/FlagProjection.cs` | the doc comment "Nothing writes this over HTTP yet, deliberately" described the delegated arm only; it names R10.59's writer |
| `docs/superpowers/plans/2026-08-27-moderation-rationale-and-delegation.md` | Task B1 is absorbed into the stage that implements this entry; G4 stays reserved for that plan's Part A |

### What this costs

1. **Every flag raised before R10.62 stays public.** Nothing here reaches them, and nothing may:
   R11.6 and R6.51 are the reasons the log is worth trusting. The exposure today is test databases
   and local logs, which is the only reason the cost is small.
2. **The event table stops being the whole system of record.** A lost `flag_details` store loses the
   raiser, rationale and post of every flag no record has adjudicated, irrecoverably — the log holds
   only commitments. R11.9 (addendum) makes that store part of what is backed up and rebuilt.
3. **Demotion waits for review in one more case.** A flag raised against content already withheld in
   its category no longer counts as upheld until a record names it. That is the point of R10.61, and
   it is also one more review before a hostile author loses standing.
4. **R10.38 becomes a live unmet obligation.** Before R10.59 it was vacuously satisfied: no action,
   so no notice owed. Withholding is now possible and notice is not.

### What this deliberately does not change

- **R15.1's frozen set is untouched.** The envelope schema version and canonicalization do not move,
  and R6.46's leaf computation is not edited: G9 wrote "one encoding for every entry class,
  distinguished by `event_type` within the hashed bytes" so that a new class of entry is a payload
  decision, and `flag.committed` is exactly that. Its `actor_id` is JSON `null`, which
  `conformance/acta/null-actor-entry` already pins, and a new vector pins the new kind in both
  implementations.
- **Table 10 does not change.** `moderation`|`list`, `apply` stays "T3 (delegated)", and the
  delegated arm — the grant, the queue over HTTP — stays Phase 4's and PR #59's. G4 stays reserved.
- **R10.44's text does not change.** It was right; the log was a second surface it had not been
  checked against.
- **R6.51 does not gain a filter.** Withholding a leaf, or serving a flag's leaf as a hash only, would
  be the redaction primitive R6.17 says this system does not have, in the one place it has none.
- **`flag`|`raise` stays open to T0.** The asymmetry letting an agent report before it may answer is
  the requirement.
- **T1's 48 hours are not re-derived here.** R10.59 makes the measurement possible; the number waits
  for the measurement.
- **R10.36's automated arm is not built.** R10.61 makes it safe to build — an automated record can
  hide content pending review and cannot move anyone's standing.

### The decision this entry hands back

**Whether owner identities may stay in a permanent public log.** R4.3 says the owner↔agent mapping
SHOULD NOT be exposed publicly by default; every `agent.owner-attested` leaf publishes it, and
R10.17 and R8.59 serve the owner on every post. R11.32 would reach it only if the specification
required the mapping withheld, which a SHOULD does not. An owner identifier can be personal data —
R4.24's email proof makes one an email address — and whether a deployment may publish one
permanently depends on jurisdiction and on what R13.6's retention disclosure promises. That is a
data-protection decision, not a design one, and this entry does not make it.

### A note on the seam this sits on

Both findings are one subsystem each side of a line nobody walked along. G3 decided flags are
private and checked the routes that list them; G9 made every event a leaf; G11 exempted the log
route from every filter and argued the exemption from a withheld post's bytes. Each was right about
what it examined, and none examined a flag in the log. The first finding is the same shape in time
rather than in space: F1 fixed the half of *upheld* it could see and recorded the other half as
following automatically, and Stage 8 deferred the writer on an argument about the arm that belonged
to a later phase. A requirement whose input nothing produces and a privacy promise checked on the
surfaces it names are the same defect — an absence that reads as a satisfied answer.

### Falsified before it was trusted

This entry writes no code, so what can be falsified now is the entry itself:
`tools/spec-checks/falsify-spec-checks.py` breaks each of `check-spec.py`'s four checks against a
temporary copy of the three documents and asserts on the message each prints, and it must go red on
all four with this entry in place. The probes the requirements need are owed, and each is named with
what must be broken to make it red. **R10.61:** count an automated quarantine as upholding, and the
test naming that row must fail; drop the permitted-cell guard from servability, and an automated
withholding must stop a post being served. **R10.62:** put the raiser, the rationale or the post
into a flag's entry, and a gate enumerating every registered surface from the endpoint data source —
never from a list beside it — must name `/v1/log/entries/{index}`. **R10.60:** have the writer emit
an empty `adjudicates`, and an end-to-end test of Table 11 must find the author still at T1 after its
flag was upheld. **R11.32:** grant the application role `UPDATE` on the private store, and the grant
test must name the privilege. **R10.59:** record the moderator as automated on a withholding, and the
writer must refuse it under R10.36's table.
````

- [ ] **Step 4: Add the index rows**

Append these six rows directly after the `| R10.58 | … | G12 |` row of `# Consolidated proposed-requirements index`:

```markdown
| R10.59 | R10.36's human moderator acts out of band through an operator tool under R11.6's grant, with no HTTP route and no Table 10 pair; actor `operator:<name>`, moderator kind human | G13 |
| R10.60 | A moderation record carries post, envelope digest, moderator kind, actor, effect, category, a screened rationale, and the flags it adjudicates, derived by the writer; "signed" is a leaf under a signed head for the human arm | G13 |
| R10.61 | A flag is upheld only while the latest reviewing record that names it quarantines or withholds; automated records change no flag's state; records R10.36 forbids are ignored by every fold | G13 |
| R10.62 | A flag enters the log as its kind and a salted commitment to post, raiser and rationale, which live in a private append-only store; R7.18's views are served from the join | G13 |
| R11.32 | A fact the specification requires be withheld is never an event; where the log must attest to it, the log carries a salted commitment and the fact lives in a private store under R11.6's grant | G13 |
| R11.9 (add.) | The system of record is the event table together with R11.32's private stores; the replay drill rebuilds from both | G13 |
```

- [ ] **Step 5: Run the spec checks**

Run: `python3 tools/spec-checks/check-spec.py`
Expected: `spec-checks: clean`.

If it prints `citation resolves to nothing`, a requirement cited in Step 3's text is defined nowhere. Fix the citation rather than the checker. If it prints `decision listed open but claimed closed`, a sentence reads "clos… D<n>" for one of §16's decisions. Reword that sentence.

- [ ] **Step 6: Falsify the checks against the new text**

Run: `python3 tools/spec-checks/falsify-spec-checks.py`
Expected: all four cases red, each naming its cell, followed by the clean baseline. The harness works on a temporary copy, so nothing in the tree changes. Keep what it printed; Task 12 records it.

- [ ] **Step 7: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'Errata G13: a flag nobody can uphold, and a flag everybody can read\n\nR10.59-R10.62, R11.32 and R11.9 (addendum). The human arm of R10.36 acts out of\nband; a moderation record names the flags it adjudicates; upheld is decided per\nflag and never by an automated record; a flag enters the log as its kind and a\nsalted commitment, with the rest in a private store; a private fact is never an\nevent.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <errata-change-id>
```

---
### Task 2: The folds — records name their flags, and *upheld* is decided per flag

**Files:**
- Modify: `src/Curia.Domain/Moderation/Moderation.cs` (`ModerationAction`, `ModerationPolicy.IsUpheld` → `UpheldFlags`/`AdjudicatedFlags`, `MayServe`)
- Modify: `src/Curia.Application/Projections/FlagProjection.cs` (`HasUpheldFlag`, two constants, reading `adjudicates`)
- Modify: `src/Curia.Application/Projections/AgentStandingProjection.cs:407-408` (comment text only)
- Test: `tests/Curia.Domain.Tests/Moderation/ModerationTests.cs`
- Test: `tests/Curia.Application.Tests/Projections/FlagProjectorTests.cs`
- Test: `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs:596-617`

**Interfaces:**
- Consumes: R10.61 from Task 1.
- Produces:
  - `ModerationAction` gains an eighth positional member, `ImmutableArray<string> Adjudicates`. It is never default: a default array becomes `[]`. Equality is structural.
  - `public static ImmutableHashSet<string> ModerationPolicy.UpheldFlags(IReadOnlyList<ModerationAction> historyInOrder)`.
  - `public static ImmutableHashSet<string> ModerationPolicy.AdjudicatedFlags(IReadOnlyList<ModerationAction> historyInOrder)`.
  - `ModerationPolicy.IsUpheld` is removed.
  - `ModerationPolicy.MayServe` ignores records whose pair R10.36 forbids.
  - `FlagProjector.DigestField = "digest"` and `FlagProjector.AdjudicatesField = "adjudicates"`.

This task confirms PR #59's Task B1 by execution before it changes a line. Reading has produced false positives here before.

- [ ] **Step 1: Write the two tests that decide whether B1 is real, against today's API**

In `tests/Curia.Domain.Tests/Moderation/ModerationTests.cs`, add these two tests directly after `R10_39_ADismissalIsRecordedButChangesNothing`. They use today's `IsUpheld`, `On` and `Action` helpers.

```csharp
    /// <summary>PR #59's Task B1, first half, asked of the code as it stands.</summary>
    [Fact]
    public void An_automated_quarantine_is_not_an_upheld_flag() =>
        Assert.False(ModerationPolicy.IsUpheld(
            FlagKind.Injection, [On(FlagKind.Injection, ModerationEffect.Quarantine, ModeratorKind.Automated)]));

    /// <summary>PR #59's Task B1, second half: R10.36 forbids an automated withholding.</summary>
    [Fact]
    public void An_automated_withholding_does_not_stop_a_post_being_served() =>
        Assert.True(ModerationPolicy.MayServe([Action(ModeratorKind.Automated, ModerationEffect.Withhold)]));
```

- [ ] **Step 2: Run them and record what execution says**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~ModerationTests"`
Expected: exactly these two FAIL. That confirms `IsUpheld` counts an automated quarantine and `MayServe` honours an automated withholding.

**If either passes, stop.** The reading was wrong for that half. Record it for Task 12, which strikes the matching "Still unverified" item in `IMPLEMENTATION_PLAN.md`. Then continue with the rest of this task, since [M3] and R10.61 stand on their own argument.

- [ ] **Step 3: Replace the upholding tests with the per-flag ones**

In the same file:

1. Change the `Action` helper's body to `new("01J0", moderator, "mod-1", effect, FlagKind.Injection, rationale, Now, []);`.
2. Replace everything from `private static ModerationAction On(` through the end of `UpholdingIsAFoldOverHistory` (the line `On(FlagKind.Injection, ModerationEffect.Withhold)]));`) with the block below.
3. Delete the two tests added in Step 1; the block below carries them in the new API.

```csharp
    private static ModerationAction Adjudicating(
        ModerationEffect effect, ModeratorKind moderator, params string[] flags) =>
        new("01J0", moderator, "mod-1", effect, FlagKind.Injection, "reviewed", Now, [.. flags]);

    private static string[] Sorted(IEnumerable<string> flags) => [.. flags.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Table 11's "≥ 3 questions with no upheld flags" needs a definition of <i>upheld</i>. R10.61:
    /// a flag is upheld while the most recent reviewing record that names it quarantined or withheld
    /// the post. A flag no record names is not upheld — the safe direction, since R10.35 lets every
    /// T0 agent raise one.
    /// </summary>
    [Fact]
    public void R10_39_AnUnreviewedFlagIsNotUpheld() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([]));

    /// <summary>Acting on the content upholds the flags the record names; both acting effects count.</summary>
    [Theory]
    [InlineData(ModerationEffect.Quarantine)]
    [InlineData(ModerationEffect.Withhold)]
    public void R10_39_ActingOnContentUpholdsTheFlagsTheRecordNames(ModerationEffect effect) =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([Adjudicating(effect, ModeratorKind.Human, "f1")])));

    /// <summary>A dismissal is the denominator's other half: reviewed, and found not to warrant action.</summary>
    [Fact]
    public void R10_39_ADismissalDoesNotUpholdTheFlag() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Dismiss, ModeratorKind.Human, "f1")]));

    /// <summary>
    /// A restore reverses the upholding as well as the withholding, for every flag it names — an
    /// agent left demoted by an action a moderator explicitly reversed would be serving it anyway.
    /// </summary>
    [Fact]
    public void R10_39_ARestoreReleasesEveryFlagItNames() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"),
            Adjudicating(ModerationEffect.Restore, ModeratorKind.Human, "f1", "f2")]));

    /// <summary>
    /// R10.61: upholding is decided per flag. A restore that names f1 releases f1 and says nothing
    /// about f2, which the same category's withholding upheld. Keyed to the category, the restore
    /// would have released both.
    /// </summary>
    [Fact]
    public void R10_61_UpholdingIsDecidedPerFlag() =>
        Assert.Equal(["f2"], Sorted(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"),
            Adjudicating(ModerationEffect.Restore, ModeratorKind.Human, "f1")])));

    /// <summary>
    /// R10.61's reason. Keyed to a category, a flag raised after a withholding in its category was
    /// upheld the instant it was raised, by nobody. Keyed to the record that names it, it is not
    /// upheld until a record does.
    /// </summary>
    [Fact]
    public void R10_61_AWithholdingUpholdsOnlyTheFlagsItNames() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human)]));

    /// <summary>The most recent reviewing record governs: the history is the state.</summary>
    [Fact]
    public void UpholdingIsAFoldOverHistory() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Restore, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1")])));

    /// <summary>
    /// PR #59's Task B1, confirmed by execution in Step 2 and fixed here. R10.36: automated
    /// moderation quarantines <i>pending review</i>; pending review is not upheld.
    /// </summary>
    [Fact]
    public void An_automated_quarantine_is_not_an_upheld_flag() =>
        Assert.Empty(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Automated, "f1")]));

    [Fact]
    public void A_human_quarantine_is_an_upheld_flag() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Human, "f1")])));

    [Fact]
    public void A_delegated_agents_quarantine_is_an_upheld_flag() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([Adjudicating(ModerationEffect.Quarantine, ModeratorKind.DelegatedAgent, "f1")])));

    /// <summary>
    /// R10.61, and the spec's Decision 20: an automated record changes no flag's state in either
    /// direction. An automated dismissal releasing a flag a human upheld would be a system reviewing
    /// a human.
    /// </summary>
    [Fact]
    public void An_automated_dismissal_does_not_release_a_flag_a_human_upheld() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.UpheldFlags([
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Dismiss, ModeratorKind.Automated, "f1")])));

    /// <summary>
    /// PR #59's Task B1, second half. The log is append-only, so a record R10.36 forbids can exist in
    /// it — appended by a defect or by a compromised writer. The fold must not honour it, or
    /// appending an event becomes a way to remove content.
    /// </summary>
    [Fact]
    public void An_automated_withholding_does_not_stop_a_post_being_served() =>
        Assert.True(ModerationPolicy.MayServe([Action(ModeratorKind.Automated, ModerationEffect.Withhold)]));

    /// <summary>R10.36 permits exactly this, and it is the reason automated moderation exists.</summary>
    [Fact]
    public void An_automated_quarantine_does_stop_a_post_being_served() =>
        Assert.False(ModerationPolicy.MayServe([Action(ModeratorKind.Automated, ModerationEffect.Quarantine)]));

    /// <summary>R10.39's denominator: the flags a reviewing record named, dismissed or not; never an automated one's.</summary>
    [Fact]
    public void R10_61_AdjudicatedFlagsCountOnlyReviewingRecords() =>
        Assert.Equal(["f1"], Sorted(ModerationPolicy.AdjudicatedFlags([
            Adjudicating(ModerationEffect.Dismiss, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Quarantine, ModeratorKind.Automated, "f2")])));

    /// <summary>
    /// Structural equality, including what the record adjudicates. R11.9's rebuild drill compares a
    /// fold with its own rebuild, and a record compared by array reference would make it compare
    /// nothing.
    /// </summary>
    [Fact]
    public void A_moderation_action_is_equal_by_value_including_what_it_adjudicates()
    {
        Assert.Equal(
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"),
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1", "f2"));

        Assert.NotEqual(
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f1"),
            Adjudicating(ModerationEffect.Withhold, ModeratorKind.Human, "f2"));
    }
```

- [ ] **Step 4: Run them to see them fail to build**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~ModerationTests"`
Expected: build FAILS with `CS1729` (no eight-argument constructor) and `CS0117: 'ModerationPolicy' does not contain a definition for 'UpheldFlags'`.

- [ ] **Step 5: Implement the domain half**

In `src/Curia.Domain/Moderation/Moderation.cs`:

1. Add `using System.Collections.Immutable;` above `using System.Collections.Frozen;`.

2. Replace the `ModerationAction` record, from its `/// <summary>` through `    ServerTimestamp At);`, with:

```csharp
/// <summary>
/// A moderation action. R10.37: "Every moderation action SHALL be a signed log entry (R6.25) with
/// actor, category, and rationale."
///
/// <para>All three are required fields rather than optional ones, so an action without a rationale
/// does not construct. R10.39 publishes the upheld rate and median time to action; both are
/// uncomputable from actions that did not record why or when.</para>
///
/// <para><b><see cref="Adjudicates"/> names the flags this action decided (R10.60).</b> Upholding is
/// a property of a flag, decided by the record that names it (R10.61), and the record is the only
/// place a reader of the public log learns which flags a moderator reviewed: a flag's own entry names
/// no post (R10.62). The writer derives the list; a moderator never types it.</para>
/// </summary>
public sealed record ModerationAction(
    string PostId,
    ModeratorKind Moderator,
    string ActorId,
    ModerationEffect Effect,
    FlagKind Category,
    string Rationale,
    ServerTimestamp At,
    ImmutableArray<string> Adjudicates)
{
    /// <summary>The flags this action adjudicates. Never the default array, so a fold can always enumerate it.</summary>
    public ImmutableArray<string> Adjudicates { get; init; } = Adjudicates.IsDefault ? [] : Adjudicates;

    /// <summary>
    /// Structural equality, spelled out for the reason <c>PostModeration</c> records: generated
    /// record equality compares an <see cref="ImmutableArray{T}"/> by reference, so two folds of the
    /// same log would hold unequal actions and R11.9's drill would compare nothing.
    /// </summary>
    public bool Equals(ModerationAction? other) =>
        other is not null
        && string.Equals(PostId, other.PostId, StringComparison.Ordinal)
        && Moderator == other.Moderator
        && string.Equals(ActorId, other.ActorId, StringComparison.Ordinal)
        && Effect == other.Effect
        && Category == other.Category
        && string.Equals(Rationale, other.Rationale, StringComparison.Ordinal)
        && At == other.At
        && Adjudicates.SequenceEqual(other.Adjudicates, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(PostId, Moderator, Effect, Category, At, Adjudicates.Length);
}
```

3. Replace `IsUpheld` (its whole doc comment and body) with:

```csharp
    /// <summary>
    /// R10.61: the flags upheld after <paramref name="historyInOrder"/>, one post's moderation
    /// records in log order.
    ///
    /// <para><b>Upheld is decided per flag, by the most recent reviewing record that names it.</b> A
    /// flag is upheld while that record quarantined or withheld the post, and released by a restore
    /// or a dismissal that names it. Keyed to the category a record cites, as it was when flags first
    /// shipped, a flag raised against content already withheld in its category was upheld the instant
    /// it was raised, by nobody — the unilateral demotion primitive <i>upheld</i> was defined to refuse
    /// (R10.35 opens flagging to every T0 agent).</para>
    ///
    /// <para><b>Only a reviewing record counts.</b> An automated record changes no flag's state in
    /// either direction: R10.36's automated quarantine is "pending review", and an automated
    /// dismissal releasing a flag a human upheld would be a system reviewing a human. A record whose
    /// (moderator, effect) pair R10.36 forbids is ignored, because the log is append-only and such a
    /// record can exist in it.</para>
    /// </summary>
    public static ImmutableHashSet<string> UpheldFlags(IReadOnlyList<ModerationAction> historyInOrder)
    {
        ArgumentNullException.ThrowIfNull(historyInOrder);

        var upheld = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        foreach (var action in historyInOrder)
        {
            if (!Reviewed(action)) continue;

            var upholds = action.Effect switch
            {
                ModerationEffect.Quarantine => true,
                ModerationEffect.Withhold => true,

                // A restore lifts the penalty as well as the withholding: an agent left demoted by
                // an action a moderator explicitly reversed would be serving it anyway.
                ModerationEffect.Restore => false,

                // Reviewed, and found not to warrant action.
                ModerationEffect.Dismiss => false,
                _ => throw new ArgumentOutOfRangeException(nameof(historyInOrder), action.Effect, "Not an effect"),
            };

            foreach (var flag in action.Adjudicates)
            {
                if (upholds) upheld.Add(flag);
                else upheld.Remove(flag);
            }
        }

        return upheld.ToImmutable();
    }

    /// <summary>
    /// The flags a reviewing record has named at least once: R10.39's denominator for the upheld
    /// rate, and what tells a dismissed flag from an open one.
    /// </summary>
    public static ImmutableHashSet<string> AdjudicatedFlags(IReadOnlyList<ModerationAction> historyInOrder)
    {
        ArgumentNullException.ThrowIfNull(historyInOrder);

        return historyInOrder
            .Where(Reviewed)
            .SelectMany(a => a.Adjudicates)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }
```

4. In `MayServe`, insert as the first statement inside the `foreach`:

```csharp
            // R10.36 reserves permanent removal for a human or a delegated agent, and the log is
            // append-only, so a forbidden record can exist in it. The fold refuses to act on one;
            // otherwise appending an event is a way to remove content (R10.61).
            if (!Permits(action)) continue;
```

5. Add these two private helpers at the end of `ModerationPolicy`, after `MayServe`:

```csharp
    /// <summary>Whether R10.36's table permits this record's (moderator, effect) pair.</summary>
    private static bool Permits(ModerationAction action) => Permitted.Contains((action.Moderator, action.Effect));

    /// <summary>A record that decides flags: permitted, and written by a moderator who reviews (R10.61).</summary>
    private static bool Reviewed(ModerationAction action) =>
        Permits(action) && action.Moderator is not ModeratorKind.Automated;
```

- [ ] **Step 6: Run the domain tests**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~ModerationTests"`
Expected: all `ModerationTests` PASS. This project references only `Curia.Domain`.

The solution as a whole does not build yet. `Curia.Application` still constructs a seven-argument `ModerationAction` and calls `IsUpheld`. Step 7 fixes that.

- [ ] **Step 7: Implement the projector half**

In `src/Curia.Application/Projections/FlagProjection.cs`:

1. Replace `HasUpheldFlag` and its doc comment with:

```csharp
    /// <summary>
    /// Table 11's "no upheld flags", for one post: whether any flag this post's records adjudicated is
    /// upheld (R10.61). False for a post nobody has adjudicated, however many flags it carries — see
    /// <see cref="ModerationPolicy.UpheldFlags"/> for why the alternative hands every agent a
    /// demotion primitive.
    /// </summary>
    public bool HasUpheldFlag => !ModerationPolicy.UpheldFlags(History).IsEmpty;
```

2. After the `RationaleField` constant, add:

```csharp
    /// <summary>R6.25's "a <c>moderation</c> record referencing a digest": the post's envelope digest (R10.60).</summary>
    public const string DigestField = "digest";

    /// <summary>The flags a moderation record adjudicates, by event id (R10.60, R10.61).</summary>
    public const string AdjudicatesField = "adjudicates";
```

3. In the private `ApplyModeration` method, replace

```csharp
        For(actions, postId).Add(new ModerationAction(
            postId, moderator, actorId, effect, category, rationale, appended.ServerTimestamp));
```

with

```csharp
        // A record without the member still governs servability and upholds nothing (R10.61). It is
        // read, not dropped: dropping a withholding would serve what a human withheld.
        ImmutableArray<string> adjudicates = fields.TryGetValue(AdjudicatesField, out var named) && named is JsonValue.Array list
            ? [.. list.Items.OfType<JsonValue.String>().Select(s => s.Value)]
            : [];

        For(actions, postId).Add(new ModerationAction(
            postId, moderator, actorId, effect, category, rationale, appended.ServerTimestamp, adjudicates));
```

In `src/Curia.Application/Projections/AgentStandingProjection.cs`, in the comment above `builder.CleanQuestions++`, change `see ModerationPolicy.IsUpheld for why` to `see ModerationPolicy.UpheldFlags for why`.

- [ ] **Step 8: Bring the projector tests to the per-flag rule**

In `tests/Curia.Application.Tests/Projections/FlagProjectorTests.cs`:

1. Replace the `ModerateAsync` helper with:

```csharp
    private static Task ModerateAsync(
        InMemoryEventStore store,
        string eventId,
        FlagKind category,
        ModerationEffect effect,
        CancellationToken ct,
        ModeratorKind moderator = ModeratorKind.Human,
        params string[] adjudicates) =>
        AppendAsync(store, Post, eventId, Moderator, FlagProjector.ModerationAppliedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(Post)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(moderator))),
            new(FlagProjector.ActorIdField, new JsonValue.String(Moderator)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(a => (JsonValue)new JsonValue.String(a))])),
        ]), ct);
```

2. In `R10_39_AFlagIsUpheldOnlyWhenAModeratorActsOnIt`, make both moderation calls name the flag. Append `, ModeratorKind.Human, "01JFLAG000000000000000001"` to the `Dismiss` call and to the `Withhold` call.

3. Add this test after `R10_37_AnActionInAnotherCategoryDoesNotUpholdTheFlag`:

```csharp
    /// <summary>
    /// R10.61's reason, at the projection. A withholding that names no flag upholds nothing, so a flag
    /// raised against the post afterwards is not upheld until a record names it — it could not have
    /// been reviewed by a record written before it existed.
    /// </summary>
    [Fact]
    public async Task R10_61_AFlagRaisedAfterAWithholdingIsNotUpheldUntilARecordNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Withhold, ct);
        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }
```

This is spec Decision 5's late-flag test, and falsification case 3 is aimed at it. Task 5 replaces this file whole; it carries the test across under the same name, re-expressed for a fold that no longer reads flags. Never let a rewrite drop it.

In `tests/Curia.Application.Tests/Projections/AgentStandingProjectorTests.cs`, replace `UpholdFlagAsync` (its doc comment and body) with:

```csharp
    /// <summary>
    /// Appends a flag of <paramref name="category"/> on <paramref name="postId"/> and a
    /// <c>moderation.applied</c> record that names it and withholds the post — the half of §10.10
    /// that decides, as distinct from the flag that asks. The record names the flag's event id,
    /// because R10.61 decides upholding per flag: a record naming nothing upholds nothing.
    /// </summary>
    private static async Task UpholdFlagAsync(
        InMemoryEventStore store, string postId, FlagKind category, string reporter, CancellationToken ct)
    {
        // AppendToPostAsync names each event "{postId}-{position}", so the flag's id is read here,
        // before the flag is appended.
        var position = Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(postId)), ct).ConfigureAwait(false)).Count;
        var flagId = $"{postId}-{position.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        await AppendToPostAsync(store, postId, FlagProjector.FlagRaisedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.RaisedByField, new JsonValue.String(reporter)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reported")),
        ]), ct).ConfigureAwait(false);

        await AppendToPostAsync(store, postId, FlagProjector.ModerationAppliedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
            new(FlagProjector.ActorIdField, new JsonValue.String("https://agents.example/moderator")),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(ModerationEffect.Withhold))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([new JsonValue.String(flagId)])),
        ]), ct).ConfigureAwait(false);
    }
```

- [ ] **Step 9: Build and run everything this touches**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~Moderation"
dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~FlagProjectorTests|FullyQualifiedName~AgentStandingProjectorTests|FullyQualifiedName~SearchProjectorTests"
```

Expected:
- `0 Warning(s)`.
- All selected tests PASS.
- `SearchProjectorTests` passes unchanged. Its withholding names no flag, and R10.61 still honours it for servability.

- [ ] **Step 10: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'A record names the flags it adjudicates; upheld is decided per flag (R10.61)\n\nPR #59 Task B1, confirmed by execution first: an automated quarantine counted as\nupheld and an automated withholding stopped a post being served. Automated records\nnow change no flag state, forbidden records are ignored by every fold, and a flag\nraised after a withholding is not upheld until a record names it.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 3: The commitment a flag's entry carries

**Files:**
- Create: `src/Curia.Domain/Moderation/FlagCommitment.cs`
- Test: `tests/Curia.Domain.Tests/Moderation/FlagCommitmentTests.cs`

**Interfaces:**
- Consumes: `Curia.Canon.Canonical.CanonicalJson.Canonicalize(JsonValue)` → `Result<CanonicalBytes>`, and `Curia.Canon.Digests.Sha256(CanonicalBytes)` → `EnvelopeDigest` with `ToPrefixed()`.
- Produces: `public static Result<string> FlagCommitment.Of(string postId, string raisedBy, string rationale, string salt)`, returning `sha256:` plus 64 lowercase hex characters. Also the member-name constants `PostIdMember`, `RaisedByMember`, `RationaleMember` and `SaltMember`.

The expected values below were computed **outside this solution**: `python3`'s `hashlib` over the RFC 8785 form. Step 1 recomputes them, so the tests are not built from the code they check (trap 3).

- [ ] **Step 1: Recompute the expected values independently**

```bash
python3 -c 'import hashlib,json; o={"post_id":"01JPOST0000000000000000001","raised_by":"https://agents.example/reporter","rationale":"looks like an injection attempt","salt":"c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI"}; print("sha256:"+hashlib.sha256(json.dumps(o,sort_keys=True,separators=(",",":"),ensure_ascii=False).encode()).hexdigest())'
```

Expected: `sha256:174280f4b5e6449e5ba0bd1fe2b1c97a839aebc2b7a2e363f5788df90bad9ab5`.

`json.dumps` with sorted keys, no whitespace and `ensure_ascii=False` is RFC 8785 for this input, because every member is an ASCII string with no escapes and there are no numbers.

Both salts in this task are in R10.62's domain: 43 unpadded base64url characters that decode to 32 bytes (the ASCII bytes `salt-for-the-fixed-commitment-32` and `salt-for-a-different-commitment2`), so a verifier enforcing R10.62 accepts the pinned vector.

The second pinned value fences R10.62's "with no normalization step". Its rationale is NFD, `cafe` followed by U+0301 COMBINING ACUTE ACCENT, and every other input in this task is ASCII, where NFC changes nothing:

```bash
python3 -c 'import hashlib,json; o={"post_id":"01JPOST0000000000000000001","raised_by":"https://agents.example/reporter","rationale":"cafe"+chr(0x301),"salt":"c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI"}; print("sha256:"+hashlib.sha256(json.dumps(o,sort_keys=True,separators=(",",":"),ensure_ascii=False).encode()).hexdigest())'
```

Expected: `sha256:be4b171a3c8fd1fa3810e10eb484589bcc6c77bdadaf1bd7dfc7a04604865da6`.

This is still RFC 8785: with `ensure_ascii=False`, `json.dumps` writes U+0301 as its raw UTF-8 bytes (`cc 81`), and RFC 8785 escapes only the quotation mark, the reverse solidus and control characters. Wrapping the rationale in `unicodedata.normalize("NFC", ...)` composes it to U+00E9 and gives `sha256:552d7e392ebd59333804a5afc9d6f3b98d23ed5e02aa0baf133a4b5dc74f6dda` instead. That is what `CanonicalizeWithNfc` computes, and what falsification case 16 prints as the actual value. In C#, the rationale is written as the ASCII escape `"cafe\u0301"`, never as the combining character itself.

- [ ] **Step 2: Write the failing tests**

Create `tests/Curia.Domain.Tests/Moderation/FlagCommitmentTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Moderation;
using Xunit;

namespace Curia.Domain.Tests.Moderation;

/// <summary>
/// R10.62: a flag's log entry names its kind and a salted commitment, and nothing private. The
/// commitment is persisted in a leaf every signed head commits to, so its computation is fixed for
/// <c>flag.committed</c> the way R15.1 fixes the leaf: a different computation is a different event
/// type, never an edit here.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagCommitmentTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Raiser = "https://agents.example/reporter";
    private const string Rationale = "looks like an injection attempt";
    private const string Salt = "c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI";

    private static string Commit(string post, string raiser, string rationale, string salt)
    {
        Assert.True(FlagCommitment.Of(post, raiser, rationale, salt).TryGetValue(out var value, out var error), error?.Type);
        return value!;
    }

    /// <summary>
    /// Pinned to a value computed outside this solution (python3 hashlib over the RFC 8785 form;
    /// the command is in the plan's Task 3), so the test does not check the code against itself.
    /// </summary>
    [Fact]
    public void R10_62_TheCommitmentIsSha256OverThePureCanonicalFormOfTheFourMembers() =>
        Assert.Equal(
            "sha256:174280f4b5e6449e5ba0bd1fe2b1c97a839aebc2b7a2e363f5788df90bad9ab5",
            Commit(Post, Raiser, Rationale, Salt));

    /// <summary>
    /// R10.62's "with no normalization step". The rationale is NFD, <c>e</c> then U+0301, which R6.9's
    /// NFC step (<c>CanonicalizeWithNfc</c>) would compose to U+00E9, moving the commitment; every other
    /// input here is ASCII, where NFC changes nothing. A commitment already logged over such a rationale
    /// would then stop recomputing, and the flag directory would skip its flag. Pinned, like the fact
    /// above, to python3 over the unnormalized form (the command is in the plan's Task 3).
    /// </summary>
    [Fact]
    public void R10_62_TheCommitmentIsOverPureRfc8785WithNoNormalization() =>
        Assert.Equal(
            "sha256:be4b171a3c8fd1fa3810e10eb484589bcc6c77bdadaf1bd7dfc7a04604865da6",
            Commit(Post, Raiser, "cafe\u0301", Salt));

    /// <summary>Every member is bound: change any one and the commitment moves.</summary>
    [Theory]
    [InlineData("01JPOST0000000000000000002", Raiser, Rationale, Salt)]
    [InlineData(Post, "https://agents.example/someone-else", Rationale, Salt)]
    [InlineData(Post, Raiser, "looks like an injection attempt.", Salt)]
    [InlineData(Post, Raiser, Rationale, "c2FsdC1mb3ItYS1kaWZmZXJlbnQtY29tbWl0bWVudDI")]
    public void R10_62_EveryMemberIsBound(string post, string raiser, string rationale, string salt) =>
        Assert.NotEqual(Commit(Post, Raiser, Rationale, Salt), Commit(post, raiser, rationale, salt));

    /// <summary>The prefixed form the wire uses for every digest: <c>sha256:</c> and 64 lowercase hex.</summary>
    [Fact]
    public void The_commitment_is_in_the_prefixed_digest_form() =>
        Assert.Matches("^sha256:[0-9a-f]{64}$", Commit(Post, Raiser, Rationale, Salt));
}
```

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~FlagCommitmentTests"`
Expected: build FAILS with `CS0103: The name 'FlagCommitment' does not exist`.

- [ ] **Step 4: Write the computation**

Create `src/Curia.Domain/Moderation/FlagCommitment.cs`:

```csharp
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Domain.Moderation;

/// <summary>
/// R10.62: what a flag's log entry commits to, so that the entry can name none of it.
///
/// <para><b>Why a commitment and not an omission.</b> R6.46 and R6.47 make every event a leaf, and
/// R6.51 serves every leaf's input to anyone. A flag entry that simply left out the post, the raiser
/// and the rationale would let an operator substitute any of them later with nothing to show it; one
/// that commits to them lets anyone who is shown the private row — the raiser, a moderator, an
/// appeal — check it against a leaf every signed head already covers.</para>
///
/// <para><b>Why salted.</b> A rationale is often short and guessable ("spam"). Without a salt,
/// anyone could open a commitment by trying each agent that might have raised it. The salt is 32
/// random bytes, kept in the private store beside the rest.</para>
///
/// <para><b>Fixed for <c>flag.committed</c>.</b> The input is these four members under pure RFC 8785
/// (R6.8, the profile R6.46 uses), hashed with SHA-256 and written in the prefixed form every digest on
/// the wire takes. It is persisted in leaves, so a different computation is a different event type,
/// never an edit to this one.</para>
/// </summary>
public static class FlagCommitment
{
    public const string PostIdMember = "post_id";
    public const string RaisedByMember = "raised_by";
    public const string RationaleMember = "rationale";
    public const string SaltMember = "salt";

    /// <summary><c>sha256:</c> and the hex SHA-256 of the four members' pure canonical form.</summary>
    public static Result<string> Of(string postId, string raisedBy, string rationale, string salt)
    {
        ArgumentNullException.ThrowIfNull(postId);
        ArgumentNullException.ThrowIfNull(raisedBy);
        ArgumentNullException.ThrowIfNull(rationale);
        ArgumentNullException.ThrowIfNull(salt);

        var input = new JsonValue.Object(
        [
            new(PostIdMember, new JsonValue.String(postId)),
            new(RaisedByMember, new JsonValue.String(raisedBy)),
            new(RationaleMember, new JsonValue.String(rationale)),
            new(SaltMember, new JsonValue.String(salt)),
        ]);

        return CanonicalJson.Canonicalize(input).Map(bytes => Digests.Sha256(bytes).ToPrefixed());
    }
}
```

- [ ] **Step 5: Run the tests to see them pass**

Run: `dotnet test tests/Curia.Domain.Tests -c Release --nologo --filter "FullyQualifiedName~FlagCommitmentTests"`
Expected: 7 PASS. Then `dotnet test tests/Curia.Architecture.Tests -c Release --nologo` still passes: CS-7 permits `Curia.Domain` to depend on `Curia.Canon`.

- [ ] **Step 6: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'The salted commitment a flag entry carries (R10.62)\n\nPinned to a value computed outside the solution.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---
### Task 4: The private store — `flag_details`, one contract, two adapters

**Files:**
- Create: `src/Curia.Application/Ports/IFlagDetailStore.cs`
- Create: `tests/Curia.Application.Tests/InMemory/InMemoryFlagDetailStore.cs`
- Create: `tests/Curia.Application.Tests/FlagDetailStorePortContractTests.cs`
- Create: `db/0004_create_flag_details.sql`
- Modify: `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs` (a constant and a `FileNames` entry)
- Create: `src/Curia.Infrastructure/PostgresFlagDetailStore.cs`
- Modify: `src/Curia.Infrastructure/PostgresAdapters.cs` (a `FlagDetails` property)
- Modify: `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs` (`CreateIsolatedFlagDetailSchemaAsync`)
- Create: `tests/Curia.Infrastructure.Tests/PostgresFlagDetailStoreTests.cs`
- Modify: `src/Curia.Api/Program.cs` (register `IFlagDetailStore`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `public sealed record FlagDetail(string EventId, string PostId, string RaisedBy, string Rationale, string Salt)`.
  - `public interface IFlagDetailStore`, with `Task<Result<FlagDetail>> AppendAsync(FlagDetail, CancellationToken = default)` and `Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken = default)`. `ReadAllAsync` orders by `EventId`, ordinal.
  - `public static class FlagDetailRules`, with `Admit(FlagDetail)` and the errors `Exists(string)` (`curia/flag/detail-exists`), `Unstorable()` (`curia/flag/detail-unstorable`) and `Unavailable(string)` (`curia/flag/detail-store-unavailable`).
  - `SchemaMigrations.FlagDetailsFile = "0004_create_flag_details.sql"`.
  - `PostgresAdapters.FlagDetails`, typed `IFlagDetailStore`.
  - `PostgresDatabaseFixture.CreateIsolatedFlagDetailSchemaAsync(CancellationToken)`, which returns a schema name.

- [ ] **Step 1: Write the contract suite**

Create `tests/Curia.Application.Tests/FlagDetailStorePortContractTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IFlagDetailStore"/> promises, run against the in-memory adapter here and
/// against Postgres in <c>Curia.Infrastructure.Tests</c> (R11.4, R11.21, R11.22). Two adapters that
/// happen to agree are not a contract; this is.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class FlagDetailStorePortContractTests
{
    /// <summary>A fresh, empty store: no test sees another's rows.</summary>
    protected abstract IFlagDetailStore CreateStore();

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static FlagDetail Detail(string eventId, string rationale = "looks like spam") =>
        new(eventId, "01JPOST0000000000000000001", "https://agents.example/reporter", rationale, "c2FsdA");

    [Fact]
    public async Task R11_32_AnEmptyStoreReadsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty(Require(await CreateStore().ReadAllAsync(ct)));
    }

    /// <summary>Every member comes back exactly as appended — a store that normalized one would break the commitment.</summary>
    [Fact]
    public async Task R11_32_AnAppendedDetailReadsBackVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var detail = Detail("01JFLAG000000000000000001", "Café — naïve rationale\nwith a second line");

        Require(await store.AppendAsync(detail, ct));

        Assert.Equal(detail, Assert.Single(Require(await store.ReadAllAsync(ct))));
    }

    /// <summary>
    /// Ordered by event id, ordinally. Appended in the reverse order, so an adapter returning
    /// insertion order comes back wrong (trap 10: store fixtures in the order a wrong implementation
    /// would return them).
    /// </summary>
    [Fact]
    public async Task R11_32_DetailsReadBackInEventIdOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        Require(await store.AppendAsync(Detail("01JFLAG000000000000000002"), ct));
        Require(await store.AppendAsync(Detail("01JFLAG000000000000000001"), ct));

        Assert.Equal(
            ["01JFLAG000000000000000001", "01JFLAG000000000000000002"],
            Require(await store.ReadAllAsync(ct)).Select(d => d.EventId));
    }

    /// <summary>Append-only: a second row for the same event is refused, and the first stands unchanged.</summary>
    [Fact]
    public async Task R11_32_ASecondDetailForTheSameEventIsRefusedAndTheFirstStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var first = Detail("01JFLAG000000000000000001", "the first rationale");

        Require(await store.AppendAsync(first, ct));

        Assert.False((await store.AppendAsync(Detail("01JFLAG000000000000000001", "a replacement"), ct))
            .TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/detail-exists", error!.Type);
        Assert.Equal(first, Assert.Single(Require(await store.ReadAllAsync(ct))));
    }

    /// <summary>
    /// R11.21: the in-memory adapter accepts exactly what Postgres accepts. A <c>text</c> column
    /// cannot hold U+0000, so neither adapter may take one — and the refusal names no content.
    /// </summary>
    [Theory]
    [InlineData("event\0id", "p", "r", "why", "s")]
    [InlineData("e", "post\0id", "r", "why", "s")]
    [InlineData("e", "p", "raiser\0", "why", "s")]
    [InlineData("e", "p", "r", "a rationale with \0 in it", "s")]
    [InlineData("e", "p", "r", "why", "sa\0lt")]
    [InlineData("e", "p", "r", "", "s")]
    public async Task R11_21_AMemberTheStoreCannotHoldIsRefusedByName(
        string eventId, string postId, string raisedBy, string rationale, string salt)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        Assert.False((await store.AppendAsync(new FlagDetail(eventId, postId, raisedBy, rationale, salt), ct))
            .TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/detail-unstorable", error!.Type);
        Assert.DoesNotContain("rationale with", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Require(await store.ReadAllAsync(ct)));
    }
}

/// <summary>R11.4's in-memory adapter, held to the contract.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public; every [Fact] is inherited, so the analyzer's test-class heuristic does not see it.")]
public sealed class InMemoryFlagDetailStoreContractTests : FlagDetailStorePortContractTests
{
    protected override IFlagDetailStore CreateStore() => new InMemory.InMemoryFlagDetailStore();
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~FlagDetailStore"`
Expected: build FAILS with `CS0246: The type or namespace name 'IFlagDetailStore' could not be found`.

- [ ] **Step 3: Write the port**

Create `src/Curia.Application/Ports/IFlagDetailStore.cs`:

```csharp
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// The private half of a flag (R10.62, R11.32): the post it concerns, who raised it, why, and the
/// salt its log entry commits with.
///
/// <para><b>Never an event.</b> R6.46 and R6.47 make every event of the store a leaf, and R6.51
/// serves every leaf's input verbatim to anyone, so a fact written as an event is published. This
/// row is bound to its public <c>flag.committed</c> entry by the commitment that entry carries,
/// which is what makes a later substitution of any member here detectable.</para>
/// </summary>
/// <param name="EventId">The <c>flag.committed</c> event this row opens.</param>
/// <param name="PostId">The post the flag concerns. Public only once a moderation record adjudicates the flag (R10.60).</param>
/// <param name="RaisedBy">The authenticated principal that raised it. Never published.</param>
/// <param name="Rationale">R10.35's rationale, screened before it was stored. Never published.</param>
/// <param name="Salt">The commitment's salt, 32 random bytes in base64url.</param>
public sealed record FlagDetail(string EventId, string PostId, string RaisedBy, string Rationale, string Salt);

/// <summary>
/// The store of <see cref="FlagDetail"/> rows: append-only under R11.6's grant, in Postgres in
/// production and in memory for the application tests (R11.4).
/// </summary>
public interface IFlagDetailStore
{
    /// <summary>
    /// Records <paramref name="detail"/>. Refuses a second row for the same event
    /// (<c>curia/flag/detail-exists</c>) and any member the store cannot hold
    /// (<c>curia/flag/detail-unstorable</c>, <see cref="FlagDetailRules.Admit"/>), leaving the store
    /// unchanged either way.
    /// </summary>
    Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default);

    /// <summary>Every row, ordered by <see cref="FlagDetail.EventId"/> ordinally. An empty store yields an empty list.</summary>
    Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The one admission rule both adapters apply, and the refusals the port names. One rule, called by
/// both, because two copies of a rule are how two adapters come to disagree (R11.21, errata E11).
/// </summary>
public static class FlagDetailRules
{
    /// <summary>
    /// Refuses a row with an empty member, or with U+0000 anywhere — which a Postgres <c>text</c>
    /// column cannot hold, so an in-memory store that took it would be more permissive than the real
    /// one.
    /// </summary>
    public static Result<FlagDetail> Admit(FlagDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        string?[] members = [detail.EventId, detail.PostId, detail.RaisedBy, detail.Rationale, detail.Salt];

        return members.Any(m => string.IsNullOrEmpty(m) || m.Contains('\0', StringComparison.Ordinal))
            ? Result<FlagDetail>.Fail(Unstorable())
            : Result<FlagDetail>.Ok(detail);
    }

    /// <summary>A second row for one event. Append-only: the first stands.</summary>
    public static Error Exists(string eventId) => new(
        "curia/flag/detail-exists",
        "A flag detail is already recorded for that event",
        $"event={eventId}");

    /// <summary>An empty member, or U+0000. The detail names no member value: a rationale is screened text, but it is still the raiser's.</summary>
    public static Error Unstorable() => new(
        "curia/flag/detail-unstorable",
        "A flag detail member is empty or contains U+0000, which the store cannot hold");

    /// <summary>The store failed for a reason of its own; the detail carries its state code and nothing else.</summary>
    public static Error Unavailable(string detail) => new(
        "curia/flag/detail-store-unavailable",
        "The flag detail store could not complete the request",
        detail);
}
```

- [ ] **Step 4: Write the in-memory adapter**

Create `tests/Curia.Application.Tests/InMemory/InMemoryFlagDetailStore.cs`:

```csharp
using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>R11.4's in-memory <see cref="IFlagDetailStore"/>: a dictionary, the shared admission rule, ordinal order.</summary>
internal sealed class InMemoryFlagDetailStore : IFlagDetailStore
{
    private readonly Dictionary<string, FlagDetail> _rows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default)
    {
        if (!FlagDetailRules.Admit(detail).TryGetValue(out _, out var refusal))
            return Task.FromResult(Result<FlagDetail>.Fail(refusal!));

        lock (_gate)
        {
            if (!_rows.TryAdd(detail.EventId, detail))
                return Task.FromResult(Result<FlagDetail>.Fail(FlagDetailRules.Exists(detail.EventId)));
        }

        return Task.FromResult(Result<FlagDetail>.Ok(detail));
    }

    public Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FlagDetail> rows = [.. _rows.Values.OrderBy(d => d.EventId, StringComparer.Ordinal)];
            return Task.FromResult(Result<IReadOnlyList<FlagDetail>>.Ok(rows));
        }
    }
}
```

- [ ] **Step 5: Run the in-memory contract**

Run: `dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~FlagDetailStore"`
Expected: 10 PASS (4 facts plus 6 theory rows).

- [ ] **Step 6: Write the migration**

Create `db/0004_create_flag_details.sql`:

```sql
-- Migration 0004, forward-only, applied after db/0003_create_retrieval_index.sql: the private half
-- of a flag (errata G13; R10.62, R11.32, R11.9 addendum).
--
-- __CURIA_APP_ROLE__ is the placeholder 0001 introduces, substituted by
-- Curia.Infrastructure.Migrations.SchemaMigrations.RenderAll before execution.
--
-- =========================================================================================
-- WHY THIS IS NOT AN EVENT, AND WHY IT HAS THE EVENT TABLE'S GRANTS ANYWAY
-- =========================================================================================
-- Every event is a leaf of the Acta (R6.46, R6.47), and GET /v1/log/entries/{index} serves every
-- leaf's input verbatim to anyone (R6.51). A flag written as an event with its raiser and
-- rationale was therefore published -- found by executing it (errata G13, finding 2). A flag now
-- enters the log as `flag.committed`: its kind and a salted commitment to the three columns below
-- plus the salt, and nothing else. This table holds what the commitment commits to.
--
-- It is not a read model. Nothing here is derivable from the log, which holds only commitments,
-- so it is part of the system of record (R11.9 addendum) and is backed up as `events` is. That is
-- also why its grant is the event table's, not the operational tables': INSERT and SELECT, and
-- no UPDATE or DELETE. A row changed after the fact no longer opens the commitment its entry
-- carries, which is detectable; a row deleted is a flag whose raiser nobody can ever learn, which
-- is not. The grant makes the second impossible rather than detectable.
--
-- Legacy flags -- `flag.raised` events written before this migration -- carry their raiser and
-- rationale in the log itself, publicly and permanently. Nothing here reaches them.
CREATE TABLE flag_details (
  event_id    TEXT PRIMARY KEY,   -- the flag.committed event this row opens
  post_id     TEXT NOT NULL,      -- public only once a moderation record adjudicates the flag
  raised_by   TEXT NOT NULL,      -- never published
  rationale   TEXT NOT NULL,      -- screened before storage; never published
  salt        TEXT NOT NULL       -- 32 random bytes, base64url
);

CREATE INDEX ON flag_details (post_id);
CREATE INDEX ON flag_details (raised_by);

GRANT INSERT, SELECT ON flag_details TO __CURIA_APP_ROLE__;
REVOKE UPDATE, DELETE ON flag_details FROM __CURIA_APP_ROLE__;   -- R11.6, R11.32
```

In `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs`, add this constant after `RetrievalIndexFile`:

```csharp
    /// <summary>db/0004: the private half of a flag (R10.62, R11.32), under the event table's append-only grant.</summary>
    public const string FlagDetailsFile = "0004_create_flag_details.sql";
```

Then append `FlagDetailsFile,` to `FileNames`, after `RetrievalIndexFile,`.

- [ ] **Step 7: Write the Postgres adapter and expose it**

Create `src/Curia.Infrastructure/PostgresFlagDetailStore.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IFlagDetailStore"/> over db/0004's <c>flag_details</c>: append-only by grant (R11.6),
/// ordered by event id under the "C" collation so the order is the in-memory adapter's ordinal one.
/// </summary>
public sealed class PostgresFlagDetailStore : IFlagDetailStore
{
    /// <summary>Postgres's <c>unique_violation</c>: a second row for one event.</summary>
    private const string UniqueViolation = "23505";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _qualifiedTable;

    public PostgresFlagDetailStore(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _qualifiedTable = SqlIdentifier.Quote(schema) + ".flag_details";
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the connection and command, as in PostgresEventStore.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is the quoted table name built in the constructor from a schema the composition root supplies; every value is a parameter.")]
    public async Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (!FlagDetailRules.Admit(detail).TryGetValue(out _, out var refusal))
            return Result<FlagDetail>.Fail(refusal!);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {_qualifiedTable} (event_id, post_id, raised_by, rationale, salt) " +
                "VALUES (@event, @post, @raiser, @rationale, @salt);",
                connection);
            command.Parameters.Add(new NpgsqlParameter("event", NpgsqlDbType.Text) { Value = detail.EventId });
            command.Parameters.Add(new NpgsqlParameter("post", NpgsqlDbType.Text) { Value = detail.PostId });
            command.Parameters.Add(new NpgsqlParameter("raiser", NpgsqlDbType.Text) { Value = detail.RaisedBy });
            command.Parameters.Add(new NpgsqlParameter("rationale", NpgsqlDbType.Text) { Value = detail.Rationale });
            command.Parameters.Add(new NpgsqlParameter("salt", NpgsqlDbType.Text) { Value = detail.Salt });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Result<FlagDetail>.Ok(detail);
        }
        catch (PostgresException e) when (e.SqlState == UniqueViolation)
        {
            return Result<FlagDetail>.Fail(FlagDetailRules.Exists(detail.EventId));
        }
        catch (PostgresException e)
        {
            // The state code only: a Postgres message can quote the value it refused.
            return Result<FlagDetail>.Fail(FlagDetailRules.Unavailable($"sqlstate={e.SqlState}"));
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See AppendAsync.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See AppendAsync.")]
    public async Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"SELECT event_id, post_id, raised_by, rationale, salt FROM {_qualifiedTable} ORDER BY event_id COLLATE \"C\";",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var rows = new List<FlagDetail>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FlagDetail(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }

            return Result<IReadOnlyList<FlagDetail>>.Ok(rows);
        }
        catch (PostgresException e)
        {
            return Result<IReadOnlyList<FlagDetail>>.Fail(FlagDetailRules.Unavailable($"sqlstate={e.SqlState}"));
        }
    }
}
```

In `src/Curia.Infrastructure/PostgresAdapters.cs`, add after the `VectorIndex` property:

```csharp
    /// <summary>The private half of every flag (R10.62, R11.32): append-only, like the event log it is bound to.</summary>
    public IFlagDetailStore FlagDetails => new PostgresFlagDetailStore(_dataSource);
```

In `src/Curia.Api/Program.cs`, add after the `IEventReader` registration line:

```csharp
        // The private half of every flag (R10.62, R11.32). Its own port rather than a member of the
        // event store's, because what it holds must never become an event: R6.51 serves every event.
        builder.Services.AddSingleton(sp => sp.GetRequiredService<PostgresAdapters>().FlagDetails);
```

- [ ] **Step 8: Hold the Postgres adapter to the contract, and the table to its grant**

In `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs`, add directly after `CreateIsolatedRetrievalSchemaAsync`:

```csharp
    /// <summary>
    /// db/0004 rendered into a fresh schema through the production renderer, so each flag-detail
    /// contract test starts from an empty table. <c>public</c> keeps the table RenderAll created,
    /// which the grant tests read.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See CreateIsolatedOperationalSchemaAsync: a generated schema name and a checked-in template.")]
    public async Task<string> CreateIsolatedFlagDetailSchemaAsync(CancellationToken cancellationToken = default)
    {
        var schemaName = $"flg{Guid.NewGuid():N}";
        var quotedSchema = QuoteIdentifier(schemaName);

        var sql = $"""
            CREATE SCHEMA {quotedSchema};
            GRANT USAGE ON SCHEMA {quotedSchema} TO {QuoteIdentifier(_roleName)};
            SET search_path TO {quotedSchema}, public;
            {SchemaMigrations.Render(SchemaMigrations.FlagDetailsFile, _roleName)}
            RESET search_path;
            """;

        await using var connection = await AdminDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return schemaName;
    }
```


Create `tests/Curia.Infrastructure.Tests/PostgresFlagDetailStoreTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresFlagDetailStoreContractTests : FlagDetailStorePortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresFlagDetailStoreContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IFlagDetailStore CreateStore()
    {
        var schema = _fixture.CreateIsolatedFlagDetailSchemaAsync().GetAwaiter().GetResult();
        return new PostgresFlagDetailStore(_fixture.AppRoleDataSource, schema);
    }
}

/// <summary>
/// db/0004's grant, proved the way <see cref="AppRoleGrantRefusalTests"/> proves R11.6's: on a
/// connection opened as the application role, asserting Postgres's own insufficient-privilege state.
/// One test per privilege, each over constant SQL, so a failure names the privilege that was granted.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagDetailGrantTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public FlagDetailGrantTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>The positive control: the role can write and read, so the refusals below are narrow revokes and not a role with no access.</summary>
    [Fact]
    public async Task TheAppRoleCanInsertAndSelectFlagDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) " +
            "VALUES ('grant-positive-control', 'p', 'r', 'why', 's');", connection))
        {
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM flag_details WHERE event_id = 'grant-positive-control';", connection);
        Assert.Equal(1L, (long)(await select.ExecuteScalarAsync(ct))!);
    }

    /// <summary>R11.6 applied to the private store (R11.32): no UPDATE, by grant rather than by restraint.</summary>
    [Fact]
    public async Task R11_32_TheAppRoleCannotUpdateAFlagDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE flag_details SET rationale = 'rewritten' WHERE event_id = 'no-such-row';", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table flag_details", ex.MessageText, StringComparison.Ordinal);
    }

    /// <summary>R11.6 applied to the private store (R11.32): no DELETE, by grant rather than by restraint.</summary>
    [Fact]
    public async Task R11_32_TheAppRoleCannotDeleteAFlagDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "DELETE FROM flag_details WHERE event_id = 'no-such-row';", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table flag_details", ex.MessageText, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 9: Run the Infrastructure suite**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Infrastructure.Tests -c Release --nologo
```

Expected:
- `0 Warning(s)`.
- `Curia.Infrastructure.Tests` passes in full, including `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder`. Without Step 6's `FileNames` line that test fails, naming `0004_create_flag_details.sql`.
- The 10 Postgres contract rows and the 3 grant tests pass: the positive control, and one refusal per privilege.

- [ ] **Step 10: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'A private, append-only store for what a flag entry commits to (R10.62, R11.32)\n\ndb/0004 flag_details under the event table grant; one contract over the\nin-memory and Postgres adapters.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---
### Task 5: The flag directory — R7.18's views from the log and the private store

**Files:**
- Create: `src/Curia.Application/Projections/FlagDirectory.cs`
- Modify (whole file): `src/Curia.Application/Projections/FlagProjection.cs`
- Modify: `src/Curia.Api/ForumEndpoints.cs` (`ListRaisedFlagsAsync`, `ListPostFlagsAsync`, a new `FlagsAsync` helper)
- Test: `tests/Curia.Application.Tests/Projections/FlagDirectoryTests.cs`
- Test (whole file): `tests/Curia.Application.Tests/Projections/FlagProjectorTests.cs`

**Interfaces:**
- Consumes:
  - `FlagCommitment.Of` (Task 3).
  - `FlagDetail`, `IFlagDetailStore` and `InMemoryFlagDetailStore` (Task 4).
  - `ModerationPolicy.UpheldFlags` (Task 2).
- Produces:
  - `public sealed record RaisedFlag(string FlagId, string PostId, string RaisedBy, FlagKind Kind, ServerTimestamp At)`. It moves here from `FlagProjection.cs` and gains `FlagId`.
  - `public sealed record FlagDirectory(ImmutableArray<RaisedFlag> Flags, ImmutableSortedDictionary<string, int> Skipped)`, with `public static FlagDirectory Join(IReadOnlyList<AppendedEvent> eventsInSeqOrder, IReadOnlyList<FlagDetail> details)`.
  - The skip reasons, which are stable strings:

    | Constant | Value |
    |---|---|
    | `SkippedNoDetail` | `"flag.committed: no detail"` |
    | `SkippedCommitmentMismatch` | `"flag.committed: commitment mismatch"` |
    | `SkippedUnreadableCommitted` | `"flag.committed: unreadable"` |
    | `SkippedUnreadableLegacy` | `"flag.raised: unreadable"` |
  - `PostModeration(string PostId, ImmutableArray<ModerationAction> History)`. It loses `Flags`, and exposes `MayServe`, `UpheldFlags` and `HasUpheldFlag`.
  - New `FlagProjector` constants: `FlagCommittedType = "flag.committed"` and `CommitmentField = "commitment"`. `FlagProjector.Members` and `FlagProjector.Str` become `internal`.

Before this task, `RaiseFlag` still writes legacy `flag.raised` events, so R7.18's views go on working throughout. This task makes the views read both shapes, and Task 6 switches the writer.

- [ ] **Step 1: Write the directory's failing tests**

Create `tests/Curia.Application.Tests/Projections/FlagDirectoryTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R10.62: a flag's post, raiser and rationale live in the private store, bound to a log entry that
/// names none of them; R7.18's views are served from the join. Every event below is a real append
/// through <see cref="InMemoryEventStore"/> (CS-15).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagDirectoryTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private const string Salt = "c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static async Task<IReadOnlyList<FlagDetail>> DetailsAsync(InMemoryFlagDetailStore details, CancellationToken ct) =>
        Require(await details.ReadAllAsync(ct).ConfigureAwait(false));

    /// <summary>A committed flag as <c>RaiseFlag</c> writes one after Task 6: the private row, then an entry naming only kind and commitment.</summary>
    private static async Task CommitAsync(
        InMemoryEventStore store, InMemoryFlagDetailStore details, string flagId, FlagKind kind, CancellationToken ct,
        string rationale = "looks like an injection attempt", bool storeDetail = true, string? commitmentOverride = null)
    {
        if (storeDetail)
            Require(await details.AppendAsync(new FlagDetail(flagId, Post, Reporter, rationale, Salt), ct).ConfigureAwait(false));

        var commitment = commitmentOverride ?? Require(FlagCommitment.Of(Post, Reporter, rationale, Salt));

        Require(await store.AppendAsync(
            Require(AggregateId.Create("flag:" + flagId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(flagId)),
                Require(EventType.Create(FlagProjector.FlagCommittedType)),
                null,
                new JsonValue.Object(
                [
                    new(FlagProjector.CommitmentField, new JsonValue.String(commitment)),
                    new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>A flag as the log held one before R10.62: public, on the post's own stream.</summary>
    private static async Task RaiseLegacyAsync(InMemoryEventStore store, string flagId, FlagKind kind, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(flagId)),
                Require(EventType.Create(FlagProjector.FlagRaisedType)),
                Require(ActorId.Create(Reporter)),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(Post)),
                    new(FlagProjector.RaisedByField, new JsonValue.String(Reporter)),
                    new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
                    new(FlagProjector.RationaleField, new JsonValue.String("an old, public rationale")),
                ]))],
            ct).ConfigureAwait(false));
    }

    [Fact]
    public async Task R10_62_ACommittedFlagIsJoinedToItsPostAndRaiser()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct);

        var directory = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        var flag = Assert.Single(directory.Flags);
        Assert.Equal("01JFLAG000000000000000001", flag.FlagId);
        Assert.Equal(Post, flag.PostId);
        Assert.Equal(Reporter, flag.RaisedBy);
        Assert.Equal(FlagKind.Spam, flag.Kind);
        Assert.Equal(Start, flag.At.Value);
        Assert.Empty(directory.Skipped);
    }

    /// <summary>A log written before R10.62 still lists its flags; their disclosure is permanent, and they are not lost.</summary>
    [Fact]
    public async Task R10_62_ALegacyFlagIsStillListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseLegacyAsync(store, "01JLEGACY00000000000000001", FlagKind.Injection, ct);

        var flag = Assert.Single(FlagDirectory.Join(await LogAsync(store, ct), []).Flags);
        Assert.Equal(("01JLEGACY00000000000000001", Post, Reporter, FlagKind.Injection), (flag.FlagId, flag.PostId, flag.RaisedBy, flag.Kind));
    }

    /// <summary>
    /// A public commitment with no private row is not listed, and is counted — R11.31's shape for the
    /// one join this stage creates. Silence would make a lost row indistinguishable from no flag.
    /// </summary>
    [Fact]
    public async Task R10_62_ACommittedFlagWithNoDetailIsSkippedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct, storeDetail: false);

        var directory = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        Assert.Empty(directory.Flags);
        Assert.Equal(1, directory.Skipped[FlagDirectory.SkippedNoDetail]);
    }

    /// <summary>A private row that no longer opens its entry's commitment is not believed — the commitment is what makes substitution detectable.</summary>
    [Fact]
    public async Task R10_62_ATamperedDetailIsSkippedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct,
            commitmentOverride: Require(FlagCommitment.Of(Post, Reporter, "a different rationale", Salt)));

        var directory = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        Assert.Empty(directory.Flags);
        Assert.Equal(1, directory.Skipped[FlagDirectory.SkippedCommitmentMismatch]);
    }

    /// <summary>The projection carries no rationale: nothing that serves from it can echo one.</summary>
    [Fact]
    public void R10_44_ARaisedFlagCarriesNoRationale()
    {
        var strings = typeof(RaisedFlag).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal([nameof(RaisedFlag.FlagId), nameof(RaisedFlag.PostId), nameof(RaisedFlag.RaisedBy)], strings);
    }

    /// <summary>R11.9 (addendum): the directory rebuilds from both stores to the identical state, and a different log gives a different one.</summary>
    [Fact]
    public async Task R11_9_TheDirectoryRebuildsFromBothStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct);
        await RaiseLegacyAsync(store, "01JLEGACY00000000000000001", FlagKind.Injection, ct);

        var log = await LogAsync(store, ct);
        var rows = await DetailsAsync(details, ct);
        var first = FlagDirectory.Join(log, rows);
        var second = FlagDirectory.Join(log, rows);

        Assert.Equal(2, first.Flags.Length);
        Assert.True(first.Flags.SequenceEqual(second.Flags));
        Assert.True(first.Skipped.SequenceEqual(second.Skipped));

        // The negative control: a longer log must not rebuild to the same directory.
        await CommitAsync(store, details, "01JFLAG000000000000000002", FlagKind.Duplicate, ct, rationale: "a repeat");
        Assert.False(first.Flags.SequenceEqual(FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct)).Flags));
    }
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~FlagDirectoryTests"`
Expected: build FAILS with `CS0103: The name 'FlagDirectory' does not exist` and `CS0117: 'FlagProjector' does not contain a definition for 'FlagCommittedType'`.

- [ ] **Step 3: Write the directory**

Create `src/Curia.Application/Projections/FlagDirectory.cs`:

```csharp
using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// One flag, as the log and the private store together record it.
///
/// <para><b>There is no rationale here, and that is the design.</b> The rationale is
/// attacker-controlled text; a read model carrying it would let a serving path echo it, and a
/// rationale reading "this post leaks AKIA…" would republish the credential it reported. It stays in
/// the private store, where the operator's out-of-band listing reads it (R10.59).</para>
/// </summary>
/// <param name="FlagId">The flag's event id: what a moderation record's <c>adjudicates</c> names (R10.60).</param>
public sealed record RaisedFlag(string FlagId, string PostId, string RaisedBy, FlagKind Kind, ServerTimestamp At);

/// <summary>
/// R7.18's flags, joined from the two places R10.62 puts them.
///
/// <para><b>Two shapes.</b> A <c>flag.committed</c> entry names its kind and a commitment, and the
/// private row supplies the post and the raiser; the row is believed only if it still opens the
/// entry's commitment. A legacy <c>flag.raised</c> event — written before R10.62 — carries all of it
/// in the log, publicly and permanently, and is read as it stands.</para>
///
/// <para><b>Skips are counted, never silent</b> (R11.31's shape, for the join this stage creates). A
/// committed entry with no row, or with a row that no longer opens it, is listed in
/// <see cref="Skipped"/> by reason; a directory that dropped it quietly would read as a Forum where
/// that flag was never raised.</para>
///
/// <para><b>No clock</b>, for the reason every projector here gives: a rebuild that read "now" would
/// make R11.9's drill tautological.</para>
/// </summary>
public sealed record FlagDirectory(ImmutableArray<RaisedFlag> Flags, ImmutableSortedDictionary<string, int> Skipped)
{
    public const string SkippedNoDetail = "flag.committed: no detail";
    public const string SkippedCommitmentMismatch = "flag.committed: commitment mismatch";
    public const string SkippedUnreadableCommitted = "flag.committed: unreadable";
    public const string SkippedUnreadableLegacy = "flag.raised: unreadable";

    /// <summary>Joins a seq-ordered log with the private store's rows.</summary>
    public static FlagDirectory Join(IReadOnlyList<AppendedEvent> eventsInSeqOrder, IReadOnlyList<FlagDetail> details)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);
        ArgumentNullException.ThrowIfNull(details);

        var byEvent = new Dictionary<string, FlagDetail>(StringComparer.Ordinal);
        foreach (var detail in details)
            byEvent.TryAdd(detail.EventId, detail);

        var flags = ImmutableArray.CreateBuilder<RaisedFlag>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lastSeq = EventSequence.Zero;

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Seq < lastSeq)
                throw new ArgumentException(
                    "Events must arrive in ascending seq order; every IEventReader this solution " +
                    "ships already guarantees that, so a violation means the caller did not get " +
                    "these from a store's forward scan.",
                    nameof(eventsInSeqOrder));

            lastSeq = appended.Seq;

            var type = appended.Event.Type.Value;
            if (type != FlagProjector.FlagRaisedType && type != FlagProjector.FlagCommittedType) continue;

            var fields = appended.Event.Payload is JsonValue.Object payload
                ? FlagProjector.Members(payload)
                : new Dictionary<string, JsonValue>(StringComparer.Ordinal);

            var (flag, reason) = type == FlagProjector.FlagRaisedType
                ? Legacy(fields, appended)
                : Committed(fields, appended, byEvent);

            if (flag is not null) flags.Add(flag);
            else skipped[reason!] = skipped.GetValueOrDefault(reason!) + 1;
        }

        return new FlagDirectory(flags.ToImmutable(), skipped.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    private static (RaisedFlag? Flag, string? Reason) Legacy(Dictionary<string, JsonValue> fields, AppendedEvent appended) =>
        FlagProjector.Str(fields, FlagProjector.PostIdField, out var postId)
        && FlagProjector.Str(fields, FlagProjector.RaisedByField, out var raisedBy)
        && FlagProjector.Str(fields, FlagProjector.KindField, out var kindWire)
        && FlagKinds.Parse(kindWire).TryGetValue(out var kind, out _)
            ? (new RaisedFlag(appended.Event.Id.Value, postId, raisedBy, kind, appended.ServerTimestamp), null)
            : (null, SkippedUnreadableLegacy);

    private static (RaisedFlag? Flag, string? Reason) Committed(
        Dictionary<string, JsonValue> fields, AppendedEvent appended, Dictionary<string, FlagDetail> byEvent)
    {
        if (!FlagProjector.Str(fields, FlagProjector.KindField, out var kindWire)
            || !FlagKinds.Parse(kindWire).TryGetValue(out var kind, out _)
            || !FlagProjector.Str(fields, FlagProjector.CommitmentField, out var commitment))
            return (null, SkippedUnreadableCommitted);

        if (!byEvent.TryGetValue(appended.Event.Id.Value, out var detail))
            return (null, SkippedNoDetail);

        var recomputed = FlagCommitment.Of(detail.PostId, detail.RaisedBy, detail.Rationale, detail.Salt);
        if (!recomputed.TryGetValue(out var expected, out _) || !string.Equals(expected, commitment, StringComparison.Ordinal))
            return (null, SkippedCommitmentMismatch);

        return (new RaisedFlag(appended.Event.Id.Value, detail.PostId, detail.RaisedBy, kind, appended.ServerTimestamp), null);
    }
}
```

- [ ] **Step 4: Make the projector about moderation records alone**

Replace `src/Curia.Application/Projections/FlagProjection.cs` in full with:

```csharp
using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;

namespace Curia.Application.Projections;

/// <summary>
/// What §10.10's moderation records say about one post, in order (R10.60, R10.61).
///
/// <para><b>Flags are not here.</b> A flag's log entry names no post (R10.62), so which post a flag
/// concerns is known only to the private store; <see cref="FlagDirectory"/> joins the two. What a
/// post's moderation needs — whether it may be served, and which flags are upheld — is decided by
/// the records alone, which name the flags they adjudicate, so posture and serving read public
/// records only.</para>
/// </summary>
public sealed record PostModeration(string PostId, ImmutableArray<ModerationAction> History)
{
    /// <summary>R10.36, delegated to the domain: whether the serving path may still serve this post.</summary>
    public bool MayServe => ModerationPolicy.MayServe(History);

    /// <summary>R10.61: the flags this post's records currently uphold.</summary>
    public ImmutableHashSet<string> UpheldFlags => ModerationPolicy.UpheldFlags(History);

    /// <summary>
    /// Table 11's "no upheld flags", for one post: whether any flag its records adjudicated is upheld.
    /// False for a post nobody has adjudicated, however many flags it carries — see
    /// <see cref="ModerationPolicy.UpheldFlags"/> for why the alternative hands every agent a
    /// demotion primitive.
    /// </summary>
    public bool HasUpheldFlag => !UpheldFlags.IsEmpty;

    /// <summary>
    /// Structural equality, spelled out: <see cref="ImmutableArray{T}"/>'s own <c>Equals</c> compares
    /// the underlying array by reference, so generated record equality would report two folds of the
    /// same events as different and make R11.9's drill unassertable — and green.
    /// </summary>
    public bool Equals(PostModeration? other) =>
        other is not null
        && string.Equals(PostId, other.PostId, StringComparison.Ordinal)
        && History.SequenceEqual(other.History);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(PostId, History.Length);
}

/// <summary>
/// Folds <c>moderation.applied</c> records into per-post moderation state, and names every §10.10
/// event type and member.
///
/// <para><b>No clock</b>, for the reason <see cref="PostProjector"/> records: a rebuild that
/// consulted "now" would make R11.9's replay drill tautological.</para>
///
/// <para><b>A post no record names is absent</b> rather than present-and-empty. Absence is already
/// what the serving path means by "no moderation history".</para>
/// </summary>
public static class FlagProjector
{
    /// <summary>
    /// A flag as written before R10.62: its post, raiser and rationale in the log, publicly and
    /// permanently. Read by <see cref="FlagDirectory"/>; never written again.
    /// </summary>
    public const string FlagRaisedType = "flag.raised";

    /// <summary>A flag as <c>RaiseFlag</c> writes one (R10.62): its kind and a salted commitment, on its own aggregate, with no actor.</summary>
    public const string FlagCommittedType = "flag.committed";

    /// <summary>
    /// A moderation record. R10.37: "Every moderation action SHALL be a signed log entry with actor,
    /// category, and rationale."
    /// </summary>
    public const string ModerationAppliedType = "moderation.applied";

    /// <summary>The post a record acts on, or a legacy flag concerns.</summary>
    public const string PostIdField = "post_id";

    /// <summary>A legacy flag's raiser. Never written after R10.62.</summary>
    public const string RaisedByField = "raised_by";

    /// <summary>A flag's type, in R10.35's published spelling.</summary>
    public const string KindField = "kind";

    /// <summary>R10.37's "category" on a moderation record, in R10.35's published spelling.</summary>
    public const string CategoryField = "category";

    /// <summary>R10.36's moderator kind, which decides what the record was permitted to be.</summary>
    public const string ModeratorField = "moderator";

    /// <summary>R10.37's "actor".</summary>
    public const string ActorIdField = "actor_id";

    /// <summary>What the record did.</summary>
    public const string EffectField = "effect";

    /// <summary>A moderation record's rationale (R10.37), or a legacy flag's.</summary>
    public const string RationaleField = "rationale";

    /// <summary>R6.25's "a <c>moderation</c> record referencing a digest": the post's envelope digest (R10.60).</summary>
    public const string DigestField = "digest";

    /// <summary>The flags a moderation record adjudicates, by event id (R10.60, R10.61).</summary>
    public const string AdjudicatesField = "adjudicates";

    /// <summary>A committed flag's commitment (R10.62).</summary>
    public const string CommitmentField = "commitment";

    /// <summary>Folds a seq-ordered event list into per-post moderation state.</summary>
    public static ImmutableDictionary<string, PostModeration> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var actions = new Dictionary<string, ImmutableArray<ModerationAction>.Builder>(StringComparer.Ordinal);
        var lastSeq = EventSequence.Zero;

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Seq < lastSeq)
                throw new ArgumentException(
                    "Events must arrive in ascending seq order; every IEventReader this solution " +
                    "ships already guarantees that, so a violation means the caller did not get " +
                    "these from a store's forward scan.",
                    nameof(eventsInSeqOrder));

            lastSeq = appended.Seq;

            if (appended.Event.Type.Value != ModerationAppliedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            ApplyModeration(actions, Members(payload), appended);
        }

        return actions.ToImmutableDictionary(
            entry => entry.Key,
            entry => new PostModeration(entry.Key, entry.Value.ToImmutable()),
            StringComparer.Ordinal);
    }

    private static void ApplyModeration(
        Dictionary<string, ImmutableArray<ModerationAction>.Builder> actions,
        Dictionary<string, JsonValue> fields,
        AppendedEvent appended)
    {
        if (!Str(fields, PostIdField, out var postId)) return;
        if (!Str(fields, ActorIdField, out var actorId)) return;
        if (!Str(fields, RationaleField, out var rationale)) return;
        if (!Str(fields, ModeratorField, out var moderatorWire)) return;
        if (!Str(fields, EffectField, out var effectWire)) return;
        if (!Str(fields, CategoryField, out var categoryWire)) return;

        if (!ModeratorKinds.Parse(moderatorWire).TryGetValue(out var moderator, out _)) return;
        if (!ModerationEffects.Parse(effectWire).TryGetValue(out var effect, out _)) return;
        if (!FlagKinds.Parse(categoryWire).TryGetValue(out var category, out _)) return;

        // A record without the member still governs servability and upholds nothing (R10.61). It is
        // read, not dropped: dropping a withholding would serve what a human withheld.
        ImmutableArray<string> adjudicates = fields.TryGetValue(AdjudicatesField, out var named) && named is JsonValue.Array list
            ? [.. list.Items.OfType<JsonValue.String>().Select(s => s.Value)]
            : [];

        if (!actions.TryGetValue(postId, out var builder))
        {
            builder = ImmutableArray.CreateBuilder<ModerationAction>();
            actions[postId] = builder;
        }

        builder.Add(new ModerationAction(
            postId, moderator, actorId, effect, category, rationale, appended.ServerTimestamp, adjudicates));
    }

    internal static Dictionary<string, JsonValue> Members(JsonValue.Object payload)
    {
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in payload.Members)
            fields[member.Key] = member.Value;

        return fields;
    }

    internal static bool Str(Dictionary<string, JsonValue> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.String s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
```

The `ModerationAppliedType` doc comment above deliberately drops "Nothing writes this over HTTP yet". Task 9 adds the sentence naming R10.59's writer once the writer exists.

- [ ] **Step 5: Rewrite the projector's tests for moderation records alone**

Replace `tests/Curia.Application.Tests/Projections/FlagProjectorTests.cs` in full with:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// §10.10's moderation records, folded out of the log (R10.60, R10.61). Flags themselves are
/// <see cref="FlagDirectoryTests"/>' subject: a flag's entry names no post (R10.62). Every
/// <see cref="AppendedEvent"/> comes from a real append through <see cref="InMemoryEventStore"/>
/// (CS-15).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagProjectorTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Moderator = "operator:reviewer";
    private const string Flag = "01JFLAG000000000000000001";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static async Task ModerateAsync(
        InMemoryEventStore store,
        string eventId,
        FlagKind category,
        ModerationEffect effect,
        CancellationToken ct,
        ModeratorKind moderator = ModeratorKind.Human,
        bool withAdjudicates = true,
        params string[] adjudicates)
    {
        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        List<KeyValuePair<string, JsonValue>> members =
        [
            new(FlagProjector.PostIdField, new JsonValue.String(Post)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(moderator))),
            new(FlagProjector.ActorIdField, new JsonValue.String(Moderator)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
        ];

        if (withAdjudicates)
            members.Add(new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(a => (JsonValue)new JsonValue.String(a))])));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(eventId)),
                Require(EventType.Create(FlagProjector.ModerationAppliedType)),
                Require(ActorId.Create(Moderator)),
                new JsonValue.Object([.. members]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A flag on the post's own stream, as the log held one before R10.62. It is the one shape whose
    /// post a public fold could ever see, so it is the one a category-keyed fold would have upheld.
    /// </summary>
    private static async Task RaiseLegacyAsync(InMemoryEventStore store, string eventId, FlagKind kind, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(eventId)),
                Require(EventType.Create(FlagProjector.FlagRaisedType)),
                Require(ActorId.Create("https://agents.example/reporter")),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(Post)),
                    new(FlagProjector.RaisedByField, new JsonValue.String("https://agents.example/reporter")),
                    new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
                    new(FlagProjector.RationaleField, new JsonValue.String("reported after the withholding")),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>A post no record names is absent, not present-and-empty.</summary>
    [Fact]
    public async Task APostNoRecordNamesIsAbsentFromTheProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty(FlagProjector.Fold(await LogAsync(new InMemoryEventStore(new ManualTimeProvider(Start)), ct)));
    }

    /// <summary>R10.36: an automated quarantine takes the post out of the serving path — and upholds nothing (R10.61).</summary>
    [Fact]
    public async Task R10_36_AQuarantineMakesThePostUnservable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Quarantine, ct,
            ModeratorKind.Automated, true, Flag);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }

    /// <summary>A restore puts it back, with nothing to invalidate — the history is the state.</summary>
    [Fact]
    public async Task ARestoreMakesThePostServableAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Withhold, ct, ModeratorKind.Human, true, Flag);
        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Restore, ct, ModeratorKind.Human, true, Flag);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.True(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }

    /// <summary>Table 11 reads off this: a flag becomes upheld when a record that names it acts; a dismissal leaves it unupheld.</summary>
    [Fact]
    public async Task R10_61_AFlagIsUpheldOnlyWhenARecordThatNamesItActs()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Dismiss, ct, ModeratorKind.Human, true, Flag);
        Assert.False(FlagProjector.Fold(await LogAsync(store, ct))[Post].HasUpheldFlag);

        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Withhold, ct, ModeratorKind.Human, true, Flag);
        Assert.Equal([Flag], FlagProjector.Fold(await LogAsync(store, ct))[Post].UpheldFlags);
    }

    /// <summary>
    /// Spec Decision 5's late-flag test, carried across from Task 2 when flags left this fold. A
    /// withholding names the flags it reviewed. A flag raised against the post afterwards, in the same
    /// category, was reviewed by nobody. Keyed to the category, that flag was upheld the instant it was
    /// raised. Keyed to the record that names it, it is not upheld until a record does. The first half
    /// is also Decision 16: a proactive withholding moves no one's standing.
    /// </summary>
    [Fact]
    public async Task R10_61_AFlagRaisedAfterAWithholdingIsNotUpheldUntilARecordNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Spam, ModerationEffect.Withhold, ct);
        await RaiseLegacyAsync(store, Flag, FlagKind.Spam, ct);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);

        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Spam, ModerationEffect.Withhold, ct, ModeratorKind.Human, true, Flag);
        Assert.Equal([Flag], FlagProjector.Fold(await LogAsync(store, ct))[Post].UpheldFlags);
    }

    /// <summary>
    /// A record written without <c>adjudicates</c> — as hand-built fixtures wrote them before R10.60 —
    /// is read, not dropped: it still withholds, and it upholds nothing.
    /// </summary>
    [Fact]
    public async Task R10_61_ARecordWithoutAdjudicatesStillWithholdsAndUpholdsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Spam, ModerationEffect.Withhold, ct,
            ModeratorKind.Human, withAdjudicates: false);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }

    /// <summary>
    /// R11.9: the projection rebuilds from zero to the identical state. Meaningful only because
    /// <see cref="PostModeration"/> and <see cref="ModerationAction"/> spell out structural equality.
    /// </summary>
    [Fact]
    public async Task R11_9_TheProjectionRebuildsFromZeroToTheIdenticalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Quarantine, ct, ModeratorKind.Human, true, Flag);

        var log = await LogAsync(store, ct);
        Assert.Equal(FlagProjector.Fold(log), FlagProjector.Fold(log));

        // The negative control: two folds of different logs must not compare equal.
        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Restore, ct, ModeratorKind.Human, true, Flag);
        Assert.NotEqual(FlagProjector.Fold(log), FlagProjector.Fold(await LogAsync(store, ct)));
    }
}
```

- [ ] **Step 6: Serve R7.18's views from the directory**

In `src/Curia.Api/ForumEndpoints.cs`:

1. In `ListRaisedFlagsAsync`, add the parameter `IFlagDetailStore details,` directly after `IEventReader events,`. Then replace

```csharp
        var mine = FlagProjector.Fold(ok!.Log).Values
            .SelectMany(m => m.Flags)
            .Where(f => string.Equals(f.RaisedBy, ok.Subject, StringComparison.Ordinal));
```

with

```csharp
        var (flags, detailProblem) = await FlagsAsync(ok!.Log, details, cancellationToken).ConfigureAwait(false);
        if (detailProblem is not null) return detailProblem;

        var mine = flags.Where(f => string.Equals(f.RaisedBy, ok.Subject, StringComparison.Ordinal));
```

2. In `ListPostFlagsAsync`, add the same parameter after `IEventReader events,`. Then replace

```csharp
        var flags = FlagProjector.Fold(ok.Log).TryGetValue(postId, out var moderation)
            ? moderation.Flags.AsEnumerable()
            : [];
```

with

```csharp
        var (all, detailProblem) = await FlagsAsync(ok.Log, details, cancellationToken).ConfigureAwait(false);
        if (detailProblem is not null) return detailProblem;

        var flags = all.Where(f => string.Equals(f.PostId, postId, StringComparison.Ordinal));
```

3. Add this helper directly after `AuthorizeFlagListAsync`:

```csharp
    /// <summary>
    /// R7.18's flags, joined from the log's commitments and the private store (R10.62). A store that
    /// cannot be read is a 503: serving the log's half alone would list every committed flag as
    /// absent, which reads as "nothing was raised".
    /// </summary>
    private static async Task<(ImmutableArray<RaisedFlag> Flags, IResult? Problem)> FlagsAsync(
        IReadOnlyList<AppendedEvent> log, IFlagDetailStore details, CancellationToken cancellationToken)
    {
        var rows = await details.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(out var detailRows, out var error))
            return ([], Problem(StatusCodes.Status503ServiceUnavailable, error!));

        return (FlagDirectory.Join(log, detailRows!).Flags, null);
    }
```

In `tests/Curia.Api.Tests/FlagListingTests.cs:229`, the comment names `RaisedFlag`. It stays true, since the type still carries no rationale. Leave it.

- [ ] **Step 7: Build and run what this touches**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~FlagListingTests|FullyQualifiedName~FlagEndpointTests|FullyQualifiedName~SearchEndpointTests"
```

Expected:
- `0 Warning(s)`.
- `Curia.Application.Tests` passes in full, including the six new `FlagDirectoryTests`.
- The API suites pass unchanged. `RaiseFlag` still writes legacy flags, and the directory reads them.

- [ ] **Step 8: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'R7.18 views read a flag directory joined from the log and the private store\n\nThe moderation fold keeps records only; flags move to FlagDirectory, which reads\nlegacy flag.raised as it stands, believes a committed flag only if its private\nrow still opens the commitment, and counts every skip.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 6: Commit, don't publish — the writer, and the gate that proves it

**Files:**
- Modify (whole file): `src/Curia.Application/Moderation/RaiseFlag.cs`
- Modify: `src/Curia.Api/Program.cs` (`RaiseFlag` registration)
- Modify: `src/Curia.Api/ForumEndpoints.cs` (`StatusFor`: one arm)
- Modify: `src/Curia.Mcp/ToolText.cs:139-147`, `src/Curia.Mcp/WriteTools.cs:101-106`
- Test: `tests/Curia.Application.Tests/Moderation/RaiseFlagTests.cs`
- Test: `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs`

**Interfaces:**
- Consumes: `FlagCommitment.Of` (Task 3); `IFlagDetailStore` and `FlagDetail` (Task 4); `FlagProjector.FlagCommittedType` and `FlagProjector.CommitmentField` (Task 5).
- Produces:
  - `RaiseFlag(IEventStore events, IFlagDetailStore details, TimeProvider clock, Func<string>? newSalt = null)`. `RecordAsync` is unchanged in signature, and `FlagRaised` is unchanged.
  - `RaiseFlag.FlagAggregatePrefix = "flag:"`.
  - `public static class FlagSalt`, with `Bytes = 32` and `New()`, which returns 43 base64url characters.

- [ ] **Step 1: Write the gate, which is red on today's tree**

Create `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.62, R10.44 and errata G3's holding, as a gate: no surface the Forum serves hands a third
/// party a flag's rationale, its raiser, or — before a moderator adjudicates it — the post it concerns.
///
/// <para><b>The scope is the registrations, never a list beside the test.</b> The two flag-listing
/// routes were held to R10.44 by name, and the log route served every flag in full the whole time
/// (errata G13, finding 2) — trap 15, a classification that classified only what someone thought of.
/// Every route comes from the host's <see cref="EndpointDataSource"/>; a route this gate cannot drive
/// is a failure naming it (R14.9's discipline); and a route it deliberately does not drive is named in
/// <see cref="WriteRoutes"/>, where a reviewer can see it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class FlagPrivacyGateTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string EntriesRoute = "/v1/log/entries/{index:long}";

    /// <summary>Routes that write rather than read. A new route is placed here or given a driver, never neither.</summary>
    private static readonly HashSet<string> WriteRoutes = new(StringComparer.Ordinal)
    {
        "POST /v1/agents",
        "POST /v1/posts",
        "POST /v1/posts/{postId}/flags",
        "POST /v1/posts/{postId}/accept",
        "POST /oauth/token",
    };

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<Party> PartyAsync(HttpClient client, string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private List<(string Method, string Route)> Registered()
    {
        var source = forum.Services.GetRequiredService<EndpointDataSource>();
        var routes = new List<(string, string)>();

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                routes.Add((method, endpoint.RoutePattern.RawText ?? "(no pattern)"));

        return routes;
    }

    /// <summary>The requests that drive one route, or null when this gate has no driver for it.</summary>
    private static List<(string Url, object? Body)>? Drive(
        string method, string route, string postId, string digest, string board, string authorId, long treeSize)
    {
        List<(string, object?)> Each(string prefix) =>
            [.. Enumerable.Range(0, checked((int)treeSize)).Select(i => ($"{prefix}{i}", (object?)null))];

        return (method, route) switch
        {
            ("GET", EntriesRoute) => Each("/v1/log/entries/"),
            ("GET", "/v1/log/proof/{index:long}") => Each("/v1/log/proof/"),
            ("GET", "/v1/log/head") => [("/v1/log/head", null)],
            ("GET", "/v1/log/consistency") => [($"/v1/log/consistency?from=1&to={treeSize}", null)],
            ("GET", "/v1/log/jwks") => [("/v1/log/jwks", null)],
            ("GET", "/v1/posts/{postId}") => [($"/v1/posts/{postId}", null)],
            ("GET", "/v1/threads/{rootPostId}") => [($"/v1/threads/{postId}", null)],
            ("GET", "/v1/boards/{board}/posts") => [($"/v1/boards/{board}/posts", null)],
            ("GET", "/v1/search") => [($"/v1/search?q=ordering&board={board}", null)],
            ("GET", "/v1/inbox") => [($"/v1/inbox?board={board}", null)],
            ("GET", "/v1/flags") => [("/v1/flags", null)],
            ("GET", "/v1/posts/{postId}/flags") => [($"/v1/posts/{postId}/flags", null)],
            ("GET", "/v1/jwks") => [($"/v1/jwks?agent={Uri.EscapeDataString(authorId)}", null)],
            ("GET", "/.well-known/reader-contract/v1") => [("/.well-known/reader-contract/v1", null)],
            ("GET", "/health") => [("/health", null)],
            ("GET", "/oauth/jwks") => [("/oauth/jwks", null)],
            ("GET", "/.well-known/oauth-authorization-server") => [("/.well-known/oauth-authorization-server", null)],
            ("POST", "/v1/posts/batch") => [("/v1/posts/batch", new { digests = new[] { digest } })],
            _ => null,
        };
    }

    /// <summary>One request, anonymously or as <paramref name="who"/>; DPoP's <c>htu</c> excludes the query (RFC 9449 §4.2).</summary>
    private async Task<string> FetchAsync(HttpClient client, string url, object? body, Party? who, CancellationToken ct)
    {
        if (body is not null)
        {
            using var posted = await client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, ct);
            return await posted.Content.ReadAsStringAsync(ct);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", who.Token);
            request.Headers.Add("DPoP", who.Dpop.Proof("GET", "http://localhost" + url.Split('?')[0], forum.Now, who.Token, nonce: null));
        }

        using var response = await client.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<long> TreeSizeAsync(HttpClient client, CancellationToken ct)
    {
        for (long i = 0; i < 100_000; i++)
        {
            using var response = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return i;
        }

        throw new InvalidOperationException("the log did not end within 100,000 entries; this gate would not finish");
    }

    /// <summary>A question, and a flag against it from an agent that authors nothing, carrying a nonce rationale.</summary>
    private sealed record Flagged(Party Author, Party Raiser, Party Bystander, string Board, string PostId, string Digest, string Nonce);

    private async Task<Flagged> FlagAQuestionAsync(HttpClient client, CancellationToken ct)
    {
        var board = "gate-" + Guid.NewGuid().ToString("N")[..8];

        var author = await PartyAsync(client, "gate-author", ct);
        var raiser = await PartyAsync(client, "gate-raiser", ct);
        var bystander = await PartyAsync(client, "gate-bystander", ct);

        using var asked = await author.Dpop.PostAsync(
            client, PostsUrl, author.Token,
            author.Agent.SignQuestion(board, "How does JCS order object members?", "Member ordering " + Guid.NewGuid().ToString("N")[..8], forum.Now),
            forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);

        string postId, digest;
        using (var receipt = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct)))
        {
            postId = receipt.RootElement.GetProperty("post_id").GetString()!;
            digest = receipt.RootElement.GetProperty("digest").GetString()!;
        }

        // The raiser authors nothing, so its identifier appearing anywhere but its own enrolment is the flag talking.
        var nonce = "rationale-nonce-" + Guid.NewGuid().ToString("N");
        using var raised = await raiser.Dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "spam", rationale = nonce })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);

        return new Flagged(author, raiser, bystander, board, postId, digest, nonce);
    }

    /// <summary>What one sweep of every registered surface met.</summary>
    private sealed record Sweep(
        List<string> Undriven, List<string> Leaks, int Fetched, long TreeSize, bool SawPostLeaf, List<string> FlagLeaves, string? RecordLeaf);

    /// <summary>
    /// Drives every registered read surface, anonymously and as the bystander, and records each
    /// response that serves the flag's rationale, its raiser, or, in the flag's own leaf, its post.
    /// </summary>
    private async Task<Sweep> SweepAsync(HttpClient client, Flagged flagged, CancellationToken ct)
    {
        var treeSize = await TreeSizeAsync(client, ct);
        var undriven = new List<string>();
        var leaks = new List<string>();
        var fetched = 0;
        var sawPostLeaf = false;
        var flagLeaves = new List<string>();
        string? recordLeaf = null;

        foreach (var (method, route) in Registered())
        {
            var key = $"{method} {route}";
            if (WriteRoutes.Contains(key)) continue;

            var requests = Drive(method, route, flagged.PostId, flagged.Digest, flagged.Board, flagged.Author.Agent.AgentId, treeSize);
            if (requests is null)
            {
                undriven.Add(key);
                continue;
            }

            foreach (var (url, body) in requests)
            {
                foreach (var who in (Party?[])[null, flagged.Bystander])
                {
                    if (body is not null && who is not null) continue; // the batch is an anonymous read

                    var served = await FetchAsync(client, url, body, who, ct);
                    fetched++;
                    var reader = who is null ? "an anonymous caller" : "an uninvolved agent";

                    if (served.Contains(flagged.Nonce, StringComparison.Ordinal))
                        leaks.Add($"{key} served the flag's rationale to {reader} ({url})");

                    var ownEnrolment = served.Contains("\"event_type\":\"agent.", StringComparison.Ordinal);
                    if (served.Contains(flagged.Raiser.Agent.AgentId, StringComparison.Ordinal) && !ownEnrolment)
                        leaks.Add($"{key} served the raiser's identity to {reader} ({url})");

                    if (route == EntriesRoute)
                    {
                        if (served.Contains("\"post.accepted\"", StringComparison.Ordinal)
                            && served.Contains(flagged.PostId, StringComparison.Ordinal))
                            sawPostLeaf = true;

                        if (served.Contains("\"event_type\":\"flag.", StringComparison.Ordinal))
                        {
                            flagLeaves.Add(served);

                            // R10.62: the post stays out of the flag's own leaf, whatever else becomes public later.
                            if (served.Contains(flagged.PostId, StringComparison.Ordinal))
                                leaks.Add($"{key} served the flagged post's id in the flag's own leaf to {reader} ({url})");
                        }

                        if (served.Contains("\"event_type\":\"moderation.applied\"", StringComparison.Ordinal)
                            && served.Contains(flagged.PostId, StringComparison.Ordinal))
                            recordLeaf = served;
                    }
                }
            }
        }

        return new Sweep(undriven, leaks, fetched, treeSize, sawPostLeaf, flagLeaves, recordLeaf);
    }

    /// <summary>Non-vacuity, each in its own assertion: a failure here is a defect in this gate, not in the Forum.</summary>
    private static void AssertTheSweepReachedEverything(Sweep sweep)
    {
        Assert.True(sweep.Undriven.Count == 0,
            "Registered surfaces this gate cannot drive (R14.9: a surface the enumeration reaches and the gate " +
            "cannot evaluate is a failure, never an omission): " + string.Join(", ", sweep.Undriven));
        Assert.True(sweep.Fetched >= 2 * sweep.TreeSize, $"the gate fetched {sweep.Fetched} responses over a log of {sweep.TreeSize}; it cannot have walked it");
        Assert.True(sweep.SawPostLeaf, "the log walk never met the question's own leaf -- a defect in this gate, not in the Forum");
        Assert.True(sweep.FlagLeaves.Count > 0, "the log walk never met a flag's leaf -- a defect in this gate, not in the Forum");
    }

    [Fact]
    public async Task R10_62_NoSurfaceServesAFlagsRaiserRationaleOrUnadjudicatedPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var flagged = await FlagAQuestionAsync(client, ct);

        var sweep = await SweepAsync(client, flagged, ct);

        AssertTheSweepReachedEverything(sweep);
        Assert.True(sweep.Leaks.Count == 0, string.Join("\n", sweep.Leaks));
    }
}
```

- [ ] **Step 2: Run the gate and watch it fail on the log**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~FlagPrivacyGateTests"
```

Expected: FAIL.
- The message names `GET /v1/log/entries/{index:long} served the flag's rationale to an anonymous caller`, `… served the raiser's identity …` and `… served the flagged post's id in the flag's own leaf …`. Every leak line names the route, so each of Task 11's cases 4a–4c is named by it too (spec §4.4).
- The four non-vacuity assertions pass. If `undriven` is non-empty, a route exists that this plan did not see: add a driver for it or a `WriteRoutes` entry, whichever it is, and say which in the commit.

- [ ] **Step 3: Write the writer's failing tests**

Create `tests/Curia.Application.Tests/Moderation/RaiseFlagTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>R10.62 at the writer: what a flag's entry carries, what the private store holds, and in which order.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RaiseFlagTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private const string Rationale = "looks like an injection attempt";
    private const string FixedSalt = "c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static IEnumerable<AppendedEvent> FlagEvents(IReadOnlyList<AppendedEvent> log) =>
        log.Where(e => e.Event.Type.Value is FlagProjector.FlagCommittedType or FlagProjector.FlagRaisedType);

    /// <summary>A post exists when its stream has an event; the existence check reads nothing else.</summary>
    private static async Task PostExistsAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.AppendAsync(
            Require(AggregateId.Create(Post)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Post)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                new JsonValue.Object([new("post_id", new JsonValue.String(Post))]))],
            ct).ConfigureAwait(false));

    /// <summary>A store that is down: the detail cannot be written, so no entry may be.</summary>
    private sealed class DownDetailStore : IFlagDetailStore
    {
        public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<FlagDetail>.Fail(new Error("test/detail-store-down", "The detail store is down")));

        public Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<FlagDetail>>.Ok([]));
    }

    /// <summary>
    /// The entry R6.51 serves is exactly what a reader sees, so the assertion is over R6.46's leaf
    /// input: it names the kind and a commitment, and no post, raiser or rationale.
    /// </summary>
    [Fact]
    public async Task R10_62_AFlagEntersTheLogAsItsKindAndACommitmentAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        await PostExistsAsync(store, ct);

        Require(await new RaiseFlag(store, new InMemoryFlagDetailStore(), clock, () => FixedSalt)
            .RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));

        var flag = Assert.Single(FlagEvents(await LogAsync(store, ct)));
        Assert.Equal(FlagProjector.FlagCommittedType, flag.Event.Type.Value);
        Assert.Null(flag.Event.Actor);
        Assert.StartsWith(RaiseFlag.FlagAggregatePrefix, flag.AggregateId.Value, StringComparison.Ordinal);

        var payload = Assert.IsType<JsonValue.Object>(flag.Event.Payload);
        Assert.Equal([FlagProjector.CommitmentField, FlagProjector.KindField], payload.Members.Select(m => m.Key).Order(StringComparer.Ordinal));

        var leaf = Encoding.UTF8.GetString(Require(LogLeaf.Input(flag)).Span);
        Assert.DoesNotContain(Post, leaf, StringComparison.Ordinal);
        Assert.DoesNotContain(Reporter, leaf, StringComparison.Ordinal);
        Assert.DoesNotContain(Rationale, leaf, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"spam\"", leaf, StringComparison.Ordinal);
    }

    /// <summary>The private row names the entry's event, and opens its commitment.</summary>
    [Fact]
    public async Task R10_62_ThePrivateRowOpensTheEntrysCommitment()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        Require(await new RaiseFlag(store, details, clock, () => FixedSalt).RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));

        var flag = Assert.Single(FlagEvents(await LogAsync(store, ct)));
        var row = Assert.Single(Require(await details.ReadAllAsync(ct)));
        Assert.Equal(new FlagDetail(flag.Event.Id.Value, Post, Reporter, Rationale, FixedSalt), row);

        var commitment = ((JsonValue.String)((JsonValue.Object)flag.Event.Payload).Members
            .Single(m => m.Key == FlagProjector.CommitmentField).Value).Value;
        Assert.Equal(Require(FlagCommitment.Of(Post, Reporter, Rationale, FixedSalt)), commitment);
    }

    /// <summary>Review Focus 2: the same text flagged twice commits differently, so a reader cannot count one raiser's repeats.</summary>
    [Fact]
    public async Task R10_62_TwoIdenticalFlagsCommitDifferently()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        var raise = new RaiseFlag(store, details, clock);
        Require(await raise.RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));
        Require(await raise.RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));

        var salts = Require(await details.ReadAllAsync(ct)).Select(d => d.Salt).ToArray();
        Assert.Equal(2, salts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(salts, s => Assert.Equal(43, s.Length));

        var commitments = FlagEvents(await LogAsync(store, ct))
            .Select(e => ((JsonValue.Object)e.Event.Payload).Members.Single(m => m.Key == FlagProjector.CommitmentField).Value)
            .ToArray();
        Assert.NotEqual(commitments[0], commitments[1]);
    }

    /// <summary>
    /// The private row first (spec Decision 4): a detail that cannot be written leaves no public entry
    /// to open. Checked first the way a reader meets the failure, through the join, which skips and
    /// counts a commitment that has no row. Written the other way round, this is where it shows.
    /// </summary>
    [Fact]
    public async Task R10_62_AFailedDetailAppendLeavesNoCommitmentForTheJoinToSkip()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new DownDetailStore();
        await PostExistsAsync(store, ct);

        var result = await new RaiseFlag(store, details, clock).RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("test/detail-store-down", error!.Type);

        var log = await LogAsync(store, ct);
        Assert.Empty(FlagDirectory.Join(log, Require(await details.ReadAllAsync(ct))).Skipped);
        Assert.Empty(FlagEvents(log));
    }

    /// <summary>Review Focus 1: a rationale the store cannot hold is refused by name, and nothing is written anywhere.</summary>
    [Fact]
    public async Task R11_21_ARationaleCarryingANulIsRefusedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        var result = await new RaiseFlag(store, details, clock).RecordAsync(Post, Reporter, FlagKind.Spam, "bad\0rationale", ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/detail-unstorable", error!.Type);
        Assert.Empty(FlagEvents(await LogAsync(store, ct)));
        Assert.Empty(Require(await details.ReadAllAsync(ct)));
    }

    /// <summary>A flag against a post the log never accepted is refused, and neither store is written: the log stays empty.</summary>
    [Fact]
    public async Task AFlagAgainstAPostThatDoesNotExistIsRefusedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();

        var result = await new RaiseFlag(store, details, clock).RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/no-such-post", error!.Type);
        Assert.Empty(Require(await details.ReadAllAsync(ct)));
        Assert.Empty(await LogAsync(store, ct));
    }

    /// <summary>R10.26: a credential in the rationale is refused before either store sees it.</summary>
    [Fact]
    public async Task R10_26_ACredentialInTheRationaleIsRefusedBeforeEitherStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        var result = await new RaiseFlag(store, details, clock)
            .RecordAsync(Post, Reporter, FlagKind.CredentialLeak, "Leaks AKIAIOSFODNN7EXAMPLE in the body.", ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/rationale-rejected", error!.Type);
        Assert.DoesNotContain("AKIA", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(FlagEvents(await LogAsync(store, ct)));
        Assert.Empty(Require(await details.ReadAllAsync(ct)));
    }
}
```

- [ ] **Step 4: Run them to see them fail**

Run: `dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~RaiseFlagTests"`
Expected: build FAILS with `CS1729: 'RaiseFlag' does not contain a constructor that takes 4 arguments`.

- [ ] **Step 5: Rewrite the writer**

Replace `src/Curia.Application/Moderation/RaiseFlag.cs` in full with:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when a flag was accepted.</summary>
/// <param name="PostId">The post flagged.</param>
/// <param name="Kind">The flag's type.</param>
/// <param name="RaisedAt">The store's <c>server_ts</c> for the append — R6.5's Forum observation.</param>
public sealed record FlagRaised(string PostId, FlagKind Kind, DateTimeOffset RaisedAt);

/// <summary>
/// R10.35's "any credentialed agent MAY flag content", as a use case.
///
/// <para><b>The flag is committed, not published (R10.62).</b> R6.46 and R6.47 make every event a
/// leaf and R6.51 serves every leaf's input to anyone, so a flag written as an event carrying its
/// raiser and rationale was published — executed in errata G13. The post, the raiser, the rationale
/// and a fresh salt go to the private store (R11.32); the log receives <c>flag.committed</c> on the
/// flag's own aggregate, with no actor, naming the kind and a salted commitment to the rest. The
/// kind and instant stay public so R10.39's volume by category is auditable; which post it concerns
/// becomes public only when a moderation record adjudicates it (R10.60).</para>
///
/// <para><b>The private row is written first.</b> A row whose entry never lands is read by nothing,
/// since every read starts from the log. An entry whose row never landed would be a public
/// commitment nobody can open; <see cref="FlagDirectory"/> counts one if it ever exists, but the order
/// here means it should not.</para>
///
/// <para><b>The rationale is screened</b> under the two-regime table the post body uses: credential
/// material is refused (R10.26) and injection patterns are annotated. It persists in a store with no
/// redaction primitive behind it, and R10.28's argument applies unchanged.</para>
///
/// <para><b>Attributed by token rather than signed.</b> R10.37 requires a <i>moderation action</i> to
/// be a signed log entry; R10.35 requires no such thing of a flag. The raiser is the DPoP-bound
/// principal the transport authenticated.</para>
/// </summary>
public sealed class RaiseFlag
{
    /// <summary>
    /// A flag's own aggregate. Not its post's stream: the aggregate id is a member of the leaf
    /// (R6.46), and the post must not be (R10.62).
    /// </summary>
    public const string FlagAggregatePrefix = "flag:";

    private readonly IEventStore _events;
    private readonly IFlagDetailStore _details;
    private readonly UlidGenerator _ids;
    private readonly Func<string> _newSalt;

    /// <param name="newSalt">The salt source. <see cref="FlagSalt.New"/> in production; a fixed value only in tests that pin a commitment.</param>
    public RaiseFlag(IEventStore events, IFlagDetailStore details, TimeProvider clock, Func<string>? newSalt = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _details = details;
        _ids = new UlidGenerator(clock);
        _newSalt = newSalt ?? FlagSalt.New;
    }

    /// <summary>Records a flag against <paramref name="postId"/>, or reports why it was not recorded.</summary>
    /// <param name="postId">The post being flagged. Recorded privately, never in the log (R10.62).</param>
    /// <param name="raisedBy">The authenticated principal raising it. Recorded privately.</param>
    /// <param name="kind">One of R10.35's seven types. Public.</param>
    /// <param name="rationale">Required, screened, and recorded privately.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<FlagRaised>> RecordAsync(
        string postId,
        string raisedBy,
        FlagKind kind,
        string rationale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(raisedBy);

        if (string.IsNullOrWhiteSpace(rationale))
            return Result<FlagRaised>.Fail(ModerationErrors.RationaleRequired());

        // SCREEN, before anything is written. The screener takes a span, which cannot be stored in
        // a field, so this phase structurally cannot retain what it screened.
        var screened = ContentScreener.ScreenText(Encoding.UTF8.GetBytes(rationale));
        if (!screened.TryGetValue(out var screening, out var screeningError))
            return Result<FlagRaised>.Fail(screeningError!);

        if (!screening!.MayPersist)
            return Result<FlagRaised>.Fail(FlagErrors.RationaleRejected(screening.Annotations));

        if (!AggregateId.Create(postId).TryGetValue(out var post, out var postError))
            return Result<FlagRaised>.Fail(postError!);

        // The post's own stream, read to establish that there is a post at all: an append-only store
        // cannot take back a flag raised against a typo.
        var history = await _events.ReadByAggregateAsync(post, cancellationToken).ConfigureAwait(false);
        if (!history.TryGetValue(out var events, out var readError))
            return Result<FlagRaised>.Fail(readError!);

        if (events!.Count == 0)
            return Result<FlagRaised>.Fail(FlagErrors.NoSuchPost(postId));

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<FlagRaised>.Fail(idError!);

        var id = ulid.ToString();
        if (!EventId.Create(id).TryGetValue(out var eventId, out var eventIdError))
            return Result<FlagRaised>.Fail(eventIdError!);

        if (!AggregateId.Create(FlagAggregatePrefix + id).TryGetValue(out var aggregate, out var aggregateError))
            return Result<FlagRaised>.Fail(aggregateError!);

        if (!EventType.Create(FlagProjector.FlagCommittedType).TryGetValue(out var type, out var typeError))
            return Result<FlagRaised>.Fail(typeError!);

        var salt = _newSalt();
        if (!FlagCommitment.Of(postId, raisedBy, rationale, salt).TryGetValue(out var commitment, out var commitmentError))
            return Result<FlagRaised>.Fail(commitmentError!);

        // The private row first (see the remarks above).
        var stored = await _details
            .AppendAsync(new FlagDetail(id, postId, raisedBy, rationale, salt), cancellationToken)
            .ConfigureAwait(false);
        if (!stored.TryGetValue(out _, out var storeError)) return Result<FlagRaised>.Fail(storeError!);

        var payload = new JsonValue.Object(
        [
            new(FlagProjector.CommitmentField, new JsonValue.String(commitment!)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, AggregateVersion.New, [new DomainEvent(eventId, type, null, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(recorded => new FlagRaised(postId, kind, recorded[0].ServerTimestamp.Value));
    }
}

/// <summary>The salt a flag's commitment is taken with (R10.62): 32 random bytes, base64url.</summary>
public static class FlagSalt
{
    public const int Bytes = 32;

    /// <summary>A fresh salt; 43 characters.</summary>
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));
}

/// <summary>RFC 9457 problem-type slugs the flag path emits.</summary>
public static class FlagErrors
{
    /// <summary>
    /// A flag against a post the log has no record of. Named as its own condition rather than
    /// reported as a generic 404, so a client can tell "there is no such post" from "this route does
    /// not exist" — which return the same status and are very different problems.
    /// </summary>
    public static Error NoSuchPost(string postId) => new(
        "curia/flag/no-such-post",
        "No such post",
        $"post={postId}");

    /// <summary>
    /// R10.26/R10.28: the rationale carried credential material and was refused. The detail names
    /// the category and its position (R10.27) and never the matched value — structurally, because
    /// <c>RiskFlag</c> has no member that can carry content.
    /// </summary>
    public static Error RationaleRejected(RiskAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var categories = string.Join(
            ", ",
            annotations.Flags.Select(f => $"{f.Category}@{f.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        return new Error(
            "curia/flag/rationale-rejected",
            "The flag's rationale was rejected by ingest screening",
            categories);
    }
}
```

In `src/Curia.Api/Program.cs`, replace the `RaiseFlag` registration with:

```csharp
        // R10.35's flag path. Holds IEventStore for the reason EnrollAgent does, and the private
        // store because a flag's post, raiser and rationale are never an event (R10.62, R11.32).
        builder.Services.AddSingleton(sp => new RaiseFlag(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<IFlagDetailStore>(),
            sp.GetRequiredService<TimeProvider>()));
```

In `src/Curia.Api/ForumEndpoints.cs`, in `StatusFor`, add this arm before the `_ =>` default:

```csharp
        // The private store could not be written; nothing was, in either store (R10.62).
        "curia/flag/detail-store-unavailable" => StatusCodes.Status503ServiceUnavailable,
```

- [ ] **Step 6: Correct the two sentences the gate proved false**

In `src/Curia.Mcp/ToolText.cs`, in `FlagTemplate`, replace

```csharp
        "Any other value is refused. The rationale is required and is recorded under this agent's " +
        "identity; neither it nor who raised the flag is ever served back to anyone, including the " +
        "post's author. A flag is attributed by the authenticated session rather than signed, and " +
        "raising one removes nothing by itself.\n\n" +
```

with

```csharp
        "Any other value is refused. The rationale is required. Who raised a flag and why are never " +
        "published: the Forum's log records only that a flag of this kind was raised and when, and " +
        "which post it concerns becomes public only if a moderator acts on it. A flag is attributed " +
        "by the authenticated session rather than signed, and raising one removes nothing by " +
        "itself.\n\n" +
```

In `src/Curia.Mcp/WriteTools.cs`, replace

```csharp
            $"The rationale is recorded under this agent's identity and is never served back to anyone, " +
            $"including the post's author (R10.44). The flag removes nothing by itself."));
```

with

```csharp
            $"The rationale and who raised the flag are never published: the Forum's log records only " +
            $"that a flag of this kind was raised and when (R10.62). The flag removes nothing by itself; " +
            $"a moderator decides."));
```

- [ ] **Step 7: Run the writer's tests, the gate, and the suites that raise flags**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~RaiseFlagTests|FullyQualifiedName~FlagDirectoryTests"
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~FlagPrivacyGateTests|FullyQualifiedName~FlagListingTests|FullyQualifiedName~FlagEndpointTests|FullyQualifiedName~McpWriteEndToEndTests|FullyQualifiedName~StubFidelityTests"
dotnet test tests/Curia.Mcp.Tests -c Release --nologo
```

Expected:
- `0 Warning(s)`.
- The 7 `RaiseFlagTests` pass.
- `R10_62_NoSurfaceServesAFlagsRaiserRationaleOrUnadjudicatedPost` passes.
- `FlagListingTests` still shows the author and the raiser their flags, now through the join.
- `StubFidelityTests` passes, because the flag receipt did not change.
- `ToolDescriptionTests.R10_35_TheFlagDescriptionNamesExactlyTheForumsKinds` passes, because the kind sentence is untouched.

- [ ] **Step 8: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'A flag enters the log as its kind and a salted commitment (R10.62)\n\nThe post, raiser, rationale and salt go to flag_details first; flag.committed\nlands on its own aggregate with no actor. The disclosure gate walks every\nregistered surface and was red on the log-entry route before this commit.\nThe curia_flag description and result no longer promise what the log broke.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---
### Task 7: The Acta over the new entry kind — both implementations, and `curia-testis` end to end

**Files:**
- Create: `conformance/acta/flag-committed-entry/{input.json,expected.canonical,expected.digest,expected.leaf,meta.json}`
- Modify: `conformance/index.json` (`acta` count 5→6)
- Modify: `conformance/README.md` (one paragraph in "The `acta/` family")
- Modify: `rust/curia-testis/tests/vectors.rs:646-693` (the hand count: 69→70, 75→76 twice)
- Test: `tests/Curia.Api.Tests/ActaEndpointTests.cs` (one test, one `using`)

**Interfaces:**
- Consumes: `flag.committed`'s shape, from Tasks 5 and 6.
- Produces: nothing for later tasks. This task is the evidence that R15.1's frozen leaf computation covers the new entry kind unchanged, in C# and in Rust.

**What R15.1 and G9 say here.** R6.46's leaf is one event rendered as `{actor_id, aggregate_id, event_id, event_type, payload, server_ts}` under pure RFC 8785. G9 chose "one encoding for every entry class, distinguished by `event_type` within the hashed bytes" precisely so that a new class of entry is a payload decision. `flag.committed` is that case:
- a new `event_type`;
- a null `actor_id`, which `null-actor-entry` already pins;
- a two-member payload.

**No Rust verifier code changes.** `curia-testis`'s `log` verbs and its `acta` runner never dispatch on `event_type`. What changes in Rust is one *test*: `corpus_size_matches_charter` hard-counts the corpus on purpose, so adding a vector must move its literal.

**Legacy entries.** `flag.raised` entries already in any log keep their leaves exactly. Nothing re-hashes them, and nothing may. The only logs that exist are the throwaway databases the fixtures create and drop.

- [ ] **Step 1: Recompute the vector's values independently**

```bash
python3 - <<'EOF'
import hashlib, json
entry = {"actor_id": None, "aggregate_id": "flag:01K5ZQ8M3N4P5Q6R7S8T9V0W1X", "event_id": "01K5ZQ8M3N4P5Q6R7S8T9V0W1X",
         "event_type": "flag.committed",
         "payload": {"commitment": "sha256:6800fba2224d9bb7f7a7c7f90b6094a9215f1cf9e8c1d924da04e8b173152715", "kind": "spam"},
         "server_ts": "2026-09-26T12:00:00.000000Z"}
c = json.dumps(entry, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
print(c.decode()); print(hashlib.sha256(c).hexdigest()); print(hashlib.sha256(b"\x00" + c).hexdigest())
EOF
```

Expected:

```
{"actor_id":null,"aggregate_id":"flag:01K5ZQ8M3N4P5Q6R7S8T9V0W1X","event_id":"01K5ZQ8M3N4P5Q6R7S8T9V0W1X","event_type":"flag.committed","payload":{"commitment":"sha256:6800fba2224d9bb7f7a7c7f90b6094a9215f1cf9e8c1d924da04e8b173152715","kind":"spam"},"server_ts":"2026-09-26T12:00:00.000000Z"}
2f8eb4ac1634c330de29a08a8f8a3f9e7e9ccde7d47d62f0f55515fe8cb942d2
66128f1fe528857613fef8e66d312b65214bfd0d7b7a7aa82ca5e0ab2cf2219c
```

The commitment inside is an opaque, shape-valid commitment: `sha256:` and 64 lowercase hex, which is all the leaf encoding sees of one. It is not Task 3's pinned value and opens to no private row; this vector tests the leaf, not the commitment. Every string is ASCII with no escapes and there are no numbers, so `json.dumps` is RFC 8785 here, exactly as for the family's other vectors.

- [ ] **Step 2: Write the vector, byte for byte**

```bash
d=conformance/acta/flag-committed-entry && mkdir -p "$d"
printf '%s' '{"actor_id":null,"aggregate_id":"flag:01K5ZQ8M3N4P5Q6R7S8T9V0W1X","event_id":"01K5ZQ8M3N4P5Q6R7S8T9V0W1X","event_type":"flag.committed","payload":{"commitment":"sha256:6800fba2224d9bb7f7a7c7f90b6094a9215f1cf9e8c1d924da04e8b173152715","kind":"spam"},"server_ts":"2026-09-26T12:00:00.000000Z"}' > "$d/input.json"
cp "$d/input.json" "$d/expected.canonical"
printf '%s\n' 2f8eb4ac1634c330de29a08a8f8a3f9e7e9ccde7d47d62f0f55515fe8cb942d2 > "$d/expected.digest"
printf '%s\n' 66128f1fe528857613fef8e66d312b65214bfd0d7b7a7aa82ca5e0ab2cf2219c > "$d/expected.leaf"
cat > "$d/meta.json" <<'EOF'
{
  "profile": "acta-leaf",
  "requirement": "R6.46",
  "note": "A flag as R10.62 writes one: event_type flag.committed on the flag's own aggregate, a null actor, and a payload of its kind and a salted commitment -- no post, no raiser, no rationale. The commitment is opaque here: a shape-valid sha256: value that opens to no private row, since the leaf encoding never looks inside it. Expected values were computed with python3's json and hashlib, outside both implementations; a new entry kind is a payload decision under G9's one encoding, and this vector is the evidence that both runners agree on it unchanged."
}
EOF
xxd "$d/input.json" | tail -1; xxd "$d/expected.digest" | tail -1
```

Expected:
- `input.json` and `expected.canonical` end with `}` and no newline (`7d`), like `null-actor-entry`'s.
- `expected.digest` and `expected.leaf` end with `0a`.

In `conformance/index.json`, change the `acta` entry's `"count": 5` to `"count": 6`.

In `conformance/README.md`'s section "The `acta/` family", add this paragraph after the one ending "the two families describe the same post.":

```markdown
`flag-committed-entry` pins the entry kind errata G13 introduced (R10.62): a flag written as its
kind and a salted commitment, on its own aggregate, with no actor. Nothing about the encoding
changed to admit it -- that is the point of G9's one encoding for every entry class -- and the
vector is the evidence, run by all three runners. Its values were computed with python3's
`json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)` and `hashlib`, which is
RFC 8785 for an all-ASCII, number-free entry.
```

- [ ] **Step 3: Move the Rust hand count, and run every runner**

In `rust/curia-testis/tests/vectors.rs`, in `corpus_size_matches_charter`'s doc comment:
- Change `unicode 6, envelope 8, merkle 9, acta 5 — 69 vector directories — plus` to `unicode 6, envelope 8, merkle 9, acta 6 — 70 vector directories — plus`.
- Change `the 6 vendored \`rfc8785/\` file pairs, 75 in all.` to `the 6 vendored \`rfc8785/\` file pairs, 76 in all.`
- Append this sentence to the parenthetical that begins `(Envelope grew from 6 to`: `Acta grew from 5 to 6 with errata G13's \`flag-committed-entry\`.`

In the body, change the three literals as follows:
- `        69,` → `        70,`
- `assert_eq!(c.total_len(), 75,` → `assert_eq!(c.total_len(), 76,`
- `        declared, 75,` → `        declared, 76,`

```bash
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked --test vectors
dotnet test tests/Curia.Canon.Tests -c Release --nologo --filter "FullyQualifiedName~ActaLeafVectorTests|FullyQualifiedName~ConformanceIndexTests"
dotnet test tests/Curia.Client.Tests -c Release --nologo --filter "FullyQualifiedName~ActaLeafRecomputationTests"
```

Expected:
- `cargo fmt` is clean and `clippy` reports no warnings.
- `vectors` passes, including `acta` and `corpus_size_matches_charter`.
- `ActaLeafVectorTests` and `ConformanceIndexTests` pass.
- `ActaLeafRecomputationTests` shows six theory rows, all passing. The new row is `flag-committed-entry`.

Watch this vector go red **before** trusting it. Change one hex digit of `expected.leaf`, then run the three runners again:
- `cargo test --test vectors` must fail, and its message names `flag-committed-entry`.
- `ActaLeafRecomputationTests` must fail on exactly one theory row, `R6_46_TheClientRecomputesEveryPublishedLeaf(name: "flag-committed-entry")`. This is the C# runner that names the vector.
- `ActaLeafVectorTests` must fail too, but it does **not** name the vector. It loops over the family inside one fact and asserts the leaf with no message, so it prints only the expected and computed values (the edited digits against `66128f1f…`). That is enough to see it read the file; it is not a name.

Then restore it by re-running Step 2's `printf '%s\n' 66128f1f… > "$d/expected.leaf"` line, and check `git diff --quiet -- conformance/acta` is clean. Task 11 repeats this, but a vector nobody has seen fail is not known to be read.

- [ ] **Step 4: Write the end-to-end test: `curia-testis` verifies a real committed flag**

In `tests/Curia.Api.Tests/ActaEndpointTests.cs`, add `using System.Text;` after `using System.Net.Http.Json;`, and add this test after `R6_18_R6_48_APostCarriesItsLogIndexAndAProofTestisVerifiesFromTheEntry`:

```csharp
    /// <summary>
    /// R10.62 through the Acta: a committed flag is an ordinary leaf, its entry names no post, raiser
    /// or rationale, and <c>curia-testis</c> — which never dispatches on <c>event_type</c> — verifies its
    /// inclusion under a signed head offline, and refuses it once a byte of the entry changes.
    /// </summary>
    [Fact]
    public async Task R10_62_ACommittedFlagIsALeafTestisVerifiesAndItNamesNoRaiserOrPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        var postId = await PostQuestionAsync(http, ct);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var raiser = ForumAgent.Create("https://agents.example/acta-raiser-" + suffix, "acta-raiser-" + suffix);
        using (var enrolled = await raiser.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var dpop = DpopClient.For(raiser, raiser.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using (var flagged = await dpop.PostAsync(
            http, $"http://localhost/v1/posts/{postId}/flags", token,
            Encoding.UTF8.GetBytes("{\"kind\":\"spam\",\"rationale\":\"Advertising, not a question.\"}"),
            forum.Now, ct, contentType: "application/json"))
            Assert.Equal(HttpStatusCode.Created, flagged.StatusCode);

        var (exit, _, stderr) = await SignHeadAsync(ct);
        Assert.True(exit == ExitCode.Ok, stderr);

        // Found the way any reader finds it: by walking the log.
        long? index = null;
        string? entry = null;
        for (long i = 0; i < 100_000; i++)
        {
            using var response = await http.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) break;

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("\"event_type\":\"flag.committed\"", StringComparison.Ordinal))
                (index, entry) = (i, body);
        }

        Assert.True(index is not null, "no flag.committed leaf in the log: the raise did not commit");
        Assert.DoesNotContain(postId, entry!, StringComparison.Ordinal);
        Assert.DoesNotContain(raiser.AgentId, entry!, StringComparison.Ordinal);
        Assert.DoesNotContain("Advertising", entry!, StringComparison.Ordinal);

        var entryPath = Path.Combine(dir, "entry.json");
        await File.WriteAllTextAsync(entryPath, entry!, ct);
        var proofPath = await SaveAsync(http, $"/v1/log/proof/{index}", dir, "proof.json", ct);
        var headPath = await SaveAsync(http, "/v1/log/head", dir, "head.json", ct);
        var jwksPath = await SaveAsync(http, "/v1/log/jwks", dir, "log-jwks.json", ct);
        var verifier = TestisBinary.Locate();

        var (code, verified, failure) = TestisBinary.Run(
            verifier, $"log inclusion --entry \"{entryPath}\" --proof \"{proofPath}\" --head \"{headPath}\" --log-jwks \"{jwksPath}\"");
        Assert.True(code == 0, failure);
        Assert.Contains($"log_index: {index}", verified, StringComparison.Ordinal);

        // One member changed, and the entry is a different leaf the proof does not describe.
        var tampered = entry!.Replace("\"kind\":\"spam\"", "\"kind\":\"incorrect\"", StringComparison.Ordinal);
        Assert.NotEqual(entry, tampered);
        var tamperedPath = Path.Combine(dir, "entry-tampered.json");
        await File.WriteAllTextAsync(tamperedPath, tampered, ct);

        var (tamperedCode, _, tamperedFailure) = TestisBinary.Run(
            verifier, $"log inclusion --entry \"{tamperedPath}\" --proof \"{proofPath}\"");
        Assert.Equal(1, tamperedCode);
        Assert.Contains("curia/acta/leaf-mismatch", tamperedFailure, StringComparison.Ordinal);
    }
```

- [ ] **Step 5: Run it**

```bash
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~ActaEndpointTests"
```

Expected: every `ActaEndpointTests` test passes, including the new one. `curia-testis` prints `log_index: <n>` for the committed flag and exits 1 with `curia/acta/leaf-mismatch` for the tampered entry.

- [ ] **Step 6: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'The Acta over flag.committed: a vector both runners read, and testis end to end\n\nNo leaf computation changed (R15.1, G9): a new entry kind is a payload decision.\nThe Rust change is the corpus hand count alone, 69 to 70 and 75 to 76.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 8: The writer — `ApplyModeration`

**Files:**
- Create: `src/Curia.Application/Moderation/ApplyModeration.cs`
- Create: `src/Curia.Application/Moderation/RationaleRefusal.cs`
- Modify: `src/Curia.Application/Moderation/RaiseFlag.cs` (`FlagErrors.RationaleRejected` delegates to `RationaleRefusal`)
- Test: `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs`

**Interfaces:**
- Consumes:
  - `ModerationAction`, `ModerationPolicy.UpheldFlags`, `AdjudicatedFlags`, `MayServe` and `Authorize` (Task 2).
  - `FlagDirectory.Join` and `FlagProjector` constants (Task 5).
  - `IFlagDetailStore` (Task 4).
  - `RaiseFlag` (Task 6), used in the tests.
  - `PostProjector.Fold` → `PostView.Digest` (existing).
- Produces:
  - `public sealed record ModerationRecorded(string PostId, string Digest, ModerationEffect Effect, FlagKind Category, ImmutableArray<string> Adjudicates, ActorId Moderator, DateTimeOffset At)`.
  - `public sealed class ApplyModeration(IEventStore events, IFlagDetailStore details, TimeProvider clock)`, with `Task<Result<ModerationRecorded>> RecordAsync(string postId, ModerationEffect effect, FlagKind category, string rationale, ActorId moderator, CancellationToken cancellationToken = default)`.
  - `ApplyModeration.OperatorPrefix = "operator:"`.
  - `public static class ModerationRecordErrors`, with these slugs:

    | Error | Slug |
    |---|---|
    | `NotAnOperator()` | `curia/moderation/not-an-operator` |
    | `NoSuchPost(string)` | `curia/moderation/no-such-post` |
    | `RationaleRejected(RiskAnnotations)` | `curia/moderation/rationale-rejected` |
    | `NoOp(string, ModerationEffect, FlagKind)` | `curia/moderation/no-op` |
  - `internal static class RationaleRefusal`, with `Error Of(string type, string title, RiskAnnotations annotations)`: the one body behind both `FlagErrors.RationaleRejected` and `ModerationRecordErrors.RationaleRejected`, which differ only in slug and title.

- [ ] **Step 1: Write the failing tests**

Create `tests/Curia.Application.Tests/Moderation/ApplyModerationTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>R10.59 and R10.60 at the writer: what a record carries, what it names, and what it refuses.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ApplyModerationTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private static readonly string Digest = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static ActorId Operator => Require(ActorId.Create("operator:reviewer"));

    private sealed record World(InMemoryEventStore Store, InMemoryFlagDetailStore Details, ManualTimeProvider Clock)
    {
        public ApplyModeration Moderate => new(Store, Details, Clock);
    }

    private static async Task<World> WorldWithPostAsync(CancellationToken ct)
    {
        var clock = new ManualTimeProvider(Start);
        var world = new World(new InMemoryEventStore(clock), new InMemoryFlagDetailStore(), clock);

        // Every member PostProjector requires, shaped as IngestPipeline persists a post.
        Require(await world.Store.AppendAsync(
            Require(AggregateId.Create(Post)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Post)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(Post)),
                    new("canonical", new JsonValue.String("{\"body\":\"a question\"}")),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String(Digest)),
                    new("author", new JsonValue.String("https://agents.example/author")),
                    new("board", new JsonValue.String("board-1")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            ct).ConfigureAwait(false));

        return world;
    }

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(World world, CancellationToken ct) =>
        Require(await world.Store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>Raises a flag through the real writer and returns its event id.</summary>
    private static async Task<string> FlagAsync(World world, FlagKind kind, CancellationToken ct)
    {
        Require(await new RaiseFlag(world.Store, world.Details, world.Clock).RecordAsync(Post, Reporter, kind, "reported", ct).ConfigureAwait(false));
        return (await LogAsync(world, ct).ConfigureAwait(false)).Last(e => e.Event.Type.Value == FlagProjector.FlagCommittedType).Event.Id.Value;
    }

    private static async Task<PostModeration> ModerationAsync(World world, CancellationToken ct) =>
        FlagProjector.Fold(await LogAsync(world, ct).ConfigureAwait(false))[Post];

    [Fact]
    public async Task R10_60_ARecordNamesThePostItsDigestAndTheFlagsItAdjudicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var flag = await FlagAsync(world, FlagKind.Spam, ct);

        var recorded = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct));

        Assert.Equal(Digest, recorded.Digest);
        Assert.Equal([flag], recorded.Adjudicates);

        var record = (await LogAsync(world, ct)).Single(e => e.Event.Type.Value == FlagProjector.ModerationAppliedType);
        Assert.Equal(Post, record.AggregateId.Value);
        Assert.Equal("operator:reviewer", record.Event.Actor?.Value);

        var payload = ((JsonValue.Object)record.Event.Payload).Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
        Assert.Equal(new JsonValue.String(Digest), payload[FlagProjector.DigestField]);
        Assert.Equal(new JsonValue.String("human"), payload[FlagProjector.ModeratorField]);
        Assert.Equal([flag], ((JsonValue.Array)payload[FlagProjector.AdjudicatesField]).Items.Cast<JsonValue.String>().Select(s => s.Value));

        var moderation = await ModerationAsync(world, ct);
        Assert.False(moderation.MayServe);
        Assert.Equal([flag], moderation.UpheldFlags);
    }

    /// <summary>Review Focus 3: only the record's category is adjudicated; the other flag stays open.</summary>
    [Fact]
    public async Task R10_60_OnlyTheRecordsCategoryIsAdjudicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var spam = await FlagAsync(world, FlagKind.Spam, ct);
        var injection = await FlagAsync(world, FlagKind.Injection, ct);

        var recorded = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct));

        Assert.Equal([spam], recorded.Adjudicates);
        Assert.DoesNotContain(injection, ModerationPolicy.AdjudicatedFlags((await ModerationAsync(world, ct)).History));
    }

    /// <summary>A flag written before R10.62, public on the post's stream, is adjudicated like a committed one.</summary>
    [Fact]
    public async Task R10_60_ALegacyFlagIsAdjudicatedLikeACommittedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await world.Store.ReadByAggregateAsync(aggregate, ct));
        Require(await world.Store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create("01JLEGACY00000000000000001")),
                Require(EventType.Create(FlagProjector.FlagRaisedType)),
                Require(ActorId.Create(Reporter)),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(Post)),
                    new(FlagProjector.RaisedByField, new JsonValue.String(Reporter)),
                    new(FlagProjector.KindField, new JsonValue.String("spam")),
                    new(FlagProjector.RationaleField, new JsonValue.String("an old, public rationale")),
                ]))],
            ct));

        var recorded = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct));

        Assert.Equal(["01JLEGACY00000000000000001"], recorded.Adjudicates);
    }

    /// <summary>R10.39 counts records, so a record that changes nothing is refused by name, and nothing is appended.</summary>
    [Fact]
    public async Task R10_39_ASecondIdenticalRecordIsRefusedAsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, ct);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct));
        var before = (await LogAsync(world, ct)).Count;

        var again = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed again.", Operator, ct);

        Assert.False(again.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-op", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>A dismissal of open flags records a review (R10.39's denominator); a dismissal of nothing is a no-op.</summary>
    [Fact]
    public async Task R10_39_ADismissalOfOpenFlagsIsARecordAndADismissalOfNothingIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var nothing = await world.Moderate.RecordAsync(Post, ModerationEffect.Dismiss, FlagKind.Incorrect, "Nothing to review.", Operator, ct);
        Assert.False(nothing.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-op", error!.Type);

        var flag = await FlagAsync(world, FlagKind.Incorrect, ct);
        var dismissed = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Dismiss, FlagKind.Incorrect, "Reviewed: the premise holds.", Operator, ct));

        Assert.Equal([flag], dismissed.Adjudicates);
        var moderation = await ModerationAsync(world, ct);
        Assert.True(moderation.MayServe);
        Assert.Contains(flag, ModerationPolicy.AdjudicatedFlags(moderation.History));
        Assert.DoesNotContain(flag, moderation.UpheldFlags);
    }

    /// <summary>Review Focus 4: a restore after a proactive withholding is a record; a restore of a servable post is not.</summary>
    [Fact]
    public async Task R10_59_ARestoreAfterAProactiveWithholdingIsARecordARestoreOfAServablePostIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var early = await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.Spam, "Nothing withheld.", Operator, ct);
        Assert.False(early.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-op", error!.Type);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.CredentialLeak, "A credential on line 2.", Operator, ct));
        Assert.False((await ModerationAsync(world, ct)).MayServe);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.CredentialLeak, "Rotated; restoring.", Operator, ct));
        Assert.True((await ModerationAsync(world, ct)).MayServe);
    }

    /// <summary>R10.59: the human arm is an operator's. An agent-shaped actor is refused, and nothing is appended.</summary>
    [Fact]
    public async Task R10_59_OnlyAnOperatorRecordsAHumanAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Require(ActorId.Create("https://agents.example/moderator")), ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/not-an-operator", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>R10.60: the rationale lands in a leaf R6.51 serves verbatim, so a credential in it is refused and not echoed.</summary>
    [Fact]
    public async Task R10_60_ACredentialInTheReasonIsRefusedAndNothingIsAppended()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.CredentialLeak, "It leaks AKIAIOSFODNN7EXAMPLE.", Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-rejected", error!.Type);
        Assert.DoesNotContain("AKIA", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    [Fact]
    public async Task AnActionOnAPostThatDoesNotExistIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var result = await world.Moderate.RecordAsync(
            "01JNOSUCHPOST0000000000001", ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-such-post", error!.Type);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~ApplyModerationTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'ApplyModeration' could not be found`.

- [ ] **Step 3: Write the writer**

Create `src/Curia.Application/Moderation/ApplyModeration.cs`:

```csharp
using System.Collections.Immutable;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when a moderation record was accepted.</summary>
/// <param name="Adjudicates">The flags the record names (R10.60): every flag of its category on the post, derived, never typed.</param>
/// <param name="At">The store's <c>server_ts</c> for the append.</param>
public sealed record ModerationRecorded(
    string PostId,
    string Digest,
    ModerationEffect Effect,
    FlagKind Category,
    ImmutableArray<string> Adjudicates,
    ActorId Moderator,
    DateTimeOffset At);

/// <summary>
/// R10.59's human arm: an operator's moderation record, appended out of band.
///
/// <para><b>No HTTP route reaches this.</b> Table 10 grants <c>moderation</c>|<c>apply</c> only to a
/// delegated T3 agent (Phase 4); an operator endpoint would need a pair that does not exist, and
/// <c>ResourceActionModel.RowFor</c> reports an unmodelled pair as a failure. The operator tool calls
/// this over the event store's append-only grant, as it calls <c>AttestOwner</c> (errata G5).</para>
///
/// <para><b>What a record carries (R10.60).</b> The post and its envelope digest (R6.25), the
/// moderator kind and actor, the effect and category, a screened rationale, and the flags it
/// adjudicates — derived here from the flag directory, never typed by the moderator, because the
/// record is the only place a reader of the public log learns which flags were reviewed.</para>
///
/// <para><b>A record that changes nothing is refused.</b> R10.39 counts records. A record that
/// changes neither servability nor any flag's upheld state, and names no flag no earlier record
/// named, would inflate the counts while recording no decision.</para>
/// </summary>
public sealed class ApplyModeration
{
    /// <summary>The actor namespace R10.59 gives the human arm (plan D4: a convention the domain cannot enforce, so it is enforced here).</summary>
    public const string OperatorPrefix = "operator:";

    private readonly IEventStore _events;
    private readonly IFlagDetailStore _details;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public ApplyModeration(IEventStore events, IFlagDetailStore details, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _details = details;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Records <paramref name="effect"/> on <paramref name="postId"/> in <paramref name="category"/>, or reports why not.</summary>
    public async Task<Result<ModerationRecorded>> RecordAsync(
        string postId,
        ModerationEffect effect,
        FlagKind category,
        string rationale,
        ActorId moderator,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        if (moderator.Value is null
            || !moderator.Value.StartsWith(OperatorPrefix, StringComparison.Ordinal)
            || moderator.Value.Length == OperatorPrefix.Length)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NotAnOperator());

        if (string.IsNullOrWhiteSpace(rationale))
            return Result<ModerationRecorded>.Fail(ModerationErrors.RationaleRequired());

        // R10.60: the rationale lands in a leaf R6.51 serves verbatim, under the same two-regime
        // table a flag's rationale is screened with.
        var screened = ContentScreener.ScreenText(Encoding.UTF8.GetBytes(rationale));
        if (!screened.TryGetValue(out var screening, out var screeningError))
            return Result<ModerationRecorded>.Fail(screeningError!);

        if (!screening!.MayPersist)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.RationaleRejected(screening.Annotations));

        if (!AggregateId.Create(postId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<ModerationRecorded>.Fail(aggregateError!);

        var read = await _events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
            return Result<ModerationRecorded>.Fail(readError!);

        var post = PostProjector.Fold(log!).FirstOrDefault(p => string.Equals(p.PostId, postId, StringComparison.Ordinal));
        if (post is null)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NoSuchPost(postId));

        var rows = await _details.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(out var details, out var detailError))
            return Result<ModerationRecorded>.Fail(detailError!);

        // R10.60: every flag of this category raised against the post so far — derived, never typed.
        ImmutableArray<string> adjudicates =
        [
            .. FlagDirectory.Join(log!, details!).Flags
                .Where(f => string.Equals(f.PostId, postId, StringComparison.Ordinal) && f.Kind == category)
                .Select(f => f.FlagId),
        ];

        ImmutableArray<ModerationAction> before = FlagProjector.Fold(log!).TryGetValue(postId, out var moderation)
            ? moderation.History
            : [];

        var action = new ModerationAction(
            postId, ModeratorKind.Human, moderator.Value, effect, category, rationale, ServerTimestamp.At(_clock.GetUtcNow()), adjudicates);

        if (!ModerationPolicy.Authorize(action).TryGetValue(out _, out var authorizeError))
            return Result<ModerationRecorded>.Fail(authorizeError!);

        ImmutableArray<ModerationAction> after = [.. before, action];
        var noOp = ModerationPolicy.MayServe(before) == ModerationPolicy.MayServe(after)
            && ModerationPolicy.UpheldFlags(before).SetEquals(ModerationPolicy.UpheldFlags(after))
            && ModerationPolicy.AdjudicatedFlags(before).IsSupersetOf(adjudicates);
        if (noOp)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NoOp(postId, effect, category));

        var stream = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!stream.TryGetValue(out var streamEvents, out var streamError))
            return Result<ModerationRecorded>.Fail(streamError!);

        if (!AggregateVersion.From(streamEvents!.Count).TryGetValue(out var version, out var versionError))
            return Result<ModerationRecorded>.Fail(versionError!);

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<ModerationRecorded>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<ModerationRecorded>.Fail(eventIdError!);

        if (!EventType.Create(FlagProjector.ModerationAppliedType).TryGetValue(out var type, out var typeError))
            return Result<ModerationRecorded>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.DigestField, new JsonValue.String(post.Digest)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
            new(FlagProjector.ActorIdField, new JsonValue.String(moderator.Value)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String(rationale)),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(id => (JsonValue)new JsonValue.String(id))])),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, type, moderator, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new ModerationRecorded(
            postId, post.Digest, effect, category, adjudicates, moderator, events[0].ServerTimestamp.Value));
    }
}

/// <summary>RFC 9457 problem-type slugs the moderation writer emits.</summary>
public static class ModerationRecordErrors
{
    /// <summary>R10.59: the human arm is an operator's, named <c>operator:&lt;name&gt;</c>.</summary>
    public static Error NotAnOperator() => new(
        "curia/moderation/not-an-operator",
        "Only an operator records a human moderator's action (R10.59)",
        "the actor must be named operator:<name>");

    public static Error NoSuchPost(string postId) => new(
        "curia/moderation/no-such-post",
        "No such post",
        $"post={postId}");

    /// <summary>R10.60: categories and offsets only (R10.27), never the matched value.</summary>
    public static Error RationaleRejected(RiskAnnotations annotations) => RationaleRefusal.Of(
        "curia/moderation/rationale-rejected",
        "The moderator's rationale was rejected by screening; it would land in a public leaf (R10.60)",
        annotations);

    /// <summary>A record that would change nothing (spec Decision 11).</summary>
    public static Error NoOp(string postId, ModerationEffect effect, FlagKind category) => new(
        "curia/moderation/no-op",
        "That record would change nothing, and R10.39 counts records",
        $"post={postId} effect={ModerationEffects.Wire(effect)} category={FlagKinds.Wire(category)}");
}
```

The moderator's rationale is refused exactly as a flag's is, so the two refusals share one body. Create `src/Curia.Application/Moderation/RationaleRefusal.cs`:

```csharp
using System.Globalization;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>
/// The refusal a rationale carrying credential material earns (R10.26), for both writers that screen
/// one: a flag's (<see cref="RaiseFlag"/>) and a moderator's (<see cref="ApplyModeration"/>). The
/// detail names each category and its offset (R10.27) and never the matched value (R10.28):
/// structurally, because <c>RiskFlag</c> has no member that can carry content. One body, so the two
/// refusals cannot come to differ in what they echo.
/// </summary>
internal static class RationaleRefusal
{
    /// <summary>An RFC 9457 error of <paramref name="type"/> whose detail lists each annotation as <c>category@offset</c>.</summary>
    public static Error Of(string type, string title, RiskAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var categories = string.Join(
            ", ",
            annotations.Flags.Select(f => $"{f.Category}@{f.Offset.ToString(CultureInfo.InvariantCulture)}"));

        return new Error(type, title, categories);
    }
}
```

In `src/Curia.Application/Moderation/RaiseFlag.cs`, keep `FlagErrors.RationaleRejected`'s doc comment and replace the method itself, from `    public static Error RationaleRejected(RiskAnnotations annotations)` through its closing brace, with:

```csharp
    public static Error RationaleRejected(RiskAnnotations annotations) => RationaleRefusal.Of(
        "curia/flag/rationale-rejected",
        "The flag's rationale was rejected by ingest screening",
        annotations);
```

The slug and title are Task 6's, unchanged, so `R10_26_ACredentialInTheRationaleIsRefusedBeforeEitherStore` still reads `curia/flag/rationale-rejected`.

- [ ] **Step 4: Run them to see them pass**

Run: `dotnet test tests/Curia.Application.Tests -c Release --nologo --filter "FullyQualifiedName~ApplyModerationTests|FullyQualifiedName~RaiseFlagTests"`
Expected: 16 PASS, the 9 `ApplyModerationTests` and the 7 `RaiseFlagTests`, which still read the flag path's refusal through the shared body. Then `dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"` reports `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'ApplyModeration: the human arm of R10.36, out of band (R10.59, R10.60)\n\nA record carries the post digest and the flags it adjudicates, derived from the\ndirectory; an operator actor only; a screened rationale; no-op records refused.\nThe flag and moderation rationale refusals now share one body.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---
### Task 9: The operator's verbs, and every withholding through the writer

**Files:**
- Modify: `src/Curia.Operator/Program.cs` (usage text, dispatch, two verbs)
- Create: `src/Curia.Operator/TerminalText.cs`
- Modify: `src/Curia.Api/Program.cs` (register `ApplyModeration`)
- Modify: `src/Curia.Application/Projections/FlagProjection.cs` (`ModerationAppliedType`'s doc comment)
- Modify: `tests/Curia.Api.Tests/ForumFixture.cs` (`WithholdAsync` through `ApplyModeration`)
- Modify: `tests/Curia.Api.Tests/FlagEndpointTests.cs`, `tests/Curia.Api.Tests/SearchEndpointTests.cs` (delete their private `WithholdAsync` copies)
- Test: `tests/Curia.Api.Tests/OperatorModerationTests.cs`

**Interfaces:**
- Consumes: `ApplyModeration`, `ModerationRecorded` and `ModerationRecordErrors` (Task 8); `FlagDirectory` (Task 5); `ModerationPolicy.UpheldFlags` and `AdjudicatedFlags` (Task 2); `PostgresAdapters.FlagDetails` (Task 4); `Datamarking.Render` with `MarkingMode.Datamark` (existing, `Curia.Domain.Serving`).
- Produces:
  - `curia-operator moderate --post <id> --category <kind> --effect withhold|quarantine|restore|dismiss --reason <text> --by <name>`. Exit codes: `0` recorded, `1` usage, `2` refused.
  - `curia-operator flags [--post <id>] [--open]`.
  - `internal static class TerminalText` with `Line(string)` and `Block(string)`.
  - Stdout formats, which the tests below read:

    ```
    moderated    <post>
    effect       <effect>
    category     <kind>
    digest       <sha256:…>
    adjudicates  <n>[: <id>, <id>]
    by           operator:<name>
    at           <instant, "O">
    ```

    and, per flag,

    ```
    flag       <id>
    post       <post>
    kind       <kind>
    raised_by  <agent>
    raised_at  <instant, "O">
    state      open|upheld|adjudicated, not upheld
    rationale
    <<<CURIA-UNTRUSTED-BEGIN>>>
    …datamarked…
    <<<CURIA-UNTRUSTED-END>>>
    ```

    then `skipped    <reason>: <n>` lines, and `<n> flag(s)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Curia.Api.Tests/OperatorModerationTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Domain.Serving;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.59 end to end: the operator's out-of-band verbs against the real Forum's database, through
/// <see cref="OperatorCommands.RunAsync"/> — the whole path an operator runs, minus reading one
/// environment variable.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class OperatorModerationTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<(int Exit, string Out, string Err)> RunAsync(string[] args, CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, forum.ConnectionString, forum.Clock, stdout, stderr, ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Party> PartyAsync(HttpClient client, string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private async Task<(string PostId, string Digest)> AskAsync(HttpClient client, Party author, CancellationToken ct)
    {
        var board = "op-" + Guid.NewGuid().ToString("N")[..8];
        using var asked = await author.Dpop.PostAsync(
            client, PostsUrl, author.Token,
            author.Agent.SignQuestion(board, "How does JCS order object members?", "Ordering " + Guid.NewGuid().ToString("N")[..8], forum.Now),
            forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);

        using var receipt = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct));
        return (receipt.RootElement.GetProperty("post_id").GetString()!, receipt.RootElement.GetProperty("digest").GetString()!);
    }

    private async Task FlagAsync(HttpClient client, Party raiser, string postId, string kind, string rationale, CancellationToken ct)
    {
        using var raised = await raiser.Dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, rationale })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
    }

    private static string[] Moderate(string postId, string effect, string category = "spam", string reason = "Reviewed: advertising.") =>
        ["moderate", "--post", postId, "--category", category, "--effect", effect, "--reason", reason, "--by", "reviewer"];

    /// <summary>How many entries the log serves, counted the way any reader counts them.</summary>
    private static async Task<long> LogSizeAsync(HttpClient client, CancellationToken ct)
    {
        for (long i = 0; i < 100_000; i++)
        {
            using var entry = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (entry.StatusCode == HttpStatusCode.NotFound) return i;
        }

        throw new InvalidOperationException("the log did not end within 100,000 entries");
    }

    /// <summary>R10.59, R10.60, R6.25: the post stops being served, and the record — public, in the log — names who and why, and the digest.</summary>
    [Fact]
    public async Task R10_59_TheOperatorWithholdsAPostOutOfBandAndTheRecordIsPublic()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, digest) = await AskAsync(client, author, ct);

        var (exit, stdout, stderr) = await RunAsync(Moderate(postId, "withhold"), ct);

        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains($"moderated    {postId}", stdout, StringComparison.Ordinal);
        Assert.Contains($"digest       {digest}", stdout, StringComparison.Ordinal);
        Assert.Contains("adjudicates  0", stdout, StringComparison.Ordinal);
        Assert.Contains("by           operator:reviewer", stdout, StringComparison.Ordinal);

        using (var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        string? record = null;
        for (long i = 0; i < 100_000; i++)
        {
            using var entry = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (entry.StatusCode == HttpStatusCode.NotFound) break;

            var body = await entry.Content.ReadAsStringAsync(ct);
            if (body.Contains("\"moderation.applied\"", StringComparison.Ordinal) && body.Contains(postId, StringComparison.Ordinal))
                record = body;
        }

        Assert.True(record is not null, "no moderation record for the post in the log");
        Assert.Contains("\"actor_id\":\"operator:reviewer\"", record!, StringComparison.Ordinal);
        Assert.Contains("\"moderator\":\"human\"", record, StringComparison.Ordinal);
        Assert.Contains($"\"digest\":\"{digest}\"", record, StringComparison.Ordinal);
    }

    /// <summary>R10.60, R10.61: the record names the flag, and the listing shows it open before and upheld after.</summary>
    [Fact]
    public async Task R10_60_TheRecordNamesTheFlagAndTheListingShowsItUpheld()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var raiser = await PartyAsync(client, "op-raiser", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        await FlagAsync(client, raiser, postId, "spam", "Advertising, not a question.", ct);

        var (listExit, before, listErr) = await RunAsync(["flags", "--post", postId], ct);
        Assert.True(listExit == ExitCode.Ok, listErr);
        Assert.Contains("state      open", before, StringComparison.Ordinal);
        Assert.Contains($"raised_by  {raiser.Agent.AgentId}", before, StringComparison.Ordinal);
        Assert.Contains("1 flag(s)", before, StringComparison.Ordinal);

        var flagId = before.Split('\n').Single(l => l.StartsWith("flag       ", StringComparison.Ordinal))["flag       ".Length..].Trim();

        var (exit, stdout, stderr) = await RunAsync(Moderate(postId, "withhold"), ct);
        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains($"adjudicates  1: {flagId}", stdout, StringComparison.Ordinal);

        var (_, after, _) = await RunAsync(["flags", "--post", postId], ct);
        Assert.Contains("state      upheld", after, StringComparison.Ordinal);

        var (_, open, _) = await RunAsync(["flags", "--post", postId, "--open"], ct);
        Assert.Contains("0 flag(s)", open, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review Focus 5. R10.44 requires a rationale served under <c>moderation</c>|<c>list</c> be
    /// delimited and marked — the operator's reviewer may be a model — and a terminal must not
    /// interpret an escape sequence or a bidi override someone typed into a flag.
    /// </summary>
    [Fact]
    public async Task R10_62_TheListingMarksTheRationaleAndEscapesControlCharacters()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var raiser = await PartyAsync(client, "op-raiser", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        await FlagAsync(client, raiser, postId, "injection", "Spam.\u001b[31m red \u202e reversed", ct);

        var (exit, stdout, stderr) = await RunAsync(["flags", "--post", postId], ct);

        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains(Datamarking.OpenDelimiter, stdout, StringComparison.Ordinal);
        Assert.Contains(Datamarking.CloseDelimiter, stdout, StringComparison.Ordinal);
        Assert.Contains(Datamarking.DefaultControlToken, stdout, StringComparison.Ordinal);
        Assert.Contains("\\u001B", stdout, StringComparison.Ordinal);
        Assert.Contains("\\u202E", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", stdout, StringComparison.Ordinal);
    }

    /// <summary>R10.39 counts records: the same record twice is refused by name, and the post stays as the first left it.</summary>
    [Fact]
    public async Task R10_39_ARepeatedRecordIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);

        Assert.Equal(ExitCode.Ok, (await RunAsync(Moderate(postId, "withhold"), ct)).Exit);

        var (exit, _, stderr) = await RunAsync(Moderate(postId, "withhold"), ct);
        Assert.Equal(ExitCode.Refused, exit);
        Assert.Contains("curia/moderation/no-op", stderr, StringComparison.Ordinal);
    }

    /// <summary>R10.60: the reason lands in a public leaf, so a credential in it is refused, never echoed, and nothing changes.</summary>
    [Fact]
    public async Task R10_60_ACredentialInTheReasonIsRefusedAndThePostStaysServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);

        var (exit, _, stderr) = await RunAsync(Moderate(postId, "withhold", "credential_leak", "It leaks AKIAIOSFODNN7EXAMPLE."), ct);

        Assert.Equal(ExitCode.Refused, exit);
        Assert.Contains("curia/moderation/rationale-rejected", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", stderr, StringComparison.Ordinal);

        using var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// A usage error is refused before anything is written. It is aimed at a real, servable post, so
    /// a verb that defaulted the missing category would have withheld it: the log would grow and the
    /// post would stop being served. Against a post that does not exist, "writes nothing" would hold
    /// whatever the verb did.
    /// </summary>
    [Fact]
    public async Task AMissingCategoryIsAUsageErrorAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        var before = await LogSizeAsync(client, ct);

        var (exit, _, stderr) = await RunAsync(["moderate", "--post", postId, "--effect", "withhold", "--reason", "r", "--by", "x"], ct);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("--category is required", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await LogSizeAsync(client, ct));

        using var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~OperatorModerationTests"`
Expected: the tests build, and every test that runs a verb FAILS with exit code `1` (`unknown verb 'moderate'` or `'flags'`). `AMissingCategoryIsAUsageErrorAndWritesNothing` also fails, on the message.

- [ ] **Step 3: Write the terminal-safety helper**

Create `src/Curia.Operator/TerminalText.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Curia.OperatorTool;

/// <summary>
/// Text an agent wrote, made safe to print on an operator's terminal: every C0 and C1 control
/// character, DEL, and every Unicode bidirectional override or isolate is shown as <c>\uXXXX</c>
/// instead of being interpreted. A flag's rationale is attacker-controlled (R10.35), and an ESC
/// sequence or a right-to-left override in it would otherwise rewrite what the moderator sees.
/// </summary>
internal static class TerminalText
{
    /// <summary>A single-line field: line breaks and tabs are escaped too.</summary>
    public static string Line(string text) => Escape(text, keepLayout: false);

    /// <summary>A multi-line field: line breaks and tabs survive, everything else above is escaped.</summary>
    public static string Block(string text) => Escape(text, keepLayout: true);

    private static string Escape(string text, bool keepLayout)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var layout = c is '\n' or '\t';
            var unsafeChar = (c < ' ' && !(keepLayout && layout))
                || c is '\u007f'
                || c is >= '\u0080' and <= '\u009f'
                || c is >= '\u202a' and <= '\u202e'
                || c is >= '\u2066' and <= '\u2069';

            if (unsafeChar) builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else builder.Append(c);
        }

        return builder.ToString();
    }
}
```

- [ ] **Step 4: Write the two verbs**

In `src/Curia.Operator/Program.cs`:

1. Add these usings, keeping the file's order: `using System.Collections.Immutable;`, `using Curia.Application.Moderation;`, `using Curia.Domain.Moderation;`, `using Curia.Domain.Serving;`.

2. In the `Usage` string, insert this text after the `attest-owner` paragraph (before `ENVIRONMENT`):

```text
          curia-operator moderate --post <post-id> --category <kind> --effect <effect>
                                  --reason <text> --by <operator-name>

              Records R10.36's human moderator acting on <post-id> (R10.59): <effect> is one of
              withhold, quarantine, restore, dismiss; <kind> is one of R10.35's seven. The record
              names every flag of that kind raised against the post (R10.60) and is refused if it
              would change nothing. The reason lands in a public leaf and is screened like a
              flag's: credential material is refused.

          curia-operator flags [--post <post-id>] [--open]

              Lists flags with who raised them and why -- the review queue, out of band. Each
              rationale is delimited and datamarked (R10.44), and control characters are escaped.
              --open lists only flags no record has adjudicated.
```

3. In `RunAsync`, directly after the `if (args[0] == "sign-head") …` statement, add:

```csharp
        if (args[0] == "moderate")
            return await ModerateAsync(args.Skip(1).ToArray(), connectionString, clock, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "flags")
            return await FlagsAsync(args.Skip(1).ToArray(), connectionString, clock, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);
```

4. Add these methods to `OperatorCommands`, directly before `LoadKey`:

```csharp
    /// <summary>
    /// R10.59: an operator's moderation record, appended out of band through
    /// <see cref="ApplyModeration"/> — the only producer of <c>moderation.applied</c>.
    /// </summary>
    private static async Task<int> ModerateAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return await UsageErrorAsync(stderr, $"unexpected argument '{arg}'.").ConfigureAwait(false);

            var name = arg[2..];
            if (name is not ("post" or "category" or "effect" or "reason" or "by"))
                return await UsageErrorAsync(stderr, $"unknown flag --{name}.").ConfigureAwait(false);

            if (i + 1 >= argv.Length)
                return await UsageErrorAsync(stderr, $"--{name} needs a value.").ConfigureAwait(false);

            values[name] = argv[++i];
        }

        foreach (var required in (string[])["post", "category", "effect", "reason", "by"])
            if (!values.TryGetValue(required, out var given) || given.Length == 0)
                return await UsageErrorAsync(stderr, $"--{required} is required.").ConfigureAwait(false);

        if (!FlagKinds.Parse(values["category"]).TryGetValue(out var category, out var categoryError))
            return await UsageErrorAsync(stderr, categoryError!.Detail ?? categoryError.Title).ConfigureAwait(false);

        if (!ModerationEffects.Parse(values["effect"]).TryGetValue(out var effect, out var effectError))
            return await UsageErrorAsync(stderr, effectError!.Detail ?? effectError.Title).ConfigureAwait(false);

        // The operator namespace, applied once here as attest-owner applies it (plan D4).
        var by = values["by"];
        var actorValue = by.StartsWith(ApplyModeration.OperatorPrefix, StringComparison.Ordinal) ? by : ApplyModeration.OperatorPrefix + by;
        if (!ActorId.Create(actorValue).TryGetValue(out var actor, out _))
            return await UsageErrorAsync(stderr, "--by <operator-name> is required.").ConfigureAwait(false);

        Result<ModerationRecorded> result;
        var adapters = new PostgresAdapters(connectionString, clock);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new ApplyModeration(adapters.EventStore, adapters.FlagDetails, clock)
                .RecordAsync(values["post"], effect, category, values["reason"], actor, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.TryGetValue(out var recorded, out var error))
            return await RefuseAsync(stderr, error!).ConfigureAwait(false);

        var count = recorded!.Adjudicates.Length.ToString(CultureInfo.InvariantCulture);
        var named = recorded.Adjudicates.IsEmpty ? string.Empty : ": " + string.Join(", ", recorded.Adjudicates);

        await stdout.WriteLineAsync($"moderated    {TerminalText.Line(recorded.PostId)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"effect       {ModerationEffects.Wire(recorded.Effect)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"category     {FlagKinds.Wire(recorded.Category)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"digest       {recorded.Digest}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"adjudicates  {count}{named}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"by           {TerminalText.Line(recorded.Moderator.Value)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"at           {recorded.At.ToString("O", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>
    /// The review queue, out of band: every flag with its raiser and rationale, which only an
    /// operator ever sees (R10.44's <c>moderation</c>|<c>list</c> view for the human arm). Rationales
    /// are delimited and datamarked, because the reader may be a model, and every agent-written
    /// string is made terminal-safe first.
    /// </summary>
    private static async Task<int> FlagsAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string? post = null;
        var openOnly = false;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--open") { openOnly = true; continue; }
            if (argv[i] == "--post" && i + 1 < argv.Length) { post = argv[++i]; continue; }
            return await UsageErrorAsync(stderr, $"unexpected argument '{argv[i]}'. flags takes [--post <post-id>] [--open].").ConfigureAwait(false);
        }

        IReadOnlyList<AppendedEvent> log;
        IReadOnlyList<FlagDetail> details;
        var adapters = new PostgresAdapters(connectionString, clock);
        await using (adapters.ConfigureAwait(false))
        {
            var read = await adapters.EventStore.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var events, out var readError))
                return await RefuseAsync(stderr, readError!).ConfigureAwait(false);

            var rows = await adapters.FlagDetails.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!rows.TryGetValue(out var detailRows, out var detailError))
                return await RefuseAsync(stderr, detailError!).ConfigureAwait(false);

            log = events!;
            details = detailRows!;
        }

        var directory = FlagDirectory.Join(log, details);
        var moderation = FlagProjector.Fold(log);
        var rationales = RationalesByFlag(log, details);

        var listed = 0;
        foreach (var flag in directory.Flags.Where(f => post is null || string.Equals(f.PostId, post, StringComparison.Ordinal)))
        {
            ImmutableArray<ModerationAction> history = moderation.TryGetValue(flag.PostId, out var state) ? state.History : [];
            var status = ModerationPolicy.UpheldFlags(history).Contains(flag.FlagId) ? "upheld"
                : ModerationPolicy.AdjudicatedFlags(history).Contains(flag.FlagId) ? "adjudicated, not upheld"
                : "open";

            if (openOnly && status != "open") continue;
            listed++;

            await stdout.WriteLineAsync($"flag       {flag.FlagId}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"post       {TerminalText.Line(flag.PostId)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"kind       {FlagKinds.Wire(flag.Kind)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"raised_by  {TerminalText.Line(flag.RaisedBy)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"raised_at  {flag.At.Value.ToString("O", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"state      {status}").ConfigureAwait(false);
            await stdout.WriteLineAsync("rationale").ConfigureAwait(false);
            await stdout.WriteLineAsync(Datamarking.Render(
                TerminalText.Block(rationales.GetValueOrDefault(flag.FlagId, string.Empty)), MarkingMode.Datamark)).ConfigureAwait(false);
            await stdout.WriteLineAsync().ConfigureAwait(false);
        }

        foreach (var (reason, skipped) in directory.Skipped)
            await stdout.WriteLineAsync($"skipped    {reason}: {skipped.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);

        await stdout.WriteLineAsync($"{listed.ToString(CultureInfo.InvariantCulture)} flag(s)").ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>A flag's rationale: the private row for a committed flag, the event itself for a legacy one.</summary>
    private static Dictionary<string, string> RationalesByFlag(IReadOnlyList<AppendedEvent> log, IReadOnlyList<FlagDetail> details)
    {
        var rationales = details.ToDictionary(d => d.EventId, d => d.Rationale, StringComparer.Ordinal);

        foreach (var appended in log.Where(e => e.Event.Type.Value == FlagProjector.FlagRaisedType))
        {
            if (appended.Event.Payload is JsonValue.Object payload
                && payload.Members.FirstOrDefault(m => m.Key == FlagProjector.RationaleField).Value is JsonValue.String legacy)
                rationales.TryAdd(appended.Event.Id.Value, legacy.Value);
        }

        return rationales;
    }

    private static async Task<int> UsageErrorAsync(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync("error: " + message).ConfigureAwait(false);
        return ExitCode.Usage;
    }
```

If the compiler reports `AppendedEvent`, `FlagDetail`, `FlagDirectory`, `FlagProjector`, `JsonValue` or `ModerationAction` unresolved, add the matching `using`. The file already imports `Curia.Application.Ports`, `Curia.Application.Projections`, `Curia.Canon.Json` and `Curia.Domain`.

- [ ] **Step 5: Register the writer, correct the doc comment, and route every withholding through it**

In `src/Curia.Api/Program.cs`, after the `RaiseFlag` registration, add:

```csharp
        // R10.59's writer, reached by no HTTP route: registered so the end-to-end fixture withholds
        // through the same object the operator tool builds, never by hand-building the event.
        builder.Services.AddSingleton(sp => new ApplyModeration(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<IFlagDetailStore>(),
            sp.GetRequiredService<TimeProvider>()));
```

In `src/Curia.Application/Projections/FlagProjection.cs`, extend `ModerationAppliedType`'s doc comment with:

```csharp
    /// <para><b>One producer: R10.59's human arm</b>, <c>ApplyModeration</c>, reached out of band by
    /// <c>curia-operator moderate</c>. The delegated arm (R10.36's T3 grant, Table 22's Phase 4) has
    /// none yet, and Table 10 gives no route one.</para>
```

In `tests/Curia.Api.Tests/ForumFixture.cs`, replace `WithholdAsync` (its doc comment and body) with the code below, and add `using Curia.Application.Moderation;` and `using Curia.Domain.Moderation;`:

```csharp
    /// <summary>
    /// Withholds a post through R10.59's writer — the same <see cref="ApplyModeration"/> the operator
    /// tool runs — never by hand-building the event. A fixture written by the people who wrote the
    /// fold agrees with the fold (trap 16); this one has to agree with the writer.
    /// </summary>
    internal async Task WithholdAsync(string postId, CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        var moderate = scope.ServiceProvider.GetRequiredService<ApplyModeration>();

        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        Require(await moderate.RecordAsync(
            postId,
            ModerationEffect.Withhold,
            FlagKind.Spam,
            "withheld by the test fixture",
            Require(ActorId.Create("operator:fixture")),
            ct));
    }
```

In `tests/Curia.Api.Tests/FlagEndpointTests.cs`:
- Change `await WithholdAsync(postId, ct);` to `await forum.WithholdAsync(postId, ct);`.
- Delete the private `WithholdAsync` method and its doc comment.
- In `R10_36_AWithheldPostStopsBeingServedAndIsNotDeleted`'s doc comment, replace the paragraph beginning `The moderation action is appended directly to the store` with: `The withholding is R10.59's record, written through the operator's own use case by the fixture.`

In `tests/Curia.Api.Tests/SearchEndpointTests.cs`, change `await WithholdAsync(withheld, ct);` to `await forum.WithholdAsync(withheld, ct);`, and delete the private `WithholdAsync` method and its doc comment.

In both files, deleting the private `WithholdAsync` leaves `using Curia.Application.Ports;` unused. Delete that one line from each file. The build will not point at it: IDE0005 has no severity in this repository, so an unused `using` builds clean. Checked with IDE0005 switched on, those two lines are the only ones this task orphans; every other `using` in both files is still used.

- [ ] **Step 6: Run the verbs and every suite that withholds**

```bash
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~OperatorModerationTests|FullyQualifiedName~OperatorAttestationTests|FullyQualifiedName~FlagEndpointTests|FullyQualifiedName~SearchEndpointTests|FullyQualifiedName~BatchRetrievalTests|FullyQualifiedName~ConditionalRequestTests"
dotnet test tests/Curia.Architecture.Tests -c Release --nologo
```

Expected:
- `0 Warning(s)`.
- The 6 `OperatorModerationTests` pass.
- `BatchRetrievalTests` and `ConditionalRequestTests` pass unchanged. They withhold through the fixture, which now runs the writer.
- `CS7_HostProjectsDoNotNameDatabaseOrCryptoTypes` passes, because the operator names no Npgsql type.

- [ ] **Step 7: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'curia-operator moderate and flags: the review queue and its record, out of band\n\nThe listing delimits and datamarks each rationale and escapes control and bidi\ncharacters. Every test withholding now runs the writer instead of hand-building\nthe event (trap 16).\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 10: The loop — Table 11 carries information, and R10.39 is auditable

**Files:**
- Test: `tests/Curia.Api.Tests/ModerationLoopTests.cs`
- Test: `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs` (a second fact: the gate again, over a moderated fixture)

**Interfaces:**
- Consumes: everything above, through HTTP and `OperatorCommands.RunAsync`. The one exception is the R10.39 test's oracle, which reads the private join (`IEventReader`, `IFlagDetailStore`, `FlagDirectory.Join`, `FlagProjector.Fold`) from the host's own services, because the spec asks that the public figures equal the private ones.
- Produces: nothing new in `src/`. If a step fails, the defect is in an earlier task's code. Fix it there, in that task's own terms, and say so in this task's commit.

- [ ] **Step 1: Write the loop**

Create `tests/Curia.Api.Tests/ModerationLoopTests.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain.Authorization;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.OperatorTool;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G13's two findings, closed where a user meets them. Table 11's T1 row — "≥ 3 questions with
/// no upheld flags" — was vacuous because nothing could uphold a flag; here an upheld flag demotes its
/// author, an unadjudicated one does not, and a restore reinstates. And R10.39's figures are computed
/// from nothing but what an anonymous reader can fetch.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ModerationLoopTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<Party> PartyAsync(HttpClient client, ForumAgent agent, CancellationToken ct)
    {
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private static ForumAgent NewAgent(string stem)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
    }

    private async Task<(string PostId, string Digest)> AskAsync(HttpClient client, Party party, string board, string body, string title, CancellationToken ct)
    {
        using var asked = await party.Dpop.PostAsync(client, PostsUrl, party.Token, party.Agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);
        var text = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, text);

        using var receipt = JsonDocument.Parse(text);
        return (receipt.RootElement.GetProperty("post_id").GetString()!, receipt.RootElement.GetProperty("digest").GetString()!);
    }

    private async Task<(HttpStatusCode Status, string Body)> AnswerAsync(HttpClient client, Party party, string board, string question, string body, CancellationToken ct)
    {
        using var answered = await party.Dpop.PostAsync(client, PostsUrl, party.Token, party.Agent.SignAnswer(board, body, question, forum.Now), forum.Now, ct);
        return (answered.StatusCode, await answered.Content.ReadAsStringAsync(ct));
    }

    private async Task FlagAsync(HttpClient client, Party raiser, string postId, string kind, string rationale, CancellationToken ct)
    {
        using var raised = await raiser.Dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, rationale })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
    }

    private async Task OperatorAsync(CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, forum.ConnectionString, forum.Clock, stdout, stderr, ct);
        Assert.True(exit == ExitCode.Ok, $"curia-operator {string.Join(' ', args)} exited {exit}: {stderr}");
    }

    private async Task<string> InboxAsync(HttpClient client, Party party, string board, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/v1/inbox?board={board}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", party.Token);
        request.Headers.Add("DPoP", party.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, party.Token, nonce: null));

        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Every log entry an anonymous reader can fetch, as the <c>entry</c> object R6.51 serves.</summary>
    private static async Task<List<JsonElement>> PublicLogAsync(HttpClient client, CancellationToken ct)
    {
        var entries = new List<JsonElement>();
        for (long i = 0; i < 100_000; i++)
        {
            using var response = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return entries;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            entries.Add(document.RootElement.GetProperty("entry").Clone());
        }

        throw new InvalidOperationException("the log did not end within 100,000 entries");
    }

    private static string TypeOf(JsonElement entry) => entry.GetProperty("event_type").GetString()!;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    /// <summary>Every read path agrees, in both directions — the second direction is what stops the first passing on an empty page.</summary>
    private async Task AssertServedAsync(HttpClient client, Party reader, string board, string postId, string digest, bool served, CancellationToken ct)
    {
        using (var single = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))
            Assert.Equal(served ? HttpStatusCode.OK : HttpStatusCode.NotFound, single.StatusCode);

        using (var thread = await client.GetAsync(new Uri($"/v1/threads/{postId}", UriKind.Relative), ct))
            Assert.Equal(served ? HttpStatusCode.OK : HttpStatusCode.NotFound, thread.StatusCode);

        using (var listing = await client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), ct))
            Assert.Equal(served, (await listing.Content.ReadAsStringAsync(ct)).Contains(postId, StringComparison.Ordinal));

        using (var search = await client.GetAsync(new Uri($"/v1/search?q=canonical&board={board}&kind=question", UriKind.Relative), ct))
            Assert.Equal(served, (await search.Content.ReadAsStringAsync(ct)).Contains(postId, StringComparison.Ordinal));

        Assert.Equal(served, (await InboxAsync(client, reader, board, ct)).Contains(postId, StringComparison.Ordinal));

        using var batch = await client.PostAsJsonAsync(new Uri("/v1/posts/batch", UriKind.Relative), new { digests = new[] { digest } }, ct);
        using var items = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(ct));
        Assert.Equal(served ? "current" : "withheld", items.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
    }

    /// <summary>
    /// R10.61 and Table 11's T1 row, end to end. An author reaches T1; an unadjudicated flag against
    /// one of its three questions leaves it there (no unilateral demotion primitive); the operator
    /// upholds the flag and the question disappears from every read path and the author drops to T0;
    /// a restore reverses both.
    /// </summary>
    [Fact]
    public async Task R10_61_AnUpheldFlagDemotesItsAuthorAndARestoreReinstatesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "loop-" + Guid.NewGuid().ToString("N")[..8];

        ForumAgent authorAgent = NewAgent("loop-author"), askerAgent = NewAgent("loop-asker"), reporterAgent = NewAgent("loop-reporter");
        var author = await PartyAsync(client, authorAgent, ct);
        var asker = await PartyAsync(client, askerAgent, ct);
        await forum.AttestOwnerAsync(authorAgent.AgentId, ct);

        var questions = new List<(string PostId, string Digest)>();
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            questions.Add(await AskAsync(client, author, board, $"Question {i} about canonical form ({nonce}).", $"Author's question {i} {nonce}", ct));
        }

        // One question per answer attempt, so no step depends on answering the same question twice.
        var open = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            open.Add((await AskAsync(client, asker, board, $"Which canonical form does the log hash ({nonce})?", $"Asker's question {i} {nonce}", ct)).PostId);
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        author = await PartyAsync(client, authorAgent, ct);
        asker = await PartyAsync(client, askerAgent, ct);
        var reporter = await PartyAsync(client, reporterAgent, ct);

        var (status, body) = await AnswerAsync(client, author, board, open[0], "The pure RFC 8785 form (R6.46).", ct);
        Assert.True(status == HttpStatusCode.Created, "the author did not reach T1, so this test proves nothing: " + body);

        var (flagged, flaggedDigest) = questions[0];
        await FlagAsync(client, reporter, flagged, "spam", "Advertising, not a question.", ct);

        (status, body) = await AnswerAsync(client, author, board, open[1], "Still T1: an open flag decides nothing.", ct);
        Assert.True(status == HttpStatusCode.Created, "an unadjudicated flag demoted its target: " + body);

        await OperatorAsync(ct, "moderate", "--post", flagged, "--category", "spam", "--effect", "withhold", "--reason", "Reviewed: advertising.", "--by", "reviewer");
        await AssertServedAsync(client, asker, board, flagged, flaggedDigest, served: false, ct);

        (status, body) = await AnswerAsync(client, author, board, open[2], "Now T0: two clean questions, not three.", ct);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("curia/authz/denied", body, StringComparison.Ordinal);

        // The record is public (R6.25) and names the flag it upheld (R10.60) — and still no raiser.
        var record = (await PublicLogAsync(client, ct)).Single(e =>
            TypeOf(e) == "moderation.applied" && e.GetProperty("payload").GetProperty("post_id").GetString() == flagged);
        Assert.Single(record.GetProperty("payload").GetProperty("adjudicates").EnumerateArray());
        Assert.DoesNotContain(reporterAgent.AgentId, record.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Advertising", record.GetRawText(), StringComparison.Ordinal);

        await OperatorAsync(ct, "moderate", "--post", flagged, "--category", "spam", "--effect", "restore", "--reason", "Appeal upheld on review.", "--by", "reviewer");
        await AssertServedAsync(client, asker, board, flagged, flaggedDigest, served: true, ct);

        (status, body) = await AnswerAsync(client, author, board, open[2], "T1 again: the restore released the flag.", ct);
        Assert.True(status == HttpStatusCode.Created, "the restore did not reinstate the author: " + body);
    }

    /// <summary>
    /// R10.39 and R10.60: each flag's time to action and the upheld rate are computable from the public
    /// log alone. Flag entries give the instants, records give the adjudications, and each record's
    /// digest ties it to the envelope the log accepted for its post (R6.25), so an auditor counts no
    /// record for content the log never held. The figures must equal the same figures computed through
    /// the private join, which knows each flag's post without any record (spec Increment 4). The test's
    /// own clock is kept only as the non-vacuity guard: two flags, 90 and 120 minutes, one upheld.
    /// </summary>
    [Fact]
    public async Task R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "r1039-" + Guid.NewGuid().ToString("N")[..8];

        var author = await PartyAsync(client, NewAgent("r1039-author"), ct);
        var first = await AskAsync(client, author, board, "Is canonical form unique?", "First " + Guid.NewGuid().ToString("N")[..8], ct);
        var second = await AskAsync(client, author, board, "Is canonical form stable?", "Second " + Guid.NewGuid().ToString("N")[..8], ct);

        var reporterA = await PartyAsync(client, NewAgent("r1039-a"), ct);
        await FlagAsync(client, reporterA, first.PostId, "spam", "Advertising.", ct);

        forum.Clock.Advance(TimeSpan.FromMinutes(30));
        var reporterB = await PartyAsync(client, NewAgent("r1039-b"), ct);
        await FlagAsync(client, reporterB, second.PostId, "incorrect", "The premise is wrong.", ct);

        forum.Clock.Advance(TimeSpan.FromMinutes(90));
        await OperatorAsync(ct, "moderate", "--post", first.PostId, "--category", "spam", "--effect", "withhold", "--reason", "Reviewed: advertising.", "--by", "reviewer");
        await OperatorAsync(ct, "moderate", "--post", second.PostId, "--category", "incorrect", "--effect", "dismiss", "--reason", "Reviewed: the premise holds.", "--by", "reviewer");

        // Only what an anonymous reader can fetch.
        var log = await PublicLogAsync(client, ct);
        static DateTimeOffset At(JsonElement e) => DateTimeOffset.Parse(e.GetProperty("server_ts").GetString()!, CultureInfo.InvariantCulture);

        var raisedAt = log.Where(e => TypeOf(e) == "flag.committed")
            .ToDictionary(e => e.GetProperty("event_id").GetString()!, At, StringComparer.Ordinal);

        string AcceptedDigest(string postId) => log
            .Single(e => TypeOf(e) == "post.accepted" && e.GetProperty("payload").GetProperty("post_id").GetString() == postId)
            .GetProperty("payload").GetProperty("digest").GetString()!;

        var publicTimeToAction = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var publicUpheld = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in log.Where(e => TypeOf(e) == "moderation.applied"))
        {
            var payload = record.GetProperty("payload");
            var postId = payload.GetProperty("post_id").GetString()!;
            if (postId != first.PostId && postId != second.PostId) continue;

            // R6.25: the record names the bytes it acted on, and they are the bytes the log accepted.
            Assert.Equal(AcceptedDigest(postId), payload.GetProperty("digest").GetString());

            var upholds = payload.GetProperty("effect").GetString() is "withhold" or "quarantine";
            foreach (var flag in payload.GetProperty("adjudicates").EnumerateArray().Select(f => f.GetString()!))
            {
                publicTimeToAction.TryAdd(flag, At(record) - raisedAt[flag]);
                if (upholds) publicUpheld.Add(flag); else publicUpheld.Remove(flag);
            }
        }

        // The same figures through the private join: the directory knows each flag's post from the
        // private store, and the fold decides upholding exactly as posture does.
        var events = Require(await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct));
        var details = Require(await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct));
        var moderation = FlagProjector.Fold(events);

        var privateTimeToAction = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var privateUpheld = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in FlagDirectory.Join(events, details).Flags.Where(f => f.PostId == first.PostId || f.PostId == second.PostId))
        {
            var history = moderation[flag.PostId].History;
            privateTimeToAction[flag.FlagId] = history.First(a => a.Adjudicates.Contains(flag.FlagId)).At.Value - flag.At.Value;
            if (ModerationPolicy.UpheldFlags(history).Contains(flag.FlagId)) privateUpheld.Add(flag.FlagId);
        }

        Assert.Equal(
            privateTimeToAction.OrderBy(p => p.Key, StringComparer.Ordinal),
            publicTimeToAction.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(privateUpheld.Order(StringComparer.Ordinal), publicUpheld.Order(StringComparer.Ordinal));

        // Non-vacuity: two flags, acted on 90 and 120 minutes after they were raised, one of them upheld.
        Assert.Equal([TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(120)], publicTimeToAction.Values.Order());
        Assert.Single(publicUpheld);

        // And the public log carries neither raiser.
        foreach (var entry in log.Where(e => TypeOf(e) is "flag.committed" or "moderation.applied"))
        {
            Assert.DoesNotContain(reporterA.Agent.AgentId, entry.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(reporterB.Agent.AgentId, entry.GetRawText(), StringComparison.Ordinal);
        }
    }
}
```

- [ ] **Step 2: Run the disclosure gate again, over a moderated fixture**

Spec Increment 4 asks for the gate again once moderation can act. After a record adjudicates the flag, that record is public, names the post, and is served by the same log route. So the gate's sweep has to be run over that state too, not only over an unmoderated one.

In `tests/Curia.Api.Tests/FlagPrivacyGateTests.cs`, add this fact after `R10_62_NoSurfaceServesAFlagsRaiserRationaleOrUnadjudicatedPost`. It reuses Task 6's `FlagAQuestionAsync`, `SweepAsync` and `AssertTheSweepReachedEverything`, and withholds through `ForumFixture.WithholdAsync`, which since Task 9 runs R10.59's writer in category `spam`:

```csharp
    /// <summary>
    /// The same gate over a moderated fixture (spec Increment 4). Once a moderator adjudicates the
    /// flag, the record is public and names the post, its envelope digest, the category and the flag
    /// it adjudicates (R10.60, R6.25). Still no surface serves the raiser or the rationale, the record
    /// included, and the flag's own leaf still names no post. The record is written by R10.59's
    /// writer, as every withholding in this suite is (trap 16).
    /// </summary>
    [Fact]
    public async Task R10_60_AfterModerationTheRecordIsPublicAndStillNamesNoRaiser()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var flagged = await FlagAQuestionAsync(client, ct);

        await forum.WithholdAsync(flagged.PostId, ct);
        var sweep = await SweepAsync(client, flagged, ct);

        AssertTheSweepReachedEverything(sweep);
        Assert.True(sweep.RecordLeaf is not null, "the log walk never met the moderation record -- the withholding wrote nothing");
        Assert.True(sweep.Leaks.Count == 0, string.Join("\n", sweep.Leaks));

        using var record = JsonDocument.Parse(sweep.RecordLeaf!);
        var payload = record.RootElement.GetProperty("entry").GetProperty("payload");
        Assert.Equal(flagged.PostId, payload.GetProperty("post_id").GetString());
        Assert.Equal(flagged.Digest, payload.GetProperty("digest").GetString());
        Assert.Equal("spam", payload.GetProperty("category").GetString());

        // The record names the flag by the event id its public entry carries, so the two can be joined from the log alone.
        var adjudicated = Assert.Single(payload.GetProperty("adjudicates").EnumerateArray()).GetString()!;
        Assert.Contains(sweep.FlagLeaves, leaf => leaf.Contains($"\"event_id\":\"{adjudicated}\"", StringComparison.Ordinal));
    }
```

- [ ] **Step 3: Run the loop and both gates**

Run: `dotnet test tests/Curia.Api.Tests -c Release --nologo --filter "FullyQualifiedName~ModerationLoopTests|FullyQualifiedName~FlagPrivacyGateTests"`
Expected: all four tests pass, the two `ModerationLoopTests` and the two `FlagPrivacyGateTests`.
- If the first loop test fails at `the author did not reach T1`, the test's setup is wrong. Compare it with `TwoAgentsConversationTests`, and do not weaken an assertion.
- If it fails at the refused answer, R10.61 is not reaching `TierPolicy`. The defect is in Task 2 or Task 9's code.
- If the R10.39 test's two dictionaries differ, the public derivation and the private join disagree about a flag. Find which side is wrong before touching either; neither is the oracle for the other by default.

- [ ] **Step 4: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'The loop: an upheld flag demotes, a restore reinstates, R10.39 from the public log\n\nTable 11s T1 clause carries information for the first time: withholding one of\nthree clean questions drops the author to T0 on every read path, and the log\nalone yields time to action and the upheld rate, equal to the private join.\nThe disclosure gate runs again over a moderated fixture.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---
### Task 11: Falsify every new gate

**Files:**
- Create (scratchpad only, never committed): `falsify.py`
- No tracked file changes. Every patch is restored, and each restore is proved.

**Preconditions:**
- Tasks 1–10 are committed, and `git status --porcelain` is empty.
- The runner restores from a kept copy with a **plain copy**: `shutil.copyfile`, which gives the file a fresh mtime. It never uses `copy2` and never `git checkout`. `copy2` restores the old mtime, and MSBuild then keeps the patched assembly (trap 18).
- No case is quoted until the unpatched gates have been rebuilt with `--no-incremental` and run green (Step 3).

- [ ] **Step 1: Write the runner in the scratchpad**

```python
#!/usr/bin/env python3
"""Falsify each gate: patch, run its filter, restore by plain copy, prove the restore clean."""
import pathlib, shutil, subprocess, sys

ROOT = pathlib.Path.cwd()
KEEP = pathlib.Path(sys.argv[1]); KEEP.mkdir(parents=True, exist_ok=True)

def dotnet(project, flt):
    return ["dotnet", "test", project, "-c", "Release", "--nologo", "--filter", flt]

MOD = "src/Curia.Domain/Moderation/Moderation.cs"
PROJ = "src/Curia.Application/Projections/FlagProjection.cs"
DIR = "src/Curia.Application/Projections/FlagDirectory.cs"
RAISE = "src/Curia.Application/Moderation/RaiseFlag.cs"
APPLY = "src/Curia.Application/Moderation/ApplyModeration.cs"
GATE = "tests/Curia.Api.Tests/FlagPrivacyGateTests.cs"
KIND_LINE = "            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),\n"
DETAIL_FIRST = ("        // The private row first (see the remarks above).\n"
                "        var stored = await _details\n"
                "            .AppendAsync(new FlagDetail(id, postId, raisedBy, rationale, salt), cancellationToken)\n"
                "            .ConfigureAwait(false);\n"
                "        if (!stored.TryGetValue(out _, out var storeError)) return Result<FlagRaised>.Fail(storeError!);\n\n")
RETURN_LINE = "        return appended.Map(recorded => new FlagRaised("

CASES = [
    dict(id="1 automated record upholds", cmds=[dotnet("tests/Curia.Domain.Tests", "FullyQualifiedName~ModerationTests")],
         edits=[(MOD, "Permits(action) && action.Moderator is not ModeratorKind.Automated;", "Permits(action);")]),
    dict(id="2 forbidden record honoured", cmds=[dotnet("tests/Curia.Domain.Tests", "FullyQualifiedName~ModerationTests")],
         edits=[(MOD, "            if (!Permits(action)) continue;\n", "")]),
    dict(id="3 category-keyed upholding", cmds=[dotnet("tests/Curia.Application.Tests", "FullyQualifiedName~FlagProjectorTests")],
         edits=[(PROJ, "    public bool HasUpheldFlag => !UpheldFlags.IsEmpty;",
                 "    public bool HasUpheldFlag => History.GroupBy(a => a.Category).Any(g => g.Last().Effect is ModerationEffect.Withhold or ModerationEffect.Quarantine);")]),
    dict(id="4a raiser in the entry", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~FlagPrivacyGateTests")],
         edits=[(RAISE, KIND_LINE, KIND_LINE + "            new(FlagProjector.RaisedByField, new JsonValue.String(raisedBy)),\n")]),
    dict(id="4b rationale in the entry", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~FlagPrivacyGateTests")],
         edits=[(RAISE, KIND_LINE, KIND_LINE + "            new(FlagProjector.RationaleField, new JsonValue.String(rationale)),\n")]),
    dict(id="4c post in the entry", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~FlagPrivacyGateTests")],
         edits=[(RAISE, KIND_LINE, KIND_LINE + "            new(FlagProjector.PostIdField, new JsonValue.String(postId)),\n")]),
    dict(id="5 a route the gate cannot drive", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~FlagPrivacyGateTests")],
         edits=[(GATE, "            (\"GET\", \"/health\") => [(\"/health\", null)],\n", "")]),
    dict(id="5b the gate walks no log", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~FlagPrivacyGateTests")],
         edits=[(GATE, "            if (response.StatusCode == HttpStatusCode.NotFound) return i;", "            if (response.StatusCode == HttpStatusCode.NotFound || i >= 0) return 0;")]),
    dict(id="6 event written before the detail row", cmds=[dotnet("tests/Curia.Application.Tests", "FullyQualifiedName~RaiseFlagTests")],
         edits=[(RAISE, DETAIL_FIRST, ""), (RAISE, RETURN_LINE, DETAIL_FIRST + RETURN_LINE)]),
    dict(id="7 commitment not recomputed", cmds=[dotnet("tests/Curia.Application.Tests", "FullyQualifiedName~FlagDirectoryTests")],
         edits=[(DIR, "|| !string.Equals(expected, commitment, StringComparison.Ordinal))",
                 "|| (string.IsNullOrEmpty(expected) && !string.Equals(expected, commitment, StringComparison.Ordinal)))")]),
    dict(id="8 empty adjudicates", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~ModerationLoopTests")],
         edits=[(APPLY, "                .Select(f => f.FlagId),", "                .Where(_ => false).Select(f => f.FlagId),")]),
    dict(id="9 moderator recorded as automated", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~ModerationLoopTests")],
         edits=[(APPLY, "postId, ModeratorKind.Human, moderator.Value, effect,", "postId, ModeratorKind.Automated, moderator.Value, effect,")]),
    dict(id="10 reason not screened", cmds=[dotnet("tests/Curia.Application.Tests", "FullyQualifiedName~ApplyModerationTests")],
         edits=[(APPLY, "        if (!screening!.MayPersist)\n            return Result<ModerationRecorded>",
                 "        if (screening!.Outcome is (ScreeningOutcome)(-1))\n            return Result<ModerationRecorded>")]),
    dict(id="11 no-op not refused", cmds=[dotnet("tests/Curia.Application.Tests", "FullyQualifiedName~ApplyModerationTests")],
         edits=[(APPLY, "        var noOp = ModerationPolicy.MayServe(before)", "        var noOp = false && ModerationPolicy.MayServe(before)")]),
    dict(id="12a record without adjudicates", cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~ModerationLoopTests")],
         edits=[(APPLY, "            new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(id => (JsonValue)new JsonValue.String(id))])),\n", "")]),
    dict(id="12b record without digest",
         cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~ModerationLoopTests"),
               dotnet("tests/Curia.Application.Tests", "FullyQualifiedName~ApplyModerationTests")],
         edits=[(APPLY, "            new(FlagProjector.DigestField, new JsonValue.String(post.Digest)),\n", "")]),
    dict(id="13 writer spells an effect the fold cannot read",
         cmds=[dotnet("tests/Curia.Api.Tests", "FullyQualifiedName~BatchRetrievalTests|FullyQualifiedName~R10_36_AWithheldPostStopsBeingServedAndIsNotDeleted")],
         edits=[(APPLY, "new JsonValue.String(ModerationEffects.Wire(effect))", "new JsonValue.String(effect.ToString())")]),
    dict(id="14 acta vector's leaf off by one digit",
         cmds=[dotnet("tests/Curia.Canon.Tests", "FullyQualifiedName~ActaLeafVectorTests"),
               dotnet("tests/Curia.Client.Tests", "FullyQualifiedName~ActaLeafRecomputationTests"),
               ["cargo", "test", "--manifest-path", "rust/curia-testis/Cargo.toml", "--locked", "--test", "vectors", "acta"]],
         edits=[("conformance/acta/flag-committed-entry/expected.leaf", "66128f1f", "76128f1f")]),
    dict(id="15 private store rewritable", cmds=[dotnet("tests/Curia.Infrastructure.Tests", "FullyQualifiedName~FlagDetailGrantTests")],
         edits=[("db/0004_create_flag_details.sql", "GRANT INSERT, SELECT ON flag_details", "GRANT INSERT, SELECT, UPDATE, DELETE ON flag_details"),
                ("db/0004_create_flag_details.sql", "REVOKE UPDATE, DELETE ON flag_details FROM __CURIA_APP_ROLE__;   -- R11.6, R11.32\n", "")]),
    dict(id="16 commitment normalized", cmds=[dotnet("tests/Curia.Domain.Tests", "FullyQualifiedName~FlagCommitmentTests")],
         edits=[("src/Curia.Domain/Moderation/FlagCommitment.cs", "CanonicalJson.Canonicalize(input)", "CanonicalJson.CanonicalizeWithNfc(input)")]),
]

MARKERS = ("[FAIL]", "Assert.", "cannot drive", "served the", "never met", "names the post", "FAILED", "panicked", "mismatch", "exited")

for case in CASES:
    files = sorted({f for f, _, _ in case["edits"]})
    for f in files: shutil.copyfile(ROOT / f, KEEP / f.replace("/", "__"))
    ok = True
    for f, old, new in case["edits"]:
        p = ROOT / f; s = p.read_text(encoding="utf-8"); n = s.count(old)
        if n != 1:
            print(f"[{case['id']}] PATCH MISMATCH in {f}: {n} matches -- fix the patch, not the code"); ok = False; break
        p.write_text(s.replace(old, new), encoding="utf-8")
    if ok:
        for cmd in case["cmds"]:
            r = subprocess.run(cmd, capture_output=True, text=True)
            out = r.stdout + r.stderr
            # Any MSBuild error line -- CS, CA, IDE or MSB -- means the patched code did not build. A
            # failing test run prints none, so this cannot report RED as BUILD FAILED, and an analyzer
            # error can no longer pass for RED.
            status = "BUILD FAILED" if ": error " in out else ("RED" if r.returncode != 0 else "GREEN -- bad patch or a gap")
            print(f"[{case['id']}] {' '.join(cmd[:3])} {status}")
            for line in out.splitlines():
                if any(m in line for m in MARKERS):
                    print("    " + line.strip()[:240])
    for f in files: shutil.copyfile(KEEP / f.replace("/", "__"), ROOT / f)   # plain copy: a fresh mtime (trap 18)
    clean = subprocess.run(["git", "diff", "--quiet", "--", *files]).returncode == 0
    print(f"[{case['id']}] restore {'clean' if clean else 'DIRTY -- STOP'}")
    if not clean: sys.exit(1)
```

- [ ] **Step 2: Run it**

From the repository root, with `CURIA_TEST_POSTGRES` exported and `curia-testis` built:

```bash
python3 <scratchpad>/falsify.py <scratchpad>/falsify-keep 2>&1 | tee <scratchpad>/falsify.log
```

Each case must print `RED` and `restore clean`. Case 12b prints `RED` twice, once per suite, and case 14 three times, once per runner.

| Case | Must fail, by name |
|---|---|
| 1 | `An_automated_quarantine_is_not_an_upheld_flag`; `An_automated_dismissal_does_not_release_a_flag_a_human_upheld`; `R10_61_AdjudicatedFlagsCountOnlyReviewingRecords` |
| 2 | `An_automated_withholding_does_not_stop_a_post_being_served` |
| 3 | `R10_61_AFlagRaisedAfterAWithholdingIsNotUpheldUntilARecordNamesIt`, the late-flag test (spec §4.3), at its first `HasUpheldFlag`; also `R10_61_ARecordWithoutAdjudicatesStillWithholdsAndUpholdsNothing` and `R10_36_AQuarantineMakesThePostUnservable`. `ARestoreMakesThePostServableAgain` stays green, as it should: under the category-keyed rule a restore released the category too |
| 4a, 4b, 4c | both `FlagPrivacyGateTests` facts, each leak line naming `GET /v1/log/entries/{index:long}` (spec §4.4): `served the raiser's identity` (4a), `served the flag's rationale` (4b), `served the flagged post's id in the flag's own leaf` (4c), each to an anonymous caller and to an uninvolved agent |
| 5 | both gate facts: `Registered surfaces this gate cannot drive … GET /health` |
| 5b | both gate facts: `the log walk never met the question's own leaf`. The patched walk reports a log of size 0, so the fetch-count guard passes and the leaf guard is the one that catches it |
| 6 | `R10_62_AFailedDetailAppendLeavesNoCommitmentForTheJoinToSkip`, the skip-and-count test (spec §4.6), at the join's skip count: `Collection: [["flag.committed: no detail"] = 1]`; also `R11_21_ARationaleCarryingANulIsRefusedAndNothingIsWritten`, whose refused row now leaves an entry behind |
| 7 | `R10_62_ATamperedDetailIsSkippedAndCounted` |
| 8 | `R10_61_AnUpheldFlagDemotesItsAuthorAndARestoreReinstatesIt`, at the refused answer: expected `Forbidden`, got `Created`. This is the case that shows Table 11's clause now carries information. `R10_39_…` fails too, at its dismissal: a record naming no flag dismisses nothing, so the writer refuses it as `curia/moderation/no-op` |
| 9 | both loop tests, at their first withholding: `curia-operator moderate … exited 2: … curia/moderation/not-permitted` |
| 10 | `R10_60_ACredentialInTheReasonIsRefusedAndNothingIsAppended` |
| 11 | `R10_39_ASecondIdenticalRecordIsRefusedAsANoOp`; `R10_39_ADismissalOfOpenFlagsIsARecordAndADismissalOfNothingIsNot`; `R10_59_ARestoreAfterAProactiveWithholdingIsARecordARestoreOfAServablePostIsNot` |
| 12a | both `ModerationLoopTests`: `R10_61_…` at the refused answer (a record naming no flag upholds nothing), and `R10_39_…` at the missing `adjudicates` member (`KeyNotFoundException`) |
| 12b | `R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone`, the public-log derivation (spec §4.12), at the missing `digest` it ties each record to its accepted post with (`KeyNotFoundException`); and, in memory, `R10_60_ARecordNamesThePostItsDigestAndTheFlagsItAdjudicates` |
| 13 | `R9_18_OneItemPerElementInOrderAndNothingOmitted`; `R10_36_AWithheldPostStopsBeingServedAndIsNotDeleted`. They would stay green if the fixture still hand-built its events (trap 16) |
| 14 | `R6_46_TheClientRecomputesEveryPublishedLeaf(name: "flag-committed-entry")` and the Rust `acta` family test (`[FAIL] acta/flag-committed-entry: leaf hash: expected 76128f1f…, got 66128f1f…`), each naming `flag-committed-entry`; and `R6_46_EveryVectorCanonicalizesUnderThePureProfileAndHashesToItsLeaf`, which names no vector and prints only `Expected: "76128f1f…"` against `Actual: "66128f1f…"` |
| 15 | `R11_32_TheAppRoleCannotUpdateAFlagDetail` and `R11_32_TheAppRoleCannotDeleteAFlagDetail`: no exception is thrown, and each failing test's name names the privilege that was granted |
| 16 | `R10_62_TheCommitmentIsOverPureRfc8785WithNoNormalization` alone, at its pinned value: expected `sha256:be4b171a…`, actual `sha256:552d7e39…`, the NFC form, which composes `cafe` + U+0301 to U+00E9. The other six `FlagCommitmentTests` stay green, as they must: every other input is ASCII, where NFC is the identity. `CanonicalJson`'s own remarks say anything signed or verified SHALL use `CanonicalizeWithNfc`, which invites exactly this swap |

Two patches follow the spec's wording rather than the shortest edit that goes red:
- **Case 3 restores the category-keyed rule itself.** Stage 8's rule was `Flags.Any(f => IsUpheld(f.Kind, History))`: a flag is upheld while the latest record in its category quarantines or withholds. After Task 5 the fold holds no flags, because a flag's entry names no post (R10.62). So the patch applies the same `IsUpheld` over the categories the records cite. For the late-flag test, where a flag of that category does exist, the two are the same rule.
- **Case 6 writes the event before the detail row** (spec §4.6), instead of ignoring the failed append. The detail append moves below the event append, and its failure is still returned, so the only change is the order.

If a case prints `PATCH MISMATCH`, `BUILD FAILED` or `GREEN`, the patch is wrong for the code as written. Inspect it and correct the **patch**, never the product code, then re-run that case. Record every correction. A patch that stays green on its first attempt is a finding until it is shown to be a bad patch (trap 13).

- [ ] **Step 3: Rebuild clean, then run the gates unpatched**

```bash
git status --porcelain
dotnet build Curia.sln -c Release --no-incremental --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!"
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked
```

Expected:
- `git status --porcelain` prints nothing.
- The build reports `0 Warning(s)`.
- **Eleven** `Passed!` lines and no `Failed!`, read from the `grep` output as printed. Each line begins with its status word and ends with its assembly's name. Do not pipe it through `sed` or anything else that strips the status word: a line with `Failed!` removed reads exactly like a passing one.
- The Rust suite is green.

Only now is `falsify.log` quotable. Keep it; Task 12 copies from it.

---

### Task 12: Register, documents, and the traps

**Files:**
- Modify: `IMPLEMENTATION_PLAN.md`
- Modify: `CLAUDE.md`, `README.md`
- Modify: `docs/superpowers/plans/2026-08-27-moderation-rationale-and-delegation.md` (its status block, and Task B4's first bullet)
- Modify: `docs/superpowers/specs/2026-09-26-moderation-that-can-act-design.md` (its status line)

Match every edit by its text, not by a line number. The numbers quoted are from the tree this plan was written against, and they move as earlier insertions land.

- [ ] **Step 1: Open and close the two register entries**

1. In the register header's **Closed** list, change `D17 and D19 by the screener stage (2026-09-25).` to `D17 and D19 by the screener stage (2026-09-25); D20 and D21 by the moderation stage (2026-09-26).` The new clause goes before the sentence's full stop, not after it.

2. Insert both entries after the D19 entry, before `### Observed during the screener stage, not acted on`. Paste into each `Falsified` line the lines `falsify.log` printed for the cases it names.

   ```markdown
   ### D20 — no flag could be upheld *(opened and closed by the moderation stage, 2026-09-26)*

   **Found by reading, confirmed by searching every producer**, by `curia-architect` while
   choosing this stage: `moderation.applied` had a fold (`FlagProjection.cs`) and no writer
   anywhere in `src/`. The Phase 2 record's Stage 8 deferred the writer because *delegated*
   moderation is Phase 4; R10.36's *human* arm is not, and nothing deferred it. So Table 11's T1
   "≥ 3 questions with no upheld flags", T2/T3's clean record and R7.8's demotion were vacuous —
   implemented, tested, guarding nothing — and R6.17's only remedy could not be exercised. F1 had
   written that the clause "becomes real the moment flags are servable"; it did not. F1's defect
   one layer up.

   **Closed** by errata G13's R10.59–R10.61: `ApplyModeration`, reached only by
   `curia-operator moderate`, writes a record carrying the post's digest and the flags it
   adjudicates; *upheld* is decided per flag by the reviewing record that names it, automated
   records change no flag's state, and forbidden records are ignored by every fold (PR #59's Task
   B1, confirmed by execution first). `ModerationLoopTests` drives an author from T1 to T0 by
   upholding one flag and back by restoring it. Falsified: *(paste cases 1, 2, 3, 8, 9, 10, 11 and 13)*.

   ### D21 — the log served every flag's raiser and rationale to anyone *(opened and closed by the moderation stage, 2026-09-26)*

   **Confirmed by execution** on 2026-09-25 against a pristine archive of the tree: an anonymous
   walk of `GET /v1/log/entries/{i}` returned a `flag.raised` leaf carrying `raised_by`, the
   rationale and the post (errata G13, finding 2). R6.46/R6.47 make every event a leaf and R6.51
   serves each verbatim, so R10.44, G3's holding and the `curia_flag` description were false. The
   R10.44 tests held the two listing routes by name and never reached the log — trap 15.

   **Closed** by R10.62 and R11.32: a flag enters the log as `flag.committed` — its kind and a
   salted commitment, on its own aggregate, with no actor — and its post, raiser, rationale and
   salt go first to `flag_details` (db/0004, INSERT/SELECT only). `FlagDirectory` serves R7.18's
   views from the join, believes a private row only if it opens its entry's commitment, and counts
   every skip. `FlagPrivacyGateTests` walks every registered surface from the endpoint data
   source, anonymously and as an uninvolved agent. It was red on the log route before the writer
   changed. The new `conformance/acta/flag-committed-entry` vector pins the entry kind in C# and in
   Rust, with no leaf computation changed (R15.1). **Flags raised before this stage stay public
   forever**; the only logs that held any were test databases. Falsified: *(paste cases 4a, 4b,
   4c, 5, 5b, 6, 7, 12a, 12b, 14, 15 and 16)*.
   ```

3. In `### Still unverified — do not cite as established`, replace the bullet beginning `**\`ModerationPolicy.IsUpheld\` may count an automated quarantine as upheld.**` with what Task 2 Step 2 printed. Write either `**Confirmed by execution and fixed (D20):** …` or `**Refuted by execution:** …`, and say which half. The second bullet, about the quarantine floor, is unchanged.

- [ ] **Step 2: Record what this stage observed and did not fix**

Insert a new section after the D21 entry:

```markdown
### Observed during the moderation stage, not acted on

- **R10.38 is now a live unmet obligation.** Withholding is exercisable (R10.59) and owners have no
  channel (D7); an author agent learns an outcome only from R9.18's `withheld` state or a 404.
  Deferred by the spec's Decision 17, together with appeal.
- **R10.39's publication.** Every figure but the appeal rate is computable from the public log
  (`ModerationLoopTests`); the route that publishes them is a small follow-on stage (Decision 18).
- **An operator's action on an unflagged post moves no one's standing** (Decision 16). The lever
  against a hostile agent is credential suspension (Table 6), which has no operator verb yet.
- **A withheld post can be re-posted as a revision**, which is its own post and needs its own
  record (Decision 21).
- **Owner identities stay in a permanent public log.** Every `agent.owner-attested` leaf publishes
  the owner↔agent mapping R4.3 says SHOULD NOT be public by default, and R10.17/R8.59 serve the
  owner on every post. An owner identifier can be personal data, so whether a deployment may
  publish it permanently is a data-protection decision **left open for the owner** (Decision 25);
  no task depends on it.
- **A flag's raiser cannot yet prove its flag was recorded.** The salt is kept in the private
  store; serving it on the raiser's own view, and a `curia_verify` mode for flag receipts, are
  later work (Decision 19).
```

- [ ] **Step 3: Bring "Start here" and "What comes next" current**

1. In "Start here", after the paragraph that begins `**Stage 4 found two defects outside its scope and fixed neither**`, add:

   > **The moderation stage** (`docs/superpowers/plans/2026-09-26-moderation-that-can-act.md`, errata G13) closes **D20** and **D21**. R10.36's human arm acts out of band through `curia-operator moderate`, so a flag can be upheld and Table 11's T1 clause carries information for the first time. A flag enters the log as its kind and a salted commitment, with who raised it, why, and against which post held in the private `flag_details` store (db/0004). Flags raised before it stay public in the log, permanently.

2. In "What comes next", replace item 2 (the one beginning `**PR #59's moderation plan**`) with:

   > 2. **PR #59's moderation plan**, now two parts. Its Task B1 was absorbed and closed by the moderation stage (D20). What remains is **Part A**, a raiser reading its own rationale (errata G4, still reserved), and **Part B**, the delegated grant and an HTTP moderation queue, which is Table 22's Phase 4. Part B's premise closed with Stage 1 and should be re-argued. Beside it sits one small stage the moderation stage handed on: **R10.39's publication**, an anonymous statistics route computed only from the public log, with `ModerationLoopTests`' derivation as its oracle.

3. In the paragraph beginning `Before any of those, the **next errata pass** has a queue`, append:

   > The moderation stage adds **R4.3 against the attestation leaves**, which is open for the owner as a data-protection question (its spec's Decision 25). It also records **D16 as decided** (option 1: run `Curia.Architecture.Tests` in both configurations in CI), to be carried out as its own one-line CI change.

- [ ] **Step 4: Add the two traps**

Change the Traps header's `17 and 18 are the screener stage's.` to `17 and 18 are the screener stage's; 19 and 20 are the moderation stage's.`, and append after item 18:

```markdown
19. **A criterion whose input nothing produces.** Table 11's "no upheld flags" was implemented,
    tested and green while nothing in `src/` could write the moderation record that makes a flag
    upheld (D20). F1 found the same clause vacuous for want of a flag endpoint, shipped the
    endpoint, and recorded the clause as real — the missing half was the other one. **For every
    criterion, name the code path that can make it false, and run it.**

20. **A privacy promise checked on the surfaces it names.** R10.44 was held on the two flag-listing
    routes while the log route served every flag in full (D21) — the tests' scope was the routes
    someone thought of. A second publication channel is invisible to a test that names the first.
    **Derive the scope from the registrations, and make an undriven surface a failure.**
```

- [ ] **Step 5: The other documents**

- **`CLAUDE.md`, "What works today" paragraph.** After the clause about flags, the flag listing and moderation state, add: `a human moderator acting out of band through curia-operator moderate (errata G13), and flags that enter the log as a commitment, with their raiser and rationale held privately`.
- **`CLAUDE.md`, "What does not" sentence.** Add: `R10.38's notice and appeal, and R10.39's published statistics`.
- **`README.md`.** Where it describes flags or the operator tool, name `curia-operator moderate` and `curia-operator flags`, and say that a flag's raiser and rationale are never published.
- **PR #59's plan.** Two edits, which together absorb Task B1 and answer the Task B4 bullet it left open.
  - Add a line to its top status block: `> **Task B1 was absorbed and closed by the moderation stage (2026-09-26, register D20).** Parts A and B remain; Task B4's first bullet is answered for the human arm.`
  - In `### Task B4`, Step 2, directly under its first bullet (the one saying `moderation`/`apply` has no writer), add this indented line. It answers the bullet for the human arm and leaves the delegated arm's half open, because that half is Part B's:

    ```markdown
      *Answered for R10.36's human arm by the moderation stage (2026-09-26; errata G13's R10.59 and R10.60; register D20).* `ApplyModeration` is the writer, reached only by `curia-operator moderate`, out of band under R11.6's grant, with no HTTP route and no Table 10 pair. R10.36's admissibility check runs on its write path as `ModerationPolicy.Authorize`. R10.37's signed entry is, for a human moderator who holds no key, a leaf under a head signed with the log key. The record also carries the post's digest and the flags it adjudicates, derived by the writer. **Still unanswered:** the delegated arm's writer, a T3 grantee acting through `moderation`|`apply`, which is this plan's Part B.
    ```
- **The spec.** Set its status to `**Status:** implemented by \`docs/superpowers/plans/2026-09-26-moderation-that-can-act.md\`.`

- [ ] **Step 6: Check the documents**

```bash
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
git ls-files -m -o --exclude-standard | xargs grep -InE '/Users/|/home/[a-z]|100\.[0-9]+\.[0-9]+\.' || echo "no private identifiers"
```

Expected: both spec checks clean, and `no private identifiers`.

- [ ] **Step 7: Commit**

```bash
but status -fv
but commit -b moderation-that-can-act -m "$(printf 'Register: D20 and D21 closed, with what each falsification printed\n\nTwo traps added: a criterion whose input nothing produces, and a privacy promise\nchecked on the surfaces it names.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')" <change-ids>
```

---

### Task 13: Every gate, then the PR

- [ ] **Step 1: Run the gates CI runs, plus the architecture tests in Debug (D16, option 1)**

```bash
export CURIA_TEST_POSTGRES="Host=localhost;Port=5432;Username=$(whoami);Database=postgres"
dotnet restore Curia.sln --locked-mode
dotnet build Curia.sln -c Release --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
cargo build --manifest-path rust/curia-testis/Cargo.toml --bin curia-testis
dotnet test Curia.sln -c Release --nologo 2>&1 | grep -E "Passed!|Failed!"
dotnet build Curia.sln -c Debug --nologo 2>&1 | grep -E "Warning\(s\)|Error\(s\)"
dotnet test tests/Curia.Architecture.Tests -c Debug --nologo 2>&1 | grep -E "Passed!|Failed!"
python3 tools/spec-checks/check-spec.py
python3 tools/spec-checks/falsify-spec-checks.py
cargo fmt --manifest-path rust/curia-testis/Cargo.toml --check
cargo clippy --manifest-path rust/curia-testis/Cargo.toml --all-targets --locked -- -D warnings
cargo test --manifest-path rust/curia-testis/Cargo.toml --locked
dotnet build tools/Curia.Differential/Curia.Differential.csproj -c Release
cargo build --manifest-path rust/curia-testis/Cargo.toml --release --bin curia-differential
node tools/differential-oracle/compare.mjs --fail-on-divergence
```

Expected:
- `0 Warning(s)` from both builds, Release and Debug.
- **Eleven** `Passed!` lines and no `Failed!`, read from the `grep` output as printed (Task 11 Step 3 says why nothing may strip the status word).
- The Debug architecture run passes. It reads the Debug outputs the Debug build just wrote. Without that build, CS-15 fails naming a missing `Curia.Domain.Tests.dll`, or passes over stale assemblies left by an older build.
- Both spec checks clean.
- Rust clean, with `corpus_size_matches_charter` passing at 70 and 76.
- The differential exits 0.

Never `head` a gate's output. If the test run regenerated a tracked file (`RESULTS.md`, a baseline), `git status --porcelain` shows it. Commit it only if it is the expected change, and say so.

- [ ] **Step 2: Confirm nothing is left uncommitted**

Run: `git status --porcelain`. Expected: empty.

- [ ] **Step 3: Open the PR**

Write the PR text to the scratchpad as `pr.md`. `but pr new -F` takes the file's first line as the PR title, so line 1 is the title, for example `Moderation that can act, and flags that stay private (errata G13)`, and a blank line follows it. The body covers:
- the two findings, with the probe's leaf quoted;
- the six requirements in one line each;
- what R15.1 and G9 say about the new entry kind, and what happened to legacy flags;
- the falsification table from `falsify.log`;
- the test plan, with the per-assembly counts Step 1 printed;
- the observations recorded but not fixed, including the one question left open for the owner.

End the body with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

```bash
but push moderation-that-can-act
but pr new moderation-that-can-act -F <scratchpad>/pr.md
```

Then watch CI to completion with `gh pr checks <number> --watch`. A red CI run is reported with its log. It is not re-run until it passes.
