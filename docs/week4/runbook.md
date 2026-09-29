# Week 4 runbook

## Start and health

```bash
docker compose up -d --build
curl -fsS http://localhost:8080/health/live
curl -fsS http://localhost:8080/health/ready
```

`live` confirms that the gateway process can answer. `ready` confirms that the
API can reach PostgreSQL. A provider outage is not an API, worker, or dispatcher
readiness failure: durable Outbox accumulation is the expected recovery mode.

Use `make logs SERVICE=<service>` for JSON logs and `make psql` only for local
investigation. Never copy JWTs, HMAC signatures, database passwords, complete
callback bodies, payment payloads, or manual reasons into a ticket or report.

## Trace an operation

```bash
curl -fsS http://localhost:8080/api/diagnostics/trace \
  -H "Authorization: Bearer $DIAGNOSTICS_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'X-Action-Version: 1' \
  -d '{"identifier":"<known-id>"}'
```

The result is a deterministic saved-fact projection: dispatches, operation,
process and steps, jobs and attempts, Outbox, Inbox, receipt and decision.
`matchedBy` explains the matching identifier kind. Raw payloads, callback
messages, signatures and secrets are deliberately omitted. HTTP 404 with
`diagnostics.trace_not_found` means the identifier is unknown.

## Find operations stalled after delivery exhaustion

```bash
curl -fsS http://localhost:8080/api/diagnostics/stalled \
  -H "Authorization: Bearer $DIAGNOSTICS_TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'X-Action-Version: 1' \
  -d '{}'
```

`FOUND` with `{"items":[]}` is a healthy empty result. An item means delivery
reached `DEAD` while its process waits for a receipt; it does not mean the domain
operation was rejected. A late valid receipt can still confirm delivery and
advance the process.

## Recovery proof

```bash
make e2e_recovery
```

The runner creates isolated payment identifiers, waits for the structured
`failpoint.reached` acknowledgement at each published boundary, kills the target
container, restarts it without the point, and observes recovery through the public
API and read-only `autocheck` views. It performs no manual DML. Retain a failed
stack with `python3 scripts/e2e_recovery.py --keep`.
