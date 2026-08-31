-- Ticket 10: bounded retry, consistent failure and the closed privilege list.
-- Three things land together because they are the same boundary seen from
-- three angles -- the function the worker calls after a rolled-back action
-- transaction, the vocabulary that function's own table already declared for
-- states it could not yet reach, and the role whose rights only become
-- exhaustive once this fourth function exists.
--
-- workflow_job.state was declared in migration 022 as ('READY', 'DEFERRED',
-- 'LEASED', 'DONE', 'FAILED') -- a placeholder guess made before this ticket's
-- own vocabulary (TASK.md "Job states: READY, LEASED, RETRY_WAIT, SUCCEEDED,
-- DEAD") existed. DEFERRED/DONE were never observable outside this schema;
-- FAILED was never reachable at all, since a failed job is DEAD, not FAILED
-- (FAILED belongs to step/process, not job). Fixing the vocabulary here, not
-- widening it, keeps exactly one job-state contract on the table for
-- claim_jobs, finish_job and fail_job to share from this point on.
alter table workflow.workflow_job drop constraint workflow_job_state_check;
alter table workflow.workflow_job add constraint workflow_job_state_check
  check (state in ('READY', 'LEASED', 'RETRY_WAIT', 'SUCCEEDED', 'DEAD'));

-- workflow.claim_job is renamed to workflow.claim_jobs: the role's own
-- contract (TASK.md, week-2-brief.md #61, and the checker's own
-- worker_database_security allowed_boundaries) all name the function this
-- way, not the singular migration 022 picked. Renaming instead of leaving the
-- singular name in place matters here specifically because this ticket is
-- the one that closes the role's list and tests it exhaustively -- a name the
-- checker does not recognise would make that list wrong regardless of how
-- many rights it actually has. Body is byte-for-byte migration 022's, minus
-- the one literal this migration's own state fix requires.
create function workflow.claim_jobs(p_owner text, p_lease_ms integer)
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

revoke all on function workflow.claim_jobs(text, integer) from public;
grant execute on function workflow.claim_jobs(text, integer) to workflow_worker;

-- Dropping the old name also drops its grant to workflow_worker: nothing else
-- ever called it (PostgresWorkflowJobRuntime and every direct-connection test
-- move to claim_jobs in this same commit).
drop function workflow.claim_job(text, integer);

-- finish_job's own terminal literal follows the same vocabulary fix.
-- Everything else -- fencing, transition lookup, event/history writes -- is
-- unchanged from migration 023.
create or replace function workflow.finish_job(
  p_job_id       uuid,
  p_owner        text,
  p_lease_version integer,
  p_outcome      text,
  p_result       jsonb
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_job        workflow.workflow_job%rowtype;
  v_process    workflow.process_instance%rowtype;
  v_step       workflow.step_instance%rowtype;
  v_transition workflow.transition_definition%rowtype;
begin
  select * into v_job from workflow.workflow_job where job_id = p_job_id for update;

  if not found
     or v_job.state <> 'LEASED'
     or v_job.owner is distinct from p_owner
     or v_job.lease_version <> p_lease_version
  then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow.lease_stale',
      'message', 'job is not leased to this owner at this lease version'
    );
  end if;

  select * into v_step from workflow.step_instance where step_instance_id = v_job.step_instance_id;
  select * into v_process from workflow.process_instance where process_id = v_job.process_id;

  select * into v_transition
    from workflow.transition_definition
   where flow_name = v_process.flow_name
     and version = v_process.flow_version
     and from_step = v_step.step_key
     and outcome = p_outcome;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow.unknown_outcome',
      'message', format('outcome %s has no transition from step %s', p_outcome, v_step.step_key)
    );
  end if;

  update workflow.workflow_attempt
     set state = 'SUCCEEDED', outcome = p_outcome, result = p_result, finished_at = now()
   where job_id = p_job_id and lease_version = p_lease_version and state = 'LEASED';

  update workflow.step_instance
     set state = 'COMPLETED', outcome = p_outcome, completed_at = now()
   where step_instance_id = v_step.step_instance_id;

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (v_process.process_id, v_step.step_instance_id, 'AttemptSucceeded');

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (v_process.process_id, v_step.step_instance_id, 'StepCompleted');

  update workflow.process_instance
     set process_data = process_data || jsonb_build_object(v_step.step_key, p_result),
         updated_at = now()
   where process_id = v_process.process_id;

  update workflow.workflow_job set state = 'SUCCEEDED', updated_at = now() where job_id = p_job_id;

  perform workflow.enter_step(v_process.process_id, v_transition.to_step);

  return jsonb_build_object('status', 'ok', 'jobId', p_job_id::text, 'outcome', p_outcome);
end;
$$;

-- Failing an attempt: the mirror of finish_job, called after the caller's own
-- action transaction has already rolled back (mapping/request rejections
-- never opened one at all). Re-checks owner/lease_version/state exactly like
-- finish_job -- a stale caller (already reclaimed) gets workflow.lease_stale
-- and touches nothing, since the job is no longer this caller's to fail.
--
-- The retry budget is counted from FAILED attempt rows, never from
-- workflow_job.attempt_count: attempt_count also counts STALE attempts a
-- reclaim opens on a crashed worker's behalf, which spent no failure budget
-- at all (SPEC, this ticket's own trap). Counting the budget from
-- attempt_count would starve a job that merely lost a worker twice.
create function workflow.fail_job(
  p_job_id        uuid,
  p_owner         text,
  p_lease_version integer,
  p_error_code    text,
  p_retryable     boolean
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_job          workflow.workflow_job%rowtype;
  v_process      workflow.process_instance%rowtype;
  v_step         workflow.step_instance%rowtype;
  v_task         workflow.task_definition%rowtype;
  v_failed_count integer;
  v_delay_ms     integer;
begin
  select * into v_job from workflow.workflow_job where job_id = p_job_id for update;

  if not found
     or v_job.state <> 'LEASED'
     or v_job.owner is distinct from p_owner
     or v_job.lease_version <> p_lease_version
  then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow.lease_stale',
      'message', 'job is not leased to this owner at this lease version'
    );
  end if;

  select * into v_step from workflow.step_instance where step_instance_id = v_job.step_instance_id;
  select * into v_process from workflow.process_instance where process_id = v_job.process_id;

  select * into v_task
    from workflow.task_definition
   where flow_name = v_process.flow_name and version = v_process.flow_version and step_key = v_step.step_key;

  update workflow.workflow_attempt
     set state = 'FAILED', error_code = p_error_code, finished_at = now()
   where job_id = p_job_id and lease_version = p_lease_version and state = 'LEASED';

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (v_process.process_id, v_step.step_instance_id, 'AttemptFailed');

  select count(*) into v_failed_count
    from workflow.workflow_attempt
   where job_id = p_job_id and state = 'FAILED';

  -- Retryable and budget not yet exhausted: come back to READY through
  -- RETRY_WAIT at the declared delay for this attempt number. Every other
  -- case (non-retryable, or retryable with nothing left) is the same
  -- terminal path -- a non-retryable failure never gets a second chance
  -- regardless of budget, and an exhausted budget stops a retryable one.
  if p_retryable and v_failed_count < v_task.max_attempts then
    v_delay_ms := (v_task.delays_ms ->> (v_failed_count - 1))::integer;

    update workflow.workflow_job
       set state = 'RETRY_WAIT',
           owner = null,
           lease_expires_at = null,
           next_attempt_at = now() + make_interval(secs => v_delay_ms / 1000.0),
           updated_at = now()
     where job_id = p_job_id;

    return jsonb_build_object('status', 'ok', 'jobId', p_job_id::text, 'jobState', 'RETRY_WAIT');
  end if;

  update workflow.workflow_job set state = 'DEAD', updated_at = now() where job_id = p_job_id;

  update workflow.step_instance
     set state = 'FAILED', completed_at = now()
   where step_instance_id = v_step.step_instance_id;

  update workflow.process_instance
     set state = 'FAILED', updated_at = now()
   where process_id = v_process.process_id;

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (v_process.process_id, v_step.step_instance_id, 'TaskFailed');

  return jsonb_build_object('status', 'ok', 'jobId', p_job_id::text, 'jobState', 'DEAD');
end;
$$;

revoke all on function workflow.fail_job(uuid, text, integer, text, boolean) from public;
grant execute on function workflow.fail_job(uuid, text, integer, text, boolean) to workflow_worker;

-- Migration 025 granted api.get_action_manifest to workflow_worker because
-- PostgresActionExecutor's shared pipeline always re-read the manifest before
-- this ticket, on every caller's behalf. That grant is now a fifth executable
-- function the role's own contract (TASK.md, week-2-brief.md #61) never
-- declared: claim_jobs already hands the worker every manifest field its
-- pipeline needs (module/action/version, request/response schema, outcomes,
-- required_policy, timeout_ms), so the C# side (PostgresActionExecutor,
-- ActionCallerContext.Manifest) now skips that read for a worker-driven call
-- and this grant has no caller left.
revoke execute on function api.get_action_manifest(text, text, integer) from workflow_worker;

-- Closing the leaks: every function in the two schemas workflow_worker holds
-- USAGE on (api, workflow) that never got its own "revoke ... from public"
-- has been executable by workflow_worker this whole time through the default
-- PUBLIC grant every CREATE FUNCTION leaves in place -- the ticket's own
-- trap. None of these four is meant for direct use by anyone: three are
-- trigger functions Postgres invokes on its own, and enter_step/
-- apply_pending_signal only ever run nested inside another SECURITY DEFINER
-- function's already-elevated context. Revoking does not disturb any of
-- that -- a function's owner always retains implicit execute on its own
-- functions regardless of what is granted to PUBLIC, which is how the
-- SECURITY DEFINER callers above keep calling them after this.
revoke execute on function api.action_catalog_prevent_contract_change() from public;
revoke execute on function workflow.flow_version_prevent_definition_change() from public;
revoke execute on function workflow.enter_step(uuid, text) from public;
revoke execute on function workflow.apply_pending_signal(uuid, text) from public;
