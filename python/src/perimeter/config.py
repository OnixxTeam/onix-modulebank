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


@dataclass(frozen=True)
class DispatcherConfig:
    provider_url: str
    owner: str
    poll_interval_seconds: float
    claim_limit: int


def load_dispatcher_config(env: dict[str, str] | None = None) -> DispatcherConfig:
    env = os.environ if env is None else env

    missing = [name for name in REQUIRED_VARS if not env.get(name)]
    if missing:
        raise MissingConfigError(missing)

    # COURSE_TEST_PROFILE=1 selects the short interval the public/hidden
    # checker needs (same convention as Workflow.Worker's IntervalProfile);
    # unset or any other value keeps the slower production default.
    test_profile = env.get("COURSE_TEST_PROFILE") == "1"

    return DispatcherConfig(
        provider_url=env["PROVIDER_URL"].rstrip("/"),
        owner=env["OUTBOX_OWNER"],
        poll_interval_seconds=0.2 if test_profile else 2.0,
        claim_limit=5,
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

    # configuration.md/04-week-3.md: test profile poll interval must not
    # exceed 500 ms; production keeps a slower default, same convention as
    # the dispatcher's own test-profile switch.
    test_profile = env.get("COURSE_TEST_PROFILE") == "1"

    return ReconcilerConfig(
        poll_interval_seconds=0.2 if test_profile else 2.0,
        limit=20,
    )
