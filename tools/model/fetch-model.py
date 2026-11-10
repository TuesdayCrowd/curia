#!/usr/bin/env python3
"""Fetch the embedding model by digest (errata G65, Ruling 4; R11.91, R14.13).

    python3 tools/model/fetch-model.py --out <dir>                 # download what <dir> lacks, check every file, exit 0 when all match
    python3 tools/model/fetch-model.py --out <dir> --verify-only   # check <dir> against the manifest; download nothing

The digests come from models/bge-small-en-v1.5/manifest.json, the same file the Forum embeds, so the digest this script
checks and the digest the Forum checks are one string. Each file is taken from its first reachable source (the repository's
release asset, then the pinned Hugging Face revision), written to a temporary name beside its target, hashed, and renamed
only when the sha256 equals the manifest's; a mismatch deletes the temporary file, names the file, exits non-zero and leaves
any earlier file alone. A source is never trusted: the digest is checked on every use, the cache's included. Standard library only.

The cache is $XDG_CACHE_HOME/curia-model, or ~/.cache/curia-model; --no-cache skips it.
"""
import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MANIFEST = ROOT / "models" / "bge-small-en-v1.5" / "manifest.json"
CHUNK = 1 << 20
TIMEOUT_SECONDS = 60


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(CHUNK), b""):
            digest.update(block)
    return digest.hexdigest()


def cache_dir():
    base = os.environ.get("XDG_CACHE_HOME") or os.path.join(os.path.expanduser("~"), ".cache")
    return Path(base) / "curia-model"


def check(path, name, expected):
    """Returns None when <path> is the file the manifest names, else a sentence naming the file and the cause."""
    if not path.is_file():
        return f"{name}: missing"
    actual = sha256_of(path)
    if actual != expected:
        return f"{name}: digest mismatch (sha256 {actual}, the manifest holds {expected})"
    return None


def download(url, target, expected, name):
    """Fetches <url> to a temporary name beside <target>, renames it only when its sha256 equals <expected>. Returns None or a sentence."""
    handle, temporary = tempfile.mkstemp(prefix=name + ".", suffix=".part", dir=target.parent)
    try:
        digest = hashlib.sha256()
        with os.fdopen(handle, "wb") as out, urllib.request.urlopen(url, timeout=TIMEOUT_SECONDS) as response:
            for block in iter(lambda: response.read(CHUNK), b""):
                digest.update(block)
                out.write(block)
        if digest.hexdigest() != expected:
            return f"{name}: digest mismatch from {url} (sha256 {digest.hexdigest()}, the manifest holds {expected})"
        os.chmod(temporary, 0o644)
        os.replace(temporary, target)
        temporary = None
        return None
    except (urllib.error.URLError, OSError, TimeoutError) as error:
        return f"{name}: could not fetch {url} ({type(error).__name__})"
    finally:
        if temporary is not None and os.path.exists(temporary):
            os.unlink(temporary)


def main():
    parser = argparse.ArgumentParser(description="Fetch the embedding model by digest.")
    parser.add_argument("--out", required=True, help="the directory to fetch into (or to verify)")
    parser.add_argument("--verify-only", action="store_true", help="check a directory against the manifest; download nothing")
    parser.add_argument("--manifest", default=str(DEFAULT_MANIFEST), help="the manifest to read (default: the repository's)")
    parser.add_argument("--no-cache", action="store_true", help="do not read or write the local cache")
    args = parser.parse_args()

    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    out = Path(args.out)
    failures = []

    if args.verify_only:
        for name, entry in manifest["files"].items():
            problem = check(out / name, name, entry["sha256"])
            if problem:
                failures.append(problem)
    else:
        out.mkdir(parents=True, exist_ok=True)
        cache = None if args.no_cache else cache_dir()
        for name, entry in manifest["files"].items():
            expected, target = entry["sha256"], out / name
            if check(target, name, expected) is None:
                continue
            cached = None if cache is None else cache / expected
            if cached is not None and cached.is_file() and check(cached, name, expected) is None:
                shutil.copyfile(cached, target)
                continue
            problems = []
            for url in entry["sources"]:
                problem = download(url, target, expected, name)
                if problem is None:
                    problems = []
                    break
                problems.append(problem)
            if problems:
                failures.extend(problems)
                continue
            if cache is not None:
                try:
                    cache.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(target, cached)
                except OSError:
                    pass  # a cache that cannot be written costs the next run a download and nothing else

    for failure in failures:
        print(failure, file=sys.stderr)
    if failures:
        print("fetch-model.py: the model directory is not the manifest's", file=sys.stderr)
        return 1
    print(f"ok: {', '.join(manifest['files'])} match the manifest's sha256 digests in {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
