-- Migration 0007, forward-only, applied after db/0006_events_insert_columns.sql: the log stores each event's leaf hash, and the lookups the serving
-- routes make by payload are indexed (errata G87 (the read cost is bounded), R6.69).
--
-- Every name below is unqualified on purpose, as in 0002, 0005 and 0006: a test fixture renders this file under a search_path to put it in an isolated schema beside that
-- schema's own events table. __CURIA_APP_ROLE__ is substituted by Curia.Infrastructure.Migrations.SchemaMigrations.Render* before execution.
--
-- WHY A COLUMN. Every request that serves a post hashes the leaf of every row of the log (R6.46's six-member entry, canonicalized and SHA-256'd) to produce one root, because the
-- Forum may not carry the tree between requests. That cost grows with the bytes of the log. A leaf is a pure function of its own row (seq is not a member of it, R6.46), so the
-- append computes it before the INSERT and writes it in the same statement; nothing is ever updated, so nothing can go stale. It is derived data in the system of record, and
-- the controls that make it safe are the ones that do not read it: curia-operator sign-head recomputes every leaf before it signs and refuses a log in which a stored leaf differs
-- from its recomputation, curia-operator lifecycle-audit reports a difference and a row with no leaf, and a reader (curia verify, curia-testis) recomputes the leaf from the
-- entry the log serves, so a wrong stored hash makes an inclusion proof FAIL at the client and is never a forged success. A mismatch is detected and reported, never healed.
--
-- NULLABLE ON PURPOSE. A row appended before this migration has no leaf. The Forum serves such a row by recomputation (correct, slow, counted). The owner fills them with
-- `curia-operator backfill-leaves`, run with the database owner's role, because the application role holds no UPDATE (R11.6). A later migration may set NOT NULL.
--
-- THE GRANT. 0006 narrowed INSERT on events to the columns the Forum supplies and said that a later migration adding such a column must grant it in the same file. This one
-- does. The role gets INSERT on leaf_hash and nothing else new: no UPDATE of any column (0001's REVOKE stands), so a stored leaf is write-once by the role.
ALTER TABLE events ADD COLUMN IF NOT EXISTS leaf_hash BYTEA CHECK (leaf_hash IS NULL OR octet_length(leaf_hash) = 32);
GRANT INSERT (leaf_hash) ON events TO __CURIA_APP_ROLE__;   -- R6.69: never UPDATE

-- The covering index: a read of the leaves alone is index-only (the heap, with a post's bytes in it, is not read).
CREATE INDEX IF NOT EXISTS events_seq_leaf_hash ON events (seq) INCLUDE (leaf_hash);

-- The payload lookups a serving route makes (errata G87): a post by digest, the posts of a parent, the posts of a board, and the votes and verifications that target a digest.
-- Partial, over post.accepted only, so a log's other rows pay nothing. An index costs a little on every INSERT; a Forum without these works and is slower.
-- The signal target's expression reads the canonical envelope's member, as the fold does: it runs only for the rows the predicate admits, which are votes and verifications.
CREATE INDEX IF NOT EXISTS events_post_digest ON events ((payload->>'digest')) WHERE event_type = 'post.accepted';
CREATE INDEX IF NOT EXISTS events_post_parent ON events ((payload->>'parent'), seq) WHERE event_type = 'post.accepted' AND payload->>'parent' IS NOT NULL;
CREATE INDEX IF NOT EXISTS events_post_board ON events ((payload->>'board'), seq) WHERE event_type = 'post.accepted';
CREATE INDEX IF NOT EXISTS events_post_signal_target ON events (((payload->>'canonical')::jsonb->>'target'), seq)
    WHERE event_type = 'post.accepted' AND payload->>'kind' IN ('vote', 'verification');
