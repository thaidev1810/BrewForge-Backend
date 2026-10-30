-- BrewForge - migration 014: backfill the impact run header for legacy runs.
--
-- The impact_run_header table arrived in migration 006. Impact runs created before
-- then exist only through their change_impact rows and have no header, so reading or
-- committing them now (which looks the run up by its header) fails with a 404. This
-- rebuilds a header for every such run from the rows it already has: the trigger comes
-- from the rows, and the run counts as committed only if all of its rows are.
--
-- Safe to run again, like every script after 001: once every run has a header the
-- SELECT finds nothing, and ON CONFLICT guards against a concurrent insert besides.

INSERT INTO impact_run_header (run_id, trigger_entity, trigger_id, committed, created_at)
SELECT ci.analysis_run_id,
       (array_agg(ci.trigger_entity ORDER BY ci.id))[1],
       (array_agg(ci.trigger_id    ORDER BY ci.id))[1],
       bool_and(ci.committed),
       min(ci.created_at)
FROM change_impact ci
WHERE NOT EXISTS (SELECT 1 FROM impact_run_header h WHERE h.run_id = ci.analysis_run_id)
GROUP BY ci.analysis_run_id
ON CONFLICT (run_id) DO NOTHING;
