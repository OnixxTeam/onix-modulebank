-- Ticket 05: completing a manual step. A third way to advance a process,
-- deliberately not folded into apply_signal -- a manual decision has its own
-- idempotency key (api.invoke's, keyed on the workflow.manual action) and its
-- own source of allowed outcomes (the pinned card's transition table, read
-- the same way finish_job reads it for an automatic step), so sharing
-- apply_signal's function would mean one function juggling two unrelated
-- idempotency keys and two unrelated outcome vocabularies.
--
-- Not granted to any role, like enter_step and apply_pending_signal: it only
-- ever runs nested inside workflow.manual_v1's already-elevated context. Also
-- like those two (migration 027's own closing revoke), a bare CREATE FUNCTION
-- leaves it EXECUTE-able by PUBLIC by default, which is what makes it show up
-- in workflow_worker's effective privileges without a revoke of its own --
-- FailJobRetryPrivilegesTests' four-function closing check would otherwise
-- see a fifth.
create function workflow.complete_manual_step(
  p_process_id       uuid,
  p_step_instance_id uuid,
  p_outcome          text
) returns jsonb
language plpgsql
set search_path = ''
as $$
declare
  v_process    workflow.process_instance%rowtype;
  v_step       workflow.step_instance%rowtype;
  v_transition workflow.transition_definition%rowtype;
begin
  -- Scoped to (step_instance_id, process_id) together: a step instance that
  -- exists but belongs to a different process is exactly as absent as one
  -- that does not exist at all, never a cross-process peek. The lock taken
  -- here is what turns two operators racing the same step into one accepted
  -- effect and one clean conflict, rather than a database uniqueness error --
  -- the second decision blocks until the first's transaction commits, then
  -- reads state = COMPLETED and takes the conflict branch below.
  select * into v_step
    from workflow.step_instance
   where step_instance_id = p_step_instance_id and process_id = p_process_id
   for update;

  if not found or v_step.step_type <> 'MANUAL' then
    return jsonb_build_object(
      'status', 'error',
      'code', 'step.not_found',
      'message', 'manual step instance not found for this process'
    );
  end if;

  -- A manual step instance is only ever WAITING or COMPLETED (enter_step's
  -- MANUAL branch never produces any other state), so anything other than
  -- WAITING here means some decision -- this one's own race loss, or a stale
  -- reference to an already-decided step -- already completed it.
  if v_step.state <> 'WAITING' then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow.decision_conflict',
      'message', 'manual step is not waiting for a decision'
    );
  end if;

  select * into v_process from workflow.process_instance where process_id = p_process_id;

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

  update workflow.step_instance
     set state = 'COMPLETED', outcome = p_outcome, completed_at = now()
   where step_instance_id = v_step.step_instance_id;

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (p_process_id, v_step.step_instance_id, 'ManualDecided');

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (p_process_id, v_step.step_instance_id, 'StepCompleted');

  perform workflow.enter_step(p_process_id, v_transition.to_step);

  return jsonb_build_object(
    'status', 'ok',
    'processId', p_process_id::text,
    'stepInstanceId', v_step.step_instance_id::text,
    'outcome', p_outcome
  );
end;
$$;

revoke execute on function workflow.complete_manual_step(uuid, uuid, text) from public;

-- workflow.manual version 1 target, published on the same generic HTTP route
-- as every other action -- no dedicated controller. The decision row is
-- written here, never inside complete_manual_step, so a rejected completion
-- (unknown step, wrong type, already decided, unknown outcome) never leaves a
-- decision behind: core.decisions only ever grows on an accepted effect.
-- Principal comes from p_context, which TrustedContext builds from the
-- verified JWT before the payload is even parsed -- the payload's own schema
-- (additionalProperties: false, no principal field) makes a client-supplied
-- principal structurally impossible, not just policy-discouraged.
create function workflow.manual_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_process_id       uuid := (p_payload ->> 'processId')::uuid;
  v_step_instance_id uuid := (p_payload ->> 'stepInstanceId')::uuid;
  v_decision         text := p_payload ->> 'decision';
  v_reason           text := p_payload ->> 'reason';
  v_principal        text := p_context ->> 'principal';
  v_completion       jsonb;
  v_decision_id      uuid;
begin
  v_completion := workflow.complete_manual_step(v_process_id, v_step_instance_id, v_decision);

  if v_completion ->> 'status' <> 'ok' then
    return v_completion;
  end if;

  insert into core.decisions (process_id, step_instance_id, source, principal, reason_hash, outcome)
  values (
    v_process_id,
    v_step_instance_id,
    'MANUAL',
    v_principal,
    pg_catalog.encode(pg_catalog.sha256(convert_to(v_reason, 'UTF8')), 'hex'),
    v_decision
  )
  returning decision_id into v_decision_id;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', v_decision,
    'result', jsonb_build_object(
      'decisionId', v_decision_id,
      'processId', v_process_id,
      'stepInstanceId', v_step_instance_id,
      'decision', v_decision,
      'source', 'MANUAL',
      'principal', v_principal
    )
  );
end;
$$;

revoke all on function workflow.manual_v1(jsonb, jsonb) from public;
