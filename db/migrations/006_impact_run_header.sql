-- BrewForge - migration 006: the impact run header.
--
-- An impact run used to exist only through its change_impact rows, so a what-if
-- that affected nothing left no trace and could not be read back or committed
-- (it was indistinguishable from a run that never existed). The header is stored
-- apart from the affected rows: a run that affects nothing has a header and no
-- rows, and can still be read (empty affected set) and committed.
--
-- Safe to run again, like every script after 001.

CREATE TABLE IF NOT EXISTS impact_run_header (
    run_id                 UUID NOT NULL,
    trigger_entity         VARCHAR(48) NOT NULL,
    trigger_id             BIGINT NOT NULL,
    committed              BOOLEAN NOT NULL DEFAULT FALSE,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (run_id)
);
