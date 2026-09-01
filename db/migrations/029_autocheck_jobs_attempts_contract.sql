-- Ticket 13 (open checker, first real end-to-end run): TASK.md's autocheck
-- view contract (section "Обязательный интерфейс запуска") names columns and
-- types the two views below never actually matched --
--   jobs:     owner            -> must be lease_owner
--             lease_expires_at -> must be lease_until
--             execution_id     text -> must be uuid
--             lease_version    integer -> must be bigint
--   attempts: state            -> must be status, and 'LEASED' -> 'RUNNING'
--             execution_id     missing entirely -> must be uuid, joined from
--                               the owning job (workflow_attempt has no such
--                               column of its own)
--             lease_version    integer -> must be bigint
--
-- Only the two views change: the underlying workflow.workflow_job /
-- workflow.workflow_attempt columns, their C# callers and every internal
-- 'LEASED' attempt state stay exactly as they are (TASK.md: "Authoritative
-- checker использует CLI и views, а не физические таблицы"). Views can't be
-- CREATE OR REPLACE'd across a column rename, so both are dropped and
-- recreated; grants don't survive that and are reissued identically to
-- migration 022's.

drop view autocheck.attempts;
drop view autocheck.jobs;

create view autocheck.jobs as
  select j.job_id, j.process_id, j.step_instance_id, j.execution_id::uuid as execution_id,
         j.state, j.owner as lease_owner, j.lease_version::bigint as lease_version,
         j.lease_expires_at as lease_until, j.attempt_count, j.next_attempt_at,
         j.created_at, j.updated_at
  from workflow.workflow_job j;

grant select on autocheck.jobs to course_runtime;
revoke insert, update, delete on autocheck.jobs from course_runtime;

create view autocheck.attempts as
  select a.attempt_id, a.job_id, j.execution_id::uuid as execution_id,
         a.lease_version::bigint as lease_version, a.attempt_number,
         case a.state when 'LEASED' then 'RUNNING' else a.state end as status,
         a.outcome, a.error_code, a.started_at, a.finished_at
  from workflow.workflow_attempt a
  join workflow.workflow_job j on j.job_id = a.job_id;

grant select on autocheck.attempts to course_runtime;
revoke insert, update, delete on autocheck.attempts from course_runtime;
