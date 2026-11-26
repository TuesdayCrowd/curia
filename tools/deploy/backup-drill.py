#!/usr/bin/env python3
"""The backup-and-restore drill of docs/runbooks/backup-restore.md (errata G83, R11.110): a developer gate, not a CI test.

    python3 -I tools/deploy/backup-drill.py --admin-url postgresql://user@host:5432/postgres --source-db curia
        [--app-role curia_app] [--operator-cmd "curia-operator"] [--npgsql-base "Host=...;Username=..."]

It dumps the source database with pg_dump -Fc, restores it into a fresh database with pg_restore --no-owner, and
reports, per check, PASS, FAIL or NOT CHECKED (exit 0 only if every check PASSed, 1 if any FAILed, 8 if none FAILed
and any was NOT CHECKED: a drill that could not run is never reported as passed). The restored database is dropped at
the end whatever happened. Not an xUnit test, because a test that must pass in CI cannot depend on a binary CI may not
have (an older pg_dump refuses a newer server): the form that cannot be mistaken for green is this script saying
NOT CHECKED and exiting 8 when pg_dump is absent or its major is older than the server's.

  D1  pg_dump, pg_restore and psql are present, and pg_dump is not older than the server
  D2  the source database dumps and restores into a fresh one (the app role must exist in the cluster first: roles
      are cluster-level, and a restore into a cluster that lacks it fails its grants)
  D3  the restored events table holds as many rows as the source
  D4  the restore post-check's grants block reads got = want on every row, and db/0002's tables exist
  D5  lifecycle-audit exits 0 over the restored database (needs --operator-cmd and --npgsql-base)
"""
import argparse
import importlib.util
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import uuid

PASS, FAIL, NOT_CHECKED = "PASS", "FAIL", "NOT CHECKED"


def load_readiness():
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "readiness.py")
    spec = importlib.util.spec_from_file_location("readiness_sql", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def major_of(version_text):
    digits = "".join(c if c.isdigit() or c == "." else " " for c in version_text).split()
    return int(digits[0].split(".")[0]) if digits else 0


def psql(admin_url, database, sql):
    url = admin_url.rsplit("/", 1)[0] + "/" + database
    run = subprocess.run(["psql", url, "-X", "-A", "-t", "-F", "|", "-v", "ON_ERROR_STOP=1", "-c", sql], capture_output=True, text=True, timeout=300, check=False)
    return run.returncode, run.stdout.strip()


def main(argv):
    p = argparse.ArgumentParser(prog="backup-drill.py", description="Dump, restore and check a Curia events database.")
    p.add_argument("--admin-url", required=True)
    p.add_argument("--source-db", required=True)
    p.add_argument("--app-role", default="curia_app")
    p.add_argument("--operator-cmd")
    p.add_argument("--npgsql-base")
    args = p.parse_args(argv)
    rows = []

    def add(check, status, reason):
        rows.append((check, status, reason))

    def finish():
        for check, status, reason in rows:
            print(f"{check:<3} {status:<12} {reason}")
        statuses = {s for _, s, _ in rows}
        code = 1 if FAIL in statuses else 8 if NOT_CHECKED in statuses else 0
        print(f"result: exit {code}")
        return code

    tools = {name: shutil.which(name) for name in ("pg_dump", "pg_restore", "psql")}
    missing = [n for n, path in tools.items() if path is None]
    if missing:
        add("D1", NOT_CHECKED, f"{', '.join(missing)} not found on PATH: the drill did not run")
        for check in ("D2", "D3", "D4", "D5"):
            add(check, NOT_CHECKED, "the drill did not run")
        return finish()
    dump_major = major_of(subprocess.run(["pg_dump", "--version"], capture_output=True, text=True, check=False).stdout)
    code, version = psql(args.admin_url, "postgres", "SHOW server_version;")
    server_major = major_of(version)
    if code != 0:
        add("D1", NOT_CHECKED, "the server could not be read with psql (--admin-url): the drill did not run")
        for check in ("D2", "D3", "D4", "D5"):
            add(check, NOT_CHECKED, "the drill did not run")
        return finish()
    if dump_major < server_major:
        add("D1", NOT_CHECKED, f"pg_dump {dump_major} is older than the server {server_major}: an older pg_dump refuses a newer server, so the drill did not run")
        for check in ("D2", "D3", "D4", "D5"):
            add(check, NOT_CHECKED, "the drill did not run")
        return finish()
    add("D1", PASS, f"pg_dump {dump_major} against server {server_major}")

    restored = "curia_drill_" + uuid.uuid4().hex[:10]
    workdir = tempfile.mkdtemp(prefix="curia-drill-")
    try:
        code, role = psql(args.admin_url, "postgres", f"SELECT 1 FROM pg_roles WHERE rolname = '{args.app_role}';")
        if code != 0 or role != "1":
            add("D2", FAIL, f"the role {args.app_role} does not exist in the cluster: roles are cluster-level, and a restore into a cluster that lacks it fails its grants; create it first")
            for check in ("D3", "D4", "D5"):
                add(check, NOT_CHECKED, "the restore did not happen")
            return finish()
        dump = os.path.join(workdir, "events.dump")
        source_url = args.admin_url.rsplit("/", 1)[0] + "/" + args.source_db
        dumped = subprocess.run(["pg_dump", "-Fc", "-f", dump, source_url], capture_output=True, text=True, timeout=600, check=False)
        created = psql(args.admin_url, "postgres", f'CREATE DATABASE "{restored}";')[0] if dumped.returncode == 0 else 1
        restore = subprocess.run(
            ["pg_restore", "--no-owner", "-d", args.admin_url.rsplit("/", 1)[0] + "/" + restored, dump], capture_output=True, text=True, timeout=600, check=False
        ) if created == 0 else None
        if restore is None or restore.returncode != 0:
            add("D2", FAIL, "pg_dump or pg_restore failed" + (f" (restore exit {restore.returncode})" if restore else ""))
            for check in ("D3", "D4", "D5"):
                add(check, NOT_CHECKED, "the restore did not complete")
            return finish()
        add("D2", PASS, f"{args.source_db} dumped and restored into a fresh database")

        _, before = psql(args.admin_url, args.source_db, "SELECT count(*) FROM events;")
        _, after = psql(args.admin_url, restored, "SELECT count(*) FROM events;")
        add("D3", PASS if before == after and before != "" else FAIL, f"events: {before} in the source, {after} restored")

        readiness = load_readiness()
        code, grants = psql(args.admin_url, restored, readiness.GRANTS_SQL.replace("curia_app", args.app_role))
        _, tables = psql(args.admin_url, restored, readiness.TABLES_SQL)
        bad = [row.rsplit("|", 2)[0] for row in grants.splitlines() if row.rsplit("|", 2)[1] != row.rsplit("|", 2)[2]] if code == 0 else ["the grants block did not run"]
        if tables != "t":
            bad.append("db/0002's tables")
        add("D4", FAIL if bad else PASS, ("; ".join(bad) if bad else f"the role {args.app_role} holds the narrowed grants and db/0002's tables exist"))

        if args.operator_cmd and args.npgsql_base:
            env = dict(os.environ, CURIA_EVENTS_POSTGRES=f"{args.npgsql_base};Database={restored}")
            audit = subprocess.run(shlex.split(args.operator_cmd) + ["lifecycle-audit"], capture_output=True, text=True, env=env, timeout=300, check=False)
            first = (audit.stdout.strip().splitlines() or [""])[0]
            add("D5", PASS if audit.returncode == 0 else FAIL, f"lifecycle-audit exit {audit.returncode}: {first}")
        else:
            add("D5", NOT_CHECKED, "--operator-cmd and --npgsql-base were not both given")
    finally:
        psql(args.admin_url, "postgres", f'DROP DATABASE IF EXISTS "{restored}" WITH (FORCE);')
        shutil.rmtree(workdir, ignore_errors=True)
    return finish()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
