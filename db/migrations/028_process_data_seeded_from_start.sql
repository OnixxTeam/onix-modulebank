-- Ticket 13 (open checker, first real end-to-end run): the checker's own
-- first-step input_mapping reads fields straight out of `flow start`'s
-- --data payload -- e.g. {"/mode": "/behavior"} on the start step itself.
-- Migration 021 deliberately kept process_data apart from start_data ("a
-- later ticket writing into process_data can never perturb it"), which is
-- still true and still the reason start_data/start_data_hash stay untouched
-- here -- but it also left process_data's *initial* value at '{}', so any
-- first-step mapping pointer was unresolvable by construction (see
-- WorkerLoopTests' RunOnce_MappingSourceMissing_... comment). SPEC ("Mapping")
-- never actually names a separate "start data" source, only "process data";
-- seeding process_data from the start payload (when it's an object) closes
-- that gap without touching the idempotency columns at all.
create or replace function workflow.start_process(
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
    flow_name, flow_version, business_key, state, current_step_key,
    start_data, start_data_hash, process_data
  ) values (
    p_flow_name, v_active.version, p_business_key, 'CREATED', v_active.start_step,
    p_start_data, p_start_data_hash,
    case when jsonb_typeof(p_start_data) = 'object' then p_start_data else '{}'::jsonb end
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
