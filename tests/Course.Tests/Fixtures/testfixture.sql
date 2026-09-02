-- Test-only target functions for ticket 13's rollback invariants (integration
-- tests 3 and 4). Applied by Integration/PostgresFixture, never by the real
-- migration runner, so none of this ships in the graded database. Same
-- SECURITY DEFINER / search_path pattern as every real target function
-- (db/migrations/010, 011): api.invoke calls these by name as course_owner,
-- so they must be owned by course_owner too, matching the "set local role
-- course_owner" wrapper the migration runner uses.

create schema testfixture;

-- Written by the fixture functions before they report failure, so a rolled-back
-- transaction leaves no row here -- the "canary" the rollback tests look for.
create table testfixture.canary_marks (
  correlation_id uuid primary key,
  created_at     timestamptz not null default pg_catalog.now()
);

-- Mirrors a target that performed a real write and then rejected the request
-- with a controlled status=error envelope (contract-reference: "Target вернул
-- status=error").
create function testfixture.error_after_insert_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  insert into testfixture.canary_marks (correlation_id)
  values ((p_context->>'correlationId')::uuid);

  return jsonb_build_object(
    'status', 'error',
    'code', 'business.rejected',
    'message', 'fixture forced rejection after a domain write'
  );
end;
$$;

revoke all on function testfixture.error_after_insert_v1(jsonb, jsonb) from public;

-- Mirrors a target that wrote data and returned status=ok, but with an outcome
-- the manifest never declared -- the C# executor must reject this as
-- action.contract_violation before commit, rolling the write back too.
create function testfixture.unknown_outcome_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  insert into testfixture.canary_marks (correlation_id)
  values ((p_context->>'correlationId')::uuid);

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'NOT_A_DECLARED_OUTCOME',
    'result', '{}'::jsonb
  );
end;
$$;

revoke all on function testfixture.unknown_outcome_v1(jsonb, jsonb) from public;

-- Trivial contract-shaped target for integration test 7 ("new action without
-- rebuild"): echoes the payload's "note" field back inside a schema-valid
-- result, proving a manifest published mid-run is callable with no C# change.
create function testfixture.echo_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'ECHOED',
    'result', jsonb_build_object('note', p_payload->>'note')
  );
end;
$$;

revoke all on function testfixture.echo_v1(jsonb, jsonb) from public;

-- Reflects the trusted context straight back as the result, so C#-side tests can
-- assert on exactly what api.invoke received: whether workflow identifiers
-- (processId/jobId/executionId/attemptId) were serialized in, and what deadline
-- the executor computed from the manifest's timeout_ms or a caller override.
create function testfixture.echo_context_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'ECHOED',
    'result', p_context
  );
end;
$$;

revoke all on function testfixture.echo_context_v1(jsonb, jsonb) from public;
