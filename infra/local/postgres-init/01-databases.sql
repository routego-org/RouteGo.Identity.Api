-- Runs once, when the local Postgres volume is first created (docker-entrypoint-initdb.d).
-- Local-only credentials; on RDS the database and user are created by the DB admin and the
-- password lives in Secrets Manager under routego/identity/db.
--
-- identity_user OWNS the database so the migrations can create the schemas and extensions they
-- need, and has CREATEDB because the test fixture creates a throwaway database per test run on
-- the server named by the developer's user-secrets.
CREATE USER identity_user WITH PASSWORD 'local-only' CREATEDB;
CREATE DATABASE routego_identity OWNER identity_user;
