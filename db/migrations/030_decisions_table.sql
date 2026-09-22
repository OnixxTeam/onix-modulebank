-- core.decisions (ticket 03): one shared audit table for both the automatic
-- limit-rule decision and, from ticket 05 on, the operator's manual decision.
-- The two sources fill different columns (LIMIT_RULE never has a human
-- reason; MANUAL never has a rule version), enforced below rather than left
-- to caller discipline. reason_hash only, never the reason text itself, so
-- the autocheck projection can expose audit without leaking what was typed.
create table core.decisions (
  decision_id      uuid primary key default pg_catalog.gen_random_uuid(),
  process_id       uuid        not null references workflow.process_instance (process_id),
  step_instance_id uuid        not null references workflow.step_instance (step_instance_id),
  source           text        not null check (source in ('LIMIT_RULE', 'MANUAL')),
  principal        text        not null,
  reason_hash      text,
  outcome          text        not null check (outcome in ('APPROVED', 'REJECTED')),
  rule_version     text,
  created_at       timestamptz not null default pg_catalog.now(),
  check (
    (source = 'LIMIT_RULE' and rule_version is not null and reason_hash is null) or
    (source = 'MANUAL' and reason_hash is not null and rule_version is null)
  )
);

-- Stable observation boundary (migration 012's precedent): the checker never
-- sees core.decisions directly, and the view never raises the reason itself.
create view autocheck.decisions as
  select decision_id, process_id, step_instance_id, source, principal, reason_hash, outcome, rule_version, created_at
  from core.decisions;

grant select on autocheck.decisions to course_runtime;
revoke insert, update, delete on autocheck.decisions from course_runtime;
