-- BrewForge - migration 022: a record of the schema scripts applied (BE-04).
--
-- An API answering 200 does not prove the database has every migration. The initializer now records each
-- script of db/migrations it has applied, with a checksum of its text, so that GET /system/info can say which
-- scripts the database is missing or holds in an older form. A database prepared only by the PostgreSQL
-- container's init (no API start with Database:InitializeOnStartup) has an empty log until the API applies
-- the scripts, and is reported as not ready.
--
-- Safe to run again, like every script after 001.

CREATE TABLE IF NOT EXISTS schema_script_log (
    name                   VARCHAR(120) NOT NULL,
    checksum               CHAR(64) NOT NULL,
    applied_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (name)
);
