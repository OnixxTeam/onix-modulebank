-- payment.request version 1 target. Creates the operation and its single
-- OPERATION_CREATED event in one statement each, inside the one transaction
-- api.invoke already opened, so the two can never diverge. SECURITY DEFINER
-- with search_path pinned to '' matches every other action target and
-- api.invoke itself: no unqualified name can be shadowed by a caller.
create function payment.request_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation core.operations%rowtype;
begin
  insert into core.operations (request_id, operation_kind, amount, currency, status)
  values (
    p_context ->> 'requestId',
    p_payload ->> 'operationKind',
    (p_payload ->> 'amount')::numeric,
    p_payload ->> 'currency',
    'CREATED'
  )
  returning * into v_operation;

  insert into core.operation_events (operation_id, event_type, payload_hash)
  values (
    v_operation.operation_id,
    'OPERATION_CREATED',
    pg_catalog.encode(pg_catalog.sha256(p_payload::text::pg_catalog.bytea), 'hex')
  );

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'CREATED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

revoke all on function payment.request_v1(jsonb, jsonb) from public;
