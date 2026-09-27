"""Ticket 13: the dispatcher's two failpoints and the gate's own rules."""

from __future__ import annotations

import io
import json
import threading

import httpx
import pytest

from perimeter import db
from perimeter.config import DispatcherConfig
from perimeter.dispatcher import process_claim
from perimeter.failpoints import (
    AFTER_OUTBOX_CLAIM,
    AFTER_PROVIDER_RESPONSE,
    NAMES,
    Failpoint,
    from_environment,
)

CONFIG = DispatcherConfig(
    provider_url="http://provider",
    owner="outbox-dispatcher",
    claim_limit=10,
    poll_interval_seconds=0.05,
    provider_timeout_seconds=0.5,
    max_attempts=4,
    lease_seconds=2.0,
)

DELIVERY = db.ClaimedDelivery(
    outbox_id="11111111-1111-1111-1111-111111111111",
    external_request_id="ext-1",
    correlation_id="22222222-2222-2222-2222-222222222222",
    amount="1000.00",
    currency="RUB",
    lease_version=1,
)


def test_the_six_published_names_are_the_ones_the_gate_knows() -> None:
    assert NAMES == (
        "after_job_claim",
        "after_action_before_finish",
        "after_outbox_claim",
        "after_provider_response",
        "after_inbox_saved",
        "after_manual_decision",
    )


@pytest.mark.parametrize("armed", [None, "", "after_everything_explodes", "AFTER_OUTBOX_CLAIM"])
def test_an_unknown_or_missing_name_disarms_every_point(armed: str | None) -> None:
    output = io.StringIO()
    failpoint = Failpoint(armed, "outbox-dispatcher", output)

    assert failpoint.armed is None
    # Every one of these would block forever if it were armed.
    for name in NAMES:
        failpoint.reach(name)

    assert output.getvalue() == ""


def test_only_the_one_armed_name_writes_an_acknowledgement() -> None:
    output = io.StringIO()
    failpoint = Failpoint(AFTER_OUTBOX_CLAIM, "outbox-dispatcher", output)

    for name in NAMES:
        if name != AFTER_OUTBOX_CLAIM:
            failpoint.reach(name)
    assert output.getvalue() == ""

    reached = threading.Thread(target=failpoint.reach, args=(AFTER_OUTBOX_CLAIM,), daemon=True)
    reached.start()
    _wait_for_ack(output)
    # Still blocked: the point returns only when the process is killed, which the
    # test stands in for with release().
    assert reached.is_alive()
    failpoint.release()
    reached.join(timeout=5)
    assert not reached.is_alive()

    ack = json.loads(output.getvalue().strip())
    assert ack == {
        "event": "failpoint.reached",
        "name": "after_outbox_claim",
        "instanceId": "outbox-dispatcher",
    }


@pytest.mark.parametrize("profile", [None, "", "0", "true"])
def test_outside_the_test_profile_nothing_is_armed(monkeypatch: pytest.MonkeyPatch, profile: str | None) -> None:
    if profile is None:
        monkeypatch.delenv("COURSE_TEST_PROFILE", raising=False)
    else:
        monkeypatch.setenv("COURSE_TEST_PROFILE", profile)
    monkeypatch.setenv("COURSE_FAILPOINT", AFTER_OUTBOX_CLAIM)

    assert from_environment("outbox-dispatcher").armed is None


def test_inside_the_test_profile_the_named_point_is_armed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("COURSE_TEST_PROFILE", "1")
    monkeypatch.setenv("COURSE_FAILPOINT", AFTER_PROVIDER_RESPONSE)

    assert from_environment("outbox-dispatcher").armed == AFTER_PROVIDER_RESPONSE


def test_after_outbox_claim_blocks_before_the_provider_is_called() -> None:
    output = io.StringIO()
    failpoint = Failpoint(AFTER_OUTBOX_CLAIM, "outbox-dispatcher", output)
    calls: list[str] = []
    client = httpx.Client(transport=_recording_transport(calls))
    connection = _RecordingConnection()

    worker = threading.Thread(
        target=process_claim, args=(connection, client, CONFIG, DELIVERY, failpoint), daemon=True)
    worker.start()
    _wait_for_ack(output)

    # The lease is already durable -- claim_outbox committed before process_claim
    # was called -- and nothing has left the process yet.
    assert calls == [], "the provider was called before the failpoint blocked"
    assert connection.statements == []
    assert worker.is_alive()
    failpoint.release()
    worker.join(timeout=5)

    # And once released it goes on to do exactly what it would have done.
    assert calls == ["http://provider/payments"]
    assert len(connection.statements) == 1


def test_after_provider_response_blocks_before_the_delivery_is_completed() -> None:
    output = io.StringIO()
    failpoint = Failpoint(AFTER_PROVIDER_RESPONSE, "outbox-dispatcher", output)
    calls: list[str] = []
    client = httpx.Client(transport=_recording_transport(calls))
    connection = _RecordingConnection()

    worker = threading.Thread(
        target=process_claim, args=(connection, client, CONFIG, DELIVERY, failpoint), daemon=True)
    worker.start()
    _wait_for_ack(output)

    assert calls == ["http://provider/payments"], "the provider was not called before the failpoint"
    assert connection.statements == [], "the delivery was completed before the failpoint blocked"
    assert worker.is_alive()
    failpoint.release()
    worker.join(timeout=5)

    assert len(connection.statements) == 1


def test_a_claim_without_a_failpoint_needs_no_gate() -> None:
    calls: list[str] = []
    client = httpx.Client(transport=_recording_transport(calls))
    connection = _RecordingConnection()

    process_claim(connection, client, CONFIG, DELIVERY)

    assert calls == ["http://provider/payments"]
    assert len(connection.statements) == 1


def _recording_transport(calls: list[str]) -> httpx.MockTransport:
    def handler(request: httpx.Request) -> httpx.Response:
        calls.append(str(request.url))
        return httpx.Response(202, content=b'{"providerPaymentId":"provider-1","status":"ACCEPTED"}')

    return httpx.MockTransport(handler)


def _wait_for_ack(output: io.StringIO, timeout: float = 5.0) -> None:
    deadline = threading.Event()
    waited = 0.0
    while waited < timeout:
        if "failpoint.reached" in output.getvalue():
            return
        deadline.wait(0.02)
        waited += 0.02
    raise AssertionError("the failpoint never acknowledged")


class _RecordingConnection:
    def __init__(self) -> None:
        self.statements: list[str] = []

    def cursor(self) -> _RecordingCursor:
        return _RecordingCursor(self.statements)


class _RecordingCursor:
    def __init__(self, statements: list[str]) -> None:
        self._statements = statements

    def __enter__(self) -> _RecordingCursor:
        return self

    def __exit__(self, *exc: object) -> None:
        return None

    def execute(self, statement: str, *args: object) -> None:
        self._statements.append(statement)

    def fetchone(self) -> tuple[object, ...]:
        return ({"status": "ok"},)
