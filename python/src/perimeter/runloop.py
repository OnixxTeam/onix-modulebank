"""Graceful-shutdown flag shared by the poll-loop perimeter processes.

Both outbox-dispatcher and inbox-reconciler are "claim/reconcile, then sleep"
loops that must stop cleanly on SIGTERM/SIGINT (a container stop, not a
crash) rather than being killed mid-claim -- a bare module-level global plus
a free `_handle_shutdown` function would work per-process but duplicates
identically across both entrypoints. One small stateful object here removes
that duplication without adding any behavior neither file already had.
"""

from __future__ import annotations

import signal
from types import FrameType


class ShutdownFlag:
    def __init__(self) -> None:
        self.requested = False

    def install(self) -> None:
        signal.signal(signal.SIGTERM, self._handle)
        signal.signal(signal.SIGINT, self._handle)

    def _handle(self, signum: int, frame: FrameType | None) -> None:
        self.requested = True
