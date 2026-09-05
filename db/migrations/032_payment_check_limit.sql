-- payment.check_limit version 1 target (ticket 03). The server-side
-- course-limit-v1 rule: the client never supplies a threshold, a rule
-- version, or a desired branch (assignment "Обязательная граница
-- payment.submit"/"payment-review") -- this function alone decides.
--
-- Amount is compared as core.operations.amount, a numeric column, never as
-- text: a string comparison of "99999.99" against "100000.00" does not sort
-- the way the rule requires.
--
-- The auto-approve branch alone writes a core.decisions row (source
-- LIMIT_RULE): course-limit-v1 never auto-rejects, only auto-approves or
-- defers to REVIEW_REQUIRED, so a LIMIT_RULE decision is always outcome
-- APPROVED in practice, matching the check on core.decisions. The action's
-- own outcome (WITHIN_LIMIT/REVIEW_REQUIRED, for map routing) is a distinct
-- vocabulary from the decision's outcome (APPROVED/REJECTED, for audit) --
-- conflating them would leave the map with no way to route a decision that
-- still needs a manual step.
--
-- step_instance_id comes from the worker-invoked context's jobId
-- (workflow.workflow_job), never from the payload: only the platform knows
-- which step instance is currently running this action.
create function payment.check_limit_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_operation core.operations%rowtype;
  v_step_instance_id uuid;
  v_outcome text;
begin
  select * into v_operation
    from core.operations
   where operation_id = (p_payload ->> 'operationId')::uuid;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'operation.not_found',
      'message', 'operation not found'
    );
  end if;

  if v_operation.amount <= 100000.00 then
    v_outcome := 'WITHIN_LIMIT';

    select step_instance_id into v_step_instance_id
      from workflow.workflow_job
     where job_id = (p_context ->> 'jobId')::uuid;

    insert into core.decisions (process_id, step_instance_id, source, principal, outcome, rule_version)
    values (
      (p_context ->> 'processId')::uuid,
      v_step_instance_id,
      'LIMIT_RULE',
      p_context ->> 'principal',
      'APPROVED',
      'course-limit-v1'
    );
  else
    v_outcome := 'REVIEW_REQUIRED';
  end if;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', v_outcome,
    'result', jsonb_build_object(
      'operationId', v_operation.operation_id,
      'amount', pg_catalog.to_char(v_operation.amount, 'FM99999999999999990.00'),
      'ruleVersion', 'course-limit-v1'
    )
  );
end;
$$;

revoke all on function payment.check_limit_v1(jsonb, jsonb) from public;
