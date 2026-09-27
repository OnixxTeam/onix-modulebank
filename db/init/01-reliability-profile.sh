#!/usr/bin/env bash
# Turns the reliability profile into database-level settings, before any
# migration runs.
#
# These six values reach the `postgres` service and nothing else in the published
# configuration table: the migration runner runs as `cli`, so a migration cannot
# read them, and an init script cannot write them into delivery.retry_policy either
# -- initdb scripts run before the schema exists. Database-level settings are the
# one channel that crosses that gap: set here, read by the delivery and job
# functions through current_setting at call time.
#
# Names are prefixed (`course.`) because PostgreSQL only accepts custom settings
# with a prefix. Values are strings; the functions validate and fall back to
# delivery.retry_policy when a setting is absent or unusable, so week 3's behaviour
# survives an environment that sets none of them.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<-SQL
	alter database "$POSTGRES_DB" set course.outbox_max_attempts    = '${COURSE_OUTBOX_MAX_ATTEMPTS:-4}';
	alter database "$POSTGRES_DB" set course.outbox_backoff_base_ms = '${COURSE_OUTBOX_BACKOFF_BASE_MS:-200}';
	alter database "$POSTGRES_DB" set course.outbox_backoff_max_ms  = '${COURSE_OUTBOX_BACKOFF_MAX_MS:-800}';
	alter database "$POSTGRES_DB" set course.outbox_jitter_max_ms   = '${COURSE_OUTBOX_JITTER_MAX_MS:-100}';
	alter database "$POSTGRES_DB" set course.outbox_lease_ms        = '${COURSE_OUTBOX_LEASE_MS:-2000}';
	alter database "$POSTGRES_DB" set course.job_lease_ms           = '${COURSE_JOB_LEASE_MS:-2000}';
	SQL
