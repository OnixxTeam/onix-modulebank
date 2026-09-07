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
