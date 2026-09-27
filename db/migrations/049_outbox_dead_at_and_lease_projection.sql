-- Week 4, ticket 05: DEAD is a technical outcome with its own evidence, and a late
-- receipt outranks it.
--
-- Exhaustion already left `state = DEAD`, `last_error_code` and `attempt_count`
-- behind, but not *when* it happened, and the stable projection exposed none of the
-- lease columns the week-4 contract names. Both are diagnosis, not domain: nothing
-- about the operation or the process changes when a delivery dies.
alter table delivery.outbox add column dead_at timestamptz;

comment on column delivery.outbox.dead_at is
  'When the automatic attempts ran out. Kept when a late receipt confirms the delivery afterwards.';

create or replace function delivery.fail_outbox(
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
  v_row          delivery.outbox%rowtype;
  v_policy       delivery.retry_policy%rowtype;
  v_retryable    boolean;
  v_max_attempts integer;
  v_base_ms      integer;
  v_max_ms       integer;
  v_jitter_ms    integer;
  v_delay_ms     integer;
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

  v_max_attempts := profile.number('course.outbox_max_attempts', v_policy.max_attempts);
  v_retryable    := p_error_code like '%.retryable';

  if v_retryable and v_row.attempt_count < v_max_attempts then
    -- The array's own first entry is the fallback base, and its last the fallback
    -- cap, so an unset profile reproduces 200/400/800 exactly.
    v_base_ms := profile.number(
      'course.outbox_backoff_base_ms',
      (v_policy.delays_ms ->> 0)::integer);
    v_max_ms := profile.number(
      'course.outbox_backoff_max_ms',
      (v_policy.delays_ms ->> (pg_catalog.jsonb_array_length(v_policy.delays_ms) - 1))::integer);
    -- The published window is inclusive on both ends: 0..jitter_max. A fallback of
    -- 1 stands for "no jitter", so an environment without the profile keeps week
    -- 3's exact schedule; an explicit 1 is indistinguishable from that and also
    -- means no jitter, which is the only value this reader cannot express (it
    -- rejects 0 along with every other non-positive setting).
    v_jitter_ms := profile.number('course.outbox_jitter_max_ms', 1);

    -- The exponent is clamped before it multiplies: 2^31 overflows integer, so a
    -- generous retry budget would raise here instead of capping. Anything past the
    -- cap is the cap anyway.
    v_delay_ms := least(
      v_base_ms * pg_catalog.power(2, least(greatest(v_row.attempt_count - 1, 0), 20))::integer,
      v_max_ms);

    if v_jitter_ms > 1 then
      v_delay_ms := v_delay_ms + pg_catalog.floor(pg_catalog.random() * (v_jitter_ms + 1))::integer;
    end if;

    update delivery.outbox
       set state            = 'RETRY_WAIT',
           lease_owner      = null,
           lease_expires_at = null,
           next_attempt_at  = pg_catalog.now() + pg_catalog.make_interval(secs => v_delay_ms / 1000.0)
     where outbox_id = p_outbox_id
    returning * into v_row;
  else
    update delivery.outbox
       set state            = 'DEAD',
           lease_owner      = null,
           lease_expires_at = null,
           -- coalesce, not a plain assignment: a delivery can only die once, and a
           -- second pass must not move the moment it happened. Unqualified because
           -- coalesce is a parser construct, not a pg_catalog function -- qualifying
           -- it fails at call time even with an empty search_path.
           dead_at          = coalesce(dead_at, pg_catalog.now())
     where outbox_id = p_outbox_id
    returning * into v_row;
  end if;

  return jsonb_build_object(
    'status', 'ok', 'outboxId', v_row.outbox_id, 'state', v_row.state,
    'attemptCount', v_row.attempt_count, 'nextAttemptAt', v_row.next_attempt_at,
    'deadAt', v_row.dead_at, 'lastErrorCode', v_row.last_error_code
  );
end;
$$;

-- The projection the verifying run reads. Four columns are new since week 3:
-- lease_owner, lease_version, lease_until and dead_at.
--
-- `lease_until` is the contract's name for the column this schema calls
-- lease_expires_at, and it is aliased rather than renamed: the physical name is
-- referenced by claim_outbox, succeed_outbox and fail_outbox -- three functions of
-- a signature-pinned boundary -- and renaming it would touch all of them to gain
-- nothing the view cannot give.
--
-- Empty until the corresponding state is reached, per the published nullability
-- rule: no lease columns on a delivery nobody has claimed, no dead_at on one that
-- still has attempts left.
create or replace view autocheck.outbox as
  select outbox_id,
         external_request_id,
         state,
         attempt_count,
         next_attempt_at,
         last_error_code,
         created_at,
         delivered_at,
         lease_owner,
         lease_version,
         lease_expires_at as lease_until,
         dead_at
  from delivery.outbox;

grant select on autocheck.outbox to course_runtime;
-- Migration 045's rule: a new or widened autocheck view grants SELECT to the
-- read-only reader in its own migration.
grant select on autocheck.outbox to autocheck_reader;
revoke insert, update, delete on autocheck.outbox from course_runtime;
