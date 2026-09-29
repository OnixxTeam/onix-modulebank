"""Graceful-shutdown flag and transient-error retry logging shared by the
poll-loop perimeter processes.

Both outbox-dispatcher and inbox-reconciler are "claim/reconcile, then sleep"
loops that must stop cleanly on SIGTERM/SIGINT (a container stop, not a
crash) rather than being killed mid-claim -- a bare module-level global plus
a free `_handle_shutdown` function would work per-process but duplicates
identically across both entrypoints. One small stateful object here removes
that duplication without adding any behavior neither file already had.
"""

from __future__ import annotations

import logging
import signal
import time
from types import FrameType


class ShutdownFlag:
    def __init__(self) -> None:
        self.requested = False

    def install(self) -> None:
        signal.signal(signal.SIGTERM, self._handle)
        signal.signal(signal.SIGINT, self._handle)

    def _handle(self, signum: int, frame: FrameType | None) -> None:
        self.requested = True


def log_and_wait_after_transient_db_error(
    logger: logging.Logger, description: str, error: Exception, poll_interval_seconds: float
) -> None:
    """Log a poll-iteration's PostgreSQL error and sleep before the next attempt.

    Postgres may still be mid-migration when either process starts (compose
    brings services up in parallel with cli's own bootstrap, SPEC "circuit is
    up with one command"), so a claim/reconcile failure on the very first
    iterations is not fatal -- log and retry, the same "will retry" idiom
    Workflow.Worker's own claim loop (Workflow.Worker/WorkerLoop.cs) uses on
    the C# side, rather than crashing the process outright.
    """
    logger.error("%s failed, will retry", description, extra={"error_code": type(error).__name__})
    time.sleep(poll_interval_seconds)
