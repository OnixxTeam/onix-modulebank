-- Domain tables for payment.request / operation.get (ticket 09). No grants to
-- course_runtime or course_publisher here: every read/write goes through the
-- SECURITY DEFINER target functions in migrations 010/011, owned by
-- course_owner like every other action target, so the application roles never
-- need a direct table grant at all -- that alone makes operation_events
-- insert-only for them, with nothing left to revoke.
create table core.operations (
  operation_id   uuid primary key default pg_catalog.gen_random_uuid(),
  request_id     text        not null,
  operation_kind text        not null check (operation_kind in
                   ('PAYMENT_EXECUTION','PAYMENT_APPROVAL')),
  amount         numeric(18,2) not null check (amount >= 0.01),
  currency       text        not null check (currency = 'RUB'),
  status         text        not null check (status in
                   ('CREATED','PROCESSING','COMPLETED','REJECTED')),
  process_id     uuid,                       -- always null in week 1
  created_at     timestamptz not null default pg_catalog.now(),
  updated_at     timestamptz not null default pg_catalog.now()
);

create table core.operation_events (
  event_id     uuid primary key default pg_catalog.gen_random_uuid(),
  operation_id uuid not null references core.operations(operation_id),
  event_type   text not null,
  payload_hash text not null,
  occurred_at  timestamptz not null default pg_catalog.now()
);

-- Freezes identity and payload columns once a row exists; status/process_id/
-- updated_at stay mutable for future weeks. A denylist rather than the
-- allowlist style action_catalog_immutable uses, since operations has
-- operational columns that must keep changing.
create function core.operations_prevent_identity_change()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if new.operation_id is distinct from old.operation_id
     or new.request_id is distinct from old.request_id
     or new.operation_kind is distinct from old.operation_kind
     or new.amount is distinct from old.amount
     or new.currency is distinct from old.currency
     or new.created_at is distinct from old.created_at
  then
    raise exception 'operation identity and payload fields are immutable';
  end if;
  return new;
end;
$$;

create trigger operations_identity_immutable
  before update on core.operations
  for each row
  execute function core.operations_prevent_identity_change();

-- Shared by payment.request_v1 and operation.get_v1 (migrations 010/011) so the
-- operation.result.schema.json shape is built in exactly one place.
create function core.operation_to_result(p_operation core.operations)
returns jsonb
language sql
set search_path = ''
immutable
as $$
  select jsonb_build_object(
    'operationId', p_operation.operation_id,
    'requestId', p_operation.request_id,
    'operationKind', p_operation.operation_kind,
    'amount', pg_catalog.to_char(p_operation.amount, 'FM99999999999999990.00'),
    'currency', p_operation.currency,
    'status', p_operation.status
  );
$$;

revoke all on function core.operation_to_result(core.operations) from public;
