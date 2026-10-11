-- BrewForge - migration 006: the pictures of a lesson.
--
-- Not part of the developer pack. The pack gives a lesson one media_url,
-- "image upload or embedded video link", and nowhere to keep an upload. A
-- trainer enriches a lesson with pictures (FE-07): how the cup is held, what
-- the foam should look like. A lesson may have several, in an order.
-- lesson.media_url stays what it is, the link of an embedded video.
--
-- Safe to run again, like every script after 001.

-- LessonMedia  (A picture a trainer uploaded to a lesson.)
CREATE TABLE IF NOT EXISTS lesson_media (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    lesson_id              BIGINT NOT NULL,
    sort_order             INTEGER NOT NULL,
    file_name              VARCHAR(255) NOT NULL,
    content_type           VARCHAR(64) NOT NULL,
    size_bytes             BIGINT NOT NULL,
    sha256                 CHAR(64) NOT NULL,
    storage_key            VARCHAR(255) NOT NULL,
    uploaded_by            BIGINT NOT NULL,
    uploaded_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (storage_key),
    CHECK (size_bytes > 0),
    CHECK (sort_order >= 1),
    FOREIGN KEY (lesson_id) REFERENCES lesson(id) ON DELETE CASCADE,
    FOREIGN KEY (uploaded_by) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_lessonmedia_lesson ON lesson_media(lesson_id);
