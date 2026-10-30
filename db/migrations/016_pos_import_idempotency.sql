-- BrewForge - migration 016: a durable ledger for POS import idempotency.
--
-- A POS import carries an optional Idempotency-Key so a client can retry a timed-out
-- upload without importing the file twice. That guard used to be a scan of the user's
-- recent audit entries for a matching key: it missed keys older than the window, and,
-- being a check separate from the write, let two concurrent retries both import. This
-- table makes the key a primary key, so the database itself admits only the first
-- import of a key and a later or racing retry is answered with the stored result.
--
-- job_number points at the audit_log entry (entity_type 'SalesImport', action
-- 'POS_IMPORT') that holds the import's result. Safe to run again, like every script
-- after 001.

CREATE TABLE IF NOT EXISTS pos_import_idempotency (
    user_id          BIGINT NOT NULL,
    idempotency_key  VARCHAR(200) NOT NULL,
    job_number       BIGINT NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, idempotency_key)
);

ALTER TABLE pos_import_idempotency DROP CONSTRAINT IF EXISTS fk_pos_import_idempotency_user_id;
ALTER TABLE pos_import_idempotency ADD CONSTRAINT fk_pos_import_idempotency_user_id
    FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE CASCADE;
