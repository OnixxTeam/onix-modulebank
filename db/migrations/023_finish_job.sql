-- Finishing a job: the boundary that closes an attempt, closes the step,
-- resolves the outcome's transition and advances the process, all in one
-- transaction. The caller reports an outcome, never the next step -- the
-- transition lookup happens in here, against the map version pinned to the
-- process, so a worker whose lease has gone stale cannot push a decision
-- computed against state that no longer holds (see PLAN's Implementation
-- Decisions: "Интерпретация графа и продвижение процесса живут в PL/pgSQL").
--
-- Every rejection path (owner/lease_version/state mismatch, undeclared
-- outcome) returns before touching a row: workflow.lease_stale and
-- workflow.unknown_outcome both leave job, process and step exactly as they
-- were.

-- The result is kept on the attempt that produced it, not only merged into
-- process_data: process_data only ever holds the latest merge per step key,
-- so without this column a reclaimed-then-retried step's earlier attempt
-- result would be unrecoverable from workflow_attempt alone.
alter table workflow.workflow_attempt add column result jsonb;

create function workflow.finish_job(
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

  -- Merged under the step's own key, never into the root: the next step can
  -- read this step's output by key without any chance of clobbering
  -- start_data's fields (SPEC's "Данные процесса и история").
  update workflow.process_instance
     set process_data = process_data || jsonb_build_object(v_step.step_key, p_result),
         updated_at = now()
   where process_id = v_process.process_id;

  update workflow.workflow_job set state = 'DONE', updated_at = now() where job_id = p_job_id;

  perform workflow.enter_step(v_process.process_id, v_transition.to_step);

  return jsonb_build_object('status', 'ok', 'jobId', p_job_id::text, 'outcome', p_outcome);
end;
$$;

revoke all on function workflow.finish_job(uuid, text, integer, text, jsonb) from public;
grant execute on function workflow.finish_job(uuid, text, integer, text, jsonb) to workflow_worker;
