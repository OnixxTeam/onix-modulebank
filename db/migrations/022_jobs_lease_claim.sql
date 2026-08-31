-- Jobs and attempts: the queue an AUTOMATIC step's entry feeds and a worker
-- drains via claim_job. No executor exists yet (ticket 08) -- claim is
-- exercised here by direct calls and by racing two connections against the
-- same row. workflow_worker (created below) is the only role ever granted
-- execute on claim_job; it gets no direct table access.

create table workflow.workflow_job (
  job_id           uuid        not null default pg_catalog.gen_random_uuid() primary key,
  process_id       uuid        not null references workflow.process_instance (process_id),
  step_instance_id uuid        not null references workflow.step_instance (step_instance_id),
  -- The subject-effect idempotency key (TrustedContext.ExecutionId). Set once
  -- at insert, alongside the job, and never touched again -- not by a retry,
  -- not by a reclaim -- so every attempt of this job calls the target action
  -- with the same key.
  execution_id     text        not null default pg_catalog.gen_random_uuid()::text unique,
  -- DONE/FAILED are unreachable until ticket 07/10 write to them; declaring
  -- them now avoids a second migration widening this constraint later, the
  -- same reasoning as process_instance.state (migration 021).
  state            text        not null check (state in ('READY', 'DEFERRED', 'LEASED', 'DONE', 'FAILED')),
  owner            text,
  -- The fencing token: bumped on every claim and every reclaim, never on
  -- anything else. finish_job (ticket 07) accepts a completion only when the
  -- caller's lease_version still matches this column.
  lease_version    integer     not null default 0,
  lease_expires_at timestamptz,
  -- Every claim and reclaim opens a new attempt and increments this; it is
  -- not the failure budget ticket 10 will enforce against max_attempts --
  -- conflating the two would fail a job early on nothing but lost workers.
  attempt_count    integer     not null default 0,
  next_attempt_at  timestamptz,
  created_at       timestamptz not null default now(),
  updated_at       timestamptz not null default now()
);

-- claim_job's own WHERE clause (ready, due-deferred, expired-leased) is
-- exactly this column's three live states -- everything else is DONE/FAILED
-- and permanently excluded, so a narrow index on it is what the query needs.
create index workflow_job_state_idx on workflow.workflow_job (state);

create table workflow.workflow_attempt (
  attempt_id     uuid        not null default pg_catalog.gen_random_uuid() primary key,
  job_id         uuid        not null references workflow.workflow_job (job_id),
  lease_version  integer     not null,
  attempt_number integer     not null,
  state          text        not null check (state in ('LEASED', 'SUCCEEDED', 'FAILED', 'STALE')),
  outcome        text,
  error_code     text,
  started_at     timestamptz not null default now(),
  finished_at    timestamptz
);

create index workflow_attempt_job_id_idx on workflow.workflow_attempt (job_id);

-- Redefines migration 021's enter_step, adding the one branch that was left
-- for this ticket: an AUTOMATIC step now opens a job in READY state (and its
-- own history event) instead of just marking the step instance ready with
-- nothing behind it. Every other branch is byte-for-byte what it was.
create or replace function workflow.enter_step(p_process_id uuid, p_step_key text)
returns void
language plpgsql
set search_path = ''
as $$
declare
  v_process          workflow.process_instance%rowtype;
  v_step             workflow.step_definition%rowtype;
  v_step_instance_id uuid;
  v_job_id           uuid;
begin
  select * into v_process from workflow.process_instance where process_id = p_process_id;

  select * into v_step
    from workflow.step_definition
   where flow_name = v_process.flow_name and version = v_process.flow_version and step_key = p_step_key;

  insert into workflow.step_instance (process_id, step_key, step_type, state)
    values (p_process_id, p_step_key, v_step.step_type, 'PENDING')
    returning step_instance_id into v_step_instance_id;

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (p_process_id, v_step_instance_id, 'StepEntered');

  case v_step.step_type
    when 'MANUAL' then
      update workflow.step_instance set state = 'WAITING' where step_instance_id = v_step_instance_id;

      update workflow.process_instance
         set state = 'WAITING_MANUAL', current_step_key = p_step_key, updated_at = now()
       where process_id = p_process_id;

    when 'WAIT_SIGNAL' then
      -- A signal that already arrived for this process/type (flow signal,
      -- ticket 09) gets applied here once workflow.workflow_signal exists;
      -- until then this step can only be entered and waited on.
      update workflow.step_instance set state = 'WAITING' where step_instance_id = v_step_instance_id;

      update workflow.process_instance
         set state = 'WAITING_SIGNAL', current_step_key = p_step_key, updated_at = now()
       where process_id = p_process_id;

    when 'AUTOMATIC' then
      update workflow.step_instance set state = 'READY' where step_instance_id = v_step_instance_id;

      update workflow.process_instance
         set state = 'RUNNING', current_step_key = p_step_key, updated_at = now()
       where process_id = p_process_id;

      insert into workflow.workflow_job (process_id, step_instance_id, state)
        values (p_process_id, v_step_instance_id, 'READY')
        returning job_id into v_job_id;

      insert into workflow.workflow_event (process_id, step_instance_id, event_type)
        values (p_process_id, v_step_instance_id, 'JobScheduled');

    when 'END' then
      update workflow.step_instance
         set state = 'COMPLETED', outcome = v_step.end_outcome, completed_at = now()
       where step_instance_id = v_step_instance_id;

      update workflow.process_instance
         set state = 'COMPLETED', current_step_key = p_step_key, updated_at = now()
       where process_id = p_process_id;

      insert into workflow.workflow_event (process_id, step_instance_id, event_type)
        values (p_process_id, v_step_instance_id, 'StepCompleted');

      insert into workflow.workflow_event (process_id, step_instance_id, event_type)
        values (p_process_id, v_step_instance_id, 'ProcessCompleted');
  end case;
end;
$$;

-- Claims one job (never a batch: see the ticket's own reasoning -- a batch's
-- tail would outlive the test profile's two-second lease and mint spurious
-- stale attempts) and returns everything a worker needs to call the action
-- and later call finish_job, without another catalog read. FOR UPDATE SKIP
-- LOCKED is what makes two concurrent callers never receive the same row:
-- the loser skips a row the winner is still holding instead of blocking on
-- it, so it either lands on the next claimable row or returns an empty job.
--
-- Ready, due-deferred and expired-leased are the one query this function
-- runs; no separate sweeper promotes any of them first, deliberately -- a
-- fifth function on this role would be a fifth right, and the role's rights
-- are meant to be exhaustive.
create function workflow.claim_job(p_owner text, p_lease_ms integer)
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
      or (state = 'DEFERRED' and next_attempt_at <= now())
      or (state = 'LEASED' and lease_expires_at < now())
   order by created_at
   for update skip locked
   limit 1;

  if not found then
    return jsonb_build_object('status', 'ok', 'job', null);
  end if;

  -- Reclaim: the row we just locked was LEASED with an expired lease. The
  -- attempt that lease belonged to is stale now -- close it before opening
  -- the new one, so exactly one attempt of this job is ever LEASED at a time.
  if v_job.state = 'LEASED' then
    update workflow.workflow_attempt
       set state = 'STALE', finished_at = now()
     where job_id = v_job.job_id and lease_version = v_job.lease_version and state = 'LEASED';
  end if;

  v_lease_expires_at := now() + make_interval(secs => p_lease_ms / 1000.0);

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

revoke all on function workflow.claim_job(text, integer) from public;

-- workflow_worker (login role, own password, created by db/init/00-roles.sh
-- alongside course_migrator/course_publisher/course_runtime) is the
-- not-yet-built worker's connection identity. Its rights are meant to stay
-- exhaustive: claim_job today, finish_job/fail_job/apply_signal once the
-- tickets behind them land, and never a direct table grant.
grant usage on schema workflow to workflow_worker;
grant execute on function workflow.claim_job(text, integer) to workflow_worker;

create view autocheck.jobs as
  select job_id, process_id, step_instance_id, execution_id, state, owner,
         lease_version, lease_expires_at, attempt_count, next_attempt_at, created_at, updated_at
  from workflow.workflow_job;

grant select on autocheck.jobs to course_runtime;
revoke insert, update, delete on autocheck.jobs from course_runtime;

create view autocheck.attempts as
  select attempt_id, job_id, lease_version, attempt_number, state, outcome, error_code, started_at, finished_at
  from workflow.workflow_attempt;

grant select on autocheck.attempts to course_runtime;
revoke insert, update, delete on autocheck.attempts from course_runtime;
