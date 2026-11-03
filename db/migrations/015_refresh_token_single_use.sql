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
-- Token ids are random 63-bit values, so no two live tokens share one and no existing
-- row collides; audit_log is append-only, so a revocation, once written, stays.
-- Safe to run again, like every script after 001.

CREATE UNIQUE INDEX IF NOT EXISTS ux_refresh_token_single_revoke
    ON audit_log (entity_id)
    WHERE entity_type = 'RefreshToken' AND action = 'REVOKE';
