"""Pure validation of one provider v0.2.0 legacy callback body.

Deliberately hand-rolled, not a Pydantic model: a model would hand back a
reserialized object, and any divergence in key order or number/string
formatting from the exact bytes the provider sent would not matter here
(the callback itself is never re-signed), but it *would* matter downstream if
this module ever grew into signing input -- keeping it this way from the
start means the adapter module never needs to reconsider it (spec.md
"Модель валидации фреймворка не должна стоять между сырым телом и подписью").

Mirrors contracts/course-1/provider-v02-callback.schema.json literally:
exactly the five declared fields, CR/LF rejected anywhere in every string
transport field, and ``occurredAt`` checked as a real RFC 3339 instant rather
than by shape alone. The string itself is still carried out verbatim --
04-week-3.md forbids *reformatting* it, not validating it, and a receipt built
from an impossible calendar date is exactly what the anti-corruption boundary
exists to stop.
"""

from __future__ import annotations

import datetime as dt
import json
import re
from dataclasses import dataclass

# ``fullmatch`` throughout, and \A/\Z rather than ^/$: Python's ``$`` also
# matches immediately before a trailing newline, so "value\n" passed a
# ``^[^\r\n]{1,128}$`` check that exists precisely to reject it.
_NO_CRLF_1_TO_128 = re.compile(r"\A[^\r\n]{1,128}\Z")
_NO_CRLF_0_TO_500 = re.compile(r"\A[^\r\n]{0,500}\Z")
_OCCURRED_AT = re.compile(
    r"\A(?P<date>\d{4}-\d{2}-\d{2})T(?P<hour>\d{2}):(?P<minute>\d{2}):(?P<second>\d{2})(\.\d{1,9})?Z\Z"
)

_RESULTS = {"COMPLETED", "REJECTED"}
_REQUIRED_FIELDS = {"providerPaymentId", "operationId", "result", "message", "occurredAt"}


def _is_a_real_instant(occurred_at: str) -> bool:
    """Shape *and* calendar: the regex alone accepts 2026-13-45T99:99:99Z."""
    match = _OCCURRED_AT.fullmatch(occurred_at)
    if match is None:
        return False

    second = int(match["second"])
    if second > 60:
        return False

    # RFC 3339 (and so the contract's "format": "date-time") allows :60 for a
    # leap second, which datetime cannot represent; the rest of the stamp is
    # what needs checking, so the calendar check runs against :59 instead.
    try:
        dt.datetime.strptime(
            f"{match['date']}T{match['hour']}:{match['minute']}:{min(second, 59):02d}",
            "%Y-%m-%dT%H:%M:%S",
        )
    except ValueError:
        return False

    return True


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

    # Type first, value second, in every case. ``result not in _RESULTS`` on
    # its own raises TypeError: unhashable type for an object or an array,
    # which the adapter could only turn into HTTP 500 -- a malformed body has
    # to be classified, not to crash the perimeter.
    if not isinstance(provider_payment_id, str) or not _NO_CRLF_1_TO_128.fullmatch(provider_payment_id):
        return None
    if not isinstance(operation_id, str) or not _NO_CRLF_1_TO_128.fullmatch(operation_id):
        return None
    if not isinstance(result, str) or result not in _RESULTS:
        return None
    if not isinstance(message, str) or not _NO_CRLF_0_TO_500.fullmatch(message):
        return None
    if not isinstance(occurred_at, str) or not _is_a_real_instant(occurred_at):
        return None

    return ValidCallback(
        provider_payment_id=provider_payment_id,
        operation_id=operation_id,
        result=result,
        occurred_at=occurred_at,
    )
