import pytest

from perimeter.config import (
    MissingConfigError,
    load_adapter_config,
    load_dispatcher_config,
    load_reconciler_config,
)

BASE_ENV = {
    "PGHOST": "postgres",
    "PGDATABASE": "course",
    "PGUSER": "outbox_dispatcher",
    "PGPASSWORD": "secret",
    "PGAPPNAME": "outbox-dispatcher",
    "PROVIDER_URL": "http://provider-simulator:8081",
    "OUTBOX_OWNER": "outbox-dispatcher",
}

ADAPTER_ENV = {
    "PROVIDER_CALLBACK_CAPABILITY": "capability-123",
    "PROVIDER_CALLBACK_TOKEN": "callback-token",
    "PROVIDER_HMAC_SECRET": "hmac-secret",
    "RECEIPT_API_URL": "http://gateway:8080/api/receipt/accept",
}

RECONCILER_ENV = {
    "PGHOST": "postgres",
    "PGDATABASE": "course",
    "PGUSER": "inbox_reconciler",
    "PGPASSWORD": "secret",
    "PGAPPNAME": "inbox-reconciler",
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


def test_adapter_loads_successfully_when_every_required_variable_is_present():
    config = load_adapter_config(dict(ADAPTER_ENV))

    assert config.capability == "capability-123"
    assert config.callback_token == "callback-token"
    assert config.receipt_api_url == "http://gateway:8080/api/receipt/accept"


@pytest.mark.parametrize("missing", list(ADAPTER_ENV))
def test_adapter_fails_explicitly_when_a_required_variable_is_missing(missing: str):
    env = {key: value for key, value in ADAPTER_ENV.items() if key != missing}

    with pytest.raises(MissingConfigError) as excinfo:
        load_adapter_config(env)

    assert missing in excinfo.value.missing


def test_adapter_strips_a_trailing_slash_from_the_receipt_api_url():
    env = dict(ADAPTER_ENV, RECEIPT_API_URL="http://gateway:8080/api/receipt/accept/")

    config = load_adapter_config(env)

    assert config.receipt_api_url == "http://gateway:8080/api/receipt/accept"


def test_adapter_hmac_secret_is_the_exact_utf8_bytes_not_trimmed_or_decoded():
    # Two of the most common HMAC-key handling bugs: trimming incidental
    # whitespace, and treating a value that happens to look like Base64 as
    # something to decode. Either one produces a signature that verifies
    # locally and fails against the real secret bytes.
    padded = load_adapter_config(dict(ADAPTER_ENV, PROVIDER_HMAC_SECRET="  padded-secret  "))
    assert padded.hmac_secret == b"  padded-secret  "

    base64_looking = load_adapter_config(dict(ADAPTER_ENV, PROVIDER_HMAC_SECRET="c2VjcmV0LWJ5dGVz"))
    assert base64_looking.hmac_secret == b"c2VjcmV0LWJ5dGVz"


def test_reconciler_loads_successfully_when_every_required_variable_is_present():
    config = load_reconciler_config(dict(RECONCILER_ENV))

    assert config.limit == 20


@pytest.mark.parametrize("missing", list(RECONCILER_ENV))
def test_reconciler_fails_explicitly_when_a_required_variable_is_missing(missing: str):
    env = {key: value for key, value in RECONCILER_ENV.items() if key != missing}

    with pytest.raises(MissingConfigError) as excinfo:
        load_reconciler_config(env)

    assert missing in excinfo.value.missing


def test_reconciler_test_profile_selects_the_short_poll_interval():
    config = load_reconciler_config(dict(RECONCILER_ENV, COURSE_TEST_PROFILE="1"))

    assert config.poll_interval_seconds == 0.2


def test_reconciler_unset_test_profile_selects_the_production_poll_interval():
    config = load_reconciler_config(dict(RECONCILER_ENV))

    assert config.poll_interval_seconds == 2.0
