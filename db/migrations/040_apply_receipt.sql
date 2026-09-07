-- Ticket 10: payment.apply_receipt reads only the saved receipt (SPEC "Ни
-- клиентское тело, ни транспортный ответ провайдера не могут поставить статус
-- напрямую"). Nothing about *how* the receipt got here matters to this
-- function -- ticket 09's reconcile_inbox already turned it into a workflow
-- signal, and that signal is what got this automatic step scheduled at all.
-- Replaces migration 037's placeholder body now that the Inbox/receipts
-- boundary (ticket 08) and the delivery functions (ticket 09) both exist.
create or replace function payment.apply_receipt_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation core.operations%rowtype;
  v_external_request core.external_requests%rowtype;
  v_receipt core.receipts%rowtype;
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

  select * into v_external_request
    from core.external_requests
   where operation_id = v_operation.operation_id;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'payment.no_external_request',
      'message', 'operation has no external request yet'
    );
  end if;

  select * into v_receipt
    from core.receipts
   where external_request_id = v_external_request.external_request_id
   order by received_at
   limit 1;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'payment.no_receipt',
      'message', 'external request has no saved receipt yet'
    );
  end if;

  -- Idempotent by construction: a retried attempt of this same job re-reads
  -- the same row and reports the same outcome. Only the first attempt moves
  -- applied_at off null (SPEC autocheck.receipts "applied_at" -- the moment
  -- the process actually consumed the receipt, distinct from Inbox's own
  -- applied_at, which ticket 09's reconcile_inbox already set when it turned
  -- the receipt into a signal).
  update core.receipts
     set applied_at = pg_catalog.now()
   where message_id = v_receipt.message_id
     and applied_at is null;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', v_receipt.outcome,
    'result', jsonb_build_object(
      'operationId', v_operation.operation_id,
      'externalRequestId', v_external_request.external_request_id,
      'messageId', v_receipt.message_id,
      'outcome', v_receipt.outcome
    )
  );
end;
$$;

-- Replaces migration 037's other placeholder. Byte-for-byte payment.approve_v1's
-- own shape (migration 033): the deciding step (apply_receipt, here) already
-- has its own evidence -- the receipt row itself -- so this function only
-- carries the operation to its terminal status, the same common core
-- payment-review's approve/reject already run through (SPEC "Обе карты
-- исполнены общим ядром без веток по имени карты или действия в C#").
create or replace function payment.complete_v1(p_context jsonb, p_payload jsonb)
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
    'outcome', 'COMPLETED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;
