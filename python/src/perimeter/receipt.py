"""Pure construction of the outbound receipt v1 body.

The anti-corruption mapping itself (04-week-3.md's table): ``providerPaymentId``
becomes both ``messageId`` and stays ``providerPaymentId``, ``operationId``
becomes ``externalRequestId``, ``result`` becomes ``outcome``, ``occurredAt``
passes through as the exact same string. Serialization matches the spec's own
literal recipe -- compact, sorted keys, no BOM, no trailing newline -- so the
HMAC computed over these bytes (perimeter.signature) verifies on the C# side,
which recomputes the same canonical form independently.
"""

from __future__ import annotations

import json

from perimeter.callback import ValidCallback


def build_receipt_bytes(callback: ValidCallback) -> bytes:
    receipt = {
        "externalRequestId": callback.operation_id,
        "messageId": callback.provider_payment_id,
        "occurredAt": callback.occurred_at,
        "outcome": callback.result,
        "providerPaymentId": callback.provider_payment_id,
        "version": 1,
    }
    return json.dumps(receipt, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")
