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
  -v workflow_worker_password="${WORKFLOW_WORKER_PASSWORD:?}" \
  --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-'SQL'
	create role course_owner     nologin;
	create role course_migrator  login password :'course_migrator_password' in role course_owner;
	create role course_publisher login password :'course_publisher_password';
	create role course_runtime   login password :'course_runtime_password';
	create role workflow_worker  login password :'workflow_worker_password';

	revoke all on database course from public;
	grant connect on database course to course_migrator, course_publisher, course_runtime, workflow_worker;
	grant create on database course to course_owner;
	revoke all on schema public from public;
	SQL
