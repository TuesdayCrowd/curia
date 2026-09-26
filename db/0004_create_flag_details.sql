-- Migration 0004, forward-only, applied after db/0003_create_retrieval_index.sql: the private half
-- of a flag (errata G13; R10.62, R11.32, R11.9 addendum).
--
-- __CURIA_APP_ROLE__ is the placeholder 0001 introduces, substituted by
-- Curia.Infrastructure.Migrations.SchemaMigrations.RenderAll before execution.
--
-- =========================================================================================
-- WHY THIS IS NOT AN EVENT, AND WHY IT HAS THE EVENT TABLE'S GRANTS ANYWAY
-- =========================================================================================
-- Every event is a leaf of the Acta (R6.46, R6.47), and GET /v1/log/entries/{index} serves every
-- leaf's input verbatim to anyone (R6.51). A flag written as an event with its raiser and
-- rationale was therefore published -- found by executing it (errata G13, finding 2). A flag now
-- enters the log as `flag.committed`: its kind and a salted commitment to the three columns below
-- plus the salt, and nothing else. This table holds what the commitment commits to.
--
-- It is not a read model. Nothing here is derivable from the log, which holds only commitments,
-- so it is part of the system of record (R11.9 addendum) and is backed up as `events` is. That is
-- also why its grant is the event table's, not the operational tables': INSERT and SELECT, and
-- no UPDATE or DELETE. A row changed after the fact no longer opens the commitment its entry
-- carries, which is detectable; a row deleted is a flag whose raiser nobody can ever learn, which
-- is not. The grant makes the second impossible rather than detectable.
--
-- Legacy flags -- `flag.raised` events written before this migration -- carry their raiser and
-- rationale in the log itself, publicly and permanently. Nothing here reaches them.
CREATE TABLE flag_details (
  event_id    TEXT PRIMARY KEY,   -- the flag.committed event this row opens
  post_id     TEXT NOT NULL,      -- public only once a moderation record adjudicates the flag
  raised_by   TEXT NOT NULL,      -- never published
  rationale   TEXT NOT NULL,      -- screened before storage; never published
  salt        TEXT NOT NULL       -- 32 random bytes, base64url
);

CREATE INDEX ON flag_details (post_id);
CREATE INDEX ON flag_details (raised_by);

GRANT INSERT, SELECT ON flag_details TO __CURIA_APP_ROLE__;
REVOKE UPDATE, DELETE ON flag_details FROM __CURIA_APP_ROLE__;   -- R11.6, R11.32
