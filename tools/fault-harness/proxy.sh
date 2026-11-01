#!/bin/bash
# proxy.sh start | stop | status | mode <passthru|cutcommit[:p]|dropcancel|delaycommit:<s>> | p <0..1>
# Needs FH_DB and FH_ROLE (see common.sh); refuses otherwise. `p 0` is passthru; `p <v>` is cutcommit:<v>.
set -euo pipefail
. "$(dirname "$0")/common.sh"
check_pair
PIDF=$FH_DIR/pgproxy.pid
MODEF=$FH_DIR/pgproxy.mode
alive(){ [ -f "$PIDF" ] && kill -0 "$(cat "$PIDF")" 2>/dev/null; }
setmode(){
  python3 -I -c '
import re, sys
m = sys.argv[1]
ok = m in ("passthru", "cutcommit", "dropcancel") or re.fullmatch(r"cutcommit:(0|1|0?\.[0-9]+|1\.0+)", m) or re.fullmatch(r"delaycommit:[0-9]+(\.[0-9]+)?", m)
sys.exit(0 if ok else 1)' "$1" || { echo "mode must be passthru, cutcommit[:p], dropcancel or delaycommit:<s>" >&2; exit 2; }
  echo "$1" > "$MODEF"; echo "mode=$1"
}
case ${1:-status} in
start)
  alive && { echo "proxy already running pid $(cat "$PIDF") port $FH_PROXY_PORT"; exit 0; }
  [ -z "$(lsof -tiTCP:"$FH_PROXY_PORT" -sTCP:LISTEN 2>/dev/null)" ] || { echo "port $FH_PROXY_PORT is in use" >&2; exit 1; }
  [ -e "$MODEF" ] || echo passthru > "$MODEF"
  nohup python3 -I "$FH_SRC/pgproxy.py" > "$FH_DIR/pgproxy.out" 2>&1 &
  echo $! > "$PIDF"
  for _ in $(seq 1 40); do
    lsof -tiTCP:"$FH_PROXY_PORT" -sTCP:LISTEN >/dev/null 2>&1 && { echo "proxy up pid $(cat "$PIDF") port $FH_PROXY_PORT mode=$(cat "$MODEF")"; exit 0; }
    sleep 0.25
  done
  echo "proxy NOT UP"; cat "$FH_DIR/pgproxy.out"; exit 1 ;;
stop)
  alive || { echo "proxy not running"; rm -f "$PIDF"; exit 0; }
  p=$(cat "$PIDF")
  # The pid file can outlive the proxy and the pid be reused: signal only a process whose command line is this proxy.
  case "$(ps -p "$p" -o command= 2>/dev/null)" in *pgproxy.py*) ;; *) echo "pid $p is not the proxy; not signalled" >&2; rm -f "$PIDF"; exit 1;; esac
  kill -TERM "$p"
  for _ in $(seq 1 40); do kill -0 "$p" 2>/dev/null || { echo "proxy stopped"; rm -f "$PIDF"; exit 0; }; sleep 0.25; done
  echo "proxy still running"; exit 1 ;;
status)
  if alive; then echo "proxy running pid $(cat "$PIDF") port $FH_PROXY_PORT mode=$(cat "$MODEF" 2>/dev/null || echo passthru)"
  else echo "proxy not running mode=$(cat "$MODEF" 2>/dev/null || echo passthru)"; exit 1; fi ;;
mode) setmode "${2:-}" ;;
p)
  v=${2:-}
  python3 -I -c "import sys;v=float(sys.argv[1]);assert 0<=v<=1" "$v" 2>/dev/null || { echo "p must be a number in [0,1]" >&2; exit 2; }
  if python3 -I -c "import sys;sys.exit(0 if float(sys.argv[1])==0 else 1)" "$v"; then setmode passthru; else setmode "cutcommit:$v"; fi ;;
*) echo "usage: proxy.sh start|stop|status|mode <mode>|p <value>" >&2; exit 2 ;;
esac
