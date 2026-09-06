-- Ticket 06: the payment-processing card's own domain step, run before any
-- network code exists. payment.prepare_external writes the external request
-- and its Outbox delivery row in the same transaction the workflow job's
-- finish_job commits in (SPEC "Outbox: решение отправить и факт отправки --
-- разные события"), so a stopped or not-yet-built dispatcher never loses the
-- intent to pay: the durable row already exists, only the HTTP call is
-- missing.
create schema delivery;

-- No grants to course_runtime or workflow_worker here, same reasoning as
-- migration 009's core.operations: every read/write goes through the
-- SECURITY DEFINER target function below, owned by course_owner.
create table core.external_requests (
  external_request_id text        primary key,
  operation_id         uuid        not null references core.operations (operation_id),
  state                text        not null check (state in ('CREATED', 'SENT', 'CONFIRMED')) default 'CREATED',
  payload_hash         text        not null,
  created_at           timestamptz not null default pg_catalog.now()
);

-- Columns beyond ticket 06's own needs (lease_owner/lease_version/
-- lease_expires_at/last_error_code) belong to ticket 09's claim/succeed/fail
-- functions, but the ticket's own boundary assigns *tables* here ("здесь
-- создаются только таблицы и строки") -- designing the shape once, now, beats
-- a second migration widening it later. lease_version is bigint, not
-- integer: the claim_outbox contract (docs/week3/04-week-3.md) fixes that
-- type literally, unlike workflow.workflow_job's own integer lease_version.
create table delivery.outbox (
  outbox_id            uuid        not null default pg_catalog.gen_random_uuid() primary key,
  external_request_id  text        not null unique references core.external_requests (external_request_id),
  correlation_id       uuid        not null,
  amount               text        not null,
  currency             text        not null,
  state                text        not null check (state in
                         ('PENDING', 'LEASED', 'RETRY_WAIT', 'DELIVERED', 'DEAD', 'CONFIRMED')) default 'PENDING',
  lease_owner          text,
  lease_version        bigint      not null default 0,
  lease_expires_at     timestamptz,
  attempt_count        integer     not null default 0,
  next_attempt_at      timestamptz not null default pg_catalog.now(),
  last_error_code      text,
  created_at           timestamptz not null default pg_catalog.now(),
  delivered_at         timestamptz
);

-- payment.prepare_external version 1 target. externalRequestId is
-- deterministic from operationId, not from the job's executionId: the
-- ticket's own hint ("Детерминированность от идентификатора операции даёт
-- устойчивость к повтору шага бесплатно") means a retried attempt of the
-- same job re-derives the identical id and takes the "already exists"
-- branch below, so exactly one external request and one Outbox row survive
-- any number of attempts -- without a separate effect-key table.
create function payment.prepare_external_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation           core.operations%rowtype;
  v_external_request_id text;
  v_existing            core.external_requests%rowtype;
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

  v_external_request_id := 'ext-' || v_operation.operation_id::text;

  select * into v_existing
    from core.external_requests
   where external_request_id = v_external_request_id;

  if found then
    return jsonb_build_object(
      'status', 'ok',
      'outcome', 'PREPARED',
      'result', jsonb_build_object(
        'operationId', v_operation.operation_id,
        'externalRequestId', v_external_request_id
      )
    );
  end if;

  insert into core.external_requests (external_request_id, operation_id, payload_hash)
  values (
    v_external_request_id,
    v_operation.operation_id,
    pg_catalog.encode(pg_catalog.sha256(p_payload::text::pg_catalog.bytea), 'hex')
  );

  -- Amount is stored as the exact contract-format string ("1000.00", never a
  -- number) up front: the provider request body and its Idempotency-Key are
  -- built straight from this row by the dispatcher (ticket 11), so the value
  -- must already be in replay-exact form -- recomputing to_char on every
  -- retry could format a boundary value differently between attempts.
  insert into delivery.outbox (external_request_id, correlation_id, amount, currency)
  values (
    v_external_request_id,
    (p_context ->> 'correlationId')::uuid,
    pg_catalog.to_char(v_operation.amount, 'FM99999999999999990.00'),
    v_operation.currency
  );

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'PREPARED',
    'result', jsonb_build_object(
      'operationId', v_operation.operation_id,
      'externalRequestId', v_external_request_id
    )
  );
end;
$$;

revoke all on function payment.prepare_external_v1(jsonb, jsonb) from public;

-- Ticket 10 replaces this body once the Inbox/receipts boundary (ticket 08)
-- and the delivery functions (ticket 09) exist. Published here, unreached in
-- this ticket's own tests (the process stops at wait_receipt), only so the
-- given payment-processing-v1 map can publish at all: workflow.publish_flow
-- (migration 020) requires every automatic step's action to already exist,
-- enabled, in api.action_catalog with a matching required_policy.
create function payment.apply_receipt_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  return jsonb_build_object(
    'status', 'error',
    'code', 'internal.not_implemented',
    'message', 'payment.apply_receipt is implemented in ticket 10'
  );
end;
$$;

revoke all on function payment.apply_receipt_v1(jsonb, jsonb) from public;

-- Same reasoning as payment.apply_receipt_v1 above: manifest must exist for
-- the map to publish, body arrives in ticket 10.
create function payment.complete_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  return jsonb_build_object(
    'status', 'error',
    'code', 'internal.not_implemented',
    'message', 'payment.complete is implemented in ticket 10'
  );
end;
$$;

revoke all on function payment.complete_v1(jsonb, jsonb) from public;

-- Stable observation boundary (migration 012's precedent).
create view autocheck.external_requests as
  select external_request_id, operation_id, state, payload_hash, created_at
  from core.external_requests;

grant select on autocheck.external_requests to course_runtime;
revoke insert, update, delete on autocheck.external_requests from course_runtime;

create view autocheck.outbox as
  select outbox_id, external_request_id, state, attempt_count, next_attempt_at, last_error_code, created_at, delivered_at
  from delivery.outbox;

grant select on autocheck.outbox to course_runtime;
revoke insert, update, delete on autocheck.outbox from course_runtime;
