-- BrewForge - migration 002: the video of a practical evaluation.
--
-- Not part of the developer pack. The pack's schema (001) has no place for
-- the recording a practical evaluation is judged from, and the team decided
-- on 09/10/2026 that every practical evaluation must have one.
--
-- Every script after 001 must be safe to run again: the API applies them on
-- each start, and Docker applies them once to an empty volume.

-- PracticalVideo  (The recording of one practical, uploaded by the trainer who ran it.)
CREATE TABLE IF NOT EXISTS practical_video (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    uploaded_by            BIGINT NOT NULL,
    file_name              VARCHAR(255) NOT NULL,
    content_type           VARCHAR(64) NOT NULL,
    size_bytes             BIGINT NOT NULL,
    sha256                 CHAR(64) NOT NULL,
    storage_key            VARCHAR(255) NOT NULL,
    uploaded_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (storage_key),
    CHECK (size_bytes > 0),
    FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE CASCADE,
    FOREIGN KEY (uploaded_by) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_pracvideo_enrol ON practical_video(enrollment_id);

-- The video an evaluation was judged from. NULL only on rows written before this migration.
ALTER TABLE practical_evaluation
    ADD COLUMN IF NOT EXISTS practical_video_id BIGINT REFERENCES practical_video(id) ON DELETE RESTRICT;

-- One recording is the evidence of one evaluation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_praceval_video ON practical_evaluation(practical_video_id);

-- New evaluations must name their video; the rows that were already there are left as they are.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'practical_evaluation_video_required') THEN
        ALTER TABLE practical_evaluation
            ADD CONSTRAINT practical_evaluation_video_required CHECK (practical_video_id IS NOT NULL) NOT VALID;
    END IF;
END $$;
