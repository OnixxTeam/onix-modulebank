"""Pure classification of one provider HTTP attempt.

Cut out from the transport layer on purpose (spec.md "Каждый цикл режется
на чистую функцию решения и тонкую оболочку ввода-вывода... без такого
разреза проверка логики повторов и классификации ответов требует фейков
базы и сети, то есть четвёртого шва"). Nothing here does I/O; the dispatcher
module is the only thing that ever calls httpx or psycopg.

Error codes follow docs/week3/external-contracts.md's own naming convention
literally: a ``*.retryable`` or ``*.terminal`` suffix is the only thing
``delivery.fail_outbox`` reads to decide whether another attempt is spent --
this module's job stops at picking the right suffix and status number, never
retry counts or delays (PostgreSQL's own job, ticket 09).
"""

from __future__ import annotations

import json
from dataclasses import dataclass

TRANSPORT_ERROR_RETRYABLE = "transport.error.retryable"
RESPONSE_INVALID_TERMINAL = "response.invalid.terminal"

_RETRYABLE_STATUSES = {408, 429}


@dataclass(frozen=True)
class Success:
    provider_payment_id: str


@dataclass(frozen=True)
class Retry:
    error_code: str


@dataclass(frozen=True)
class Terminal:
    error_code: str


ClassificationResult = Success | Retry | Terminal


def classify_transport_failure() -> Retry:
    """A connection error or a timeout -- the request never got a response at all."""
    return Retry(TRANSPORT_ERROR_RETRYABLE)


def classify_http_response(status_code: int, body: bytes) -> ClassificationResult:
    if status_code == 202:
        return _classify_accepted_body(body)

    if status_code in _RETRYABLE_STATUSES or status_code >= 500:
        return Retry(f"http.{status_code}.retryable")

    # Every other status -- including redirects, which are never treated as
    # success (external-contracts.md "Redirect не считается успешным `202`")
    # -- is non-retryable.
    return Terminal(f"http.{status_code}.terminal")


def _classify_accepted_body(body: bytes) -> ClassificationResult:
    try:
        text = body.decode("utf-8")
        parsed = json.loads(text)
    except (UnicodeDecodeError, json.JSONDecodeError):
        return Terminal(RESPONSE_INVALID_TERMINAL)

    if not isinstance(parsed, dict):
        return Terminal(RESPONSE_INVALID_TERMINAL)

    # Strict: exactly the two declared fields, nothing else (external-contracts.md
    # "строгое тело без дополнительных полей" -- an extra field is not a success).
    if set(parsed.keys()) != {"providerPaymentId", "status"}:
        return Terminal(RESPONSE_INVALID_TERMINAL)

    provider_payment_id = parsed.get("providerPaymentId")
    status = parsed.get("status")

    if not isinstance(provider_payment_id, str) or not provider_payment_id:
        return Terminal(RESPONSE_INVALID_TERMINAL)

    if status != "ACCEPTED":
        return Terminal(RESPONSE_INVALID_TERMINAL)

    return Success(provider_payment_id)
