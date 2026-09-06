-- Ticket 09: the whole SQL boundary the Python perimeter will ever see, built
-- and proven before a single line of Python exists (ticket's own framing:
-- "к моменту появления первого Python-процесса спорить остаётся только о
-- транспорте"). Four fixed functions, least-privilege roles, nothing else.

-- Retry policy is data, not a literal in claim/fail_outbox's own body (SPEC
-- "Предел числа попыток и задержки берутся из конфигурации профиля, а не
-- зашиты") -- the same reasoning workflow.task_definition already applies to
-- job retries (migration 020/027), just for the one delivery boundary rather
-- than per flow step. A single row: unlike task_definition there is only ever
-- one Outbox, so a profile keyed by flow/step would be a table with one row
-- anyway.
create table delivery.retry_policy (
  id           integer not null primary key default 1 check (id = 1),
  max_attempts integer not null,
  delays_ms    jsonb   not null
);

insert into delivery.retry_policy (max_attempts, delays_ms) values (5, '[200, 400, 800, 1600, 3200]'::jsonb);

-- Least-privilege perimeter roles (SPEC "роли периметра создаются с
-- минимальными правами -- только исполнение своих функций, без прямого
-- доступа к таблицам"), granted USAGE up front so each function below can
-- grant EXECUTE to its own role right next to its own definition, the same
-- pairing every earlier migration's claim/finish/fail function uses (022,
-- 026, 027). Postgres does not treat schema USAGE as table access, and
-- neither role is ever granted anything on core, workflow, api or autocheck.
-- Neither role is a member of any other role (no `in role` clause), so
-- neither inherits course_owner's, course_publisher's, course_runtime's or
-- workflow_worker's rights either.
grant usage on schema delivery to outbox_dispatcher;
grant usage on schema delivery to inbox_reconciler;

-- Claims up to p_limit deliveries in one statement: PENDING/RETRY_WAIT rows
-- already due, or a LEASED row whose lease expired (an owner that crashed
-- mid-attempt without ever calling succeed/fail_outbox), same three-way
-- eligibility test as workflow.claim_jobs (migration 022/027). FOR UPDATE
-- SKIP LOCKED on the inner select is what makes two concurrent callers of a
-- multi-row claim never take the same row twice.
create function delivery.claim_outbox(p_owner text, p_limit integer)
returns table (
  outbox_id           uuid,
  lease_version       bigint,
  external_request_id text,
  correlation_id      uuid,
  amount              text,
  currency            text
)
language plpgsql
security definer
set search_path = ''
as $$
begin
  return query
  update delivery.outbox o
     set state             = 'LEASED',
         lease_owner       = p_owner,
         lease_version     = o.lease_version + 1,
         lease_expires_at  = pg_catalog.now() + interval '30 seconds'
    from (
      select oi.outbox_id
        from delivery.outbox oi
       where (oi.state in ('PENDING', 'RETRY_WAIT') and oi.next_attempt_at <= pg_catalog.now())
          or (oi.state = 'LEASED' and oi.lease_expires_at < pg_catalog.now())
       order by oi.next_attempt_at
       for update of oi skip locked
       limit p_limit
    ) claimable
   where o.outbox_id = claimable.outbox_id
  returning o.outbox_id, o.lease_version, o.external_request_id, o.correlation_id, o.amount, o.currency;
end;
$$;

revoke all on function delivery.claim_outbox(text, integer) from public;
grant execute on function delivery.claim_outbox(text, integer) to outbox_dispatcher;

-- Conditional owner/lease-version update in one statement (SPEC "проверяет
-- владельца и версию условно"): a caller whose lease was already reclaimed
-- (stale owner or lease_version) updates zero rows and gets a no-op back, not
-- an error -- its report simply arrived too late to matter. `state <>
-- 'CONFIRMED'` is the never-regress guard: a receipt may already have carried
-- this delivery all the way to CONFIRMED (receipt.accept_v1, migration 038)
-- before the dispatcher's own success report lands.
create function delivery.succeed_outbox(
  p_outbox_id      uuid,
  p_owner          text,
  p_lease_version  bigint,
  p_provider_payment_id text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_row delivery.outbox%rowtype;
begin
  update delivery.outbox
     set state = 'DELIVERED', delivered_at = pg_catalog.now()
   where outbox_id = p_outbox_id
     and lease_owner = p_owner
     and lease_version = p_lease_version
     and state <> 'CONFIRMED'
  returning * into v_row;

  if found then
    return jsonb_build_object(
      'status', 'ok', 'outboxId', v_row.outbox_id, 'state', v_row.state,
      'providerPaymentId', p_provider_payment_id
    );
  end if;

  select * into v_row from delivery.outbox where outbox_id = p_outbox_id;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'delivery.not_found');
  end if;

  return jsonb_build_object('status', 'ok', 'outboxId', v_row.outbox_id, 'state', v_row.state, 'skipped', true);
end;
$$;

revoke all on function delivery.succeed_outbox(uuid, text, bigint, text) from public;
grant execute on function delivery.succeed_outbox(uuid, text, bigint, text) to outbox_dispatcher;

-- Mirror of succeed_outbox. Retryable vs terminal is read off the error
-- code's own naming convention (external-contracts.md: "*.retryable" /
-- "*.terminal") -- the dispatcher classifies *what* happened, PostgreSQL
-- alone decides *whether* another attempt is spent and *when* it runs, per
-- ticket's own boundary ("Он сообщает базе классифицированный код, база
-- решает остальное").
create function delivery.fail_outbox(
  p_outbox_id      uuid,
  p_owner          text,
  p_lease_version  bigint,
  p_error_code     text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_row        delivery.outbox%rowtype;
  v_policy     delivery.retry_policy%rowtype;
  v_retryable  boolean;
  v_delay_ms   integer;
begin
  update delivery.outbox
     set attempt_count   = attempt_count + 1,
         last_error_code = p_error_code
   where outbox_id = p_outbox_id
     and lease_owner = p_owner
     and lease_version = p_lease_version
     and state <> 'CONFIRMED'
  returning * into v_row;

  if not found then
    select * into v_row from delivery.outbox where outbox_id = p_outbox_id;

    if not found then
      return jsonb_build_object('status', 'error', 'code', 'delivery.not_found');
    end if;

    return jsonb_build_object('status', 'ok', 'outboxId', v_row.outbox_id, 'state', v_row.state, 'skipped', true);
  end if;

  select * into v_policy from delivery.retry_policy where id = 1;

  v_retryable := p_error_code like '%.retryable';

  if v_retryable and v_row.attempt_count < v_policy.max_attempts then
    v_delay_ms := (v_policy.delays_ms ->> least(v_row.attempt_count - 1, jsonb_array_length(v_policy.delays_ms) - 1))::integer;

    update delivery.outbox
       set state            = 'RETRY_WAIT',
           lease_owner      = null,
           lease_expires_at = null,
           next_attempt_at  = pg_catalog.now() + make_interval(secs => v_delay_ms / 1000.0)
     where outbox_id = p_outbox_id
    returning * into v_row;
  else
    update delivery.outbox
       set state            = 'DEAD',
           lease_owner      = null,
           lease_expires_at = null
     where outbox_id = p_outbox_id
    returning * into v_row;
  end if;

  return jsonb_build_object(
    'status', 'ok', 'outboxId', v_row.outbox_id, 'state', v_row.state,
    'attemptCount', v_row.attempt_count, 'nextAttemptAt', v_row.next_attempt_at
  );
end;
$$;

revoke all on function delivery.fail_outbox(uuid, text, bigint, text) from public;
grant execute on function delivery.fail_outbox(uuid, text, bigint, text) to outbox_dispatcher;

-- The mirror problem to Outbox's claim: applies every RECEIVED inbox message
-- whose external request resolves to a pinned process, atomically, in one
-- call (SPEC "reconciler одним вызовом атомарно применяет подходящие
-- сообщения"). workflow.apply_signal (migration 026) already carries the
-- early-vs-late distinction on its own -- a signal ACCEPTED before the
-- process reaches wait_receipt sits until workflow.enter_step's own
-- apply_pending_signal call picks it up, so this function never needs to know
-- whether the pinned process is waiting yet. "Applied" here means "turned
-- into a workflow signal", the same event apply_signal itself records,
-- distinct from apply_receipt (ticket 10) actually consuming it.
--
-- FOR UPDATE OF i SKIP LOCKED on the driving row: a message this call cannot
-- resolve to a pinned process, or whose apply_signal call comes back an
-- error (workflow.signal_conflict, the one case that should not be possible
-- given messageId's own dedup scope but is cheap to guard anyway), is simply
-- left RECEIVED for the next call to retry (SPEC "неподходящее раннее
-- сообщение остаётся в принятом состоянии и не теряется").
create function delivery.reconcile_inbox(p_limit integer)
returns integer
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_row           record;
  v_process_id    uuid;
  v_body          jsonb;
  v_body_hash     text;
  v_signal_result jsonb;
  v_applied       integer := 0;
begin
  for v_row in
    select i.message_id, i.external_request_id, r.outcome
      from delivery.inbox i
      join core.receipts r on r.message_id = i.message_id
     where i.state = 'RECEIVED'
     order by i.received_at
     limit p_limit
     for update of i skip locked
  loop
    select o.process_id into v_process_id
      from core.external_requests er
      join core.operations o on o.operation_id = er.operation_id
     where er.external_request_id = v_row.external_request_id;

    if v_process_id is null then
      continue;
    end if;

    v_body := jsonb_build_object(
      'messageId', v_row.message_id,
      'externalRequestId', v_row.external_request_id,
      'outcome', v_row.outcome
    );
    v_body_hash := pg_catalog.encode(pg_catalog.sha256(v_body::text::pg_catalog.bytea), 'hex');

    v_signal_result := workflow.apply_signal(
      v_process_id,
      'payment-receipt:' || v_row.message_id,
      'payment.receipt',
      v_body_hash,
      v_body
    );

    if v_signal_result ->> 'status' <> 'ok' then
      continue;
    end if;

    update delivery.inbox
       set state = 'APPLIED', applied_at = pg_catalog.now()
     where message_id = v_row.message_id;

    v_applied := v_applied + 1;
  end loop;

  return v_applied;
end;
$$;

revoke all on function delivery.reconcile_inbox(integer) from public;
grant execute on function delivery.reconcile_inbox(integer) to inbox_reconciler;

-- Closing a pre-existing leak (same pattern as migration 027's own "Closing
-- the leaks" section): this trigger function has carried the default PUBLIC
-- EXECUTE grant every CREATE FUNCTION leaves in place since migration 009,
-- long before either perimeter role existed. It surfaces here because this
-- ticket's own privilege test (unlike any earlier one) checks every schema,
-- not just the ones a role holds USAGE on -- exactly the ticket's own
-- pitfall ("права проверяются не по нашему намерению, а по фактическим
-- привилегиям"). Revoking does not disturb the trigger itself: Postgres
-- always invokes trigger functions as the table owner, never through a
-- caller's own EXECUTE privilege.
revoke execute on function core.operations_prevent_identity_change() from public;
