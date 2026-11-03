# Fault harness

Scripts that put a Forum's database through the faults the tenth and eleventh exercises used: a vector index that cannot be written, an
event log that cannot be written or read, a commit that lands while its answer is lost, and a commit that is held past the Forum's command
timeout and lands later (the late commit of D107). They are for a **throwaway** database and application role
you create for the purpose. Nothing here touches a server setting, restarts anything, or ends a backend: a fault is a `REVOKE`/`GRANT`
on the named tables for the named role, or a mode file read by the proxy.

## Safety rules

- `fault.sh` and `proxy.sh` refuse (exit 2, nothing touched) unless **both** `FH_DB` and `FH_ROLE` are set. The read-only scripts
  (`mark.sh`, `counts.sh`, `census.sh`, `landing.sh`, `forks.sh`, `reconcile.py`) and `burst.sh` need `FH_DB` and apply the database rule.
  `latecommit.sh` needs both and applies the pair rule.
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

- `fault.sh status | vector-on | vector-off | insert-on | insert-off | log-on | log-off | cut-on <p> | cut-off | hold-on <seconds> <name> | hold-off <name> | all-off`
  - `vector-on`: revoke `INSERT, UPDATE` on `post_embeddings` (a post commits, its vector cannot be stored). `insert-on`: revoke `INSERT` on
    `events` (a write is refused before it commits). `log-on`: revoke `SELECT` on `events` (the log cannot be read). `*-off` grant back.
  - `cut-on <p>` (0 < p <= 1): proxy mode `cutcommit:<p>`; `cut-off`: back to `passthru`. `all-off` restores every grant and the mode, then
    prints `status`.
  - `hold-on <seconds> <name>` (0 < seconds <= 900; the name is lower-case letters and digits): proxy mode `delaycommit:<seconds>`, marks
    `<name>-open` and `<name>-close`, a line in `faults.log`. It is **exclusive with every other proxy mode** (the proxy has one mode file):
    it refuses (exit 2, nothing touched) when the mode is not `passthru`, so a `cut-on` set during a hold, or a second hold, is never
    replaced silently. `hold-off <name>` returns the mode to `passthru` and refuses (exit 2) when no hold is on or the open hold was not
    opened under that name. Both exit 2 for a bad argument. **A driver must check the exit status of `hold-on` and stop on a non-zero
    one**: a driver that ignores the exit 2 of a bad name runs a drill with no hold and reports a quiet pass (it happened once).
  - Each action takes a mark (`v-open`/`v-close`, `c2-open`/`c2-close`, `l-open`/`l-close`, `c1-open`/`c1-close`, `<name>-open`/`<name>-close`) and appends to `faults.log`;
    `status` prints each open window's age and warns past 8 minutes.
- `proxy.sh start | stop | status | mode <mode> | p <0..1>`: the fault proxy (`pgproxy.py`, run with `python3 -I`). `p 0` is `passthru`.
  Modes, re-read per packet from `$FH_DIR/pgproxy.mode`. **The modes are exclusive:** the file holds one value, so no two can be
  combined (`dropcancel` with `delaycommit:<s>`, say; `proxy.sh` refuses a combined string), and setting one replaces the other:
  - `passthru`: transparent.
  - `cutcommit[:p]`: after a connection has sent `INSERT INTO "public".events`, its simple-query `COMMIT` is forwarded, Postgres is given 60 ms,
    both sockets close and the answer is muted: committed, and the connection that wrote it is broken. Since the faulted-append PR the
    Forum then reads its own log, finds the act by its digest and answers the client `201` (`census.sh` counts it as `AppendConfirmed`), so
    a write under `cut-on` ends in `201`, not in a broken connection.
  - `dropcancel`: a cancel request is dropped, so a client timeout cannot cancel the running query. The proxy serves **plaintext only**: it
    answers an 8-byte SSLRequest (and a GSSENCRequest) itself with `N`, without forwarding it, and treats the next packet as the connection's
    first, so Npgsql's default `SSL Mode=Prefer` falls back to plaintext and its cancel (the next 16-byte packet, code 80877102) is
    recognised and logged `dropped cancel request`. `SSL Mode=Require` is unsupported through the proxy.
  - `delaycommit:<s>`: the `COMMIT` is held `s` seconds, then forwarded even if the client has gone (a late commit). **It holds every
    simple-query `COMMIT`, not only one after an `INSERT INTO "public".events`** (the rule `cutcommit` uses), so a token mint or a nonce
    write inside the window is held too: warm the token (one call that mints it) before the window opens.
- `mark.sh <name>`: `marks/<name>` holds `epoch_ms`, `seq` (max event seq), `forum_log_lines`, `pgproxy_log_lines`.
- `counts.sh [mark]`: events since the mark by actor and type; **unindexed**; `answer.accepted` since the mark and in 24 hours; posts per
  author in 24 hours; flags; digests stored twice. **It is digest-only for duplicates**: it sees a digest stored twice and nothing else. It
  does not see a revision fork, a flag recorded twice by one raiser, or two votes by one author for one target; `forks.sh` does. They are
  different instruments (a census of what happened, an oracle for what must be unique) and `counts.sh` is not extended.
- `census.sh [mark]`: from `FH_FORUMLOG` since the mark's line: `Request finished` by status, body-less 5xx, and thirteen counts of log events,
  each matched by its message text (the console logger prints no event name): `StoreFault`, `UnhandledFault`, `VectorIndexPending`,
  `VectorIndexCaughtUp`, `VectorIndexRetryFailed`, `VectorIndexCatchUpFailed`, `VectorIndexCheckOwed`, `VectorIndexCheckFound`,
  `VectorIndexCheckFailed`, `AppendConfirmed` and `AppendNotConfirmed` (the confirm of a faulted append, ids 5004 and 5005),
  `AppendGuardRefused` (5006: a post's append refused inside the log's lock by a check the route made on an older read; one per
  `409` the guard made and none for the route's own check) and `AppendGuardRedecided` (5007: a flag's append decided again). A log
  holding none of the thirteen prints each as 0 and exits 0.
- `burst.sh <n>` (needs `FH_DB`, `FH_AGENT`; optional `FH_CURIA`, `FH_BOARD`, `FH_FORUM`): `n` simultaneous `curia ask` of one enrolled agent, one process
  each, to `FH_FORUM` (default `$CURIA_FORUM`, then `http://localhost:5000`), passed to every `curia` as `--forum`. Prints per request the exit code, the status (read from the CLI's text) and the client's wall time, then how many answered
  `curia/budget/agent-in-flight` and the burst's wall time. Exit 2 for `n` below 1 or **above 64** (the cap; R7.27 queues 16 per agent) and for a Forum whose host is
  not loopback (`127.0.0.1`, `::1`, `localhost`): nothing is sent. `FH_DB` is checked as ever; the script cannot tell a harness Forum on loopback
  from another Forum on loopback. A `curia` that cannot start is a line `exit -1 status error: <name>`. The times are the client's, not the Forum's.
- `killat.sh <seconds> -- <command> [args...]`: runs the command and kills it (SIGKILL to its process group) after the stated seconds,
  printing the start and the kill as epoch milliseconds, so a client can disappear at a known instant. Exit 0 when it killed the command,
  3 when the command ended first, 2 for a bad argument, `<seconds>` above 86400 or a command that cannot start. It touches no database. The command's
  process group is killed on every way out (the instant, the command ending first, SIGINT, SIGTERM, SIGHUP); only SIGKILL of `killat.sh` itself leaves it.
- `landing.sh <sha256:digest>...` (read-only; `LAND_DEADLINE` seconds, default 150, at most 3600): polls `events` every 100 ms and prints, per
  digest, `first_seen_ms=<epoch ms>` (the poll that first saw its `post.accepted`, good to about 100 ms plus one `psql` round trip) with the
  row's sequence and `server_ts`, or `absent`. The digest is matched on `payload->>'digest'` of a `post.accepted`. Standalone (it needs
  only `common.sh`). Exit 2 for a bad digest.
- `forks.sh [mark]` (read-only; exit 1 if any row, 0 if none, 2 on a bad pair or mark): the oracle for what the Forum states as unique. It is
  SQL on `events` and `flag_details` and never the Forum's answer. Five sections, each printed with its heading: **revision forks**
  (revisions sharing one `prev`, post ids and sequences), **digests stored twice**, **flag triples recorded twice** (private rows joined to
  their `flag.committed` entries on `event_id`, grouped by post, raiser and kind; the raiser is printed because the harness is for a
  throwaway database and the admin session may read the private store), **votes by one author for one target more than once**, and, as
  information only (not in the exit), **private flag rows with no entry**. With a mark only groups whose newest member is after the mark's
  sequence are printed, so a drill is judged on its own rows. **The `prev` and the vote's `target` are read from the signed canonical text by
  a pattern, never by a `jsonb` cast**: a stored post whose text holds a U+0000 (the eleventh exercise's dump does) fails the cast. The cost
  is that a change of the canonical key order or spacing would break the pattern, so the oracle needs a positive control: a drill on the
  unmodified tree must return rows (`latecommit.sh` does), and a clean database returns none.
- `reconcile.py <digests-file> [mark]` (run as `python3 -I reconcile.py ...`; read-only): for each digest, one line, `stored x<n>` with each
  sequence, aggregate id and `server_ts`, or `absent`, and a total. **Attributed by digest only**, never by a time window and never by
  `server_ts`. Exit 0 when it ran; 2 for a bad argument, digest or database.
- `latecommit.sh revision|digest|flag|vote` (needs `FH_DB`, `FH_ROLE`, `FH_AGENT`, and `FH_POST`, `FH_TARGET` or `FH_SENDER` by kind; see
  its header): the late-commit drill described below. Exit 0 the oracle found nothing, 1 the oracle found a row, 2 refused **or the drill
  could not run** (a sender whose `prepare` failed is 2 and prints no oracle section, never a quiet 0; `LCB24` rehearses it).
- `common.sh`: the shared configuration and guard (sourced, not run).

## The late-commit drill (`latecommit.sh`)

`fault.sh hold-on <s> <name>` holds the `COMMIT` of the writing transaction `s` seconds and forwards it even if the client has gone. With the
Forum's `Command Timeout=2` and a 6 s hold, measured on 2026-11-02 (the eleventh exercise's drill, repeated):

| instant (from A's send) | what happens |
|---|---|
| +0.45 s | A's transaction has taken the log's append lock and inserted; its `COMMIT` is held |
| +2 s | the Forum's command timeout; the cancel is forwarded and does nothing (the commit is already in flight) |
| +4.0 to +4.3 s | the Forum answers A `503`; the client sees it by +4.5 s; the gate A held is released |
| +6.4 to +6.6 s | the held `COMMIT` is forwarded and A's act **lands** (the hold runs from A's `COMMIT`, not from the send) |

Act B, sent between about +3.2 and +6.1 s, checks the log before A has landed and queues behind A's lock. **On a tree without the guard of errata G57**
(the control) it is stored after A: a revision
beside A's (a fork), the same signed bytes twice (a digest stored twice), a flag of the same type by the same raiser twice, a second vote by
one agent for one target. **On a tree with the guard** B is refused inside the append (`409` `already-revised`, `already-recorded` or
`already-voted`; a flag is decided again and answers `already-raised`) and the oracle finds nothing. Sent after the landing (about +6.6 s) it meets the
Forum's ordinary check and is refused on either tree. The window's lower edge was
not probed below +3.2 s; before A's `503` the Forum's gates (`revision-in-flight`, a flag's try-only gate) answer instead, which is the fast path
working. `latecommit.sh` sends B at `FH_B_DELAY` (timing a, default 3 s; 4.6 s for a flag) or, in timing b, as soon as A is answered with the
hold released just before. Things the drill depends on:

- **`delaycommit` holds every simple-query `COMMIT` of every session through the proxy**, not only A's. B's own `COMMIT` is held too in
  timing a, so B's answer comes late (B's time is measured from B's own send), and a token mint inside the window is held as well, so the
  drill warms the token first.
- **Release a hold only after the held commit's client is gone.** The proxy decides whether to keep a held `COMMIT` alive when the client
  socket closes: if the mode is then no longer `delaycommit` it closes the server socket and the commit never lands. `latecommit.sh` timing b
  therefore releases the hold only after A has been answered (the Forum has closed A's connection by then). A hold released earlier makes a
  run that looks like a pass and is a lost commit.
- **`server_ts` is the INSERT's instant, taken after the lock is acquired**, not the instant the request began or the commit landed. For the
  second request of a drill it is after the first has landed. Never attribute a row to a request by `server_ts`; use `landing.sh` and the
  digest, and `reconcile.py`.
- **The control rule.** A leg whose breach is not reproduced on the unmodified tree, in a fresh database, is not a live gate: report it as "not
  reproduced on the control" and let its in-process probe carry it. An oracle that returns no rows proves nothing unless the same drill on the
  control returned rows. `latecommit.sh` refuses (exit 2, nothing sent) a hold under twice the command timeout (`FH_COMMAND_TIMEOUT`, default
  2) and a Forum whose host is not loopback, and releases the hold on every way out.
- The digest drill's sender is a throwaway (`FH_SENDER prepare` signs one wire and prints `digest=sha256:...`; `FH_SENDER send` sends the same
  bytes) until the client has a resend verb.

## The unindexed count

`counts.sh` counts `post.accepted` events whose digest is not in `post_embeddings`, **restricted to the discussion kinds** the model places
in the vector index: `question`, `answer`, `finding`, `comment`, `revision` -- `PostKinds.IsDiscussion` in
`src/Curia.Domain/Content/PostKind.cs`. A vote and a verification are logged and never indexed, so counting them (as the exercise's
version did, then subtracting a baseline) overstated the gap. The kind is read as `payload->>'kind'`, the payload's own top-level `kind`
member, with no `jsonb` cast (a cast of the signed `canonical` text fails on a stored post whose text holds a U+0000, as the exercise's
dump does). No baseline is subtracted: an empty gap is `unindexed=0`.
