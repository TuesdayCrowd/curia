-- Stage 2 (R11.6, R11.9): the append-only event log and the application role
-- constrained to it. This is the forward-only migration; nothing here is ever
-- rewritten in place -- a later change to this table is a new numbered file.
--
-- The events table, its two indexes, and the REVOKE line are Appendix D's DDL
-- verbatim (curia-agent-forum-WHITEPAPER.md, "Appendix D -- Database schema").
-- The CREATE ROLE and the two GRANT statements are Stage 2 additions: Appendix
-- D's abridged excerpt shows only the REVOKE, but R11.6's own words are "append-
-- only should be enforced by the grant, not merely by the code's intentions" --
-- a REVOKE against a role holding no grants at all proves nothing, so the role
-- and its INSERT/SELECT grants have to exist somewhere, and this migration is
-- that somewhere.
--
-- __CURIA_APP_ROLE__ and __CURIA_APP_ROLE_PASSWORD__ are literal placeholder
-- tokens, not real values: this file is rendered before it is executed, by
-- Curia.Infrastructure.Migrations.EventStoreSchema.Render (see that type's
-- remarks). A real deployment renders it with the deployment's own role name
-- and a generated secret; the test harness (Curia.Infrastructure.Tests'
-- PostgresDatabaseFixture) renders it with a fresh, throwaway name and
-- password for every test run, so concurrent runs never share a role and
-- never collide.
CREATE ROLE __CURIA_APP_ROLE__
  LOGIN
  PASSWORD '__CURIA_APP_ROLE_PASSWORD__'
  NOSUPERUSER NOCREATEDB NOCREATEROLE;

-- ============ append-only event log ============
CREATE TABLE events (
  seq             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_id        TEXT UNIQUE NOT NULL,
  event_type      TEXT NOT NULL,
  aggregate_id    TEXT NOT NULL,
  actor_id        TEXT,
  payload         JSONB NOT NULL,
  server_ts       TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- CS-9 / T1.2 / T4.2: the DEFAULT above is Appendix D's own, left in place
-- verbatim rather than edited out -- but nothing relies on it. Every write
-- Curia.Infrastructure.PostgresEventStore performs supplies server_ts
-- explicitly, read once per AppendAsync call from the injected TimeProvider
-- (CS-9), exactly as Curia.Application.Tests.InMemory.InMemoryEventStore
-- already does. Resolving the DDL-vs-Clock-port contradiction itself --
-- i.e. whether the DEFAULT should be removed -- is T4.2, recorded and open,
-- not this migration's job.
GRANT USAGE ON SCHEMA public TO __CURIA_APP_ROLE__;
GRANT INSERT, SELECT ON events TO __CURIA_APP_ROLE__;
REVOKE UPDATE, DELETE ON events FROM __CURIA_APP_ROLE__;   -- R11.6
CREATE INDEX ON events (aggregate_id, seq);
CREATE INDEX ON events (event_type, seq);
