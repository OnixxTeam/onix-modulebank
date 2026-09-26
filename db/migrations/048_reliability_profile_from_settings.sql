-- Week 4, ticket 04: the reliability profile becomes configuration the functions
-- read, instead of numbers written into a migration.
--
-- Migration 043 put the retry budget, the delays and the delivery lease into
-- delivery.retry_policy, which was the right place while the values were fixed.
-- They are not: the published table addresses COURSE_OUTBOX_MAX_ATTEMPTS,
-- COURSE_OUTBOX_BACKOFF_BASE_MS, COURSE_OUTBOX_BACKOFF_MAX_MS,
-- COURSE_OUTBOX_JITTER_MAX_MS, COURSE_OUTBOX_LEASE_MS and COURSE_JOB_LEASE_MS to
-- the `postgres` service alone. A migration cannot see them (the runner is `cli`)
-- and an init script cannot write them into a table that does not exist yet, so
-- db/init/01-reliability-profile.sh sets them as database-level settings and the
-- functions below read them at call time.
--
-- delivery.retry_policy stays as the fallback. The profile is an override, not a
-- replacement: an environment that sets nothing behaves exactly as week 3 did.
create schema profile;

-- One reader for all six, because "absent, unparseable or non-positive means fall
-- back" is the same decision every time. current_setting(..., true) returns null
-- for an unset custom setting rather than raising, which is the whole reason the
-- second argument exists.
create function profile.number(p_setting text, p_fallback integer)
returns integer
language plpgsql
stable
set search_path = ''
as $$
declare
  v_raw   text;
  v_value integer;
begin
  v_raw := pg_catalog.current_setting(p_setting, true);

  if v_raw is null or pg_catalog.btrim(v_raw) = '' then
    return p_fallback;
  end if;

  begin
    v_value := v_raw::integer;
  exception when others then
    return p_fallback;
  end;

  if v_value <= 0 then
    return p_fallback;
  end if;

  return v_value;
end;
$$;

revoke all on function profile.number(text, integer) from public;

-- Migration 043's claim_outbox with one change: the lease comes from the profile,
-- falling back to the policy row. The signature stays exactly as the published
-- contract pins it (p_owner text, p_limit integer).
create or replace function delivery.claim_outbox(p_owner text, p_limit integer)
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
declare
  v_policy   delivery.retry_policy%rowtype;
  v_lease_ms integer;
begin
  select * into v_policy from delivery.retry_policy where id = 1;
  v_lease_ms := profile.number('course.outbox_lease_ms', v_policy.lease_ms);

  return query
  update delivery.outbox o
     set state             = 'LEASED',
         lease_owner       = p_owner,
         lease_version     = o.lease_version + 1,
         lease_expires_at  = pg_catalog.now() + pg_catalog.make_interval(secs => v_lease_ms / 1000.0)
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

-- Migration 039's fail_outbox with the budget and the schedule read from the
-- profile. Two things change shape:
--
--  * the delay is computed (base * 2^(attempt-1), capped at max) rather than
--    indexed out of the delays array, because base/max are what the published
--    variables give us; the array remains the fallback when they are unset, so an
--    environment without the profile keeps week 3's exact schedule;
--  * jitter is added here, where next_attempt_at is decided. It has to be: the
--    dispatcher does not choose delays, and COURSE_OUTBOX_JITTER_MAX_MS is
--    addressed to PostgreSQL. Two dispatchers failing the same delivery at the
--    same instant must not line their retries up.
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
    v_jitter_ms := profile.number('course.outbox_jitter_max_ms', 1) - 1;

    v_delay_ms := least(
      v_base_ms * pg_catalog.power(2, greatest(v_row.attempt_count - 1, 0))::integer,
      v_max_ms);

    if v_jitter_ms > 0 then
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

-- The job lease keeps its parameter -- a worker passes the value it was configured
-- with -- but a caller without a usable one now gets the profile instead of a
-- lease of zero. Migration 027's body verbatim, with that one line changed:
-- `create or replace` needs the whole function, and copying it is what keeps the
-- history forward-only.
create or replace function workflow.claim_jobs(p_owner text, p_lease_ms integer)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_job        workflow.workflow_job%rowtype;
  v_process    workflow.process_instance%rowtype;
  v_step       workflow.step_instance%rowtype;
  v_task       workflow.task_definition%rowtype;
  v_catalog    api.action_catalog%rowtype;
  v_attempt_id uuid;
  v_lease_expires_at timestamptz;
begin
  select * into v_job
    from workflow.workflow_job
   where state = 'READY'
      or (state = 'RETRY_WAIT' and next_attempt_at <= now())
      or (state = 'LEASED' and lease_expires_at < now())
   order by created_at
   for update skip locked
   limit 1;

  if not found then
    return jsonb_build_object('status', 'ok', 'job', null);
  end if;

  if v_job.state = 'LEASED' then
    update workflow.workflow_attempt
       set state = 'STALE', finished_at = now()
     where job_id = v_job.job_id and lease_version = v_job.lease_version and state = 'LEASED';
  end if;

  -- The caller's value wins when it has one; otherwise the profile decides, which
  -- is what makes COURSE_JOB_LEASE_MS meaningful on the PostgreSQL side (the
  -- published table addresses it to `postgres` as well as to the workers).
  v_lease_expires_at := now() + make_interval(
    secs => (case
              when p_lease_ms is not null and p_lease_ms > 0 then p_lease_ms
              else profile.number('course.job_lease_ms', 30000)
            end) / 1000.0);

  update workflow.workflow_job
     set state = 'LEASED',
         owner = p_owner,
         lease_version = lease_version + 1,
         lease_expires_at = v_lease_expires_at,
         attempt_count = attempt_count + 1,
         updated_at = now()
   where job_id = v_job.job_id
   returning * into v_job;

  insert into workflow.workflow_attempt (job_id, lease_version, attempt_number, state)
    values (v_job.job_id, v_job.lease_version, v_job.attempt_count, 'LEASED')
    returning attempt_id into v_attempt_id;

  select * into v_process from workflow.process_instance where process_id = v_job.process_id;
  select * into v_step from workflow.step_instance where step_instance_id = v_job.step_instance_id;

  select * into v_task
    from workflow.task_definition
   where flow_name = v_process.flow_name and version = v_process.flow_version and step_key = v_step.step_key;

  select * into v_catalog
    from api.action_catalog
   where module = v_task.module and action = v_task.action and version = v_task.action_version;

  return jsonb_build_object(
    'status', 'ok',
    'job', jsonb_build_object(
      'jobId', v_job.job_id::text,
      'executionId', v_job.execution_id,
      'attemptId', v_attempt_id::text,
      'leaseVersion', v_job.lease_version,
      'leaseExpiresAt', v_job.lease_expires_at,
      'process', jsonb_build_object(
        'processId', v_process.process_id::text,
        'data', v_process.process_data
      ),
      'task', jsonb_build_object(
        'module', v_task.module,
        'action', v_task.action,
        'actionVersion', v_task.action_version,
        'requestSchema', v_catalog.request_schema,
        'responseSchema', v_catalog.response_schema,
        'outcomes', v_catalog.outcomes,
        'requiredPolicy', v_task.required_policy,
        'timeoutMs', v_task.timeout_ms,
        'inputMapping', v_task.input_mapping,
        'inputConstants', v_task.input_constants,
        'retry', jsonb_build_object('maxAttempts', v_task.max_attempts, 'delaysMs', v_task.delays_ms)
      )
    )
  );
end;
$$;
