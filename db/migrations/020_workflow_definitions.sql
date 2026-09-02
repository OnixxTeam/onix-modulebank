-- Normalized storage for immutable workflow-map definitions. C# owns the
-- RFC 8785 canonicalization and hash (Workflow.Core.Canonicalization) --
-- jsonb does not preserve object key order, so the hash must be computed
-- before the document ever reaches this schema, never derived from a stored
-- copy. The source document itself is never stored: only its decomposition
-- into flow/version/step/transition/task rows and the hash that identifies
-- it.
create schema workflow;

create table workflow.flow_definition (
  flow_name  text        not null primary key,
  created_at timestamptz not null default now()
);

create table workflow.flow_version (
  flow_name      text        not null references workflow.flow_definition (flow_name),
  version        integer     not null,
  start_step     text        not null,
  canonical_hash text        not null,
  status         text        not null default 'PUBLISHED' check (status = 'PUBLISHED'),
  is_active      boolean     not null default false,
  published_at   timestamptz not null default now(),
  primary key (flow_name, version)
);

-- "Ровно одна активная версия flow" is a per-flow invariant, not a
-- system-wide one -- mirrors action_catalog_one_default (migration 002).
create unique index flow_version_one_active
  on workflow.flow_version (flow_name) where is_active;

-- Nothing outside activate_flow ever updates a published version, but a
-- defensive trigger keeps that true even against a future bug rather than
-- relying on discipline -- same shape as action_catalog_immutable
-- (migration 002), minus the one column (is_active) that command owns.
create function workflow.flow_version_prevent_definition_change()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if (to_jsonb(new) - 'is_active') is distinct from (to_jsonb(old) - 'is_active') then
    raise exception 'published flow version is immutable';
  end if;
  return new;
end;
$$;

create trigger flow_version_immutable
  before update on workflow.flow_version
  for each row
  execute function workflow.flow_version_prevent_definition_change();

create table workflow.step_definition (
  flow_name        text    not null,
  version          integer not null,
  step_key         text    not null,
  step_type        text    not null check (step_type in ('AUTOMATIC', 'WAIT_SIGNAL', 'MANUAL', 'END')),
  signal_type      text,
  wait_outcome     text,
  allowed_outcomes jsonb,
  end_outcome      text,
  primary key (flow_name, version, step_key),
  foreign key (flow_name, version) references workflow.flow_version (flow_name, version)
);

-- Primary key order (flow_name, version, from_step, outcome) is the index
-- transition lookup needs: a running process is pinned to one flow_name
-- plus version, then resolves the next step by (from_step, outcome).
create table workflow.transition_definition (
  flow_name text    not null,
  version   integer not null,
  from_step text    not null,
  outcome   text    not null,
  to_step   text    not null,
  primary key (flow_name, version, from_step, outcome),
  foreign key (flow_name, version, from_step) references workflow.step_definition (flow_name, version, step_key)
);

create table workflow.task_definition (
  flow_name       text    not null,
  version         integer not null,
  step_key        text    not null,
  module          text    not null,
  action          text    not null,
  action_version  integer not null,
  required_policy jsonb   not null,
  timeout_ms      integer not null,
  max_attempts    integer not null,
  delays_ms       jsonb   not null,
  input_mapping   jsonb   not null,
  input_constants jsonb   not null,
  primary key (flow_name, version, step_key),
  foreign key (flow_name, version, step_key) references workflow.step_definition (flow_name, version, step_key)
);

-- Publishes an already schema- and semantically-validated map document
-- (Workflow.Core.Validation.WorkflowMapValidator ran first in the CLI).
-- What only this function can see is the catalog at the instant of the
-- write, so it re-proves existence/enabled/policy-equality for every
-- automatic step's task here, right before the insert -- closing the race
-- window between an earlier `flow validate` and this call, the same reason
-- publish_action re-derives its checksum instead of trusting the caller.
create function workflow.publish_flow(p_document jsonb, p_hash text)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_flow_name  text    := p_document ->> 'flow_name';
  v_version    integer := (p_document ->> 'version')::integer;
  v_start_step text    := p_document ->> 'start_step';
  v_existing   workflow.flow_version%rowtype;
  v_step       jsonb;
  v_task       jsonb;
  v_transition jsonb;
  v_catalog    api.action_catalog%rowtype;
begin
  select * into v_existing
    from workflow.flow_version
   where flow_name = v_flow_name and version = v_version;

  if found then
    if v_existing.canonical_hash = p_hash then
      return jsonb_build_object('status', 'ok', 'flowName', v_flow_name, 'flowVersion', v_version);
    end if;

    return jsonb_build_object(
      'status', 'error',
      'code', 'flow.conflict',
      'message', 'published flow version is immutable'
    );
  end if;

  for v_step in select * from jsonb_array_elements(p_document -> 'steps')
  loop
    if v_step ->> 'type' <> 'automatic' then
      continue;
    end if;

    v_task := v_step -> 'task';

    select * into v_catalog
      from api.action_catalog
     where module = v_task ->> 'module'
       and action = v_task ->> 'action'
       and version = (v_task ->> 'action_version')::integer;

    if not found then
      return jsonb_build_object(
        'status', 'error',
        'code', 'flow.unknown_action',
        'message', format(
          'step %s: action %s.%s v%s does not exist',
          v_step ->> 'key', v_task ->> 'module', v_task ->> 'action', v_task ->> 'action_version')
      );
    end if;

    if not v_catalog.enabled then
      return jsonb_build_object(
        'status', 'error',
        'code', 'flow.disabled_action',
        'message', format(
          'step %s: action %s.%s v%s is disabled',
          v_step ->> 'key', v_task ->> 'module', v_task ->> 'action', v_task ->> 'action_version')
      );
    end if;

    if (select array_agg(p order by p) from jsonb_array_elements_text(v_task -> 'required_policy') p)
       is distinct from
       (select array_agg(p order by p) from jsonb_array_elements_text(v_catalog.required_policy) p)
    then
      return jsonb_build_object(
        'status', 'error',
        'code', 'flow.policy_mismatch',
        'message', format('step %s: required_policy does not equal action''s', v_step ->> 'key')
      );
    end if;
  end loop;

  insert into workflow.flow_definition (flow_name)
    values (v_flow_name)
    on conflict (flow_name) do nothing;

  insert into workflow.flow_version (flow_name, version, start_step, canonical_hash)
    values (v_flow_name, v_version, v_start_step, p_hash);

  for v_step in select * from jsonb_array_elements(p_document -> 'steps')
  loop
    insert into workflow.step_definition (
      flow_name, version, step_key, step_type, signal_type, wait_outcome, allowed_outcomes, end_outcome
    ) values (
      v_flow_name, v_version, v_step ->> 'key', upper(v_step ->> 'type'),
      v_step ->> 'signal_type',
      case when v_step ->> 'type' = 'wait_signal' then v_step ->> 'outcome' end,
      v_step -> 'allowed_outcomes',
      case when v_step ->> 'type' = 'end' then v_step ->> 'outcome' end
    );

    if v_step ->> 'type' = 'automatic' then
      v_task := v_step -> 'task';

      insert into workflow.task_definition (
        flow_name, version, step_key, module, action, action_version,
        required_policy, timeout_ms, max_attempts, delays_ms, input_mapping, input_constants
      ) values (
        v_flow_name, v_version, v_step ->> 'key',
        v_task ->> 'module', v_task ->> 'action', (v_task ->> 'action_version')::integer,
        v_task -> 'required_policy', (v_task ->> 'timeout_ms')::integer,
        (v_task -> 'retry' ->> 'max_attempts')::integer, v_task -> 'retry' -> 'delays_ms',
        v_task -> 'input_mapping', v_task -> 'input_constants'
      );
    end if;
  end loop;

  for v_transition in select * from jsonb_array_elements(p_document -> 'transitions')
  loop
    insert into workflow.transition_definition (flow_name, version, from_step, outcome, to_step)
      values (v_flow_name, v_version, v_transition ->> 'from', v_transition ->> 'outcome', v_transition ->> 'to');
  end loop;

  return jsonb_build_object('status', 'ok', 'flowName', v_flow_name, 'flowVersion', v_version);
end;
$$;

revoke all on function workflow.publish_flow(jsonb, text) from public;
grant usage on schema workflow to course_publisher;
grant execute on function workflow.publish_flow(jsonb, text) to course_publisher;

-- Read-side counterpart, same SECURITY DEFINER boundary as list_actions:
-- course_publisher reads flow_version only through this function, never via
-- a direct table grant.
create function workflow.list_flows()
returns jsonb
language sql
security definer
set search_path = ''
stable
as $$
  select coalesce(
    jsonb_agg(
      jsonb_build_object(
        'flowName', flow_name,
        'flowVersion', version,
        'status', status,
        'isActive', is_active,
        'publishedAt', published_at
      )
      order by flow_name, version
    ),
    '[]'::jsonb
  )
  from workflow.flow_version;
$$;

revoke all on function workflow.list_flows() from public;
grant execute on function workflow.list_flows() to course_publisher;

-- Atomic switch: both updates run inside this function's own transaction, so
-- no external observer ever sees the flow without an active version --
-- matches activate_action (migration 005).
create function workflow.activate_flow(p_flow_name text, p_version integer)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_exists boolean;
begin
  select true into v_exists
    from workflow.flow_version
   where flow_name = p_flow_name and version = p_version;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'flow.not_found', 'message', 'flow version not found');
  end if;

  update workflow.flow_version
     set is_active = false
   where flow_name = p_flow_name and is_active;

  update workflow.flow_version
     set is_active = true
   where flow_name = p_flow_name and version = p_version;

  return jsonb_build_object('status', 'ok', 'flowName', p_flow_name, 'flowVersion', p_version);
end;
$$;

revoke all on function workflow.activate_flow(text, integer) from public;
grant execute on function workflow.activate_flow(text, integer) to course_publisher;

create view autocheck.flow_versions as
  select flow_name, version as flow_version, status, is_active, published_at
  from workflow.flow_version;

grant select on autocheck.flow_versions to course_runtime;
revoke insert, update, delete on autocheck.flow_versions from course_runtime;
