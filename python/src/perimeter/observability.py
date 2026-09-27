"""The three routes every perimeter process answers, and the counters behind them.

Same contract as the C# side (``Course.Observability``): ``/health/live`` says the
process can serve a probe at all, ``/health/ready`` says its one critical dependency
answers, ``/metrics`` renders an OpenMetrics 1.0.0 document. The bodies are read
literally by the verifying run, so they are written out rather than serialized.

Each process supplies only its readiness predicate and its own metrics -- PostgreSQL
for the dispatchers and the reconcilers, the gateway for the adapter, and the
provider for nobody, because a growing Outbox is a normal mode rather than an
outage.

The dispatchers and reconcilers are loops, not servers: they mount this app on a
background thread (``serve_in_background``) and keep the loop on the main one. A
loop parked on a failpoint therefore blocks its own work and nothing else, which is
exactly what the failpoint scenarios need liveness to survive.
"""

from __future__ import annotations

import logging
import re
import threading
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass, field

import uvicorn
from fastapi import FastAPI
from fastapi.responses import Response

logger = logging.getLogger("perimeter.observability")

OPENMETRICS_MEDIA_TYPE = "application/openmetrics-text; version=1.0.0; charset=utf-8"
JSON_MEDIA_TYPE = "application/json"

_LIVE_BODY = b'{"status":"live"}'
_READY_BODY = b'{"status":"ready"}'
_NOT_READY_BODY = b'{"status":"not_ready","code":"dependency.unavailable"}'

_MAX_LABEL_VALUE_LENGTH = 64
_MAX_LABEL_VALUES_PER_SCRAPE = 20

_NAME_PATTERN = re.compile(r"^[A-Za-z_:][A-Za-z0-9_:]*$")
_LABEL_NAME_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
_UNIQUE_IDENTIFIER = re.compile(
    r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
)

_FORBIDDEN_SNAKE_CASE = (
    "correlation_id", "request_id", "operation_id", "process_id", "step_instance_id",
    "job_id", "execution_id", "attempt_id", "outbox_id", "external_request_id",
    "message_id", "decision_id",
)


def _camel_case(snake_case: str) -> str:
    head, *rest = snake_case.split("_")
    return head + "".join(part.capitalize() for part in rest)


# Both spellings the contract forbids; the camelCase half is derived so the two
# cannot drift apart.
FORBIDDEN_LABEL_NAMES = frozenset(_FORBIDDEN_SNAKE_CASE) | {
    _camel_case(name) for name in _FORBIDDEN_SNAKE_CASE
}


@dataclass(frozen=True)
class Sample:
    value: float
    labels: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class Family:
    name: str
    kind: str  # "gauge" or "counter"
    help_text: str
    samples: tuple[Sample, ...]


def render_openmetrics(families: Iterable[Family]) -> str:
    """Renders the document, or refuses to render one that breaks the contract.

    Violations raise instead of being repaired: every label value these processes
    publish comes from a closed set (an outcome, an owner name), so a violation is
    a programming error, and a document that scrapes cleanly while carrying
    unbounded cardinality fails a check much later and much less visibly.
    """
    label_values: dict[str, set[str]] = {}
    lines: list[str] = []

    for family in families:
        if not _NAME_PATTERN.match(family.name):
            raise ValueError(f"{family.name!r} is not a valid metric family name")
        if family.kind not in ("gauge", "counter"):
            raise ValueError(f"{family.kind!r} is not a supported metric type")

        suffix = "_total" if family.kind == "counter" else ""
        if suffix and family.name.endswith(suffix):
            raise ValueError(
                f"counter family {family.name!r} must be declared without the {suffix} suffix"
            )

        lines.append(f"# TYPE {family.name} {family.kind}")
        if family.help_text:
            lines.append(f"# HELP {family.name} {family.help_text}")

        for sample in family.samples:
            rendered_labels = _render_labels(sample.labels, label_values)
            lines.append(f"{family.name}{suffix}{rendered_labels} {_render_value(sample.value)}")

    for label, values in label_values.items():
        if len(values) > _MAX_LABEL_VALUES_PER_SCRAPE:
            raise ValueError(
                f"label {label!r} carries {len(values)} values in one scrape, "
                f"more than the {_MAX_LABEL_VALUES_PER_SCRAPE} the contract allows"
            )

    lines.append("# EOF")
    return "\n".join(lines) + "\n"


def _render_labels(labels: Mapping[str, str], seen: dict[str, set[str]]) -> str:
    if not labels:
        return ""

    parts = []
    for name, value in labels.items():
        if not _LABEL_NAME_PATTERN.match(name):
            raise ValueError(f"{name!r} is not a valid label name")
        if name in FORBIDDEN_LABEL_NAMES:
            raise ValueError(f"label name {name!r} names a unique identifier")
        if len(value) > _MAX_LABEL_VALUE_LENGTH:
            raise ValueError(
                f"label {name!r} has a value of {len(value)} characters, "
                f"more than the {_MAX_LABEL_VALUE_LENGTH} the contract allows"
            )
        if _UNIQUE_IDENTIFIER.match(value):
            raise ValueError(f"label {name!r} carries a unique identifier as its value")

        seen.setdefault(name, set()).add(value)
        escaped = value.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")
        parts.append(f'{name}="{escaped}"')

    return "{" + ",".join(parts) + "}"


def _render_value(value: float) -> str:
    if float(value).is_integer():
        return str(int(value))
    return repr(float(value))


class Counters:
    """The process's own counters: a small, closed set of names, counted safely.

    Every increment happens on the loop thread while every read happens on the HTTP
    thread, so the lock is not decoration.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._values: dict[tuple[str, tuple[tuple[str, str], ...]], float] = {}

    def increment(self, name: str, labels: Mapping[str, str] | None = None, by: float = 1) -> None:
        key = (name, tuple(sorted((labels or {}).items())))
        with self._lock:
            self._values[key] = self._values.get(key, 0) + by

    def snapshot(self, name: str) -> tuple[Sample, ...]:
        with self._lock:
            return tuple(
                Sample(value, dict(labels))
                for (counter, labels), value in sorted(self._values.items())
                if counter == name
            )


def create_observability_app(
    readiness: Callable[[], bool],
    metrics: Callable[[], Iterable[Family]],
) -> FastAPI:
    app = FastAPI(openapi_url=None, docs_url=None, redoc_url=None)
    mount_observability(app, readiness, metrics)
    return app


def mount_observability(
    app: FastAPI,
    readiness: Callable[[], bool],
    metrics: Callable[[], Iterable[Family]],
) -> None:
    """Adds the three routes to an app -- its own, or one that already serves traffic.

    The receipt adapter is already an HTTP server, so it mounts these beside its
    callback route instead of standing up a second listener.
    """

    # Liveness touches nothing outside the process on purpose: a probe that fails
    # when PostgreSQL is down turns one database outage into a restart loop across
    # every service that depends on it.
    @app.get("/health/live")
    def health_live() -> Response:
        return Response(content=_LIVE_BODY, media_type=JSON_MEDIA_TYPE)

    @app.get("/health/ready")
    def health_ready() -> Response:
        try:
            ready = readiness()
        except Exception:  # noqa: BLE001 -- any dependency failure means "not ready"
            logger.warning("readiness probe failed", exc_info=True)
            ready = False

        if ready:
            return Response(content=_READY_BODY, media_type=JSON_MEDIA_TYPE)
        return Response(content=_NOT_READY_BODY, media_type=JSON_MEDIA_TYPE, status_code=503)

    @app.get("/metrics")
    def metrics_document() -> Response:
        return Response(content=render_openmetrics(metrics()), media_type=OPENMETRICS_MEDIA_TYPE)


def serve_in_background(app: FastAPI, port: int) -> threading.Thread:
    """Serves the app on a daemon thread, leaving the caller's main thread free.

    A daemon thread on purpose: when the loop exits on SIGTERM the process should
    end, not linger because a server thread is still accepting probes.
    """
    server = uvicorn.Server(uvicorn.Config(app, host="0.0.0.0", port=port, log_level="warning"))
    thread = threading.Thread(target=server.run, name="observability", daemon=True)
    thread.start()
    return thread
