import json

import pytest

from perimeter.callback import ValidCallback, parse_callback

VALID = {
    "providerPaymentId": "provider-123",
    "operationId": "external-123",
    "result": "COMPLETED",
    "message": "Payment completed",
    "occurredAt": "2026-09-04T12:00:00Z",
}


def _body(**overrides) -> bytes:
    payload = dict(VALID, **overrides)
    return json.dumps(payload).encode("utf-8")


def test_a_valid_callback_is_parsed():
    result = parse_callback(_body())

    assert result == ValidCallback(
        provider_payment_id="provider-123",
        operation_id="external-123",
        result="COMPLETED",
        occurred_at="2026-09-04T12:00:00Z",
    )


def test_rejected_outcome_is_accepted_too():
    result = parse_callback(_body(result="REJECTED"))

    assert result is not None
    assert result.result == "REJECTED"


def test_occurred_at_string_is_preserved_verbatim_including_fractional_seconds():
    result = parse_callback(_body(occurredAt="2026-09-04T12:00:00.123456789Z"))

    assert result is not None
    assert result.occurred_at == "2026-09-04T12:00:00.123456789Z"


def test_malformed_json_is_rejected():
    assert parse_callback(b"not json") is None


def test_a_json_array_instead_of_an_object_is_rejected():
    assert parse_callback(b'["providerPaymentId"]') is None


def test_invalid_utf8_is_rejected():
    assert parse_callback(b"\xff\xfe") is None


@pytest.mark.parametrize("missing", list(VALID))
def test_a_missing_required_field_is_rejected(missing: str):
    payload = {key: value for key, value in VALID.items() if key != missing}

    assert parse_callback(json.dumps(payload).encode()) is None


def test_an_unknown_field_is_rejected():
    payload = dict(VALID, extra="surprise")

    assert parse_callback(json.dumps(payload).encode()) is None


@pytest.mark.parametrize("field", ["providerPaymentId", "operationId", "message", "occurredAt"])
def test_carriage_return_or_newline_in_a_transport_field_is_rejected(field: str):
    assert parse_callback(_body(**{field: "line1\r\nline2"})) is None


def test_an_invalid_result_value_is_rejected():
    assert parse_callback(_body(result="PENDING")) is None


def test_an_empty_provider_payment_id_is_rejected():
    assert parse_callback(_body(providerPaymentId="")) is None


def test_a_provider_payment_id_over_128_characters_is_rejected():
    assert parse_callback(_body(providerPaymentId="x" * 129)) is None


def test_a_message_over_500_characters_is_rejected():
    assert parse_callback(_body(message="x" * 501)) is None


def test_an_empty_message_is_accepted():
    result = parse_callback(_body(message=""))

    assert result is not None


def test_occurred_at_without_a_trailing_z_is_rejected():
    assert parse_callback(_body(occurredAt="2026-09-04T12:00:00+00:00")) is None


def test_occurred_at_with_garbage_is_rejected():
    assert parse_callback(_body(occurredAt="not-a-timestamp")) is None


def test_a_non_string_field_is_rejected():
    assert parse_callback(_body(providerPaymentId=123)) is None


# Ticket 18. Three concrete holes the week-3 review found, each one letting a
# malformed legacy callback either cross the anti-corruption boundary or blow
# up inside the adapter instead of being classified as HTTP 400.

_STRING_FIELDS = ["providerPaymentId", "operationId", "message", "occurredAt"]


@pytest.mark.parametrize("field", list(VALID))
@pytest.mark.parametrize("value", [{"a": 1}, ["x"], None, 123, True])
def test_a_field_of_the_wrong_type_is_rejected_without_raising(field: str, value: object):
    # `result` used to be membership-tested against a set before its type was
    # checked, so an object or array raised TypeError: unhashable type and the
    # adapter answered 500 instead of 400.
    assert parse_callback(_body(**{field: value})) is None


@pytest.mark.parametrize("field", _STRING_FIELDS)
@pytest.mark.parametrize("template", ["\r{}", "\n{}", "{}\r", "{}\n", "{}\r\n"])
def test_a_carriage_return_or_newline_anywhere_in_a_string_field_is_rejected(field: str, template: str):
    # A trailing LF used to pass: `$` matches before a final newline, and the
    # patterns were applied with re.match rather than re.fullmatch.
    assert parse_callback(_body(**{field: template.format(VALID[field])})) is None


@pytest.mark.parametrize(
    "occurred_at",
    [
        "2026-13-01T00:00:00Z",  # month 13
        "2026-02-30T00:00:00Z",  # February 30th
        "2026-00-10T00:00:00Z",  # month 0
        "2026-01-00T00:00:00Z",  # day 0
        "2026-01-01T25:00:00Z",  # hour 25
        "2026-01-01T00:60:00Z",  # minute 60
        "2026-01-01T00:00:61Z",  # second 61
        "2025-02-29T00:00:00Z",  # not a leap year
    ],
)
def test_a_calendar_impossible_occurred_at_is_rejected(occurred_at: str):
    assert parse_callback(_body(occurredAt=occurred_at)) is None


@pytest.mark.parametrize(
    "occurred_at",
    [
        "2026-09-04T12:00:00Z",
        "2026-09-04T12:00:00.1Z",
        "2026-09-04T12:00:00.123456789Z",
        "2024-02-29T23:59:59Z",  # a real leap day
        "2026-12-31T23:59:60Z",  # a leap second: RFC 3339 allows second 60
    ],
)
def test_a_real_date_is_accepted_and_its_string_survives_verbatim(occurred_at: str):
    result = parse_callback(_body(occurredAt=occurred_at))

    assert result is not None
    assert result.occurred_at == occurred_at
