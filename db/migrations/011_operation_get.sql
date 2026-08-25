-- operation.get version 1 target. Read-only, no idempotency. An unknown
-- operationId returns a controlled status=error envelope with a business
-- code api.invoke's runtime layer does not recognise (contract-reference:
-- outside the fixed HTTP matrix that falls back to internal.error) rather
-- than raising an exception, so it always surfaces as a contract-shaped
-- response instead of a stack trace.
create function operation.get_v1(p_context jsonb, p_payload jsonb)
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
    'outcome', 'FOUND',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

revoke all on function operation.get_v1(jsonb, jsonb) from public;
