-- Operational state (enabled/is_default) is the only thing a route ever
-- changes after publish. Both functions do their work in a single
-- transaction each, clearing the previous default before setting the new
-- one; action_catalog_one_default (migration 002) is the backstop if a
-- future edit reorders the statements.
create function api.activate_action(p_module text, p_action text, p_version integer)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_exists boolean;
begin
  select true into v_exists
    from api.action_catalog
   where module = p_module and action = p_action and version = p_version;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'action.not_found', 'message', 'action version not found');
  end if;

  update api.action_catalog
     set is_default = false
   where module = p_module and action = p_action and is_default;

  update api.action_catalog
     set enabled = true, is_default = true
   where module = p_module and action = p_action and version = p_version;

  return jsonb_build_object('status', 'ok', 'key', p_module || '.' || p_action, 'version', p_version);
end;
$$;

revoke all on function api.activate_action(text, text, integer) from public;
grant execute on function api.activate_action(text, text, integer) to course_publisher;

-- Disabling the current default is refused unless a replacement is given and
-- the route would otherwise be left without one; disabling the last enabled
-- version needs no replacement since the route simply stops existing for
-- clients. The target's is_default is cleared before the replacement is
-- promoted (not after, as the prose in the ticket enumerates it) so the two
-- writes never leave two rows with is_default = true at the same time --
-- action_catalog_one_default checks immediately, not at commit.
create function api.disable_action(
  p_module               text,
  p_action               text,
  p_version              integer,
  p_replacement_version  integer default null
)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_target             api.action_catalog%rowtype;
  v_has_other_enabled  boolean;
  v_replacement_exists boolean;
begin
  select * into v_target
    from api.action_catalog
   where module = p_module and action = p_action and version = p_version and enabled;

  if not found then
    return jsonb_build_object('status', 'error', 'code', 'action.not_found', 'message', 'action version not found or not enabled');
  end if;

  -- A version cannot replace itself: with the target still counted as
  -- "existing" this would otherwise sail past the replacement-required
  -- guard below and leave the route's other enabled versions without a
  -- default once the target is disabled.
  if p_replacement_version = p_version then
    return jsonb_build_object(
      'status', 'error', 'code', 'request.invalid',
      'message', 'replacement version must differ from the version being disabled'
    );
  end if;

  select exists(
    select 1 from api.action_catalog
     where module = p_module and action = p_action and version <> p_version and enabled
  ) into v_has_other_enabled;

  if v_target.is_default and v_has_other_enabled and p_replacement_version is null then
    return jsonb_build_object(
      'status', 'error', 'code', 'action.replacement_required',
      'message', 'route would be left without a default version'
    );
  end if;

  if p_replacement_version is not null then
    select exists(
      select 1 from api.action_catalog
       where module = p_module and action = p_action and version = p_replacement_version
    ) into v_replacement_exists;

    if not v_replacement_exists then
      return jsonb_build_object('status', 'error', 'code', 'action.not_found', 'message', 'replacement version not found');
    end if;
  end if;

  update api.action_catalog
     set is_default = false
   where module = p_module and action = p_action and version = p_version and is_default;

  if p_replacement_version is not null then
    update api.action_catalog
       set enabled = true, is_default = true
     where module = p_module and action = p_action and version = p_replacement_version;
  end if;

  update api.action_catalog
     set enabled = false, is_default = false
   where module = p_module and action = p_action and version = p_version;

  return jsonb_build_object('status', 'ok', 'key', p_module || '.' || p_action, 'version', p_version);
end;
$$;

revoke all on function api.disable_action(text, text, integer, integer) from public;
grant execute on function api.disable_action(text, text, integer, integer) to course_publisher;
