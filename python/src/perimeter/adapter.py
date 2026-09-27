"""receipt-adapter entrypoint: legacy provider callback -> signed receipt v1 -> gateway.

The thin I/O shell around ``callback``/``receipt``/``signature`` (all pure).
This module owns exactly three kinds of I/O: reading the raw HTTP request
body, one outbound HTTP call to the platform, and writing back an HTTP
response -- nothing about the callback's shape or the receipt's bytes is
decided here.

No PostgreSQL credentials of any kind reach this process (ticket 12
acceptance) -- ``config.load_adapter_config`` has no libpq variable in its
required set, unlike the dispatcher's and reconciler's.
"""

from __future__ import annotations

import hmac
import logging

import httpx
import uvicorn
from fastapi import FastAPI, Request, Response
from starlette.exceptions import HTTPException as StarletteHTTPException

from perimeter.callback import parse_callback
from perimeter.config import AdapterConfig, InvalidConfigError, MissingConfigError, load_adapter_config
from perimeter.observability import Counters, Family, mount_observability
from perimeter.receipt import build_receipt_bytes
from perimeter.signature import sign_hex

logger = logging.getLogger("perimeter.adapter")

_MAX_BODY_BYTES = 65_536
_HTTP_TIMEOUT_SECONDS = 10.0
_DEPENDENCY_UNAVAILABLE_BODY = b'{"status":"error","code":"dependency.unavailable"}'

# This process's own metrics: callbacks in, receipts forwarded by outcome. `outcome`
# is a closed set, and no identifier ever becomes a label.
CALLBACKS_RECEIVED = "receipt_adapter_callbacks_received"
RECEIPTS_FORWARDED = "receipt_adapter_receipts_forwarded"

counters = Counters()


def metric_families() -> list[Family]:
    return [
        Family(CALLBACKS_RECEIVED, "counter", "Provider callbacks accepted at the boundary", counters.snapshot(CALLBACKS_RECEIVED)),
        Family(RECEIPTS_FORWARDED, "counter", "Signed receipts forwarded to the platform, by outcome", counters.snapshot(RECEIPTS_FORWARDED)),
    ]


def gateway_reachable(config: AdapterConfig, client: httpx.Client) -> bool:
    """The adapter's one critical dependency is the gateway, not PostgreSQL.

    It holds no database credentials in any form, and the only thing it needs in
    order to do its work is the platform's own entry point. The probe reuses the
    receipt URL's origin, so there is no second piece of configuration to keep in
    step with the first.
    """
    origin = httpx.URL(config.receipt_api_url).copy_with(path="/health/live", query=None)
    try:
        return client.get(origin, timeout=2).status_code == 200
    except httpx.RequestError:
        return False


def create_app(config: AdapterConfig, client: httpx.Client) -> FastAPI:
    app = FastAPI(openapi_url=None, docs_url=None, redoc_url=None)

    # Mounted beside the callback route rather than served by a second listener:
    # this process is already an HTTP server, on the internal port the contract
    # names for it.
    mount_observability(
        app,
        readiness=lambda: gateway_reachable(config, client),
        metrics=metric_families,
    )

    # Wrong path and wrong capability must both return an empty body (SPEC
    # "Неверный path или неверный сегмент дают 404 с пустым телом"), but
    # FastAPI's own default 404 for an unmatched route carries a JSON
    # {"detail": "Not Found"} body -- this handler overrides every framework-
    # raised HTTPException with the same status and an empty body instead.
    @app.exception_handler(StarletteHTTPException)
    async def _empty_body(_: Request, exc: StarletteHTTPException) -> Response:
        return Response(status_code=exc.status_code, content=b"")

    @app.post("/callbacks/provider-v02/{capability}")
    async def handle_callback(capability: str, request: Request) -> Response:
        # The capability segment is a bearer-style secret, not a public route
        # parameter (04-week-3.md "непредсказуемый сегмент capability") --
        # compared constant-time like the HMAC signature is on the C# side.
        if not hmac.compare_digest(capability, config.capability):
            return Response(status_code=404, content=b"")

        content_length = request.headers.get("content-length")
        if content_length is None or not content_length.isdigit() or int(content_length) > _MAX_BODY_BYTES:
            return Response(status_code=400, content=b"")

        body = await request.body()
        if len(body) != int(content_length):
            return Response(status_code=400, content=b"")

        callback = parse_callback(body)
        if callback is None:
            return Response(status_code=400, content=b"")

        counters.increment(CALLBACKS_RECEIVED)

        receipt_body = build_receipt_bytes(callback)
        signature = sign_hex(config.hmac_secret, receipt_body)

        try:
            platform_response = client.post(
                config.receipt_api_url,
                content=receipt_body,
                headers={
                    "Authorization": f"Bearer {config.callback_token}",
                    "Content-Type": "application/json",
                    # messageId, not a value derived from this attempt --
                    # dedup lands on the provider payment, not the callback
                    # delivery (04-week-3.md "messageId квитанции равен
                    # providerPaymentId").
                    "Idempotency-Key": callback.provider_payment_id,
                    "X-Action-Version": "1",
                    "X-Provider-Signature": f"v1={signature}",
                },
                timeout=_HTTP_TIMEOUT_SECONDS,
            )
            counters.increment(
                RECEIPTS_FORWARDED,
                {"outcome": "accepted" if platform_response.status_code < 400 else "rejected"},
            )
        except httpx.RequestError:
            counters.increment(RECEIPTS_FORWARDED, {"outcome": "unreachable"})
            # The one response this adapter ever fabricates itself -- every
            # other status/body/content-type is passed through verbatim
            # below, since a locally invented code here would hide a
            # platform-side conflict from the provider and stop its retries
            # (spec.md "Сквозная передача ответа платформы означает именно
            # сквозную").
            return Response(status_code=503, content=_DEPENDENCY_UNAVAILABLE_BODY, media_type="application/json")

        return Response(
            status_code=platform_response.status_code,
            content=platform_response.content,
            media_type=platform_response.headers.get("content-type"),
        )

    return app


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")

    try:
        config = load_adapter_config()
    except (MissingConfigError, InvalidConfigError) as exc:
        logger.error(str(exc))
        raise SystemExit(1) from exc

    with httpx.Client(follow_redirects=False) as client:
        app = create_app(config, client)
        uvicorn.run(app, host="0.0.0.0", port=8082)


if __name__ == "__main__":
    main()
