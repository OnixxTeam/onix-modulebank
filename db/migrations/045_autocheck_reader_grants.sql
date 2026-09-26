-- Week 4, ticket 01: the grants of the verifying run's read-only channel.
--
-- The role itself lives in db/init/00-roles.sh, because its password only ever
-- reaches the postgres service and the migration runner (which runs as `cli`)
-- never sees it. Its *grants* belong here, next to the views they apply to, so
-- that a new autocheck view and its grant are versioned by the same runner.
--
-- RULE for every future migration that adds an autocheck view: grant SELECT on
-- it to autocheck_reader in that same migration. The blanket grant below only
-- covers views that exist when this migration runs; the default-privileges
-- statement covers views created *later by course_owner*, which is the runner's
-- own DDL role -- but a view created by any other role would still be invisible,
-- and the verifying run reads every view it knows about.
grant usage on schema autocheck to autocheck_reader;
grant select on all tables in schema autocheck to autocheck_reader;

alter default privileges for role course_owner in schema autocheck
  grant select on tables to autocheck_reader;

-- PostgreSQL grants EXECUTE on a new function to PUBLIC by default, and the
-- verifying run rejects *any* EXECUTE the reader holds on an application
-- function. Individual migrations already revoke it function by function; this
-- is the schema-wide sweep that closes the ones they missed and makes the
-- property hold for the schemas as a whole rather than per file.
--
-- `from public` only touches the PUBLIC pseudo-role: every explicit grant to
-- course_runtime, workflow_worker, outbox_dispatcher, inbox_reconciler and
-- course_publisher survives untouched.
revoke execute on all functions in schema
  api, core, payment, operation, receipt, workflow, workflow_probe, delivery, migration
  from public;

-- Same reasoning for the future: a function added later by course_owner must
-- not arrive with EXECUTE already granted to PUBLIC.
alter default privileges for role course_owner in schema
  api, core, payment, operation, receipt, workflow, workflow_probe, delivery, migration
  revoke execute on functions from public;

-- Belt and braces on the two database-level privileges the reader must not
-- have. `revoke all on database course from public` in the init script already
-- takes TEMP away from PUBLIC, and the reader is granted CONNECT only -- but
-- CONNECT is granted there by name, so stating the negative here keeps the
-- property in the versioned history rather than only in an init script that
-- never runs again on an existing volume.
revoke create, temporary on database course from autocheck_reader;
