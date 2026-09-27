"""outbox-dispatcher entrypoint: PostgreSQL Outbox -> provider-simulator.

The thin I/O shell around ``request``/``classify`` (both pure) and ``db``
(the three-function SQL boundary, ticket 09). One claim is one HTTP attempt
(spec.md "Главный инвариант доставки: один захват -- одна HTTP-попытка"); the
dispatcher never decides retry counts or delays, it only reports a
classified error code and lets PostgreSQL decide the rest.
"""

from __future__ import annotations

import logging
import sys
import time

import httpx
import psycopg

from perimeter import db
from perimeter.classify import ClassificationResult, Retry, Success, Terminal, classify_http_response, classify_transport_failure
from perimeter.config import DispatcherConfig, InvalidConfigError, MissingConfigError, load_dispatcher_config
from perimeter.logs import configure as configure_logging
from perimeter.observability import Counters, Family, create_observability_app, serve_in_background
from perimeter.request import build_provider_request
from perimeter.runloop import ShutdownFlag, log_and_wait_after_transient_db_error

logger = logging.getLogger("perimeter.dispatcher")

# This process's own metrics. Not the six required series -- those are `api`'s, read
# from one PostgreSQL snapshot -- but what only this process can see: how often its
# loop turned, and how its attempts came out. `outcome` is a closed set of three
# values, so its cardinality is bounded by construction.
POLL_CYCLES = "outbox_dispatcher_poll_cycles"
DELIVERY_ATTEMPTS = "outbox_dispatcher_attempts"
DELIVERIES_CLAIMED = "outbox_dispatcher_claimed"

counters = Counters()

# Internal only -- no host port is published for this service.
OBSERVABILITY_PORT = 8080


def metric_families() -> list[Family]:
    return [
        Family(POLL_CYCLES, "counter", "Loop iterations completed", counters.snapshot(POLL_CYCLES)),
        Family(DELIVERIES_CLAIMED, "counter", "Deliveries claimed from the Outbox", counters.snapshot(DELIVERIES_CLAIMED)),
        Family(DELIVERY_ATTEMPTS, "counter", "Provider attempts by outcome", counters.snapshot(DELIVERY_ATTEMPTS)),
    ]


def attempt_delivery(client: httpx.Client, config: DispatcherConfig, delivery: db.ClaimedDelivery) -> ClassificationResult:
    request = build_provider_request(delivery.external_request_id, delivery.correlation_id, delivery.amount, delivery.currency)

    try:
        response = client.post(
            f"{config.provider_url}/payments",
            content=request.body,
            headers={
                "Content-Type": "application/json",
                "Idempotency-Key": request.idempotency_key,
                "X-Correlation-ID": request.correlation_id,
            },
            timeout=config.provider_timeout_seconds,
        )
    except httpx.RequestError:
        return classify_transport_failure()

    return classify_http_response(response.status_code, response.content)


def process_claim(conn: psycopg.Connection, client: httpx.Client, config: DispatcherConfig, delivery: db.ClaimedDelivery) -> None:
    result = attempt_delivery(client, config, delivery)

    match result:
        case Success(provider_payment_id=provider_payment_id):
            counters.increment(DELIVERY_ATTEMPTS, {"outcome": "succeeded"})
            db.succeed_outbox(conn, delivery.outbox_id, config.owner, delivery.lease_version, provider_payment_id)
            logger.info("delivery succeeded", extra={"outbox_id": delivery.outbox_id})
        case Retry(error_code=error_code):
            counters.increment(DELIVERY_ATTEMPTS, {"outcome": "retryable"})
            db.fail_outbox(
                conn, delivery.outbox_id, config.owner, delivery.lease_version, error_code)
            logger.info(
                "delivery failed",
                extra={
                    "outbox_id": delivery.outbox_id,
                    "error_code": error_code,
                    "max_attempts": config.max_attempts,
                },
            )

        case Terminal(error_code=error_code):
            counters.increment(DELIVERY_ATTEMPTS, {"outcome": "terminal"})
            db.fail_outbox(conn, delivery.outbox_id, config.owner, delivery.lease_version, error_code)
            # max_attempts is the denominator only: whether this failure spends the
            # budget is fail_outbox's answer, not this process's.
            logger.info(
                "delivery failed",
                extra={
                    "outbox_id": delivery.outbox_id,
                    "error_code": error_code,
                    "max_attempts": config.max_attempts,
                },
            )


def run(config: DispatcherConfig) -> None:
    shutdown = ShutdownFlag()
    shutdown.install()

    with psycopg.connect(autocommit=True) as conn, httpx.Client(follow_redirects=False) as client:
        # PostgreSQL is this process's only critical dependency; the provider is
        # deliberately not one, because a provider outage accumulates in the Outbox
        # and is a normal mode rather than an unready process. The probe opens its
        # own connection so a loop parked mid-claim cannot make readiness hang on
        # the connection it is holding.
        serve_in_background(
            create_observability_app(readiness=postgres_reachable, metrics=metric_families),
            port=OBSERVABILITY_PORT,
        )

        while not shutdown.requested:
            try:
                claims = db.claim_outbox(conn, config.owner, config.claim_limit)
            except psycopg.Error as exc:
                log_and_wait_after_transient_db_error(logger, "outbox claim", exc, config.poll_interval_seconds)
                continue

            counters.increment(POLL_CYCLES)

            if not claims:
                time.sleep(config.poll_interval_seconds)
                continue

            counters.increment(DELIVERIES_CLAIMED, by=len(claims))

            for delivery in claims:
                if shutdown.requested:
                    return
                process_claim(conn, client, config, delivery)


def postgres_reachable() -> bool:
    try:
        with psycopg.connect(connect_timeout=2) as probe, probe.cursor() as cursor:
            cursor.execute("select 1")
            return True
    except psycopg.Error:
        return False


def main() -> None:
    configure_logging()

    try:
        config = load_dispatcher_config()
    except (MissingConfigError, InvalidConfigError) as exc:
        logger.error(str(exc))
        sys.exit(1)

    run(config)


if __name__ == "__main__":
    main()
