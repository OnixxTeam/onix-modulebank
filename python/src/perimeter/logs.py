"""One JSON object per log line, with the identifiers a step is diagnosed by.

The contract asks for two things at once: every application log line is a separate
JSON object carrying the applicable identifiers, action, outcome, duration and safe
error code -- and none of them carries a JWT, an HMAC secret or signature, a database
password, a full payment payload, a receipt body, a provider callback message or a
manual decision's reason.

Unique identifiers *are* allowed here, unlike in metric labels: a log line is read by
a person following one operation, not by a time series database.
"""

from __future__ import annotations

import datetime as _datetime
import json
import logging
import re

# Keys whose value never belongs in a log line, whatever a caller passes. Matched on
# the key rather than the value, because the value is exactly what must not be seen:
# a redaction that looked for secret-shaped strings would need the secret to compare
# against, and would still miss a payload.
REDACTED_KEYS = frozenset(
    {
        "authorization",
        "token",
        "callback_token",
        "jwt",
        "secret",
        "hmac_secret",
        "signature",
        "password",
        "pgpassword",
        "payload",
        "body",
        "receipt",
        "receipt_body",
        "reason",
    }
)

REDACTED = "[redacted]"

_RESERVED = frozenset(
    vars(logging.LogRecord("", 0, "", 0, "", None, None)).keys()
) | {"message", "asctime", "taskName"}


# Credentials quoted inside a free-text message, which redaction by key cannot
# reach: a libpq error repeats the connection string it failed on.
_CREDENTIAL_IN_TEXT = re.compile(
    r"(?i)\b(password|pgpassword|authorization|token|secret)\s*[=:]\s*\S+"
)


def scrub(text: str) -> str:
    """Replaces credential-looking assignments inside free text."""
    return _CREDENTIAL_IN_TEXT.sub(lambda match: f"{match.group(1)}={REDACTED}", text)


def _safe(key: str, value: object) -> object:
    if key.lower() in REDACTED_KEYS:
        return REDACTED
    return scrub(value) if isinstance(value, str) else value


class JsonFormatter(logging.Formatter):
    """Renders a record as one JSON object, extras included and secrets left out.

    ``message`` is the record's own text, which the log call decides. A field of that
    name passed through ``extra`` -- a provider callback carries one, and the contract
    forbids it in logs -- never reaches the line at all: ``logging`` reserves the
    attribute, so it is skipped here and rejected outright by ``Logger.makeRecord``.
    """

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "timestamp": _datetime.datetime.fromtimestamp(
                record.created, tz=_datetime.timezone.utc
            ).isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "message": scrub(record.getMessage()),
        }

        for key, value in vars(record).items():
            if key in _RESERVED:
                continue
            payload[key] = _safe(key, value)

        if record.exc_info:
            # Driver exception text may contain a complete connection string.
            # Keep only the type; the caller supplies a safe error code.
            exception = record.exc_info[1]
            payload["error"] = type(exception).__name__ if exception else "unknown"

        return json.dumps(payload, ensure_ascii=False, default=str)


def configure(level: int = logging.INFO) -> None:
    """Installs the formatter as the process's only handler.

    Called by every perimeter entrypoint in place of logging.basicConfig, which would
    otherwise emit plain text and drop every extra field on the floor.
    """
    handler = logging.StreamHandler()
    handler.setFormatter(JsonFormatter())

    root = logging.getLogger()
    root.handlers.clear()
    root.addHandler(handler)
    root.setLevel(level)

    # uvicorn installs its own handlers when it starts; letting them propagate to the
    # root handler above is what keeps the observability server's lines JSON too.
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        server_logger = logging.getLogger(name)
        server_logger.handlers.clear()
        server_logger.propagate = True
