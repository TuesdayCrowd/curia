# Fault harness

Scripts that put a Forum's database through the faults the tenth exercise used: a vector index that cannot be written, an event log that
cannot be written or read, and a commit that lands while its answer is lost. They are for a **throwaway** database and application role
you create for the purpose. Nothing here touches a server setting, restarts anything, or ends a backend: a fault is a `REVOKE`/`GRANT`
on the named tables for the named role, or a mode file read by the proxy.

## Safety rules

- `fault.sh` and `proxy.sh` refuse (exit 2, nothing touched) unless **both** `FH_DB` and `FH_ROLE` are set. The read-only scripts
  (`mark.sh`, `counts.sh`, `census.sh`) need `FH_DB` and apply the database rule.
- The database name must match `curia_[a-z0-9_]+`. It is refused if it matches `curia_e2e_*`, is `postgres`, or is a template; and
  `current_database()` on the connection must equal `FH_DB`.
- The role's name must begin with `FH_DB` (a `curia_*` database on a shared server is never touched with an unrelated role). The role
  must be a plain lowercase identifier, must already exist, must not be `postgres` or `pg_*`, and must not be a superuser or the
  admin session's own user.
- SQL issued: `REVOKE`/`GRANT` of `INSERT`/`UPDATE`/`SELECT` on `events` and `post_embeddings` to `FH_ROLE`, and reads. `psql` is the admin
  connection (the standard `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`), never through the proxy.
- `proxy.sh stop` signals the pid in the pid file only if that process's command line is `pgproxy.py`. The state directory is created
  with mode 700.
- `fault.sh status` exits 0 only when the grants and the proxy mode are the originals; run `fault.sh all-off` before you leave.

## Configuration (environment only)

| Variable | Meaning | Default |
|---|---|---|
| `FH_DB` | the throwaway database | none (required) |
| `FH_ROLE` | the Forum's application role in it | none (required by `fault.sh`, `proxy.sh`) |
| `FH_DIR` | state: `pgproxy.mode`, `pgproxy.pid`, `pgproxy.log`, `faults.log`, `marks/` | `${TMPDIR:-/tmp}/curia-fault-harness-<FH_DB>` |
| `FH_PROXY_PORT` | proxy listen port (127.0.0.1) | `5433` |
| `FH_PG_HOST`, `FH_PG_PORT` | where the proxy forwards | `127.0.0.1`, `5432` |
| `FH_FORUMLOG` | the Forum's console log (`mark.sh`, `census.sh`) | unset |

Known behaviours, documented and not changed: after `proxy.sh stop` and `start` the Forum's pooled connections are dead and it answers
`503` until it is restarted, so restart the Forum with the proxy. `common.sh` calls `psql` with no database, so the admin connection needs
the default database to exist or `PGDATABASE` set.

Point the Forum's connection string at the proxy (`Host=127.0.0.1;Port=$FH_PROXY_PORT;...`); use the real port for the operator tool and psql.
To build the database, render `db/*.sql` with the app role name substituted for `__CURIA_APP_ROLE__` and apply them in order as an
admin (pgvector is needed by `db/0003`).

## Scripts

- `fault.sh status | vector-on | vector-off | insert-on | insert-off | log-on | log-off | cut-on <p> | cut-off | all-off`
  - `vector-on`: revoke `INSERT, UPDATE` on `post_embeddings` (a post commits, its vector cannot be stored). `insert-on`: revoke `INSERT` on
    `events` (a write is refused before it commits). `log-on`: revoke `SELECT` on `events` (the log cannot be read). `*-off` grant back.
  - `cut-on <p>` (0 < p <= 1): proxy mode `cutcommit:<p>`; `cut-off`: back to `passthru`. `all-off` restores every grant and the mode, then
    prints `status`.
  - Each action takes a mark (`v-open`/`v-close`, `c2-open`/`c2-close`, `l-open`/`l-close`, `c1-open`/`c1-close`) and appends to `faults.log`;
    `status` prints each open window's age and warns past 8 minutes.
- `proxy.sh start | stop | status | mode <mode> | p <0..1>`: the fault proxy (`pgproxy.py`, run with `python3 -I`). `p 0` is `passthru`.
  Modes, re-read per packet from `$FH_DIR/pgproxy.mode`:
  - `passthru`: transparent.
  - `cutcommit[:p]`: after a connection has sent `INSERT INTO "public".events`, its simple-query `COMMIT` is forwarded, Postgres is given 60 ms,
    both sockets close and the answer is muted: committed, and the client sees a broken connection.
  - `dropcancel`: a cancel request is dropped, so a client timeout cannot cancel the running query. The proxy serves **plaintext only**: it
    answers an 8-byte SSLRequest (and a GSSENCRequest) itself with `N`, without forwarding it, and treats the next packet as the connection's
    first, so Npgsql's default `SSL Mode=Prefer` falls back to plaintext and its cancel (the next 16-byte packet, code 80877102) is
    recognised and logged `dropped cancel request`. `SSL Mode=Require` is unsupported through the proxy.
  - `delaycommit:<s>`: the `COMMIT` is held `s` seconds, then forwarded even if the client has gone (a late commit). **It holds every
    simple-query `COMMIT`, not only one after an `INSERT INTO "public".events`** (the rule `cutcommit` uses), so a token mint or a nonce
    write inside the window is held too: warm the token (one call that mints it) before the window opens.
- `mark.sh <name>`: `marks/<name>` holds `epoch_ms`, `seq` (max event seq), `forum_log_lines`, `pgproxy_log_lines`.
- `counts.sh [mark]`: events since the mark by actor and type; **unindexed**; `answer.accepted` since the mark and in 24 hours; posts per
  author in 24 hours; flags; digests stored twice.
- `census.sh [mark]`: from `FH_FORUMLOG` since the mark's line: `Request finished` by status, body-less 5xx, and the counts of the log events
  StoreFault, UnhandledFault, VectorIndexPending, VectorIndexCaughtUp, VectorIndexRetryFailed, VectorIndexCatchUpFailed.
- `common.sh`: the shared configuration and guard (sourced, not run).

## The unindexed count

`counts.sh` counts `post.accepted` events whose digest is not in `post_embeddings`, **restricted to the discussion kinds** the model places
in the vector index: `question`, `answer`, `finding`, `comment`, `revision` -- `PostKinds.IsDiscussion` in
`src/Curia.Domain/Content/PostKind.cs`. A vote and a verification are logged and never indexed, so counting them (as the exercise's
version did, then subtracting a baseline) overstated the gap. The kind is read as `payload->>'kind'`, the payload's own top-level `kind`
member, with no `jsonb` cast (a cast of the signed `canonical` text fails on a stored post whose text holds a U+0000, as the exercise's
dump does). No baseline is subtracted: an empty gap is `unindexed=0`.
