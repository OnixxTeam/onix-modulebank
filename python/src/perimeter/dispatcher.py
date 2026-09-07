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
from perimeter.config import DispatcherConfig, MissingConfigError, load_dispatcher_config
from perimeter.request import build_provider_request
from perimeter.runloop import ShutdownFlag, log_and_wait_after_transient_db_error

logger = logging.getLogger("perimeter.dispatcher")

_HTTP_TIMEOUT_SECONDS = 10.0


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
            timeout=_HTTP_TIMEOUT_SECONDS,
        )
    except httpx.RequestError:
        return classify_transport_failure()

    return classify_http_response(response.status_code, response.content)


def process_claim(conn: psycopg.Connection, client: httpx.Client, config: DispatcherConfig, delivery: db.ClaimedDelivery) -> None:
    result = attempt_delivery(client, config, delivery)

    match result:
        case Success(provider_payment_id=provider_payment_id):
            db.succeed_outbox(conn, delivery.outbox_id, config.owner, delivery.lease_version, provider_payment_id)
            logger.info("delivery succeeded", extra={"outbox_id": delivery.outbox_id})
        case Retry(error_code=error_code) | Terminal(error_code=error_code):
            db.fail_outbox(conn, delivery.outbox_id, config.owner, delivery.lease_version, error_code)
            logger.info("delivery failed", extra={"outbox_id": delivery.outbox_id, "error_code": error_code})


def run(config: DispatcherConfig) -> None:
    shutdown = ShutdownFlag()
    shutdown.install()

    with psycopg.connect(autocommit=True) as conn, httpx.Client(follow_redirects=False) as client:
        while not shutdown.requested:
            try:
                claims = db.claim_outbox(conn, config.owner, config.claim_limit)
            except psycopg.Error as exc:
                log_and_wait_after_transient_db_error(logger, "outbox claim", exc, config.poll_interval_seconds)
                continue

            if not claims:
                time.sleep(config.poll_interval_seconds)
                continue

            for delivery in claims:
                if shutdown.requested:
                    return
                process_claim(conn, client, config, delivery)


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    try:
        config = load_dispatcher_config()
    except MissingConfigError as exc:
        logger.error(str(exc))
        sys.exit(1)

    run(config)


if __name__ == "__main__":
    main()
