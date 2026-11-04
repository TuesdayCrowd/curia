#!/bin/bash
# forks.sh [mark]: the oracle for what the Forum's own checks were meant to keep unique. Read-only SQL on FH_DB, and never the Forum's answer.
# Prints each query under its heading with its rows; exit 1 if any row of the first five, 0 if none, 2 on a bad pair (the database rule of common.sh).
#   1. revision forks: two or more post.accepted revisions sharing one `prev` (post ids and sequences)
#   2. digests stored twice: two or more post.accepted events with one digest (aggregate ids and sequences)
#   3. flag triples recorded twice: private rows (flag_details) joined to their flag.committed entries on event_id, grouped by post, raiser and
#      kind. The raiser is printed because the harness is for a throwaway database and the admin session may read the private store.
#   4. votes: one author, one target, more than once (kind=vote post.accepted)
#   5. bodies stored twice: two or more post.accepted events of one author, kind, board and parent whose signed canonical text holds an identical
#      `body` (the oracle for "the verb was run again": two runs of a verb sign two acts with two nonces and two digests, so section 2 never
#      sees them; a resend stores nothing and so leaves no row). The body is read by pattern and printed only as its length and md5.
#   6. (information only, not counted in the exit) private rows with no entry: an orphan, which the Forum reads nowhere
# With a mark (mark.sh), only groups whose newest member is after the mark's sequence are printed, so a drill is judged on its own rows.
# The `prev` and the vote's `target` are read from the signed canonical text by a PATTERN, never by a jsonb cast: a stored post whose text
# holds a U+0000 (the eleventh exercise's dump does) fails the cast. A change of the canonical key order or spacing would break the
# pattern; the control is a run on a database seeded with a known fork (see README).
set -euo pipefail
. "$(dirname "$0")/common.sh"
check_db
SEQ=0
if [ -n "${1:-}" ]; then
  [[ "$1" =~ ^[A-Za-z0-9._-]+$ ]] && [ -f "$FH_DIR/marks/$1" ] || { echo "no mark $1" >&2; exit 2; }
  SEQ=$(grep '^seq=' "$FH_DIR/marks/$1" | cut -d= -f2)
fi
Q(){ psql -d "$FH_DB" -At -F ' | ' -c "$1"; }
rc=0
section(){
  local out; echo "== $1"; out=$(Q "$2"); if [ -n "$out" ]; then echo "$out"; [ "${3:-count}" = "info" ] || rc=1; else echo "(none)"; fi
}
echo "oracle: events and flag_details by SQL; prev and target read from the canonical text by pattern; mark seq > $SEQ"
section "revision forks (revisions sharing one prev: prev | n | post@seq)" \
  "select substring(payload->>'canonical' from '\"prev\":\"(sha256:[0-9a-f]{64})\"') as prev, count(*), string_agg(payload->>'post_id'||'@'||seq, ', ' order by seq) from events where event_type='post.accepted' and payload->>'kind'='revision' group by 1 having count(*)>1 and max(seq) > $SEQ order by min(seq)"
section "digests stored twice (digest | n | aggregate@seq)" \
  "select payload->>'digest', count(*), string_agg(aggregate_id||'@'||seq, ', ' order by seq) from events where event_type='post.accepted' group by 1 having count(*)>1 and max(seq) > $SEQ order by min(seq)"
section "flag triples recorded twice (post | raiser | kind | n | entry seqs)" \
  "select d.post_id, d.raised_by, e.payload->>'kind', count(*), string_agg(e.seq::text, ',' order by e.seq) from flag_details d join events e on e.event_id=d.event_id where e.event_type='flag.committed' group by 1,2,3 having count(*)>1 and max(e.seq) > $SEQ order by min(e.seq)"
section "votes by one author for one target more than once (author | target | n | seqs)" \
  "select payload->>'author', substring(payload->>'canonical' from '\"target\":\"(sha256:[0-9a-f]{64})\"'), count(*), string_agg(seq::text, ',' order by seq) from events where event_type='post.accepted' and payload->>'kind'='vote' group by 1,2 having count(*)>1 and max(seq) > $SEQ order by min(seq)"
section "bodies stored twice (author | kind | board | parent | body length, md5 | n | post@seq)" \
  "select payload->>'author', payload->>'kind', payload->>'board', coalesce(payload->>'parent', ''), length(b)||', '||md5(b), count(*), string_agg(payload->>'post_id'||'@'||seq, ', ' order by seq) from (select *, substring(payload->>'canonical' from '\"body\":\"((?:[^\"\\\\]|\\\\.)*)\"') as b from events where event_type='post.accepted') t where b is not null group by 1,2,3,4,b having count(*)>1 and max(seq) > $SEQ order by min(seq)"
section "private flag rows with no entry (information only: post | raiser | event id)" \
  "select d.post_id, d.raised_by, d.event_id from flag_details d left join events e on e.event_id=d.event_id where e.event_id is null" info
if [ $rc -ne 0 ]; then echo "ORACLE: ROWS (a uniqueness the Forum states is broken in this database)"; else echo "ORACLE: none"; fi
exit $rc
