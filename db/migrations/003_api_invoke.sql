-- The single point of domain execution. SECURITY DEFINER so it can reach
-- api.action_catalog and the target function while course_runtime keeps no
-- direct grant on either; search_path is pinned to '' and every referenced
-- name below is schema-qualified, otherwise a caller could shadow an
-- unqualified name (e.g. its own "format") by manipulating its session
-- search_path before calling in.
create function api.invoke(
  p_module  text,
  p_action  text,
  p_version integer,
  p_context jsonb,
  p_payload jsonb
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_catalog      api.action_catalog%rowtype;
  v_required     text[];
  v_granted      text[];
  v_target_signature_ok boolean;
  v_result       jsonb;
begin
  if p_version is not null then
    select * into v_catalog
      from api.action_catalog
     where module = p_module and action = p_action and version = p_version and enabled;
  else
    select * into v_catalog
      from api.action_catalog
     where module = p_module and action = p_action and is_default and enabled;
  end if;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'action.not_found');
  end if;

  select coalesce(pg_catalog.array_agg(scope), array[]::text[])
    into v_required
    from pg_catalog.jsonb_array_elements_text(v_catalog.required_policy) as scope;

  select coalesce(pg_catalog.array_agg(scope), array[]::text[])
    into v_granted
    from pg_catalog.jsonb_array_elements_text(coalesce(p_context->'scopes', '[]'::jsonb)) as scope;

  if not (v_required <@ v_granted) then
    return jsonb_build_object('status', 'error', 'code', 'access.denied');
  end if;

  -- Exact (jsonb, jsonb) -> jsonb signature, checked before target is called
  -- so a manifest pointing at a missing or mismatched function fails closed.
  select pg_catalog.pg_get_function_identity_arguments(p.oid) = 'p_context jsonb, p_payload jsonb'
    into v_target_signature_ok
    from pg_catalog.pg_proc p
    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
   where n.nspname = v_catalog.target_schema
     and p.proname = v_catalog.target_function
     and p.prorettype = 'pg_catalog.jsonb'::pg_catalog.regtype;

  if v_target_signature_ok is not true then
    return jsonb_build_object('status', 'error', 'code', 'action.not_found');
  end if;

  -- Idempotency is a stub on this ticket; ticket 08 adds api.idempotency_keys
  -- and the claim-then-call sequence in this same transaction.

  -- Identifiers come from the catalog row, never from p_module/p_action/p_payload,
  -- so a caller cannot steer execution at a function of its choosing.
  execute pg_catalog.format('select %I.%I($1, $2)', v_catalog.target_schema, v_catalog.target_function)
    into v_result
    using p_context, p_payload;

  return v_result;
end;
$$;

revoke all on function api.invoke(text, text, integer, jsonb, jsonb) from public;
grant usage on schema api to course_runtime;
grant execute on function api.invoke(text, text, integer, jsonb, jsonb) to course_runtime;
