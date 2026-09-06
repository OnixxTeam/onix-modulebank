"""The dispatcher's whole database surface: three functions, nothing else.

Every call goes through ``delivery.claim_outbox``/``succeed_outbox``/
``fail_outbox`` (ticket 09) via the ``outbox_dispatcher`` role's own
connection -- no physical table is ever read or written here, matching the
role's own EXECUTE-only grants.
"""

from __future__ import annotations

from dataclasses import dataclass

import psycopg


@dataclass(frozen=True)
class ClaimedDelivery:
    outbox_id: str
    lease_version: int
    external_request_id: str
    correlation_id: str
    amount: str
    currency: str


def claim_outbox(conn: psycopg.Connection, owner: str, limit: int) -> list[ClaimedDelivery]:
    with conn.cursor() as cur:
        cur.execute(
            "select outbox_id, lease_version, external_request_id, correlation_id, amount, currency "
            "from delivery.claim_outbox(%s, %s)",
            (owner, limit),
        )
        rows = cur.fetchall()

    return [
        ClaimedDelivery(
            outbox_id=str(row[0]),
            lease_version=row[1],
            external_request_id=row[2],
            correlation_id=str(row[3]),
            amount=row[4],
            currency=row[5],
        )
        for row in rows
    ]


def succeed_outbox(conn: psycopg.Connection, outbox_id: str, owner: str, lease_version: int, provider_payment_id: str) -> None:
    with conn.cursor() as cur:
        cur.execute(
            "select delivery.succeed_outbox(%s, %s, %s, %s)",
            (outbox_id, owner, lease_version, provider_payment_id),
        )


def fail_outbox(conn: psycopg.Connection, outbox_id: str, owner: str, lease_version: int, error_code: str) -> None:
    with conn.cursor() as cur:
        cur.execute(
            "select delivery.fail_outbox(%s, %s, %s, %s)",
            (outbox_id, owner, lease_version, error_code),
        )
