-- Migration 0003, forward-only, applied after db/0002_create_operational_state.sql: the
-- vector half of hybrid retrieval (§9.2, R9.4) -- pgvector, and one table of post embeddings.
--
-- __CURIA_APP_ROLE__ is the placeholder 0001 introduces, substituted by
-- Curia.Infrastructure.Migrations.SchemaMigrations.RenderAll before execution.
--
-- =========================================================================================
-- WHY THIS IS A READ MODEL, AND WHAT THAT PERMITS
-- =========================================================================================
-- Nothing here is the system of record. Every row is derived from a `post.accepted` event
-- in `events` by one deterministic computation (the embedding model named in `model`), so
-- the whole table is rebuildable by replay (R11.9) and a change of model is a reindex, not a
-- migration (R11.10). That is why the app role may UPDATE: an upsert keyed on (digest, model)
-- is how a rebuild converges on the same rows without a second write path. It may not DELETE:
-- a reindex under a new model writes new rows under the new `model` value and leaves the old
-- ones to an operator, and nothing in the application has a reason to remove a vector.
--
-- `model` is R9.5 made structural: "Embeddings SHALL be versioned and the model recorded per
-- vector." Two vectors compare only under the same model, every query names its model, and a
-- model change is discovered as an empty result under the new name rather than as
-- "mysterious relevance degradation".
--
-- The `embedding` column is deliberately untyped in dimension (`vector`, not `vector(256)`):
-- the dimension belongs to the model, several models may coexist during a reindex, and a
-- typed column would make a model change a schema change. The cost is that pgvector's
-- approximate indexes (HNSW, IVFFlat) need a typed dimension and so are not created here.
-- Nearest-neighbour search is therefore exact, ordered by `<=>` over every row of the model,
-- which is the right trade at this Forum's scale and is stated as the bound it is in the
-- plan: when a model's rows outgrow an exact scan, the fix is a typed column per model with an
-- HNSW index, in a migration of its own.
--
-- CREATE EXTENSION needs a superuser (pgvector's control file is not marked trusted), which
-- the migration runner is and the application role is not. A server without pgvector fails
-- here, at provisioning, and the Infrastructure suite fails with it -- deliberately. A
-- retrieval test that could pass by falling back to lexical search would pass without the
-- extension, which is the recurring failure shape this project's plan names.
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE post_embeddings (
  digest      TEXT NOT NULL,          -- the envelope digest, sha256:<hex>, as every read model keys
  model       TEXT NOT NULL,          -- R9.5: name@version of the model that produced the vector
  post_id     TEXT NOT NULL,
  seq         BIGINT NOT NULL,        -- the post.accepted event's seq, for stable tie-breaks (R9.7)
  embedding   VECTOR NOT NULL,        -- L2-normalized by the embedder; <=> is then cosine distance
  indexed_at  TIMESTAMPTZ NOT NULL,   -- from the injected clock (CS-9), never now()
  PRIMARY KEY (digest, model)
);

CREATE INDEX ON post_embeddings (model, seq);

GRANT SELECT, INSERT, UPDATE ON post_embeddings TO __CURIA_APP_ROLE__;
REVOKE DELETE ON post_embeddings FROM __CURIA_APP_ROLE__;
