# Runbook: the reverse proxy in front of the Forum (errata G83, R7.33)

Audience: the Forum's operator. The recipe is nginx, in three files under `deploy/proxy/` so that the file that is exercised
is the file that ships: `deploy/proxy/curia-http.conf` (the http context: the two limit zones), `deploy/proxy/curia-locations.conf`
(everything a server needs to be the Forum's front) and `deploy/proxy/nginx.conf` (the whole file an operator copies, which
includes the other two). Each is read by `DeploymentDocsTests` against the rule it carries, and a model of the recipe runs in
front of a real Forum in CI (`ProxyForwardingTests`). **What the model cannot show is that nginx honours a stanza as the
model reads it**; the recipe's own header says what nginx ran and when. A platform's edge (a managed load balancer) is not
shipped here: it must do the four things below, and `tools/deploy/readiness.py` is the only check.

## The four obligations

1. **Forward the `Host` as received.** `proxy_set_header Host $http_host`. The Forum compares the header to its one configured
   name (R5.27) on every route, `/health` included; an edge that rewrites it makes the Forum's refusal the edge's.
   `$host` drops the port, which is measured: with `$host` and an origin that has a port the Forum answers `400`
   `curia/request/host-not-served` to its own honest client. A default-port `https` origin hides this, which is why the pack's
   probes run under an origin with a port. The Forum reads no forwarded header for any name (R5.26); a client's
   `X-Forwarded-Host` changes nothing, and the recipe overwrites the three `X-Forwarded-*` from the connection and never appends
   to a client's.
2. **Terminate TLS and leave the origin `https`.** The Forum is told its name by `CURIA_PUBLIC_ORIGIN` and never reads the
   scheme. The Forum is not served under a path prefix: the origin has no path (G82).
3. **Limit the two unauthenticated routes, per address, before the Forum reads them** (R7.33): `GET /v1/enroll/nonce` and
   `POST /oauth/token`. The figures are G83's table in the errata and are changed there and nowhere else; a probe fails a rate or
   a burst above it. **They are provisional, and a cost bound and not an abuse control**: a fleet spreads across addresses at no
   cost and one honest operator behind one address pays in full, so what bounds abuse is possession of a key and the owner's
   attestation. Behind a second proxy the key is that proxy's address: set `real_ip` from a trusted, **named** range, never from
   `X-Forwarded-For`, which a client writes. An IPv6 client can rotate within its /64 for nothing, so the key bounds one host and
   not anyone with an allocation. A fleet of a hundred agents behind one address starting in the same second meets `429`s.
   **Measured (the pack's live run, 2026-11-26): `curia` does not wait and does not retry on a `429`.** It does not read `Retry-After`, exits **8**
   (the code it also uses when a Forum cannot be reached, so the exit code alone does not tell a limit from an outage), and prints the body's title and its
   problem type (`curia/edge/rate-limited`, or `temporarily_unavailable` from the token route): **a script must read the printed type**, and retry itself. An operator with a measured need for more raises the numbers in the errata and then in the recipe.
4. **Answer `429` and `413` in a body a client can read.** An RFC 9457 problem document (`application/problem+json`, types
   `curia/edge/rate-limited` and `curia/request/too-large`), and at `/oauth/token` RFC 6749's error object. nginx's own HTML page
   makes `curia` print `curia/client/unreadable-problem` and exit 3, the failure `AllowedHosts` has (G82).

## The body cap, and what it is not

`client_max_body_size 1m` is R6.39's submission size (1 MiB, 1,048,576 bytes), read from the white paper by the probe. A body of
exactly 1 MiB passes the edge and reaches the Forum; one byte more is the edge's `413`. The Forum's own bound is one byte wider
(below), so the one body that reaches R6.40's `curia/admit/size-exceeded` is exactly 1,048,577 bytes, and the edge never lets it through.
**A client does not screen R6.39 before it signs** (the client signs and sends whatever it is given): through the edge an act of
1,048,577 bytes or more gets the edge's `413`, which the recipe answers as the Forum's own typed problem document
(`curia/request/too-large`), and `curia` prints R11.111's one sentence, exits 3 and keeps nothing (an edge whose `413` is another
page, an HTML one, is a refusal `curia` cannot read the cause of: exit 8, with the resend recipe it prints for any act it kept).

**The Forum bounds a request body itself (G84, R6.68 (rev.); D125 is closed).** It reads at most R6.39's size plus one byte, **1,048,577
bytes**, on every route and for every method: a body whose declared length is larger, or that is sent with no declared length and
passes that count, is the Forum's `413` `curia/request/too-large` before a handler acts on it (at `/oauth/token` RFC 6749's error
object), and a request the host's own reader rejects while a handler reads (too slow, wrongly framed) is answered as a problem document
and never reaches the host's own response writer. A bound set only at the edge would disappear on the day the edge does; this one does not,
and the edge's 1 MiB is a second line in front of it. An edge `413` and the Forum's share a type, `curia/request/too-large`, where the edge
answers it as this recipe does; they are not the same document (the recipe's has an extra `status` member, no `detail` and the title "larger than this Forum reads"), and a client decides by the type. **What the bound does not do:** it bounds the bytes a request may carry, not the memory they cost (a body at the bound costs the Forum several times its size to parse,
measured at about 28 MiB on the post route), and it does not bound the number of bodies in flight: the post route reads before the per-agent gate is entered and three routes need
no credential, so the burst limit above and the owner's memory limit are what bound many bodies at once (G84's non-properties (a) and (b)).

## Installing it

Copy the three files to `/etc/nginx/`, edit the three things `deploy/proxy/nginx.conf`'s header names (the upstream's address,
the literal name in the port-80 redirect, the certificate paths), and check the whole file before reloading:

```bash
nginx -t
```

`server_name` is `_` on purpose: one server, so that a request under a foreign `Host` reaches the Forum, whose refusal is the one the
client sees. A second `default_server` block answering `444` would make the Forum's control the edge's. Port 80 redirects to the
literal name and never to the request's own host.

## Monitoring

The monitor's line sends the origin's `Host`, through the edge, from the host that runs the edge (with a port-bearing origin the
`--resolve` and the URL carry the port):

```bash
curl -fsS --resolve forum.example:443:127.0.0.1 https://forum.example/health
```

This checks name resolution's bypass, the certificate for the name, the `Host`, nginx and the Forum in one request, and it answers
`{"status":"ok"}` after the Host check, reading no store. **A probe by address** (`http://127.0.0.1/health`, a cloud load balancer's
default health check) **sends `Host: 127.0.0.1` and is answered `400` `curia/request/host-not-served` for ever**: configure a platform's
health check with the origin's authority as its `Host` header, and where it cannot, check nginx and run the line above. `/health` is
public and carries no limit: the readiness script's R1 reads it from the public side, and it discloses liveness and nothing else.

## What each probe holds

| Claim | Held by |
|---|---|
| the Host line is `$http_host`; the three `X-Forwarded-*` are overwritten and `Forwarded` is cleared (a focused real-nginx run checked the clearing) | `DeploymentDocsTests` DPK4; `ProxyForwardingTests` PXY1 to PXY3 |
| the two limits are at or below G83's table, and the burst is refused with the Forum asked no more than it allows | DPK4; PXY4, PXY5 (the red twin); readiness R9 through the origin |
| a refusal is a readable body | DPK4; PXY6 |
| a foreign `Host` is the Forum's refusal, not the edge's | PXY7; readiness R2 |
| the monitor line and the by-address sentence above | `RunbookMonitorLineTests` DPK8 |
| the Forum bounds a body at R6.39's size plus one byte, on every route, in both environments | `BodyBoundTests` BND1 to BND5, BND7 to BND9; `BodyBoundKestrelTests` BND6 and BND2k (both environments; the undeclared path); `DeploymentDocsTests` BND11 |
| no Forum code reads a forwarded header | `OriginFenceTests` (ORG13), which reads `Curia.Api.dll` only |
