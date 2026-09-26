-- Ticket 19 (part A): the Outbox retry budget and lease now both come from
-- delivery.retry_policy, and both carry the published test-profile numbers.
--
-- Migration 039 seeded five attempts with five delays, while the published
-- profile (docs/week3/07-autocheck-outline.md, "Максимум попыток Outbox 3")
-- is the first attempt plus three retries at 200/400/800 ms -- four in total.
-- The lease was worse: `interval '30 seconds'` written straight into
-- claim_outbox's body, where no profile could reach it, so a crashed owner
-- held a delivery for 30 seconds even under the 2-second test profile.
--
-- claim_outbox's signature stays exactly as the contract pins it
-- (docs/week3/04-week-3.md: p_owner text, p_limit integer), so the lease
-- travels as configuration data in the policy row rather than as a third
-- argument -- workflow.claim_job's own p_lease_ms parameter is not an option
-- here.
alter table delivery.retry_policy add column lease_ms integer not null default 30000;
alter table delivery.retry_policy alter column lease_ms drop default;

update delivery.retry_policy
   set max_attempts = 4,
       delays_ms    = '[200, 400, 800]'::jsonb
 where id = 1;

-- The one profile-dependent value, so it is the one that gets a setter:
-- attempts and delays are the contract's retry budget and stay the same in
-- both profiles, while the lease is 2 seconds under COURSE_TEST_PROFILE=1 and
-- conservative in production, exactly like Workflow.Worker's IntervalProfile.
-- Bootstrap calls this on every run; outbox_dispatcher is deliberately not
-- granted it -- a claimant does not get to choose how long it may hold a row.
create function delivery.set_lease_ms(p_lease_ms integer)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
  if p_lease_ms is null or p_lease_ms <= 0 then
    return jsonb_build_object(
      'status', 'error',
      'code', 'delivery.invalid_lease',
      'message', 'lease must be a positive number of milliseconds'
    );
  end if;

  update delivery.retry_policy set lease_ms = p_lease_ms where id = 1;

  return jsonb_build_object('status', 'ok', 'leaseMs', p_lease_ms);
end;
$$;

revoke all on function delivery.set_lease_ms(integer) from public;
-- USAGE on the schema as well as EXECUTE on the function: migration 039
-- granted delivery's schema usage to the two perimeter roles only, and
-- EXECUTE alone is not enough to resolve delivery.set_lease_ms. No table
-- privileges come with it -- course_publisher still cannot touch
-- delivery.outbox or delivery.retry_policy directly.
grant usage on schema delivery to course_publisher;
grant execute on function delivery.set_lease_ms(integer) to course_publisher;

-- Migration 039's claim_outbox with one change: the lease interval is read
-- from the policy row instead of being written into the statement.
create or replace function delivery.claim_outbox(p_owner text, p_limit integer)
returns table (
  outbox_id           uuid,
  lease_version       bigint,
  external_request_id text,
  correlation_id      uuid,
  amount              text,
  currency            text
)
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_policy delivery.retry_policy%rowtype;
begin
  select * into v_policy from delivery.retry_policy where id = 1;

  return query
  update delivery.outbox o
     set state             = 'LEASED',
         lease_owner       = p_owner,
         lease_version     = o.lease_version + 1,
         lease_expires_at  = pg_catalog.now() + make_interval(secs => v_policy.lease_ms / 1000.0)
    from (
      select oi.outbox_id
        from delivery.outbox oi
       where (oi.state in ('PENDING', 'RETRY_WAIT') and oi.next_attempt_at <= pg_catalog.now())
          or (oi.state = 'LEASED' and oi.lease_expires_at < pg_catalog.now())
       order by oi.next_attempt_at
       for update of oi skip locked
       limit p_limit
    ) claimable
   where o.outbox_id = claimable.outbox_id
  returning o.outbox_id, o.lease_version, o.external_request_id, o.correlation_id, o.amount, o.currency;
end;
$$;
