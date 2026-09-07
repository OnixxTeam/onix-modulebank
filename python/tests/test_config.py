import pytest

from perimeter.config import MissingConfigError, load_dispatcher_config

BASE_ENV = {
    "PGHOST": "postgres",
    "PGDATABASE": "course",
    "PGUSER": "outbox_dispatcher",
    "PGPASSWORD": "secret",
    "PGAPPNAME": "outbox-dispatcher",
    "PROVIDER_URL": "http://provider-simulator:8081",
    "OUTBOX_OWNER": "outbox-dispatcher",
}


def test_loads_successfully_when_every_required_variable_is_present():
    config = load_dispatcher_config(dict(BASE_ENV))

    assert config.provider_url == "http://provider-simulator:8081"
    assert config.owner == "outbox-dispatcher"


def test_strips_a_trailing_slash_from_the_provider_url():
    env = dict(BASE_ENV, PROVIDER_URL="http://provider-simulator:8081/")

    config = load_dispatcher_config(env)

    assert config.provider_url == "http://provider-simulator:8081"


@pytest.mark.parametrize("missing", list(BASE_ENV))
def test_fails_explicitly_when_a_required_variable_is_missing(missing: str):
    env = {key: value for key, value in BASE_ENV.items() if key != missing}

    with pytest.raises(MissingConfigError) as excinfo:
        load_dispatcher_config(env)

    assert missing in excinfo.value.missing


def test_test_profile_selects_the_short_poll_interval():
    config = load_dispatcher_config(dict(BASE_ENV, COURSE_TEST_PROFILE="1"))

    assert config.poll_interval_seconds == 0.2


def test_unset_test_profile_selects_the_production_poll_interval():
    config = load_dispatcher_config(dict(BASE_ENV))

    assert config.poll_interval_seconds == 2.0
