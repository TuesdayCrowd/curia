#!/usr/bin/env python3
"""Readiness report for a hosted Curia Forum (errata G83, R11.110).

    python3 -I tools/deploy/readiness.py --origin https://forum.example [options]

Every check prints one line, `<id>  PASS|FAIL|NOT CHECKED  <reason>`, and the exit code says the worst:
0 only if every check PASSed, 1 if any FAILed, 8 if none FAILed and any was NOT CHECKED (a check that could not
run is never reported as passed). 2 is a usage error and nothing ran. A reason names no secret and echoes no
response body: it says what was sent, what status came back and what was compared.

Checks (the runbook docs/runbooks/hosted-instance.md says what each is for):
  R1  GET /health answers 200 under the origin's Host
  R2  a foreign Host is refused 400 as a problem document of type curia/request/host-not-served
  R3  the discovery document's issuer is the origin, exactly
  R4  the discovery document's token_endpoint and jwks_uri are under the origin
  R5  https with a certificate that verifies for the origin's host and does not expire within 14 days
  R6  /v1/log/jwks carries the announced kids (--declaration-kid, --operator-kid, --log-kid)
  R7  /v1/log/head exists and is no older than --max-head-age seconds
  R8  X-Forwarded-Host and X-Forwarded-Proto change nothing
  R9  with --probe-limits: the edge answers 429 on GET /v1/enroll/nonce and POST /oauth/token
  R10 lifecycle-audit exits 0 (needs CURIA_EVENTS_POSTGRES and the operator tool)
  R11 the app role's grants and the db/0002 tables, by psql (--db-admin-url)

Deliberately not checks: the lifecycle guard's state, the embedding model's identity (the Forum does not start
without it) and the Forum's own body bound (an edge's 413 and the Forum's are the same answer through the origin).
R6 compares the Forum with the kids it is given; it does not compare the announcement with the key's material.
R9 is a request flood against the origin and is opt-in.
"""
import argparse
import datetime
import http.client
import json
import os
import shlex
import shutil
import ssl
import subprocess
import sys
import urllib.parse

PASS, FAIL, NOT_CHECKED = "PASS", "FAIL", "NOT CHECKED"
ALL_CHECKS = ["R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R11"]
FOREIGN_HOST = "not-this-forum.invalid"
HOST_NOT_SERVED = "curia/request/host-not-served"

# The restore post-check of docs/runbooks/backup-restore.md, with the role as the literal curia_app (one textual
# substitution below). Column-level grants: has_table_privilege(..., 'INSERT') is false with and without db/0006,
# so the rows that decide are the column and any-column ones, and two rows are positive controls (a role stripped of
# every grant would otherwise read as narrowed).
GRANTS_SQL = """SELECT 'insert on events, whole table' AS name, has_table_privilege('curia_app', 'events', 'INSERT') AS got, false AS want
UNION ALL SELECT 'insert on events.seq', has_column_privilege('curia_app', 'events', 'seq', 'INSERT'), false
UNION ALL SELECT 'update on any events column', has_any_column_privilege('curia_app', 'events', 'UPDATE'), false
UNION ALL SELECT 'delete on events', has_table_privilege('curia_app', 'events', 'DELETE'), false
UNION ALL SELECT 'insert on events.event_id (the positive control)', has_column_privilege('curia_app', 'events', 'event_id', 'INSERT'), true
UNION ALL SELECT 'select on events.seq', has_column_privilege('curia_app', 'events', 'seq', 'SELECT'), true;"""
TABLES_SQL = "SELECT to_regclass('authn_dpop_nonces') IS NOT NULL AND to_regclass('authn_replay') IS NOT NULL;"


class Report:
    def __init__(self):
        self.rows = []

    def add(self, check, status, reason):
        self.rows.append((check, status, reason))

    def exit_code(self):
        statuses = {s for _, s, _ in self.rows}
        if FAIL in statuses:
            return 1
        if NOT_CHECKED in statuses:
            return 8
        return 0


class Target:
    def __init__(self, origin, allow_http, ca_file, timeout, connect=None):
        parts = urllib.parse.urlsplit(origin)
        if parts.scheme not in ("http", "https") or not parts.hostname or parts.path not in ("", "/") or parts.query or parts.fragment:
            raise ValueError("--origin must read scheme://host[:port]")
        self.origin = origin
        self.scheme = parts.scheme
        self.host = parts.hostname
        self.port = parts.port or (443 if parts.scheme == "https" else 80)
        self.authority = parts.netloc
        self.dial_host, self.dial_port = self.host, self.port
        if connect:
            dial_host, _, dial_port = connect.rpartition(":")
            self.dial_host, self.dial_port = dial_host, int(dial_port)
        self.allow_http = allow_http
        self.ca_file = ca_file
        self.timeout = timeout

    def context(self):
        if self.ca_file:
            return ssl.create_default_context(cafile=self.ca_file)
        return ssl.create_default_context()

    def request(self, method, path, host=None, headers=None, body=None):
        """One request with the Host header written as given (never the library's), answered as (status, headers, text)."""
        if self.scheme == "https":
            conn = http.client.HTTPSConnection(self.dial_host, self.dial_port, timeout=self.timeout, context=self.context())
        else:
            conn = http.client.HTTPConnection(self.dial_host, self.dial_port, timeout=self.timeout)
        try:
            conn.putrequest(method, path, skip_host=True, skip_accept_encoding=True)
            conn.putheader("Host", host if host is not None else self.authority)
            for name, value in (headers or {}).items():
                conn.putheader(name, value)
            payload = body if body is not None else b""
            if method == "POST":
                conn.putheader("Content-Length", str(len(payload)))
            conn.endheaders(payload)
            response = conn.getresponse()
            text = response.read(65536).decode("utf-8", "replace")
            return response.status, {k.lower(): v for k, v in response.getheaders()}, text
        finally:
            conn.close()


def fault(error):
    """A transport fault named by its class and errno, never by a message that could carry a value."""
    return f"{type(error).__name__}" + (f" (errno {error.errno})" if getattr(error, "errno", None) else "")


def as_json(text):
    try:
        value = json.loads(text)
    except ValueError:
        return None
    return value if isinstance(value, dict) else None


def check_r1(t):
    status, _, _ = t.request("GET", "/health")
    if status == 200:
        return PASS, f"GET /health answered 200 under Host {t.authority}"
    return FAIL, f"GET /health under Host {t.authority} answered {status}, not 200"


def check_r2(t):
    status, headers, text = t.request("GET", "/health", host=FOREIGN_HOST)
    if status != 400:
        return FAIL, f"a request under Host {FOREIGN_HOST} answered {status}, not 400: the Forum serves a name that is not its own"
    problem = as_json(text)
    if "json" not in headers.get("content-type", "") or problem is None:
        return FAIL, "the refusal is a 400 with no problem document: AllowedHosts is set (G82), and the host answered before the Forum could"
    if problem.get("type") != HOST_NOT_SERVED:
        return FAIL, f"the refusal is a problem document of another type than {HOST_NOT_SERVED}"
    return PASS, f"a request under Host {FOREIGN_HOST} was refused 400 {HOST_NOT_SERVED}"


def discovery(t):
    status, _, text = t.request("GET", "/.well-known/oauth-authorization-server")
    doc = as_json(text)
    if status != 200 or doc is None:
        return None, f"GET /.well-known/oauth-authorization-server answered {status} and no document"
    return doc, None


def check_r3(t):
    doc, problem = discovery(t)
    if doc is None:
        return FAIL, problem
    if doc.get("issuer") == t.origin:
        return PASS, f"the issuer is {t.origin}"
    return FAIL, f"the issuer is not the origin {t.origin}: the Forum is configured with another name (R5.26)"


def check_r4(t):
    doc, problem = discovery(t)
    if doc is None:
        return FAIL, problem
    wrong = [m for m in ("token_endpoint", "jwks_uri") if not str(doc.get(m, "")).startswith(t.origin + "/")]
    if wrong:
        return FAIL, f"{', '.join(wrong)} is not under {t.origin}"
    return PASS, f"token_endpoint and jwks_uri are under {t.origin}"


def check_r5(t):
    if t.scheme == "http":
        if t.allow_http:
            return NOT_CHECKED, "the origin is http and --allow-http was given: there is no certificate to check"
        return FAIL, "the origin is http: a hosted instance is served over https (pass --allow-http for a local run)"
    try:
        with socket_to(t) as sock:
            cert = sock.getpeercert()
    except ssl.SSLCertVerificationError as error:
        return FAIL, f"the certificate does not verify for {t.host}: {error.verify_message}"
    except ssl.SSLError as error:
        return FAIL, f"the TLS handshake with {t.host}:{t.port} failed: {type(error).__name__}"
    expires = datetime.datetime.fromtimestamp(ssl.cert_time_to_seconds(cert["notAfter"]), datetime.timezone.utc)
    days = (expires - datetime.datetime.now(datetime.timezone.utc)).days
    if days < 14:
        return FAIL, f"the certificate verifies for {t.host} and expires in {days} days (the floor is 14)"
    return PASS, f"the certificate verifies for {t.host} and expires in {days} days"


def socket_to(t):
    import socket
    raw = socket.create_connection((t.dial_host, t.dial_port), timeout=t.timeout)
    try:
        return t.context().wrap_socket(raw, server_hostname=t.host)
    except BaseException:
        raw.close()
        raise


def check_r6(t, args):
    status, _, text = t.request("GET", "/v1/log/jwks")
    doc = as_json(text)
    if status != 200 or doc is None:
        return FAIL, f"GET /v1/log/jwks answered {status} and no key set"
    wanted = [("declaration_keys", args.declaration_kid, True), ("operator_keys", args.operator_kid, True), ("keys", args.log_kid, False)]
    wrong, unchecked = [], []
    for member, kid, exactly_one in wanted:
        if kid is None:
            unchecked.append(member)
            continue
        kids = [k.get("kid") for k in doc.get(member, []) if isinstance(k, dict)]
        if exactly_one and len(kids) != 1:
            wrong.append(f"{member} lists {len(kids)} keys, not one")
        elif kid not in kids:
            wrong.append(f"{member} does not list the announced kid")
    if wrong:
        return FAIL, "; ".join(wrong)
    if unchecked:
        return NOT_CHECKED, f"no kid was given for {', '.join(unchecked)}; the ones given match"
    return PASS, "the declaration, operator-acts and log kids are the announced ones"


def check_r7(t, args):
    status, _, text = t.request("GET", "/v1/log/head")
    if status == 404:
        return FAIL, "GET /v1/log/head answered 404: no head has been signed (the timer has not run)"
    doc = as_json(text)
    head = doc.get("head") if doc else None
    stamp = head.get("timestamp") if isinstance(head, dict) else None
    if status != 200 or stamp is None:
        return FAIL, f"GET /v1/log/head answered {status} with no readable head timestamp"
    try:
        signed = datetime.datetime.fromisoformat(stamp) if isinstance(stamp, str) else datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc)
    except (ValueError, OverflowError, OSError):
        return FAIL, "the head's timestamp is not a time this check can read"
    if signed.tzinfo is None:
        signed = signed.replace(tzinfo=datetime.timezone.utc)
    age = (datetime.datetime.now(datetime.timezone.utc) - signed).total_seconds()
    if age > args.max_head_age:
        return FAIL, f"the latest head was signed {int(age)} s ago, more than --max-head-age {args.max_head_age}: the head timer is not running"
    return PASS, f"the latest head was signed {max(int(age), 0)} s ago (bound {args.max_head_age})"


def check_r8(t):
    plain, _, _ = t.request("GET", "/health")
    forged, _, _ = t.request("GET", "/health", headers={"X-Forwarded-Host": "another.example", "X-Forwarded-Proto": "http"})
    if plain == forged == 200:
        return PASS, "GET /health answered 200 with and without X-Forwarded-Host and X-Forwarded-Proto"
    if plain != 200:
        return FAIL, f"GET /health answered {plain} with no forwarded header at all, so this check cannot tell whether the Forum reads one; when R1 fails beside it the proxy rewrote the Host (R7.33 (a)), and that is the cause to look for first"
    return FAIL, f"GET /health answered {plain} without and {forged} with X-Forwarded-Host and X-Forwarded-Proto: the Forum reads a forwarded header (R5.26)"


def flood(t, method, path, headers, count):
    for sent in range(1, count + 1):
        status, _, _ = t.request(method, path, headers=headers, body=b"" if method == "POST" else None)
        if status == 429:
            return sent, sent
    return None, count


def check_r9(t, args):
    if not args.probe_limits:
        return NOT_CHECKED, "--probe-limits was not given: the burst is a request flood against the origin"
    count = args.burst + 10
    parts, failed = [], False
    for method, path, headers in (
        ("GET", "/v1/enroll/nonce", {}),
        ("POST", "/oauth/token", {"Content-Type": "application/x-www-form-urlencoded"}),
    ):
        first, sent = flood(t, method, path, headers, count)
        if first is None:
            failed = True
            parts.append(f"{method} {path}: no 429 in {sent} requests (no edge limit)")
        else:
            parts.append(f"{method} {path}: first 429 at request {first}")
    return (FAIL if failed else PASS), "; ".join(parts)


def check_r10(args):
    if not os.environ.get("CURIA_EVENTS_POSTGRES"):
        return NOT_CHECKED, "CURIA_EVENTS_POSTGRES is not set: lifecycle-audit reads the log"
    try:
        run = subprocess.run(shlex.split(args.operator_cmd) + ["lifecycle-audit"], capture_output=True, text=True, timeout=300, check=False)
    except FileNotFoundError:
        return NOT_CHECKED, f"the operator tool ({args.operator_cmd}) was not found"
    first = (run.stdout.strip().splitlines() or [""])[0]
    if run.returncode == 0:
        return PASS, f"lifecycle-audit exit 0: {first}"
    if run.returncode == 7:
        return FAIL, f"lifecycle-audit exit 7: {first} (the Forum skips rows it does not honour; read them before anything signs over them)"
    return FAIL, f"lifecycle-audit exit {run.returncode}"


def check_r11(args):
    if not args.db_admin_url:
        return NOT_CHECKED, "--db-admin-url was not given"
    psql = shutil.which("psql")
    if psql is None:
        return NOT_CHECKED, "psql was not found on PATH"
    base = [psql, args.db_admin_url, "-X", "-A", "-t", "-F", "|", "-v", "ON_ERROR_STOP=1"]
    grants = subprocess.run(base + ["-c", GRANTS_SQL.replace("curia_app", args.app_role)], capture_output=True, text=True, timeout=60, check=False)
    tables = subprocess.run(base + ["-c", TABLES_SQL], capture_output=True, text=True, timeout=60, check=False)
    if grants.returncode != 0 or tables.returncode != 0:
        return NOT_CHECKED, f"psql could not run the check (exit {grants.returncode or tables.returncode})"
    bad = []
    for row in grants.stdout.strip().splitlines():
        name, got, want = row.rsplit("|", 2)
        if got != want:
            bad.append(name)
    if tables.stdout.strip() != "t":
        bad.append("authn_dpop_nonces and authn_replay (db/0002)")
    if bad:
        return FAIL, "the database does not match db/0002 and db/0006: " + "; ".join(bad)
    return PASS, f"the role {args.app_role} holds the narrowed grants of db/0006 and db/0002's tables exist"


def parse(argv):
    p = argparse.ArgumentParser(prog="readiness.py", description="Readiness report for a hosted Curia Forum (errata G83, R11.110).")
    p.add_argument("--origin", required=True)
    p.add_argument("--allow-http", action="store_true")
    p.add_argument("--ca-file")
    p.add_argument("--connect", help="HOST:PORT to dial instead of the origin's (like curl --resolve); the Host header and the certificate name stay the origin's")
    p.add_argument("--declaration-kid")
    p.add_argument("--operator-kid")
    p.add_argument("--log-kid")
    p.add_argument("--probe-limits", action="store_true")
    p.add_argument("--burst", type=int, default=20, help="the edge's burst (G83's table); R9 sends burst+10 requests per route")
    p.add_argument("--db-admin-url", default=os.environ.get("CURIA_READINESS_DB_URL"))
    p.add_argument("--app-role", default="curia_app")
    p.add_argument("--max-head-age", type=int, default=7200)
    p.add_argument("--operator-cmd", default="curia-operator")
    p.add_argument("--only", help="comma list of check ids; every other check reports NOT CHECKED")
    p.add_argument("--timeout", type=float, default=10.0)
    return p.parse_args(argv)


def main(argv):
    args = parse(argv)
    try:
        target = Target(args.origin, args.allow_http, args.ca_file, args.timeout, args.connect)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    if args.connect and target.scheme == "https":
        print("error: --connect is for an http origin; an https origin is dialed by its own name so that its certificate is checked for that name", file=sys.stderr)
        return 2
    only = set(args.only.split(",")) if args.only else None
    runners = {
        "R1": lambda: check_r1(target), "R2": lambda: check_r2(target), "R3": lambda: check_r3(target), "R4": lambda: check_r4(target),
        "R5": lambda: check_r5(target), "R6": lambda: check_r6(target, args), "R7": lambda: check_r7(target, args),
        "R8": lambda: check_r8(target), "R9": lambda: check_r9(target, args), "R10": lambda: check_r10(args), "R11": lambda: check_r11(args),
    }
    report = Report()
    for check in ALL_CHECKS:
        if only is not None and check not in only:
            report.add(check, NOT_CHECKED, "not selected by --only")
            continue
        try:
            status, reason = runners[check]()
        except (OSError, http.client.HTTPException, ssl.SSLError, subprocess.SubprocessError) as error:
            status, reason = FAIL, f"{fault(error)} while running the check"
        report.add(check, status, reason)
    for check, status, reason in report.rows:
        print(f"{check:<4} {status:<12} {reason}")
    code = report.exit_code()
    counts = {s: sum(1 for _, x, _ in report.rows if x == s) for s in (PASS, FAIL, NOT_CHECKED)}
    print(f"result: {counts[PASS]} PASS, {counts[FAIL]} FAIL, {counts[NOT_CHECKED]} NOT CHECKED; exit {code}")
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
