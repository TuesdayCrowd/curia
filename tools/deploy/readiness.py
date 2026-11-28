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
  R12 the log holds no more than --event-ceiling events (default 1,500, errata G86 (rev.); a warning above --event-warn, 1,125):
      counted by psql (--db-admin-url), else read from the latest head's tree_size, which understates and so certifies only a size
      at or below the warning figure; an unreadable size is NOT CHECKED, never PASS
  R13 enrolment at the edge is as --expect-enrolment says (default closed: POST /v1/agents answers 403 curia/edge/enrolment-closed
      in two spellings of the route; open: the Forum's own 400 curia/enroll/proof-required to a well-formed body with no proof);
      appends no row to the log

Deliberately not checks: the lifecycle guard's state, the embedding model's identity (the Forum does not start
without it) and the Forum's own body bound (an edge's 413 and the Forum's share a type, not the document's bytes).
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
ALL_CHECKS = ["R1", "R2", "R3", "R4", "R5", "R6", "R7", "R8", "R9", "R10", "R11", "R12", "R13"]
FOREIGN_HOST = "not-this-forum.invalid"
HOST_NOT_SERVED = "curia/request/host-not-served"
ENROLMENT_CLOSED = "curia/edge/enrolment-closed"
PROOF_REQUIRED = "curia/enroll/proof-required"
# R13's request: the route's own shape (agent_id, alg, kid and a public key that is a P-256 SubjectPublicKeyInfo) and no `proof`. An empty body is
# `curia/request/unreadable`, `{}` is `curia/enroll/invalid`, and a made-up key is `curia/enroll/invalid-key`: only a well-formed body shows that an
# open route is open on purpose. The key is a public key generated for this probe; its private half was discarded. R4.46 refuses a request with no
# proof before any store is read, so the probe appends no row (measured, Task 0). The two spellings are the route's, in the case the router also accepts.
ENROL_BODY = json.dumps({
    "agent_id": "https://agents.example/readiness-probe",
    "alg": "ES256",
    "kid": "readiness-probe",
    "public_key": "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDUwxV4LhSCHH44gGzVvcuzh9b/oHXZC/5VMhxKEkHcx7wCRltha3CcBJ5UKDtS5to1j/nZdyVQGoGf+TTfF5/g==",
}).encode("utf-8")
ENROL_SPELLINGS = ("/v1/agents", "/V1/AGENTS")
COUNT_SQL = "SELECT count(*) FROM events"

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
# db/0007 (R6.69): the column exists and the role may INSERT it. has_column_privilege raises on a column that is not there, so the catalogue is asked first; "not there" is the answer.
LEAF_SQL = """SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('events') AND attname = 'leaf_hash' AND NOT attisdropped)
THEN has_column_privilege('curia_app', 'events', 'leaf_hash', 'INSERT') ELSE false END;"""
# db/0008 (errata G88, R11.114): the three tables exist and the role may SELECT and INSERT each; the Forum does not start without them.
SEARCH_SQL = """SELECT to_regclass('search_docs') IS NOT NULL AND to_regclass('search_terms') IS NOT NULL AND to_regclass('search_index_versions') IS NOT NULL
AND has_table_privilege('curia_app', 'search_docs', 'SELECT') AND has_table_privilege('curia_app', 'search_docs', 'INSERT')
AND has_table_privilege('curia_app', 'search_terms', 'SELECT') AND has_table_privilege('curia_app', 'search_terms', 'INSERT')
AND has_table_privilege('curia_app', 'search_index_versions', 'SELECT') AND has_table_privilege('curia_app', 'search_index_versions', 'INSERT');"""


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
    leaf = subprocess.run(base + ["-c", LEAF_SQL.replace("curia_app", args.app_role)], capture_output=True, text=True, timeout=60, check=False)
    search = subprocess.run(base + ["-c", SEARCH_SQL.replace("curia_app", args.app_role)], capture_output=True, text=True, timeout=60, check=False)
    if grants.returncode != 0 or tables.returncode != 0 or leaf.returncode != 0 or search.returncode != 0:
        return NOT_CHECKED, f"psql could not run the check (exit {grants.returncode or tables.returncode or leaf.returncode or search.returncode})"
    bad = []
    for row in grants.stdout.strip().splitlines():
        name, got, want = row.rsplit("|", 2)
        if got != want:
            bad.append(name)
    if tables.stdout.strip() != "t":
        bad.append("authn_dpop_nonces and authn_replay (db/0002)")
    if leaf.stdout.strip() != "t":
        bad.append("insert on events.leaf_hash (db/0007: the column is missing or the role cannot write it; the Forum does not start)")
    if search.stdout.strip() != "t":
        bad.append("search_docs, search_terms and search_index_versions with SELECT and INSERT for the role (db/0008: the Forum does not start)")
    if bad:
        return FAIL, "the database does not match db/0002, db/0006, db/0007 and db/0008: " + "; ".join(bad)
    return PASS, f"the role {args.app_role} holds the narrowed grants of db/0006, the leaf column of db/0007 and the search index of db/0008, and db/0002's tables exist"


def size_verdict(count, args, source, certain):
    """R12's verdict for a count. `certain` is true for an exact count (psql) and false for a head's tree_size, which can only understate."""
    if count > args.event_ceiling:
        return FAIL, f"{count} events ({source}), above the {args.event_ceiling} the beta is sized for: close enrolment and read register item 278"
    if count <= args.event_warn:
        return PASS, f"{count} events ({source}), within the ceiling of {args.event_ceiling}"
    if certain:
        return PASS, f"warning: {count} events ({source}), above {args.event_warn}; the ceiling is {args.event_ceiling}: stop inviting"
    return NOT_CHECKED, f"the latest head counts {count} events, between {args.event_warn} and {args.event_ceiling}, and the head lags the log: use --db-admin-url"


def check_r12(t, args):
    if args.db_admin_url:
        psql = shutil.which("psql")
        if psql is None:
            return NOT_CHECKED, "--db-admin-url was given and psql was not found on PATH"
        run = subprocess.run([psql, args.db_admin_url, "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", COUNT_SQL], capture_output=True, text=True, timeout=60, check=False)
        text = run.stdout.strip()
        if run.returncode != 0:
            return NOT_CHECKED, f"psql could not count the events (exit {run.returncode})"
        if not text.isdigit():
            return NOT_CHECKED, "psql's answer was not a count of events"
        return size_verdict(int(text), args, "counted by psql", True)
    status, _, text = t.request("GET", "/v1/log/head")
    doc = as_json(text)
    head = doc.get("head") if doc else None
    size = head.get("tree_size") if isinstance(head, dict) else None
    if status != 200 or isinstance(size, bool) or not isinstance(size, int) or size < 0:
        return NOT_CHECKED, f"no event count: --db-admin-url was not given and GET /v1/log/head answered {status} with no readable tree_size"
    return size_verdict(size, args, "the latest head's tree_size, which understates", False)


def enrol_answer(t, path):
    status, _, text = t.request("POST", path, headers={"Content-Type": "application/json"}, body=ENROL_BODY)
    doc = as_json(text)
    return status, (doc.get("type") if doc else None)


def check_r13(t, args):
    for path in ENROL_SPELLINGS:
        status, kind = enrol_answer(t, path)
        if args.expect_enrolment == "closed":
            if status == 403 and kind == ENROLMENT_CLOSED:
                continue
            if status == 403:
                return FAIL, f"POST {path} answered 403 of another type than {ENROLMENT_CLOSED}: not the edge's closure"
            return FAIL, f"POST {path} answered {status}, not the edge's 403 {ENROLMENT_CLOSED}: it reached the Forum's enrolment route (status {status}), or another answer closed it"
        if status == 400 and kind == PROOF_REQUIRED:
            continue
        if status == 403 and kind == ENROLMENT_CLOSED:
            return FAIL, f"POST {path} was closed by the edge and --expect-enrolment open was given"
        return FAIL, f"POST {path} answered {status}, not the Forum's own 400 {PROOF_REQUIRED}"
    if args.expect_enrolment == "closed":
        return PASS, f"POST {' and '.join(ENROL_SPELLINGS)} were each answered 403 {ENROLMENT_CLOSED} by the edge"
    return PASS, f"POST {' and '.join(ENROL_SPELLINGS)} each reached the Forum's own 400 {PROOF_REQUIRED}: enrolment is open, as expected"


KID_FLAGS = ("--declaration-kid", "--operator-kid", "--log-kid")


def join_kid_values(argv, parser):
    """A key id is base64url, so about one in 64 begins with `-`, which argparse reads as an option. Join `--log-kid -abc` as `--log-kid=-abc`
    unless the next word is one of this script's own options (then the value is missing and argparse says so)."""
    known = set(parser._option_string_actions)  # noqa: SLF001 (argparse offers no public list of its option strings)
    joined, i = [], 0
    while i < len(argv):
        word = argv[i]
        if word in KID_FLAGS and i + 1 < len(argv) and argv[i + 1] not in known:
            joined.append(f"{word}={argv[i + 1]}")
            i += 2
        else:
            joined.append(word)
            i += 1
    return joined


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
    p.add_argument("--event-ceiling", type=int, default=1500, help="R12 FAILs a log of more events than this (errata G86, R11.110 (add.))")
    p.add_argument("--event-warn", type=int, default=1125, help="R12 PASSes with a warning above this many events")
    p.add_argument("--expect-enrolment", choices=("closed", "open"), default="closed", help="R13: closed (the default, edge refuses) or open (the Forum's own refusal of a proofless body)")
    p.add_argument("--max-head-age", type=int, default=7200)
    p.add_argument("--operator-cmd", default="curia-operator", help="R10 only: no operator verb counts events, so R12 does not use it")
    p.add_argument("--only", help="comma list of check ids; every other check reports NOT CHECKED")
    p.add_argument("--timeout", type=float, default=10.0)
    return p.parse_args(join_kid_values(argv, p))


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
        "R12": lambda: check_r12(target, args), "R13": lambda: check_r13(target, args),
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
