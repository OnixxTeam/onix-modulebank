-- OpenApiEndpoints needs request_schema/response_schema/outcomes to assemble
-- documents on every request (spec §7) -- but course_runtime has no direct
-- grant on api.action_catalog, only on the functions below. Same boundary as
-- api.get_action_manifest (migration 006): course_runtime reads the catalog
-- only through SECURITY DEFINER functions, never via a table grant.

create function api.openapi_default_actions()
returns jsonb
language sql
security definer
set search_path = ''
stable
as $$
  select coalesce(
    jsonb_agg(
      jsonb_build_object(
        'module',         module,
        'action',         action,
        'version',        version,
        'requestSchema',  request_schema,
        'responseSchema', response_schema,
        'outcomes',       outcomes
      )
      order by module, action
    ),
    '[]'::jsonb
  )
  from api.action_catalog
  where enabled and is_default;
$$;

revoke all on function api.openapi_default_actions() from public;
grant execute on function api.openapi_default_actions() to course_runtime;

-- Version documents are addressed by exact (module, action, version) and are
-- not restricted to enabled/is_default rows: a disabled version still
-- published a contract and callers may still want to read its document.
create function api.openapi_action_version(p_module text, p_action text, p_version integer)
returns jsonb
language sql
security definer
set search_path = ''
stable
as $$
  select jsonb_build_object(
    'module',         module,
    'action',         action,
    'version',        version,
    'requestSchema',  request_schema,
    'responseSchema', response_schema,
    'outcomes',       outcomes
  )
  from api.action_catalog
  where module = p_module
    and action = p_action
    and version = p_version;
$$;

revoke all on function api.openapi_action_version(text, text, integer) from public;
grant execute on function api.openapi_action_version(text, text, integer) to course_runtime;
