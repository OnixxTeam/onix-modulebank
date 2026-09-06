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
from perimeter.config import MissingConfigError, ReconcilerConfig, load_reconciler_config
from perimeter.runloop import ShutdownFlag

logger = logging.getLogger("perimeter.reconciler")


def run(config: ReconcilerConfig) -> None:
    shutdown = ShutdownFlag()
    shutdown.install()

    with psycopg.connect(autocommit=True) as conn:
        while not shutdown.requested:
            applied = db.reconcile_inbox(conn, config.limit)

            if applied:
                logger.info("inbox reconciled", extra={"applied": applied})
                # More RECEIVED messages may still be waiting behind this
                # call's own p_limit -- poll again immediately rather than
                # sleeping a full interval while a backlog exists.
                continue

            time.sleep(config.poll_interval_seconds)


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    try:
        config = load_reconciler_config()
    except MissingConfigError as exc:
        logger.error(str(exc))
        sys.exit(1)

    run(config)


if __name__ == "__main__":
    main()
