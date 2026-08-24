-- course_runtime reads the autocheck boundary and nothing else: no direct DML
-- on the views (Postgres would otherwise auto-route simple single-table
-- selects through to the base table) and no direct access to core at all,
-- so domain state changes only ever go through the registered SECURITY
-- DEFINER target functions inside the trusted transaction.
grant usage  on schema autocheck to course_runtime;
grant select on all tables in schema autocheck to course_runtime;
revoke insert, update, delete on all tables in schema autocheck from course_runtime;

revoke all on all tables in schema core from course_runtime;
revoke all on schema core from course_runtime;
