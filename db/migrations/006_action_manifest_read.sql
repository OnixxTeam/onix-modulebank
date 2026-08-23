-- The HTTP executor needs the manifest's contract fields (request/response schema,
-- required_policy, idempotency_mode, timeout_ms) before it can validate a payload or
-- decide idempotency/policy in C# -- but course_runtime has no direct grant on
-- api.action_catalog, only on api.invoke (migration 003). Same SECURITY DEFINER
-- boundary as api.invoke: course_runtime reads the catalog only through this
-- function, never via a table grant.
--
-- Deliberately omits target_schema/target_function: api.invoke resolves and calls
-- the target internally, so the C# executor never needs to know it, matching the
-- boundary that keeps identifiers out of anything payload- or request-adjacent.
create function api.get_action_manifest(p_module text, p_action text, p_version integer)
returns jsonb
language sql
security definer
set search_path = ''
stable
as $$
  select jsonb_build_object(
    'version',          version,
    'idempotencyMode',  idempotency_mode,
    'idempotencyScope', idempotency_scope,
    'requiredPolicy',   required_policy,
    'requestSchema',    request_schema,
    'responseSchema',   response_schema,
    'outcomes',         outcomes,
    'timeoutMs',        timeout_ms
  )
  from api.action_catalog
  where module = p_module
    and action = p_action
    and enabled
    and (
      (p_version is not null and version = p_version)
      or (p_version is null and is_default)
    )
  limit 1;
$$;

revoke all on function api.get_action_manifest(text, text, integer) from public;
grant execute on function api.get_action_manifest(text, text, integer) to course_runtime;
