-- BrewForge - migration 020: retraining obligations (G-19).
--
-- A corrective action that requires retraining used to be closable on "any pass of the employee on a
-- course of the SOP", which a pass from before the failed audit could satisfy. Each named employee now has
-- an obligation of their own: the course and SOP version they are given, and the enrolment cycle linked to
-- it. The action closes only when every obligation is met by a pass of its own cycle earned after the link,
-- or was exempted by an auditor with a reason.
--
-- Someone who already PASSED the course must be able to start a new cycle on it, so a passed enrolment no
-- longer counts as the one active cycle per course and person: ux_enrollment_active now leaves out PASSED
-- (and CLOSED). The passed cycle, with its quiz, practical, videos and certificate, stays as history.
--
-- Open actions raised before this have no record of what was retrained: their employees get a PENDING
-- obligation marked legacy, to be reconciled (assigned or exempted) before the action can close. Nothing
-- is marked done from an old pass, and closed actions are left as they are.
--
-- Safe to run again, like every script after 001.

CREATE TABLE IF NOT EXISTS retraining_obligation (
    id                     BIGINT GENERATED ALWAYS AS IDENTITY NOT NULL,
    corrective_action_id   BIGINT NOT NULL,
    employee_id            BIGINT NOT NULL,
    status                 VARCHAR(12) NOT NULL DEFAULT 'PENDING',
    course_id              BIGINT,
    recipe_version_id      BIGINT,
    enrollment_id          BIGINT,
    assigned_at            TIMESTAMPTZ,
    exempt_reason          VARCHAR(500),
    exempted_by            BIGINT,
    exempted_at            TIMESTAMPTZ,
    legacy                 BOOLEAN NOT NULL DEFAULT FALSE,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (id),
    UNIQUE (corrective_action_id, employee_id),
    CHECK (status IN ('PENDING', 'ASSIGNED', 'EXEMPTED')),
    CHECK (status <> 'ASSIGNED' OR (course_id IS NOT NULL AND enrollment_id IS NOT NULL AND assigned_at IS NOT NULL)),
    CHECK (status <> 'EXEMPTED' OR (exempt_reason IS NOT NULL AND exempted_by IS NOT NULL AND exempted_at IS NOT NULL)),
    FOREIGN KEY (corrective_action_id) REFERENCES corrective_action(id) ON DELETE RESTRICT,
    FOREIGN KEY (employee_id) REFERENCES app_user(id) ON DELETE RESTRICT,
    FOREIGN KEY (course_id) REFERENCES course(id) ON DELETE RESTRICT,
    FOREIGN KEY (recipe_version_id) REFERENCES recipe_version(id) ON DELETE RESTRICT,
    FOREIGN KEY (enrollment_id) REFERENCES enrollment(id) ON DELETE RESTRICT,
    FOREIGN KEY (exempted_by) REFERENCES app_user(id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_retraining_obligation_enrollment ON retraining_obligation(enrollment_id);

-- One active cycle per course and person; a passed cycle is history, not an active one. Rebuilt only when
-- the index still has its old definition, so a start-up does not rebuild it every time.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_indexes
                   WHERE indexname = 'ux_enrollment_active' AND indexdef LIKE '%PASSED%') THEN
        DROP INDEX IF EXISTS ux_enrollment_active;
        CREATE UNIQUE INDEX ux_enrollment_active ON enrollment(course_id, user_id)
            WHERE state NOT IN ('CLOSED', 'PASSED');
    END IF;
END $$;

-- Legacy: open retraining actions get a PENDING obligation per named employee, awaiting reconciliation.
INSERT INTO retraining_obligation (corrective_action_id, employee_id, status, legacy, created_at)
SELECT ca.id, named.employee_id, 'PENDING', TRUE, ca.created_at
FROM corrective_action ca
CROSS JOIN LATERAL unnest(ca.employee_ids) AS named(employee_id)
WHERE ca.retraining_required AND ca.status <> 'CLOSED'
  AND EXISTS (SELECT 1 FROM app_user u WHERE u.id = named.employee_id)
ON CONFLICT (corrective_action_id, employee_id) DO NOTHING;
