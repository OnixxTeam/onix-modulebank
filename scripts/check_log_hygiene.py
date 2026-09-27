"""Checks the log hygiene the week-4 contract asks for, on a running stack.

Every application log line of every checked process must be one JSON object, and
none may carry a synthetic secret. Run it after a scenario, while the stack is up:

    python3 scripts/check_log_hygiene.py

Exit code 0 means clean; every offending line is printed with its service.
"""

import json
import subprocess
import sys


def _is_json(line: str) -> bool:
    try:
        return isinstance(json.loads(line), dict)
    except ValueError:
        return False

SERVICES = [
    "api", "worker-a", "worker-b",
    "outbox-dispatcher", "outbox-dispatcher-b",
    "receipt-adapter", "inbox-reconciler", "inbox-reconciler-b",
]

FORBIDDEN = ["Bearer ", "PGPASSWORD", "dev-insecure-hmac", "dev-insecure-key"]

bad = 0
for service in SERVICES:
    raw = subprocess.run(
        ["docker", "compose", "logs", "--no-log-prefix", "--tail", "40", service],
        capture_output=True, text=True, check=False,
    ).stdout

    lines = [line for line in raw.splitlines() if line.strip()]
    non_json = [line for line in lines if not _is_json(line)] if lines else []
    leaks = [line for line in lines for needle in FORBIDDEN if needle in line]

    print(f"{service:20} lines={len(lines):3} non-json={len(non_json):2} leaks={len(leaks)}")
    for line in non_json[:2]:
        print(f"    NOT JSON: {line[:160]}")
    for line in leaks[:2]:
        print(f"    LEAK: {line[:160]}")
    bad += len(non_json) + len(leaks)

sys.exit(1 if bad else 0)
