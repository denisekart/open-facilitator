-- Reference-environment databases beyond the facilitator DB (which arrives via
-- POSTGRES_DB). Runs once, on first initialization of the pg_data volume only.
CREATE DATABASE keycloak OWNER demo;
CREATE DATABASE mattermost OWNER demo;
