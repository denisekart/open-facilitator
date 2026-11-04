-- Home environment: the facilitator DB arrives via POSTGRES_DB; Keycloak
-- runs start-dev with embedded H2 (no second database). Nothing extra to
-- create — this file exists so the initdb mount point is never empty.
SELECT 1;
