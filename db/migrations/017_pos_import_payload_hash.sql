-- BrewForge - migration 017: fingerprint the file behind a POS import idempotency key.
--
-- A retried upload under the same Idempotency-Key must carry the same file to replay the
-- original result; the same key sent with a different file is a client mistake and is refused
-- (409), not silently answered with the wrong import. This stores a hash of the imported file
-- beside the key so the two can be told apart. Rows written before this stay NULL and are
-- replayed without the check (there is nothing to compare them against).
--
-- Safe to run again, like every script after 001.

ALTER TABLE pos_import_idempotency ADD COLUMN IF NOT EXISTS payload_hash VARCHAR(64);
