-- payment.validate version 1 target (ticket 03). Shared by both week-3 flows
-- (payment-processing and payment-review): a checkpoint confirming the
-- operation an automatic step was handed still exists before the flow-
-- specific branch runs. Same not-found shape as operation.get_v1 (migration
-- 011) -- a controlled status=error envelope, never a raised exception.
create function payment.validate_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation core.operations%rowtype;
begin
  select * into v_operation
    from core.operations
   where operation_id = (p_payload ->> 'operationId')::uuid;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'operation.not_found',
      'message', 'operation not found'
    );
  end if;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'VALID',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

revoke all on function payment.validate_v1(jsonb, jsonb) from public;
