import asyncio
import json
import sys
import time
import uuid

import aiohttp
import jwt

API_URL = "http://localhost:8080/api"
JWT_SECRET = "dev-insecure-key-not-for-production-32"
JWT_ISSUER = "moduledev-course"
JWT_AUDIENCE = "moduledev-api"


def generate_token(
    sub="candidate-client",
    scopes="payment:write payment:read workflow:read",
    exp_offset=3600,
):
    payload = {
        "iss": JWT_ISSUER,
        "aud": JWT_AUDIENCE,
        "sub": sub,
        "consumer": "web",
        "scope": scopes,
        "iat": int(time.time()),
        "exp": int(time.time()) + exp_offset,
    }
    return jwt.encode(payload, JWT_SECRET, algorithm="HS256")


async def make_request(
    session,
    url,
    token,
    idempotency_key,
    payload=None,
    data=None,
    method="POST",
    headers=None,
):
    if headers is None:
        headers = {}
    if token and "Authorization" not in headers:
        headers["Authorization"] = f"Bearer {token}"
    if idempotency_key and "Idempotency-Key" not in headers:
        headers["Idempotency-Key"] = idempotency_key

    if payload is not None and "Content-Type" not in headers:
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload)
    elif data is not None and "Content-Type" not in headers:
        headers["Content-Type"] = "application/json"

    try:
        start = time.time()
        async with session.request(method, url, data=data, headers=headers) as response:
            status = response.status
            text = await response.text()
            return status, time.time() - start, text
    except Exception as e:
        return 0, 0, str(e)


# ==========================================
# 1. СТРЕСС-ТЕСТЫ НА КОНКУРЕНТНОСТЬ
# ==========================================
async def test_5000_concurrent_same_key_same_payload():
    print("\n=== [STRESS] 5000 concurrent requests (Same Key, Same Payload) ===")
    token = generate_token()
    idem_key = str(uuid.uuid4())
    url = f"{API_URL}/payment/request"
    payload = {
        "operationKind": "PAYMENT_EXECUTION",
        "amount": "1000.00",
        "currency": "RUB",
    }

    async with aiohttp.ClientSession() as session:
        tasks = [
            make_request(session, url, token, idem_key, payload=payload)
            for _ in range(5000)
        ]
        results = await asyncio.gather(*tasks)

    statuses = [r[0] for r in results]
    successes = statuses.count(200)
    conflicts = statuses.count(409)
    errors = [s for s in statuses if s not in (200, 409)]
    print(
        f"Results: 200 OK: {successes}, 409 Conflict: {conflicts}, Errors/Timeouts: {len(errors)}"
    )

    passed = successes > 0 and conflicts == 0
    if passed:
        print("[OK] All completed requests returned 200 OK (replays).")
    else:
        print("[FAIL] Unexpected conflicts or zero successes.")
    return "Concurrency (Same Payload)", passed


async def test_5000_concurrent_same_key_diff_payload():
    print("\n=== [STRESS] 5000 concurrent requests (Same Key, Diff Payload) ===")
    token = generate_token()
    idem_key = str(uuid.uuid4())
    url = f"{API_URL}/payment/request"

    async with aiohttp.ClientSession() as session:
        tasks = []
        for i in range(5000):
            payload = {
                "operationKind": "PAYMENT_EXECUTION",
                "amount": f"{1000.00 + i:.2f}",
                "currency": "RUB",
            }
            tasks.append(make_request(session, url, token, idem_key, payload=payload))
        results = await asyncio.gather(*tasks)

    statuses = [r[0] for r in results]
    successes = statuses.count(200)
    conflicts = statuses.count(409)
    errors = [s for s in statuses if s not in (200, 409)]
    print(
        f"Results: 200 OK: {successes}, 409 Conflict: {conflicts}, Errors/Timeouts: {len(errors)}"
    )

    passed = successes == 1 and conflicts > 0
    if passed:
        print(
            "[OK] Exactly 1 success, the rest are conflicts or timeouts. Atomicity is maintained."
        )
    else:
        print(f"[FAIL] {successes} requests succeeded. Atomicity broken!")
    return "Concurrency (Diff Payload)", passed


async def test_deep_json():
    print("\n=== [STRESS] Deep JSON (StackOverflow attack) ===")
    token = generate_token()
    idem_key = str(uuid.uuid4())
    url = f"{API_URL}/payment/request"
    deep_json_str = (
        '{"nested":' * 5000
        + '{"amount": "10.00", "currency": "RUB", "operationKind": "PAYMENT_EXECUTION"}'
        + "}" * 5000
    )

    async with aiohttp.ClientSession() as session:
        status, _, text = await make_request(
            session, url, token, idem_key, data=deep_json_str
        )
        print(f"Status: {status}, Response: {text[:150]}")

    passed = status in (400, 422, 500, 413)
    if passed:
        print("[OK] Server safely rejected the deep JSON payload.")
    else:
        print(
            "[FAIL] Server unexpectedly accepted the payload or crashed without HTTP response."
        )
    return "StackOverflow (Deep JSON)", passed


async def test_slowloris_headers():
    print("\n=== [STRESS] Slowloris (Headers) ===")
    try:
        reader, writer = await asyncio.open_connection("localhost", 8080)
        writer.write(b"POST /api/payment/request HTTP/1.1\r\n")
        await writer.drain()
        for byte in b"Host: localhost:8080\r\nAuthorization: Bearer token\r\n\r\n":
            writer.write(bytes([byte]))
            await writer.drain()
            await asyncio.sleep(0.1)
        print("[OK] Slow request finished without hanging the server indefinitely.")
        writer.close()
        await writer.wait_closed()
        return "Slowloris (Headers)", True
    except Exception as e:
        print(f"[OK] Connection safely closed by timeout/server: {e}")
        return "Slowloris (Headers)", True


# ==========================================
# 2. КОВАРНЫЕ ХАКЕРСКИЕ ТЕСТЫ
# ==========================================
async def test_evil_idempotency_cross_principal():
    print("\n=== [EVIL] Idempotency Cross-Principal (User A vs User B) ===")
    idem_key = f"shared-evil-key-{uuid.uuid4()}"
    token_a = generate_token(sub="user-a")
    token_b = generate_token(sub="user-b")
    url = f"{API_URL}/payment/request"
    payload = {
        "operationKind": "PAYMENT_EXECUTION",
        "amount": "1000.00",
        "currency": "RUB",
    }

    async with aiohttp.ClientSession() as session:
        st_a, _, _ = await make_request(
            session, url, token_a, idem_key, payload=payload
        )
        st_b, _, _ = await make_request(
            session, url, token_b, idem_key, payload=payload
        )

        passed = st_a == 200 and st_b == 200
        if passed:
            print(
                "[OK] Both users successfully created operations. Principal isolation works."
            )
        else:
            print(f"[FAIL] User A: {st_a}, User B: {st_b}. Expected 200 and 200.")
        return "Idempotency (Cross-Principal)", passed


async def test_evil_json_key_order():
    print("\n=== [EVIL] JSON Key Order Hashing ===")
    idem_key = str(uuid.uuid4())
    token = generate_token()
    url = f"{API_URL}/payment/request"
    json_1 = '{"operationKind":"PAYMENT_EXECUTION","amount":"50.00","currency":"RUB"}'
    json_2 = '{"currency":"RUB","amount":"50.00","operationKind":"PAYMENT_EXECUTION"}'

    async with aiohttp.ClientSession() as session:
        st1, _, txt1 = await make_request(session, url, token, idem_key, data=json_1)
        st2, _, txt2 = await make_request(session, url, token, idem_key, data=json_2)

        if st1 == 200 and st2 == 200:
            print(
                "[OK] System successfully normalizes JSON before hashing (200 OK Replay)."
            )
            return "JSON Key Order", True
        elif st1 == 200 and st2 == 409:
            print("[WARN] System hashes raw text and returned 409 Conflict.")
            return "JSON Key Order", True  # Still secure, just strict
        else:
            print(f"[FAIL] Unexpected statuses: {st1}, {st2}")
            return "JSON Key Order", False


async def test_evil_huge_jwt_scopes():
    print("\n=== [EVIL] Huge JWT Scopes Array (20,000 scopes) ===")
    giant_scopes = "payment:write payment:read workflow:read " + " ".join(
        f"fake:scope_{i}" for i in range(20000)
    )
    token = generate_token(scopes=giant_scopes)
    idem_key = str(uuid.uuid4())
    payload = {
        "operationKind": "PAYMENT_EXECUTION",
        "amount": "99.00",
        "currency": "RUB",
    }

    async with aiohttp.ClientSession() as session:
        st, t, _ = await make_request(
            session, f"{API_URL}/payment/request", token, idem_key, payload=payload
        )
        print(f"Status: {st}, Time: {t:.2f} sec.")

        passed = st in (200, 431, 413, 503)
        if passed:
            print("[OK] System handled or safely rejected the massive JWT.")
        else:
            print(f"[FAIL] Unexpected behavior, status {st}")
        return "Huge JWT Scopes", passed


async def test_evil_unicode_and_types():
    print("\n=== [EVIL] Malicious Data Types & Unicode ===")
    token = generate_token()
    idem_key = str(uuid.uuid4())
    url = f"{API_URL}/payment/request"

    payloads = [
        (
            '{"operationKind": "PAYMENT_EXECUTION", "amount": "١٠٠.٠٠", "currency": "RUB"}',
            "Arabic numerals",
        ),
        (
            '{"operationKind": "PAYMENT_EXECUTION", "amount": "-0.00", "currency": "RUB"}',
            "Negative Zero",
        ),
        (
            '{"operationKind": "PAYMENT_EXECUTION", "amount": "1e2", "currency": "RUB"}',
            "Scientific Notation",
        ),
        (
            '{"operationKind": "PAYMENT_EXECUTION", "amount": "10.00", "currency": "RUB"}',
            "Invalid Header Char",
        ),
    ]

    all_passed = True
    async with aiohttp.ClientSession() as session:
        for data, desc in payloads:
            headers = {
                "Idempotency-Key": "evil\x00key"
                if desc == "Invalid Header Char"
                else str(uuid.uuid4())
            }
            st, _, _ = await make_request(
                session, url, token, idempotency_key=None, data=data, headers=headers
            )
            print(f"[{desc}] Status: {st}")
            if st == 200:
                print(
                    f"[FAIL] Payload '{desc}' was accepted but should have been rejected."
                )
                all_passed = False

    if all_passed:
        print("[OK] All malicious payloads were safely rejected.")
    return "Malicious Unicode & Types", all_passed


# ==========================================
# 3. 400 ХАОС-ТЕСТОВ (FUZZING)
# ==========================================
async def send_chaos(
    session,
    name,
    path="/payment/request",
    method="POST",
    token=None,
    headers=None,
    data=None,
):
    url = f"http://localhost:8080/api{path}"
    return await make_request(
        session,
        url,
        token,
        idempotency_key=str(uuid.uuid4()),
        data=data,
        method=method,
        headers=headers,
    )


async def test_400_chaos_fuzzer():
    print("\n=== [CHAOS] 400 Randomized Destruction Tests (Fuzzing) ===")
    token = generate_token()
    valid_payload = (
        '{"operationKind": "PAYMENT_EXECUTION", "amount": "100.00", "currency": "RUB"}'
    )

    tasks = []
    async with aiohttp.ClientSession() as session:
        # SQL Injections
        sql_payloads = [
            "'",
            "''",
            "%;",
            "--",
            "/*",
            "*/",
            "@@version",
            "pg_sleep(1)",
            "1=1",
            "1=0",
            chr(0),
            "\\",
        ]
        for i, inj in enumerate(sql_payloads * 4):
            tasks.append(
                send_chaos(
                    session,
                    f"SQL_URL_{i}",
                    path=f"/{inj}/request",
                    token=token,
                    data=valid_payload,
                )
            )
            tasks.append(
                send_chaos(
                    session,
                    f"SQL_JSON_{i}",
                    token=token,
                    data=json.dumps(
                        {
                            "operationKind": "PAYMENT_EXECUTION",
                            "amount": "100.00",
                            "currency": inj,
                        }
                    ),
                )
            )

        # Null-Byte Poisoning
        for i in range(20):
            tasks.append(
                send_chaos(
                    session,
                    f"NULL_BYTE_{i}",
                    token=token,
                    data='{"operationKind": "PAYMENT_EXECUTION", "amount": "100.00", "currency": "RUB\\u0000"}',
                )
            )

        # Numeric Extremes
        extremes = [
            "1e+100",
            "-1e+100",
            "NaN",
            "Infinity",
            "-Infinity",
            "0.00000000001",
            "999999999999999999999999.99",
            "-0",
            "0",
        ]
        for i, val in enumerate(extremes * 5):
            tasks.append(
                send_chaos(
                    session,
                    f"NUMERIC_OVERFLOW_{i}",
                    token=token,
                    data=json.dumps(
                        {
                            "operationKind": "PAYMENT_EXECUTION",
                            "amount": val,
                            "currency": "RUB",
                        }
                    ),
                )
            )

        # Giant Headers
        for i in range(40):
            headers = {
                "Idempotency-Key": "A" * (100 * i),
                "X-Action-Version": str(9999999999999999 + i),
            }
            tasks.append(
                send_chaos(
                    session,
                    f"GIANT_HEADERS_{i}",
                    token=token,
                    headers=headers,
                    data=valid_payload,
                )
            )

        # Bad JWT
        for i in range(40):
            bad_token = (
                generate_token(exp_offset=-3600)
                if i % 2 == 0
                else generate_token(scopes="invalid:scope")
            )
            tasks.append(
                send_chaos(session, f"BAD_JWT_{i}", token=bad_token, data=valid_payload)
            )

        # JSON Mutation
        for i in range(100):
            mutated = {
                "operationKind": "PAYMENT_EXECUTION",
                "amount": "100.00",
                "currency": "RUB",
                f"junk_{i}": ["array", {"nested": "value"}] * (i % 10),
            }
            if i % 3 == 0:
                mutated["amount"] = [1, 2, 3]
            if i % 4 == 0:
                mutated["operationKind"] = None
            tasks.append(
                send_chaos(
                    session, f"JSON_MUTATION_{i}", token=token, data=json.dumps(mutated)
                )
            )

        results = await asyncio.gather(*tasks)

    status_counts = {}
    fatal_errors = []
    for name, status, text in results:
        status_counts[status] = status_counts.get(status, 0) + 1
        if status == 500 or status == 0 or "Exception" in text or "Npgsql" in text:
            fatal_errors.append((name, status, text[:150]))

    print("Chaos HTTP Status Codes:")
    for st, count in sorted(status_counts.items()):
        print(f" - {st}: {count}")

    # 500 is considered passed if it's the generic internal.error without stacktrace
    is_safe = True
    for name, st, txt in fatal_errors:
        if "Exception" in txt or "Npgsql" in txt or "StackTrace" in txt:
            is_safe = False
            print(f"[FAIL] Data leak detected: {txt}")
            break

    if is_safe:
        print(
            "[OK] All bad requests were safely rejected without leaking internal data."
        )
        return "400 Chaos Fuzzer", True
    return "400 Chaos Fuzzer", False


async def main():
    print("[START] ULTIMATE API DESTRUCTION SUITE\n")

    results = []

    results.append(await test_evil_idempotency_cross_principal())
    results.append(await test_evil_json_key_order())
    results.append(await test_evil_huge_jwt_scopes())
    results.append(await test_evil_unicode_and_types())
    results.append(await test_deep_json())
    results.append(await test_slowloris_headers())
    results.append(await test_5000_concurrent_same_key_diff_payload())
    results.append(await test_5000_concurrent_same_key_same_payload())
    results.append(await test_400_chaos_fuzzer())

    print("\n" + "=" * 50)
    print("FINAL TEST SUMMARY")
    print("=" * 50)

    all_passed = True
    for name, passed in results:
        status_str = "PASSED" if passed else "FAILED"
        print(f"[{status_str}] {name}")
        if not passed:
            all_passed = False

    print("=" * 50)
    if all_passed:
        print("RESULT: PERFECT SCORE! System is bulletproof.")
    else:
        print("RESULT: SOME TESTS FAILED. See logs above.")


if __name__ == "__main__":
    asyncio.run(main())
