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
# /app for /app/.venv/bin/outbox-dispatcher to still resolve.
COPY --from=build /app/.venv /app/.venv
COPY --from=build /app/src /app/src

ENV PATH="/app/.venv/bin:$PATH"

# No ENTRYPOINT/CMD: outbox-dispatcher, receipt-adapter and inbox-reconciler
# are three console-script entrypoints of the same installed package (SPEC
# "Три Python-сервиса запускаются разными entrypoints одного локально
# собранного image") -- each compose service names the one it runs via its
# own `command`.
