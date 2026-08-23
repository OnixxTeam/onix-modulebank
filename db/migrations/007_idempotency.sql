-- Claim-then-call idempotency inside api.invoke. The primary key
-- (scope_key, module, action, request_id) is the only source of uniqueness:
-- a concurrent duplicate blocks on that row via INSERT ... ON CONFLICT until
-- the first transaction commits, then takes the "not claimed" branch and
-- sees the same stored envelope -- concurrency is guaranteed by PostgreSQL,
-- not a process-local lock.
create table api.idempotency_keys (
  scope_key    text not null,
  module       text not null,
  action       text not null,
  request_id   text not null,
  payload_hash text not null,
  envelope     jsonb not null,
  created_at   timestamptz not null default pg_catalog.now(),
  primary key (scope_key, module, action, request_id)
);

-- Replaces migration 003's api.invoke with the same signature and grants,
-- now claiming an idempotency key (when the manifest's scope isn't 'none'
-- and the caller sent a request id) before calling target, and writing the
-- envelope back into the claimed row afterwards.
--
-- That write happens in this same transaction. The C# runtime layer
-- validates outcome and result against the manifest after invoke returns
-- but before commit; a violation returns without committing, so the claim
-- row rolls back along with the domain effect and a retry proceeds as a
-- first attempt. A separate transaction for the key would make "a
-- successful idempotency result is not persisted on a rejected outcome"
-- impossible to satisfy.
create or replace function api.invoke(
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
  v_hash         text;
  v_request_id   text;
  v_scope_key    text;
  v_idempotent   boolean;
  v_claimed      boolean;
  v_stored_hash  text;
  v_stored_envelope jsonb;
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

  -- jsonb's text form is already normalised for key order and whitespace, so
  -- hashing it is a free canonicalisation; sha256 has been in pg_catalog
  -- since PG 11, no pgcrypto needed.
  v_hash := pg_catalog.encode(pg_catalog.sha256(p_payload::text::pg_catalog.bytea), 'hex');
  v_request_id := p_context->>'requestId';
  v_idempotent := v_catalog.idempotency_scope <> 'none' and v_request_id is not null;

  if v_idempotent then
    v_scope_key := case v_catalog.idempotency_scope
      when 'principal_action' then p_context->>'principal'
      when 'consumer_action'  then p_context->>'consumer'
      when 'global_action'    then '*'
    end;

    insert into api.idempotency_keys (scope_key, module, action, request_id, payload_hash, envelope)
    values (v_scope_key, p_module, p_action, v_request_id, v_hash, '{}'::jsonb)
    on conflict (scope_key, module, action, request_id) do nothing
    returning true into v_claimed;

    if v_claimed is not true then
      select payload_hash, envelope into v_stored_hash, v_stored_envelope
        from api.idempotency_keys
       where scope_key = v_scope_key and module = p_module and action = p_action and request_id = v_request_id
       for share;

      if v_stored_hash = v_hash then
        return v_stored_envelope;
      end if;

      return jsonb_build_object('status', 'error', 'code', 'idempotency.conflict');
    end if;
  end if;

  -- Identifiers come from the catalog row, never from p_module/p_action/p_payload,
  -- so a caller cannot steer execution at a function of its choosing.
  execute pg_catalog.format('select %I.%I($1, $2)', v_catalog.target_schema, v_catalog.target_function)
    into v_result
    using p_context, p_payload;

  if v_idempotent then
    update api.idempotency_keys set envelope = v_result
     where scope_key = v_scope_key and module = p_module and action = p_action and request_id = v_request_id;
  end if;

  return v_result;
end;
$$;

revoke all on function api.invoke(text, text, integer, jsonb, jsonb) from public;
grant execute on function api.invoke(text, text, integer, jsonb, jsonb) to course_runtime;
