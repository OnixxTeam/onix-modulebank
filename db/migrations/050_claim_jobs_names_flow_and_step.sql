-- Week 4, ticket 10: the claimed job names its flow, version and step.
--
-- Every other identifier a log line needs is already in the claim payload
-- (jobId, executionId, attemptId, leaseVersion, processId) or in the task
-- (module, action, version). Flow name, flow version and step key were only in
-- rows the function had already read and then dropped, so a worker wanting to
-- log them had to query again per attempt.
--
-- Migration 048's body verbatim with those three added: `create or replace`
-- needs the whole function, and copying it is what keeps the history
-- forward-only.
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
      -- Week 4, ticket 10: flow name, flow version and step key travel with the
      -- claim so the worker can name them in its structured log without a second
      -- query per attempt. Read from rows this function already selected.
      'flowName', v_process.flow_name,
      'flowVersion', v_process.flow_version,
      'stepKey', v_step.step_key,
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
