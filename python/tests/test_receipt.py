from perimeter.callback import ValidCallback
from perimeter.receipt import build_receipt_bytes

CALLBACK = ValidCallback(
    provider_payment_id="provider-123",
    operation_id="external-123",
    result="COMPLETED",
    occurred_at="2026-09-04T12:00:00Z",
)


def test_receipt_bytes_match_the_contract_literally():
    body = build_receipt_bytes(CALLBACK)

    assert body == (
        b'{"externalRequestId":"external-123","messageId":"provider-123",'
        b'"occurredAt":"2026-09-04T12:00:00Z","outcome":"COMPLETED",'
        b'"providerPaymentId":"provider-123","version":1}'
    )


def test_no_trailing_newline_or_bom():
    body = build_receipt_bytes(CALLBACK)

    assert not body.startswith(b"\xef\xbb\xbf")
    assert not body.endswith(b"\n")


def test_message_id_equals_provider_payment_id():
    body = build_receipt_bytes(CALLBACK)

    assert b'"messageId":"provider-123"' in body
    assert b'"providerPaymentId":"provider-123"' in body


def test_repeated_calls_produce_byte_identical_output():
    first = build_receipt_bytes(CALLBACK)
    second = build_receipt_bytes(CALLBACK)

    assert first == second


def test_rejected_outcome_passes_through():
    callback = ValidCallback(
        provider_payment_id="provider-999",
        operation_id="external-999",
        result="REJECTED",
        occurred_at="2026-09-04T12:00:00Z",
    )

    body = build_receipt_bytes(callback)

    assert b'"outcome":"REJECTED"' in body
