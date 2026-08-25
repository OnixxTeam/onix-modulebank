create table api.action_catalog (
  module            text        not null,
  action            text        not null,
  version           integer     not null,
  contract_version  text        not null,
  http_method       text        not null,
  target_schema     text        not null,
  target_function   text        not null,
  request_schema    jsonb       not null,
  response_schema   jsonb       not null,
  outcomes          jsonb       not null,
  required_policy   jsonb       not null,
  idempotency_mode  text        not null,
  idempotency_scope text        not null,
  timeout_ms        integer     not null,
  enabled           boolean     not null default false,
  is_default        boolean     not null default false,
  manifest_checksum text        not null,
  published_at      timestamptz not null default now(),
  primary key (module, action, version)
);

-- At most one default version per route, enforced across all versions
-- regardless of enabled state; activate/disable (ticket 06) keep it that way.
create unique index action_catalog_one_default
  on api.action_catalog (module, action) where is_default;

-- Only enabled/is_default may change after publish; every other column is
-- part of the published contract. publish_action (ticket 05) never attempts
-- such an update itself -- it detects a checksum mismatch and returns
-- manifest.conflict without touching the row -- so this trigger is a
-- backstop against any other write path, not the primary conflict check.
-- Compares whole rows minus the two mutable columns, so a future contract
-- column added to the table is protected automatically instead of silently
-- slipping past an enumerated field list.
create function api.action_catalog_prevent_contract_change()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if (to_jsonb(new) - 'enabled' - 'is_default') is distinct from (to_jsonb(old) - 'enabled' - 'is_default') then
    raise exception 'published action version is immutable';
  end if;
  return new;
end;
$$;

create trigger action_catalog_immutable
  before update on api.action_catalog
  for each row
  execute function api.action_catalog_prevent_contract_change();
