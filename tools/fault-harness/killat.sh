#!/bin/bash
# killat.sh <seconds> -- <command> [args...]: runs the command and kills it (SIGKILL to its process group) <seconds> after it started, so
# a client can be made to disappear at a stated instant. Prints the start and the kill as epoch milliseconds (compare with the Forum's
# `Request finished` and the proxy log, which carry their own clocks) and the elapsed time. Kills only the process it started.
# Exit 0 when it killed the command; 3 when the command ended by itself first (its exit code is printed; the kill was not delivered);
# 2 for a bad argument. <seconds> is a positive number. It needs no database: it does not touch Postgres.
set -euo pipefail
s=${1:-}
[ "${2:-}" = "--" ] && [ "$#" -ge 3 ] && python3 -I -c "import sys;assert float(sys.argv[1])>0" "$s" 2>/dev/null \
  || { echo "usage: killat.sh <seconds> -- <command> [args...]   (seconds a positive number)" >&2; exit 2; }
shift 2
exec python3 -I - "$s" "$@" <<'PY'
import os, signal, subprocess, sys, time
secs, cmd = float(sys.argv[1]), sys.argv[2:]
ms = lambda: int(time.time() * 1000)
t0, e0 = time.time(), ms()
p = subprocess.Popen(cmd, start_new_session=True)
print("started %d" % e0, flush=True)
try:
    rc = p.wait(timeout=secs)
    print("exited by itself with code %d at +%.3f s (epoch ms %d); the kill was not delivered" % (rc, time.time() - t0, ms()))
    sys.exit(3)
except subprocess.TimeoutExpired:
    pass
os.killpg(p.pid, signal.SIGKILL)
p.wait()
print("killed at %d (+%.3f s after the start)" % (ms(), time.time() - t0))
PY
