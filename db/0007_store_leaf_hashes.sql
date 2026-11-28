-- Migration 0007, forward-only, applied after db/0006_events_insert_columns.sql: the log stores each event's leaf hash and the signal target of a vote or verification, and the
-- lookups the serving routes make by payload are indexed (errata G87 (the read cost is bounded), R6.69, R11.6 (add.)).
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
-- does. The role gets INSERT on leaf_hash and signal_target and nothing else new: no UPDATE of any column (0001's REVOKE stands), so each is write-once by the role.
--
-- WHAT IS INDEXED HERE, AND WHY NOT AN AUTHOR'S WORDS (R11.6 (add.)). Every expression or column db/ indexes over a payload member is total and bounded over every input ADMIT admits:
-- R6.39 admits a 256 KiB string and a \u0000 escape, a btree row is limited to about 2,700 bytes and a jsonb cannot hold U+0000, so an index over an author's own words (a parent, a
-- board, a member read out of the canonical text) is a way for a valid post to answer 503. The only payload member indexed is the post's digest, which the Forum makes (71 bytes). The
-- signal target of a vote or verification is a member of the SIGNED envelope, so the append reads it with the reader the fold uses (PostEnvelope.Target) and writes it to its own column,
-- whose CHECK is the form VERIFY already required (a prefixed SHA-256 digest, 71 ASCII bytes). Nothing is ever parsed out of the canonical text by SQL.
--
-- NULLABLE ON PURPOSE, as leaf_hash is: a vote appended before this migration has no target column. curia-operator backfill-leaves fills it (database owner's role), and
-- curia-operator lifecycle-audit reports a vote or verification whose column is NULL or is not its canonical's target (exit 7): a NULL column is a vote that does not count toward a
-- finding's verification, a deny nobody decided, so the audit and the start-up check are what keep it from being silent.
ALTER TABLE events ADD COLUMN IF NOT EXISTS leaf_hash BYTEA CHECK (leaf_hash IS NULL OR octet_length(leaf_hash) = 32);
-- The signal target (R8.55) of a vote or verification, written by the append from the verified envelope. VERIFY has already required the prefixed digest form, so the value is 71 ASCII
-- bytes or absent; the CHECK says so, and so bounds every index over it.
ALTER TABLE events ADD COLUMN IF NOT EXISTS signal_target TEXT
    CHECK (signal_target IS NULL OR (event_type = 'post.accepted' AND octet_length(signal_target) = 71 AND signal_target ~ '^sha256:[0-9a-f]{64}$'));
GRANT INSERT (leaf_hash, signal_target) ON events TO __CURIA_APP_ROLE__;   -- R6.69: never UPDATE

-- The covering index: a read of the leaves alone is index-only (the heap, with a post's bytes in it, is not read).
CREATE INDEX IF NOT EXISTS events_seq_leaf_hash ON events (seq) INCLUDE (leaf_hash);

-- The lookups a serving route makes (errata G87): a post by digest, and the votes and verifications that target a digest. Partial, so a log's other rows pay nothing. An index costs a
-- little on every INSERT; a Forum without these works and is slower. events_post_parent and events_post_board are NOT here: no read clause reads either, and an index over an author-chosen
-- string refuses valid posts (R11.6 (add.)). The stage that reads them adds them with its reader, over md5() of the member and with an equality recheck in the query.
CREATE INDEX IF NOT EXISTS events_post_digest ON events ((payload->>'digest')) WHERE event_type = 'post.accepted';   -- server-made, 71 bytes: total
CREATE INDEX IF NOT EXISTS events_post_signal_target ON events (signal_target, seq) WHERE signal_target IS NOT NULL;
