import json
import logging

import pytest

from perimeter.logs import REDACTED, JsonFormatter


def render(record: logging.LogRecord) -> dict:
    return json.loads(JsonFormatter().format(record))


def make_record(text: str = "delivery failed", **extra) -> logging.LogRecord:
    record = logging.LogRecord(
        name="perimeter.dispatcher", level=logging.INFO, pathname=__file__, lineno=1,
        msg=text, args=None, exc_info=None,
    )
    for key, value in extra.items():
        setattr(record, key, value)
    return record


def test_a_line_is_one_json_object_with_the_level_and_the_logger():
    line = render(make_record())

    assert line["level"] == "INFO"
    assert line["logger"] == "perimeter.dispatcher"
    assert line["message"] == "delivery failed"
    assert "timestamp" in line


def test_the_line_is_a_single_line():
    formatted = JsonFormatter().format(make_record("delivery\nfailed"))

    assert "\n" not in formatted


# The identifiers a step is diagnosed by. Unique values are allowed in logs, unlike
# in metric labels, because a log line is read by a person following one operation.
def test_extra_identifiers_reach_the_line():
    line = render(
        make_record(
            correlation_id="4a1c0f6e-2b7d-4c81-9a55-0d2f9c7b3e10",
            external_request_id="external-42",
            outbox_id="9f2b1c3d",
            error_code="transport.error.retryable",
            duration_ms=37,
        )
    )

    assert line["correlation_id"] == "4a1c0f6e-2b7d-4c81-9a55-0d2f9c7b3e10"
    assert line["external_request_id"] == "external-42"
    assert line["error_code"] == "transport.error.retryable"
    assert line["duration_ms"] == 37


@pytest.mark.parametrize(
    "key",
    ["authorization", "token", "callback_token", "hmac_secret", "signature", "password", "payload", "body", "receipt", "reason"],
)
def test_a_secret_bearing_field_is_redacted(key: str):
    line = render(make_record(**{key: "s3cret-or-payload"}))

    assert line[key] == REDACTED
    assert "s3cret-or-payload" not in json.dumps(line)


# The record's own text is the log call's choice and stays. A field *named* message
# passed through extra is a different thing -- a provider callback carries one, and
# the contract forbids it in logs -- and it never reaches the line at all: logging
# reserves that attribute name, so the formatter drops it, which is at least as safe
# as redacting it.
def test_the_records_own_message_survives_while_an_extra_message_field_never_appears():
    line = render(make_record("inbox reconciled", message="Payment declined by issuer"))

    assert line["message"] == "inbox reconciled"
    assert "Payment declined by issuer" not in json.dumps(line)


def test_an_exception_is_reported_by_type_and_text_only():
    try:
        raise ValueError("connection refused")
    except ValueError:
        import sys

        record = make_record("outbox claim failed")
        record.exc_info = sys.exc_info()
        line = render(record)

    assert line["error"] == "ValueError"
    assert line["error_message"] == "connection refused"
    assert "Traceback" not in json.dumps(line)
