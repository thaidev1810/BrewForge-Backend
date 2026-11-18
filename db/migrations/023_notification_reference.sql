-- BrewForge - migration 023: what a notification is about (BE-05).
--
-- The client had to guess the object of a notification from its subject. A notification now says what it is
-- about - reference_type (ENROLLMENT, CERTIFICATE, COURSE, PRACTICAL_SCHEDULE, TRAINING_CLASS, BRANCH_AUDIT,
-- RECIPE_FEEDBACK, PILOT, RECIPE) and reference_id - with every id it was raised with in context_json.
-- Notifications from before keep both empty: the client shows their message alone.
--
-- Safe to run again, like every script after 001.

ALTER TABLE notification ADD COLUMN IF NOT EXISTS reference_type VARCHAR(40);
ALTER TABLE notification ADD COLUMN IF NOT EXISTS reference_id BIGINT;
ALTER TABLE notification ADD COLUMN IF NOT EXISTS context_json JSONB;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_notification_reference') THEN
        ALTER TABLE notification ADD CONSTRAINT ck_notification_reference
            CHECK ((reference_type IS NULL) = (reference_id IS NULL));
    END IF;
END $$;
