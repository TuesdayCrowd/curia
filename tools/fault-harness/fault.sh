#!/bin/bash
# fault.sh status | vector-on | vector-off | insert-on | insert-off | log-on | log-off | cut-on <p> | cut-off | all-off
# Faults are REVOKE/GRANT on the throwaway database's events and post_embeddings tables for FH_ROLE, plus the proxy's mode file.
# Nothing else: no server setting, no backend termination, no role limit, no restart.
# Needs FH_DB and FH_ROLE (see common.sh); refuses (exit 2, nothing touched) unless they pass check_pair.
set -euo pipefail
. "$(dirname "$0")/common.sh"
check_pair
act=${1:-status}
PSQL="psql -d $FH_DB -v ON_ERROR_STOP=1 -q"
log(){ echo "$(now_ms) $1 $FH_DB" >> "$FH_DIR/faults.log"; }
priv(){ psql -d "$FH_DB" -Atc "select has_table_privilege('$FH_ROLE','public.$1','$2')"; }
mk(){ "$FH_SRC/mark.sh" "$1" > /dev/null; }
privs(){ local s="" p; for p in SELECT INSERT UPDATE DELETE; do s="$s$(priv "$1" $p)"; done; echo "$s"; }
status(){
  local rc=0 open="" mode on off rest last ts age pair
  echo "db=$FH_DB role=$FH_ROLE"
  for t in events post_embeddings; do
    printf "%s:" $t; for p in SELECT INSERT UPDATE DELETE; do printf " %s=%s" $p "$(priv $t $p)"; done; echo
  done
  mode=$(cat "$FH_DIR/pgproxy.mode" 2>/dev/null || echo passthru)
  echo "proxy: mode=$mode $("$FH_SRC/proxy.sh" status 2>&1 | head -1)"
  [ "$(privs events)" = "ttff" ] || { open="$open events(log-or-insert)"; rc=1; }
  [ "$(privs post_embeddings)" = "tttf" ] || { open="$open post_embeddings(vector)"; rc=1; }
  [ "$mode" = "passthru" ] || { open="$open proxy($mode)"; rc=1; }
  # which window was opened, and for how long (from faults.log; the privileges above are the truth)
  for pair in "vector-on:vector-off" "insert-on:insert-off" "log-on:log-off" "cut-on:cut-off"; do
    on=${pair%%:*}; off=${pair#*:}
    last=$(awk -v on="$on" -v off="$off" '$2==on||$2==off{l=$0} END{print l}' "$FH_DIR/faults.log" 2>/dev/null || true)
    case "$last" in *" $on "*) ts=${last%% *}; age=$(( ($(now_ms) - ts) / 1000 )); echo "  open: $on for ${age}s"; [ $age -gt 480 ] && echo "  WARNING over 8 minutes: $on";; esac
  done
  if [ $rc -ne 0 ]; then echo "FAULT OPEN:$open"; else echo "original: events SELECT+INSERT, post_embeddings SELECT+INSERT+UPDATE, proxy passthru"; fi
  return $rc
}
case $act in
status) status; exit $? ;;
vector-on)  mk v-open;  $PSQL -c "REVOKE INSERT, UPDATE ON post_embeddings FROM $FH_ROLE"; log vector-on; echo "vector-on" ;;
vector-off) $PSQL -c "GRANT INSERT, UPDATE ON post_embeddings TO $FH_ROLE"; log vector-off; mk v-close; echo "vector-off" ;;
insert-on)  mk c2-open; $PSQL -c "REVOKE INSERT ON events FROM $FH_ROLE"; log insert-on; echo "insert-on" ;;
insert-off) $PSQL -c "GRANT INSERT ON events TO $FH_ROLE"; log insert-off; mk c2-close; echo "insert-off" ;;
log-on)     mk l-open;  $PSQL -c "REVOKE SELECT ON events FROM $FH_ROLE"; log log-on; echo "log-on" ;;
log-off)    $PSQL -c "GRANT SELECT ON events TO $FH_ROLE"; log log-off; mk l-close; echo "log-off" ;;
cut-on)
  p=${2:-}; python3 -I -c "import sys;v=float(sys.argv[1]);assert 0<v<=1" "$p" 2>/dev/null || { echo "usage: fault.sh cut-on <p> with 0 < p <= 1" >&2; exit 2; }
  mk c1-open; "$FH_SRC/proxy.sh" p "$p" >/dev/null; log "cut-on"; echo "cut-on p=$p" ;;
cut-off) "$FH_SRC/proxy.sh" mode passthru >/dev/null; log cut-off; mk c1-close; echo "cut-off" ;;
all-off)
  $PSQL -c "GRANT INSERT, UPDATE ON post_embeddings TO $FH_ROLE" -c "GRANT INSERT ON events TO $FH_ROLE" -c "GRANT SELECT ON events TO $FH_ROLE"
  "$FH_SRC/proxy.sh" mode passthru >/dev/null
  log all-off; status; exit $? ;;
*) echo "usage: fault.sh status|vector-on|vector-off|insert-on|insert-off|log-on|log-off|cut-on <p>|cut-off|all-off" >&2; exit 2 ;;
esac
