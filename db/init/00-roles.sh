#!/usr/bin/env bash
# Creates roles only; schemas are left to the checksummed migration runner (ticket 03).
#
# Passwords come from the container environment (set in compose.yaml from .env)
# and are substituted with `psql -v` rather than baked into a plain .sql file,
# so no secret is written into the image or left interpolated on disk.
set -euo pipefail

psql -v ON_ERROR_STOP=1 \
  -v course_migrator_password="${COURSE_MIGRATOR_PASSWORD:?}" \
  -v course_publisher_password="${COURSE_PUBLISHER_PASSWORD:?}" \
  -v course_runtime_password="${COURSE_RUNTIME_PASSWORD:?}" \
  -v course_worker_password="${COURSE_WORKER_PASSWORD:?}" \
  -v course_outbox_password="${COURSE_OUTBOX_PASSWORD:?}" \
  -v course_inbox_password="${COURSE_INBOX_PASSWORD:?}" \
  -v course_autocheck_password="${COURSE_AUTOCHECK_PASSWORD:?}" \
  --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-'SQL'
	create role course_owner       nologin;
	create role course_migrator    login password :'course_migrator_password' in role course_owner;
	create role course_publisher   login password :'course_publisher_password';
	create role course_runtime     login password :'course_runtime_password';
	create role workflow_worker    login password :'course_worker_password';
	create role outbox_dispatcher  login password :'course_outbox_password';
	create role inbox_reconciler   login password :'course_inbox_password';

	-- The verifying run's own read-only channel: it executes every statement of
	-- its check as this role and asserts current_user/transaction_read_only
	-- before anything else. Created here rather than by a migration because its
	-- password only ever reaches the postgres service
	-- (COURSE_AUTOCHECK_PASSWORD is addressed to `postgres` alone in the
	-- published configuration table) -- the migration runner, which runs as
	-- `cli`, never sees it. Its grants do live in a migration, next to the
	-- views they apply to.
	create role autocheck_reader  login password :'course_autocheck_password';

	-- Read-only on the role, not on the session: the verifying run sets no
	-- parameter of its own, it connects and reads transaction_read_only back.
	alter role autocheck_reader set default_transaction_read_only = on;

	revoke all on database course from public;
	grant connect on database course to
	  course_migrator, course_publisher, course_runtime, workflow_worker, outbox_dispatcher, inbox_reconciler,
	  autocheck_reader;
	grant create on database course to course_owner;
	revoke all on schema public from public;
	SQL
