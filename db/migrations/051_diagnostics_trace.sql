-- Ticket 11: diagnostics.trace version 1.
--
-- The whole point of the schema is that sections are computed from the *root*
-- of the chain (operation + process), never from the identifier the operator
-- happened to know. That is what makes eleven different identifiers of one
-- chain return one and the same set of subject facts: only query.identifier and
-- query.matchedBy differ.
--
-- Nothing here returns a JWT, a signature, a secret, a full payload, the
-- provider callback's `message` or a manual decision's `reason`: every section
-- lists its columns out by hand, and the two hash columns that stand in for
-- those texts (core.decisions.reason_hash, *.body_hash) are not among them.

create schema diagnostics;

-- RFC 3339 in UTC, spelled out rather than left to to_jsonb(timestamptz),
-- whose output follows the session's TimeZone -- a setting no part of this
-- contract controls. STRICT so a null timestamp stays a JSON null.
create function diagnostics.rfc3339(p_at timestamptz)
returns text
language sql
immutable
strict
set search_path = ''
as $$
  select pg_catalog.to_char(p_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
$$;

-- The chain root plus the kinds the operator's input matched. matched_by is
-- ordered by the contract's own enum order (correlationId ... decisionId), not
-- by a priority the contract never states -- all matches are reported.
create type diagnostics.chain_root as (
  operation_id uuid,
  process_id   uuid,
  matched_by   text[]
);

-- The only link between an HTTP command's own correlation identifier (or its
-- Idempotency-Key) and the chain that command started: api.idempotency_keys
-- keeps the envelope the command returned, and payment.submit's result names the
-- process it created.
--
-- One view holds that envelope's shape, because two callers ask opposite
-- questions of it -- the resolver asks "which chain does this identifier name",
-- the dispatch section asks "which keys and correlations name this chain". The
-- envelope is read for linkage only; not one byte of it reaches the trace.
create view diagnostics.command_envelope as
  select
    k.request_id,
    k.envelope ->> 'correlationId'                                       as correlation_id,
    (k.envelope -> 'result' ->> 'processId')::uuid                       as process_id
  from api.idempotency_keys k
  where k.envelope -> 'result' ->> 'processId'
        ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';

create function diagnostics.envelope_process(p_identifier text)
returns uuid
language sql
stable
security definer
set search_path = ''
as $$
  select c.process_id
    from diagnostics.command_envelope c
   where c.correlation_id = p_identifier or c.request_id = p_identifier
   order by c.process_id, c.request_id
   limit 1
$$;

create function diagnostics.resolve_chain_root(p_identifier text)
returns diagnostics.chain_root
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_uuid         uuid;
  v_matched      text[] := array[]::text[];
  v_operation_id uuid;
  v_process_id   uuid;
  v_root         diagnostics.chain_root;
begin
  -- A non-uuid identifier is ordinary input (externalRequestId, messageId and
  -- requestId are all free-form text), not an error.
  begin
    v_uuid := p_identifier::uuid;
  exception when others then
    v_uuid := null;
  end;

  if v_uuid is not null and (
       exists (select 1 from api.action_dispatches d where d.correlation_id = v_uuid)
    or exists (select 1 from delivery.outbox o where o.correlation_id = v_uuid))
  then
    v_matched := pg_catalog.array_append(v_matched, 'correlationId');
  end if;

  if exists (select 1 from api.action_dispatches d where d.request_id = p_identifier)
    or exists (select 1 from core.operations o where o.request_id = p_identifier)
  then
    v_matched := pg_catalog.array_append(v_matched, 'requestId');
  end if;

  if v_uuid is not null and exists (select 1 from core.operations o where o.operation_id = v_uuid) then
    v_matched := pg_catalog.array_append(v_matched, 'operationId');
  end if;

  if v_uuid is not null and exists (select 1 from workflow.process_instance p where p.process_id = v_uuid) then
    v_matched := pg_catalog.array_append(v_matched, 'processId');
  end if;

  if v_uuid is not null and exists (select 1 from workflow.step_instance s where s.step_instance_id = v_uuid) then
    v_matched := pg_catalog.array_append(v_matched, 'stepInstanceId');
  end if;

  if v_uuid is not null and exists (select 1 from workflow.workflow_job j where j.job_id = v_uuid) then
    v_matched := pg_catalog.array_append(v_matched, 'jobId');
  end if;

  if exists (select 1 from workflow.workflow_job j where j.execution_id = p_identifier) then
    v_matched := pg_catalog.array_append(v_matched, 'executionId');
  end if;

  if v_uuid is not null and exists (select 1 from workflow.workflow_attempt a where a.attempt_id = v_uuid) then
    v_matched := pg_catalog.array_append(v_matched, 'attemptId');
  end if;

  if exists (select 1 from core.external_requests e where e.external_request_id = p_identifier) then
    v_matched := pg_catalog.array_append(v_matched, 'externalRequestId');
  end if;

  if exists (select 1 from delivery.inbox i where i.message_id = p_identifier)
    or exists (select 1 from core.receipts r where r.message_id = p_identifier)
  then
    v_matched := pg_catalog.array_append(v_matched, 'messageId');
  end if;

  if v_uuid is not null and exists (select 1 from core.decisions c where c.decision_id = v_uuid) then
    v_matched := pg_catalog.array_append(v_matched, 'decisionId');
  end if;

  if pg_catalog.cardinality(v_matched) = 0 then
    v_root := (null, null, v_matched);
    return v_root;
  end if;

  -- Root derivation, most direct source first. Every branch below reaches the
  -- same two ids for one chain, which is exactly the property the acceptance
  -- criteria measure.
  if 'operationId' = any (v_matched) then
    v_operation_id := v_uuid;
  end if;

  if v_process_id is null and 'processId' = any (v_matched) then
    v_process_id := v_uuid;
  end if;

  if v_process_id is null and 'stepInstanceId' = any (v_matched) then
    select s.process_id into v_process_id from workflow.step_instance s where s.step_instance_id = v_uuid;
  end if;

  if v_process_id is null and 'jobId' = any (v_matched) then
    select j.process_id into v_process_id from workflow.workflow_job j where j.job_id = v_uuid;
  end if;

  if v_process_id is null and 'executionId' = any (v_matched) then
    select j.process_id into v_process_id from workflow.workflow_job j where j.execution_id = p_identifier;
  end if;

  if v_process_id is null and 'attemptId' = any (v_matched) then
    select j.process_id into v_process_id
      from workflow.workflow_attempt a
      join workflow.workflow_job j on j.job_id = a.job_id
     where a.attempt_id = v_uuid;
  end if;

  if v_process_id is null and 'decisionId' = any (v_matched) then
    select c.process_id into v_process_id from core.decisions c where c.decision_id = v_uuid;
  end if;

  if v_operation_id is null and 'externalRequestId' = any (v_matched) then
    select e.operation_id into v_operation_id
      from core.external_requests e where e.external_request_id = p_identifier;
  end if;

  if v_operation_id is null and 'messageId' = any (v_matched) then
    select e.operation_id into v_operation_id
      from core.external_requests e
     where e.external_request_id in (
       select i.external_request_id from delivery.inbox i where i.message_id = p_identifier
       union
       select r.external_request_id from core.receipts r where r.message_id = p_identifier
     )
     limit 1;
  end if;

  if v_operation_id is null and 'requestId' = any (v_matched) then
    select o.operation_id into v_operation_id from core.operations o where o.request_id = p_identifier;
  end if;

  if v_operation_id is null and 'correlationId' = any (v_matched) then
    select e.operation_id into v_operation_id
      from delivery.outbox ob
      join core.external_requests e on e.external_request_id = ob.external_request_id
     where ob.correlation_id = v_uuid;
  end if;

  -- A correlation or Idempotency-Key that belongs to a worker attempt reaches
  -- the chain through the job whose execution id the worker uses as its request
  -- id; one that belongs to an HTTP command reaches it through the stored
  -- envelope. Both are still root derivation, just one hop longer.
  if v_process_id is null and v_operation_id is null
    and ('correlationId' = any (v_matched) or 'requestId' = any (v_matched))
  then
    select j.process_id into v_process_id
      from api.action_dispatches d
      join workflow.workflow_job j on j.execution_id = d.request_id
     where d.correlation_id = v_uuid or d.request_id = p_identifier
     limit 1;

    if v_process_id is null then
      v_process_id := diagnostics.envelope_process(p_identifier);
    end if;
  end if;

  if v_operation_id is null and v_process_id is not null then
    select o.operation_id into v_operation_id from core.operations o where o.process_id = v_process_id;
  end if;

  if v_process_id is null and v_operation_id is not null then
    select o.process_id into v_process_id from core.operations o where o.operation_id = v_operation_id;
  end if;

  v_root := (v_operation_id, v_process_id, v_matched);
  return v_root;
end;
$$;

-- Section functions. Each one is keyed by the root, takes nothing from the
-- operator's input, and orders by time then identifier so two identical calls
-- return byte-identical arrays.

-- The dispatch rows of a chain are reachable two ways, and both are computed
-- from the root: a worker call carries the job's execution id as its request
-- id, and an HTTP command's stored envelope carries both its Idempotency-Key
-- and its correlation identifier next to the process it created.
--
-- p_identifier is the operator's own input and is here for the one chain shape
-- that has no root at all -- a dispatch that errored before anything
-- domain-level existed. For a chain with a root it adds nothing the two links
-- above do not already reach, which is what keeps this section identical
-- across all eleven identifiers.
create function diagnostics.section_dispatches(
  p_operation_id uuid, p_process_id uuid, p_identifier text)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  with envelopes as (
    select c.request_id, c.correlation_id
      from diagnostics.command_envelope c
     where c.process_id = p_process_id
  ),
  -- The receipt-adapter uses the provider's own message id as its
  -- Idempotency-Key, so the dispatch row of an arriving receipt is reachable
  -- from the chain's messages -- and reachable the same way whichever identifier
  -- the operator asked by.
  messages as (
    select i.message_id as value
      from delivery.inbox i
      join core.external_requests e on e.external_request_id = i.external_request_id
     where e.operation_id = p_operation_id
    union
    select r.message_id
      from core.receipts r
      join core.external_requests e on e.external_request_id = r.external_request_id
     where e.operation_id = p_operation_id
  ),
  request_ids as (
    select j.execution_id as value from workflow.workflow_job j where j.process_id = p_process_id
    union
    select e.request_id from envelopes e
    union
    select m.value from messages m
    union
    -- Only when there is no root at all. Seeding the operator's own input into a
    -- chain that does have a root is what breaks the contract's main promise:
    -- an identifier that happens to be some dispatch's request id would add a row
    -- the other ten identifiers cannot see.
    select p_identifier where p_operation_id is null and p_process_id is null
  ),
  correlation_ids as (
    select ob.correlation_id::text as value
      from delivery.outbox ob
      join core.external_requests e on e.external_request_id = ob.external_request_id
     where e.operation_id = p_operation_id
    union
    select e.correlation_id from envelopes e
    union
    select p_identifier where p_operation_id is null and p_process_id is null
  )
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'correlationId', d.correlation_id,
      -- request_id is nullable (an action without idempotency carries none);
      -- the contract's dispatch.requestId is a required string, so "none" is
      -- the empty string rather than a dropped field.
      'requestId', coalesce(d.request_id, ''),
      'module', d.module,
      'action', d.action,
      'version', d.version,
      'status', d.status,
      'outcome', d.outcome,
      'occurredAt', diagnostics.rfc3339(d.occurred_at)
    ) order by d.occurred_at, d.correlation_id, d.module, d.action), '[]'::jsonb)
  from api.action_dispatches d
  where d.correlation_id::text in (select value from correlation_ids where value is not null)
     or d.request_id in (select value from request_ids where value is not null)
$$;

create function diagnostics.section_operation(p_operation_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select pg_catalog.jsonb_build_object(
    'operationId', o.operation_id,
    'requestId', o.request_id,
    'operationKind', o.operation_kind,
    'amount', pg_catalog.to_char(o.amount, 'FM99999999999999990.00'),
    'currency', o.currency,
    'status', o.status,
    'processId', o.process_id,
    'createdAt', diagnostics.rfc3339(o.created_at),
    'updatedAt', diagnostics.rfc3339(o.updated_at)
  )
  from core.operations o
  where o.operation_id = p_operation_id
$$;

create function diagnostics.section_operation_events(p_operation_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'eventId', e.event_id,
      'eventType', e.event_type,
      'occurredAt', diagnostics.rfc3339(e.occurred_at)
    ) order by e.occurred_at, e.event_id), '[]'::jsonb)
  from core.operation_events e
  where e.operation_id = p_operation_id
$$;

create function diagnostics.section_process(p_process_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select pg_catalog.jsonb_build_object(
    'processId', p.process_id,
    'flowName', p.flow_name,
    'flowVersion', p.flow_version,
    'state', p.state,
    'currentStepKey', p.current_step_key,
    'createdAt', diagnostics.rfc3339(p.created_at),
    'updatedAt', diagnostics.rfc3339(p.updated_at)
  )
  from workflow.process_instance p
  where p.process_id = p_process_id
$$;

create function diagnostics.section_steps(p_process_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'stepInstanceId', s.step_instance_id,
      'stepKey', s.step_key,
      'stepType', s.step_type,
      'state', s.state,
      'outcome', s.outcome,
      'enteredAt', diagnostics.rfc3339(s.entered_at),
      'completedAt', diagnostics.rfc3339(s.completed_at)
    ) order by s.entered_at, s.step_instance_id), '[]'::jsonb)
  from workflow.step_instance s
  where s.process_id = p_process_id
$$;

-- No translation for job.state: migration 027 replaced workflow_job_state_check
-- with the contract's own vocabulary (READY, LEASED, RETRY_WAIT, SUCCEEDED,
-- DEAD), so the column already holds exactly the published enum. The attempt
-- section below is the one that still needs a mapping.
create function diagnostics.section_jobs(p_process_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'jobId', j.job_id,
      'stepInstanceId', j.step_instance_id,
      'executionId', j.execution_id,
      'state', j.state,
      'leaseVersion', j.lease_version,
      'attemptCount', j.attempt_count,
      'nextAttemptAt', diagnostics.rfc3339(j.next_attempt_at)
    ) order by j.created_at, j.job_id), '[]'::jsonb)
  from workflow.workflow_job j
  where j.process_id = p_process_id
$$;

-- The one real vocabulary gap: workflow_attempt.state still carries week 2's
-- own names (claim_jobs writes 'LEASED'), and the contract calls that state
-- RUNNING. Mapping here rather than rewriting the constraint and every writer
-- of it for the sake of an observability contract.
create function diagnostics.section_attempts(p_process_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'attemptId', a.attempt_id,
      'jobId', a.job_id,
      'executionId', j.execution_id,
      'leaseVersion', a.lease_version,
      'attemptNumber', a.attempt_number,
      'status', case a.state when 'LEASED' then 'RUNNING' else a.state end,
      'outcome', a.outcome,
      'errorCode', a.error_code,
      'startedAt', diagnostics.rfc3339(a.started_at),
      'finishedAt', diagnostics.rfc3339(a.finished_at)
    ) order by a.started_at, a.attempt_id), '[]'::jsonb)
  from workflow.workflow_attempt a
  join workflow.workflow_job j on j.job_id = a.job_id
  where j.process_id = p_process_id
$$;

create function diagnostics.section_outbox(p_operation_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'outboxId', ob.outbox_id,
      'externalRequestId', ob.external_request_id,
      'state', ob.state,
      'attemptCount', ob.attempt_count,
      'leaseVersion', ob.lease_version,
      'nextAttemptAt', diagnostics.rfc3339(ob.next_attempt_at),
      'lastErrorCode', ob.last_error_code,
      'createdAt', diagnostics.rfc3339(ob.created_at),
      'deliveredAt', diagnostics.rfc3339(ob.delivered_at),
      'deadAt', diagnostics.rfc3339(ob.dead_at)
    ) order by ob.created_at, ob.outbox_id), '[]'::jsonb)
  from delivery.outbox ob
  join core.external_requests e on e.external_request_id = ob.external_request_id
  where e.operation_id = p_operation_id
$$;

create function diagnostics.section_inbox(p_operation_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'messageId', i.message_id,
      'state', i.state,
      'receivedAt', diagnostics.rfc3339(i.received_at),
      'appliedAt', diagnostics.rfc3339(i.applied_at)
    ) order by i.received_at, i.message_id), '[]'::jsonb)
  from delivery.inbox i
  join core.external_requests e on e.external_request_id = i.external_request_id
  where e.operation_id = p_operation_id
$$;

-- signature_valid is filtered, not projected as-is: the contract pins
-- receipt.signatureValid to const true, so a row that somehow recorded an
-- unverified receipt would make the whole result fail its own schema.
create function diagnostics.section_receipts(p_operation_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'messageId', r.message_id,
      'externalRequestId', r.external_request_id,
      'outcome', r.outcome,
      'signatureValid', r.signature_valid,
      'receivedAt', diagnostics.rfc3339(r.received_at),
      'appliedAt', diagnostics.rfc3339(r.applied_at)
    ) order by r.received_at, r.message_id), '[]'::jsonb)
  from core.receipts r
  join core.external_requests e on e.external_request_id = r.external_request_id
  where e.operation_id = p_operation_id
    and r.signature_valid
$$;

create function diagnostics.section_decisions(p_process_id uuid)
returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
  select coalesce(pg_catalog.jsonb_agg(
    pg_catalog.jsonb_build_object(
      'decisionId', c.decision_id,
      'stepInstanceId', c.step_instance_id,
      'source', c.source,
      'principal', c.principal,
      'outcome', c.outcome,
      'ruleVersion', c.rule_version,
      'createdAt', diagnostics.rfc3339(c.created_at)
    ) order by c.created_at, c.decision_id), '[]'::jsonb)
  from core.decisions c
  where c.process_id = p_process_id
$$;

-- The action target itself: resolve the root once, then compose the twelve
-- required properties (eleven sections plus the echo of the query).
create function diagnostics.trace_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
  v_identifier text := p_payload ->> 'identifier';
  v_root       diagnostics.chain_root;
begin
  v_root := diagnostics.resolve_chain_root(v_identifier);

  if pg_catalog.cardinality(v_root.matched_by) = 0 then
    return pg_catalog.jsonb_build_object(
      'status', 'error',
      'code', 'diagnostics.trace_not_found',
      'message', 'no chain matches this identifier'
    );
  end if;

  return pg_catalog.jsonb_build_object(
    'status', 'ok',
    'outcome', 'FOUND',
    'result', pg_catalog.jsonb_build_object(
      'query', pg_catalog.jsonb_build_object(
        'identifier', v_identifier,
        'matchedBy', pg_catalog.to_jsonb(v_root.matched_by)
      ),
      'dispatches', diagnostics.section_dispatches(v_root.operation_id, v_root.process_id, v_identifier),
      'operation', diagnostics.section_operation(v_root.operation_id),
      'operationEvents', diagnostics.section_operation_events(v_root.operation_id),
      'process', diagnostics.section_process(v_root.process_id),
      'steps', diagnostics.section_steps(v_root.process_id),
      'jobs', diagnostics.section_jobs(v_root.process_id),
      'attempts', diagnostics.section_attempts(v_root.process_id),
      'outbox', diagnostics.section_outbox(v_root.operation_id),
      'inbox', diagnostics.section_inbox(v_root.operation_id),
      'receipts', diagnostics.section_receipts(v_root.operation_id),
      'decisions', diagnostics.section_decisions(v_root.process_id)
    )
  );
end;
$$;

revoke all on diagnostics.command_envelope from public;
revoke all on function diagnostics.rfc3339(timestamptz) from public;
revoke all on function diagnostics.resolve_chain_root(text) from public;
revoke all on function diagnostics.section_dispatches(uuid, uuid, text) from public;
revoke all on function diagnostics.envelope_process(text) from public;
revoke all on function diagnostics.section_operation(uuid) from public;
revoke all on function diagnostics.section_operation_events(uuid) from public;
revoke all on function diagnostics.section_process(uuid) from public;
revoke all on function diagnostics.section_steps(uuid) from public;
revoke all on function diagnostics.section_jobs(uuid) from public;
revoke all on function diagnostics.section_attempts(uuid) from public;
revoke all on function diagnostics.section_outbox(uuid) from public;
revoke all on function diagnostics.section_inbox(uuid) from public;
revoke all on function diagnostics.section_receipts(uuid) from public;
revoke all on function diagnostics.section_decisions(uuid) from public;
revoke all on function diagnostics.trace_v1(jsonb, jsonb) from public;
