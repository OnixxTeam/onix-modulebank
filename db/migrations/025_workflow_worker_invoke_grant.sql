-- Ticket 08: the worker loop calls the shared action executor's foreign-
-- transaction path (PostgresActionExecutor.ExecuteInTransactionAsync), which
-- runs api.invoke on whatever connection the caller opened. Every earlier
-- redefinition of api.invoke (003/007/008/015/018) only ever granted execute
-- to course_runtime -- workflow_worker's own connection has never been able
-- to call it. Function identity and schema are unchanged, so this is a grant
-- only, not a redefinition.
grant usage on schema api to workflow_worker;
grant execute on function api.invoke(text, text, integer, jsonb, jsonb) to workflow_worker;

-- The pipeline's own first step (PostgresActionExecutor.ReadManifestAsync)
-- reads the manifest before it ever calls invoke -- migration 006 granted
-- this only to course_runtime, the same oversight as invoke itself.
grant execute on function api.get_action_manifest(text, text, integer) to workflow_worker;
