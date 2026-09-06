"""Pure validation of one provider v0.2.0 legacy callback body.

Deliberately hand-rolled, not a Pydantic model: a model would hand back a
reserialized object, and any divergence in key order or number/string
formatting from the exact bytes the provider sent would not matter here
(the callback itself is never re-signed), but it *would* matter downstream if
this module ever grew into signing input -- keeping it this way from the
start means the adapter module never needs to reconsider it (spec.md
"Модель валидации фреймворка не должна стоять между сырым телом и подписью").

Mirrors contracts/course-1/provider-v02-callback.schema.json literally:
exactly the five declared fields, CR/LF rejected in every string transport
field, ``occurredAt`` validated by shape only and never reparsed into a time
object (04-week-3.md "исходная строка `occurredAt` сохраняется без
перепарсинга и переформатирования").
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass

_NO_CRLF_1_TO_128 = re.compile(r"^[^\r\n]{1,128}$")
_NO_CRLF_0_TO_500 = re.compile(r"^[^\r\n]{0,500}$")
_OCCURRED_AT = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?Z$")

_RESULTS = {"COMPLETED", "REJECTED"}
_REQUIRED_FIELDS = {"providerPaymentId", "operationId", "result", "message", "occurredAt"}


@dataclass(frozen=True)
class ValidCallback:
    provider_payment_id: str
    operation_id: str
    result: str
    occurred_at: str


def parse_callback(body: bytes) -> ValidCallback | None:
    """Validate and extract the four fields the receipt needs.

    ``message`` is intentionally not carried into the return value once it
    passes validation -- 04-week-3.md's own mapping table drops it, and
    external-contracts.md forbids free-text provider prose from reaching the
    platform at all.
    """
    try:
        data = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None

    # additionalProperties: false plus every field required -- the key set
    # must match exactly, not just be a superset or subset of it.
    if not isinstance(data, dict) or set(data.keys()) != _REQUIRED_FIELDS:
        return None

    provider_payment_id = data["providerPaymentId"]
    operation_id = data["operationId"]
    result = data["result"]
    message = data["message"]
    occurred_at = data["occurredAt"]

    if not isinstance(provider_payment_id, str) or not _NO_CRLF_1_TO_128.match(provider_payment_id):
        return None
    if not isinstance(operation_id, str) or not _NO_CRLF_1_TO_128.match(operation_id):
        return None
    if result not in _RESULTS:
        return None
    if not isinstance(message, str) or not _NO_CRLF_0_TO_500.match(message):
        return None
    if not isinstance(occurred_at, str) or not _OCCURRED_AT.match(occurred_at):
        return None

    return ValidCallback(
        provider_payment_id=provider_payment_id,
        operation_id=operation_id,
        result=result,
        occurred_at=occurred_at,
    )
