-- Migration 0008, forward-only, applied after db/0007_store_leaf_hashes.sql: the keyword channel's index (errata G88, R9.74 to R9.77, R11.114 to R11.116).
--
-- Every name below is unqualified on purpose, as in 0002 to 0007: a test fixture renders this file under a search_path to put it in an isolated schema beside that schema's own events table.
-- __CURIA_APP_ROLE__ is substituted by Curia.Infrastructure.Migrations.SchemaMigrations.Render* before execution.
--
-- WHAT IT HOLDS. For each discussion post and each index version, search_docs holds the post's filters and search_terms the frequency of every key in its title, tags and body. Both are a function of
-- the post, the tokenizer, the tag-fold table and the 4,096-key cap, and of nothing else; R9.72's list of ignored words is a query's and is never applied here. The Forum writes them in the very
-- transaction that appends the post (a post that cannot be indexed is not stored: R11.115), and rebuilds them from the log by an insert-only reconcile. Nothing reads them in this migration's
-- release: the keyword channel is unchanged, and `curia-operator search-audit` compares the rows with the log.
--
-- APPEND-ONLY BY GRANT. The role gets SELECT and INSERT and nothing else: a row is never updated or deleted (R11.114), because a change of tokenizer, key function, fold table or cap is a new
-- index_version written beside the old, and an old version's rows are unread, exactly as post_embeddings handles a model id. The explicit REVOKE is for a database that already holds a broader grant.
--
-- KEYS. term holds a key (SearchIndex.Key): the text itself when it is at most 64 UTF-8 bytes and does not begin with '#', otherwise '#' and the unpadded base64url SHA-256 of its UTF-8 bytes, so a
-- btree entry never exceeds its ceiling and a tag cannot spell another key's hash. COLLATE "C" on term and tags_folded: equality does not depend on a collation, but a btree built under a glibc or ICU
-- collation is the classic index that corrupts on an operating-system upgrade, and these keys are not text anyone sorts.
--
-- THE VERSION ROW. search_index_versions records, once per index version, the digest of the tokenizer's letter-and-case tables and of the tag-fold table the rows were written under. The Forum refuses
-- to start when the running runtime's digest differs from the one recorded (R11.116): a tokenizer an upgrade can change is a different index under the same number.
CREATE TABLE IF NOT EXISTS search_docs (
    seq             BIGINT      NOT NULL,
    index_version   SMALLINT    NOT NULL,
    post_id         TEXT        NOT NULL,
    digest          TEXT        NOT NULL,
    board           TEXT        NOT NULL,
    kind            TEXT        NOT NULL,
    author          TEXT        NOT NULL,
    tags_folded     TEXT[] COLLATE "C" NOT NULL,
    terms_truncated BOOLEAN     NOT NULL,
    indexed_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT search_docs_pkey PRIMARY KEY (seq, index_version)
);

CREATE INDEX IF NOT EXISTS search_docs_board   ON search_docs (index_version, board, seq);
CREATE INDEX IF NOT EXISTS search_docs_author  ON search_docs (index_version, author, seq);
CREATE INDEX IF NOT EXISTS search_docs_digest  ON search_docs (index_version, digest);
CREATE INDEX IF NOT EXISTS search_docs_post_id ON search_docs (index_version, post_id);
-- Task 0c (item iii): a conjunctive tag filter (@>) is 4.6 ms without it and 0.33 ms with it at 12,000 posts; a btree cannot serve @> at all.
CREATE INDEX IF NOT EXISTS search_docs_tags_folded ON search_docs USING GIN (tags_folded);

CREATE TABLE IF NOT EXISTS search_terms (
    index_version SMALLINT NOT NULL,
    term          TEXT COLLATE "C" NOT NULL,
    seq           BIGINT   NOT NULL,
    title_tf      INTEGER  NOT NULL,
    tag_tf        INTEGER  NOT NULL,
    body_tf       INTEGER  NOT NULL,
    CONSTRAINT search_terms_pkey PRIMARY KEY (index_version, term, seq)
);

CREATE TABLE IF NOT EXISTS search_index_versions (
    index_version    SMALLINT    NOT NULL,
    tokenizer_digest BYTEA       NOT NULL CHECK (octet_length(tokenizer_digest) = 32),
    fold_digest      BYTEA       NOT NULL CHECK (octet_length(fold_digest) = 32),
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT search_index_versions_pkey PRIMARY KEY (index_version)
);

REVOKE UPDATE, DELETE, TRUNCATE ON search_docs, search_terms, search_index_versions FROM __CURIA_APP_ROLE__;
GRANT SELECT, INSERT ON search_docs, search_terms, search_index_versions TO __CURIA_APP_ROLE__;
