#!/usr/bin/env python3
"""A test double for R11.20's external signer: a separate process holding the registered key.

Speaks the reference client's signer protocol (src/Curia.Client/AgentSigners.cs, ExternalSigner):

    <command> describe   -> one line of JSON: {"alg": ..., "kid": ..., "public_key": <base64 SPKI>}
    <command> sign       -> reads base64url signing input on stdin, writes a base64url
                            IEEE P1363 (r || s) ES256 signature on stdout

It is invoked as `test-signer.py <key-directory> <verb>` through a one-line wrapper the tests
write, because ExternalSigner runs a single command with the verb as its only argument.

P-256 is implemented here in the standard library alone, deliberately. The signatures are
verified by .NET, so this is a second, independent implementation of the primitive rather than
a copy of the first: a signer that agreed with the client only because both called one library
would prove the plumbing and nothing about the signature.

The key directory holds:
    d              the private scalar, lowercase hex
    describe.json  what `describe` prints
    refuse         if present, `sign` refuses (exit 1) -- a signer that is up and declines
    signed.log     one line appended per signature, so a test can show the signer was used
"""

import base64
import hashlib
import os
import secrets
import sys

P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
A = P - 3
G = (
    0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296,
    0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5,
)


def add(p, q):
    if p is None:
        return q
    if q is None:
        return p
    if p[0] == q[0] and (p[1] + q[1]) % P == 0:
        return None
    if p == q:
        slope = (3 * p[0] * p[0] + A) * pow(2 * p[1], -1, P) % P
    else:
        slope = (q[1] - p[1]) * pow(q[0] - p[0], -1, P) % P
    x = (slope * slope - p[0] - q[0]) % P
    return (x, (slope * (p[0] - x) - p[1]) % P)


def multiply(k, point):
    result = None
    while k:
        if k & 1:
            result = add(result, point)
        point = add(point, point)
        k >>= 1
    return result


def sign(d, message):
    # SHA-256's output is exactly P-256's order length, so no truncation step applies.
    z = int.from_bytes(hashlib.sha256(message).digest(), "big")
    while True:
        k = secrets.randbelow(N - 1) + 1
        r = multiply(k, G)[0] % N
        if r == 0:
            continue
        s = pow(k, -1, N) * (z + r * d) % N
        if s == 0:
            continue
        return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def unbase64url(text):
    text = text.strip()
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


def main():
    if len(sys.argv) != 3:
        print("usage: test-signer.py <key-directory> describe|sign", file=sys.stderr)
        return 2

    directory, verb = sys.argv[1], sys.argv[2]

    if verb == "describe":
        with open(os.path.join(directory, "describe.json"), encoding="utf-8") as described:
            sys.stdout.write(described.read())
        return 0

    if verb == "sign":
        if os.path.exists(os.path.join(directory, "refuse")):
            print("this signer is configured to refuse", file=sys.stderr)
            return 1

        with open(os.path.join(directory, "d"), encoding="ascii") as scalar:
            d = int(scalar.read().strip(), 16)

        signature = sign(d, unbase64url(sys.stdin.read()))
        with open(os.path.join(directory, "signed.log"), "a", encoding="ascii") as log:
            log.write("signed\n")

        sys.stdout.write(base64.urlsafe_b64encode(signature).decode("ascii").rstrip("="))
        return 0

    print(f"unknown verb: {verb}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
