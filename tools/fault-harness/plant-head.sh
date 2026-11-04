#!/bin/bash
# plant-head.sh <client-home> <from-dir> <to-dir>: copies the head the client retained for one Forum into the directory it keeps for another,
# so the next verification against the second Forum meets a retained head that Forum did not serve (errata G59, D111: a signed-versus-signed
# FAILED from a head the operator really signed over a different tree).
# <client-home> is the verifier's home (the one CURIA_CLIENT_HOME names, holding logs/). <from-dir> and <to-dir> are the NAMES of two
# directories under <client-home>/logs, as `ls <client-home>/logs` shows them. This script never computes such a name: the name is the origin
# plus a hash (HeadStore.DirectoryFor), and a second computation of it would be a second implementation. Make the target directory by running
# one verification (curia verify ...) against the second Forum first; it must already exist.
# Refuses (exit 2, nothing copied) for: a wrong number of arguments; a home that is not a directory holding logs/; a name that is empty, holds
# a slash or a backslash, or is . or ..; the same name twice; a source directory with no head.json; a target directory that does not exist
# ("run one verification against that Forum first"). It overwrites the target's head.json (that is the plant) and checks the copy byte for
# byte. Exit 0 when the copy is identical; 1 when the copy could not be made or differs. It touches no database and no other file.
set -euo pipefail
die(){ echo "refusing: $*" >&2; exit 2; }
[ "$#" -eq 3 ] || die "usage: plant-head.sh <client-home> <from-dir> <to-dir>   (the two names are directories under <client-home>/logs)"
home=$1; from=$2; to=$3
[ -d "$home/logs" ] || die "'$home' is not a client home: it has no logs/ directory"
for name in "$from" "$to"; do
  [ -n "$name" ] || die "a directory name is empty"
  case "$name" in .|..|*/*|*\\*) die "'$name' is not a directory name under logs/ (no slash, backslash, . or ..)";; esac
done
[ "$from" != "$to" ] || die "the source and the target are the same directory"
[ -f "$home/logs/$from/head.json" ] || die "'$from' has no retained head (logs/$from/head.json): run one verification against that Forum first"
[ -d "$home/logs/$to" ] || die "the target directory 'logs/$to' does not exist: run one verification against that Forum first, so that the client makes it (this script never names it itself)"
cp "$home/logs/$from/head.json" "$home/logs/$to/head.json" || { echo "plant-head: the copy failed" >&2; exit 1; }
cmp -s "$home/logs/$from/head.json" "$home/logs/$to/head.json" || { echo "plant-head: the copy differs from the source" >&2; exit 1; }
echo "planted logs/$from/head.json into logs/$to/head.json ($(wc -c < "$home/logs/$to/head.json" | tr -d ' ') bytes, identical)"
