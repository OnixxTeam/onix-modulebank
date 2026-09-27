"""inbox-reconciler entrypoint: PostgreSQL Inbox -> workflow signal, one function call per cycle.

The thinnest of the three perimeter processes (spec.md "третий и самый
тонкий процесс периметра: один вызов одной функции в цикле"). Matching a
message to its pinned process, creating or deduplicating the workflow
signal, and transitioning Inbox to ``APPLIED`` all happen inside
``delivery.reconcile_inbox`` itself (ticket 09) -- this module owns nothing
but the poll loop and its own least-privilege connection, and never reads a
physical table.
"""

from __future__ import annotations

import logging
import sys
import time

import psycopg

from perimeter import db
from perimeter.config import InvalidConfigError, MissingConfigError, ReconcilerConfig, load_reconciler_config
from perimeter.logs import configure as configure_logging
from perimeter.observability import Counters, Family, create_observability_app, serve_in_background
from perimeter.runloop import ShutdownFlag, log_and_wait_after_transient_db_error

logger = logging.getLogger("perimeter.reconciler")

# Internal only -- no host port is published for this service.
OBSERVABILITY_PORT = 8080

POLL_CYCLES = "inbox_reconciler_poll_cycles"
MESSAGES_APPLIED = "inbox_reconciler_messages_applied"

counters = Counters()


def metric_families() -> list[Family]:
    return [
        Family(POLL_CYCLES, "counter", "Loop iterations completed", counters.snapshot(POLL_CYCLES)),
        Family(MESSAGES_APPLIED, "counter", "Inbox messages turned into signals", counters.snapshot(MESSAGES_APPLIED)),
    ]


def postgres_reachable() -> bool:
    try:
        with psycopg.connect(connect_timeout=2) as probe, probe.cursor() as cursor:
            cursor.execute("select 1")
            return True
    except psycopg.Error:
        return False


def run(config: ReconcilerConfig) -> None:
    shutdown = ShutdownFlag()
    shutdown.install()

    with psycopg.connect(autocommit=True) as conn:
        # PostgreSQL is this process's only critical dependency, same as the
        # dispatcher's; none of the six published failpoints is reached here, and the
        # gate's own semantics cover that (an unmatched name is a no-op).
        serve_in_background(
            create_observability_app(readiness=postgres_reachable, metrics=metric_families),
            port=OBSERVABILITY_PORT,
        )

        while not shutdown.requested:
            try:
                applied = db.reconcile_inbox(conn, config.limit)
            except psycopg.Error as exc:
                log_and_wait_after_transient_db_error(logger, "inbox reconcile", exc, config.poll_interval_seconds)
                continue

            counters.increment(POLL_CYCLES)

            if applied:
                counters.increment(MESSAGES_APPLIED, by=applied)
                logger.info("inbox reconciled", extra={"applied": applied})
                # More RECEIVED messages may still be waiting behind this
                # call's own p_limit -- poll again immediately rather than
                # sleeping a full interval while a backlog exists.
                continue

            time.sleep(config.poll_interval_seconds)


def main() -> None:
    configure_logging()

    try:
        config = load_reconciler_config()
    except (MissingConfigError, InvalidConfigError) as exc:
        logger.error(str(exc))
        sys.exit(1)

    run(config)


if __name__ == "__main__":
    main()
