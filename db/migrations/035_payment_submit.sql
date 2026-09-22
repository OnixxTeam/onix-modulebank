-- Ticket 04: payment.submit and operation.events. The client names only the
-- operation; this table alone decides which flow a PAYMENT_EXECUTION or
-- PAYMENT_APPROVAL operation pins to, so no card name, version, amount, limit
-- or status ever crosses the wire from the client (SPEC "Обязательная граница
-- payment.submit").
create table core.payment_flow_binding (
  operation_kind text primary key,
  flow_name      text not null
);

insert into core.payment_flow_binding (operation_kind, flow_name) values
  ('PAYMENT_EXECUTION', 'payment-processing'),
  ('PAYMENT_APPROVAL', 'payment-review');

-- The two invariants the ticket names directly: an operation can never be
-- PROCESSING (or later) without a pinned process, and can never carry a
-- process while still CREATED. One symmetric check covers both directions at
-- once, so the two can never drift into two separately-enforced halves.
alter table core.operations
  add constraint operations_created_iff_no_process
  check ((status = 'CREATED') = (process_id is null));

-- payment.submit version 1 target. Idempotency barrier 1 (repeat of the same
-- command) is api.invoke's own idempotency_keys claim (migration 007) --
-- nothing here duplicates it. Barrier 2 (a new key against the same
-- operation) is workflow.start_process's own unique (flow_name, business_key)
-- with business_key = operationId. Barrier 3 (both barriers cleared at once
-- by a genuine race) is this function's own `for update` lock on the
-- operation row: the first caller to acquire it is the only one that ever
-- calls workflow.start_process for this operation at all, so twenty
-- concurrent first-time submits serialize into one winner and nineteen
-- readers of the row the winner already committed -- never nineteen callers
-- racing workflow.process_instance's unique constraint directly.
--
-- The subject result always reports status PROCESSING, literally, never the
-- operation's live status (contract's own const): a repeat after the
-- operation finishes must still show the pinned-at-submit-time snapshot, and
-- the pinned flow_version is read straight off the process row, which
-- start_process never revises once created.
create function payment.submit_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation  core.operations%rowtype;
  v_process    workflow.process_instance%rowtype;
  v_flow_name  text;
  v_start_data jsonb;
  v_start_hash text;
  v_started    jsonb;
  v_process_id uuid;
begin
  select * into v_operation
    from core.operations
   where operation_id = (p_payload ->> 'operationId')::uuid
   for update;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'operation.not_found',
      'message', 'operation not found'
    );
  end if;

  -- Already submitted, either by a prior call under a different idempotency
  -- key or by the winner of the race this same call just lost the lock to:
  -- read the pinned process back rather than touching start_process again.
  if v_operation.process_id is not null then
    select * into v_process from workflow.process_instance where process_id = v_operation.process_id;

    return jsonb_build_object(
      'status', 'ok',
      'outcome', 'SUBMITTED',
      'result', jsonb_build_object(
        'operationId', v_operation.operation_id,
        'processId', v_process.process_id,
        'flowName', v_process.flow_name,
        'flowVersion', v_process.flow_version,
        'status', 'PROCESSING'
      )
    );
  end if;

  select flow_name into v_flow_name
    from core.payment_flow_binding
   where operation_kind = v_operation.operation_kind;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'payment.no_flow_binding',
      'message', format('no flow is bound to operation kind %s', v_operation.operation_kind)
    );
  end if;

  v_start_data := jsonb_build_object('operationId', v_operation.operation_id);
  v_start_hash := pg_catalog.encode(pg_catalog.sha256(v_start_data::text::pg_catalog.bytea), 'hex');

  v_started := workflow.start_process(v_flow_name, v_operation.operation_id::text, v_start_data, v_start_hash);

  if v_started ->> 'status' <> 'ok' then
    return v_started;
  end if;

  v_process_id := (v_started ->> 'processId')::uuid;

  update core.operations
     set status = 'PROCESSING', process_id = v_process_id
   where operation_id = v_operation.operation_id;

  insert into core.operation_events (operation_id, event_type, payload_hash)
  values (
    v_operation.operation_id,
    'OPERATION_SUBMITTED',
    pg_catalog.encode(pg_catalog.sha256(p_payload::text::pg_catalog.bytea), 'hex')
  );

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'SUBMITTED',
    'result', jsonb_build_object(
      'operationId', v_operation.operation_id,
      'processId', v_process_id,
      'flowName', v_started ->> 'flowName',
      'flowVersion', (v_started ->> 'flowVersion')::int,
      'status', 'PROCESSING'
    )
  );
end;
$$;

revoke all on function payment.submit_v1(jsonb, jsonb) from public;

-- operation.events version 1 target. Read-only, no idempotency, same
-- controlled not-found shape as operation.get_v1 -- the current status is
-- read there; this action is only ever the history alongside it.
create function operation.events_v1(p_context jsonb, p_payload jsonb)
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
    'result', jsonb_build_object(
      'operationId', v_operation.operation_id,
      'events', coalesce((
        select jsonb_agg(jsonb_build_object(
          'eventId', e.event_id,
          'eventType', e.event_type,
          'occurredAt', e.occurred_at
        ) order by e.occurred_at)
        from core.operation_events e
        where e.operation_id = v_operation.operation_id
      ), '[]'::jsonb)
    )
  );
end;
$$;

revoke all on function operation.events_v1(jsonb, jsonb) from public;
