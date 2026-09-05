-- payment.reject version 1 target (ticket 03). Shared by both week-3 flows,
-- same shape as payment.approve_v1: the deciding action already recorded
-- (or, for a manual rejection from ticket 05, will record) the decision row,
-- so this function only carries the operation to its terminal status.
create function payment.reject_v1(p_context jsonb, p_payload jsonb)
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
     set status = 'REJECTED'
   where operation_id = v_operation.operation_id
  returning * into v_operation;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'REJECTED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

revoke all on function payment.reject_v1(jsonb, jsonb) from public;
