-- payment.approve version 1 target (ticket 03). Reached either straight from
-- check_limit's WITHIN_LIMIT branch or, from ticket 05 on, from a manual
-- APPROVED decision -- either way the decision itself was already recorded
-- by whichever action routed here (check_limit's LIMIT_RULE row, or
-- workflow.manual's MANUAL row), so this function only carries the operation
-- to its terminal status. Recording a second decision here would double the
-- audit trail for the manual path.
create function payment.approve_v1(p_context jsonb, p_payload jsonb)
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

  update core.operations
     set status = 'COMPLETED'
   where operation_id = v_operation.operation_id
  returning * into v_operation;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'APPROVED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

revoke all on function payment.approve_v1(jsonb, jsonb) from public;
