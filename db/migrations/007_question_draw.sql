-- BrewForge - migration 007: drawing the questions of an attempt.
--
-- Not part of the developer pack. In the pack a quiz is its questions, all of
-- them, the same on every attempt: a trainee who fails sees the very same
-- questions on the retake. With this column a quiz is a bank, and an attempt
-- is a draw from it: so many questions from each module, another draw on
-- every attempt.
--
-- Safe to run again, like every script after 001.

-- How many questions of each module one attempt is given. NULL: every question, as before.
ALTER TABLE quiz ADD COLUMN IF NOT EXISTS draw_per_module INTEGER;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'quiz_draw_per_module_range') THEN
        ALTER TABLE quiz ADD CONSTRAINT quiz_draw_per_module_range
            CHECK (draw_per_module IS NULL OR draw_per_module BETWEEN 1 AND 50);
    END IF;
END $$;
