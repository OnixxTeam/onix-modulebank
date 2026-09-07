FROM python:3.12-slim AS build
WORKDIR /app

COPY --from=ghcr.io/astral-sh/uv:0.5.11 /uv /uvx /bin/

COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project

COPY src/ src/
RUN uv sync --frozen --no-dev

FROM python:3.12-slim AS runtime
WORKDIR /app

# The venv's own console-script shebangs are absolute (baked in at `uv sync`
# time, not relocatable) -- runtime's WORKDIR has to be the build stage's own
# /app for /app/.venv/bin/outbox-dispatcher (still installed, still usable
# directly for local debugging) to resolve.
COPY --from=build /app/.venv /app/.venv
COPY --from=build /app/src /app/src

# Puts the venv's own python3 first, so `python3 -m perimeter.<module>`
# (compose.yaml's command:) runs against these installed dependencies rather
# than the base image's system interpreter.
ENV PATH="/app/.venv/bin:$PATH"

# No ENTRYPOINT/CMD: outbox-dispatcher, receipt-adapter and inbox-reconciler
# are three entrypoints of the same installed package (SPEC "Три Python-сервиса
# запускаются разными entrypoints одного локально собранного image") -- each
# compose service names the one it runs via its own `command`, as
# `python3 -m perimeter.<module>` rather than the console-script name (ticket
# 14: PID 1's process name has to read as `python3` for the public checker's
# own main-process inspection to recognise it as Python).
