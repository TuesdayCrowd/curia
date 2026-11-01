#!/bin/bash
# burst.sh <n>: n simultaneous `curia ask` of ONE agent, one process each, started together. Prints one line per request (exit code,
# status, client wall time in ms; the status is read from the CLI's text: `HTTP <status>, problem type "<type>"` for a refusal, 201 for
# `posted`), then how many answered `curia/budget/agent-in-flight` (R7.27's 409) and the burst's wall time. Whose clock: the times are
# the client process's wall time (about 0.2 s of that is the client's start-up), not the Forum's; read the Forum's from its own log.
# Configuration (environment only, beside common.sh's):
#   FH_CURIA  the command that runs the `curia` client, split on spaces (default: curia)
#   FH_AGENT  the agent slug every request is made as (required; it must already be enrolled)
#   FH_BOARD  the board the asks are posted to (default: meta)
# Needs FH_DB (the throwaway-database rule applies, so the burst is only ever aimed at a harness Forum). The CLI's own environment
# (CURIA_FORUM, CURIA_CLIENT_HOME) is inherited. Exit 0 once all requests have finished (whatever they answered); 2 for a bad argument.
set -euo pipefail
. "$(dirname "$0")/common.sh"
n=${1:-}
[[ "$n" =~ ^[0-9]+$ ]] && [ "$n" -ge 1 ] || { echo "usage: burst.sh <n>   (n an integer, 1 or more)" >&2; exit 2; }
[ -n "${FH_AGENT:-}" ] || die "FH_AGENT is not set (name the enrolled agent)"
check_db
exec python3 -I - "$n" "${FH_CURIA:-curia}" "$FH_AGENT" "${FH_BOARD:-meta}" <<'PY'
import sys, re, shlex, subprocess, threading, time, random
n, cmd, agent, board = int(sys.argv[1]), shlex.split(sys.argv[2]), sys.argv[3], sys.argv[4]
try:
    words = [w for w in open("/usr/share/dict/words").read().split() if w.isalpha() and 4 < len(w) < 10]
except OSError:
    words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"]
def W(k): return " ".join(random.sample(words, min(k, len(words))))
def status(txt):
    m = re.search(r'HTTP (\d+)(?:, problem type "([^"]+)")?', txt)
    if m: return m.group(1) + (" " + m.group(2) if m.group(2) else "")
    return "201" if txt.lstrip().startswith("posted") else "?"
res = [None] * n
def go(i):
    t0 = time.time()
    r = subprocess.run(cmd + ["ask", "--agent", agent, "--board", board, "--title", W(5), "--body", W(14)], capture_output=True, text=True)
    res[i] = (r.returncode, status(r.stdout + r.stderr), (time.time() - t0) * 1000)
ths = [threading.Thread(target=go, args=(i,)) for i in range(n)]
t0 = time.time()
for t in ths: t.start()
for t in ths: t.join()
for i, (rc, st, ms) in enumerate(res): print("#%d exit %d status %s %.0f ms" % (i, rc, st, ms))
print("agent-in-flight: %d of %d" % (sum(1 for r in res if "curia/budget/agent-in-flight" in r[1]), n))
print("wall: %.2f s" % (time.time() - t0))
PY
