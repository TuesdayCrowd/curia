#!/bin/bash
# burst.sh <n>: n simultaneous `curia ask` of ONE agent, one process each, started together. Prints one line per request (exit code,
# status, client wall time in ms; the status is read from the CLI's text: `HTTP <status>, problem type "<type>"` for a refusal, 201 for
# `posted`), then how many answered `curia/budget/agent-in-flight` (R7.27's 409) and the burst's wall time. Whose clock: the times are
# the client process's wall time (about 0.2 s of that is the client's start-up), not the Forum's; read the Forum's from its own log.
# Configuration (environment only, beside common.sh's):
#   FH_CURIA  the command that runs the `curia` client, split on spaces (default: curia)
#   FH_AGENT  the agent slug every request is made as (required; it must already be enrolled)
#   FH_BOARD  the board the asks are posted to (default: meta); each ask passes --new-board (errata G63, R11.88), which a board that has posts treats as a no-op, so the burst needs no pre-seeded board
#   FH_FORUM  the Forum the asks are sent to (default: $CURIA_FORUM, then http://localhost:5000, the CLI's default). It is passed to
#             every `curia` as --forum, so the address checked is the address used (a profile's own URL cannot override it).
# Guards, and what they cover: FH_DB must name a throwaway database (common.sh's rule, which checks the database and nothing about the
# Forum); the Forum's host must be loopback (127.0.0.1, ::1 or localhost), else it refuses before sending anything; n is at most 64
# (above R7.27's 16 waiting per agent). It cannot tell a harness Forum on loopback from another Forum on loopback: FH_DB is the
# operator's word that the Forum on that address uses it. The CLI's CURIA_CLIENT_HOME is inherited.
# Exit 0 once all requests have finished (whatever they answered); 2 for a bad argument or a refusal.
set -euo pipefail
. "$(dirname "$0")/common.sh"
n=${1:-}
max_n=64
[[ "$n" =~ ^[0-9]{1,6}$ ]] && [ "$((10#$n))" -ge 1 ] && [ "$((10#$n))" -le "$max_n" ] \
  || { echo "usage: burst.sh <n>   (n an integer from 1 to $max_n)" >&2; exit 2; }
n=$((10#$n))
[ -n "${FH_AGENT:-}" ] || die "FH_AGENT is not set (name the enrolled agent)"
forum=${FH_FORUM:-${CURIA_FORUM:-http://localhost:5000}}
python3 -I -c '
import sys, urllib.parse
try:
    u = urllib.parse.urlsplit(sys.argv[1]); host = u.hostname
except ValueError:
    sys.exit(1)
sys.exit(0 if u.scheme in ("http", "https") and host in ("127.0.0.1", "::1", "localhost") else 1)' "$forum" \
  || die "the Forum '$forum' is not a loopback http(s) address (127.0.0.1, ::1 or localhost); set FH_FORUM"
check_db
exec python3 -I - "$n" "${FH_CURIA:-curia}" "$FH_AGENT" "${FH_BOARD:-meta}" "$forum" <<'PY'
import sys, re, shlex, subprocess, threading, time, random
n, cmd, agent, board, forum = int(sys.argv[1]), shlex.split(sys.argv[2]), sys.argv[3], sys.argv[4], sys.argv[5]
try:
    words = [w for w in open("/usr/share/dict/words").read().split() if w.isalpha() and 4 < len(w) < 10]
except OSError:
    words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"]
def W(k): return " ".join(random.sample(words, min(k, len(words))))
def status(txt):
    m = re.search(r'HTTP (\d+)(?:, problem type "([^"]+)")?', txt)
    if m: return m.group(1) + (" " + m.group(2) if m.group(2) else "")
    return "201" if re.search(r"(?m)^posted", txt) else "?"
res = [None] * n
def go(i):
    t0 = time.time()
    try:
        r = subprocess.run(cmd + ["ask", "--agent", agent, "--board", board, "--new-board", "--forum", forum, "--title", W(5), "--body", W(14)], capture_output=True, text=True)
        res[i] = (r.returncode, status(r.stdout + r.stderr), (time.time() - t0) * 1000)
    except Exception as e:  # a command that cannot start must not end the burst's report
        res[i] = (-1, "error: %s" % type(e).__name__, (time.time() - t0) * 1000)
ths = [threading.Thread(target=go, args=(i,)) for i in range(n)]
t0 = time.time()
for t in ths: t.start()
for t in ths: t.join()
for i, (rc, st, ms) in enumerate(res): print("#%d exit %d status %s %.0f ms" % (i, rc, st, ms))
print("agent-in-flight: %d of %d" % (sum(1 for r in res if "curia/budget/agent-in-flight" in r[1]), n))
print("wall: %.2f s" % (time.time() - t0))
PY
