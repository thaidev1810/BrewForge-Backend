-- BrewForge - migration 018: a security version on each user for session revocation.
--
-- An access token is a short-lived snapshot of a user's role and branch. Deactivating a user,
-- changing their role or branch, or resetting their password must end their live sessions at once,
-- not wait out the token's lifetime. Each such change bumps this counter; every access token carries
-- the value it was minted with, and the auth layer refuses a token whose value no longer matches.
-- Existing users start at 0, the same as the value a freshly minted token carries (G-11).
--
-- Safe to run again, like every script after 001.

ALTER TABLE app_user ADD COLUMN IF NOT EXISTS security_version INT NOT NULL DEFAULT 0;
