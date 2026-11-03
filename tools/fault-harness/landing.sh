#!/bin/bash
# landing.sh <sha256:digest>...   (read-only on FH_DB; LAND_DEADLINE seconds, default 150)
# Polls events every 100 ms and prints, per digest, the epoch ms of the poll that first saw its post.accepted row
# (`first_seen_ms=<epoch ms>`, good to about 100 ms plus one psql round trip), with the row's sequence and server_ts, or `absent`
# at the deadline. The digest is matched on payload->>'digest' of a post.accepted. server_ts is printed as a record and is NOT the
# landing: it is the INSERT's instant, taken after the lock is acquired (see README, "server_ts"). Appends the same lines to
# $FH_DIR/landing.log. Needs FH_DB (common.sh's rule); exit 2 for a bad argument.
set -euo pipefail
. "$(dirname "$0")/common.sh"
[ $# -ge 1 ] || { echo "usage: landing.sh <sha256:digest>..." >&2; exit 2; }
for d in "$@"; do [[ "$d" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "not a digest: $d" >&2; exit 2; }; done
dl=${LAND_DEADLINE:-150}
python3 -I -c "import sys;v=float(sys.argv[1]);assert 0<v<=3600" "$dl" 2>/dev/null || die "LAND_DEADLINE must be a number of seconds, 0 < s <= 3600"
check_db
exec python3 -I - "$FH_DB" "$dl" "$FH_DIR/landing.log" "$@" <<'PY'
import subprocess, sys, time
db, dl, logf, digs = sys.argv[1], float(sys.argv[2]), sys.argv[3], sys.argv[4:]
inlist = ",".join("'%s'" % d for d in digs)   # every digest was matched against ^sha256:[0-9a-f]{64}$ by the caller
q = "select payload->>'digest', seq, server_ts from events where event_type='post.accepted' and payload->>'digest' in (%s) order by seq" % inlist
seen = {}
t0 = time.time(); start_ms = int(t0 * 1000)
while time.time() - t0 < dl and len(seen) < len(set(digs)):
    out = subprocess.run(["psql", "-d", db, "-At", "-F", "|", "-c", q], capture_output=True, text=True).stdout
    now = int(time.time() * 1000)
    for l in out.strip().split("\n"):
        if l:
            d, seq, ts = l.split("|", 2)
            if d not in seen: seen[d] = (now, seq, ts)
    if len(seen) < len(set(digs)): time.sleep(0.1)
lines = []
for d in digs:
    if d in seen:
        lines.append("%s first_seen_ms=%d seq=%s event_server_ts=%s (polling began %d ms earlier)" % (d, seen[d][0], seen[d][1], seen[d][2], seen[d][0] - start_ms))
    else:
        lines.append("%s absent (deadline %gs)" % (d, dl))
print("\n".join(lines))
with open(logf, "a") as f: f.write("\n".join(lines) + "\n")
PY
