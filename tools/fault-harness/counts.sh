#!/bin/bash
# counts.sh [markname]: read-only SQL on FH_DB.
#  - events since the mark's seq (the whole log if none), by actor and type
#  - UNINDEXED: post.accepted events of a discussion kind whose digest is not in post_embeddings. No baseline is subtracted: the kinds
#    that are not placed in the vector index (vote, verification) are not counted at all.
#    The discussion kinds are PostKinds.IsDiscussion in src/Curia.Domain/Content/PostKind.cs: question, answer, finding, comment,
#    revision. A post.accepted payload has a top-level "kind" member (written by the ingest code), read here as payload->>'kind' with no
#    cast: casting the signed `canonical` text to jsonb fails on a stored post whose text holds a U+0000.
#  - answer.accepted since the mark, and in the last 24 hours, by accepted_by
#  - posts in the last 24 hours by author (the posting budget's count: post.accepted events of every kind, payload author)
#  - flags since the mark; digests stored twice
set -euo pipefail
. "$(dirname "$0")/common.sh"
check_db
Q(){ psql -d "$FH_DB" -At -F ' | ' -c "$1"; }
SEQ=0
if [ -n "${1:-}" ]; then
  [ -f "$FH_DIR/marks/$1" ] || { echo "no mark $1" >&2; exit 2; }
  SEQ=$(grep '^seq=' "$FH_DIR/marks/$1" | cut -d= -f2)
fi
echo "== events since mark ${1:-<none>} (seq > $SEQ), by actor and type"
Q "select coalesce(actor_id,'-'), event_type, count(*) from events where seq > $SEQ group by 1,2 order by 1,2"
echo "== unindexed (post.accepted of kind question|answer|finding|comment|revision whose digest is not in post_embeddings)"
U=$(psql -d "$FH_DB" -Atc "select count(*) from events e where e.event_type='post.accepted' and e.payload->>'kind' in ('question','answer','finding','comment','revision') and not exists (select 1 from post_embeddings p where p.digest = e.payload->>'digest')")
echo "unindexed=$U"
echo "== answer.accepted since the mark, by actor_id and by payload accepted_by"
Q "select coalesce(actor_id,'-'), coalesce(payload->>'accepted_by','-'), count(*) from events where event_type='answer.accepted' and seq > $SEQ group by 1,2 order by 1,2"
echo "== answer.accepted in the last 24 hours, by accepted_by (the acceptance budget's count)"
Q "select payload->>'accepted_by', count(*) from events where event_type='answer.accepted' and server_ts > now() - interval '24 hours' group by 1 order by 1"
echo "== posts in the last 24 hours by author (the posting budget's count: post.accepted events, payload author, all kinds)"
Q "select payload->>'author', count(*), min(server_ts) from events where event_type='post.accepted' and server_ts > now() - interval '24 hours' group by 1 order by 1"
echo "== flags since the mark (flag.committed; the aggregate is flag:<post id>, the raiser is private)"
Q "select aggregate_id, count(*) from events where event_type='flag.committed' and seq > $SEQ group by 1 order by 1"
echo "== digests stored twice (post.accepted grouped by digest having count > 1)"
Q "select payload->>'digest', count(*), string_agg(payload->>'post_id', ',' order by seq) from events where event_type='post.accepted' group by 1 having count(*) > 1"
echo "(end)"
