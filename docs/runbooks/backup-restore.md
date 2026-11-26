# Runbook: backup, restore and the restore post-check (errata G69, G83, R11.6, R11.9)

Audience: the Forum's operator. The events table is the system of record and every read model is rebuilt from it by replay (R11.9),
so a backup of the database is a backup of the Forum. **What the dump does not hold, and must be backed up on its own**: the issuer's
signing key (`CURIA_ISSUER_SIGNING_KEY_PEM`) and the operator's three keys (`docs/runbooks/operator-keys.md`). The model directory is
fetched by digest and is not backed up.

## Backup

`$DUMP_FILE` is a file name of the operator's choosing, one per backup (a date in it is the operator's convention, not this document's).

```bash
pg_dump -Fc --no-owner -f "$DUMP_FILE" "$SOURCE_URL"
```

Keep the dump encrypted and off the host, and keep **the latest head's JSON** (`GET /v1/log/head`) with it, read before the dump: the
post-check below needs a head signed before the dump to hold the restored log to.

## Restore

**Roles are cluster-level; `pg_restore` into a cluster that lacks the application role fails its grants.** Create the role first, with
the name the Forum's connection string uses, then restore into an empty database:

```bash
createdb "$TARGET_DB"
pg_restore --no-owner -d "$TARGET_URL" "$DUMP_FILE"
```

The restore's exit code does not say the grants came back. **The post-check does.** A restore from a backup that predates db/0006
re-opens the application role's `INSERT` on `events.seq`, and the Forum's log says so at every start (EventId 7008): re-apply
`db/0006_events_insert_columns.sql` as the table's owner, and run the post-check again.

## The restore post-check

Four steps, in this order. **Any failure is a stop**: keep the output and do not start the Forum against the database.

**1. The grants.** One block of rows, each `name, got, want`; **every row must read `got = want`**. The role is written as the literal
`curia_app`; replace it with the application role's name by one textual substitution (the test that runs this block does exactly that).
The grants are column-level under db/0006, so a table-level `INSERT` privilege is false with and without the migration and
distinguishes nothing: the rows that decide are the column ones and `has_any_column_privilege` for `UPDATE`, which sees a column-level
grant a table-level test does not. The last two rows are **positive controls**: a role restored with no grants at all would answer false on
every negative row and read as narrowed, and these two are what tell narrowed from stripped.

```sql
SELECT 'insert on events, whole table' AS name, has_table_privilege('curia_app', 'events', 'INSERT') AS got, false AS want
UNION ALL SELECT 'insert on events.seq', has_column_privilege('curia_app', 'events', 'seq', 'INSERT'), false
UNION ALL SELECT 'update on any events column', has_any_column_privilege('curia_app', 'events', 'UPDATE'), false
UNION ALL SELECT 'delete on events', has_table_privilege('curia_app', 'events', 'DELETE'), false
UNION ALL SELECT 'insert on events.event_id (the positive control)', has_column_privilege('curia_app', 'events', 'event_id', 'INSERT'), true
UNION ALL SELECT 'select on events.seq', has_column_privilege('curia_app', 'events', 'seq', 'SELECT'), true;
```

**2. The db/0002 tables.** A different fact, so a second statement; it must say `true`:

```sql
SELECT to_regclass('authn_dpop_nonces') IS NOT NULL AND to_regclass('authn_replay') IS NOT NULL;
```

**3. The audit.** `lifecycle-audit` over the restored database must exit 0 (`honoured <h>  skipped 0`). **Exit 7 means the log holds rows the
Forum does not honour: stop.** It cannot see a row planted at a position the application role's grant refuses; that is the Forum's start-up check's.

```bash
CURIA_EVENTS_POSTGRES="$RESTORED_APP_ROLE_CONNECTION" curia-operator lifecycle-audit
```

**4. The log is the log.** Verify one post against a head signed **before** the dump, then sign a head after the restore and verify again:

```bash
curia verify "$POST_ID" --forum https://forum.example
```

The first verification retains the head the Forum serves; the second, after the restore, is held to it by a consistency proof. **A restore
that drops a row the latest head covers is a fork, not a restore**, and the consistency check is what says so: do not announce the
restored Forum, and do not sign a head over it until the owner has read the difference.

## The drill, a developer gate and not a CI test

`tools/deploy/backup-drill.py` runs the whole round trip against a database you name: dump, restore into a fresh database, the row count,
the grants block above, and `lifecycle-audit`. It reports `PASS`, `FAIL` or `NOT CHECKED` per check and exits 8, not 0, when it could not
run: **`pg_dump` absent, or older than the server** (an older client refuses a newer server). CI is not known to have a `pg_dump` of the
server's major, which is why this is a script that says so and not a test that skips. No CI step runs it; run it before you rely on a
backup.

```bash
python3 -I tools/deploy/backup-drill.py --admin-url "$ADMIN_URL" --source-db curia --app-role curia_app
```
