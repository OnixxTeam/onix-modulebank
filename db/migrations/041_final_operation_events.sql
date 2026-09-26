-- Ticket 16: a terminal core.operations.status may never be written without
-- exactly one matching core.operation_events row. Before this migration the
-- only writer of that table on the domain path was payment.submit_v1
-- (migration 035, OPERATION_SUBMITTED): approve/reject/complete moved the
-- status and left the immutable history behind, so the authoritative state
-- and the audit trail disagreed and the final result could not be rebuilt
-- from events alone.
--
-- The three terminal functions share one gate below rather than each growing
-- its own copy of the insert: "exactly one" is a property of the transition,
-- not of the caller, and a per-caller copy is what lets the next terminal
-- action be added without it.

-- What actually decided the operation, as a stable object -- the saved
-- receipt for the provider path, the saved decision for the review path. The
-- final event's payload_hash is taken over this, never over the action
-- payload, which carries nothing but the operation id and so would hash the
-- same for every operation's every terminal transition.
create function core.terminal_evidence(p_operation core.operations)
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_receipt  core.receipts%rowtype;
  v_decision core.decisions%rowtype;
begin
  select r.* into v_receipt
    from core.receipts r
    join core.external_requests e on e.external_request_id = r.external_request_id
   where e.operation_id = p_operation.operation_id
   order by r.received_at, r.message_id
   limit 1;

  if found then
    return jsonb_build_object(
      'evidence', 'receipt',
      'messageId', v_receipt.message_id,
      'externalRequestId', v_receipt.external_request_id,
      'outcome', v_receipt.outcome,
      'bodyHash', v_receipt.body_hash
    );
  end if;

  if p_operation.process_id is not null then
    select * into v_decision
      from core.decisions
     where process_id = p_operation.process_id
     order by created_at desc, decision_id desc
     limit 1;

    if found then
      return jsonb_build_object(
        'evidence', 'decision',
        'decisionId', v_decision.decision_id,
        'source', v_decision.source,
        'principal', v_decision.principal,
        'outcome', v_decision.outcome,
        'reasonHash', v_decision.reason_hash,
        'ruleVersion', v_decision.rule_version
      );
    end if;
  end if;

  -- No receipt and no decision: the operation itself is the only evidence
  -- there is. Reachable by a direct call to a terminal action outside either
  -- card (the rollback fixtures do exactly that), never by the real flows.
  return jsonb_build_object(
    'evidence', 'operation',
    'operationId', p_operation.operation_id,
    'requestId', p_operation.request_id,
    'operationKind', p_operation.operation_kind
  );
end;
$$;

revoke all on function core.terminal_evidence(core.operations) from public;

-- The single terminal gate. Status update and event insert happen in the
-- caller's own transaction, under the row lock taken here, so no third party
-- observes one without the other and a rollback undoes both. An already-final
-- operation is a no-op: no status rewrite, no second event -- which is what
-- makes a retried job (and a terminal action reached twice through different
-- routes) safe.
create function core.record_terminal_status(
  p_operation_id uuid,
  p_status       text,
  p_event_type   text
) returns core.operations
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation core.operations%rowtype;
begin
  select * into v_operation
    from core.operations
   where operation_id = p_operation_id
     for update;

  if not found then
    return v_operation;
  end if;

  if v_operation.status in ('COMPLETED', 'REJECTED') then
    return v_operation;
  end if;

  update core.operations
     set status = p_status
   where operation_id = p_operation_id
  returning * into v_operation;

  insert into core.operation_events (operation_id, event_type, payload_hash)
  values (
    p_operation_id,
    p_event_type,
    pg_catalog.encode(
      pg_catalog.sha256(core.terminal_evidence(v_operation)::pg_catalog.text::pg_catalog.bytea), 'hex')
  );

  return v_operation;
end;
$$;

revoke all on function core.record_terminal_status(uuid, text, text) from public;

-- The three terminal functions, now differing only in the status/event pair
-- they ask for. Bodies are otherwise byte-for-byte what migrations 033/034/040
-- had.
create or replace function payment.approve_v1(p_context jsonb, p_payload jsonb)
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

  v_operation := core.record_terminal_status(v_operation.operation_id, 'COMPLETED', 'OPERATION_COMPLETED');

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'APPROVED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

create or replace function payment.reject_v1(p_context jsonb, p_payload jsonb)
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

  v_operation := core.record_terminal_status(v_operation.operation_id, 'REJECTED', 'OPERATION_REJECTED');

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'REJECTED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;

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

  v_operation := core.record_terminal_status(v_operation.operation_id, 'COMPLETED', 'OPERATION_COMPLETED');

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'COMPLETED',
    'result', core.operation_to_result(v_operation)
  );
end;
$$;
