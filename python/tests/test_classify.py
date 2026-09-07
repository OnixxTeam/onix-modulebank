import pytest

from perimeter.classify import (
    RESPONSE_INVALID_TERMINAL,
    TRANSPORT_ERROR_RETRYABLE,
    Retry,
    Success,
    Terminal,
    classify_http_response,
    classify_transport_failure,
)


def test_202_with_the_exact_contract_body_is_success():
    result = classify_http_response(202, b'{"providerPaymentId":"provider-123","status":"ACCEPTED"}')

    assert result == Success("provider-123")


def test_202_with_an_extra_field_is_not_success():
    result = classify_http_response(202, b'{"providerPaymentId":"provider-123","status":"ACCEPTED","extra":"x"}')

    assert result == Terminal(RESPONSE_INVALID_TERMINAL)


def test_202_with_wrong_status_value_is_terminal():
    result = classify_http_response(202, b'{"providerPaymentId":"provider-123","status":"PENDING"}')

    assert result == Terminal(RESPONSE_INVALID_TERMINAL)


def test_202_with_empty_provider_payment_id_is_terminal():
    result = classify_http_response(202, b'{"providerPaymentId":"","status":"ACCEPTED"}')

    assert result == Terminal(RESPONSE_INVALID_TERMINAL)


def test_202_with_malformed_json_is_terminal():
    result = classify_http_response(202, b"not json")

    assert result == Terminal(RESPONSE_INVALID_TERMINAL)


def test_202_with_invalid_utf8_is_terminal():
    result = classify_http_response(202, b"\xff\xfe")

    assert result == Terminal(RESPONSE_INVALID_TERMINAL)


def test_202_with_a_json_array_instead_of_an_object_is_terminal():
    result = classify_http_response(202, b'["providerPaymentId","status"]')

    assert result == Terminal(RESPONSE_INVALID_TERMINAL)


@pytest.mark.parametrize("status", [408, 429, 500, 502, 503, 504])
def test_retryable_statuses(status: int):
    result = classify_http_response(status, b"")

    assert result == Retry(f"http.{status}.retryable")


@pytest.mark.parametrize("status", [400, 401, 403, 404, 409, 422, 301, 302])
def test_non_retryable_statuses_including_redirects_are_terminal(status: int):
    result = classify_http_response(status, b"")

    assert result == Terminal(f"http.{status}.terminal")


def test_transport_failure_is_retryable():
    assert classify_transport_failure() == Retry(TRANSPORT_ERROR_RETRYABLE)
