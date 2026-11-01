# Fault-injecting TCP proxy: FH_PROXY_PORT -> FH_PG_HOST:FH_PG_PORT, for the Forum's connection only.
# The mode is re-read from $FH_DIR/pgproxy.mode on every packet (a missing file means passthru):
#   passthru          transparent
#   cutcommit[:<p>]   when a connection that has sent an INSERT INTO "public".events sends its simple-query COMMIT, with probability p
#                     (default 1) the COMMIT is forwarded, Postgres is given 60 ms to commit, then both sockets are closed and
#                     Postgres's answer never reaches the client (muted): the act is committed and the client's connection breaks
#   dropcancel        a Postgres cancel request (a connection's first real packet, code 80877102) is dropped, so a client timeout cannot
#                     cancel the query
# The proxy serves plaintext only: an 8-byte SSLRequest (code 80877103) or GSSENCRequest (80877104) opening a connection is answered
# "N" by the proxy itself and not forwarded, and the next packet is then the connection's first. A client that insists on TLS
# (SSL Mode=Require) is refused; Npgsql's default (SSL Mode=Prefer) falls back to plaintext.
#   delaycommit:<s>   the simple-query COMMIT is held s seconds, then forwarded even if the client has gone
# Log: $FH_DIR/pgproxy.log. Run with: python3 -I pgproxy.py
import asyncio, os, random, sys, time
DIR = os.environ["FH_DIR"]
PORT = int(os.environ.get("FH_PROXY_PORT", "5433"))
PG_HOST = os.environ.get("FH_PG_HOST", "127.0.0.1")
PG_PORT = int(os.environ.get("FH_PG_PORT", "5432"))
MODE = os.path.join(DIR, "pgproxy.mode")
LOG = open(os.path.join(DIR, "pgproxy.log"), "a")
CANCEL = bytes([0x04, 0xD2, 0x16, 0x2E])
SSL_REQUEST = bytes([0x04, 0xD2, 0x16, 0x2F])
GSS_REQUEST = bytes([0x04, 0xD2, 0x16, 0x30])

def mode():
    try: return open(MODE).read().strip() or "passthru"
    except Exception: return "passthru"

def log(s):
    LOG.write("%.3f %s\n" % (time.time(), s)); LOG.flush()

def cut_probability(m):
    if m == "cutcommit": return 1.0
    try: return float(m.split(":", 1)[1])
    except Exception: return 0.0

async def handle(cr, cw):
    try:
        sr, sw = await asyncio.open_connection(PG_HOST, PG_PORT)
    except Exception:
        cw.close(); return
    st = {"first": True, "ins": False, "mute": False}

    async def c2s():
        try:
            while True:
                d = await cr.read(65536)
                if not d: break
                m = mode()
                if st["first"] and len(d) == 8 and d[4:8] in (SSL_REQUEST, GSS_REQUEST):
                    log("encryption request answered N (plaintext only)")
                    cw.write(b"N"); await cw.drain(); continue
                if st["first"]:
                    st["first"] = False
                    if len(d) == 16 and d[4:8] == CANCEL:
                        if m.startswith("dropcancel"):
                            log("dropped cancel request"); sw.close(); cw.close(); return
                        log("cancel forwarded")
                if b'INTO "public".events' in d: st["ins"] = True
                if d[:1] == b"Q" and b"COMMIT" in d[:16]:
                    was = st["ins"]; st["ins"] = False
                    log("commit ins=%s mode=%s" % (was, m))
                    if was and m.startswith("cutcommit") and random.random() < cut_probability(m):
                        st["mute"] = True
                        sw.write(d); await sw.drain(); await asyncio.sleep(0.06)
                        log("cut after COMMIT"); sw.close(); cw.close(); return
                    if m.startswith("delaycommit:"):
                        s = float(m.split(":", 1)[1]); log("holding COMMIT %.1fs" % s)
                        async def later(d=d, s=s):
                            await asyncio.sleep(s)
                            try: sw.write(d); await sw.drain(); log("COMMIT forwarded after delay")
                            except Exception as e: log("forward failed %r" % e)
                        asyncio.ensure_future(later()); continue
                sw.write(d); await sw.drain()
        except Exception: pass
        # client gone: with delaycommit keep the server socket open so a held COMMIT can still land
        m = mode()
        if m.startswith("delaycommit:"):
            try: await asyncio.sleep(float(m.split(":", 1)[1]) + 1)
            except Exception: pass
        try: sw.close()
        except Exception: pass

    async def s2c():
        try:
            while True:
                d = await sr.read(65536)
                if not d: break
                if st["mute"]: continue
                cw.write(d); await cw.drain()
        except Exception: pass
        try: cw.close()
        except Exception: pass

    await asyncio.gather(c2s(), s2c())

async def main():
    s = await asyncio.start_server(handle, "127.0.0.1", PORT)
    async with s: await s.serve_forever()

asyncio.run(main())
