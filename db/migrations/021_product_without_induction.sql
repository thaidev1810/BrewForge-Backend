-- BrewForge - migration 021: product training no longer waits for induction.
--
-- The seeded regulation of PRODUCT courses named INDUCTION as its prerequisite, so a newcomer could not be
-- given the SOP of a drink before passing induction. Both stay mandatory for trainees, but they now run side
-- by side: the PRODUCT regulation loses its prerequisite. The prerequisite mechanism itself is untouched, and
-- a training manager can set one again through PUT /training-regulations/{id}.
--
-- Every script after 001 runs on each start, so this change is made once only: a marker row in audit_log
-- records that it was applied, and a prerequisite set again later is left alone. Enrolments and
-- certificates are not touched.

DO $$
DECLARE
    changed RECORD;
BEGIN
    IF EXISTS (SELECT 1 FROM audit_log
               WHERE entity_type = 'TrainingRegulation' AND action = 'MIGRATION_021') THEN
        RETURN;
    END IF;

    FOR changed IN
        UPDATE training_regulation
        SET prerequisite_type = NULL
        WHERE course_type = 'PRODUCT' AND prerequisite_type = 'INDUCTION'
        RETURNING id
    LOOP
        INSERT INTO audit_log (entity_type, entity_id, action, payload_json)
        VALUES ('TrainingRegulation', changed.id, 'UPDATE',
                jsonb_build_object('prerequisiteType', NULL, 'previousPrerequisiteType', 'INDUCTION',
                                   'reason', 'MIGRATION_021'));
    END LOOP;

    INSERT INTO audit_log (entity_type, entity_id, action, payload_json)
    VALUES ('TrainingRegulation', 0, 'MIGRATION_021',
            jsonb_build_object('note', 'PRODUCT no longer requires INDUCTION'));
END $$;
