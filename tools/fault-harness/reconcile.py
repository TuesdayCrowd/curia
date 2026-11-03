#!/usr/bin/env python3
"""reconcile.py <digests-file> [mark]

For each digest in the file (one `sha256:<64 hex>` per line; blank lines and lines starting with # are skipped) print one line:
stored (every post.accepted event with that digest: sequence, aggregate id, server_ts) or absent. Read by SQL on FH_DB, as the admin
connection (the standard PG* variables), and attributed by DIGEST ONLY: never by a time window and never by server_ts, which is the
INSERT's instant taken after the lock is acquired (a request that queued behind a stalled commit has a server_ts after the first
landed). With a mark (a file in $FH_DIR/marks written by mark.sh) an event at or before the mark's sequence is not counted.
Prints a total line. Exit 0 when it ran (absent is information, not failure), 2 for a bad argument or a database the harness refuses.
Run it as `python3 -I reconcile.py ...`; the shebang names the interpreter only.
"""
import os, re, subprocess, sys

def refuse(msg):
    print("refusing: " + msg, file=sys.stderr); sys.exit(2)

def main(argv):
    if not 1 <= len(argv) <= 2: refuse("usage: reconcile.py <digests-file> [mark]")
    db = os.environ.get("FH_DB", "")
    if not db: refuse("FH_DB is not set (name the throwaway database)")
    if not re.fullmatch(r"curia_[a-z0-9_]+", db): refuse("database '%s' does not match curia_[a-z0-9_]+" % db)
    if db.startswith("curia_e2e_"): refuse("database '%s' is a curia_e2e_* database" % db)
    cur = subprocess.run(["psql", "-d", db, "-Atc", "select current_database()"], capture_output=True, text=True)
    if cur.returncode != 0 or cur.stdout.strip() != db: refuse("cannot connect to '%s'" % db)
    state = os.environ.get("FH_DIR") or os.path.join(os.environ.get("TMPDIR", "/tmp"), "curia-fault-harness-" + db)
    seq0 = 0
    if len(argv) == 2:
        if not re.fullmatch(r"[A-Za-z0-9._-]+", argv[1]): refuse("bad mark name")
        mf = os.path.join(state, "marks", argv[1])
        if not os.path.isfile(mf): refuse("no mark " + argv[1])
        for l in open(mf):
            if l.startswith("seq="): seq0 = int(l.split("=", 1)[1])
    try:
        lines = [l.strip() for l in open(argv[0])]
    except OSError as e:
        refuse("cannot read %s: %s" % (argv[0], e.strerror))
    digs = []
    for l in lines:
        if not l or l.startswith("#"): continue
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", l): refuse("not a digest: " + l)
        digs.append(l)
    stored = absent = twice = 0
    for d in digs:
        q = ("select seq || ' ' || aggregate_id || ' ' || server_ts from events where event_type='post.accepted' "
             "and payload->>'digest'='%s' and seq > %d order by seq" % (d, seq0))
        r = subprocess.run(["psql", "-d", db, "-At", "-c", q], capture_output=True, text=True)
        if r.returncode != 0: refuse("query failed: " + r.stderr.strip())
        rows = [x for x in r.stdout.strip().split("\n") if x]
        if not rows:
            absent += 1; print("%s absent" % d)
        else:
            stored += 1; twice += 1 if len(rows) > 1 else 0
            print("%s stored x%d: %s" % (d, len(rows), "; ".join("seq %s" % x for x in rows)))
    print("digests: %d, stored: %d (of which stored more than once: %d), absent: %d" % (len(digs), stored, twice, absent))

main(sys.argv[1:])
