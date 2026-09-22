from perimeter.request import build_provider_request


def test_body_matches_the_contract_key_order_and_is_compact():
    request = build_provider_request("ext-123", "00000000-0000-0000-0000-000000000000", "1000.00", "RUB")

    assert request.body == b'{"operationId":"ext-123","amount":"1000.00","currency":"RUB"}'


def test_idempotency_key_is_the_external_request_id():
    request = build_provider_request("ext-123", "corr-1", "1000.00", "RUB")

    assert request.idempotency_key == "ext-123"


def test_correlation_id_passes_through_unchanged():
    request = build_provider_request("ext-123", "corr-1", "1000.00", "RUB")

    assert request.correlation_id == "corr-1"


def test_repeated_calls_with_the_same_inputs_produce_byte_identical_bodies():
    # The dispatcher never persists a built body -- every retry calls this
    # again with the claimed row's own fields. Determinism here is what makes
    # that safe (docs/week3/04-week-3.md "Каждый повтор одной доставки
    # использует те же тело, ключ и корреляцию").
    first = build_provider_request("ext-123", "corr-1", "1000.00", "RUB")
    second = build_provider_request("ext-123", "corr-1", "1000.00", "RUB")

    assert first.body == second.body
