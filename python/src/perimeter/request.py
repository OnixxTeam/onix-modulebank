"""Pure construction of the outbound provider request.

The whole point of a pure function here (spec.md "Тело запроса нельзя
пересобирать на каждой попытке из полей: любое расхождение в порядке ключей
или форматировании числа даст другой байтовый след") is that every retry of
one delivery calls this with the exact same inputs (the claimed row's own
``external_request_id``/``amount``/``currency``, never recomputed) and gets
back the exact same bytes -- key order and number formatting are fixed by
construction, not by discipline.
"""

from __future__ import annotations

import json
from dataclasses import dataclass


@dataclass(frozen=True)
class ProviderRequest:
    body: bytes
    idempotency_key: str
    correlation_id: str


def build_provider_request(external_request_id: str, correlation_id: str, amount: str, currency: str) -> ProviderRequest:
    # Key order matches docs/week3/04-week-3.md's own literal example
    # byte-for-byte: operationId, amount, currency. amount/currency are
    # already contract-format strings from delivery.claim_outbox -- this
    # function never parses or reformats them.
    payload = {"operationId": external_request_id, "amount": amount, "currency": currency}
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")

    return ProviderRequest(body=body, idempotency_key=external_request_id, correlation_id=correlation_id)
