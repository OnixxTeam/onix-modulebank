-- Week 4, ticket 08: the six required series as one consistent projection.
--
-- One function, one statement, one snapshot. Six separate queries would each see
-- their own snapshot, so a scrape could report an empty job queue and a non-empty
-- Outbox that never coexisted; the contract asks the metrics to reflect the actual
-- state of the queues, which means the values have to agree with each other.
-- Caching them in the process is out for the same reason, and because a counter
-- held in memory walks backwards after the restart this week is built around.
create schema observability;

-- SECURITY DEFINER because course_runtime -- the role `api` connects as -- holds no
-- privilege on workflow.workflow_job, workflow.process_instance or delivery.outbox,
-- and this ticket is not a reason to give it one: the function returns six numbers,
-- not rows.
create function observability.queue_snapshot()
returns jsonb
language sql
security definer
set search_path = ''
stable
as $$
  select jsonb_build_object(
    -- Claimable *right now*, mirroring workflow.claim_jobs' own WHERE clause:
    -- ready, a due retry, or a lease that has expired under a dead owner. A metric
    -- counting only READY would read zero while the queue was in fact claimable.
    'workflowJobsReady', (
      select count(*)
        from workflow.workflow_job
       where state = 'READY'
          or (state = 'RETRY_WAIT' and next_attempt_at <= pg_catalog.now())
          or (state = 'LEASED' and lease_expires_at < pg_catalog.now())
    ),
    -- Age of the oldest claimable job, by the same ordering claim_job uses to
    -- pick one (created_at). Zero when nothing is claimable, as the contract
    -- specifies -- not null, which has no OpenMetrics representation.
    'workflowJobOldestAgeSeconds', coalesce((
      select extract(epoch from pg_catalog.now() - pg_catalog.min(created_at))
        from workflow.workflow_job
       where state = 'READY'
          or (state = 'RETRY_WAIT' and next_attempt_at <= pg_catalog.now())
          or (state = 'LEASED' and lease_expires_at < pg_catalog.now())
    ), 0),
    'workflowProcessesWaiting', (
      select count(*)
        from workflow.process_instance
       where state in ('WAITING_SIGNAL', 'WAITING_MANUAL')
    ),
    -- PENDING, LEASED and RETRY_WAIT only: a delivery that is DEAD, DELIVERED or
    -- CONFIRMED is not pending, and a RETRY_WAIT whose next attempt is still in
    -- the future is (the contract counts deferred retries).
    'outboxPending', (
      select count(*)
        from delivery.outbox
       where state in ('PENDING', 'LEASED', 'RETRY_WAIT')
    ),
    'outboxOldestAgeSeconds', coalesce((
      select extract(epoch from pg_catalog.now() - pg_catalog.min(created_at))
        from delivery.outbox
       where state in ('PENDING', 'LEASED', 'RETRY_WAIT')
    ), 0),
    -- A terminal workflow failure is a process that reached FAILED (CONTEXT.md).
    -- Counted from stored state, so the counter cannot walk backwards across a
    -- restart, and deliberately not from attempts (a failed attempt may be
    -- followed by a successful retry) nor from operations in REJECTED (a lawful
    -- domain outcome, not an execution failure).
    'workflowFailuresTotal', (
      select count(*)
        from workflow.process_instance
       where state = 'FAILED'
    )
  );
$$;

revoke all on function observability.queue_snapshot() from public;
grant usage on schema observability to course_runtime;
grant execute on function observability.queue_snapshot() to course_runtime;
