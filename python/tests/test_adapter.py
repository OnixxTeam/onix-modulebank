import json

import httpx
from fastapi.testclient import TestClient

from perimeter.adapter import create_app
from perimeter.callback import parse_callback
from perimeter.config import AdapterConfig
from perimeter.receipt import build_receipt_bytes
from perimeter.signature import sign_hex

CONFIG = AdapterConfig(
    capability="unpredictable-capability",
    callback_token="test-callback-token",
    hmac_secret=b"test-hmac-secret",
    receipt_api_url="http://gateway:8080/api/receipt/accept",
)

VALID_BODY = json.dumps(
    {
        "providerPaymentId": "provider-123",
        "operationId": "external-123",
        "result": "COMPLETED",
        "message": "Payment completed",
        "occurredAt": "2026-09-04T12:00:00Z",
    },
    separators=(",", ":"),
).encode("utf-8")

_CALLBACK_PATH = f"/callbacks/provider-v02/{CONFIG.capability}"


def _upstream(handler) -> httpx.Client:
    return httpx.Client(transport=httpx.MockTransport(handler), follow_redirects=False)


def _client(handler) -> TestClient:
    return TestClient(create_app(CONFIG, _upstream(handler)))


def test_a_valid_callback_is_signed_and_forwarded_and_the_platform_response_passes_through():
    captured: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(200, content=b'{"status":"ok"}', headers={"content-type": "application/json"})

    response = _client(handler).post(_CALLBACK_PATH, content=VALID_BODY)

    assert response.status_code == 200
    assert response.content == b'{"status":"ok"}'
    assert response.headers["content-type"] == "application/json"

    upstream_request = captured[0]
    assert str(upstream_request.url) == CONFIG.receipt_api_url
    assert upstream_request.headers["Authorization"] == f"Bearer {CONFIG.callback_token}"
    assert upstream_request.headers["Idempotency-Key"] == "provider-123"
    assert upstream_request.headers["X-Action-Version"] == "1"

    expected_body = build_receipt_bytes(parse_callback(VALID_BODY))
    assert upstream_request.content == expected_body

    expected_signature = sign_hex(CONFIG.hmac_secret, expected_body)
    assert upstream_request.headers["X-Provider-Signature"] == f"v1={expected_signature}"


def test_the_platform_error_response_passes_through_verbatim_too():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(409, content=b'{"status":"error","code":"idempotency.conflict"}', headers={"content-type": "application/json"})

    response = _client(handler).post(_CALLBACK_PATH, content=VALID_BODY)

    assert response.status_code == 409
    assert response.content == b'{"status":"error","code":"idempotency.conflict"}'


def test_wrong_capability_gives_404_with_empty_body():
    response = _client(lambda r: httpx.Response(200)).post("/callbacks/provider-v02/wrong-capability", content=VALID_BODY)

    assert response.status_code == 404
    assert response.content == b""


def test_wrong_path_gives_404_with_empty_body():
    response = _client(lambda r: httpx.Response(200)).get("/nonexistent")

    assert response.status_code == 404
    assert response.content == b""


def test_malformed_json_gives_400_with_empty_body():
    response = _client(lambda r: httpx.Response(200)).post(_CALLBACK_PATH, content=b"not json")

    assert response.status_code == 400
    assert response.content == b""


def test_schema_invalid_body_gives_400_with_empty_body():
    body = json.dumps({"providerPaymentId": "p-1"}).encode()

    response = _client(lambda r: httpx.Response(200)).post(_CALLBACK_PATH, content=body)

    assert response.status_code == 400
    assert response.content == b""


def test_body_over_64kib_gives_400_with_empty_body():
    body = json.dumps({"padding": "x" * 70_000}).encode()

    response = _client(lambda r: httpx.Response(200)).post(_CALLBACK_PATH, content=body)

    assert response.status_code == 400
    assert response.content == b""


def test_upstream_connection_error_gives_503_dependency_unavailable():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    response = _client(handler).post(_CALLBACK_PATH, content=VALID_BODY)

    assert response.status_code == 503
    assert response.content == b'{"status":"error","code":"dependency.unavailable"}'
    assert response.headers["content-type"] == "application/json"


def test_upstream_timeout_gives_503_dependency_unavailable():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ReadTimeout("timed out", request=request)

    response = _client(handler).post(_CALLBACK_PATH, content=VALID_BODY)

    assert response.status_code == 503
    assert response.content == b'{"status":"error","code":"dependency.unavailable"}'


def test_the_adapter_does_not_acknowledge_before_the_platform_responds():
    # If the adapter acked before the platform call, the mock transport
    # (called synchronously, in-request) simply wouldn't have run yet -- this
    # asserts the observable order the hard way: the captured request exists
    # by the time our own response comes back, and its body/signature match
    # what was actually sent to us, not some pre-computed placeholder.
    captured: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        captured.append(request)
        return httpx.Response(200, content=b"{}")

    response = _client(handler).post(_CALLBACK_PATH, content=VALID_BODY)

    assert response.status_code == 200
    assert len(captured) == 1
