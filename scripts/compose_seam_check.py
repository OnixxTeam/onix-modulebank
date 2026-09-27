#!/usr/bin/env python3
"""Checks the tracked Compose file against the published week-4 configuration seam.

The verifying run reads `docker compose config` of the *tracked* file with its own
synthetic values and refuses the submission when a required variable never reaches
its recipient, when a secret reaches a service that should not hold it, when a
service other than `gateway` publishes a host port, or when a service connects as
the wrong PostgreSQL role. Those rules live in the published configuration table,
not in the solution, so they are restated here to get a fast local answer instead
of a full cold-build run.

This is the local loop, not the authority: the vendored public checker (ticket 14)
stays the authority, and a disagreement between the two means this file is stale.

    python3 scripts/compose_seam_check.py

Exit code 0 means the seam holds. Any finding is printed one per line, exit 1.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

CSHARP_SERVICES = ("gateway", "api", "worker-a", "worker-b")
PYTHON_SERVICES = (
    "outbox-dispatcher",
    "outbox-dispatcher-b",
    "receipt-adapter",
    "inbox-reconciler",
    "inbox-reconciler-b",
)
REQUIRED_SERVICES = {
    "gateway", "api", "cli", "postgres", "worker-a", "worker-b",
    "outbox-dispatcher", "outbox-dispatcher-b", "receipt-adapter",
    "inbox-reconciler", "inbox-reconciler-b", "provider-simulator",
}

# Variable -> the services whose tracked environment must reference it.
REQUIRED_ENV_RECIPIENTS: dict[str, tuple[str, ...]] = {
    "COURSE_GATEWAY_PORT": ("gateway",),
    "COURSE_TEST_PROFILE": CSHARP_SERVICES + ("cli",) + PYTHON_SERVICES,
    "COURSE_JWT_ISSUER": CSHARP_SERVICES + ("cli",),
    "COURSE_JWT_AUDIENCE": CSHARP_SERVICES + ("cli",),
    "COURSE_JWT_SIGNING_KEY": CSHARP_SERVICES + ("cli",),
    "COURSE_POSTGRES_PASSWORD": ("postgres",),
    "COURSE_MIGRATOR_PASSWORD": ("postgres", "cli"),
    "COURSE_PUBLISHER_PASSWORD": ("postgres", "cli"),
    "COURSE_RUNTIME_PASSWORD": ("postgres", "api"),
    "COURSE_WORKER_PASSWORD": ("postgres", "worker-a", "worker-b"),
    "COURSE_OUTBOX_PASSWORD": ("postgres", "outbox-dispatcher", "outbox-dispatcher-b"),
    "COURSE_INBOX_PASSWORD": ("postgres", "inbox-reconciler", "inbox-reconciler-b"),
    "COURSE_AUTOCHECK_PASSWORD": ("postgres",),
    "PROVIDER_URL": ("outbox-dispatcher", "outbox-dispatcher-b"),
    "OUTBOX_OWNER": ("outbox-dispatcher",),
    "OUTBOX_OWNER_B": ("outbox-dispatcher-b",),
    "COURSE_FAILPOINT": (
        "api", "worker-a", "worker-b",
        "outbox-dispatcher", "outbox-dispatcher-b",
        "inbox-reconciler", "inbox-reconciler-b",
    ),
    "COURSE_PROVIDER_TIMEOUT_MS": ("outbox-dispatcher", "outbox-dispatcher-b"),
    "COURSE_OUTBOX_MAX_ATTEMPTS": ("postgres", "outbox-dispatcher", "outbox-dispatcher-b"),
    "COURSE_OUTBOX_BACKOFF_BASE_MS": ("postgres",),
    "COURSE_OUTBOX_BACKOFF_MAX_MS": ("postgres",),
    "COURSE_OUTBOX_JITTER_MAX_MS": ("postgres",),
    "COURSE_OUTBOX_LEASE_MS": ("postgres", "outbox-dispatcher", "outbox-dispatcher-b"),
    "COURSE_OUTBOX_POLL_MS": ("outbox-dispatcher", "outbox-dispatcher-b"),
    "COURSE_INBOX_POLL_MS": ("inbox-reconciler", "inbox-reconciler-b"),
    "COURSE_JOB_LEASE_MS": ("postgres", "worker-a", "worker-b"),
    "COURSE_WORKER_POLL_MS": ("worker-a", "worker-b"),
    "PROVIDER_CALLBACK_CAPABILITY": ("receipt-adapter", "provider-simulator"),
    "PROVIDER_CALLBACK_TOKEN": ("receipt-adapter",),
    "PROVIDER_HMAC_SECRET": ("receipt-adapter", "api"),
    "RECEIPT_API_URL": ("receipt-adapter",),
    "PROVIDER_AUDIT_TOKEN": ("provider-simulator",),
}

# Secret -> the services allowed to hold its value at all.
SECRET_SERVICES: dict[str, tuple[str, ...]] = {
    "COURSE_JWT_SIGNING_KEY": CSHARP_SERVICES + ("cli",),
    "PROVIDER_HMAC_SECRET": ("api", "receipt-adapter"),
    "PROVIDER_CALLBACK_CAPABILITY": ("receipt-adapter", "provider-simulator"),
    "PROVIDER_CALLBACK_TOKEN": ("receipt-adapter",),
    "PROVIDER_AUDIT_TOKEN": ("provider-simulator",),
    "COURSE_POSTGRES_PASSWORD": ("postgres",),
    "COURSE_MIGRATOR_PASSWORD": ("postgres", "cli"),
    "COURSE_PUBLISHER_PASSWORD": ("postgres", "cli"),
    "COURSE_RUNTIME_PASSWORD": ("postgres", "api"),
    "COURSE_WORKER_PASSWORD": ("postgres", "worker-a", "worker-b"),
    "COURSE_OUTBOX_PASSWORD": ("postgres", "outbox-dispatcher", "outbox-dispatcher-b"),
    "COURSE_INBOX_PASSWORD": ("postgres", "inbox-reconciler", "inbox-reconciler-b"),
    "COURSE_AUTOCHECK_PASSWORD": ("postgres",),
}

EXPECTED_PRINCIPALS = {
    "api": "course_runtime",
    "worker-a": "workflow_worker",
    "worker-b": "workflow_worker",
    "outbox-dispatcher": "outbox_dispatcher",
    "outbox-dispatcher-b": "outbox_dispatcher",
    "inbox-reconciler": "inbox_reconciler",
    "inbox-reconciler-b": "inbox_reconciler",
}

DATABASE_KEY = re.compile(
    r"(?:^|_)(?:PG(?:HOST|PORT|DATABASE|USER|PASSWORD|SERVICE)|"
    r"DB_(?:HOST|PORT|NAME|USER|USERNAME|PASSWORD)|DB_(?:URL|URI|DSN)|"
    r"DATABASE_(?:HOST|PORT|NAME|USER|USERNAME|PASSWORD|URL|URI|DSN)|"
    r"POSTGRES(?:QL)?_(?:HOST|PORT|DB|DATABASE|USER|USERNAME|PASSWORD|URL|URI|DSN)|"
    r"PGDSN|CONNECTION_STRING|DATABASE_CONNECTION_STRING)(?:$|_)",
    re.I,
)
USER_KEY = re.compile(
    r"(?:^|_)(?:PGUSER|DB_USER|DB_USERNAME|DATABASE_USER|DATABASE_USERNAME|"
    r"POSTGRES_USER|POSTGRES_USERNAME)(?:$|_)",
    re.I,
)


def render(environment: dict[str, str]) -> dict:
    result = subprocess.run(
        ["docker", "compose", "config", "--format", "json"],
        cwd=REPO,
        env={"PATH": "/usr/bin:/bin:/usr/local/bin", "HOME": str(Path.home()), **environment},
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        raise SystemExit(f"docker compose config failed:\n{result.stderr.strip()}")
    return json.loads(result.stdout)


def service_environment(service: dict) -> dict[str, str]:
    environment = service.get("environment") or {}
    if isinstance(environment, list):
        pairs = (entry.split("=", 1) for entry in environment)
        return {key: value for key, *rest in pairs for value in (rest or [""])}
    return {key: "" if value is None else str(value) for key, value in environment.items()}


def published(service: dict) -> list[dict]:
    return service.get("ports") or []


def database_entries(environment: dict[str, str]) -> dict[str, str]:
    entries = {k: v for k, v in environment.items() if DATABASE_KEY.search(k)}
    for key, value in environment.items():
        if re.match(r"^postgres(?:ql)?://", value, re.I) or re.search(
            r"(?:^|\s)(?:host|hostaddr|port|dbname|user|password|service)=", value, re.I
        ):
            entries[key] = value
    return entries


def principals(environment: dict[str, str]) -> set[str]:
    found: set[str] = set()
    for key, value in database_entries(environment).items():
        if USER_KEY.search(key) and value:
            found.add(value)
        npgsql = re.search(r"(?:^|;)\s*(?:user\s*id|username)\s*=\s*([^;]+)", value, re.I)
        if npgsql:
            found.add(npgsql.group(1).strip())
    return found


def check_references(findings: list[str]) -> None:
    markers = {name: f"MARKERREF{index:03d}" for index, name in enumerate(REQUIRED_ENV_RECIPIENTS)}
    markers["COURSE_GATEWAY_PORT"] = "8080"
    config = render(markers)
    services = config.get("services", {})

    missing = sorted(REQUIRED_SERVICES - set(services))
    if missing:
        findings.append(f"missing services: {', '.join(missing)}")
        return

    for name, service in services.items():
        ports = published(service)
        if name == "gateway":
            hosts = {entry.get("host_ip") for entry in ports}
            targets = {entry.get("target") for entry in ports}
            published_ports = {int(entry.get("published")) for entry in ports if entry.get("published")}
            if published_ports != {8080} or targets != {8080} or hosts != {"127.0.0.1"}:
                findings.append(
                    f"gateway must bind 127.0.0.1:8080 to target 8080, got {ports}"
                )
        elif ports:
            findings.append(f"{name}: only gateway may publish host ports")

    for variable, recipients in REQUIRED_ENV_RECIPIENTS.items():
        if variable == "COURSE_GATEWAY_PORT":
            continue
        marker = markers[variable]
        for recipient in recipients:
            environment = service_environment(services.get(recipient, {}))
            if not any(marker in value for value in environment.values()):
                findings.append(f"{recipient}: tracked environment must reference {variable}")

    for name, expected in EXPECTED_PRINCIPALS.items():
        actual = principals(service_environment(services[name]))
        if actual != {expected}:
            findings.append(f"{name}: database principal must be {expected}, got {sorted(actual) or 'none'}")

    if database_entries(service_environment(services["receipt-adapter"])):
        findings.append("receipt-adapter must not receive PostgreSQL configuration")

    owners = {
        name: service_environment(services[name]).get("OUTBOX_OWNER")
        for name in ("outbox-dispatcher", "outbox-dispatcher-b")
    }
    if owners["outbox-dispatcher"] == owners["outbox-dispatcher-b"]:
        findings.append(f"Outbox dispatchers must use distinct owners, got {owners}")

    images = {str(services[name].get("image", "")) for name in PYTHON_SERVICES}
    builds = [name for name in PYTHON_SERVICES if services[name].get("build")]
    if len(images) != 1 or not next(iter(images), ""):
        findings.append(f"Python services must share one image, got {sorted(images)}")
    if len(builds) != 1:
        findings.append(f"exactly one Python service declares build, got {builds}")

    volumes = config.get("volumes") or {}
    sources = {
        entry.get("source")
        for entry in (services["postgres"].get("volumes") or [])
        if entry.get("type") == "volume"
    }
    if not sources & set(volumes):
        findings.append("postgres must persist data on a declared named volume")


def check_secret_distribution(findings: list[str]) -> None:
    secrets = {name: f"SECRETVALUE{index:03d}" for index, name in enumerate(SECRET_SERVICES)}
    environment = dict(secrets)
    environment["COURSE_GATEWAY_PORT"] = "8080"
    config = render(environment)
    services = config.get("services", {})

    top_level = {key: value for key, value in config.items() if key != "services"}
    top_level_text = json.dumps(top_level, ensure_ascii=False)

    for variable, allowed in SECRET_SERVICES.items():
        secret = secrets[variable]
        if secret in top_level_text:
            findings.append(f"{variable}: value is exposed outside service environment")
        for name, service in services.items():
            holder = json.dumps(service, ensure_ascii=False)
            if secret in holder and name not in allowed:
                findings.append(f"{variable}: value is exposed to {name}")


def main() -> int:
    findings: list[str] = []
    check_references(findings)
    check_secret_distribution(findings)

    if findings:
        for finding in findings:
            print(finding)
        print(f"\n{len(findings)} finding(s)")
        return 1

    print("compose seam holds")
    return 0


if __name__ == "__main__":
    sys.exit(main())
