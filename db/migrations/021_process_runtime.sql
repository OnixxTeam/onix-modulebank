-- Runtime state for process instances: the part of the workflow area that
-- changes after publish, as opposed to the immutable definition tables from
-- migration 020. No executor exists yet (ticket 08), so only the two step
-- kinds that need none -- MANUAL and END -- are exercised end-to-end by this
-- migration's own tests; AUTOMATIC and WAIT_SIGNAL enter a stable waiting
-- state here and get their job/signal integration wired in by the tickets
-- that add workflow.workflow_job and workflow.workflow_signal.

create table workflow.process_instance (
  process_id       uuid        not null default pg_catalog.gen_random_uuid() primary key,
  flow_name        text        not null,
  flow_version     integer     not null,
  business_key     text        not null,
  state            text        not null
    check (state in ('CREATED', 'RUNNING', 'WAITING_SIGNAL', 'WAITING_MANUAL', 'COMPLETED', 'FAILED')),
  current_step_key text        not null,
  -- Start data is kept apart from process_data (added once a step actually
  -- produces a result, ticket 06+): start's idempotency compares against
  -- this column alone, so a later ticket writing into process_data can never
  -- perturb it.
  start_data        jsonb       not null,
  start_data_hash   text        not null,
  process_data      jsonb       not null default '{}'::jsonb,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now(),
  unique (flow_name, business_key),
  foreign key (flow_name, flow_version) references workflow.flow_version (flow_name, version)
);

-- One business key can never collide across two different processes even if
-- an application bug races two starts: the pair is the whole idempotency
-- key, not (flow_name, business_key, flow_version), which is exactly what
-- keeps a start's conflict verdict stable across an activate_flow that
-- happens in between (start_process, further down, looks this row up before
-- ever touching flow_version.is_active).
create index process_instance_flow_business_key_idx
  on workflow.process_instance (flow_name, business_key);

create table workflow.step_instance (
  step_instance_id uuid        not null default pg_catalog.gen_random_uuid() primary key,
  process_id       uuid        not null references workflow.process_instance (process_id),
  step_key         text        not null,
  step_type        text        not null check (step_type in ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END')),
  state            text        not null check (state in ('PENDING', 'READY', 'RUNNING', 'WAITING', 'COMPLETED', 'FAILED')),
  outcome          text,
  entered_at       timestamptz not null default now(),
  completed_at     timestamptz
);

create index step_instance_process_id_idx on workflow.step_instance (process_id);

-- Append-only: nothing in this migration ever issues an update or delete
-- against this table, only inserts from enter_step and start_process.
create table workflow.workflow_event (
  event_id         uuid        not null default pg_catalog.gen_random_uuid() primary key,
  process_id       uuid        not null references workflow.process_instance (process_id),
  step_instance_id uuid references workflow.step_instance (step_instance_id),
  event_type       text        not null,
  occurred_at      timestamptz not null default now()
);

create index workflow_event_process_id_idx on workflow.workflow_event (process_id);

-- Single procedure for advancing a process onto a step, reused by start_process
-- here and, in later tickets, by finish_job and apply_signal -- the one place
-- that decides what "being on this step" means, so those three callers can
-- never drift into three slightly different notions of step entry. Not
-- granted to any role: it only ever runs nested inside another SECURITY
-- DEFINER function's already-elevated context, never called directly.
create function workflow.enter_step(p_process_id uuid, p_step_key text)
returns void
language plpgsql
set search_path = ''
as $$
declare
  v_process          workflow.process_instance%rowtype;
  v_step             workflow.step_definition%rowtype;
  v_step_instance_id uuid;
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
      -- Job creation belongs to ticket 06 (workflow.workflow_job does not
      -- exist yet): this step is ready but nothing claims it before then.
      update workflow.step_instance set state = 'READY' where step_instance_id = v_step_instance_id;

      update workflow.process_instance
         set state = 'RUNNING', current_step_key = p_step_key, updated_at = now()
       where process_id = p_process_id;

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

-- Idempotent by (flow_name, business_key) via canonical start-data hash,
-- computed in C# the same way flow publish hashes a map document (RFC 8785).
-- Everything up to and including the first stable state lands in this one
-- function's transaction, so a caller never sees a 200 for a process that
-- turns out not to be there after all.
create function workflow.start_process(
  p_flow_name       text,
  p_business_key    text,
  p_start_data      jsonb,
  p_start_data_hash text
) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_existing   workflow.process_instance%rowtype;
  v_active     workflow.flow_version%rowtype;
  v_process_id uuid;
  v_result     workflow.process_instance%rowtype;
begin
  select * into v_existing
    from workflow.process_instance
   where flow_name = p_flow_name and business_key = p_business_key;

  if found then
    if v_existing.start_data_hash = p_start_data_hash then
      return jsonb_build_object(
        'status', 'ok',
        'processId', v_existing.process_id::text,
        'flowName', v_existing.flow_name,
        'flowVersion', v_existing.flow_version,
        'state', v_existing.state
      );
    end if;

    return jsonb_build_object(
      'status', 'error',
      'code', 'process.conflict',
      'message', 'process already started with different start data'
    );
  end if;

  select * into v_active
    from workflow.flow_version
   where flow_name = p_flow_name and is_active;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'flow.no_active_version',
      'message', 'flow has no active version'
    );
  end if;

  insert into workflow.process_instance (
    flow_name, flow_version, business_key, state, current_step_key, start_data, start_data_hash
  ) values (
    p_flow_name, v_active.version, p_business_key, 'CREATED', v_active.start_step, p_start_data, p_start_data_hash
  ) returning process_id into v_process_id;

  insert into workflow.workflow_event (process_id, event_type)
    values (v_process_id, 'ProcessStarted');

  perform workflow.enter_step(v_process_id, v_active.start_step);

  select * into v_result from workflow.process_instance where process_id = v_process_id;

  return jsonb_build_object(
    'status', 'ok',
    'processId', v_result.process_id::text,
    'flowName', v_result.flow_name,
    'flowVersion', v_result.flow_version,
    'state', v_result.state
  );
end;
$$;

revoke all on function workflow.start_process(text, text, jsonb, text) from public;
grant execute on function workflow.start_process(text, text, jsonb, text) to course_publisher;

-- CLI's compact "flow get": version, state, current step, nothing else --
-- the full picture (steps/jobs/attempts) is workflow.get_v1 below, reached
-- over the network with its own declared read policy, never through this
-- direct-connection function.
create function workflow.get_process_compact(p_process_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_process workflow.process_instance%rowtype;
begin
  select * into v_process from workflow.process_instance where process_id = p_process_id;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'process.not_found', 'message', 'process not found');
  end if;

  return jsonb_build_object(
    'status', 'ok',
    'processId', v_process.process_id::text,
    'flowName', v_process.flow_name,
    'flowVersion', v_process.flow_version,
    'state', v_process.state,
    'currentStepKey', v_process.current_step_key
  );
end;
$$;

revoke all on function workflow.get_process_compact(uuid) from public;
grant execute on function workflow.get_process_compact(uuid) to course_publisher;

-- Target of the workflow.get action (db/manifests/workflow.get.v1.action.json),
-- reached only through api.invoke's SECURITY DEFINER boundary like every
-- other target function -- no direct grant here, mirroring operation.get_v1.
-- jobs/attempts are always empty until tickets 06/07 add the tables backing
-- them; the response contract is already shaped for that data so the action
-- version never has to change to grow it.
create function workflow.get_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_process    workflow.process_instance%rowtype;
  v_process_id uuid;
begin
  v_process_id := (p_payload ->> 'processId')::uuid;

  select * into v_process from workflow.process_instance where process_id = v_process_id;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'process.not_found', 'message', 'process not found');
  end if;

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'FOUND',
    'result', jsonb_build_object(
      'process', jsonb_build_object(
        'processId', v_process.process_id::text,
        'businessKey', v_process.business_key,
        'flowName', v_process.flow_name,
        'flowVersion', v_process.flow_version,
        'state', v_process.state,
        'currentStepKey', v_process.current_step_key,
        'createdAt', v_process.created_at,
        'updatedAt', v_process.updated_at
      ),
      'steps', coalesce((
        select jsonb_agg(jsonb_build_object(
          'stepInstanceId', s.step_instance_id::text,
          'stepKey', s.step_key,
          'stepType', s.step_type,
          'state', s.state,
          'outcome', s.outcome,
          'enteredAt', s.entered_at,
          'completedAt', s.completed_at
        ) order by s.entered_at)
        from workflow.step_instance s
        where s.process_id = v_process.process_id
      ), '[]'::jsonb),
      'jobs', '[]'::jsonb,
      'attempts', '[]'::jsonb
    )
  );
end;
$$;

revoke all on function workflow.get_v1(jsonb, jsonb) from public;

create view autocheck.processes as
  select process_id, business_key, flow_name, flow_version, state, current_step_key, created_at, updated_at
  from workflow.process_instance;

grant select on autocheck.processes to course_runtime;
revoke insert, update, delete on autocheck.processes from course_runtime;

create view autocheck.steps as
  select step_instance_id, process_id, step_key, step_type, state, outcome, entered_at, completed_at
  from workflow.step_instance;

grant select on autocheck.steps to course_runtime;
revoke insert, update, delete on autocheck.steps from course_runtime;

create view autocheck.workflow_events as
  select event_id, process_id, step_instance_id, event_type, occurred_at
  from workflow.workflow_event;

grant select on autocheck.workflow_events to course_runtime;
revoke insert, update, delete on autocheck.workflow_events from course_runtime;
