-- Migration 016 let a manifest with is_default = true clear the route's
-- existing default and take the flag for the version being published. That
-- makes `action publish` a way to redirect live traffic: the checker's case
-- published a third version of an action and silently moved every call to a
-- narrower request schema and idempotency_mode "none", with no activate/disable
-- in sight and nothing in the catalog history to say why.
--
-- contract-reference:87 -- "`enabled` и `is_default` являются операционным
-- состоянием и меняются только CLI-командами. Для route с включёнными версиями
-- существует ровно одна default-версия." Publish is not one of those commands.
--
-- Replaces 016's api.publish_action with the same signature and grants. Only
-- the fresh-insert branch changes: claiming a default that another version
-- already holds is refused as manifest.conflict instead of granted. The reason
-- 016 exists is untouched -- a route with no default still gets one from the
-- first manifest that asks for it, so a freshly published action stays callable
-- without a follow-up activate.
--
-- The guard returns before the insert, so the refusal needs no rollback of its
-- own: nothing has been written when it fires.
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
    if v_is_default and exists(
      select 1 from api.action_catalog
       where module = v_module and action = v_action and is_default
    ) then
      return jsonb_build_object(
        'status', 'error',
        'code', 'manifest.conflict',
        'message', 'route already has a default version; use action activate'
      );
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

  -- Republishing an unchanged manifest is safe by contract, including one that
  -- declares the default it already holds: this returns before the guard above
  -- could ever see it, since the version exists.
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
