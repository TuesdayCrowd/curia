#!/bin/bash
# census.sh [markname]: from the Forum's console log (FH_FORUMLOG, required) since the mark's line: `Request finished` lines by HTTP
# status, among 5xx how many carry content type `-` (body-less), and the log events StoreFault, UnhandledFault, VectorIndexPending,
# VectorIndexCaughtUp, VectorIndexRetryFailed, VectorIndexCatchUpFailed. The console logger prints message text, not the EventName,
# so each event is matched by its message (src: StoreFaults.cs, VectorIndexReceipt.cs). A line looks like:
#   Request finished HTTP/1.1 GET http://127.0.0.1:5099/v1/posts/<id> - 200 - application/json;+charset=utf-8 69.49ms
set -euo pipefail
. "$(dirname "$0")/common.sh"
check_db
[ -n "$FH_FORUMLOG" ] && [ -f "$FH_FORUMLOG" ] || { echo "FH_FORUMLOG must name the Forum's log file" >&2; exit 2; }
FROM=0
if [ -n "${1:-}" ]; then
  [ -f "$FH_DIR/marks/$1" ] || { echo "no mark $1" >&2; exit 2; }
  FROM=$(grep '^forum_log_lines=' "$FH_DIR/marks/$1" | cut -d= -f2)
fi
exec python3 -I - "$FH_FORUMLOG" "$FROM" <<'PY'
import sys, re, collections
path, frm = sys.argv[1], int(sys.argv[2])
lines = open(path, errors="replace").read().split("\n")[frm:]
rf = re.compile(r"Request finished \S+ (\S+) (\S+) - (\d{3}) (\S+) (\S+) ([0-9.]+)ms")
by = collections.Counter(); bodyless = []; sample = None
for l in lines:
    m = rf.search(l)
    if not m: continue
    by[m.group(3)] += 1
    if sample is None: sample = l.strip()
    if m.group(3).startswith("5") and m.group(5) == "-": bodyless.append(l.strip())
print("Request finished by status:", dict(sorted(by.items())) or "none")
print("5xx total:", sum(v for k, v in by.items() if k.startswith("5")), " 500:", by.get("500", 0), " body-less 5xx (content type -):", len(bodyless))
for b in bodyless[:5]: print("  body-less:", b)
txt = "\n".join(lines)
EV = [("StoreFault", "A store fault was served as"), ("UnhandledFault", "An unclassified exception was served as 500"),
      ("VectorIndexPending", "and its vector is not stored; it is pending"), ("VectorIndexCaughtUp", "The catch-up stored the vector of"),
      ("VectorIndexRetryFailed", "The catch-up could not store the vector of post"), ("VectorIndexCatchUpFailed", "A catch-up pass failed")]
for ev, msg in EV:
    print("%-26s %d" % (ev, txt.count(msg)))
if sample: print("sample:", sample)
PY
