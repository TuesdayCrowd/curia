#!/bin/bash
# killat.sh <seconds> -- <command> [args...]: runs the command and kills it (SIGKILL to its process group) <seconds> after it started, so
# a client can be made to disappear at a stated instant. Prints the start and the kill as epoch milliseconds (compare with the Forum's
# `Request finished` and the proxy log, which carry their own clocks) and the elapsed time. Kills only the process it started.
# Exit 0 when it killed the command; 3 when the command ended by itself first (its exit code is printed; the kill was not delivered);
# 2 for a bad argument or a command that cannot start. <seconds> is a number above 0 and at most 86400. It needs no database.
# The command's process group is killed on EVERY way out of this script: the instant, the command ending by itself (its children are
# killed too), SIGINT, SIGTERM and SIGHUP (exit 130, 143, 129). Only SIGKILL of killat itself cannot be caught and leaves the group.
set -euo pipefail
s=${1:-}
[ "${2:-}" = "--" ] && [ "$#" -ge 3 ] && python3 -I -c "import sys;x=float(sys.argv[1]);assert 0<x<=86400" "$s" 2>/dev/null \
  || { echo "usage: killat.sh <seconds> -- <command> [args...]   (seconds a number above 0 and at most 86400)" >&2; exit 2; }
shift 2
exec python3 -I - "$s" "$@" <<'PY'
import os, signal, subprocess, sys, time
secs, cmd = float(sys.argv[1]), sys.argv[2:]
ms = lambda: int(time.time() * 1000)

def stop(signum, _frame):
    sys.exit(128 + signum)  # runs the finally below, which kills the command's group
for sig in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
    signal.signal(sig, stop)

t0, e0 = time.time(), ms()
try:
    p = subprocess.Popen(cmd, start_new_session=True)
except OSError as e:
    print("killat: cannot start %r: %s" % (cmd[0], e.strerror or e), file=sys.stderr)
    sys.exit(2)
try:
    print("started %d" % e0, flush=True)
    try:
        rc = p.wait(timeout=secs)
    except subprocess.TimeoutExpired:
        os.killpg(p.pid, signal.SIGKILL)
        p.wait()
        print("killed at %d (+%.3f s after the start)" % (ms(), time.time() - t0))
        sys.exit(0)
    print("exited by itself with code %d at +%.3f s (epoch ms %d); the kill was not delivered" % (rc, time.time() - t0, ms()))
    sys.exit(3)
finally:
    try:
        os.killpg(p.pid, signal.SIGKILL)  # anything the command left in its group, and the command itself on an interrupt
    except (ProcessLookupError, PermissionError):
        pass
PY
