-- Publishes a manifest into the immutable catalog. SECURITY DEFINER so
-- course_publisher never needs a direct grant on api.action_catalog itself --
-- only on this function -- matching the same boundary api.invoke uses for
-- course_runtime.
create function api.publish_action(p_manifest jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_module   text    := p_manifest ->> 'module';
  v_action   text    := p_manifest ->> 'action';
  v_version  integer := (p_manifest ->> 'version')::integer;
  v_checksum text    := pg_catalog.encode(pg_catalog.sha256(p_manifest::text::bytea), 'hex');
  v_existing api.action_catalog%rowtype;
begin
  select * into v_existing
    from api.action_catalog
   where module = v_module and action = v_action and version = v_version;

  if not found then
    insert into api.action_catalog (
      module, action, version, contract_version, http_method,
      target_schema, target_function, request_schema, response_schema,
      outcomes, required_policy, idempotency_mode, idempotency_scope,
      timeout_ms, enabled, is_default, manifest_checksum
    ) values (
      v_module, v_action, v_version, p_manifest ->> 'contract_version', p_manifest ->> 'http_method',
      p_manifest ->> 'target_schema', p_manifest ->> 'target_function',
      p_manifest -> 'request_schema', p_manifest -> 'response_schema',
      p_manifest -> 'outcomes', p_manifest -> 'required_policy',
      p_manifest ->> 'idempotency_mode', p_manifest ->> 'idempotency_scope',
      (p_manifest ->> 'timeout_ms')::integer, false, false, v_checksum
    );

    return jsonb_build_object('status', 'ok', 'key', v_module || '.' || v_action, 'version', v_version);
  end if;

  if v_existing.manifest_checksum = v_checksum then
    return jsonb_build_object('status', 'ok', 'key', v_module || '.' || v_action, 'version', v_version);
  end if;

  return jsonb_build_object(
    'status', 'error',
    'code', 'manifest.conflict',
    'message', 'published action version is immutable'
  );
end;
$$;

revoke all on function api.publish_action(jsonb) from public;
grant usage on schema api to course_publisher;
grant execute on function api.publish_action(jsonb) to course_publisher;

-- Read-side counterpart to publish_action: same SECURITY DEFINER boundary so
-- course_publisher reads the catalog only through a function, never via a
-- direct table grant. camelCase keys because this result lands straight in
-- the CLI's JSON envelope with no further C# mapping.
create function api.list_actions()
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
