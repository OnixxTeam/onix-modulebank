-- Ticket 12: diagnostics.stalled version 1.
--
-- The predicate is deliberately four conditions, not one:
--
--   delivery is DEAD          -- automatic attempts are spent
--   no applied receipt        -- nothing has arrived late
--   process WAITING_SIGNAL    -- the chain is still parked on the receipt
--   operation PROCESSING      -- the operation has not been decided
--
-- The last two are what keeps a finished operation out of the selection
-- forever: migration 049 made the DEAD -> CONFIRMED transition keep dead_at, so
-- "just DEAD" would still be true of a chain a late receipt already completed.
-- A threshold on attempt_count instead of state would be worse still -- it
-- matches deliveries that still have a retry scheduled.
--
-- Read-only by construction: one SELECT, no writes, no job creation, no
-- delivery. The only trace the call leaves is the action audit the generic
-- runtime records for every action.

create function diagnostics.stalled_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select pg_catalog.jsonb_build_object(
    'status', 'ok',
    'outcome', 'FOUND',
    'result', pg_catalog.jsonb_build_object(
      'items', coalesce((
        select pg_catalog.jsonb_agg(
          pg_catalog.jsonb_build_object(
            'operationId', stalled.operation_id,
            'processId', stalled.process_id,
            'externalRequestId', stalled.external_request_id
          ) order by stalled.operation_id)
        from (
          -- An operation can own more than one external request; DISTINCT ON
          -- keeps it to one row, picking the lowest external_request_id so the
          -- answer is the same on every call.
          select distinct on (o.operation_id)
                 o.operation_id, p.process_id, ob.external_request_id
            from delivery.outbox ob
            join core.external_requests e on e.external_request_id = ob.external_request_id
            join core.operations o on o.operation_id = e.operation_id
            join workflow.process_instance p on p.process_id = o.process_id
           where ob.state = 'DEAD'
             and o.status = 'PROCESSING'
             and p.state = 'WAITING_SIGNAL'
             and not exists (
               select 1 from core.receipts r
                where r.external_request_id = ob.external_request_id
                  and r.applied_at is not null
             )
           order by o.operation_id, ob.external_request_id
        ) as stalled
      ), '[]'::jsonb)
    )
  )
$$;

revoke all on function diagnostics.stalled_v1(jsonb, jsonb) from public;
