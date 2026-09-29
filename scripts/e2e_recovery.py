#!/usr/bin/env python3
"""Exercise all six Week 4 failpoint recovery boundaries on a clean Compose stack.

The runner waits for each target container's ``failpoint.reached`` record before
forcing it down.  It never changes PostgreSQL state: assertions read only the
published ``autocheck`` projections and recovery is performed by the ordinary
workers, dispatchers and reconcilers.

Usage: python3 scripts/e2e_recovery.py [--keep]
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import json
import os
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
GATEWAY = f"http://127.0.0.1:{os.environ.get('COURSE_GATEWAY_PORT', '8080')}"
KEY = "dev-insecure-key-not-for-production-32"
TIMEOUT = 35.0
POLL = 0.2


class RecoveryFailure(RuntimeError):
    pass


def b64url(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def token(subject: str, consumer: str, scopes: list[str]) -> str:
    now = int(time.time())
    header = b64url(b'{"alg":"HS256","typ":"JWT"}')
    payload = b64url(json.dumps({"iss": "moduledev-course", "aud": "moduledev-api", "sub": subject,
        "consumer": consumer, "scope": " ".join(scopes), "iat": now - 5, "exp": now + 3600},
        separators=(",", ":")).encode())
    signature = b64url(hmac.new(KEY.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest())
    return f"{header}.{payload}.{signature}"


def run(*command: str, timeout: int = 120, check: bool = True, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=timeout,
        env={**os.environ, **(env or {})})
    if check and result.returncode:
        raise RecoveryFailure(f"{' '.join(command)} failed: {result.stderr.strip() or result.stdout.strip()}")
    return result


def compose(*args: str, timeout: int = 120, check: bool = True, override: Path | None = None,
            env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    prefix = ["docker", "compose"]
    if override:
        prefix += ["-f", "compose.yaml", "-f", str(override)]
    return run(*prefix, *args, timeout=timeout, check=check, env=env)


def wait_for(description: str, predicate) -> None:
    deadline = time.monotonic() + TIMEOUT
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(POLL)
    raise RecoveryFailure(f"timed out waiting for {description}")


def request(module: str, action: str, payload: dict[str, object], bearer: str, key: str,
            timeout: float = 10) -> dict[str, object]:
    body = json.dumps(payload, separators=(",", ":")).encode()
    call = urllib.request.Request(f"{GATEWAY}/api/{module}/{action}", data=body, method="POST", headers={
        "Authorization": f"Bearer {bearer}", "Content-Type": "application/json",
        "X-Action-Version": "1", "Idempotency-Key": key,
    })
    try:
        with urllib.request.urlopen(call, timeout=timeout) as response:
            return json.loads(response.read())
    except (urllib.error.HTTPError, OSError) as error:
        raise RecoveryFailure(f"{module}.{action} request failed: {error}") from error


def rows(query: str) -> list[dict[str, object]]:
    wrapped = f"SELECT COALESCE(jsonb_agg(to_jsonb(q)), '[]'::jsonb)::text FROM ({query}) AS q"
    result = compose("exec", "-T", "postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
        "-d", "course", "-At", "-c", wrapped)
    return json.loads(result.stdout.strip())


def service_id(service: str) -> str:
    result = compose("ps", "-q", service)
    identifier = result.stdout.strip()
    if not identifier:
        raise RecoveryFailure(f"{service} has no running container")
    return identifier


def arm(service: str, name: str, base_env: dict[str, str]) -> Path:
    override = Path(tempfile.mkstemp(prefix="week4-failpoint-", suffix=".yaml")[1])
    override.write_text(f"services:\n  {service}:\n    environment:\n      COURSE_TEST_PROFILE: '1'\n      COURSE_FAILPOINT: {name}\n")
    compose("up", "-d", "--no-deps", "--force-recreate", service, override=override, env=base_env)
    if service == "api":
        # A failed connection is not evidence that the pre/post-commit point was
        # reached.  Wait for gateway-to-api routing before sending its trigger.
        wait_for("recreated api", lambda: run("curl", "-fsS", f"{GATEWAY}/health/ready", check=False).returncode == 0)
    return override


def disarm(service: str, override: Path, base_env: dict[str, str]) -> None:
    try:
        compose("kill", service, check=False, env=base_env)
        compose("up", "-d", "--no-deps", "--force-recreate", service, env=base_env)
        if service == "api":
            wait_for("recovered api", lambda: run("curl", "-fsS", f"{GATEWAY}/health/ready", check=False).returncode == 0)
    finally:
        override.unlink(missing_ok=True)


def wait_for_ack(service: str, name: str) -> None:
    identifier = service_id(service)

    def acknowledged() -> bool:
        result = run("docker", "logs", identifier, check=False)
        for line in result.stdout.splitlines():
            try:
                event = json.loads(line)
            except json.JSONDecodeError:
                continue
            if event.get("event") == "failpoint.reached" and event.get("name") == name and event.get("instanceId"):
                return True
        return False

    wait_for(f"{service} acknowledgement for {name}", acknowledged)


def wait_operation(operation_id: str, state: str = "COMPLETED") -> None:
    wait_for(f"operation {operation_id} {state}", lambda: bool(rows(
        f"SELECT status FROM autocheck.operations WHERE operation_id = '{operation_id}'::uuid AND status = '{state}'")))


def submit(client: str, label: str, kind: str, amount: str) -> tuple[str, str]:
    created = request("payment", "request", {"operationKind": kind, "amount": amount, "currency": "RUB"},
        client, f"recovery-request-{label}-{secrets.token_hex(4)}")
    operation_id = created["result"]["operationId"]
    started = request("payment", "submit", {"operationId": operation_id}, client,
        f"recovery-submit-{label}-{secrets.token_hex(4)}")
    return operation_id, started["result"]["processId"]


def stop(*services: str) -> None:
    compose("stop", *services)


def start(*services: str, base_env: dict[str, str]) -> None:
    compose("up", "-d", "--no-deps", *services, env=base_env)


def worker_case(name: str, client: str, base_env: dict[str, str]) -> None:
    stop("worker-b")
    override = arm("worker-a", name, base_env)
    try:
        operation_id, _ = submit(client, name, "PAYMENT_APPROVAL", "100000.00")
        wait_for_ack("worker-a", name)
    finally:
        disarm("worker-a", override, base_env)
        start("worker-b", base_env=base_env)
    wait_operation(operation_id)


def dispatcher_case(name: str, client: str, base_env: dict[str, str]) -> None:
    stop("outbox-dispatcher-b")
    override = arm("outbox-dispatcher", name, base_env)
    try:
        operation_id, _ = submit(client, name, "PAYMENT_EXECUTION", "1000.00")
        wait_for_ack("outbox-dispatcher", name)
    finally:
        disarm("outbox-dispatcher", override, base_env)
        start("outbox-dispatcher-b", base_env=base_env)
    wait_operation(operation_id)


def manual_process(client: str, label: str) -> tuple[str, str, str]:
    operation_id, process_id = submit(client, label, "PAYMENT_APPROVAL", "100000.01")
    wait_for(f"manual process {process_id}", lambda: bool(rows(
        f"SELECT 1 FROM autocheck.processes WHERE process_id = '{process_id}'::uuid AND state = 'WAITING_MANUAL'")))
    steps = rows("SELECT step_instance_id FROM autocheck.steps "
        f"WHERE process_id = '{process_id}'::uuid AND step_type = 'MANUAL' AND state = 'WAITING'")
    if len(steps) != 1:
        raise RecoveryFailure(f"expected one waiting manual step for {process_id}, got {len(steps)}")
    return operation_id, process_id, str(steps[0]["step_instance_id"])


def manual_case(client: str, reviewer: str, base_env: dict[str, str]) -> None:
    operation_id, process_id, step_id = manual_process(client, "after-manual")
    key = f"recovery-manual-{secrets.token_hex(4)}"
    override = arm("api", "after_manual_decision", base_env)
    try:
        try:
            request("workflow", "manual", {"processId": process_id, "stepInstanceId": step_id,
                "decision": "APPROVED", "reason": "recovery check"}, reviewer, key, timeout=3)
        except RecoveryFailure:
            pass  # The request blocks at the acknowledgement boundary until api is killed.
        wait_for_ack("api", "after_manual_decision")
    finally:
        disarm("api", override, base_env)
    request("workflow", "manual", {"processId": process_id, "stepInstanceId": step_id,
        "decision": "APPROVED", "reason": "recovery check"}, reviewer, key)
    wait_operation(operation_id)
    decisions = rows(f"SELECT decision_id FROM autocheck.decisions WHERE process_id = '{process_id}'::uuid")
    if len(decisions) != 1:
        raise RecoveryFailure(f"manual recovery created {len(decisions)} decisions, expected one")


def callback_case(client: str, base_env: dict[str, str]) -> None:
    # Stop normal reconciliation before the receipt is accepted: Inbox durability is
    # the evidence at this post-commit boundary, not a race with signal application.
    stop("inbox-reconciler", "inbox-reconciler-b", "outbox-dispatcher", "outbox-dispatcher-b")
    operation_id, _ = submit(client, "after-inbox", "PAYMENT_EXECUTION", "1000.00")
    external = rows(f"SELECT external_request_id FROM autocheck.external_requests WHERE operation_id = '{operation_id}'::uuid")
    if len(external) != 1:
        raise RecoveryFailure("payment did not create exactly one external request")
    external_id = str(external[0]["external_request_id"])
    override = arm("api", "after_inbox_saved", base_env)
    message_id = f"recovery-{secrets.token_hex(8)}"
    try:
        callback = json.dumps({"providerPaymentId": message_id, "operationId": external_id, "result": "COMPLETED",
            "message": "accepted", "occurredAt": "2026-09-29T00:00:00Z"}, separators=(",", ":"))
        # The adapter owns legacy-to-receipt conversion and signing; invoking it
        # in its own container keeps this test on the public callback boundary.
        code = "import sys,urllib.request; b=sys.argv[2].encode(); r=urllib.request.Request('http://127.0.0.1:8082/callbacks/provider-v02/'+sys.argv[1],data=b,method='POST',headers={'Content-Type':'application/json'}); urllib.request.urlopen(r,timeout=10).read()"
        try:
            run("docker", "compose", "exec", "-T", "receipt-adapter", "python3", "-c", code,
                os.environ["PROVIDER_CALLBACK_CAPABILITY"], callback, timeout=3, check=False, env=base_env)
        except subprocess.TimeoutExpired:
            # The adapter waits for api's response, which is intentionally held at
            # the failpoint.  The acknowledgement below is the real barrier.
            pass
        wait_for_ack("api", "after_inbox_saved")
    finally:
        disarm("api", override, base_env)
        start("inbox-reconciler", "inbox-reconciler-b", "outbox-dispatcher", "outbox-dispatcher-b", base_env=base_env)
    wait_for(f"durable inbox receipt {message_id}", lambda: bool(rows(
        f"SELECT 1 FROM autocheck.inbox WHERE message_id = '{message_id}'")))
    wait_operation(operation_id)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--keep", action="store_true", help="leave the stack running after a successful run")
    args = parser.parse_args()
    capability = secrets.token_hex(16)
    base_env = {"COURSE_TEST_PROFILE": "1", "PROVIDER_CALLBACK_CAPABILITY": capability,
        "PROVIDER_CALLBACK_TOKEN": token("receipt-provider", "receipt-adapter", ["receipt:write"])}
    os.environ["PROVIDER_CALLBACK_CAPABILITY"] = capability
    client = token("recovery-client", "e2e", ["payment:write", "payment:read", "workflow:read"])
    reviewer = token("recovery-reviewer", "e2e", ["workflow:manual", "payment:read"])
    try:
        compose("up", "-d", "--build", timeout=600, env=base_env)
        wait_for("gateway", lambda: run("curl", "-fsS", f"{GATEWAY}/health/live", check=False).returncode == 0)
        for name in ("after_job_claim", "after_action_before_finish"):
            print(f"checking {name}...")
            worker_case(name, client, base_env)
        for name in ("after_outbox_claim", "after_provider_response"):
            print(f"checking {name}...")
            dispatcher_case(name, client, base_env)
        print("checking after_manual_decision...")
        manual_case(client, reviewer, base_env)
        print("checking after_inbox_saved...")
        callback_case(client, base_env)
        print("PASS: all six failpoints recovered through normal loops.")
        return 0
    except (RecoveryFailure, subprocess.TimeoutExpired) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    finally:
        if not args.keep:
            compose("down", "-v", timeout=120, check=False, env=base_env)


if __name__ == "__main__":
    raise SystemExit(main())
