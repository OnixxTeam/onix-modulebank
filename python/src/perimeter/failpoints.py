"""Deterministic stop points, armed only in the closed test profile.

The python half of ticket 13. Two of the six published names belong to the
dispatcher and both sit on *generic* seams: right after the claim commits and
right after the provider answers. Neither knows which delivery it is stopping,
and there is no endpoint that arms one -- the only channel is the pair of
environment variables read before the process starts.

At an armed point the process writes exactly one JSON line and blocks until it
is killed outright, so ``reach`` never returns in production.
"""

from __future__ import annotations

import json
import os
import sys
import threading
from typing import TextIO

AFTER_JOB_CLAIM = "after_job_claim"
AFTER_ACTION_BEFORE_FINISH = "after_action_before_finish"
AFTER_OUTBOX_CLAIM = "after_outbox_claim"
AFTER_PROVIDER_RESPONSE = "after_provider_response"
AFTER_INBOX_SAVED = "after_inbox_saved"
AFTER_MANUAL_DECISION = "after_manual_decision"

NAMES = (
    AFTER_JOB_CLAIM,
    AFTER_ACTION_BEFORE_FINISH,
    AFTER_OUTBOX_CLAIM,
    AFTER_PROVIDER_RESPONSE,
    AFTER_INBOX_SAVED,
    AFTER_MANUAL_DECISION,
)


class Failpoint:
    """One armed name per process, or none at all.

    A name outside the six published ones disarms every point rather than
    failing at startup: the verifying run sets the variable through a Compose
    override, and a typo there must not take the component down.
    """

    def __init__(self, armed: str | None, instance_id: str, output: TextIO | None = None) -> None:
        self._armed = armed if armed in NAMES else None
        self._instance_id = instance_id
        self._output = output if output is not None else sys.stdout
        # Never set in production, which is what makes the block permanent. Tests
        # release it to observe the block without hanging the suite -- the same
        # role the cancellation token plays in the C# gate.
        self._released = threading.Event()

    @property
    def armed(self) -> str | None:
        return self._armed

    def reach(self, name: str) -> None:
        if self._armed is None or self._armed != name:
            return

        line = json.dumps(
            {"event": "failpoint.reached", "name": name, "instanceId": self._instance_id},
            separators=(",", ":"),
        )
        print(line, file=self._output, flush=True)
        self._released.wait()

    def release(self) -> None:
        """Test seam only: lets a reached point return instead of blocking."""
        self._released.set()


def from_environment(instance_id: str, output: TextIO | None = None) -> Failpoint:
    """Disarmed unless COURSE_TEST_PROFILE is exactly "1"."""
    armed = os.environ.get("COURSE_FAILPOINT") if os.environ.get("COURSE_TEST_PROFILE") == "1" else None
    return Failpoint(armed, instance_id, output)
