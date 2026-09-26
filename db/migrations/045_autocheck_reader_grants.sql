-- Week 4, ticket 01: the grants of the verifying run's read-only channel.
--
-- The role itself lives in db/init/00-roles.sh, because its password only ever
-- reaches the postgres service and the migration runner (which runs as `cli`)
-- never sees it. Its *grants* belong here, next to the views they apply to, so
-- that a new autocheck view and its grant are versioned by the same runner.
grant usage on schema autocheck to autocheck_reader;
grant select on all tables in schema autocheck to autocheck_reader;

-- The blanket grant above only reaches views that exist right now, so the
-- statement below carries the rule forward. It names `course_owner` because
-- default privileges key on the role that *creates* the object: the runner
-- connects as course_migrator but wraps every migration in `set local role
-- course_owner` (PostgresMigrationRunner.ApplyOneFileAsync), so course_owner is
-- the creating role. Naming course_migrator instead is not merely wrong, it is
-- rejected outright -- course_owner may not set defaults for a role it is not a
-- member of -- and naming a third role would apply cleanly and then silently
-- never fire. AutocheckReaderRoleTests pins the behaviour rather than the name.
--
-- Migration 013's convention -- each new autocheck view grants SELECT to its
-- reader in its own migration -- stays primary. This is the safety net under it,
-- not a replacement, and AutocheckReaderRoleTests covers both.
alter default privileges for role course_owner in schema autocheck
  grant select on tables to autocheck_reader;

-- PostgreSQL grants EXECUTE on a new function to PUBLIC by default, and the
-- verifying run rejects *any* EXECUTE the reader holds on an application
-- function. Individual migrations already revoke it function by function, so this
-- sweep changes nothing today -- verified by removing it and watching the reader
-- still hold none. It is here so the property holds for the schemas as a whole
-- rather than only per file.
--
-- `from public` only touches the PUBLIC pseudo-role: every explicit grant to
-- course_runtime, workflow_worker, outbox_dispatcher, inbox_reconciler and
-- course_publisher survives untouched.
--
-- There is deliberately no paired ALTER DEFAULT PRIVILEGES for functions. It
-- would read as the obvious future-proofing, and on postgres:17 it does nothing:
-- the REVOKE form records no row in pg_default_acl at all, and the GRANT form
-- collapses back to the built-in default, so a function created afterwards still
-- arrives with EXECUTE for PUBLIC (measured, not assumed). The standing guard for
-- week 4's new functions is therefore a test --
-- AutocheckReaderRoleTests.TheReaderCannotExecuteAnApplicationFunction fails the
-- moment any function in these schemas forgets its own revoke.
revoke execute on all functions in schema
  api, core, payment, operation, receipt, workflow, workflow_probe, delivery, migration
  from public;
