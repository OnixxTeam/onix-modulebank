-- publish_action inserted every new version with enabled and is_default
-- forced to false, so a freshly published action had no default version and
-- every call to it resolved to action.not_found before policy, idempotency
-- or rollback handling could run (contracts/course-1/action-manifest.schema.json:22-23
-- declares both as required manifest fields; a field that publish always
-- ignores would have no reason to be required).
--
-- Replaces migration 004's api.publish_action with the same signature and
-- grants. The only behavioural change is in the fresh-insert branch: both
-- flags are now read from the manifest, and if the manifest asks for
-- is_default = true the previous default for the same route is cleared
-- first -- same order api.activate_action (migration 005) uses -- so the
-- action_catalog_one_default partial index never sees two default rows at
-- once. The republish-with-matching-checksum branch is untouched: it returns
-- before any write, so activate/disable stay authoritative for versions
-- that are already published and a republish can never resurrect a version
-- an operator disabled on purpose.
create or replace function api.publish_action(p_manifest jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_module     text    := p_manifest ->> 'module';
  v_action     text    := p_manifest ->> 'action';
  v_version    integer := (p_manifest ->> 'version')::integer;
  v_checksum   text    := pg_catalog.encode(pg_catalog.sha256(p_manifest::text::bytea), 'hex');
  v_existing   api.action_catalog%rowtype;
  v_enabled    boolean := coalesce((p_manifest ->> 'enabled')::boolean, false);
  v_is_default boolean := coalesce((p_manifest ->> 'is_default')::boolean, false);
begin
  select * into v_existing
    from api.action_catalog
   where module = v_module and action = v_action and version = v_version;

  if not found then
    if v_is_default then
      update api.action_catalog
         set is_default = false
       where module = v_module and action = v_action and is_default;
    end if;

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
      (p_manifest ->> 'timeout_ms')::integer, v_enabled, v_is_default, v_checksum
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
