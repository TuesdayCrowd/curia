#!/bin/bash
# latecommit.sh revision|digest|flag|vote: the late-commit drill (D107). Hold every COMMIT, send act A (answered 503 when its commit is
# held past the Forum's command timeout), send act B at the chosen timing, release, wait for the landing, ask the oracle (forks.sh).
#
#   revision  A and B are `curia revision` of FH_POST with different text (the fork)
#   digest    A and B are FH_SENDER send: the same signed bytes twice (the duplicate). FH_SENDER is a path to a program called as
#             `$FH_SENDER prepare` (signs one wire, keeps it, prints a line `digest=sha256:<64 hex>`) and then `$FH_SENDER send` (sends
#             the kept wire, prints the Forum's answer, exits 0 when the Forum answered). It is a throwaway until `curia resend` exists.
#   flag      A and B are `curia flag FH_POST --kind FH_KIND` (the same flag twice by one raiser)
#   vote      A and B are `curia endorse FH_TARGET` (the same agent, the same target, a new nonce each)
#
# Required environment: FH_DB, FH_ROLE (common.sh's pair rule), FH_AGENT (the enrolled agent that acts). Also FH_POST (revision: the
# post to revise; flag: the post to flag), FH_TARGET (vote: the digest endorsed), FH_SENDER (digest).
# Optional: FH_HOLD seconds (default 6); FH_COMMAND_TIMEOUT the Forum's Command Timeout in seconds (default 2: a hold under twice that is
# refused, so a window that cannot produce a late commit is never read as a pass); FH_TIMING a|b (default a: B is sent FH_B_DELAY seconds
# after A, while the hold is on; b: B is sent as soon as A has been answered, and the hold is released just before it: the hold must not be
# released before A's connection has closed, because the proxy drops a held COMMIT whose client is gone when the mode is no longer
# delaycommit at that instant); FH_B_DELAY seconds from A's send to B's in timing a (default 3, and 2 x the command timeout + 0.6 for a
# flag; the measured window that reproduced the revision pair was B at +3.24 to +6.06 s with a 6 s hold); FH_BOARD (default meta); FH_KIND (flag, default spam); FH_FORUM (default
# $CURIA_FORUM, then http://localhost:5000; must be loopback); FH_CURIA (the client command, split on spaces; default curia).
#
# Order: preflight (every refusal is exit 2, nothing sent, nothing touched) -> warm the agent's token -> mark -> `fault.sh hold-on`
# (its exit status is checked: a bad name is exit 2 and the drill stops, because a drill without a hold reports a quiet pass) -> A -> B ->
# wait for the landing -> `fault.sh hold-off` -> forks.sh with the mark. The hold is released on every way out.
# Note: delaycommit holds EVERY simple-query COMMIT of every session through the proxy, so B's own commit is held too in timing a. B's
# time is measured from B's own send.
# Exit: 0 the drill ran and the oracle found no row; 1 the oracle found a row (on the unmodified tree this is the CONTROL reproducing the
# breach; on a tree with the guard it is a failure); 2 refused or the drill could not run.
set -euo pipefail
. "$(dirname "$0")/common.sh"
kind=${1:-}
[ $# -eq 1 ] || { echo "usage: latecommit.sh revision|digest|flag|vote" >&2; exit 2; }
case "$kind" in revision|digest|flag|vote) ;; *) echo "usage: latecommit.sh revision|digest|flag|vote" >&2; exit 2;; esac
check_pair
[ -n "${FH_AGENT:-}" ] || die "FH_AGENT is not set (name the enrolled agent)"
hold=${FH_HOLD:-6}; cto=${FH_COMMAND_TIMEOUT:-2}; timing=${FH_TIMING:-a}
# B's default send time in timing a: inside the stall (3 s), except for a flag, whose own try-only gate is held until A is answered
# (about twice the command timeout): B must be sent after that, and still before the landing.
bdelay=${FH_B_DELAY:-$([ "$kind" = flag ] && python3 -I -c 'import sys;print(2*float(sys.argv[1])+0.6)' "$cto" 2>/dev/null || echo 3)}
board=${FH_BOARD:-meta}; flagkind=${FH_KIND:-spam}; curia_cmd=${FH_CURIA:-curia}
forum=${FH_FORUM:-${CURIA_FORUM:-http://localhost:5000}}
python3 -I -c '
import sys, urllib.parse
hold, cto, timing, bdelay = sys.argv[1:5]
try:
    hold, cto, bdelay = float(hold), float(cto), float(bdelay)
except ValueError:
    sys.exit(3)
if not (0 < cto <= 600 and 0 < hold <= 900 and 0 <= bdelay <= 900): sys.exit(3)
if hold < 2 * cto: sys.exit(4)
if timing not in ("a", "b"): sys.exit(5)
try:
    u = urllib.parse.urlsplit(sys.argv[5]); host = u.hostname
except ValueError:
    sys.exit(6)
sys.exit(0 if u.scheme in ("http", "https") and host in ("127.0.0.1", "::1", "localhost") else 6)' "$hold" "$cto" "$timing" "$bdelay" "$forum" && rc=0 || rc=$?
case $rc in
  0) ;;
  3) die "FH_HOLD, FH_COMMAND_TIMEOUT and FH_B_DELAY must be numbers of seconds in range";;
  4) die "the hold ($hold s) is under twice the command timeout ($cto s): the first act's 503 and the late commit need hold >= 2 x timeout";;
  5) die "FH_TIMING must be a or b";;
  *) die "the Forum '$forum' is not a loopback http(s) address (127.0.0.1, ::1 or localhost); set FH_FORUM";;
esac
case "$kind" in
  revision|flag) [ -n "${FH_POST:-}" ] || die "FH_POST is not set (the post to $kind)";;
  vote) [[ "${FH_TARGET:-}" =~ ^sha256:[0-9a-f]{64}$ ]] || die "FH_TARGET must be the digest (sha256:<64 hex>) to endorse";;
  digest) [ -n "${FH_SENDER:-}" ] && [ -x "${FH_SENDER:-}" ] || die "FH_SENDER must be an executable (see the header)";;
esac
# A drill starts from the original grants and the original proxy mode, and the proxy must be there.
"$FH_SRC/fault.sh" status >/dev/null || die "a fault is already open (fault.sh status is not 0); run fault.sh all-off first"
"$FH_SRC/proxy.sh" status 2>&1 | head -1 | grep -q "^proxy running" || die "the proxy is not running (proxy.sh start)"
name="lc$(date +%s)"
release(){ "$FH_SRC/fault.sh" all-off >/dev/null 2>&1 || true; }
trap release EXIT
"$FH_SRC/mark.sh" "$name-pre" >/dev/null
export LC_KIND=$kind LC_HOLD=$hold LC_TIMING=$timing LC_BDELAY=$bdelay LC_NAME=$name LC_BOARD=$board LC_FLAGKIND=$flagkind LC_FORUM=$forum LC_CURIA=$curia_cmd
export LC_POST=${FH_POST:-} LC_TARGET=${FH_TARGET:-} LC_SENDER=${FH_SENDER:-} LC_SRC=$FH_SRC
rcp=0
python3 -I - <<'PY' || rcp=$?
import os, re, random, shlex, subprocess, sys, threading, time
E = os.environ
kind, hold, timing, bdelay, name = E["LC_KIND"], float(E["LC_HOLD"]), E["LC_TIMING"], float(E["LC_BDELAY"]), E["LC_NAME"]
src, agent, forum, board = E["LC_SRC"], E["FH_AGENT"], E["LC_FORUM"], E["LC_BOARD"]
curia = shlex.split(E["LC_CURIA"])
def ms(): return int(time.time() * 1000)
def sh(*a):
    r = subprocess.run(list(a), capture_output=True, text=True); return r.returncode, (r.stdout + r.stderr).strip()
try:
    words = [w for w in open("/usr/share/dict/words").read().split() if w.isalpha() and 4 < len(w) < 10]
except OSError:
    words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"]
def W(k): return " ".join(random.sample(words, min(k, len(words))))
def cli(*a): return curia + list(a) + ["--agent", agent, "--forum", forum]
def act(label):
    if kind == "revision": return cli("revision", "--board", board, "--parent", E["LC_POST"], "--body", label + " " + W(12))
    if kind == "flag": return cli("flag", E["LC_POST"], "--kind", E["LC_FLAGKIND"], "--rationale", "latecommit drill " + label)
    if kind == "vote": return cli("endorse", E["LC_TARGET"], "--board", board)
    return [E["LC_SENDER"], "send"]
digests = []
if kind == "digest":
    rc, out = sh(E["LC_SENDER"], "prepare")
    m = re.search(r"digest=(sha256:[0-9a-f]{64})", out)
    if rc != 0 or not m: print("the sender's prepare failed or printed no digest: " + out[:300]); sys.exit(2)
    digests.append(m.group(1))
else:
    sh(*cli("budget"))  # warm the token before the window opens (delaycommit holds a token mint's COMMIT too)
rc, out = sh(src + "/fault.sh", "hold-on", str(E["LC_HOLD"]), name)
if rc != 0:
    print("hold-on refused (exit %d), nothing sent: %s" % (rc, out)); sys.exit(2)
t_on = ms(); T0 = t_on
print("hold-on %s s, name %s, timing %s, B %s" % (E["LC_HOLD"], name, timing, ("at +%s s" % E["LC_BDELAY"]) if timing == "a" else "after A is answered"), flush=True)
res = {}
def run(label):
    t = ms(); rc, out = sh(*act(label)); e = ms()
    res[label] = (t, e, rc, out)
    for d in re.findall(r"sha256:[0-9a-f]{64}", out):
        if d not in digests and kind in ("revision", "vote"): digests.append(d)
ta = threading.Thread(target=run, args=("A",)); ta.start()
if timing == "b":
    ta.join()
    rc, out = sh(src + "/fault.sh", "hold-off", name)
    print("hold-off at %+.2f s, after A was answered (exit %d)" % ((ms() - T0) / 1000, rc), flush=True)
    if rc != 0: sys.exit(2)
else:
    time.sleep(bdelay)
tb = threading.Thread(target=run, args=("B",)); tb.start()
ta.join(); tb.join()
# the landing: wait at least until the hold has run from the last COMMIT, then until the log has stopped growing for 3 s
mark_seq = int([l for l in open(os.path.join(E["FH_DIR"], "marks", name + "-pre")).read().split("\n") if l.startswith("seq=")][0].split("=")[1])
last = None; last_change = time.time(); deadline = time.time() + 2 * hold + bdelay + 20
while time.time() < deadline:
    n = int(sh("psql", "-d", E["FH_DB"], "-Atc", "select count(*) from events where seq > %d" % mark_seq)[1] or 0)
    if n != last: last = n; last_change = time.time()
    if (ms() - t_on) / 1000 >= hold + 1 and time.time() - last_change >= 3: break
    time.sleep(0.5)
if timing == "a":
    rc, out = sh(src + "/fault.sh", "hold-off", name)
    print("hold-off at %+.2f s (exit %d)" % ((ms() - T0) / 1000, rc), flush=True)
for label in ("A", "B"):
    t, e, rc, out = res[label]
    print("%s: sent %+.2f s, answered %+.2f s (client %.2f s from its own send), exit %d :: %s" % (label, (t - T0) / 1000, (e - T0) / 1000, (e - t) / 1000, rc, " | ".join(out.splitlines()[:3])[:300]))
print("events after the mark: %d" % last)
if digests:
    os.environ["LAND_DEADLINE"] = "2"
    print(sh(src + "/landing.sh", *digests)[1])
PY
[ $rcp -eq 0 ] || exit $rcp
echo "== oracle"
"$FH_SRC/forks.sh" "$name-pre" && exit 0 || exit $?
