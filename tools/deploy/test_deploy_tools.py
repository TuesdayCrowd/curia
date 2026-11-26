"""Unit tests of tools/deploy/readiness.py and tools/deploy/backup-drill.py (errata G83, R11.110).

Run with: python3 -m unittest discover -s tools/deploy -p "test_*.py"

A stub HTTP server stands in for the Forum, so what these hold is the script's own rules: the exit code, the three
statuses, what a reason may carry. The checks against a real Forum are `ReadinessScriptTests` in Curia.Api.Tests.
"""
import http.server
import json
import os
import subprocess
import sys
import threading
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "readiness.py")
DRILL = os.path.join(HERE, "backup-drill.py")
SECRET = "SECRET-BODY-TEXT-8f3a"


class Stub(http.server.BaseHTTPRequestHandler):
    healthy = True

    def log_message(self, *args):
        pass

    def reply(self, status, body, content_type="application/json"):
        raw = body.encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        authority = f"127.0.0.1:{self.server.server_address[1]}"
        if self.headers.get("Host") != authority:
            return self.reply(400, json.dumps({"type": "curia/request/host-not-served"}))
        if self.path == "/health":
            return self.reply(200 if Stub.healthy else 500, json.dumps({"status": "ok"}) if Stub.healthy else SECRET)
        if self.path == "/.well-known/oauth-authorization-server":
            origin = f"http://{authority}"
            return self.reply(200, json.dumps({"issuer": origin, "token_endpoint": origin + "/oauth/token", "jwks_uri": origin + "/oauth/jwks"}))
        if self.path == "/v1/log/jwks":
            return self.reply(200, json.dumps({"keys": [{"kid": "log1"}], "declaration_keys": [{"kid": "d1"}], "operator_keys": [{"kid": "o1"}]}))
        if self.path == "/v1/log/head":
            return self.reply(404, "{}")
        return self.reply(404, "{}")


class ReadinessScript(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Stub)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.origin = f"http://127.0.0.1:{cls.server.server_address[1]}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def setUp(self):
        Stub.healthy = True

    def run_script(self, *args):
        env = {k: v for k, v in os.environ.items() if k not in ("CURIA_EVENTS_POSTGRES", "CURIA_READINESS_DB_URL")}
        run = subprocess.run([sys.executable, "-I", SCRIPT, "--origin", self.origin, *args], capture_output=True, text=True, env=env, check=False)
        rows = {}
        for line in run.stdout.splitlines():
            parts = line.split(None, 1)
            if len(parts) == 2 and parts[0].startswith("R") and parts[0][1:].isdigit():
                for status in ("NOT CHECKED", "PASS", "FAIL"):
                    if parts[1].startswith(status):
                        rows[parts[0]] = status
                        break
        return run, rows

    def test_a_check_that_could_not_run_is_not_checked_and_the_exit_is_8(self):
        run, rows = self.run_script("--allow-http", "--only", "R1,R2,R3,R4,R8")
        self.assertEqual({k: rows[k] for k in ("R1", "R2", "R3", "R4", "R8")}, dict.fromkeys(("R1", "R2", "R3", "R4", "R8"), "PASS"), run.stdout)
        self.assertEqual("NOT CHECKED", rows["R5"])
        self.assertEqual(8, run.returncode, run.stdout)

    def test_a_failing_check_makes_the_exit_1_and_names_no_response_body(self):
        Stub.healthy = False
        run, rows = self.run_script("--allow-http", "--only", "R1")
        self.assertEqual("FAIL", rows["R1"], run.stdout)
        self.assertEqual(1, run.returncode)
        self.assertNotIn(SECRET, run.stdout + run.stderr)

    def test_no_kid_given_is_not_checked_never_passed(self):
        run, rows = self.run_script("--only", "R6")
        self.assertEqual("NOT CHECKED", rows["R6"], run.stdout)
        self.assertEqual(8, run.returncode)

    def test_the_announced_kids_pass_and_a_wrong_one_fails(self):
        good, rows = self.run_script("--only", "R6", "--declaration-kid", "d1", "--operator-kid", "o1", "--log-kid", "log1")
        self.assertEqual("PASS", rows["R6"], good.stdout)
        wrong, rows = self.run_script("--only", "R6", "--declaration-kid", "d1", "--operator-kid", "other", "--log-kid", "log1")
        self.assertEqual("FAIL", rows["R6"], wrong.stdout)
        self.assertEqual(1, wrong.returncode)

    def test_a_missing_head_is_a_failure_of_the_timer_check(self):
        run, rows = self.run_script("--only", "R7")
        self.assertEqual("FAIL", rows["R7"], run.stdout)

    def test_the_burst_is_opt_in(self):
        run, rows = self.run_script("--only", "R9")
        self.assertEqual("NOT CHECKED", rows["R9"], run.stdout)

    def test_a_bad_origin_is_a_usage_error_and_runs_nothing(self):
        run = subprocess.run([sys.executable, "-I", SCRIPT, "--origin", "forum.example"], capture_output=True, text=True, check=False)
        self.assertEqual(2, run.returncode)
        self.assertNotIn("PASS", run.stdout)


class BackupDrill(unittest.TestCase):
    def test_with_no_pg_dump_on_the_path_the_drill_is_not_checked_and_never_passes(self):
        env = {"PATH": "/nonexistent"}
        run = subprocess.run(
            [sys.executable, "-I", DRILL, "--admin-url", "postgresql://u@localhost/postgres", "--source-db", "x"], capture_output=True, text=True, env=env, check=False
        )
        self.assertEqual(8, run.returncode, run.stdout + run.stderr)
        self.assertIn("NOT CHECKED", run.stdout)
        self.assertNotIn("PASS", run.stdout)


if __name__ == "__main__":
    unittest.main()
