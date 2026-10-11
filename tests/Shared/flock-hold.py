#!/usr/bin/env python3
"""Hold an exclusive flock on a path from another process, for R6.53 (add.)'s facts.

    flock-hold.py <path>

Opens <path> (creating it at 0600 if absent), takes flock(LOCK_EX), prints `held` on its own line
once the lock is held, and keeps it until its standard input closes. The test closes stdin, or
kills the process, to release it.

A separate process because the property under test is that a lock held by *another* process
stops this client from reading, comparing and replacing the retained head. .NET's FileShare.None
is a flock on Unix, so this is the lock a second `curia verify` would hold.
"""

import fcntl
import os
import sys


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: flock-hold.py <path>", file=sys.stderr)
        return 2

    descriptor = os.open(sys.argv[1], os.O_RDWR | os.O_CREAT, 0o600)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX)
        print("held", flush=True)
        sys.stdin.read()
    finally:
        os.close(descriptor)
    return 0


if __name__ == "__main__":
    sys.exit(main())
