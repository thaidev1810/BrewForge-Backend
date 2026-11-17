-- BrewForge - migration 015: a refresh token can be revoked only once.
--
-- A refresh token is single-use: exchanging it revokes it by writing one audit_log
-- row (entity_type = 'RefreshToken', action = 'REVOKE', entity_id = the token id).
-- The "is it revoked yet?" check and that write were not atomic, so two requests
-- presenting the same token at once could both pass the check and both rotate it,
-- handing out two new sessions from one token. This partial unique index makes the
-- database the referee: of two concurrent revocations of the same token id, exactly
-- one insert succeeds and the other is rejected, which the refresh path turns into
-- the same 401 as reusing an already-revoked token.
--
-- Token ids are random 63-bit values, so no two live tokens share one. But a database that
-- ran before this script may already hold two revocations of one token - the very race the
-- index closes - and the index could not be built over them. So, only while the index does
-- not exist yet, the extra revocations are removed first, keeping the earliest of each token.
-- audit_log is append-only (trg_audit_log_no_update refuses UPDATE and DELETE), so the trigger
-- is switched off for that one clean-up and on again; the DO block is a single statement, so
-- a failure part-way rolls back the switch too. Once the index exists the block does nothing.
-- Safe to run again, like every script after 001.

DO $$
BEGIN
    IF to_regclass('public.ux_refresh_token_single_revoke') IS NULL THEN
        ALTER TABLE audit_log DISABLE TRIGGER trg_audit_log_no_update;
        DELETE FROM audit_log later
        USING audit_log earlier
        WHERE later.entity_type = 'RefreshToken' AND later.action = 'REVOKE'
          AND earlier.entity_type = 'RefreshToken' AND earlier.action = 'REVOKE'
          AND later.entity_id = earlier.entity_id
          AND later.id > earlier.id;
        ALTER TABLE audit_log ENABLE TRIGGER trg_audit_log_no_update;
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_refresh_token_single_revoke
    ON audit_log (entity_id)
    WHERE entity_type = 'RefreshToken' AND action = 'REVOKE';
