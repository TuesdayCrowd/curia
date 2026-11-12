# The Log Assigns Its Own Positions: Implementation Plan (PR S; opens register D123 and D61 item 272; errata G69)

> Intended repository path: `docs/superpowers/plans/2026-11-12-the-log-assigns-its-own-positions.md` (staged in the scratchpad because the architect's write allowlist does not cover it; the builder copies it to that path unchanged).
> Written by `curia-architect` on 2026-11-12 as **PR S**, the small `db/` PR the owner put **first** (Q-C, "Second round" of the memory record `owner-security-decisions-2026-11-11`), before PR N. **Planned against `origin/main` at `b348f6a9` (PR H merged as #152) and read against `origin/the-vector-channel-is-semantic` at `4d3cb47a` (PR #151, open, waiting on the owner's `ci.yml` merge; it carries `main`).** Every identifier below was computed with `git show <ref>:<path>` and `git grep <pattern> <ref>`, not `grep` over the GitButler workspace.
> **Marks.** **[read]** is a fact read in the tree by the author of this plan. **[measured]** is a fact the author ran on a scratch database and role on the local PostgreSQL server (`psql` as the table owner and as the role; the database and role were dropped after, and the script is `.../scratchpad/planD/exp.sh`, `exp2.sh`, `exp3.sh`); **no Forum was started and no test was run.** **[record]** is a fact taken from PR N's Task 0 record (`.superpowers/sdd/2026-11-11-planN/n-task0.txt`), which the author read and did not re-run. **[unverified]** is a claim nothing has read or run, and each names the Task 0 measurement that settles it.
> Read for this plan: `db/0001`, `db/0002:130-175`, `db/0005`; `src/Curia.Infrastructure/Migrations/SchemaMigrations.cs` and its `<EmbeddedResource>` glob in `Curia.Infrastructure.csproj`; `PostgresEventStore.cs` (the INSERT at `:441`, the guard's reads at `:288-320`, the paged read at `:374`, the by-aggregate read at `:340`); `Curia.Application/Ports/EventReaderExtensions.cs`; `LifecycleEntryScan.cs`; `PostgresStoreFaults.cs`; `tests/Curia.Infrastructure.Tests/PostgresDatabaseFixture.cs:60-330`, `AppRoleGrantRefusalTests.cs`, `SchemaMigrationsTests.cs`, `PostgresAppendGuardTests.cs` (head), `HarnessRehearsalTests.cs` (by grep); `tests/Curia.Api.Tests/RoleForumFixture.cs:10-125`; `tools/fault-harness/fault.sh` (whole); the errata's G67 (form) and the white paper's R11.6 and Appendix D `events`; `IMPLEMENTATION_PLAN.md:15` (the first-free line); PR N's plan and its Amendment 1.
> **What a builder must not do with this plan:** treat a **[unverified]** as a fact. The two that carried the most weight were run by Task 0 on 2026-11-12: a planted `seq` ahead of the identity through the Forum (M-S3) held as read; a `seq` 0 row (M-S2) held in its blindness half (the head signer's stop and the start-up scan do not see it) and not in its honoured half (it made the victim's key set unreadable and was not honoured as a retirement, for the payload run): Amendment 1, ruling 12.

## Order against the neighbours, and why

PR S is the first of the revised order (S, N, N2, PR O, the hosting join). It is small on purpose and independent of everything: it touches `db/`, one migration list, three test fixtures, one fault-harness script, one start-up service and its port, and the three documents. **It does not wait for #151, PR N or anything else, and PR N does not start its consumers (Task 4) until S has merged**, because N's probes run on fixtures that apply `db/0006`. The reasons it comes first are the owner's (Q-C) and one more that the reading found: **the chosen-`seq` hole is not only the denial of service Task 0 measured. A row at `seq` 0 or below is invisible to every forward read (all of them start at `seq > 0`) and visible to the by-aggregate read the token path uses today, so it is a forged row that the head signer's stop and the start-up scan do not see (measured, M-S2) and that, after PR N, the audit cannot see, and it makes its victim's key set unreadable (Amendment 1, ruling 12)**. Every detective control the beta rests on (the 7006 scan, the stop, PR N's audit and 7007) reads forward from zero; this PR is what makes "forward from zero" the whole log.

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
2. **Negative `seq`** (record): accepted; every read that touches the aggregate then throws in `EventSequence.From` (`Corrupt events.seq`, `PostgresEventStore.cs:541`) and answers a bare `500 curia/server/fault` to an uninvolved legitimate agent (the real victim's `curia budget` in the record), to the token request, and to `curia verify`; the signing side tolerates it (`sign-head` exits 0). A forward read does not return the row (`seq > 0`), so no scan counts it. **The Forum starts with such a row in the log, logs nothing about it, and no hosted service stops it** [measured, Task 0 M-S1: after a restart there is no 7006 and no scan line; an agent whose aggregate holds no such row was unaffected before and after].
3. **`seq` 0, a shadow row** [measured, Task 0 M-S2, against a control that inserted the same forged `agent.key-retired` (the same payload, a `prev` of 1) at an ordinary position]. Every whole-log read starts at `EventSequence.Zero` and reads `WHERE seq > @after` (`EventReaderExtensions.ReadAllAsync`; `PostgresEventStore.cs:374`; `LifecycleEntryScan.cs:41`), so a row at `seq` 0 is returned by none of them. **Two detectors were shown able to see the control row and not the `seq` 0 row:** `sign-head --if-grown` (exit 6 `curia/head/withheld` for the control; exit 0 `unchanged  tree_size=11 latest_head_log_index=10` for `seq` 0) and the start-up scan (one 7006 line for the control, none for `seq` 0). The head, an inclusion proof and `GET /v1/log/jwks?agent=` did not change for either row (no head was signed over the control), so they are not reflected and have no control; a plain `sign-head` over a `seq` 0 row was not run (ruling 16). The by-aggregate read (`PostgresEventStore.cs:340`, no `seq` filter) returns the row, and it is the read the token path, ingest and the enrolment path use today (N's finding 3). **The row was not honoured as a retirement; it made its victim's key set unreadable.** The control, at an ordinary position, was honoured (`key_status` `retired`, the `FOREIGN` line, a `401`). At `seq` 0 the same payload failed `curia/keys/statement-mismatch` (the fold's reason was not read): `GET /v1/jwks?agent=` `503`; `curia keys`, `curia verify` and `curia read` of the victim's post exit 8; `GET /v1/posts/<id>` still `key_status` `active`; a fresh token request `401 invalid_client` (the reason inferred from PR N's M3 record, not read) and an issued token's next request `503 curia/log/unreadable`. **For the one payload run:** whether a payload the fold accepts at position 0 is honoured is not measured (ruling 16). The record's own anomaly fits: after the `seq` 0 insert `curia verify` still printed the post at **leaf 4** and the binding at **log index 1** (a log that had shifted would print 5 and 2), though the report said every index shifted.
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
- **Statements that INSERT into `events` (none names `seq`; each names the six columns or a subset of them):** `PostgresEventStore.cs:441`; tests `AgentWriteGateSupport.cs:224`, `BudgetReadoutEndpointTests.cs:654,661`, `BudgetReadoutSupport.cs:224,252`, `HeadsSignedUnattendedTests.cs:137`, `IndexRepairDeepGapEndpointTests.cs:35`, `LateCommitEndToEndTests.cs:932`, `RoleForumFixture.cs:64`, `AppRoleGrantRefusalTests.cs:71`, `ForksBodyDuplicateTests.cs:110`, and (Task 0 M-S4) `PostgresEventStoreContractTests.cs:94` (four columns, into the isolated schema; `AppRoleGrantRefusalTests.cs:71` names five). None is edited; all keep working under the column grant [measured for the shape].
- **Statements that grant or revoke on `events`:** `db/0001:47-48` (the one this PR narrows); `PostgresDatabaseFixture.cs:218-219` (the copy); `fault.sh:39-42,64`; `RoleForumFixture.cs:54,56` (`SELECT` only, unaffected); `StoreFaultInfrastructureTests.cs:112,119` (`SELECT` only); `HeadsSignedUnattendedTests.cs:764` (`SELECT` only, a role that must not insert).
- **Predicates over the grant:** `fault.sh:12-13,25-26` (`has_table_privilege`), `RoleForumFixture.cs:97-98`. No other site (`git grep has_table_privilege|has_column_privilege|has_any_column` over `src`, `tests`, `tools`).
- **Sites that mention the grant or the statement and are unaffected (Task 0 M-S4, Amendment 1 ruling 15):** `RoleForumFixture.cs:72-73` (`CREATE POLICY ... FOR INSERT ... WITH CHECK (true)`: a row policy, which a column grant does not touch; Task 2 leaves it); `tools/fault-harness/pgproxy.py:4` and `tools/fault-harness/README.md:63,72` (they key on the store's `INSERT INTO "public".events` text, which ruling 9 leaves unchanged); `IMPLEMENTATION_PLAN.md:8017` (D121's dated measurement states the grant as "`INSERT, SELECT` on `events`": annotated by the docs task, not rewritten).
- **Fixtures that apply the migration list:** `SchemaMigrations.RenderAll` is called by `PostgresDatabaseFixture.cs:96`, `ForumFixture.cs:435`, `ForksBodyDuplicateTests.cs:105`, `HarnessRehearsalTests.cs:77`; adding the file to `FileNames` is the whole change for all four. `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder` is what fails if the list entry is forgotten.
- **Read-model rebuilds (R11.9):** `Projections/AggregateSummaryProjectionRebuildDrillTests.cs` appends through the store and replays; it names no `seq` and holds no grant [read]. It is run, not assumed (M-S7).

## Rulings (decided, not asked; 1 to 11 here, 12 to 16 in Amendment 1)

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

## Amendment 1 (2026-11-12, Task 0's record)

Written by `curia-architect` after reading `.superpowers/sdd/2026-11-12-planS/task-0-report.md` and `s-task0.txt` (sha256 prefix `625178ee`; `.superpowers/sdd/` is git-ignored, so the numbers this amendment rules on are written here and the record is not the evidence a reader can open), then checking each fact against the record's raw lines and against the tree at `a6ab37d0` (this plan's commit on `main` `b348f6a9`; `origin/main` is still `b348f6a9` as a local ref, not fetched): `IMPLEMENTATION_PLAN.md:7418` and `:8017`, `AppRoleGrantRefusalTests.cs:71`, `PostgresEventStoreContractTests.cs:94`, `PostgresEventStore.cs:441`, `Help.cs:191`, `Program.cs:186-190`, and `m2.py`'s two plant lines. The plan body is changed only by the in-place edits at the end, each printed by the build.

### What was checked, and what it found

- **M-S1 held.** A row at `seq` -3 under the victim's aggregate: the victim's `curia budget` and `curia ask` exit 8, `GET /v1/jwks?agent=` answers `500 curia/server/fault`, the victim's `curia verify` of its own post exits 8; an agent whose aggregate holds no such row is unaffected before and after. **The restart: the Forum starts, logs nothing about the row (no 7006, no scan line), and no hosted service stops it.** (The scan reads forward and does not see the row.) This was the plan's one unknown; finding 2 now states it.
- **M-S2 held in its blindness half and not in its honoured half.** Same payload both runs (`retire_payload(home, 'alice', 1)`, a `prev` of 1, a garbage `authorization`); the only difference is the position, `seq` 0 against the identity's next value (12).

  | | row at `seq` 0 | control, ordinary position |
  |---|---|---|
  | `sign-head --if-grown` | exit 0 `unchanged tree_size=11 latest_head_log_index=10` | exit 6 `curia/head/withheld` |
  | 7006 after a restart | 0 lines | 1 line |
  | `curia keys` of the victim | exit 8 `curia/keys/statement-mismatch` | exit 6, the `FOREIGN` line |
  | `GET /v1/jwks?agent=` | `503` | `200` (retired) |
  | `GET /v1/posts/<id>` `key_status` | `active` | `retired` |
  | fresh token request | `401 invalid_client` | `401 invalid_client` (with the `FOREIGN` line) |
  | issued token's next request | `503 curia/log/unreadable` | `401 invalid_client` |
  | `GET /v1/log/head`, inclusion proof, `GET /v1/log/jwks?agent=` | unchanged, `200` | **unchanged, `200`** |

  The last row is the correction to the report and to the task's summary: **the control does not differ from the shadow row on the head, the proof or the log's key set**, because no head was signed over the control row (the stop refused) and `lifecycle` is absent from that document in both. Those three reads are "not reflected" with no control, and they are not detectors this record has shown able to see a row. The two that were shown to see an ordinary row and not a `seq` 0 row are the head signer's stop and the start-up scan. A plain `sign-head` (no `--if-grown`) over a `seq` 0 row was not run.
- **M-S3 held.** Rows planted at the identity's `+1` to `+3`; five honest asks by five agents: #1 to #3 `503 curia/store/unavailable` (exit 8, "has not confirmed that the act was recorded and has not refused it"), #4 and #5 succeeded; the sequence's `last_value` went 19, 20, 21, 22 against planted 20, 21, 22, one value consumed per refused append; `curia resend` of all three succeeded (exit 0, each agent's budget used 1). No `500`.
- **M-S4 held, and the plan's own sentence was wrong.** The report found `PostgresEventStoreContractTests.cs:94` (four columns). It missed that the plan's "all name the six columns" is also false of `AppRoleGrantRefusalTests.cs:71` (`event_id, event_type, aggregate_id, payload, server_ts`: five). Both are subsets of the six and work under the column grant; the sentence is corrected (E06). Zero hits for `has_column_privilege`, `has_any_column_privilege` and `OVERRIDING SYSTEM VALUE` anywhere in `src`, `tests`, `tools`.
- **M-S5: the baseline is not green, and the register already says so.** The whole Infrastructure run is 229 passed and 1 failed of 230, twice; the failure is `PostgresAppendGuardTests.R6_60_LCB22_EveryExitReleasesTheConnectionAndTheLock` ("locks held 1, idle in transaction 1"), which passed alone 3 of 3. **The report says item 163 names four other tests and proposes adding this one. `IMPLEMENTATION_PLAN.md:7418` (item 163, "Amended 2026-11-05 (the PR 6 PR, #149)") already names it, with these two counts and the same cause hypothesis.** Other controls: Release build 0 warnings 0 errors; `AggregateSummaryProjectionRebuildDrillTests` 2 of 2 alone and inside the whole run (M-S7 held); `HarnessRehearsalTests` 1 of 1; Api `StoreFaultRoleTests` 5 of 5, `HeadsSignedUnattendedTests` 21 of 21, the closed fuzz 8 of 8 (25 m 45 s); `check-spec` clean; `falsify-spec-checks` 4 of 4 red.
- **M-S6 held**, no difference from the architect's run: after `0006` the four statements (`OVERRIDING SYSTEM VALUE` at -6, 0 and 999, and `seq = DEFAULT`) are `permission denied for table events`; the `RETURNING seq` append returns 3; `UPDATE` and `DELETE` refused; re-apply rc 0; `has_*_privilege` `f|t|f|t`; a pre-0006 row at -5 survives; the `fault.sh` shape (`REVOKE INSERT`, table-level `GRANT INSERT`) reopens the hole.
- **M-S8: about 115 to 125 lines of production code, about 185 to 215 with the two probes**, counted by analogy with `LifecycleEntryScan` (78 lines, 35 of them remark), not written. No new test assembly (`CapturedForum`, `LogCapture`, `GuardedForumFixture` are in `Curia.Api.Tests`); the port has no string parameter, so `PortStringFenceTests` is untouched. `git grep` over `tests` at `a6ab37d0` finds no `LogLevel.Error` or `Level.Error` assertion, so a 7008 at every default-fixture start (the owner's connection can name `seq`) is noise and not a failure.
- **`curia verify` takes `--forum` and no `--agent`** (`Help.cs:191`; Task 0's first call: `error: unknown flag "--agent"`, exit 1). The brief's "`--agent` on every call" was wrong for it.

### Rulings (12 to 16; decided, not asked)

12. **Finding 2 and finding 3 are restated, with the report's numbers and three corrections. Accepted, amended.** The texts are E03, E04 and E09 below. What the report proposed stands: "honoured by the token path" goes; the numbers go in; M-S1's restart joins finding 2. Three changes to what it proposed: (a) the detectors named are the two shown against a control (the head signer's stop and the start-up scan), and the head, the proof and the log's key set are said to be "not reflected, with no control", since the control does not differ (above); (b) "not honoured as a retirement" is **for the payload run** (a `prev` naming a later position than the row's own, the fold reading the row first and failing `statement-mismatch`), not for every payload at `seq` 0: whether a payload the fold accepts at position 0 is honoured is **not measured** (ruling 16); (c) the token path's `401` reason is **inferred** from PR N's M3 record, and the finding says so. **The urgency stands and its form changes:** a forged row at `seq` 0 locks one agent out of its own key set (`401` on a fresh token, `503` on an issued one) and the head signer does not stop for it, so the operator's one automatic guard against a forged lifecycle row says nothing.
13. **Task 0's proposed ruling 13 is not accepted as written; its control is restated and no item is filed. Rejected in its first half, accepted in its second.** Its premise is that item 163 does not name LCB22. It does (`IMPLEMENTATION_PLAN.md:7418`, amended 2026-11-05, #149), with the same two counts. No fifth member is added and no item is filed (item 273 is PR N's). **Task 4's control is not a count.** It is (E15): every Infrastructure test passes except at most `R6_60_LCB22_...`, which when it fails reads exactly `locks held 1, idle in transaction 1`, and which then passes **alone**; the tip's total is 230 plus the tests this PR adds, computed by `--list-tests` on both trees and not remembered. A different count in LCB22's text, or any other failure, is a finding about this PR's fixture change (LCB22 reads `pg_locks` and `pg_stat_activity` over the whole database and Task 2 changes `CreateIsolatedSchemaAsync`). Task 6 adds a dated sentence to item 163 only if LCB22 fails in a PR's CI, which item 163's own rule says.
14. **Task 3 stays in the PR; the 200-line rule counts production code. Accepted, amended.** The rule's consequence is to return with ruling 7's severable alternative, and what ruling 7 severs is the port, the adapter, the hosted service and its registration; their tests are not what the rule weighs, and an estimate by analogy that straddles the line with them included cannot decide it. **The tripwire is measured and not estimated:** when Task 3's code is written, `git diff --numstat origin/main -- src/` over the port, the adapter, `PostgresAdapters.cs`, the hosted service and `Program.cs`, remarks included, is at most 200 lines (the estimate leaves about 80 of margin); over 200, Task 3 stops and returns to the architect. Three things Task 3 does (E14): SEQ7's quiet arm and SEQ9 run on a role-connected fixture (the default fixture connects as the owner and logs 7008 at every start, which is expected and is not read as a defect); `LateCommitEndToEndTests` LCB18, whose text check reads every log line (item 163), is run with the service in place; the 7008 message is asserted to carry no connection string and no role name.
15. **Four consumer sites ruled on, one line each. Accepted, amended.** (a) `PostgresEventStoreContractTests.cs:94` and `AppRoleGrantRefusalTests.cs:71` join "What consumes what": subsets of the six columns, no edit, covered by ruling 5 (E06). (b) `RoleForumFixture.cs:72-73`'s `FOR INSERT ... WITH CHECK (true)` is a row policy and a column grant does not touch it: stated in the list so that Task 2 does not "fix" it (E07). (c) `pgproxy.py` and `tools/fault-harness/README.md` key on the store's `INSERT INTO "public".events` text, which ruling 9 leaves unchanged (E07). (d) **`IMPLEMENTATION_PLAN.md:8017` is annotated, not rewritten**: it sits inside D121's dated measurement ("Measured on `main` at 2708d826") and its "`INSERT, SELECT` on `events`" was true of `db/0001` then; the docs task adds a dated bracket after it stating the column grant from `db/0006` (E16). The report's "rewritten" would have turned a measurement into a claim about the tree it was not made on. The LS1 note that `curia verify` takes no `--agent` is E18.
16. **Two measurements are added to Task 1, on the control build, before finding 3 is pasted as G69's.** They are what ruling 12 (b) leaves unmeasured. (a) **A `seq` 0 payload the fold accepts:** Task 1 reads `KeyLifecycle`'s fold for what it accepts at the first position (a `prev` of the row's own position, or none), then runs the forged `agent.key-retired` at `seq` 0 with each shape it finds. *Rule:* a variant that is honoured (the victim's key `retired`, the `FOREIGN` line) changes finding 3's "not honoured" sentence to say so with the variant, raises the urgency text, and is **reported to the architect before G69 is pasted**, as M-S2's own rule says; none honoured is recorded as "none of N shapes". (b) **A plain `sign-head` over a `seq` 0 row** on the control build: does the head's `tree_size` count it, and does a proof of it exist. SEQ9 plants **both** forms (-3 and 0) and each is refused `42501` (E19); its red text on the tree for the `seq` 0 form is recorded verbatim (a fresh token `401 invalid_client`, an issued token `503 curia/log/unreadable`).

### What Task 1 onward now do

- **Task 0 is done**; its rules are history and keep their words. "As run / Task 0" points here (E17).
- **Task 1** runs ruling 16's two measurements first, then pastes G69 **with the restated findings 2 and 3 (E09)**, then the probes; SEQ9 as E19. `check-spec` and `falsify-spec-checks` are clean before the probes, as before.
- **Task 2** unchanged; it does not touch `RoleForumFixture.cs:72-73`, `pgproxy.py` or the `PostgresEventStore.cs` INSERT.
- **Task 3** as ruling 14: the numstat tripwire, the role-connected fixture, LCB18, the message assertion.
- **Task 4's control** is ruling 13's (E15).
- **LS1's control leg** records the issued-token `503` and the plain `sign-head` (E18).
- **Task 6's docs** annotate `IMPLEMENTATION_PLAN.md:8017` (E16) and file no item for LCB22.

### Seam with PR N (not edited here)

PR N's LRV31 and ruling 36 predict, on the control build, only "the token request is `401 invalid_client`". M-S2 adds, for the control build, an issued token's `503 curia/log/unreadable`, `curia keys` exit 8 `curia/keys/statement-mismatch`, and the sentence that the row is not honoured as a retirement. Those are PR N's to take in its own amendment; its Task 1 records them verbatim as it already must.

### Allocations

None new, none moved. Recomputed on 2026-11-12 against `a6ab37d0` and `origin/main` `b348f6a9` (local ref): `IMPLEMENTATION_PLAN.md:15` on `origin/main` still reads "First free: D123, D61 item 272, trap 245; errata G66 is D10b's, G64 PR 8's and G68 PR N's (provisional)"; `git grep -E 'G69|G70|7008|SEQ[0-9]|FSEQ' origin/main` outside `docs/superpowers/plans` is empty; `gh pr list --state all --limit 4` shows #152 merged and #151 open, so #153 is still the next number. Rulings 12 to 16 are this plan's own and are not requirement numbers. Item 163 is amended only by a dated sentence if LCB22 fails again in CI; no item is filed.

### Edits to the plan body (the in-place edits)

Each `Old` is exact text of the plan outside this amendment, matched by script and printed by the build (`build.py` beside this file; a miss is printed and does not stop the others).

#### E01
Old:
```text
The two that carry the most weight are what a `seq` 0 row does to the token path and to every detector (M-S2), and what a planted `seq` ahead of the identity does through the Forum (M-S3): both are read from the code and consistent with the record, and neither has been run through a Forum.
```
New:
```text
The two that carried the most weight were run by Task 0 on 2026-11-12: a planted `seq` ahead of the identity through the Forum (M-S3) held as read; a `seq` 0 row (M-S2) held in its blindness half (the head signer's stop and the start-up scan do not see it) and not in its honoured half (it made the victim's key set unreadable and was not honoured as a retirement, for the payload run): Amendment 1, ruling 12.
```

#### E02
Old:
```text
so it is a forged lifecycle row that no head, proof, start-up scan, stop or audit can see** (M-S2).
```
New:
```text
so it is a forged row that the head signer's stop and the start-up scan do not see (measured, M-S2) and that, after PR N, the audit cannot see, and it makes its victim's key set unreadable (Amendment 1, ruling 12)**.
```

#### E03
Old:
```text
**Whether the Forum starts with such a row in the log is not recorded** (the start-up scan reads forward and does not see it; M-S1 runs it).
```
New:
```text
**The Forum starts with such a row in the log, logs nothing about it, and no hosted service stops it** [measured, Task 0 M-S1: after a restart there is no 7006 and no scan line; an agent whose aggregate holds no such row was unaffected before and after].
```

#### E04
Old:
```text
3. **`seq` 0, a shadow row** [read; consistent with the record; M-S2 runs it through the Forum]. Every whole-log read starts at `EventSequence.Zero` and reads `WHERE seq > @after` (`EventReaderExtensions.ReadAllAsync`; `PostgresEventStore.cs:374`; `LifecycleEntryScan.cs:41`), so a row at `seq` 0 is returned by none of them: not the Acta (so no proof, no head commits to it), not any projection, not the 7006 scan, not `sign-head --if-grown`'s stop, not (after PR N) the audit. The by-aggregate read (`PostgresEventStore.cs:340`, no `seq` filter) returns it, and **that is the read the token path, ingest and the enrolment path use today** (N's finding 3). A forged `agent.key-retired` at `seq` 0 is therefore honoured by the token path and invisible to every detector. The record's own anomaly fits: after the `seq` 0 insert `curia verify` still printed the post at **leaf 4** and the binding at **log index 1** (a log that had shifted would print 5 and 2), though the report said every index shifted.
```
New:
```text
3. **`seq` 0, a shadow row** [measured, Task 0 M-S2, against a control that inserted the same forged `agent.key-retired` (the same payload, a `prev` of 1) at an ordinary position]. Every whole-log read starts at `EventSequence.Zero` and reads `WHERE seq > @after` (`EventReaderExtensions.ReadAllAsync`; `PostgresEventStore.cs:374`; `LifecycleEntryScan.cs:41`), so a row at `seq` 0 is returned by none of them. **Two detectors were shown able to see the control row and not the `seq` 0 row:** `sign-head --if-grown` (exit 6 `curia/head/withheld` for the control; exit 0 `unchanged  tree_size=11 latest_head_log_index=10` for `seq` 0) and the start-up scan (one 7006 line for the control, none for `seq` 0). The head, an inclusion proof and `GET /v1/log/jwks?agent=` did not change for either row (no head was signed over the control), so they are not reflected and have no control; a plain `sign-head` over a `seq` 0 row was not run (ruling 16). The by-aggregate read (`PostgresEventStore.cs:340`, no `seq` filter) returns the row, and it is the read the token path, ingest and the enrolment path use today (N's finding 3). **The row was not honoured as a retirement; it made its victim's key set unreadable.** The control, at an ordinary position, was honoured (`key_status` `retired`, the `FOREIGN` line, a `401`). At `seq` 0 the same payload failed `curia/keys/statement-mismatch` (the fold's reason was not read): `GET /v1/jwks?agent=` `503`; `curia keys`, `curia verify` and `curia read` of the victim's post exit 8; `GET /v1/posts/<id>` still `key_status` `active`; a fresh token request `401 invalid_client` (the reason inferred from PR N's M3 record, not read) and an issued token's next request `503 curia/log/unreadable`. **For the one payload run:** whether a payload the fold accepts at position 0 is honoured is not measured (ruling 16). The record's own anomaly fits: after the `seq` 0 insert `curia verify` still printed the post at **leaf 4** and the binding at **log index 1** (a log that had shifted would print 5 and 2), though the report said every index shifted.
```

#### E05
Old:
```text
## Rulings (decided, not asked)

```
New:
```text
## Rulings (decided, not asked; 1 to 11 here, 12 to 16 in Amendment 1)

```

#### E06
Old:
```text
(all name the six columns, none names `seq`):**
```
New:
```text
(none names `seq`; each names the six columns or a subset of them):**
```

#### E06b
Old:
```text
`ForksBodyDuplicateTests.cs:110`. None is edited;
```
New:
```text
`ForksBodyDuplicateTests.cs:110`, and (Task 0 M-S4) `PostgresEventStoreContractTests.cs:94` (four columns, into the isolated schema; `AppRoleGrantRefusalTests.cs:71` names five). None is edited;
```

#### E07: inserted before the line below
New (inserted line):
```text
- **Sites that mention the grant or the statement and are unaffected (Task 0 M-S4, Amendment 1 ruling 15):** `RoleForumFixture.cs:72-73` (`CREATE POLICY ... FOR INSERT ... WITH CHECK (true)`: a row policy, which a column grant does not touch; Task 2 leaves it); `tools/fault-harness/pgproxy.py:4` and `tools/fault-harness/README.md:63,72` (they key on the store's `INSERT INTO "public".events` text, which ruling 9 leaves unchanged); `IMPLEMENTATION_PLAN.md:8017` (D121's dated measurement states the grant as "`INSERT, SELECT` on `events`": annotated by the docs task, not rewritten).
```
Before:
```text
- **Fixtures that apply the migration list:** `SchemaMigrations.RenderAll` is called by `PostgresDatabaseFixture.cs:96`, `ForumFixture.cs:435`, `ForksBodyDuplicateTests.cs:105`, `HarnessRehearsalTests.cs:77`; adding the file to `FileNames` is the whole change for all four. `SchemaMigrationsTests.FileNamesCoversEveryCheckedInMigrationInOrder` is what fails if the list entry is forgotten.
```

#### E08
Old:
```text
(a forged row no head, proof, scan or stop can see), below zero
```
New:
```text
(a forged row that neither the start-up scan nor the head signer's stop sees, and that can make every read of its aggregate fail), below zero
```

#### E09
Old:
```text
fatal to every by-aggregate read of the aggregate (an unhandled `500`).
```
New:
```text
fatal to every by-aggregate read of the aggregate (an unhandled `500`); the Forum restarts over such a row silently (no 7006, no scan line, no hosted service stops it; Task 0 M-S1).
```

#### E09b
Old:
```text
3. **`seq` 0:** accepted and invisible to every forward read (the Acta, every projection, the start-up scan, the head signer's stop) and returned by the by-aggregate read, so a row the token path honours and no head can commit to.
```
New:
```text
3. **`seq` 0:** accepted and invisible to every forward read. **Measured (Task 0, M-S2) against a control at an ordinary position:** the head signer's stop did not see it (`sign-head --if-grown` exited 0 `unchanged`; the control exited 6) and the start-up scan did not log it (no 7006; the control logged one). The by-aggregate read returns it, and the forged `agent.key-retired` that was run was **not honoured as a retirement: it made its victim's key set unreadable**: a fresh token request answered `401 invalid_client` (the reason inferred), an issued token's next request `503 curia/log/unreadable`, the served key set `503 curia/keys/statement-mismatch`, `curia keys`, `curia verify` and `curia read` of the victim's post exit 8, and the post was still served `key_status` `active`. For the one payload run; a payload the fold accepts at position 0 is not measured.
```

#### E10
Old:
```text
The architect's scratch run measured the third form (a `seq` ahead of the identity makes an honest append fail) and the shape of the closure (a column grant).
```
New:
```text
The architect's scratch run measured the third form (a `seq` ahead of the identity makes an honest append fail) and the shape of the closure (a column grant). Task 0 of this PR then ran all three through a Forum (M-S1, M-S2, M-S3); the numbers are in the finding.
```

#### E11
Old:
```text
a negative seq (PR N's Task 0 M4: a 500 on every read of the aggregate, an uninvolved agent's included); a seq of 0 (read: invisible to every forward read, returned by the by-aggregate read: M-S2); a seq ahead of the identity (measured at the database: one honest append refused per planted value: M-S3)
```
New:
```text
a negative seq (PR N's Task 0 M4: a 500 on every read of the aggregate, the legitimate agent's included; the Forum restarts over the row silently: M-S1); a seq of 0 (measured, M-S2, for one payload: not seen by the head signer's stop or the start-up scan, and not honoured as a retirement but breaking the victim's key set: a fresh token request 401, an issued token 503 curia/log/unreadable, the served key set 503); a seq ahead of the identity (measured at the database and through a Forum: one honest append refused per planted value, three asks answered 503 curia/store/unavailable and recovered by curia resend: M-S3)
```

#### E12
Old:
```text
- **The forward-read blindness is read, not run** (finding 3). If M-S2 contradicts it, the PR is still right (the 500s and the collision are measured) and its urgency text is rewritten.
```
New:
```text
- **The forward-read blindness was run (M-S2). It held for the head signer's stop and the start-up scan; the by-aggregate half did not hold as written (Amendment 1, ruling 12).** Not measured, and stated so in finding 3: a plain `sign-head` over a `seq` 0 row, and a `seq` 0 payload the fold accepts; Task 1 measures both on the control build (ruling 16).
```

#### E13
Old:
```text
SEQ3, SEQ4 (the part that does not need the fixture change), SEQ9 and SEQ10 are controls on the tree (green by design) where the probes' own list says so.
```
New:
```text
SEQ3, SEQ4 (the part that does not need the fixture change), SEQ9 and SEQ10 are controls on the tree (green by design) where the probes' own list says so. **Before step (1), Task 1 runs ruling 16's two measurements on the control build and records them verbatim.**
```

#### E14
Old:
```text
the port-string fence is unaffected. SEQ7 and SEQ9.
```
New:
```text
the port-string fence is unaffected. SEQ7 and SEQ9. **Amendment 1, ruling 14:** the 200-line rule counts production code, measured when written (`git diff --numstat origin/main -- src/` over the port, the adapter, `PostgresAdapters.cs`, the service and `Program.cs`, remarks included; over 200, stop and return with ruling 7's severable alternative); SEQ7's quiet arm and SEQ9 run on a role-connected fixture, because the default fixture connects as the owner and logs 7008 at every start (expected, not a defect); `LateCommitEndToEndTests` LCB18, whose text check reads every log line (item 163), is run with the service in place; a test asserts the 7008 message carries no connection string and no role name.
```

#### E15
Old:
```text
the Infrastructure suite including the replay drill (M-S7); the screening-cost step;
```
New:
```text
the Infrastructure suite including the replay drill (M-S7), **whose control is Task 0's and is not a count (Amendment 1, ruling 13): every test passes except at most `PostgresAppendGuardTests.R6_60_LCB22_EveryExitReleasesTheConnectionAndTheLock`, which when it fails reads exactly `locks held 1, idle in transaction 1` (register item 163) and then passes alone; at `b348f6a9` the whole run is 229 passed and 1 failed of 230, and the tip's total is 230 plus the tests this PR adds, computed by `--list-tests` on both trees; Task 4 records the failing set, requires it to be within that one test and its text unchanged, and runs LCB22 alone**; the screening-cost step;
```

#### E16
Old:
```text
- `IMPLEMENTATION_PLAN.md`: "Current work",
```
New:
```text
- `IMPLEMENTATION_PLAN.md` (Amendment 1, rulings 13 and 15: the sentence at `:8017` inside D121 that states the grant as "`INSERT, SELECT` on `events`" is a dated measurement of `2708d826` and gets a dated bracket after it stating the column grant, not a rewrite; item 163 already names LCB22, so no item is filed and it gains a dated sentence only if LCB22 fails again in a PR's CI): "Current work",
```

#### E17
Old:
```text
### Task 0: the unmodified tree (M-S1 to M-S8, each rule and its result)
```
New:
```text
### Task 0: the unmodified tree (M-S1 to M-S8, each rule and its result)

Done 2026-11-12 (record `s-task0.txt` and `task-0-report.md`, sha256 prefixes `625178ee` and `a0a0c366`; `.superpowers/sdd/` is git-ignored, so the numbers are in Amendment 1's "What was checked"). M-S1, M-S3, M-S4, M-S6, M-S7 held; M-S2 held in its blindness half and not its honoured half; M-S5's baseline carries LCB22 (item 163 already names it); M-S8's estimate straddles 200 only with the probes counted (ruling 14).
```

#### E18
Old:
```text
*Control (`b348f6a9`):* the records' 500s, the shadow row's `401`, the refused honest appends.
```
New:
```text
*Control (`b348f6a9`):* the records' 500s, the shadow row's `401` (fresh token) and `503 curia/log/unreadable` (issued token), `curia keys` exit 8 `curia/keys/statement-mismatch`, a plain `sign-head` over a `seq` 0 row (ruling 16 (b)), the refused honest appends (`503 curia/store/unavailable`, recovered by `curia resend`).
```

#### E18b
Old:
```text
five honest asks succeed.
```
New:
```text
five honest asks succeed. `curia verify` takes `--forum` and no `--agent` (Task 0's first call: `error: unknown flag "--agent"`, exit 1); every other `curia` call passes `--agent`.
```

#### E19
Old:
```text
With the app role, the insert at -3 is refused 42501;
```
New:
```text
With the app role, the inserts at -3 and at 0 are each refused 42501;
```

#### E19b
Old:
```text
and then the 500s of the record.
```
New:
```text
and then the record's 500s (the -3 form) or, for the 0 form, a fresh token request `401 invalid_client` and an issued token's `503 curia/log/unreadable` (M-S2).
```

## Amendment 2 (2026-11-12, the first cold read of the README block)

**What happened.** Three fresh readers ran the pre-check, applied in one transaction as the owner, ran the post-check, said `seq` is not writable, deleted nothing. None said the event count was unchanged: nothing in the README asks for it. The gate's clause was met by no one, and the fault is the text's, not the readers'.

### Rulings (17 to 21; decided, not asked)

17. **The README changes; the controller's proposal is accepted in its aim and amended in its claim.** Proposed: print the count in the pre-check, print it again in the post-check, and say the two "must equal". Two defects. (a) **"Must equal" is false on the database this runbook is for.** The hosted Forum takes posts while the owner works (LS2 itself runs `curia ask` across the apply); a count that rose is an honest log. The true claim is that the migration changes grants and no row, so neither `events` nor `highest_seq` may be **lower** after, and both are equal if the Forum took nothing in between. A "must equal" instruction makes an honest owner report a finding on a busy log, or learn to ignore the sentence. (b) **The count does not belong inside the pre-check.** SEQ8 parses the pre-check as two numbers that must both be 0 (`PreCheckAsync` reads `(long, long)`); a third and fourth column there changes what "both numbers must be 0" means and the parse with it. The count is its own marked block, run twice, so the text run before and after is one text and cannot drift.

18. **The exact README text.** (i) After step 1's "Both numbers must be `0`..." paragraph, add:

> Then record how many events the log holds and its highest position, to compare in step 3. This is one text, run here and again there.
>
> ```
> <!-- events-position-count -->
> ```sql
> SELECT count(*) AS events, coalesce(max(seq), 0) AS highest_seq FROM public.events;
> ```
> <!-- /events-position-count -->
> ```
> (the markers on their own lines around the fence, as the other three blocks are written; the outer fence above is only this amendment's quoting). Keep the output.

(ii) At the end of step 3's first paragraph, after "It must say `seq_writable` `f` and `server_ts_writable` `t`." and its failure sentence, add a new paragraph:

> **Then run step 1's count text again.** `events` and `highest_seq` must each be at least what you kept, and equal if the Forum took no post in between: the migration changes the role's grants and no row. If either is lower, stop, keep both outputs and report it; do not delete or re-insert anything.

(iii) The intro sentence "runs the first and the third from this file" reads "runs the first and the third, and the count text, from this file". Steps 1 and 2's existing blocks and markers are unchanged.

19. **SEQ8 changes (one test, three additions; its existing assertions stay).** (a) It parses a fourth block, `events-position-count`, with `ReadmeBlock` (red before the block exists: `no events-position-count block in README.md`). (b) On the planted database it runs the count text before the apply and after, and asserts `(5, 500)` both times (two honest rows, three planted), so a migration that moved a row would show. (c) **It shows the text can see a loss, which the readers' run cannot:** on the clean database the test connection, as the table's owner, deletes the one row (test-only; the application role has no `DELETE`, and no tree code deletes) and the count text reports `(0, 0)` against the `(1, 1)` it reported before. Without (c) the block could be `SELECT 0, 0` and every assertion about it hold (vacuity question 1). (d) It asserts the README contains the words `at least what you kept` and `no row` in step 3. That pin restates the README against itself and is stated as such; the check that the sentence does its work is the second cold read, not this assertion.

20. **Two falsification rows.** FSEQ11: the step-3 count paragraph deleted from the README; must go red: SEQ8 (d); first form: an owner who is never asked to compare the count, which is the first pass's finding, and a test that passes while the text is absent. FSEQ12: the count block's `count(*)` replaced with `0`; must go red: SEQ8 (b) and (c); first form: a block that reports a log with no events as the same log as one that lost some. Task 5's rows become FSEQ1 to FSEQ12 and the counts recorded are for twelve.

21. **The gate is rewritten, and the second pass is run on it.** The first gate's "the event count is unchanged" is replaced by: **the reader ran the count text before the apply and again after, and says the count and highest position are not lower (equal if nothing was posted), quoting both outputs.** A reader who states the clause without the two outputs in its report fails it: a sentence without a number behind it is the readers' guess, not the README's work. Every other clause stands. **Three fresh Sonnet 5.5 readers**, none from the first pass, each in an empty directory with the amended `readme-block.md` (same shape as before: the owner connection, the application role's name, the migration's path, a throwaway database that holds events and no planted row), not told what is tested. Pass is **three of three** on every clause; one reader short is a second finding to the architect and a third pass waits for a ruling. The 7008 readers are not re-run: that text is unchanged and both passed. **LS2's wording is corrected in the same edit:** "the event count, the maximum `seq` and a SHA-256 over the ordered `event_id` list are unchanged by the apply" contradicts its own running `curia ask` loop; it reads "the ordered `event_id` list taken before the apply is a prefix of the list after it, and no `event_id` is missing".

### Edits to the plan body
SEQ8's row in "Probes" gains (a) to (d); the falsification table gains FSEQ11 and FSEQ12 and "FSEQ1 to FSEQ10" becomes "FSEQ1 to FSEQ12" in Scope, Task 5 and the Gates; the cold-read gate and LS2 are replaced as ruling 21 says; Scope's README line names the count block's markers.

## The errata entry (G69), text to paste at Task 1 (status "specified; built" is written by Task 6 from the run)

Task 1 pastes this at the end of the errata, after G68's slot is left as PR N's (or after G67 if N has not merged), with the index row for `R11.6 (add.)` and the `(add.)` mark in the Revisions table the way G13 did for R11.9; SEQ2 parses its column list.

### G69 — The log assigns its own positions: the application role can no longer name an event's `seq`, so no row can be placed where a forward read does not look, made to fail every read of its aggregate, or planted under the identity's next value

**Location.** §11.2, R11.6 (annotated by R11.6 (add.)); R6.47 (a leaf index is a position in the whole log), R6.60 (the append guard keys on `seq > cursor`); Appendix D `events` (unchanged); register D123, D61 item 272; PR N's plan (its Task 0 M4, its finding 10 and ruling 21, superseded).

**How it surfaced.** PR N's Task 0 (2026-11-11, M4) inserted rows into `events` as the application role: `OVERRIDING SYSTEM VALUE` at `seq` 0, -2 and -3 was accepted, a negative `seq` answered `500 curia/server/fault` to every read of the aggregate, including a legitimate agent's, and `server_ts` was stored verbatim. The report called a non-negative `seq` "benign"; the same record's `curia verify` still printed the post at leaf 4 after the insert. Reading why (every whole-log read is `WHERE seq > 0`; the by-aggregate read has no filter) turned "benign" into the form no detector sees. The architect's scratch run measured the third form (a `seq` ahead of the identity makes an honest append fail) and the shape of the closure (a column grant). Task 0 of this PR then ran all three through a Forum (M-S1, M-S2, M-S3); the numbers are in the finding.

**Class.** A protection that does not cover the column that defines the order. The grant R11.6 asks for (`INSERT` and `SELECT`, never `UPDATE` or `DELETE`) was applied to the whole table, so it protected every row from rewriting and no row from being placed.

### The finding

1. **The role names `seq`.** An `INSERT` with `OVERRIDING SYSTEM VALUE` is accepted at any value the key allows; the same without it is refused by the identity rule, not by a grant.
2. **Negative `seq`:** accepted, invisible to forward reads, fatal to every by-aggregate read of the aggregate (an unhandled `500`); the Forum restarts over such a row silently (no 7006, no scan line, no hosted service stops it; Task 0 M-S1).
3. **`seq` 0:** accepted and invisible to every forward read. **Measured (Task 0, M-S2) against a control at an ordinary position:** the head signer's stop did not see it (`sign-head --if-grown` exited 0 `unchanged`; the control exited 6) and the start-up scan did not log it (no 7006; the control logged one). The by-aggregate read returns it, and the forged `agent.key-retired` that was run was **not honoured as a retirement: it made its victim's key set unreadable**: a fresh token request answered `401 invalid_client` (the reason inferred), an issued token's next request `503 curia/log/unreadable`, the served key set `503 curia/keys/statement-mismatch`, `curia keys`, `curia verify` and `curia read` of the victim's post exit 8, and the post was still served `key_status` `active`. For the one payload run; a payload the fold accepts at position 0 is not measured.
4. **A `seq` ahead of the identity:** accepted, and one honest append later fails on the primary key for each planted value.
5. **The remedy is a column grant** for the six columns the Forum supplies; `server_ts` stays in it because the Forum supplies it (CS-9).

### Decisions this entry makes

1. The application role's `INSERT` on `events` is a column grant that excludes `seq`.
2. The migration revokes the table-level `INSERT` first (a column grant is additive).
3. The Forum logs at every start when its role can still name `seq`, and starts.
4. The owner applies the migration to a database that already holds rows, after a read-only check that no row sits at or below zero or ahead of the identity.

### Requirement text

**R11.6 (add.)** The `INSERT` that R11.6 grants on `events` SHALL be a grant on the columns the Forum supplies, `event_id`, `event_type`, `aggregate_id`, `actor_id`, `payload` and `server_ts`, and SHALL NOT include `seq`: an event's position in the log is assigned by the database, in the order its transactions take the append lock, and no statement of the application's role may name it, with or without `OVERRIDING SYSTEM VALUE` and as `DEFAULT`. A migration that adds a column the Forum supplies SHALL grant `INSERT` on that column in the same file. The Forum SHALL log at Error level, at every start, when the role it connects as can name `seq`, and SHALL start either way. The reason: a position is a leaf index (R6.47), and every paged read and the append guard (R6.60) are keyed on `seq > cursor`, so a row the role places at a position of its own choosing is not a row the Forum appended; at `seq` 0 or below it is invisible to every forward read while the by-aggregate read returns it (a forged row that neither the start-up scan nor the head signer's stop sees, and that can make every read of its aggregate fail), below zero it makes every read of its aggregate fail with a `500`, and above the identity's next value it makes an honest append fail on the primary key. R11.6's own reason is that append-only is held by the grant and not by the code's intentions; this is the same sentence for the column that orders the log.

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

**Order:** (1) G69 pasted as specified, with the index row and the `(add.)` mark, `check-spec.py` and `falsify-spec-checks.py` clean (SEQ2 parses it); (2) the probes below, each red on the tree without `db/0006`, its text recorded in "As run" before its fix. SEQ3, SEQ4 (the part that does not need the fixture change), SEQ9 and SEQ10 are controls on the tree (green by design) where the probes' own list says so. **Before step (1), Task 1 runs ruling 16's two measurements on the control build and records them verbatim.**

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

`IEventsPositionGrant` (Application port; `Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken)`; no string parameter; an in-memory adapter returns the value it was built with), `PostgresEventsPositionGrant` (Infrastructure: `SELECT has_column_privilege(current_user, @table, 'seq', 'INSERT')` with the store's own qualified table name, through the data source the event store uses, a fault classified by `PostgresStoreFaults` and returned as the port's error), `EventsSeqWritableCheck` (`IHostedService` in `Curia.Api/Adapters/`, registered once in `Program.cs`): on start it asks, logs EventId 7008 `EventsSeqWritable` at Error through a `[LoggerMessage]` when the answer is `true`, logs nothing when `false`, and **starts either way** (a port error is logged at Warning as "could not be checked" and also starts: a check that stops the Forum is the refusal ruling 7 declined). The message names the fact and the remedy and carries no connection string. CS-7 holds (Npgsql stays in Infrastructure); the port-string fence is unaffected. SEQ7 and SEQ9. **Amendment 1, ruling 14:** the 200-line rule counts production code, measured when written (`git diff --numstat origin/main -- src/` over the port, the adapter, `PostgresAdapters.cs`, the service and `Program.cs`, remarks included; over 200, stop and return with ruling 7's severable alternative); SEQ7's quiet arm and SEQ9 run on a role-connected fixture, because the default fixture connects as the owner and logs 7008 at every start (expected, not a defect); `LateCommitEndToEndTests` LCB18, whose text check reads every log line (item 163), is run with the service in place; a test asserts the 7008 message carries no connection string and no role name.

### Task 4: The whole run once, the gates, the review

The whole solution as CI runs it: `dotnet build Curia.sln -c Release`; the all-but-Api test step; the Debug Architecture step (D16: `Curia.Architecture.Tests` in both configurations; the port and hosted-service additions read IL); the Api suite once (`EventsPositionApiTests`, `StoreFaultRoleTests`, `HeadsSignedUnattendedTests` and the closed fuzz (R14.10); no route changes, so the fuzz's ledger stays empty); the Infrastructure suite including the replay drill (M-S7), **whose control is Task 0's and is not a count (Amendment 1, ruling 13): every test passes except at most `PostgresAppendGuardTests.R6_60_LCB22_EveryExitReleasesTheConnectionAndTheLock`, which when it fails reads exactly `locks held 1, idle in transaction 1` (register item 163) and then passes alone; at `b348f6a9` the whole run is 229 passed and 1 failed of 230, and the tip's total is 230 plus the tests this PR adds, computed by `--list-tests` on both trees; Task 4 records the failing set, requires it to be within that one test and its text unchanged, and runs LCB22 alone**; the screening-cost step; `check-spec` and `falsify-spec-checks`; the locked restore; `preflight.py --api "<the new and changed Api classes>"`. **Review, three lenses, each told the premises above:** (1) spec compliance against G69 line by line; (2) **the harness and the fixtures** (does any other fixture, script or doc grant or read `events` INSERT at table level; does any probe in the repository pass because its fixture is table-level); (3) the words (the log message, the README's pre- and post-check, `fault.sh`'s status line) read cold.

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
- **SEQ9** *(Api, control)* **a forged row cannot be planted through the Forum's own role and the victim is unaffected.** With the app role, the inserts at -3 and at 0 are each refused 42501; the victim's token request, `curia budget` and `curia verify` are as with no attempt. Red on the tree: `Expected: 42501  Actual: row inserted` and then the record's 500s (the -3 form) or, for the 0 form, a fresh token request `401 invalid_client` and an issued token's `503 curia/log/unreadable` (M-S2).
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

- **LS1 (the three forms are refused through the Forum, and the victim is unaffected).** The M-S1 to M-S3 plants as the app role. *Control (`b348f6a9`):* the records' 500s, the shadow row's `401` (fresh token) and `503 curia/log/unreadable` (issued token), `curia keys` exit 8 `curia/keys/statement-mismatch`, a plain `sign-head` over a `seq` 0 row (ruling 16 (b)), the refused honest appends (`503 curia/store/unavailable`, recovered by `curia resend`). *Rule on S:* each plant is refused `42501`; the victim's token, budget and verify are as with no attempt; five honest asks succeed. `curia verify` takes `--forum` and no `--agent` (Task 0's first call: `error: unknown flag "--agent"`, exit 1); every other `curia` call passes `--agent`.
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
- `IMPLEMENTATION_PLAN.md` (Amendment 1, rulings 13 and 15: the sentence at `:8017` inside D121 that states the grant as "`INSERT, SELECT` on `events`" is a dated measurement of `2708d826` and gets a dated bracket after it stating the column grant, not a rewrite; item 163 already names LCB22, so no item is filed and it gains a dated sentence only if LCB22 fails again in a PR's CI): "Current work", "Next stage" (PR N next, then N2, PR O, the join; PR S built), the PR table, the Beta section (item 9's preconditions: the apply), the register (below), the first-free line (**"First free: D124, D61 item 273, trap 248; errata G68 is PR N's, G70 PR N2's (provisional)"**), traps 245 to 247.
- `README.md`: the operator runbook's new step and its pre-check/apply/post-check block (marked for SEQ8), the sentence "Apply `db/*.sql` in order first" gains "and `db/0006` narrows the role's `INSERT`", the Beta notes' one sentence (a database that has not applied it can be reordered by its credential).
- `CLAUDE.md`: the status paragraph (a clause: the application role names no `seq`, errata G69), and the Build-and-test note that the fixtures render `db/0006`.
- `tools/fault-harness/README.md`: `insert-off` and `status` restore and read the column grant.
- `tools/preflight/README.md` only if preflight's step list changed (it should not).

## Register text to paste (Task 6 writes the ones the run confirms)

**D123.** `The application role can name an event's seq (db/0001 granted INSERT on every column). Fixed by errata G69 (PR S, #153): db/0006 grants INSERT on the six columns the Forum supplies and not seq; the Forum logs EventId 7008 at every start when its role can still name it. The measured numbers stay as the record: a negative seq (PR N's Task 0 M4: a 500 on every read of the aggregate, the legitimate agent's included; the Forum restarts over the row silently: M-S1); a seq of 0 (measured, M-S2, for one payload: not seen by the head signer's stop or the start-up scan, and not honoured as a retirement but breaking the victim's key set: a fresh token request 401, an issued token 503 curia/log/unreadable, the served key set 503); a seq ahead of the identity (measured at the database and through a Forum: one honest append refused per planted value, three asks answered 503 curia/store/unavailable and recovered by curia resend: M-S3). What it does not close: server_ts (PR N's item 274), a role that can write seq by being a superuser or the table's owner, rows already planted, and a restore from a backup that predates the migration.` Status `fixed`, with the legs that show it, when the owner has applied it to the hosted database (the PR's status line says "built, not applied").

**D61 item 272.** `The Forum's start-up check of the events grant is a log (EventId 7008), not a refusal: a database that has not applied db/0006, a superuser connection and a restore from an older backup all start and say so once. PR O's readiness check alarms on it; whether the Forum should refuse to start is the owner's call after the drill.`

**Traps 245 to 247:** the three above, each in the plan's one-sentence form.

## Next stage

PR N (lifecycle rows are honoured only if they verify; errata G68; re-based onto this PR's merge before its Task 4), then PR N2 (operator acts signed by a key the Forum never holds; errata G70; blocks the announcement), then PR O, then the hosting join: the order lines are in the architect's report beside this plan.

## Open risks (named so the reviewer can look)

- **Task 3 is the part that is not obviously "small".** It is severable (ruling 7). Its estimate is Task 0 M-S8's.
- **The forward-read blindness was run (M-S2). It held for the head signer's stop and the start-up scan; the by-aggregate half did not hold as written (Amendment 1, ruling 12).** Not measured, and stated so in finding 3: a plain `sign-head` over a `seq` 0 row, and a `seq` 0 payload the fold accepts; Task 1 measures both on the control build (ruling 16).
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

Written at Task 6c from the run's raw records (`.superpowers/sdd/2026-11-12-planS/`, git-ignored: `s-task0.txt`, `s-task1to4.txt`, `s-task6-live.txt`, `fseq-results.jsonl`, `cold-read-results.md` and the task reports). Every count below was recomputed from a record (the falsification rows by script from `fseq-results.jsonl`) and none copied from a report.

### Commits

On `the-log-assigns-its-own-positions`, from `main` b348f6a9, oldest first (the commit ids are GitButler's at the time of writing):

1. the plan; 2. Amendment 1 (Task 0's results, rulings 12 to 16); 3. T1 (G69 pasted, ruling 16 measured, SEQ1 to SEQ10 red on the tree); 4. T2 (`db/0006`, the fixtures, the harness); 5. T3 (the start-up check, EventId 7008); 6. T4 (the owner's runbook as text a test runs); 7. T6 (SEQ7 starts its own Forum); 8. Amendment 2 (the first cold read's finding ruled); 9. T6b (the count text, SEQ8's additions, FSEQ11 and FSEQ12); 10. T6c (this record, the errata's status, the register and the docs). Nine commits were on the branch before this one.

### Task 0: the unmodified tree (M-S1 to M-S8, each rule and its result)

Done 2026-11-12 (record `s-task0.txt` and `task-0-report.md`, sha256 prefixes `625178ee` and `a0a0c366`; `.superpowers/sdd/` is git-ignored, so the numbers are in Amendment 1's "What was checked"). M-S1, M-S3, M-S4, M-S6, M-S7 held; M-S2 held in its blindness half and not its honoured half; M-S5's baseline carries LCB22 (item 163 already names it); M-S8's estimate straddles 200 only with the probes counted (ruling 14). The control counts: Infrastructure whole 229 passed and 1 failed (LCB22) of 230, twice, LCB22 passing alone 3 of 3; `HarnessRehearsalTests` 1 of 1; the replay drill 2 of 2; Api `StoreFaultRoleTests` 5 of 5, `HeadsSignedUnattendedTests` 21 of 21; the closed fuzz 8 of 8 in 25 m 45 s; `check-spec` clean and `falsify-spec-checks` all four checks red.

### Task 1: the errata and the probes (red text, verbatim)

G69 pasted after G67 and its index row after R11.74 `(rev.)`, so as not to touch lines #151 edits (the first attempt to place it conflicted and GitButler refused). **Ruling 16, measured on the control build:** (a) four seq 0 shapes (retirements with `prev` 0, 1 and 5, and a compromise declaration with `from_index` 0 and `prev` 0): **none honoured**; every one made the victim's key set unreadable (`GET /v1/jwks` 503, `curia keys` exit 8 `statement-mismatch`, a fresh token `401 invalid_client`, an issued token `503 curia/log/unreadable`, `key_status` active, no 7006, `sign-head --if-grown` exit 0 `unchanged`); the cause, by reading `KeyLifecycle`, is that the enrolment's statement-less binding must be the second lifecycle entry and a row at position 0 precedes it. (b) A plain `sign-head` over a seq 0 row exits 0, signs the same tree size as before the plant, no log entry names the row and no proof exists for it. Neither is a stop condition; finding 3 carries both. **Red on the unmodified tree, as predicted unless said:** SEQ1 (four forms, `No exception was thrown`; its identity-rule form green, the named control); SEQ2 (`seq` listed as insertable); SEQ3 (`db/0006` does not exist); SEQ4 (`seq` among the isolated schema's columns); SEQ5 (the role named `seq` at rest, after `insert-off` and after `all-off`; **the predicted text differed**, because with no migration on the tree `status` was green); SEQ6 (at rest `INSERT on events is not granted`; after a revoke the old message; after a table-level `GRANT` an empty string; **the plan's "red at rest" holds only after the migration and before the fixture change**); SEQ7 loud (0 events 7008; its quiet arm green by design); SEQ8 (`no events-position-precheck block in README.md`); SEQ9 (the predicted text: the -3 form a 500 on token, budget and jwks; the 0 form a fresh token `401 invalid_client`, an issued token `503 curia/log/unreadable`, jwks `503 statement-mismatch`); SEQ10 green (the control). **The ten: nine red on the tree; SEQ10 a control.**

### Task 2: the migration and the fixtures

`db/0006_events_insert_columns.sql` (REVOKE, then the column GRANT, in one file; idempotent), `SchemaMigrations.FileNames`, `CreateIsolatedSchemaAsync` rendering the real migration, the harness's restores and its `status` reading each column, `RoleForumFixture`'s catalogue check. Green after the task: SEQ1 to SEQ6, SEQ9, SEQ10 and the SchemaMigrations tests. Test infrastructure touched beyond the plan's list: `LogCapture.LoggedEvent` gains a `Level` member, `PostgresDatabaseFixture.RoleName`, `RoleForumFixture`'s grant helpers, and an in-memory `IEventsPositionGrant` under `Curia.Api.Tests/Support` (the Api tests cannot see the Application tests' internals).

### Task 3: the start-up detector (or "moved to PR O")

Stayed in the PR (ruling 14: the 200-line bound counts production code). **91 production lines added** (port 14, adapter 38, hosted service 34, `Program.cs` 2, `PostgresAdapters.cs` 3). `EventsSeqWritableCheck` logs EventId 7008 at Error when `has_column_privilege(current_user, ..., 'seq', 'INSERT')` is true and starts; **EventId 7009 at Warning, which the plan did not number, when the question cannot be asked**, and starts. SEQ7 green (loud, quiet and the unchecked arm).

### Task 4: the whole run, the fuzz, the review

The whole run as CI runs it on the tip at Task 4: `preflight --no-api` passed (spec checks, restore, build, screening cost, architecture in Debug, all tests but Api, Rust fmt, clippy and tests, the differential comparison, lint, conformance unchanged); Infrastructure whole **282 passed, 0 failed** (LCB22 passed, no flake; the control's 229 of 230 is not the same count because the branch adds tests and the workspace carries #151); Api whole **1,587 passed, 0 failed**, the closed fuzz included, 40 m 59 s; architecture Release 128 of 128; a scram-authenticated throwaway cluster ran the Api probes, `StoreFaultRoleTests` and `HeadsSignedUnattendedTests` (31 of 31) and the Infrastructure `EventsPositionGrantTests` and `HarnessRehearsalTests` (9 of 9). The README's apply text was also run by hand with `psql` on a scratch database (applied twice, return code 0, the application role then refused). **The Task 5 review** (read-only; spec compliance and code quality; Important findings none) made **eight minors**, each checked against the tree at Task 6c: 1 (no `COPY` or `INSERT ... SELECT` probe) **true**, nothing in `EventsPositionGrantTests.cs` names either; 2 (the identity sequence is not probed, `setval`) **true**; 3 (a later migration's grant is a comment only) **true**, `db/0006:18-19`; 4 (7009 logs the type and not the detail) **true**, `EventsSeqWritableCheck.cs`; 5 (a `NOINHERIT` role with a `SET ROLE` route is not reported) **true by the predicate's definition, and now a sentence in G69's non-property (b)**; 6 (`fault.sh all-off` does not heal an unmigrated database) **true, and now a sentence in `tools/fault-harness/README.md`**; 7 (the pre-check run by a non-owner reads `last_value` as NULL) **true**, the README does not say to run it as the owner, and it fails in the safe direction; 8 (step 1's "stop" is prose) **true**. Minors 1 to 4, 7 and 8 are code, test or the README block SEQ8 pins, and are listed under "Not run, and left open".

### Task 5: falsification FSEQ1 to FSEQ10 (counts: rows, first forms red, the masked)

**10 rows run (the plan's ten), 10 red on the first form, 10 red in some form, masked 0, hung 0, green 0** (`fseq-results.jsonl`, the rows 1 to 10; runner and specs under the git-ignored `.superpowers/keep-tools/`). Rows aimed at code built after the plan (FSEQ5, 7, 8 and 9) had their verbatim lines recorded before patching; no row was re-aimed. FSEQ8's second form (the condition inverted) was not run, its first form being red. After the rows the sha lists matched, `git status --porcelain` was empty, the tree was rebuilt with `--no-incremental` and every named probe ran green. **A test-order dependence found on the way** (SEQ7's three tests failed in `CapturedForum`'s constructor when run alone, `Server hasn't been initialized`) was fixed at Task 6 (commit 7 above).

### Task 6: the rehearsal (controls first), the cold read against its gate, register and docs

**The live run, 4 of 4 legs held**, each on the control build `b348f6a9` first and its result recorded before the leg on the tip (`s-task6-live.txt`):

- **LS1.** Control: the -3 plant makes the victim's `budget`, `ask`, `verify`, `keys` and `jwks` answer 500; the 0 plant makes an issued token `503 curia/log/unreadable`, a fresh token `401 invalid_client`, `keys` exit 8; a plain `sign-head` over the seq 0 row exits 0 (tree size 12 to 13 against 14 rows); the ahead plants (20, 21, 22) make asks 1 to 3 `503 curia/store/unavailable` and asks 4 and 5 succeed, `curia resend` of the three exit 0. Tip: each of the six plants refused `permission denied for table events`; the victim's tokens, budget (used 2), `jwks`, `keys` and `verify` as before the attempt; five honest asks 5 of 5.
- **LS2.** A database of `db/0001` to `db/0005` only, 30 events (3 enrolments making 6 events, 3 posts, 1 log key and 20 signed heads, because the first budget cannot carry 30 posts), 3 agents. Tip: 7008 at the first start; pre-check 0 and 0; apply 27 ms, inside one ask's interval; the loop of `curia ask` 6 of 6 ok across it; post-check `f` and `t`; **the fingerprint (count and SHA-256 of the ordered `event_id`s) unchanged over the rows up to the pre-apply maximum, 30**; no 7008 after the apply restart; 7008 after a `GRANT INSERT ON events` restore and restart; the Forum served throughout. **LS2 was run under its first text, before Amendment 2's prefix rule**, which said "unchanged" without limiting the rows: the loop appended six posts during the run (36 rows after, 6 accepted), so the fingerprint was taken over the prefix, and Amendment 2 ruled the claim to "not lower, equal if the Forum took nothing in between". **The asks did post during the apply: six, one of them overlapping it, and none failed.** The apply is so short that "no ask fails across the apply" rests on one overlapping ask in each of the two runs.
- **LS3.** On a copy, a row planted at position 0 and one at 37 (identity at 30): the README's pre-check reports 1 and 1; the base Forum starts without a word and `sign-head --if-grown` signs tree size 31 over 33 rows (blind to both); on the tip the Forum logs 7008; the runbook says stop and delete nothing, and **nothing was deleted** (33 rows, both planted rows present); a grep of `src`, `db`, `tools` and the README for `DELETE FROM`, `TRUNCATE` and `DROP TABLE` naming `events` found none.
- **LS4.** Control: `insert-off` restored a table-level `INSERT`, `status` exited 0 saying "original", and a probe row at -77 landed. Tip: `status` exit 0 at rest and after each restore (after `insert-on` exit 1), the role's probe `permission denied for table events`, no row at -77, `has_column_privilege` on `seq` false.

Superseded runs, harness faults and not failures of the code: LS1's first pair spent the victim's budget before the plant (so the tip's after-plant ask answered exit 4, `budget spent`); LS2's first base run used near-duplicate loop titles (exit 3, dedupe) and its first tip run read a stale log offset and missed the post-restore 7008. `curia verify` exited 8 throughout because `curia-testis` was not built in the scratch.

**The cold read, two passes.** *First pass (a finding):* three readers of the README block each ran the pre-check first, applied in one transaction as the owner, ran the post-check, said `seq` is not writable and deleted nothing; **none said the event count was unchanged**, because nothing in the README asked for it. Two readers of the 7008 line each said the Forum is running, its role can name `seq` and that `db/0006` is the remedy: pass. The fault was the text's. Amendment 2 ruled it (rulings 17 to 21) and Task 6b wrote it. *Second pass:* three fresh readers on fresh databases (30 events, no plant), the amended block: **3 of 3 passed every clause** (pre-check first; one transaction as the owner; post-check `f` and `t`; the count text before and after quoted, 30 and 30 each time, not lower; nothing deleted). The controller's catalogue check after the readers: each database 30 events, highest position 30, `seq` `INSERT` false, `server_ts` `INSERT` true. Task 6b's falsification: **FSEQ11** (the step-3 count paragraph deleted: red in SEQ8's `Assert.Contains`) and **FSEQ12** (the count block's `count(*)` replaced with `0`: red in SEQ8's `(5, 500)` assertion), both red on the first form, so **12 rows, 12 red on the first form, masked 0** in all; each restored by a plain copy, rebuilt `--no-incremental`, the class green 7 of 7. The whole Infrastructure suite after Task 6b: **282 passed, 0 failed**, LCB22 included, **on the GitButler workspace, which also applies #151's branch** (the count is the workspace's, not this branch's; on the branch alone the suite is 238, the final whole run below).

**Register and docs (Task 6c).** `curia-whitepaper-ERRATA-AND-ADDENDUM.md`: G69's status line and a clause in non-property (b); `IMPLEMENTATION_PLAN.md`: a Current-work paragraph for PR S, a PR-table paragraph, "Next stage", the Beta section's item 10, **D123** (built, not applied), **item 272**, the dated bracket after D121's sentence about the `events` grant, a dated sentence in item 163 (LCB22 failed on the unmodified tree, twice, and did not on the tip), and **traps 245 to 247**; `README.md`: the sentence "Apply `db/*.sql` in order first, and `db/0006` narrows the role's `INSERT`" and a Beta-note bullet (the events-position block is untouched); `tools/fault-harness/README.md`: the column grant, and `all-off` on an unmigrated database. **`CLAUDE.md` is not edited** (#151 edits its status paragraph); its owed clause is carried to PR O, in item 272. First free: D124, D61 item 273, trap 248; errata G68 is PR N's, G70 PR N2's (provisional). The white paper is not edited.

### Deviations, in one list

1. Task 0's M-S2 held in its blindness half and not its honoured half (the shadow row is not honoured as a retirement; it makes the victim's key set unreadable): finding 3 restated (Amendment 1, ruling 12).
2. G69 placed after G67 and its index row after R11.74 `(rev.)`, not at the end, so as not to touch lines #151 edits.
3. EventId 7009 (the check could not be asked) is a number the plan did not give.
4. Test infrastructure touched beyond the plan's list (Task 2 above).
5. SEQ7 depended on test order (Task 5's note); fixed at Task 6.
6. The first cold-read pass was below the gate on "the event count is unchanged", returned to the architect, ruled in Amendment 2 and re-run: three readers passed. LS2 ran under the text before the prefix rule (Task 6).
7. FSEQ11 and FSEQ12 are rows the plan did not have.
8. SEQ8 asserts the parts of the README's apply text and runs the equivalent in process; the shell text itself was run by hand once (Task 4).
9. LS2's 30 events are not 30 posts (the first budget cannot carry them); see LS2.
10. The Baseline count for Infrastructure is the control's 230 on the unmodified tree, not recomputed on `origin/main`, the workspace carrying #151.

### Gates

- `preflight --no-api` passed at Task 4 (the list above); Infrastructure whole 282 of 282 at Task 4 and again at Task 6b; Api whole 1,587 passed, 0 failed, 40 m 59 s (closed fuzz included); architecture Release 128 of 128 and Debug through preflight (D16).
- Falsification: FSEQ1 to FSEQ12, 12 of 12 red on the first form, none masked.
- Live run: LS1 to LS4, 4 of 4; the cold read: pass two, 3 of 3 (and the 7008 line, 2 of 2).
- Task 6c: `check-spec.py`, `falsify-spec-checks.py` and SEQ2 (which parses the errata) were run after the docs were written; their last lines are in `task-6c-report.md`.
- **The final whole run** (the controller's, once, on a `git archive` export of the branch at `ab10b0ef`, outside the workspace, `.superpowers/keep-tools/whole-at.sh`): locked restore exit 0; Release build 0 warnings, 0 errors; Application 695/695, Architecture 124/124 (and 124/124 in Debug), AuthN 140/140, Canon.Sodium 32/32, Canon 329/329, Client 1,640/1,640, Domain.Primitives 64/64, Domain 1,043/1,043, **Infrastructure 238/238 (LCB22 passed)**, Mcp 958 passed and 4 skipped of 962; `cargo fmt --check`, `clippy -D warnings` and `cargo test` exit 0; the differential comparison `divergenceClassCount` 0; `check-spec` clean and `falsify-spec-checks` all 4 red; `conformance/` unchanged. **Api 1,576 of 1,577 (42 m 39 s): one failure, `LateCommitEndToEndTests.R8_86_LCB18_AGuardRefusalChangesNothingAndLogsOnceWithoutADigest`**, every clause as expected except `vector_check_due_at "2026-09-18T13:00:01.0000000+00:00" before and "2026-09-18T13:00:01.0000000+00:00" after` (expected `null` both times): a check was already owed in the class's shared `LateCommitForumFixture` before the test acted, the same before and after, so the refusal itself moved nothing. It then passed **alone twice and in its class 16 of 16** on the same build. No file this PR changes is in that fixture's path (`LateCommitForumFixture` and `LateCommitSeams.cs` are untouched; `git diff --name-only origin/main` lists 21 files). Joined to item 163 (LCB18 is already there, from #149's CI, by the catch-up's shared state) under its rule: re-run alone, recorded; a second occurrence in a PR's CI promotes it. **The absence list** (Task 7, `git diff --name-only origin/main...the-log-assigns-its-own-positions`): no `.github/`, no `Directory.Packages.props`, no `packages.lock.json`, no csproj, no `Curia.sln`, no `PostgresEventStore.cs`; `db/0006_events_insert_columns.sql` is the only file under `db/`; `CLAUDE.md` not touched (carried to PR O, item 272).

### Not run, and left open

- **Review minors 1 to 4** (`COPY` and `INSERT ... SELECT` probes; a `setval` probe on the identity sequence; SEQ2 asserting that every non-`seq` column is insertable; 7009 logging the exception's type name and SQLSTATE) and **7 and 8** (one README sentence saying to run the pre-check as the owner; a guard on the apply so step 2 refuses while step 1's counts are non-zero): left open, the README block being pinned by SEQ8 and the rest being a test or code change after the review.
- **The migration was not applied to the hosted database**, and nothing here does it: the owner's step (D123 stays "built, not applied").
- **A row already planted** was not found or removed (none is in any database this run used); the pre-check finds one and the architect decides.
- **LS2's "no ask fails across the apply"** rests on one ask overlapping a 27 to 32 ms apply, in each of two runs.
- **A restore from a backup that predates `db/0006`** was simulated with `GRANT`, not `pg_restore`; behaviour under a pooler was not run.
- **The head signer's own credential** (item 257) and a `NOINHERIT` role are outside the start-up check (item 272).
- **`CLAUDE.md`'s clause** (the application role names no `seq`; the fixtures render `db/0006`) is carried to PR O (item 272).

The PR is #___, on `main` at ________.
