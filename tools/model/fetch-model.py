#!/usr/bin/env python3
"""Fetch the embedding model by digest (errata G65, Ruling 4).

SEAM STUB (D10a Task 1): reads nothing, fetches nothing, checks nothing, and exits 2. Task 2 writes it:
download each file named in models/bge-small-en-v1.5/manifest.json to a temporary name, check sha256, rename on equality;
--verify-only checks a directory and downloads nothing.
"""
import argparse
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description="Fetch the embedding model by digest.")
    parser.add_argument("--out", help="the directory to fetch into (or to verify)")
    parser.add_argument("--verify-only", action="store_true", help="check a directory against the manifest; download nothing")
    parser.parse_args()
    print("fetch-model.py is a seam stub: not implemented", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
