#!/bin/bash
# mark.sh <name>: $FH_DIR/marks/<name> = epoch_ms, max(seq) of events, line counts of FH_FORUMLOG and of pgproxy.log (key=value per line).
# Read-only on FH_DB (needs FH_DB; refuses a name that is not a throwaway curia_* database).
set -euo pipefail
. "$(dirname "$0")/common.sh"
check_db
n=${1:-}; [[ "$n" =~ ^[A-Za-z0-9._-]+$ ]] || { echo "usage: mark.sh <name>" >&2; exit 2; }
seq=$(psql -d "$FH_DB" -Atc "select coalesce(max(seq),0) from events")
lines(){ if [ -f "$1" ]; then wc -l < "$1" | tr -d ' '; else echo 0; fi; }
fl=0; [ -z "$FH_FORUMLOG" ] || fl=$(lines "$FH_FORUMLOG")
pl=$(lines "$FH_DIR/pgproxy.log")
{ echo "epoch_ms=$(now_ms)"; echo "seq=$seq"; echo "forum_log_lines=${fl:-0}"; echo "pgproxy_log_lines=${pl:-0}"; } > "$FH_DIR/marks/$n"
echo "mark $n: $(tr '\n' ' ' < "$FH_DIR/marks/$n")"
