-- Ticket 09: idempotent local signals and the early-signal case. `flow signal`
-- is a trusted CLI command like start/get (decision #3, week-2-brief.md: "CLI
-- ходит напрямую по Npgsql под админской ролью для flow publish/activate/
-- start/signal/get/list/validate/test-finish"), so workflow.apply_signal is
-- granted to course_publisher, never to workflow_worker.

-- message_id is globally unique -- not scoped to a process -- so a reused
-- message_id naming a different process is exactly the "any discrepancy"
-- conflict case, not a silent second acceptance under a different key.
create table workflow.workflow_signal (
  message_id   text        not null primary key,
  process_id   uuid        not null references workflow.process_instance (process_id),
  signal_type  text        not null,
  body_hash    text        not null,
  body         jsonb       not null,
  status       text        not null check (status in ('ACCEPTED', 'APPLIED')),
  received_at  timestamptz not null default now()
);

-- workflow.apply_pending_signal's own lookup (process_id, signal_type, status
-- = 'ACCEPTED') is exactly this index's leading columns.
create index workflow_signal_process_signal_type_idx
  on workflow.workflow_signal (process_id, signal_type, status);

-- Applies the oldest ACCEPTED signal of the step's own declared type to the
-- step instance currently WAITING on it, advancing the process the same way
-- finish_job advances an automatic step. A no-op whenever there is nothing to
-- apply -- no waiting step at this key, or no ACCEPTED signal yet -- so both
-- callers below (enter_step's WAIT_SIGNAL branch, for a signal that arrived
-- early; apply_signal itself, for one that arrives while the process is
-- already waiting) can call it unconditionally and let it decide.
--
-- Not granted to any role, like enter_step itself: it only ever runs nested
-- inside another SECURITY DEFINER function's already-elevated context.
create function workflow.apply_pending_signal(p_process_id uuid, p_step_key text)
returns void
language plpgsql
set search_path = ''
as $$
declare
  v_process       workflow.process_instance%rowtype;
  v_step          workflow.step_definition%rowtype;
  v_step_instance workflow.step_instance%rowtype;
  v_signal        workflow.workflow_signal%rowtype;
  v_transition    workflow.transition_definition%rowtype;
begin
  select * into v_process from workflow.process_instance where process_id = p_process_id;

  select * into v_step
    from workflow.step_definition
   where flow_name = v_process.flow_name and version = v_process.flow_version and step_key = p_step_key;

  if not found or v_step.step_type <> 'WAIT_SIGNAL' then
    return;
  end if;

  select * into v_step_instance
    from workflow.step_instance
   where process_id = p_process_id and step_key = p_step_key and state = 'WAITING'
   for update;

  if not found then
    return;
  end if;

  select * into v_signal
    from workflow.workflow_signal
   where process_id = p_process_id and signal_type = v_step.signal_type and status = 'ACCEPTED'
   order by received_at
   limit 1
   for update;

  if not found then
    return;
  end if;

  update workflow.workflow_signal set status = 'APPLIED' where message_id = v_signal.message_id;

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (p_process_id, v_step_instance.step_instance_id, 'SignalApplied');

  update workflow.step_instance
     set state = 'COMPLETED', outcome = v_step.wait_outcome, completed_at = now()
   where step_instance_id = v_step_instance.step_instance_id;

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (p_process_id, v_step_instance.step_instance_id, 'StepCompleted');

  select * into v_transition
    from workflow.transition_definition
   where flow_name = v_process.flow_name and version = v_process.flow_version
     and from_step = p_step_key and outcome = v_step.wait_outcome;

  perform workflow.enter_step(p_process_id, v_transition.to_step);
end;
$$;

-- Redefines migration 022's enter_step (itself a redefinition of migration
-- 021's), adding the one call this ticket's own WAIT_SIGNAL branch was left
-- for: an early signal, already ACCEPTED before the process ever reached this
-- step, is picked up in the same statement that marks the step WAITING --
-- closing the window a separate follow-up pass would leave open (the
-- ticket's own pitfall: "процесс уже в ожидании, а сигнал ещё не применён").
-- Every other branch is byte-for-byte what it was.
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
      update workflow.step_instance set state = 'WAITING' where step_instance_id = v_step_instance_id;

      update workflow.process_instance
         set state = 'WAITING_SIGNAL', current_step_key = p_step_key, updated_at = now()
       where process_id = p_process_id;

      perform workflow.apply_pending_signal(p_process_id, p_step_key);

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

-- The `flow signal` CLI boundary. Idempotent by message_id: an exact repeat
-- (same process, type, canonical body hash) returns duplicate:true and
-- touches nothing; any discrepancy under the same message_id is a conflict,
-- also touching nothing. A first-seen message_id of a type the pinned map
-- version never declares is rejected before it is ever stored -- only a type
-- some WAIT_SIGNAL step of this process's flow_name/version actually names is
-- accepted, at any step, not only the current one, since it may arrive early.
create function workflow.apply_signal(
  p_process_id  uuid,
  p_message_id  text,
  p_signal_type text,
  p_body_hash   text,
  p_body        jsonb
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_existing workflow.workflow_signal%rowtype;
  v_process  workflow.process_instance%rowtype;
  v_known    boolean;
begin
  select * into v_existing from workflow.workflow_signal where message_id = p_message_id for update;

  if found then
    if v_existing.process_id = p_process_id and v_existing.signal_type = p_signal_type and v_existing.body_hash = p_body_hash then
      return jsonb_build_object(
        'status', 'ok', 'duplicate', true, 'processId', v_existing.process_id::text, 'signalType', v_existing.signal_type
      );
    end if;

    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow.signal_conflict',
      'message', 'message id already used with a different process, type or body'
    );
  end if;

  select * into v_process from workflow.process_instance where process_id = p_process_id;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'process.not_found', 'message', 'process not found');
  end if;

  select true into v_known
    from workflow.step_definition
   where flow_name = v_process.flow_name and version = v_process.flow_version
     and step_type = 'WAIT_SIGNAL' and signal_type = p_signal_type
   limit 1;

  if v_known is distinct from true then
    return jsonb_build_object(
      'status', 'error',
      'code', 'workflow.unknown_signal_type',
      'message', format('signal type %s is not declared by the pinned map version', p_signal_type)
    );
  end if;

  insert into workflow.workflow_signal (message_id, process_id, signal_type, body_hash, body, status)
    values (p_message_id, p_process_id, p_signal_type, p_body_hash, p_body, 'ACCEPTED');

  insert into workflow.workflow_event (process_id, step_instance_id, event_type)
    values (p_process_id, null, 'SignalAccepted');

  -- A no-op unless the process is right now WAITING on this exact type
  -- (SPEC "Если процесс уже стоит в соответствующем ожидании — применить
  -- сигнал и продвинуть процесс в той же транзакции") -- a process waiting on
  -- a different type, not yet at any wait_signal, or already terminal all
  -- leave the signal ACCEPTED, exactly the "сохраняется, но не двигает"
  -- clause for a completed process.
  perform workflow.apply_pending_signal(p_process_id, v_process.current_step_key);

  return jsonb_build_object('status', 'ok', 'duplicate', false, 'processId', p_process_id::text, 'signalType', p_signal_type);
end;
$$;

revoke all on function workflow.apply_signal(uuid, text, text, text, jsonb) from public;
grant execute on function workflow.apply_signal(uuid, text, text, text, jsonb) to course_publisher;

create view autocheck.signals as
  select message_id, process_id, signal_type, body_hash, status, received_at
  from workflow.workflow_signal;

grant select on autocheck.signals to course_runtime;
revoke insert, update, delete on autocheck.signals from course_runtime;
