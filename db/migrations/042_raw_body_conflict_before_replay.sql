-- Ticket 17: a signed body's exact bytes now take part in the generic
-- idempotency identity, so an exact-byte conflict is decided before the
-- cached envelope is ever returned.
--
-- Before this migration api.invoke's replay branch compared only v_hash --
-- sha256 of the *normalised* jsonb text, which is blind to key order and
-- whitespace by construction. receipt.accept (migration 038) does compare the
-- exact raw-body hash, but only if it runs at all, and under a repeated
-- Idempotency-Key the cached envelope was returned first. A correctly
-- re-signed, differently-serialized body therefore came back as HTTP 200
-- duplicate replay instead of a conflict, and the Inbox never saw it.
--
-- The fix is one column, not a special case: the trusted rawBodyHash the
-- signature boundary stamps (ticket 07, src/Course.Api/Endpoints/ActionEndpoint.cs)
-- is stored with the claim and compared on replay. Unsigned actions carry no
-- such hash, store null, and keep the exact behaviour they had -- migration
-- 018's reordered-payload replay stays a replay.
alter table api.idempotency_keys add column raw_body_hash text;

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
  v_raw_hash     text;
  v_stored_raw_hash text;
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
  -- Present only when the signature boundary verified one (ticket 07); null
  -- for every unsigned action, which is what keeps their identity unchanged.
  v_raw_hash := p_context->>'rawBodyHash';
  v_idempotent := v_catalog.idempotency_scope <> 'none' and v_request_id is not null;

  if v_idempotent then
    v_scope_key := case v_catalog.idempotency_scope
      when 'principal_action' then p_context->>'principal'
      when 'consumer_action'  then p_context->>'consumer'
      when 'global_action'    then '*'
    end;
  end if;

  -- Everything that touches the domain (the idempotency claim and the target
  -- call itself) happens inside this block, so a technical failure can undo
  -- exactly that -- and nothing else -- via the exception handler below.
  begin
    if v_idempotent then
      insert into api.idempotency_keys (scope_key, module, action, request_id, payload_hash, raw_body_hash, envelope)
      values (v_scope_key, p_module, p_action, v_request_id, v_hash, v_raw_hash, '{}'::jsonb)
      on conflict (scope_key, module, action, request_id) do nothing
      returning true into v_claimed;

      if v_claimed is not true then
        select payload_hash, raw_body_hash, envelope into v_stored_hash, v_stored_raw_hash, v_stored_envelope
          from api.idempotency_keys
         where scope_key = v_scope_key and module = p_module and action = p_action and request_id = v_request_id
         for share;

        -- Exact signed bytes decide first. v_hash cannot see the difference
        -- between two serializations of the same document, so without this the
        -- replay below would hand back the first call's envelope and the
        -- changed signed body would never reach its target's own raw-body
        -- check. `is distinct from` also covers the asymmetric cases: a signed
        -- repeat of an unsigned claim (and the reverse) is a different request,
        -- not a replay of the one that was stored.
        if v_stored_raw_hash is distinct from v_raw_hash then
          return jsonb_build_object('status', 'error', 'code', 'idempotency.conflict');
        end if;

        -- Carries the original call's correlationId (see the update at the end
        -- of this function), which is what makes this the same envelope rather
        -- than a second one that merely agrees about the result.
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

    if v_result->>'status' <> 'ok' then
      -- Deliberately unwind this block's subtransaction: undoes the domain
      -- effect and the idempotency claim above, but v_result (a plpgsql
      -- variable, not a database write) survives the rollback, so the
      -- dispatch insert below can still see it.
      raise exception using errcode = 'CRS01', message = 'course.invoke: target reported a non-ok status';
    end if;
  exception
    when sqlstate 'CRS01' then
      null;
  end;

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

  if v_idempotent and v_result->>'status' = 'ok' then
    update api.idempotency_keys
       set envelope = v_result || jsonb_build_object('correlationId', p_context->>'correlationId')
     where scope_key = v_scope_key and module = p_module and action = p_action and request_id = v_request_id;
  end if;

  return v_result;
end;
$$;

revoke all on function api.invoke(text, text, integer, jsonb, jsonb) from public;
grant execute on function api.invoke(text, text, integer, jsonb, jsonb) to course_runtime;
