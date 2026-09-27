"""Environment configuration, read once at process start.

Database connection settings are deliberately *not* parsed here: psycopg
reads the standard libpq variables (``PGHOST``, ``PGDATABASE``, ``PGUSER``,
``PGPASSWORD``, ``PGAPPNAME``, ...) from the process environment on its own
when ``psycopg.connect()`` is called with no arguments. That is not a
stylistic choice -- the course's own checker injects the claimed role's
name through ``PGUSER`` and ``PGAPPNAME`` directly, and only the standard
names make role substitution work (spec.md "Документация задания говорит,
что имена внутренних переменных выбираются свободно -- код проверки это
опровергает, приоритет у кода проверки"). This module only validates that
the required names are *present*, so a misconfigured container fails fast
with a clear message instead of hanging on a connection attempt or a
provider call with an empty base URL.
"""

from __future__ import annotations

import os
from dataclasses import dataclass

REQUIRED_VARS = (
    "PGHOST",
    "PGDATABASE",
    "PGUSER",
    "PGPASSWORD",
    "PGAPPNAME",
    "PROVIDER_URL",
    "OUTBOX_OWNER",
)

# The adapter deliberately has no PGHOST/PGUSER/... entry here at all -- it
# has no PostgreSQL credentials in any form (SPEC/ticket 12 acceptance), so
# there is nothing libpq-shaped to validate.
ADAPTER_REQUIRED_VARS = (
    "PROVIDER_CALLBACK_CAPABILITY",
    "PROVIDER_CALLBACK_TOKEN",
    "PROVIDER_HMAC_SECRET",
    "RECEIPT_API_URL",
)

RECONCILER_REQUIRED_VARS = (
    "PGHOST",
    "PGDATABASE",
    "PGUSER",
    "PGPASSWORD",
    "PGAPPNAME",
)


class MissingConfigError(RuntimeError):
    """Raised when a required environment variable is absent."""

    def __init__(self, missing: list[str]) -> None:
        super().__init__(f"missing required environment variable(s): {', '.join(missing)}")
        self.missing = missing


class InvalidConfigError(RuntimeError):
    """Raised when a required environment variable is present but unusable."""

    def __init__(self, name: str, raw: str) -> None:
        super().__init__(f"{name} must be a positive integer number of milliseconds, got {raw!r}")
        self.name = name
        self.raw = raw


def _positive_int(env: dict[str, str], name: str, default: int) -> int:
    """Reads a millisecond setting, or fails fast.

    Absent means "take the profile default"; present but unusable means a
    misconfigured container, and that must stop the process on start rather than
    silently run with a timeout or a poll interval nobody chose -- a dispatcher
    quietly polling once every two seconds under the test profile looks exactly
    like a dispatcher that lost the database.
    """
    raw = env.get(name)
    if raw is None or raw.strip() == "":
        return default

    try:
        value = int(raw)
    except ValueError:
        raise InvalidConfigError(name, raw) from None

    if value <= 0:
        raise InvalidConfigError(name, raw)

    return value


@dataclass(frozen=True)
class DispatcherConfig:
    provider_url: str
    owner: str
    poll_interval_seconds: float
    claim_limit: int
    # The external call's own deadline. It has to be shorter than the delivery
    # lease, or a slow provider would hold a claim past its expiry and a second
    # dispatcher would reclaim a delivery still in flight.
    provider_timeout_seconds: float
    # Not a decision this process makes -- PostgreSQL owns the retry budget
    # (ADR 0006) -- only the denominator of the "attempt N of M" log field.
    max_attempts: int
    lease_seconds: float


def load_dispatcher_config(env: dict[str, str] | None = None) -> DispatcherConfig:
    env = os.environ if env is None else env

    missing = [name for name in REQUIRED_VARS if not env.get(name)]
    if missing:
        raise MissingConfigError(missing)

    # COURSE_TEST_PROFILE=1 selects the short defaults the verifying run needs
    # (same convention as Workflow.Worker's IntervalProfile); unset or any other
    # value keeps the slower production default. Each published variable overrides
    # its own default regardless of profile.
    test_profile = env.get("COURSE_TEST_PROFILE") == "1"

    return DispatcherConfig(
        provider_url=env["PROVIDER_URL"].rstrip("/"),
        owner=env["OUTBOX_OWNER"],
        poll_interval_seconds=_positive_int(env, "COURSE_OUTBOX_POLL_MS", 100 if test_profile else 2_000) / 1000,
        claim_limit=5,
        provider_timeout_seconds=_positive_int(env, "COURSE_PROVIDER_TIMEOUT_MS", 500 if test_profile else 10_000) / 1000,
        max_attempts=_positive_int(env, "COURSE_OUTBOX_MAX_ATTEMPTS", 4),
        lease_seconds=_positive_int(env, "COURSE_OUTBOX_LEASE_MS", 2_000 if test_profile else 30_000) / 1000,
    )


@dataclass(frozen=True)
class AdapterConfig:
    capability: str
    callback_token: str
    hmac_secret: bytes
    receipt_api_url: str


def load_adapter_config(env: dict[str, str] | None = None) -> AdapterConfig:
    env = os.environ if env is None else env

    missing = [name for name in ADAPTER_REQUIRED_VARS if not env.get(name)]
    if missing:
        raise MissingConfigError(missing)

    return AdapterConfig(
        capability=env["PROVIDER_CALLBACK_CAPABILITY"],
        callback_token=env["PROVIDER_CALLBACK_TOKEN"],
        # Raw bytes of exactly the string value -- no trim/base64/hex decoding
        # (spec.md, quoted above the module docstring for the dispatcher's
        # own equally-literal PGUSER/PGAPPNAME rule).
        hmac_secret=env["PROVIDER_HMAC_SECRET"].encode("utf-8"),
        receipt_api_url=env["RECEIPT_API_URL"].rstrip("/"),
    )


@dataclass(frozen=True)
class ReconcilerConfig:
    poll_interval_seconds: float
    limit: int


def load_reconciler_config(env: dict[str, str] | None = None) -> ReconcilerConfig:
    env = os.environ if env is None else env

    missing = [name for name in RECONCILER_REQUIRED_VARS if not env.get(name)]
    if missing:
        raise MissingConfigError(missing)

    # The test profile's own ceiling is 500 ms; production keeps a slower
    # default, same convention as the dispatcher's switch above.
    test_profile = env.get("COURSE_TEST_PROFILE") == "1"

    return ReconcilerConfig(
        poll_interval_seconds=_positive_int(env, "COURSE_INBOX_POLL_MS", 200 if test_profile else 2_000) / 1000,
        limit=20,
    )
