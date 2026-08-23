-- Append-only log of every actual target invocation. Only the payload hash
-- is kept, never the full payload; technical target failures (a controlled
-- status=error envelope, not a PL/pgSQL exception) are recorded with
-- status = 'ERROR'.
create table api.action_dispatches (
  correlation_id uuid not null,
  request_id     text,
  module         text not null,
  action         text not null,
  version        integer,
  principal      text,
  payload_hash   text not null,
  status         text not null check (status in ('OK', 'ERROR')),
  outcome        text,
  occurred_at    timestamptz not null default pg_catalog.now()
);

-- Replaces migration 007's api.invoke with the same signature and grants,
-- adding a dispatch row for every actual call to target (the idempotency
-- cache-hit and conflict branches return before target is ever reached, so
-- they write no row -- there was no domain call to log).
--
-- The insert lives in the same transaction as the domain effect and the
-- idempotency envelope write. When the C# runtime layer rejects an unknown
-- outcome or invalid result it never commits, so this row rolls back along
-- with the effect -- consistent with "dispatch has no successful domain call"
-- for a request that never really completed.
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

  insert into api.action_dispatches (
    correlation_id, request_id, module, action, version, principal, payload_hash, status, outcome
  )
  values (
    (p_context->>'correlationId')::uuid,
    v_request_id,
    p_module,
    p_action,
    v_catalog.version,
    p_context->>'principal',
    v_hash,
    case when v_result->>'status' = 'ok' then 'OK' else 'ERROR' end,
    v_result->>'outcome'
  );

  if v_idempotent then
    update api.idempotency_keys set envelope = v_result
     where scope_key = v_scope_key and module = p_module and action = p_action and request_id = v_request_id;
  end if;

  return v_result;
end;
$$;

revoke all on function api.invoke(text, text, integer, jsonb, jsonb) from public;
grant execute on function api.invoke(text, text, integer, jsonb, jsonb) to course_runtime;
