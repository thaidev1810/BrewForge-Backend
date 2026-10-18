-- BrewForge - migration 013: notifications read in the application.
--
-- Not part of the developer pack. Migration 003 delivers a notification by
-- e-mail and Web Push; a user also reads them in the application, and marks
-- them read there.
--
-- Safe to run again, like every script after 001.

ALTER TABLE notification ADD COLUMN IF NOT EXISTS read_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS ix_notification_unread ON notification(user_id) WHERE read_at IS NULL;
