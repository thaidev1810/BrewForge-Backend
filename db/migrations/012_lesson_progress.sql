-- BrewForge - migration 012: progress lesson by lesson.
--
-- Not part of the developer pack, which keeps progress per module
-- (module_progress). The SRS asks for progress "recorded per lesson and
-- aggregated" (SCR-18): a learner marks each lesson read, and a module is
-- complete when the last of its lessons is.
--
-- Safe to run again, like every script after 001.

-- LessonProgress  (A lesson a learner has finished, within an enrolment.)
CREATE TABLE IF NOT EXISTS lesson_progress (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    enrollment_id          BIGINT NOT NULL,
    course_module_id       BIGINT NOT NULL,
    lesson_id              BIGINT NOT NULL,
    completed_at           TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (id),
    UNIQUE (enrollment_id, lesson_id),
    FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE CASCADE,
    FOREIGN KEY (course_module_id) REFERENCES course_module(id) ON DELETE CASCADE,
    FOREIGN KEY (lesson_id) REFERENCES lesson(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_lesson_progress_enrollment ON lesson_progress(enrollment_id);
