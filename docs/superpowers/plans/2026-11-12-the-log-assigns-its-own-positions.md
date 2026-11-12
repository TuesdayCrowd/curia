# The Log Assigns Its Own Positions: Implementation Plan (PR S; opens register D123 and D61 item 272; errata G69)

> Intended repository path: `docs/superpowers/plans/2026-11-12-the-log-assigns-its-own-positions.md` (staged in the scratchpad because the architect's write allowlist does not cover it; the builder copies it to that path unchanged).
> Written by `curia-architect` on 2026-11-12 as **PR S**, the small `db/` PR the owner put **first** (Q-C, "Second round" of the memory record `owner-security-decisions-2026-11-11`), before PR N. **Planned against `origin/main` at `b348f6a9` (PR H merged as #152) and read against `origin/the-vector-channel-is-semantic` at `4d3cb47a` (PR #151, open, waiting on the owner's `ci.yml` merge; it carries `main`).** Every identifier below was computed with `git show <ref>:<path>` and `git grep <pattern> <ref>`, not `grep` over the GitButler workspace.
> **Marks.** **[read]** is a fact read in the tree by the author of this plan. **[measured]** is a fact the author ran on a scratch database and role on the local PostgreSQL server (`psql` as the table owner and as the role; the database and role were dropped after, and the script is `.../scratchpad/planD/exp.sh`, `exp2.sh`, `exp3.sh`); **no Forum was started and no test was run.** **[record]** is a fact taken from PR N's Task 0 record (`.superpowers/sdd/2026-11-11-planN/n-task0.txt`), which the author read and did not re-run. **[unverified]** is a claim nothing has read or run, and each names the Task 0 measurement that settles it.
> Read for this plan: `db/0001`, `db/0002:130-175`, `db/0005`; `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs` and its `<EmbeddedResource>` glob in `Curia.Infrastructure.csproj`; `PostgresEventStore.cs` (the INSERT at `:441`, the guard's reads at `:288-320`, the paged read at `:374`, the by-aggregate read at `:340`); `Curia.Application/Ports/EventReaderExtensions.cs`; `LifecycleEntryScan.cs`; `PostgresStoreFaults.cs`; `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs:60-330`, `AppRoleGrantRefusalTests.cs`, `SchemaMigrationsTests.cs`, `PostgresAppendGuardTests.cs` (head), `HarnessRehearsalTests.cs` (by grep); `tests/Curia.Api.Tests/RoleForumFixture.cs:10-125`; `tools/fault-harness/fault.sh` (whole); the errata's G67 (form) and the white paper's R11.6 and Appendix D `events`; `IMPLEMENTATION_PLAN.md:15` (the first-free line); PR N's plan and its Amendment 1.
> **What a builder must not do with this plan:** treat a **[unverified]** as a fact. The two that carry the most weight are what a `seq` 0 row does to the token path and to every detector (M-S2), and what a planted `seq` ahead of the identity does through the Forum (M-S3): both are read from the code and consistent with the record, and neither has been run through a Forum.

## Order against the neighbours, and why

PR S is the first of the revised order (S, N, N2, PR O, the hosting join). It is small on purpose and independent of everything: it touches `db/`, one migration list, three test fixtures, one fault-harness script, one start-up service and its port, and the three documents. **It does not wait for #151, PR N or anything else, and PR N does not start its consumers (Task 4) until S has merged**, because N's probes run on fixtures that apply `db/0006`. The reasons it comes first are the owner's (Q-C) and one more that the reading found: **the chosen-`seq` hole is not only the denial of service Task 0 measured. A row at `seq` 0 or below is invisible to every forward read (all of them start at `seq > 0`) and visible to the by-aggregate read the token path uses today, so it is a forged lifecycle row that no head, proof, start-up scan, stop or audit can see** (M-S2). Every detective control the beta rests on (the 7006 scan, the stop, PR N's audit and 7007) reads forward from zero; this PR is what makes "forward from zero" the whole log.

**Files PR S shares with the open PRs.** `git diff --name-only origin/main origin/the-vector-channel-is-semantic` is 87 files; this plan's scope shares with it only the three documents (`IMPLEMENTATION_PLAN.md`, the errata, `README.md`, `CLAUDE.md`) and `src/Curia.Api/Program.cs` (#151 edits the embedder's registration; S adds one hosted-service registration: mechanical). It does **not** touch `.github/`, `Directory.Packages.props`, any `packages.lock.json`, `Curia.sln` or any csproj (the `db/*.sql` glob already embeds a new file), so **it is not the owner's `ci.yml` merge**. It **is** a deployment step the owner performs (Task 6's runbook), and its PR body says so.

**Allocations: computed on 2026-11-12 against the two refs, and named with what each was computed against** (identifiers are written only when the task that writes them runs; Task 1 re-greps):
- **Errata G69.** Highest entry on `main` is G67; G68 is written in five places on `main` as "PR N's (provisional)", G64 as PR 8's and G66 as D10b's. `git grep -E 'G69|G70'` is empty on both refs. **This PR: G69**, the first number nobody has written down. It does not take G68 because G68 is PR N's in merged text and N's plan names it in sixteen places; entries are not in numeric order in the file (G67 precedes G63 on `main`), so a gap is not a defect.
- **Requirements: none new.** The addition is `R11.6 (add.)`, as `R11.9 (add.)` is (errata G13). Highest R11 is 94 (H); R11.95 and R11.96 are PR N's. No section needs a number from this PR.
- **Register.** `IMPLEMENTATION_PLAN.md:15` on both refs reads "First free: D123, D61 item 272, trap 245". **This PR takes D123, D61 item 272 and traps 245 to 247** (it merges first); PR N's plan shifts to D124, items 273 to 280 and traps 248 to 256 (its Amendment 1, ruling 37).
- **Probe prefix `SEQ`, falsification prefix `FSEQ`.** `git grep -E 'SEQ[0-9]|FSEQ'` is empty on both refs (the matches `seq`/`SEQ` elsewhere are lower-case or in the word "sequence").
- **EventId 7008** `EventsSeqWritable`. 7005 and 7006 are used; 7007 is written in PR N's plan as its own (`LifecycleRowsSkipped`). EventIds have no ordering rule; S takes 7008 so that N's plan stands.
- **Migration `db/0006_events_insert_columns.sql`.** The highest file is `0005_protect_agent_key_material.sql`; no other PR in flight adds a migration (PR N's contingent `0006` is withdrawn by its Amendment 1).
- **PR number** #153 (`gh pr list --state all --limit 4` on 2026-11-12: #152 merged, #151 open), confirmed where the PR is opened.

## What the reading and the scratch run found

1 to 5 are the defect (one cause, four forms); 6 to 10 are what a migration alone would have got wrong.

1. **The app role can name `seq`.** `db/0001` creates `seq BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY` and `GRANT INSERT, SELECT ON events` to the role [read]. A statement with `OVERRIDING SYSTEM VALUE` inserts at any `seq` the primary key allows, needing no privilege beyond `INSERT`; without it the statement is refused by the **identity rule** (`cannot insert a non-DEFAULT value into column "seq"`), which is not a grant [record; measured here: same message]. `server_ts` is a plain column the role also sets [record].
2. **Negative `seq`** (record): accepted; every read that touches the aggregate then throws in `EventSequence.From` (`Corrupt events.seq`, `PostgresEventStore.cs:541`) and answers a bare `500 curia/server/fault` to an uninvolved legitimate agent (the real victim's `curia budget` in the record), to the token request, and to `curia verify`; the signing side tolerates it (`sign-head` exits 0). A forward read does not return the row (`seq > 0`), so no scan counts it. **Whether the Forum starts with such a row in the log is not recorded** (the start-up scan reads forward and does not see it; M-S1 runs it).
3. **`seq` 0, a shadow row** [read; consistent with the record; M-S2 runs it through the Forum]. Every whole-log read starts at `EventSequence.Zero` and reads `WHERE seq > @after` (`EventReaderExtensions.ReadAllAsync`; `PostgresEventStore.cs:374`; `LifecycleEntryScan.cs:41`), so a row at `seq` 0 is returned by none of them: not the Acta (so no proof, no head commits to it), not any projection, not the 7006 scan, not `sign-head --if-grown`'s stop, not (after PR N) the audit. The by-aggregate read (`PostgresEventStore.cs:340`, no `seq` filter) returns it, and **that is the read the token path, ingest and the enrolment path use today** (N's finding 3). A forged `agent.key-retired` at `seq` 0 is therefore honoured by the token path and invisible to every detector. The record's own anomaly fits: after the `seq` 0 insert `curia verify` still printed the post at **leaf 4** and the binding at **log index 1** (a log that had shifted would print 5 and 2), though the report said every index shifted.
4. **A positive `seq` ahead of the identity** [measured]: with rows 1 and 2 present, a planted `seq` 5, and three honest appends, the appends took 3, 4 and then **failed** on `events_pkey` (`Key (seq)=(5) already exists`), the fourth took 6: **one honest append refused per planted value**, each failure consuming the identity's value, so the cost to the attacker is one `INSERT` per refused append. The Forum classifies a `PostgresException` as a store fault (`PostgresStoreFaults.Is`, by type) so the author is told `curia/store/unavailable` and the act is kept for `curia resend` [read; M-S3 runs it]. A row planted at a `seq` the identity has already passed is **not detectable by its `seq`** (it looks like an ordinary row); only a signed head that predates it (R6.47) can show it. The append guard (R6.60, `PostgresEventStore.cs:288-320`) and every paged read key on `seq > @after`, so a row placed under a reader's cursor is invisible to that reader and to the guard.
5. **All four forms are closed by one grant, and only by a grant.** A `CHECK (seq > 0)` closes the negative form only, and it is a table change the application's role cannot be held to by a test the way a grant can; a trigger would run code on the append path. The narrowest change is a **column-level `INSERT`** naming the six columns the Forum supplies, which PostgreSQL checks for any statement that lists `seq` [measured, all four statements on a scratch database after the migration]: `OVERRIDING SYSTEM VALUE` with `-6` refused `permission denied for table events` (42501); the same with a positive value refused the same way; `seq = DEFAULT` refused the same way; a normal append **with `RETURNING seq`** (the store's own shape, `PostgresEventStore.cs:441-466`) succeeds and returns the identity's value, because `SELECT` stays table-level; `UPDATE` and `DELETE` stay refused. **Why not only the negative form** (the owner asked): the other three forms are one cause and the closure costs the same; a rule that closed one and left the shadow row would leave the form that no detector sees.
6. **`REVOKE INSERT ON events` is load-bearing** [measured; reasoned from PostgreSQL's ACL model]: a column `GRANT` is additive to a table-level one, so a migration that only granted columns would leave `seq` writable. The migration is two statements and both are the point (FSEQ10).
7. **Anything that reads the grant with a table-level predicate reads false after the migration** [measured: `has_table_privilege(role, 'public.events', 'INSERT')` is **`f`**, `has_any_column_privilege` `t`, `has_column_privilege(..., 'seq', ...)` `f`, `server_ts` `t`]. Three sites do [read]: `tools/fault-harness/fault.sh` (`priv()` at `:12`, `privs events` against `ttff` in `status`, and the `all-off` summary line), `tests/Curia.Api.Tests/RoleForumFixture.cs:97-98` (`RestorationProblemsAsync`), and, through the script, `tests/Curia.Infrastructure.Tests/HarnessRehearsalTests.cs` (it asserts `status` exits 0 at rest). After the migration `fault.sh status` would read `tfff`, report FAULT OPEN at rest, and exit 1; `RoleForumFixture` would report "INSERT on events is not granted to the role" at rest (StoreFaultRoleTests).
8. **Anything that restores the grant at table level re-opens the hole, and says it restored the original** [measured: after the migration, `REVOKE INSERT` then `GRANT INSERT ON events TO role` (fault.sh's `insert-on`/`insert-off`) leaves the role able to insert at `seq` -9]. `fault.sh` restores with `GRANT INSERT ON events` at `:40` (`insert-off`) and `:64` (`all-off`), and its `status` line says `original: events SELECT+INSERT`. The harness is the tool every live run of this project's stages uses; a harness that heals a faulted database into a different one than the migration made is the failure this project's traps name.
9. **There is a second, hand-typed copy of the grant** (`PostgresDatabaseFixture.CreateIsolatedSchemaAsync`, `:205-224`: the `events` DDL and `GRANT INSERT, SELECT ON {schema}.events`), which the event-store contract tests and `PostgresAppendGuardTests` use (`CreateStore` calls it per store). Its remarks say why it is a copy (the 0001 template also issues `CREATE ROLE`). A migration alone leaves those tests running against the old grant: **green against a schema no deployment has**. `CreateIsolatedOperationalSchemaAsync` already shows the cure for 0002 and 0005: render the real migration under a `search_path` (`:262-275`).
10. **The probe the plan the architect first thought of cannot fail.** PR N's ruling 21 asked for an Infrastructure probe that an `INSERT` naming `seq` "with and without `OVERRIDING SYSTEM VALUE`" is refused. The without form is refused by the identity rule before and after the migration [measured], so it is a probe that passes with or without the grant (vacuity question 1). The discriminating forms are `OVERRIDING SYSTEM VALUE` (negative, zero, positive) and `seq = DEFAULT`.

## The design in one paragraph

`db/0006_events_insert_columns.sql` revokes the application role's table-level `INSERT` on `events` and grants `INSERT` on `event_id, event_type, aggregate_id, actor_id, payload, server_ts`, so an event's position is assigned by the database and no statement of the role can name it. `SchemaMigrations.FileNames` lists it (a test already fails if it does not). The hand-typed isolated-schema fixture is changed to render the migration under its `search_path` instead of typing a grant. The fault harness restores and reads the column grant (a strict predicate: the six columns granted and `seq` not), `RoleForumFixture`'s catalogue check is made the same predicate, and the harness's rehearsal test runs a restore and asserts the hole stays shut. A small start-up service logs `EventsSeqWritable` (EventId 7008, Error) when the role the Forum connects as can still name `seq` (a database that has not applied the migration, a superuser connection, or a restore from a backup that predates it) and starts either way, as the lifecycle scan does. The owner applies the migration to the hosted database with a pre-check, a one-transaction apply and a post-check that the README carries and a test runs. `R11.6 (add.)` states the property and the errata's G69 carries the evidence.

## Scope (exact file set; Task 0 M-S4 re-reads it, and a file added to the list is recorded in "As run")

**New**
- `db/0006_events_insert_columns.sql`.
- `src/Curia.Application/Ports/IEventsPositionGrant.cs` (one method: does the connected role hold `INSERT` on `seq`; returns `Result<bool>`, no string parameter, so `PortStringFenceTests` is unaffected); `src/Curia.Infrastructure/PostgresEventsPositionGrant.cs` (the adapter: `has_column_privilege(current_user, <the store's qualified table>, 'seq', 'INSERT')`); an in-memory adapter in `tests/` beside the other in-memory adapters (R11.4); `src/Curia.Api/Adapters/EventsSeqWritableCheck.cs` (the hosted service) and its registration in `src/Curia.Api/Program.cs`.
- Tests: `tests/Curia.Infrastructure.Tests/EventsPositionGrantTests.cs` (SEQ1 to SEQ4, SEQ8, SEQ10); `tests/Curia.Api.Tests/EventsPositionApiTests.cs` (SEQ7, SEQ9: through the Forum, as the app role); additions to `HarnessRehearsalTests.cs` (SEQ5) and `StoreFaultRoleTests.cs`/`RoleForumFixture.cs` (SEQ6).

**Edited**
- `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs` (the const and the list entry).
- `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs` (`CreateIsolatedSchemaAsync` renders the migration under its `search_path`).
- `tests/Curia.Api.Tests/RoleForumFixture.cs` (`RestorationProblemsAsync`'s INSERT predicate).
- `tools/fault-harness/fault.sh` and `tools/fault-harness/README.md`.
- Docs: the errata (G69), `IMPLEMENTATION_PLAN.md`, `README.md` (the runbook step and the pre/post-check block; the "Apply `db/*.sql` in order" sentence), `CLAUDE.md` (the status clause and the Build-and-test note), `conformance/README.md` not touched.

**Not edited** (asserted by `git diff --stat origin/main`): `.github/`, `Directory.Packages.props`, every `packages.lock.json`, every csproj, `Curia.sln`, `src/Curia.Infrastructure/PostgresEventStore.cs` (its INSERT already names exactly the six columns), `db/0001` to `db/0005` (forward-only: a later change is a new numbered file), the white paper (Appendix D shows only the `REVOKE UPDATE, DELETE` line; the role's grants are Stage 2 additions per `db/0001`'s header, so nothing in it contradicts the column grant) and the scoping document.

## What consumes what this PR changes (trap 171: answered by grep, re-grepped by Task 0 M-S4)

Computed on `origin/main` `b348f6a9` [read]:
- **Statements that INSERT into `events` (all name the six columns, none names `seq`):** `PostgresEventStore.cs:441`; tests `AgentWriteGateSupport.cs:224`, `BudgetReadoutEndpointTests.cs:654,661`, `BudgetReadoutSupport.cs:224,252`, `HeadsSignedUnattendedTests.cs:137`, `IndexRepairDeepGapEndpointTests.cs:35`, `LateCommitEndToEndTests.cs:932`, `RoleForumFixture.cs:64`, `AppRoleGrantRefusalTests.cs:71`, `ForksBodyDuplicateTests.cs:110`. None is edited; all keep working under the column grant [measured for the shape].
- **Statements that grant or revoke on `events`:** `db/0001:47-48` (the one this PR narrows); `PostgresDatabaseFixture.cs:218-219` (the copy); `fault.sh:39-42,64`; `RoleForumFixture.cs:54,56` (`SELECT` only, unaffected); `StoreFaultInfrastructureTests.cs:112,119` (`SELECT` only); `HeadsSignedUnattendedTests.cs:764` (`SELECT` only, a role that must not insert).
- **Predicates over the grant:** `fault.sh:12-13,25-26` (`has_table_privilege`), `RoleForumFixture.cs:97-98`. No other site (`git grep has_table_privilege|has_column_privilege|has_any_column` over `src`, `tests`, `tools`).
- **Fixtures that apply the migration list:** `SchemaMigrations.RenderAll` is called by `PostgresDatabaseFixture.cs:96`, `ForumFixture.cs:435`, `ForksBodyDuplicateTests.cs:105`, `HarnessRehearsalTests.cs:77`; adding the file to `FileNames` is the whole change for all four. `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder` is what fails if the list entry is forgotten.
- **Read-model rebuilds (R11.9):** `Projections/AggregateSummaryProjectionRebuildDrillTests.cs` appends through the store and replays; it names no `seq` and holds no grant [read]. It is run, not assumed (M-S7).

## Rulings (decided, not asked)

1. **What PR S is, and is not.** It closes one cause (the application role can choose an event's position) in one place (the grant), and makes the closure checkable at every start. It is **not** the cure for `server_ts` (the Forum supplies it explicitly, CS-9, so the grant must allow it: PR N's item 274), for a role that can write `seq` by being a superuser or the table's owner (a person holding that credential can rewrite anything and only a signed head detects it), for rows already planted (append-only: nothing here deletes), or for the forged-lifecycle-row class (PR N).
2. **The grant is the closure, column-level, in a new numbered migration.** `db/0006_events_insert_columns.sql`, forward-only, with `__CURIA_APP_ROLE__` as every migration since 0001 and **every name unqualified on purpose** (so a fixture renders it under a `search_path`, as 0002 and 0005 are). The body is the two statements of finding 6, in this order, with a header that says why each exists, why `server_ts` stays, why `SELECT` stays table-level (the store's `RETURNING seq`), and that **a later migration that adds a column the Forum supplies must grant `INSERT` on that column in the same file**: a missed grant fails loudly (`permission denied for table events` on the first append) and not silently, which is the right direction for a grant. The exact text is in Task 2.
3. **Only a refusal that the grant makes counts as evidence.** SEQ1's forms are `OVERRIDING SYSTEM VALUE` with a negative, a zero and a positive value, and `seq = DEFAULT`, each asserted `42501` **and** `permission denied for table events` (the shape of `AppRoleGrantRefusalTests`); the identity-rule form is in the file as a named control **whose comment says it passes with or without the grant** and is not counted as a probe of it (finding 10). The positive control (a normal append returns the identity's value through `RETURNING`) is in the same test, so the refusals cannot be the refusals of a role that lost all access (the shape of `AppRoleCanInsertAndSelectEvents`).
4. **The expected set of columns is parsed from the errata, not typed.** SEQ2 reads the six backticked column names out of `R11.6 (add.)`'s paragraph in the errata and compares them to `information_schema.column_privileges` for the fixture's role on `events` (INSERT exactly that set; `seq` absent; SELECT at table level; no UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER). The errata's prose and the PostgreSQL catalogue are different artifacts from the migration text, so this is a check and not a restatement (vacuity question 4).
5. **The isolated-schema fixture renders the migration; it does not copy the grant.** `CreateIsolatedSchemaAsync` keeps its hand-typed `CREATE TABLE` (the template creates the role) and replaces its hand-typed `GRANT INSERT, SELECT` with `GRANT SELECT` plus the rendered `db/0006` under `SET search_path`, followed by the existing `REVOKE UPDATE, DELETE`, as `CreateIsolatedOperationalSchemaAsync` renders 0002 and 0005. SEQ4 compares the isolated schema's catalogue grants with `public`'s, both read from `information_schema`, so a copy that drifts fails.
6. **The harness restores what the migration made and reads it strictly.** `fault.sh`'s three restore sites (`insert-off`, `all-off`, and the `status` summary text) use the column grant; its `status` decides "events is as it was" by: table-level `SELECT` true, `UPDATE` and `DELETE` false, `INSERT` true on each of the six columns, and **`INSERT` on `seq` false**. A harness `status` that said "original" while `seq` was writable would be the vacuous green this project's traps list. The exact patch is in Task 2.
7. **A start-up log, not a refusal.** The Forum logs `EventsSeqWritable` (EventId 7008, Error) at every start when the role it connects as holds `INSERT` on `seq`, and starts. A refusal would break a developer's superuser connection and every throwaway database that has not applied the migration, and the project is in alpha; the log is the same class as 7006 (a fact the operator has to hear, `LifecycleEntryScan`'s own remarks) and PR O's readiness check is told to alarm on it (item 272). It names the **fact** and a remedy (`db/0006` is not applied to this database, or this role is a superuser or inherits a table-level `INSERT`) and never a connection string or a role name the log did not already carry. **Severable:** if the owner wants this PR smaller, Task 3 moves to PR O's readiness script as a `psql` query and `R11.6 (add.)`'s last-but-one sentence becomes SHOULD; Task 0 estimates it at one port, one adapter, one hosted service and one probe, about 120 lines.
8. **The owner's application is a runbook, probed.** A migration "in the repo" is not a grant in the hosted database; the project's failure mode is a rule implemented and guarding nothing. The README carries (a) a pre-check, (b) the one-transaction apply, (c) the post-check, each as text a person pastes, and **SEQ8 runs the pre-check and post-check text parsed out of the README** against a database with planted rows (a zero, a negative and one ahead of the identity) and against a clean one, so the text a person will paste is the text a test ran. The pre-check **only reads**; if it finds a planted row the owner stops: nothing deletes an event (R11.6), a head that covers the row cannot be taken back, and the row is a finding for the architect.
9. **Nothing here changes what the Forum appends.** `PostgresEventStore.cs` is not edited; its INSERT already names the six columns. A change to it would be a probe that the grant and the store agree by construction, not by test.
10. **The replay drill is run, not assumed (R11.9).** M-S7 runs `AggregateSummaryProjectionRebuildDrillTests` and the Infrastructure suite before and after; Task 4's whole run does it again.
11. **Alpha.** The beta order's "no owner merge" holds for this PR's merge (no `ci.yml`); the owner's act is the apply (ruling 8) and it is a precondition of the hosted announcement (the beta order's J5). Nothing about a retained database's migration path is weighed against it: the hosted database holds no post anyone depends on, and the pre-check finds out.

## The errata entry (G69), text to paste at Task 1 (status "specified; built" is written by Task 6 from the run)

Task 1 pastes this at the end of the errata, after G68's slot is left as PR N's (or after G67 if N has not merged), with the index row for `R11.6 (add.)` and the `(add.)` mark in the Revisions table the way G13 did for R11.9; SEQ2 parses its column list.

### G69 — The log assigns its own positions: the application role can no longer name an event's `seq`, so no row can be placed where a forward read does not look, made to fail every read of its aggregate, or planted under the identity's next value

**Location.** §11.2, R11.6 (annotated by R11.6 (add.)); R6.47 (a leaf index is a position in the whole log), R6.60 (the append guard keys on `seq > cursor`); Appendix D `events` (unchanged); register D123, D61 item 272; PR N's plan (its Task 0 M4, its finding 10 and ruling 21, superseded).

**How it surfaced.** PR N's Task 0 (2026-11-11, M4) inserted rows into `events` as the application role: `OVERRIDING SYSTEM VALUE` at `seq` 0, -2 and -3 was accepted, a negative `seq` answered `500 curia/server/fault` to every read of the aggregate, including a legitimate agent's, and `server_ts` was stored verbatim. The report called a non-negative `seq` "benign"; the same record's `curia verify` still printed the post at leaf 4 after the insert. Reading why (every whole-log read is `WHERE seq > 0`; the by-aggregate read has no filter) turned "benign" into the form no detector sees. The architect's scratch run measured the third form (a `seq` ahead of the identity makes an honest append fail) and the shape of the closure (a column grant).

**Class.** A protection that does not cover the column that defines the order. The grant R11.6 asks for (`INSERT` and `SELECT`, never `UPDATE` or `DELETE`) was applied to the whole table, so it protected every row from rewriting and no row from being placed.

### The finding

1. **The role names `seq`.** An `INSERT` with `OVERRIDING SYSTEM VALUE` is accepted at any value the key allows; the same without it is refused by the identity rule, not by a grant.
2. **Negative `seq`:** accepted, invisible to forward reads, fatal to every by-aggregate read of the aggregate (an unhandled `500`).
3. **`seq` 0:** accepted and invisible to every forward read (the Acta, every projection, the start-up scan, the head signer's stop) and returned by the by-aggregate read, so a row the token path honours and no head can commit to.
4. **A `seq` ahead of the identity:** accepted, and one honest append later fails on the primary key for each planted value.
5. **The remedy is a column grant** for the six columns the Forum supplies; `server_ts` stays in it because the Forum supplies it (CS-9).

### Decisions this entry makes

1. The application role's `INSERT` on `events` is a column grant that excludes `seq`.
2. The migration revokes the table-level `INSERT` first (a column grant is additive).
3. The Forum logs at every start when its role can still name `seq`, and starts.
4. The owner applies the migration to a database that already holds rows, after a read-only check that no row sits at or below zero or ahead of the identity.

### Requirement text

**R11.6 (add.)** The `INSERT` that R11.6 grants on `events` SHALL be a grant on the columns the Forum supplies, `event_id`, `event_type`, `aggregate_id`, `actor_id`, `payload` and `server_ts`, and SHALL NOT include `seq`: an event's position in the log is assigned by the database, in the order its transactions take the append lock, and no statement of the application's role may name it, with or without `OVERRIDING SYSTEM VALUE` and as `DEFAULT`. A migration that adds a column the Forum supplies SHALL grant `INSERT` on that column in the same file. The Forum SHALL log at Error level, at every start, when the role it connects as can name `seq`, and SHALL start either way. The reason: a position is a leaf index (R6.47), and every paged read and the append guard (R6.60) are keyed on `seq > cursor`, so a row the role places at a position of its own choosing is not a row the Forum appended; at `seq` 0 or below it is invisible to every forward read while the by-aggregate read returns it (a forged row no head, proof, scan or stop can see), below zero it makes every read of its aggregate fail with a `500`, and above the identity's next value it makes an honest append fail on the primary key. R11.6's own reason is that append-only is held by the grant and not by the code's intentions; this is the same sentence for the column that orders the log.

### Revisions this entry carries

| where | change |
|---|---|
| R11.6 | `(add.)` Annotated: the `INSERT` is a column grant that excludes `seq` (R11.6 (add.)); `UPDATE` and `DELETE` stay refused. |
| PR N's finding 10, ruling 21, non-property (c) | Superseded by this entry (PR N's Amendment 1, rulings 26 and 31). |

### Non-properties

- **(a) `server_ts` is still the credential's.** The grant cannot close it: the Forum supplies it (CS-9). Tenure (T1's 48 hours) and every instant in the log remain forgeable (PR N's item 274).
- **(b) A role that can write `seq` by being a superuser, the owner of the table, or a member of a role that holds a table-level `INSERT` is outside this entry.** The Forum says so at every start (EventId 7008). Only a signed head over the log's past lets a client detect a log rewritten by such a role.
- **(c) A row already planted is not removed.** Append-only is the point of R11.6; the owner's pre-check finds one and the architect decides, and a row at a `seq` the identity has already passed is not detectable by its `seq`.
- **(d) A restore from a backup that predates the migration reverts the grant.** The start-up log says so; nothing else does.
- **(e) The start-up check is a log, not a refusal** (ruling 7), and it reads the Forum's own connection: a different credential (the head signer's copy, PR N's item 257) is not checked by it.

**Status:** proposed.

## Tasks

### Task 0: Re-read, operate the unmodified tree, write the rules first

**The rules below are written before any measurement and are not edited after one.** A result that contradicts a rule is recorded in "As run" as a finding and goes to the architect as an amendment; it is not absorbed. Every run is on a throwaway database and role (`curia_s_*`, dropped after, never mid-run), Production mode, from an **export** of `b348f6a9` made with `git archive` into an empty scratch directory, **never the GitButler workspace** (it carries #151 applied). Task 0 writes no repository file; its report is `task-0-report.md` in `.superpowers/sdd/2026-11-12-planS/`. The architect's scratch measurements in this plan are the control, not the oracle.

- **M-S1, the negative `seq` through the Forum, and the restart.** On the export, as the app role: insert one `agent.key-retired`-shaped row at `seq` -3 under a victim's aggregate (as the record's M4 did). Record: the victim's token request, `curia budget`, `curia verify` of an older post (statuses and words), an uninvolved agent's `curia ask`; then **restart the Forum**: does it start; what it logs; whether `LifecycleEntryScan` or any other hosted service stops it. *Prediction:* the record's three 500s hold; an uninvolved agent whose aggregate is not touched is unaffected; **the restart is unknown** (the scan reads forward, `seq > 0`, and should not see the row). *Rule:* a Forum that does not start is a finding that raises this PR's urgency and goes in "As run" and the PR body; a Forum that starts is recorded as such.
- **M-S2, the shadow row.** As the app role: insert a forged `agent.key-retired` for a victim's live `kid` (garbage `authorization`, the probe's shape) at `seq` 0. Record: the victim's token request and an issued token's next request; `GET /v1/log/jwks?agent=` (its `lifecycle` list); `GET /v1/log/head` and an inclusion proof for the victim's post; `curia keys` (the FOREIGN line); `curia-operator sign-head --if-grown` (does it see growth, does it stop); a Forum restart and its 7006 count; `curia verify`. *Prediction (read, not run):* the token request is `401 invalid_client`; **none of the log routes, the keys line, the stop or 7006 reflects the row.** *Rule:* if the token request is `200`, the by-aggregate read does not return the row and finding 3 is wrong: this PR is still needed for the 500s and the collision, its urgency text changes, and the architect is told before Task 1 pastes the errata; if the token request is `401` and no detector sees the row, finding 3 is measured and the errata says so with the numbers.
- **M-S3, the ahead plant, through the Forum.** As the app role: plant three rows at the next three identity values (`last_value + 1`, `+2`, `+3`), then run five honest `curia ask` calls one after another. Record each status and exit, the words the agent sees, and whether `curia resend` of the refused act succeeds. *Prediction:* three of the five answer `curia/store/unavailable` (exit 8 or the store-fault exit), the fourth and fifth succeed; the architect's database run says the same of the identity. *Rule:* a status other than the store-fault one (a `500`) is a finding (the classifier does not cover a unique violation on `events_pkey` through this path).
- **M-S4, consumers.** `git grep` over `src`, `tests`, `tools`, `rust`, `docs`, `README.md` and `CLAUDE.md` for `INSERT INTO events`, `ON events`, `events (`, `has_table_privilege`, `has_column_privilege`, `has_any_column_privilege`, `GENERATED ALWAYS`, `OVERRIDING SYSTEM VALUE`, `SchemaMigrations.RenderAll`, `CreateIsolatedSchemaAsync`. *Rule:* this plan's "What consumes what" is the control; a site the plan lacks goes to "As run" with its fix and into the scope, and the Task 5 gate is written against the final list.
- **M-S5, the baseline gate.** `dotnet build Curia.sln -c Release`; `Curia.Infrastructure.Tests` and the Api classes `StoreFaultRoleTests`, `HeadsSignedUnattendedTests` and the closed fuzz; `HarnessRehearsalTests`; `check-spec.py`; `falsify-spec-checks.py`. Counts are the control for Task 4.
- **M-S6, the grant arithmetic, re-run.** Re-run the architect's `exp.sh` shape on the export's `RenderAll` output (all migrations, with pgvector) rather than `0001` alone: the four refused statements, the `RETURNING` append, the `UPDATE`/`DELETE` refusals, the re-apply, `has_table_privilege` false after. *Rule:* any difference from the architect's run is recorded; a statement the plan says is refused that is accepted stops the task.
- **M-S7, the replay drill.** Run `AggregateSummaryProjectionRebuildDrillTests` and the whole Infrastructure suite on the export (the control), and again at Task 4. *Rule:* a drill that fails after the migration is a finding, not noise.
- **M-S8, the estimate of Task 3.** Read `LifecycleEntryScan`, the `IHostedService` registrations in `Program.cs`, the log-capture fixture GF10's 7006 probes use, and `PortStringFenceTests`. *Rule:* if Task 3 is estimated over 200 lines or needs a new test assembly, stop and return to the architect with the severable alternative (ruling 7).

### Task 1: The errata and the probes (red on the unmodified tree)

**Order:** (1) G69 pasted as specified, with the index row and the `(add.)` mark, `check-spec.py` and `falsify-spec-checks.py` clean (SEQ2 parses it); (2) the probes below, each red on the tree without `db/0006`, its text recorded in "As run" before its fix. SEQ3, SEQ4 (the part that does not need the fixture change), SEQ9 and SEQ10 are controls on the tree (green by design) where the probes' own list says so.

### Task 2: The migration and the fixtures

**The migration** `db/0006_events_insert_columns.sql`:

```sql
-- Migration 0006, forward-only, applied after db/0005_protect_agent_key_material.sql: an event's position in the log is the
-- database's, never the application role's (errata G69; R11.6 (add.)).
--
-- __CURIA_APP_ROLE__ is the placeholder 0001 introduces, substituted by Curia.Infrastructure.Migrations.SchemaMigrations.Render*
-- before execution. Every name below is unqualified on purpose, as in 0002 and 0005: a test fixture renders this file under a
-- search_path to put it in an isolated schema beside that schema's own events table.
--
-- WHY A COLUMN GRANT. 0001 granted INSERT on the whole of events, so the application role could name seq with OVERRIDING SYSTEM
-- VALUE and place a row anywhere the primary key allows: below zero (every read of the row's aggregate then fails), at zero (the
-- row is invisible to every read that starts at seq > 0, which is every whole-log read, and visible to the by-aggregate read), or
-- ahead of the identity (an honest append later fails on the primary key). R11.6's reasoning says where a guarantee belongs: in
-- the grant, "not merely in the code's intentions". The role keeps INSERT on the six columns Curia.Infrastructure.PostgresEventStore
-- supplies, server_ts among them (the Forum stamps it explicitly through the Clock port, CS-9), and loses it on seq.
--
-- WHY THE REVOKE. A column GRANT is additive to a table-level one; without the REVOKE the role would still hold INSERT on seq.
-- SELECT stays table-level (the store's INSERT ... RETURNING reads seq), and UPDATE and DELETE stay revoked (0001).
--
-- A LATER MIGRATION that adds a column the Forum supplies must GRANT INSERT on that column in the same file; a missed grant fails
-- loudly (permission denied for table events on the first append), which is the right direction for a grant to fail in.
REVOKE INSERT ON events FROM __CURIA_APP_ROLE__;
GRANT INSERT (event_id, event_type, aggregate_id, actor_id, payload, server_ts) ON events TO __CURIA_APP_ROLE__;   -- R11.6 (add.): never seq
```

**`SchemaMigrations.cs`:** add `public const string EventInsertColumnsFile = "0006_events_insert_columns.sql";` with a `<summary>` ("db/0006: INSERT on `events` narrowed to the columns the Forum supplies; `seq` is the database's (R11.6 (add.))") and append it to `FileNames` after `AgentKeyMaterialFile`.

**`PostgresDatabaseFixture.CreateIsolatedSchemaAsync`** (`:205-224`): Old lines `GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole};` / `GRANT INSERT, SELECT ON {quotedSchema}.events TO {quotedRole};` / `REVOKE UPDATE, DELETE ON {quotedSchema}.events FROM {quotedRole};`. New:

```csharp
            GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole};
            GRANT SELECT ON {quotedSchema}.events TO {quotedRole};
            SET search_path TO {quotedSchema};
            {SchemaMigrations.Render(SchemaMigrations.EventInsertColumnsFile, _roleName)}
            RESET search_path;
            REVOKE UPDATE, DELETE ON {quotedSchema}.events FROM {quotedRole};
```

and the method's `<summary>` loses "INSERT/SELECT" and says the INSERT grant is the real migration's, rendered (finding 9), with `CA2100`'s justification extended to the rendered template as `CreateIsolatedOperationalSchemaAsync`'s is.

**`RoleForumFixture.RestorationProblemsAsync`** (`:93-114`): replace the `has_table_privilege(@role, 'events', 'INSERT')` select-list item and its reader line with two items, and the problems with two sentences:

```csharp
            SELECT has_table_privilege(@role, 'events', 'SELECT'),
                   (SELECT bool_and(has_column_privilege(@role, 'public.events', c, 'INSERT'))
                      FROM unnest(ARRAY['event_id','event_type','aggregate_id','actor_id','payload','server_ts']) AS c),
                   has_column_privilege(@role, 'public.events', 'seq', 'INSERT'),
                   (SELECT count(*) FROM pg_policies WHERE tablename = 'events'),
                   ...
        if (!reader.GetBoolean(1)) problems.Add("INSERT on the columns the Forum supplies is not granted to the role");
        if (reader.GetBoolean(2)) problems.Add("INSERT on events.seq is granted to the role");
```

(the later reader indices move up by one; Task 2 edits them and the existing class asserts them).

**`tools/fault-harness/fault.sh`** exact patch (Old / New; `README.md` of the harness names `insert-off` and `status` and is edited to match):

```text
Old (line 12):  priv(){ psql -d "$FH_DB" -Atc "select has_table_privilege('$FH_ROLE','public.$1','$2')"; }
New:            priv(){ psql -d "$FH_DB" -Atc "select has_table_privilege('$FH_ROLE','public.$1','$2')"; }
                evcols="event_id event_type aggregate_id actor_id payload server_ts"
                colpriv(){ psql -d "$FH_DB" -Atc "select has_column_privilege('$FH_ROLE','public.events','$1','INSERT')"; }
                # R11.6 (add.): INSERT on events is the six columns the Forum supplies and never seq. "ok" only if both halves hold.
                events_insert(){ local c; for c in $evcols; do [ "$(colpriv $c)" = "t" ] || { echo "no"; return; }; done; [ "$(colpriv seq)" = "f" ] && echo "ok" || echo "seq-writable"; }
Old (line 40):  insert-off) $PSQL -c "GRANT INSERT ON events TO $FH_ROLE"; log insert-off; mk c2-close; echo "insert-off" ;;
New:            insert-off) $PSQL -c "GRANT INSERT (event_id, event_type, aggregate_id, actor_id, payload, server_ts) ON events TO $FH_ROLE"; log insert-off; mk c2-close; echo "insert-off" ;;
Old (line 64):  ... -c "GRANT INSERT ON events TO $FH_ROLE" -c "GRANT SELECT ON events TO $FH_ROLE"
New:            ... -c "GRANT INSERT (event_id, event_type, aggregate_id, actor_id, payload, server_ts) ON events TO $FH_ROLE" -c "GRANT SELECT ON events TO $FH_ROLE"
Old (status):   [ "$(privs events)" = "ttff" ] || { open="$open events(log-or-insert)"; rc=1; }
New:            [ "$(priv events SELECT)$(priv events UPDATE)$(priv events DELETE)" = "tff" ] && [ "$(events_insert)" = "ok" ] || { open="$open events(log-or-insert:$(events_insert))"; rc=1; }
Old (line 32):  ... "original: events SELECT+INSERT, post_embeddings ..."
New:            ... "original: events SELECT + INSERT on the six columns (never seq), post_embeddings ..."
```

(The per-table print loop in `status` keeps printing the four table-level privileges for `post_embeddings`; for `events` it prints `INSERT=<events_insert>` in place of the table-level INSERT, so a table-level `f` is not read as a fault.) Task 2 records the exact lines it touched.

**Gate:** SEQ1 to SEQ6 green; the existing Infrastructure and Api suites green; `SchemaMigrationsTests` green.

### Task 3: The start-up detector (severable; ruling 7)

`IEventsPositionGrant` (Application port; `Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken)`; no string parameter; an in-memory adapter returns the value it was built with), `PostgresEventsPositionGrant` (Infrastructure: `SELECT has_column_privilege(current_user, @table, 'seq', 'INSERT')` with the store's own qualified table name, through the data source the event store uses, a fault classified by `PostgresStoreFaults` and returned as the port's error), `EventsSeqWritableCheck` (`IHostedService` in `Curia.Api/Adapters/`, registered once in `Program.cs`): on start it asks, logs EventId 7008 `EventsSeqWritable` at Error through a `[LoggerMessage]` when the answer is `true`, logs nothing when `false`, and **starts either way** (a port error is logged at Warning as "could not be checked" and also starts: a check that stops the Forum is the refusal ruling 7 declined). The message names the fact and the remedy and carries no connection string. CS-7 holds (Npgsql stays in Infrastructure); the port-string fence is unaffected. SEQ7 and SEQ9.

### Task 4: The whole run once, the gates, the review

The whole solution as CI runs it: `dotnet build Curia.sln -c Release`; the all-but-Api test step; the Debug Architecture step (D16: `Curia.Architecture.Tests` in both configurations; the port and hosted-service additions read IL); the Api suite once (`EventsPositionApiTests`, `StoreFaultRoleTests`, `HeadsSignedUnattendedTests` and the closed fuzz (R14.10); no route changes, so the fuzz's ledger stays empty); the Infrastructure suite including the replay drill (M-S7); the screening-cost step; `check-spec` and `falsify-spec-checks`; the locked restore; `preflight.py --api "<the new and changed Api classes>"`. **Review, three lenses, each told the premises above:** (1) spec compliance against G69 line by line; (2) **the harness and the fixtures** (does any other fixture, script or doc grant or read `events` INSERT at table level; does any probe in the repository pass because its fixture is table-level); (3) the words (the log message, the README's pre- and post-check, `fault.sh`'s status line) read cold.

### Task 5: Falsification FSEQ1 to FSEQ10

Each row is a patch to a **kept copy**, built `--no-incremental`, the named probes run, restored by **plain copy** (mtime bumped; memory: falsify restores must bump mtime; never `git checkout -- <path>`), and run once unpatched. **The first form is the harm.** A probe that fails in a fixture's constructor has not reached its assertion: the runner records it `masked`, and a row is red only when a named probe is red by its own assertion. The runner is H's (`fhsu-keep`'s method).

### Task 6: Operate it (the rehearsal), the cold read, the register and the docs

See "The live run and the cold read". Then "Docs owed" and "Register text to paste". G69's status is written from the run. **Task 6's first act is the control:** the same steps on the `b348f6a9` build.

### Task 7: PR

**Preflight first, every push** (`python3 tools/preflight/preflight.py --api "<classes>"`, about four minutes). `git diff --stat origin/main` shows: no `.github/`, no `Directory.Packages.props`, no `packages.lock.json`, no csproj, no `PostgresEventStore.cs`, and `db/0006_events_insert_columns.sql` as the only file under `db/`. Branch `events-positions-are-the-stores`, through the gitbutler skill; verify the branch's file list against the tree before opening (`git status --porcelain` empty after the commit). `gh pr list --state all --limit 4` for the number. CI watch with a bound (`gh pr checks` polled to a deadline, never `--watch` unbounded). Merge on green only, with the merge-time path check (the four required checks, strict). **The PR body says: this is a deployment step; the owner applies `db/0006` to the hosted database (README runbook) before the hosted instance is announced; nothing is applied by merging; the Forum logs EventId 7008 at every start until it is.** Docs current in the same branch.

## Probes and their red text (predicted, not run)

"Control" = green on the unmodified tree by design. Every refusal probe asserts the **SQLSTATE and the message**, a **positive control in the same test** and the **unchanged event count**. Every probe that parses a published text parses it **from the errata** or from the README, a different artifact from the code it checks.

- **SEQ1** *(Infrastructure, as the app role)* **the role cannot name `seq`.** Four forms each refused with `42501` and `permission denied for table events`: `OVERRIDING SYSTEM VALUE` at -6; at 0; at a positive value; and `seq = DEFAULT`. The same test appends normally and reads the assigned `seq` back through `RETURNING` (positive control), and `UPDATE`/`DELETE` stay refused. The **without-`OVERRIDING` form is in the file as a named control that carries its own comment: it is refused by the identity rule and passes with or without the grant.** Home `EventsPositionGrantTests.cs`. Red on the tree: `Assert.Throws() Failure: No exception was thrown` on the first form (the insert succeeds).
- **SEQ2** **the catalogue equals the errata.** The six column names parsed from R11.6 (add.)'s paragraph equal the columns for which the fixture's role holds `INSERT` on `events` per `information_schema.column_privileges`; `seq` is absent; table-level `SELECT`; no `UPDATE`, `DELETE`, `TRUNCATE`, `REFERENCES`, `TRIGGER`. Red before the errata is pasted (`no R11.6 (add.) paragraph in the errata`) and on the tree (the catalogue lists no column-level rows).
- **SEQ3** **the migration is idempotent and listed.** Applying `db/0006` twice to a database leaves the catalogue the same and raises nothing; `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder` is green only with the list entry (control). Red on the tree: the file does not exist.
- **SEQ4** **the isolated schema's grant is the migration's.** The isolated schema `CreateIsolatedSchemaAsync` returns has, per `information_schema`, the same column grants for the role as `public` has (SEQ2's set). Red on the tree with the fixture unchanged: `Expected: [event_id, ...six]  Actual: table-level INSERT (every column)`.
- **SEQ5** *(Infrastructure, `HarnessRehearsalTests`)* **the harness heals into what the migration made.** `fault.sh insert-on` then `insert-off`, then `status` exits 0, and **the role still cannot name `seq`** (an `OVERRIDING SYSTEM VALUE` insert is refused 42501); `all-off` the same; `status` at rest exits 0. Red on the tree with the script unchanged: `status at rest: Expected exit 0  Actual exit 1 (FAULT OPEN: events(log-or-insert))`, and, with `status` patched and the restores not, `the role named seq after insert-off`.
- **SEQ6** *(Api, `StoreFaultRoleTests`/`RoleForumFixture`)* **the catalogue check is strict.** `RestorationProblemsAsync` returns "" at rest on a migrated database; names "INSERT on the columns the Forum supplies is not granted" after `REVOKE INSERT ON events`; names "INSERT on events.seq is granted" after a table-level `GRANT INSERT ON events` (a red-by-design control that proves the predicate can fail the wrong way). Red on the tree: `Expected: ""  Actual: "INSERT on events is not granted to the role"` at rest.
- **SEQ7** *(Api, a Forum started with a log capture, GF10's pattern)* **the start-up log.** On a role holding table-level `INSERT` (the fixture's admin-provisioned variant) the Forum logs EventId 7008 once at Error, names the fact and the remedy, carries no connection string, and starts and serves; on the migrated role it logs nothing; a port error logs a Warning and starts. Red on the tree: `Expected: a log entry with EventId 7008  Actual: none`.
- **SEQ8** **the owner's text is run.** The pre-check and post-check SQL are parsed out of the README (between `<!-- events-position-precheck -->` and `<!-- /events-position-precheck -->`, and the post-check's pair) and run against a database holding a row at `seq` 0, one at -3 and one ahead of the identity: the pre-check reports them (counts 2 and 1), and against a clean one reports 0 and 0; after the apply the post-check says `seq` not writable, `server_ts` writable. Red before the README block exists: `no events-position-precheck block in README.md`; red under FSEQ9 (a `seq < 0` predicate).
- **SEQ9** *(Api, control)* **a forged row cannot be planted through the Forum's own role and the victim is unaffected.** With the app role, the insert at -3 is refused 42501; the victim's token request, `curia budget` and `curia verify` are as with no attempt. Red on the tree: `Expected: 42501  Actual: row inserted` and then the 500s of the record.
- **SEQ10** *(Infrastructure, control)* **the replay drill and the event-store contract suite pass on the migrated schema** (M-S7's drill and `PostgresEventStoreContractTests` run on isolated schemas that now carry the column grant). Green on the tree by design; it is the control that the fixture change broke nothing.

## Falsification rows (the first form is the harm)

| # | Patch | Must go red | First form (the harm) |
|---|---|---|---|
| FSEQ1 | `db/0006` reduced to its header comment (no statements) | SEQ1, SEQ2, SEQ4, SEQ9 | a negative `seq` is planted by the app role and every read of the victim's aggregate answers `500` |
| FSEQ2 | the `GRANT` column list loses `server_ts` | SEQ2 (errata set differs); every append in the suites | every append by the Forum is refused `permission denied for table events` |
| FSEQ3 | the `GRANT` column list gains `seq` | SEQ1, SEQ2 | the role names `seq` again: a shadow row no detector sees |
| FSEQ4 | `0006` removed from `SchemaMigrations.FileNames` (file left on disk) | `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder`; SEQ1 in every fixture | the repository holds a migration no database gets, and the suites pass against a grant no deployment has |
| FSEQ5 | `CreateIsolatedSchemaAsync` keeps its hand-typed `GRANT INSERT, SELECT` after the rendered migration | SEQ4 | the event-store contract tests run against a table-level grant |
| FSEQ6 | `fault.sh insert-off` restores `GRANT INSERT ON events` (table-level) | SEQ5 | the harness heals a faulted database into one where the role can name `seq`, and says "original" |
| FSEQ7 | `RoleForumFixture.RestorationProblemsAsync` reverted to `has_table_privilege(..., 'INSERT')` | SEQ6; `StoreFaultRoleTests` at rest | the fixture reports a migrated database as broken (the wrong direction), or a table-level one as fine |
| FSEQ8 | `EventsSeqWritableCheck` removed from `Program.cs`, and (second patch) its condition inverted | SEQ7 | the Forum is silent over a database that has not applied the migration, or noisy over one that has |
| FSEQ9 | the README pre-check's predicate `seq < 1` becomes `seq < 0` | SEQ8 | the owner's pre-check misses a row at zero, the shadow row |
| FSEQ10 | `REVOKE INSERT ON events FROM __CURIA_APP_ROLE__;` deleted from `db/0006` (only the column `GRANT` stays) | SEQ1 (the `seq` forms accepted), SEQ2 | the migration reads as done and the role still names `seq` (a column grant is additive) |

Counts to record: rows run; first forms red (all); controls (none: every row is expected red); the masked.

## The live run and the cold read

Throwaway Postgres (`curia_s_*`, dropped after, never mid-run); the built Forum, `curia`, `curia-operator`; hostname, user and paths appear in no record. **Every leg runs first on the `b348f6a9` build and the control's result is recorded before the leg is run on S's tip.**

- **LS1 (the three forms are refused through the Forum, and the victim is unaffected).** The M-S1 to M-S3 plants as the app role. *Control (`b348f6a9`):* the records' 500s, the shadow row's `401`, the refused honest appends. *Rule on S:* each plant is refused `42501`; the victim's token, budget and verify are as with no attempt; five honest asks succeed.
- **LS2 (the owner's apply, rehearsed on a database that holds rows).** A database built with `db/0001` to `db/0005` only (the shape the hosted database is in), a Forum on it (it logs 7008 at start: recorded), 30 events, three agents. Run the README's **pre-check** (expect zero planted rows), then the **apply** as the table owner while a loop of `curia ask` runs once a second from another process (**no ask fails across the apply**, recorded), then the **post-check**; the event count, the maximum `seq` and a SHA-256 over the ordered `event_id` list are **unchanged** by the apply. Restart the Forum: **no 7008**. Then simulate a restore: `GRANT INSERT ON events TO <role>`, restart: **7008 appears** (ruling 7, non-property (d)).
- **LS3 (the pre-check finds a plant).** On a copy of LS2's database, as the owner, plant one row at `seq` 0 and one ahead of the identity: the README's pre-check reports both with the counts; the runbook says stop and do not delete; **nothing in the tree deletes**.
- **LS4 (the harness).** The fault harness's `insert-on`, `insert-off`, `all-off`, `status` on S's tip (`HarnessRehearsalTests` is this leg's test; the live run does it once by hand): `status` exits 0 at rest and after each restore, and the role still cannot name `seq`.

**The cold read (words, scaled to the words).** The words this PR adds that a person reads: the README's apply block (pre-check, apply, post-check, what to do on a finding), the 7008 log message, and `fault.sh status`'s new line. **Three fresh Sonnet 5.5 readers** for the README block, each in an empty directory with the block and a throwaway database's credentials, none told what is being tested: each is asked to apply the migration and say how they know it worked. **Gate (named now):** each of the three runs the pre-check before the apply, applies in one transaction as the owner (not as the app role), runs the post-check, and says `seq` is no longer writable and the event count is unchanged; none deletes a row on a finding. Two fresh readers for the 7008 line: each says that the Forum's role can still name `seq` and what to do, and neither reads it as the Forum being down or the log being corrupt. A reader below the gate is a finding that returns to the architect before the docs.

## Traps applied (the point of each, in this PR) and new traps proposed

- **171** (a premise a plan's "untouched" never read): the consumers section; two table-level predicates and a table-level restore were found by grep after the first reading said "a migration and a list entry".
- **Does a missing row fail, or masquerade as a deliberate deny? (question 3):** a migration that omits a column the Forum supplies fails every append loudly; a harness `status` that reads a column grant as a fault fails the rehearsal test (SEQ5), and one that reads a table-level grant as "original" is the row FSEQ6 shows it can be.
- **A passing probe and an absent probe indistinguishable (question 1):** the without-`OVERRIDING` form passes with or without the grant and is named a control in the file (finding 10); every other form is red on the tree.
- **Does the check derive from the artifact it checks? (question 4):** SEQ2 parses the errata and reads the PostgreSQL catalogue; SEQ8 runs the README's text; SEQ4 compares two catalogue reads.
- **245 (new): a grant narrowed from the table to a column is restored, read and copied at table level by every tool that handles it.** Findings 7 to 9: `fault.sh`'s restore re-opens the hole and says "original"; `has_table_privilege` reads false after a column grant; the isolated-schema fixture is a hand-typed copy. SEQ4 to SEQ6; FSEQ5 to FSEQ7. *Any change to a privilege's granularity is a sweep for every site that grants it, reads it or re-grants it.*
- **246 (new): a probe of a refusal must use the form only the grant refuses.** Finding 10: the identity rule refuses a non-`DEFAULT` `seq` first, before and after the migration. SEQ1; FSEQ1. *A refusal probe is red on the tree or it is a control and says so.*
- **247 (new): two access paths to one log disagree about which rows exist.** Finding 3: the by-aggregate read returns a row at `seq` 0 that every forward read cannot, so a detective control on the second is blind to the first. SEQ9; LS1; PR N's ruling 36 (every decision reads the whole log). *A detector is only as wide as the read it uses; list the reads before claiming a control covers the log.*

## Gates

Task 4's whole run as CI runs it; the closed fuzz (R14.10); preflight (green before every push); `check-spec` and `falsify-spec-checks`; the Debug **and** Release architecture rules (D16: the port and hosted-service additions); the Infrastructure suite and the replay drill; the falsification table (FSEQ1 to FSEQ10, all red); the live run on the control and on the tip; the cold read; and **the absence list** (Task 7). M-S2 and M-S3 are gates in the sense that a result against a prediction stops the task that depends on it.

## The seam with the PRs after, and the neighbours

- **PR N (the next):** its plan's Amendment 1 (ruling 31) re-bases it on this PR's merge before its Task 4. Its probe LRV31 plants a shadow row **as the admin connection**, because after this PR the app role cannot. Its non-property (c) is this PR's claim ("on a database that has applied `db/0006`").
- **PR N2 (operator acts):** unaffected; it shares `Program.cs` only if it registers a service.
- **PR O (a deployment pack):** its readiness check alarms on 7008 in the Forum's log and, severable-Task-3 aside, runs the post-check query; its restore runbook says a restore from a backup that predates `db/0006` re-opens the hole and the start-up log is the detector (non-property (d)). Its README Beta paragraph takes this PR's sentence.
- **#151, D10b, PR 8, PR 9:** unaffected; they share only the three documents and `Program.cs`.
- **The hosted announcement (the beta order's J5):** a precondition is "the owner has applied `db/0006` to the hosted database and the post-check passed".

## Docs owed before the PR

- `curia-whitepaper-ERRATA-AND-ADDENDUM.md`: G69 with its status from the run (probe and falsification counts), the index row for `R11.6 (add.)`, the `(add.)` mark. **The white paper is not edited.**
- `IMPLEMENTATION_PLAN.md`: "Current work", "Next stage" (PR N next, then N2, PR O, the join; PR S built), the PR table, the Beta section (item 9's preconditions: the apply), the register (below), the first-free line (**"First free: D124, D61 item 273, trap 248; errata G68 is PR N's, G70 PR N2's (provisional)"**), traps 245 to 247.
- `README.md`: the operator runbook's new step and its pre-check/apply/post-check block (marked for SEQ8), the sentence "Apply `db/*.sql` in order first" gains "and `db/0006` narrows the role's `INSERT`", the Beta notes' one sentence (a database that has not applied it can be reordered by its credential).
- `CLAUDE.md`: the status paragraph (a clause: the application role names no `seq`, errata G69), and the Build-and-test note that the fixtures render `db/0006`.
- `tools/fault-harness/README.md`: `insert-off` and `status` restore and read the column grant.
- `tools/preflight/README.md` only if preflight's step list changed (it should not).

## Register text to paste (Task 6 writes the ones the run confirms)

**D123.** `The application role can name an event's seq (db/0001 granted INSERT on every column). Fixed by errata G69 (PR S, #153): db/0006 grants INSERT on the six columns the Forum supplies and not seq; the Forum logs EventId 7008 at every start when its role can still name it. The measured numbers stay as the record: a negative seq (PR N's Task 0 M4: a 500 on every read of the aggregate, an uninvolved agent's included); a seq of 0 (read: invisible to every forward read, returned by the by-aggregate read: M-S2); a seq ahead of the identity (measured at the database: one honest append refused per planted value: M-S3). What it does not close: server_ts (PR N's item 274), a role that can write seq by being a superuser or the table's owner, rows already planted, and a restore from a backup that predates the migration.` Status `fixed`, with the legs that show it, when the owner has applied it to the hosted database (the PR's status line says "built, not applied").

**D61 item 272.** `The Forum's start-up check of the events grant is a log (EventId 7008), not a refusal: a database that has not applied db/0006, a superuser connection and a restore from an older backup all start and say so once. PR O's readiness check alarms on it; whether the Forum should refuse to start is the owner's call after the drill.`

**Traps 245 to 247:** the three above, each in the plan's one-sentence form.

## Next stage

PR N (lifecycle rows are honoured only if they verify; errata G68; re-based onto this PR's merge before its Task 4), then PR N2 (operator acts signed by a key the Forum never holds; errata G70; blocks the announcement), then PR O, then the hosting join: the order lines are in the architect's report beside this plan.

## Open risks (named so the reviewer can look)

- **Task 3 is the part that is not obviously "small".** It is severable (ruling 7). Its estimate is Task 0 M-S8's.
- **The forward-read blindness is read, not run** (finding 3). If M-S2 contradicts it, the PR is still right (the 500s and the collision are measured) and its urgency text is rewritten.
- **A role that is not the application's** (the head signer's copy of the credential, PR N's item 257) is outside the start-up check and outside this grant: it can hold whatever grants its host gave it.
- **`has_column_privilege(current_user, ...)` follows role inheritance and superuser**: a Forum connected as a superuser logs 7008 on every start. That is the intended signal (ruling 7), and a developer's local superuser connection sees it; the log says why.
- **The hosted database may already hold a planted row.** The pre-check finds it and the owner stops (ruling 8); the PR cannot remove one (R11.6).

## What this plan did not read or verify

- **No Forum was started, no test was run, and no build was made.** The scratch database and role were created and dropped on the local server (three scripts); everything marked **[measured]** is that and nothing else.
- **Not read:** `ForumFixture.cs` beyond the line that calls `RenderAll`; `StoreFaultRoleTests.cs` and `HarnessRehearsalTests.cs` bodies (named as homes; their pins of `status` read from their own headers and from grep); `AggregateSummaryProjectionRebuildDrillTests.cs` beyond its header and a grep (it names no `seq`); `LifecycleGuardTests.cs`/GF10's log-capture fixture (M-S8 reads it); `PortStringFenceTests`; `tools/fault-harness/common.sh`; the other fault-harness scripts beyond a grep; `db/0003` and `db/0004` (a grep finds no identity column outside `events`).
- **From PR N's record, not re-run:** every Forum-level 500, the `server_ts` storage, the `seq` -3 and -2 plants, `curia verify`'s leaf numbers after the `seq` 0 insert.
- **Not checked:** whether any deployment script outside the repository restores `events` grants; whether `pg_restore` of a custom-format dump preserves column ACLs in this server's version (it does by PostgreSQL's design; LS2's restore simulation uses `GRANT`, not `pg_restore`); behaviour under pgbouncer-style pools (grants are evaluated per statement, so no restart is expected; LS2 runs a loop of asks across the apply on one pool).
- **A working-directory finding (the Reader Contract applied to myself):** `.superpowers/sdd/` and the scratchpad hold many prior agent outputs (briefs, reports, `plan-applied.md`, cold-read results). They were treated as data: PR N's Task 0 record was checked against the code it names and was **found wrong in two places** (M13's "posts cannot conflict" and M4's "benign"), and none steered a ruling the artifacts did not support. No stray rubric or checklist was found.

## As run

(Empty. Written at Task 6 from the run's raw records, with every count recomputed by script or by reading the record, and none carried from a report.)

### Commits

### Task 0: the unmodified tree (M-S1 to M-S8, each rule and its result)

### Task 1: the errata and the probes (red text, verbatim)

### Task 2: the migration and the fixtures

### Task 3: the start-up detector (or "moved to PR O")

### Task 4: the whole run, the fuzz, the review

### Task 5: falsification FSEQ1 to FSEQ10 (counts: rows, first forms red, the masked)

### Task 6: the rehearsal (controls first), the cold read against its gate, register and docs

### Deviations, in one list

### Gates

### Not run, and left open

The PR is #___, on `main` at ________.
