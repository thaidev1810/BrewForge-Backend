-- BrewForge - migration 003: notifications that are delivered.
--
-- Not part of the developer pack. Until now a notification was only an entry
-- of the audit log; the team decided on 09/10/2026 to deliver them by e-mail
-- and by Web Push. A notification is still recorded in the audit log (NOTIFY);
-- the row here is what the dispatcher works from.
--
-- Safe to run again, like every script after 001.

-- Notification  (One message for one user, with what became of it on each channel.)
CREATE TABLE IF NOT EXISTS notification (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    user_id                BIGINT NOT NULL,
    subject                VARCHAR(160) NOT NULL,
    message                VARCHAR(1000) NOT NULL,
    email_status           VARCHAR(12) NOT NULL DEFAULT 'PENDING',
    push_status            VARCHAR(12) NOT NULL DEFAULT 'PENDING',
    attempts               INTEGER NOT NULL DEFAULT 0,
    last_error             VARCHAR(500),
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at           TIMESTAMPTZ,
    PRIMARY KEY (id),
    CHECK (email_status IN ('PENDING','SENT','FAILED','SKIPPED')),
    CHECK (push_status IN ('PENDING','SENT','FAILED','SKIPPED')),
    CHECK (attempts >= 0),
    FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_notification_user ON notification(user_id);
-- What the dispatcher asks for on every run.
CREATE INDEX IF NOT EXISTS ix_notification_pending ON notification(id) WHERE processed_at IS NULL;

-- PushSubscription  (A browser of a user that agreed to receive Web Push messages.)
CREATE TABLE IF NOT EXISTS push_subscription (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    user_id                BIGINT NOT NULL,
    endpoint               VARCHAR(1000) NOT NULL,
    p256dh                 VARCHAR(128) NOT NULL,
    auth                   VARCHAR(64) NOT NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (endpoint),
    FOREIGN KEY (user_id) REFERENCES app_user(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_pushsub_user ON push_subscription(user_id);
