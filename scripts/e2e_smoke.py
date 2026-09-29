#!/usr/bin/env python3
"""Fast end-to-end smoke run over an already-built compose stack (ticket 14).

Exercises both cards over the real wire -- gateway, both workers, the real
provider-simulator image, and all three Python perimeter processes -- the one
seam nothing below it can reach. It is deliberately *not* a second copy of
the public checker (autocheck/): no compose-contract, no least-privilege, no
recreate-and-hygiene, no adapter deduplication/conflict assertions -- those
already have an authoritative owner (`./check.sh --repo .` from the week-3
task clone) and duplicating them here would just be a second, slower checker
(ticket 14 "Сквозной прогон не дублирует то, что уже покрыто на нижних швах").
This script exists purely so a debugging cycle does not need one of the
checker's own cold `--pull`/`--no-cache` builds: `docker compose up -d --build`
here reuses layer cache like any other local build.

Usage:
    python3 scripts/e2e_smoke.py [--keep]

--keep leaves the compose project running (and the built images) afterwards
instead of tearing it down, for follow-up `make logs`/`make psql` digging.

Exit code 0 means both cards (payment-processing, and payment-review on both
the automatic and the manual branch) reached their expected final state.
Anything else is a failure; stderr carries the reason.
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
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
# The single published entry point. COURSE_GATEWAY_PORT moves it, exactly as it
# does in compose.yaml, so a run against a stack started on another port needs
# no edit here.
GATEWAY_URL = f"http://127.0.0.1:{os.environ.get('COURSE_GATEWAY_PORT', '8080')}"
JWT_SIGNING_KEY = "dev-insecure-key-not-for-production-32"  # compose.yaml's own default
ISSUER = "moduledev-course"
AUDIENCE = "moduledev-api"
POLL_TIMEOUT_SECONDS = 30.0
POLL_INTERVAL_SECONDS = 0.3


class SmokeFailure(RuntimeError):
    pass


def _b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def issue_token(subject: str, consumer: str, scopes: list[str]) -> str:
    now = int(time.time())
    header = _b64url(b'{"alg":"HS256","typ":"JWT"}')
    payload = _b64url(
        json.dumps(
            {
                "iss": ISSUER,
                "aud": AUDIENCE,
                "sub": subject,
                "consumer": consumer,
                "scope": " ".join(scopes),
                "iat": now - 5,
                "exp": now + 3600,
            },
            separators=(",", ":"),
        ).encode("utf-8")
    )
    signing_input = f"{header}.{payload}".encode("ascii")
    signature = _b64url(hmac.new(JWT_SIGNING_KEY.encode("utf-8"), signing_input, hashlib.sha256).digest())
    return f"{header}.{payload}.{signature}"


def compose(*args: str, timeout: float = 120, env_overrides: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    import os

    env = {**os.environ, **(env_overrides or {})}
    return subprocess.run(
        ["docker", "compose", *args],
        cwd=REPO_ROOT,
        env=env,
        text=True,
        capture_output=True,
        timeout=timeout,
    )


def psql_rows(query: str) -> list[dict[str, object]]:
    wrapped = f"SELECT COALESCE(jsonb_agg(to_jsonb(q)), '[]'::jsonb)::text FROM ({query}) AS q"
    result = compose(
        "exec", "-T", "postgres", "psql", "-X", "-v", "ON_ERROR_STOP=1",
        "-U", "postgres", "-d", "course", "-At", "-c", wrapped,
    )
    if result.returncode != 0:
        raise SmokeFailure(f"psql query failed: {result.stderr.strip()}")
    return json.loads(result.stdout.strip())


def action(module: str, name: str, payload: dict[str, object], token: str, idempotency_key: str) -> dict[str, object]:
    body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        f"{GATEWAY_URL}/api/{module}/{name}",
        data=body,
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "X-Action-Version": "1",
            "Idempotency-Key": idempotency_key,
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        raise SmokeFailure(f"{module}.{name} failed: HTTP {error.code} {error.read().decode(errors='replace')}") from error


def wait_until(description: str, predicate) -> None:
    deadline = time.monotonic() + POLL_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(POLL_INTERVAL_SECONDS)
    raise SmokeFailure(f"timed out waiting for: {description}")


def wait_for_gateway() -> None:
    def probe() -> bool:
        try:
            with urllib.request.urlopen(f"{GATEWAY_URL}/health/live", timeout=2) as response:
                return response.status == 200
        except OSError:
            return False

    wait_until("gateway /health/live", probe)


def operation_status(operation_id: str) -> str | None:
    rows = psql_rows(
        "SELECT status FROM autocheck.operations "
        f"WHERE operation_id = '{operation_id}'::uuid"
    )
    return rows[0]["status"] if rows else None


def submit_card(client_token: str, label: str, operation_kind: str, amount: str) -> tuple[str, str]:
    request_result = action(
        "payment", "request",
        {"operationKind": operation_kind, "amount": amount, "currency": "RUB"},
        client_token, f"smoke-request-{label}-{secrets.token_hex(4)}",
    )
    operation_id = request_result["result"]["operationId"]
    submit_result = action(
        "payment", "submit", {"operationId": operation_id},
        client_token, f"smoke-submit-{label}-{secrets.token_hex(4)}",
    )
    process_id = submit_result["result"]["processId"]
    return operation_id, process_id


def run_payment_card(client_token: str) -> None:
    operation_id, _ = submit_card(client_token, "payment", "PAYMENT_EXECUTION", "1000.00")
    wait_until(
        f"payment-processing operation {operation_id} COMPLETED",
        lambda: operation_status(operation_id) == "COMPLETED",
    )
    print(f"  payment-processing: operation {operation_id} -> COMPLETED")


def run_review_auto_branch(client_token: str) -> None:
    # Exactly at the limit (course-limit-v1): approved automatically, no manual step.
    operation_id, _ = submit_card(client_token, "review-auto", "PAYMENT_APPROVAL", "100000.00")
    wait_until(
        f"payment-review (auto) operation {operation_id} COMPLETED",
        lambda: operation_status(operation_id) == "COMPLETED",
    )
    print(f"  payment-review (auto branch): operation {operation_id} -> COMPLETED")


def run_review_manual_branch(client_token: str, reviewer_token: str) -> None:
    # One cent over the limit: routes to the manual step instead.
    operation_id, process_id = submit_card(client_token, "review-manual", "PAYMENT_APPROVAL", "100000.01")

    def process_waiting_manual() -> bool:
        rows = psql_rows(
            f"SELECT state FROM autocheck.processes WHERE process_id = '{process_id}'::uuid"
        )
        return bool(rows) and rows[0]["state"] == "WAITING_MANUAL"

    wait_until(f"process {process_id} WAITING_MANUAL", process_waiting_manual)

    steps = psql_rows(
        "SELECT step_instance_id FROM autocheck.steps "
        f"WHERE process_id = '{process_id}'::uuid AND step_type = 'MANUAL' AND state = 'WAITING'"
    )
    if len(steps) != 1:
        raise SmokeFailure(f"expected exactly one waiting manual step for process {process_id}, found {len(steps)}")
    step_instance_id = steps[0]["step_instance_id"]

    action(
        "workflow", "manual",
        {
            "processId": process_id,
            "stepInstanceId": step_instance_id,
            "decision": "APPROVED",
            "reason": "e2e_smoke approval",
        },
        reviewer_token, f"smoke-manual-{secrets.token_hex(4)}",
    )
    wait_until(
        f"payment-review (manual) operation {operation_id} COMPLETED",
        lambda: operation_status(operation_id) == "COMPLETED",
    )
    print(f"  payment-review (manual branch): operation {operation_id} -> COMPLETED")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--keep", action="store_true", help="leave the compose project running afterwards")
    args = parser.parse_args()

    capability = secrets.token_hex(16)
    callback_token = issue_token("receipt-provider", "receipt-adapter", ["receipt:write"])
    client_token = issue_token("candidate-client", "web", ["payment:write", "payment:read", "workflow:read"])
    reviewer_token = issue_token("reviewer", "backoffice", ["workflow:manual", "payment:read"])

    env_overrides = {
        "COURSE_TEST_PROFILE": "1",
        "PROVIDER_CALLBACK_CAPABILITY": capability,
        "PROVIDER_CALLBACK_TOKEN": callback_token,
    }

    print("bringing up the compose stack (cached build, no --pull/--no-cache)...")
    up = compose("up", "-d", "--build", timeout=600, env_overrides=env_overrides)
    if up.returncode != 0:
        print(up.stdout, file=sys.stderr)
        print(up.stderr, file=sys.stderr)
        return 2

    try:
        print("waiting for gateway...")
        wait_for_gateway()

        print("running payment-processing card...")
        run_payment_card(client_token)

        print("running payment-review card, automatic branch...")
        run_review_auto_branch(client_token)

        print("running payment-review card, manual branch...")
        run_review_manual_branch(client_token, reviewer_token)

        print("PASS: both cards reached their expected final state.")
        return 0
    except SmokeFailure as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    finally:
        if args.keep:
            print("--keep set: leaving the compose project running.")
        else:
            compose("down", "-v", timeout=120, env_overrides=env_overrides)


if __name__ == "__main__":
    sys.exit(main())
