-- The course's own idempotent domain action (ticket 05): a target that
-- exercises the whole action-runtime contract on demand, so the six behaviour
-- modes workflow-smoke maps and hidden checks lean on (plain success, a
-- manual-branch outcome, retryable/non-retryable failure, an undeclared
-- outcome and a response-schema violation) can be driven without waiting on
-- a hidden checker action.
--
-- The domain effect key is p_context->>'requestId', not executionId. Every
-- caller -- a network Idempotency-Key header today, the workflow worker once
-- ticket 08 lands (its idempotency key equals the job's executionId, by that
-- ticket's own contract) -- always sets requestId, so this is the one field
-- both paths share; reading executionId instead would leave every direct
-- network reproduction of a mode (this ticket's own acceptance test) with no
-- key at all. The manifest's idempotency_mode=required guarantees it is
-- never null here.
create schema workflow_probe;

create table workflow_probe.effect (
  effect_key     text        primary key,
  business_value text        not null,
  created_at     timestamptz not null default pg_catalog.clock_timestamp()
);

-- Mirrors payment.request_v1's shape: SECURITY DEFINER, search_path pinned to
-- '' so no unqualified name can be shadowed, one insert for the domain
-- effect. Idempotent by construction -- ON CONFLICT DO NOTHING against the
-- primary key means a retried call with the same effect key never produces a
-- second row, no matter which mode is replayed.
create function workflow_probe.probe_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_effect_key text := p_context ->> 'requestId';
  v_mode       text := p_payload ->> 'mode';
  v_value      text := p_payload ->> 'value';
  v_outcome    text;
begin
  insert into workflow_probe.effect (effect_key, business_value)
  values (v_effect_key, v_value)
  on conflict (effect_key) do nothing;

  if v_mode = 'RETRYABLE_ERROR' then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow_probe.retryable',
      'message', 'forced retryable failure',
      'retryable', true
    );
  end if;

  if v_mode = 'PERMANENT_ERROR' then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow_probe.permanent',
      'message', 'forced non-retryable failure',
      'retryable', false
    );
  end if;

  v_outcome := case v_mode
    when 'MANUAL' then 'ROUTE_MANUAL'
    -- Deliberately not one of the manifest's declared outcomes -- proves the
    -- executor rejects an undeclared outcome instead of routing on it.
    when 'UNDECLARED_OUTCOME' then 'UNDECLARED'
    else 'SUCCEEDED'
  end;

  -- Deliberately violates response_schema (stored must be boolean) -- proves
  -- the executor rejects a contract-violating success instead of trusting it.
  if v_mode = 'INVALID_RESULT' then
    return jsonb_build_object(
      'status', 'ok',
      'outcome', v_outcome,
      'result', jsonb_build_object(
        'effectKey', v_effect_key,
        'value', v_value,
        'stored', 'not-a-boolean'
      )
    );
  end if;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', v_outcome,
    'result', jsonb_build_object(
      'effectKey', v_effect_key,
      'value', v_value,
      'stored', true
    )
  );
end;
$$;

revoke all on function workflow_probe.probe_v1(jsonb, jsonb) from public;
