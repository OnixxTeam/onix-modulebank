import httpx

from perimeter.classify import Retry, Success, Terminal
from perimeter.config import DispatcherConfig
from perimeter.db import ClaimedDelivery
from perimeter.dispatcher import attempt_delivery

CONFIG = DispatcherConfig(provider_url="http://provider-simulator:8081", owner="outbox-dispatcher", poll_interval_seconds=0.2, claim_limit=5)

DELIVERY = ClaimedDelivery(
    outbox_id="outbox-1",
    lease_version=1,
    external_request_id="ext-123",
    correlation_id="corr-1",
    amount="1000.00",
    currency="RUB",
)


def _client(handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler), follow_redirects=False)


def test_success_response_is_classified_and_the_request_matches_the_contract():
    captured: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(202, content=b'{"providerPaymentId":"provider-1","status":"ACCEPTED"}')

    with _client(handler) as client:
        result = attempt_delivery(client, CONFIG, DELIVERY)

    assert result == Success("provider-1")
    request = captured[0]
    assert request.url == "http://provider-simulator:8081/payments"
    assert request.headers["Idempotency-Key"] == "ext-123"
    assert request.headers["X-Correlation-ID"] == "corr-1"
    assert request.content == b'{"operationId":"ext-123","amount":"1000.00","currency":"RUB"}'


def test_retryable_status_is_classified_as_retry():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(503, content=b"")

    with _client(handler) as client:
        result = attempt_delivery(client, CONFIG, DELIVERY)

    assert result == Retry("http.503.retryable")


def test_terminal_status_is_classified_as_terminal():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(422, content=b"")

    with _client(handler) as client:
        result = attempt_delivery(client, CONFIG, DELIVERY)

    assert result == Terminal("http.422.terminal")


def test_a_connection_error_is_classified_as_a_retryable_transport_failure():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    with _client(handler) as client:
        result = attempt_delivery(client, CONFIG, DELIVERY)

    assert result == Retry("transport.error.retryable")


def test_a_read_timeout_is_classified_as_a_retryable_transport_failure():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("timed out", request=request)

    with _client(handler) as client:
        result = attempt_delivery(client, CONFIG, DELIVERY)

    assert result == Retry("transport.error.retryable")
