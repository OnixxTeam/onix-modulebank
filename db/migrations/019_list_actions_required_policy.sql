-- `flow validate` (week 2) checks a map task's required_policy against the
-- action it targets -- exact set equality, plus enabled/existence -- entirely
-- in C# (contract-reference: flow validate must not write). api.list_actions
-- already gives course_publisher every other catalog fact the validator needs;
-- required_policy was the one field missing. Additive field, same function
-- signature and grants as migration 004 -- action list's existing shape is
-- unaffected, callers that don't read the new key see no difference.
create or replace function api.list_actions()
returns jsonb
language sql
security definer
set search_path = ''
stable
as $$
  select coalesce(
    pg_catalog.jsonb_agg(
      jsonb_build_object(
        'module', module,
        'action', action,
        'version', version,
        'httpMethod', http_method,
        'targetSchema', target_schema,
        'targetFunction', target_function,
        'outcomes', outcomes,
        'requiredPolicy', required_policy,
        'enabled', enabled,
        'isDefault', is_default
      )
      order by module, action, version
    ),
    '[]'::jsonb
  )
  from api.action_catalog;
$$;

revoke all on function api.list_actions() from public;
grant execute on function api.list_actions() to course_publisher;
