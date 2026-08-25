-- Stable observation boundary for black-box verification (ticket 11). Physical
-- tables stay ours; the checker only ever sees these five views, with column
-- names and types matching the contract exactly.
create view autocheck.contract_info as
  select 'course-1'::text as contract_version, pg_catalog.now() as generated_at;

create view autocheck.action_definitions as
  select
    module, action, version, http_method, target_schema, target_function,
    outcomes, enabled, is_default
  from api.action_catalog;

create view autocheck.action_dispatches as
  select
    correlation_id, request_id, module, action, version, principal,
    payload_hash, status, outcome, occurred_at
  from api.action_dispatches;

create view autocheck.operations as
  select
    operation_id, request_id, operation_kind, amount, currency, status,
    process_id, created_at, updated_at
  from core.operations;

create view autocheck.operation_events as
  select event_id, operation_id, event_type, payload_hash, occurred_at
  from core.operation_events;
