#!/usr/bin/env python3
"""Architecture-neutral supplementary black-box checks for ModuleDev week 1."""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import datetime as dt
import hashlib
import hmac
import json
import os
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


@dataclass
class Result:
    command: list[str]
    exitCode: int
    stdout: str
    stderr: str


@dataclass
class Response:
    status: int
    body: Any
    text: str
    error: str | None = None


@dataclass
class Check:
    name: str
    group: str
    passed: bool
    expected: Any
    actual: Any


def b64(value: bytes) -> str:
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def token(secret: str, *, sub: Any = "quality-user", consumer: Any = "quality-client",
          scope: Any = "assurance:execute tenant:read", issuer: Any = "moduledev-course",
          audience: Any = "moduledev-api", expired: bool = False) -> str:
    now = int(time.time())
    header = b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    claims = {"iss": issuer, "aud": audience, "sub": sub, "consumer": consumer,
              "scope": scope, "iat": now - 120 if expired else now,
              "exp": now - 60 if expired else now + 1800}
    payload = b64(json.dumps(claims, separators=(",", ":")).encode())
    signature = b64(hmac.new(secret.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest())
    return f"{header}.{payload}.{signature}"


class Checker:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.repo = args.repo.resolve()
        self.fixtures = args.fixtures.resolve()
        self.secret = secrets.token_urlsafe(48)
        self.temp = Path(tempfile.mkdtemp(prefix="moduledev-quality-"))
        self.override = self.temp / "quality.override.yaml"
        self.checks: list[Check] = []
        self.commands: list[Result] = []
        self.started = dt.datetime.now(dt.UTC)
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.compose_file = next((self.repo / name for name in
            ("compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml")
            if (self.repo / name).is_file()), None)
        if self.compose_file is None:
            raise ValueError("root Compose file not found")

    def check(self, name: str, group: str, passed: bool, expected: Any, actual: Any) -> None:
        self.checks.append(Check(name, group, bool(passed), expected, actual))

    def command(self, command: list[str], timeout: int = 180) -> Result:
        try:
            run = subprocess.run(command, cwd=self.repo, text=True, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE, timeout=timeout, check=False)
            result = Result(command, run.returncode, run.stdout, run.stderr)
        except subprocess.TimeoutExpired as error:
            result = Result(command, 124, error.stdout or "", (error.stderr or "") + "\ntimeout")
        except OSError as error:
            result = Result(command, 127, "", str(error))
        self.commands.append(result)
        return result

    def compose(self, *args: str, timeout: int = 180) -> Result:
        prefix = ["bash", str(self.args.compose_wrapper.resolve())] if self.args.compose_wrapper else ["docker", "compose"]
        return self.command([*prefix, "--project-name", self.args.project_name, "-f",
                             str(self.compose_file), "-f", str(self.override), *args], timeout)

    def cli(self, *args: str, mount: Path | None = None) -> tuple[Result, Any]:
        source = (mount or self.fixtures).resolve()
        result = self.compose("run", "--rm", "-T", "--no-deps", "-v",
            f"{source}:/quality/input:ro", "cli", *args)
        try:
            body = json.loads(result.stdout)
        except (json.JSONDecodeError, TypeError):
            body = None
        return result, body

    @staticmethod
    def envelope(body: Any, status: str) -> bool:
        return (isinstance(body, dict) and body.get("status") == status
                and isinstance(body.get("meta"), dict)
                and body["meta"].get("contractVersion") == "course-1")

    def request(self, method: str, path: str, payload: Any = None, bearer: str | None = None,
                key: str | None = None, version: Any = None, raw: bytes | None = None,
                timeout: float = 10) -> Response:
        headers: dict[str, str] = {}
        data = raw
        if payload is not None:
            data = json.dumps(payload, separators=(",", ":")).encode()
        if data is not None:
            headers["Content-Type"] = "application/json"
        if bearer:
            headers["Authorization"] = f"Bearer {bearer}"
        if key:
            headers["Idempotency-Key"] = key
        if version is not None:
            headers["X-Action-Version"] = str(version)
        req = urllib.request.Request(self.args.api_url.rstrip("/") + path, data=data,
                                     headers=headers, method=method)
        try:
            with self.opener.open(req, timeout=timeout) as answer:
                status, content = answer.status, answer.read(1024 * 1024 + 1)
        except urllib.error.HTTPError as error:
            status, content = error.code, error.read(1024 * 1024 + 1)
        except (OSError, TimeoutError, urllib.error.URLError) as error:
            return Response(0, None, "", str(error))
        text = content.decode(errors="replace")
        try:
            body = json.loads(text)
        except json.JSONDecodeError:
            body = None
        return Response(status, body, text, "response too large" if len(content) > 1024 * 1024 else None)

    def wait_ready(self) -> Response:
        end = time.monotonic() + self.args.ready_timeout
        response = Response(0, None, "")
        while time.monotonic() < end:
            response = self.request("GET", "/health/ready", timeout=2)
            if response.status == 200:
                return response
            time.sleep(.25)
        return response

    def psql(self, sql: str) -> Result:
        return self.compose("exec", "-T", "postgres", "psql", "-X", "-A", "-t",
                            "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "course", "-c", sql, timeout=60)

    def count(self, marker: str) -> int | None:
        safe = marker.replace("'", "''")
        result = self.psql(f"SELECT count(*) FROM api.quality_assurance_evidence WHERE marker='{safe}';")
        try:
            return int(result.stdout.strip()) if result.exitCode == 0 else None
        except ValueError:
            return None

    @staticmethod
    def code(response: Response) -> Any:
        return response.body.get("code") if isinstance(response.body, dict) else None

    @staticmethod
    def result(response: Response) -> dict[str, Any]:
        value = response.body.get("result") if isinstance(response.body, dict) else None
        return value if isinstance(value, dict) else {}

    def start(self) -> bool:
        self.override.write_text("services:\n  api:\n    environment:\n"
            "      COURSE_JWT_ISSUER: moduledev-course\n      COURSE_JWT_AUDIENCE: moduledev-api\n"
            f"      COURSE_JWT_SIGNING_KEY: {json.dumps(self.secret)}\n", encoding="utf-8")
        build = self.compose("build", timeout=self.args.build_timeout) if not self.args.skip_build else Result([], 0, "", "")
        self.check("quality-compose-build", "infrastructure", build.exitCode == 0, 0, build.exitCode)
        if build.exitCode:
            return False
        up = self.compose("up", "-d", "--force-recreate", timeout=600)
        ready = self.wait_ready() if up.exitCode == 0 else Response(0, None, "")
        self.check("quality-compose-up", "infrastructure", up.exitCode == 0, 0, up.exitCode)
        self.check("quality-ready", "infrastructure", ready.status == 200, 200, asdict(ready))
        return up.exitCode == 0 and ready.status == 200

    def cli_contract(self) -> None:
        path = "/quality/input/manifests/assurance-execute-v1.action.json"
        before, before_body = self.cli("action", "list")
        validate, validate_body = self.cli("action", "validate", path)
        after, after_body = self.cli("action", "list")
        unchanged = before_body == after_body
        self.check("cli-action-validate-no-mutation", "cli",
            validate.exitCode == 0 and self.envelope(validate_body, "ok") and before.exitCode == after.exitCode == 0 and unchanged,
            "one JSON document and unchanged action list", {"validate": validate_body, "unchanged": unchanged})

        migration, migration_body = self.cli("migration", "apply", "/quality/input/migrations")
        repeat, repeat_body = self.cli("migration", "apply", "/quality/input/migrations")
        self.check("cli-migration-apply-and-repeat", "cli",
            migration.exitCode == repeat.exitCode == 0 and self.envelope(migration_body, "ok") and self.envelope(repeat_body, "ok"),
            "both calls succeed", {"first": migration_body, "repeat": repeat_body})

        changed = self.temp / "changed" / "migrations"
        changed.mkdir(parents=True)
        source = self.fixtures / "migrations" / "910_quality_assurance.sql"
        (changed / source.name).write_text(source.read_text(encoding="utf-8") + "\n-- changed\n", encoding="utf-8")
        conflict, conflict_body = self.cli("migration", "apply", "/quality/input/migrations", mount=changed.parent)
        self.check("cli-migration-checksum-conflict", "cli",
            conflict.exitCode != 0 and self.envelope(conflict_body, "error") and conflict_body.get("code") == "migration.conflict",
            "migration.conflict", conflict_body)

        invalid_dir = self.temp / "invalid"
        invalid_dir.mkdir()
        invalid_manifest = json.loads((self.fixtures / "manifests" / "assurance-execute-v1.action.json").read_text(encoding="utf-8"))
        invalid_manifest["http_method"] = "GET"
        (invalid_dir / "random-name.json").write_text(json.dumps(invalid_manifest), encoding="utf-8")
        invalid, invalid_body = self.cli("action", "validate", "/quality/input/random-name.json", mount=invalid_dir)
        self.check("cli-action-validate-invalid", "cli", invalid.exitCode != 0 and self.envelope(invalid_body, "error")
            and invalid_body.get("code") == "manifest.invalid", "manifest.invalid", invalid_body)

        for name in ("assurance-execute-v1", "assurance-execute-v2", "assurance-optional", "assurance-global"):
            published, body = self.cli("action", "publish", f"/quality/input/manifests/{name}.action.json")
            self.check(f"cli-action-publish-{name}", "cli", published.exitCode == 0 and self.envelope(body, "ok"), "published", body)
        repeated, repeated_body = self.cli("action", "publish", path)
        self.check("cli-action-publish-idempotent", "cli", repeated.exitCode == 0 and self.envelope(repeated_body, "ok"), "safe repeat", repeated_body)

        immutable_dir = self.temp / "immutable"
        immutable_dir.mkdir()
        changed_manifest = json.loads((self.fixtures / "manifests" / "assurance-execute-v1.action.json").read_text(encoding="utf-8"))
        changed_manifest["timeout_ms"] += 1
        (immutable_dir / "anything.json").write_text(json.dumps(changed_manifest), encoding="utf-8")
        immutable, immutable_body = self.cli("action", "publish", "/quality/input/anything.json", mount=immutable_dir)
        self.check("cli-action-publish-immutable", "cli", immutable.exitCode != 0 and self.envelope(immutable_body, "error")
            and immutable_body.get("code") == "manifest.conflict", "manifest.conflict", immutable_body)

        listed, list_body = self.cli("action", "list")
        items = list_body.get("result", {}).get("items", []) if isinstance(list_body, dict) else []
        assurance = [x for x in items if isinstance(x, dict) and x.get("module") == "assurance"]
        identities = {(x.get("action"), x.get("version")) for x in assurance}
        self.check("cli-action-list", "cli", listed.exitCode == 0 and self.envelope(list_body, "ok")
            and {("execute", 1), ("execute", 2), ("optional", 1), ("global", 1)} <= identities,
            "all published versions with state", assurance)

        for index, arguments in enumerate((("action",), ("action", "activate", "bad-key", "--version", "x"),
                                           ("migration", "apply"), ("unknown", "command")), start=1):
            malformed, malformed_body = self.cli(*arguments)
            self.check(f"cli-invalid-command-{index}", "cli", malformed.exitCode != 0
                and self.envelope(malformed_body, "error") and malformed_body.get("code") == "request.invalid",
                "non-zero request.invalid JSON", {"arguments": arguments, "body": malformed_body})

        transactional = self.temp / "transactional-migrations" / "migrations"
        transactional.mkdir(parents=True)
        (transactional / "920_quality_order.sql").write_text(
            "CREATE TABLE quality_assurance.migration_order(value text PRIMARY KEY);\n"
            "INSERT INTO quality_assurance.migration_order VALUES ('first');\n", encoding="utf-8")
        (transactional / "921_quality_failure.sql").write_text(
            "INSERT INTO quality_assurance.migration_order VALUES ('rolled-back');\n"
            "SELECT definitely_missing_function();\n", encoding="utf-8")
        (transactional / "922_quality_after.sql").write_text(
            "INSERT INTO quality_assurance.migration_order VALUES ('must-not-run');\n", encoding="utf-8")
        failed, failed_body = self.cli("migration", "apply", "/quality/input/migrations", mount=transactional.parent)
        evidence = self.psql("SELECT coalesce(string_agg(value,',' ORDER BY value),'') FROM quality_assurance.migration_order;")
        self.check("migration-file-transaction-and-fail-fast", "cli", failed.exitCode != 0
            and self.envelope(failed_body, "error") and evidence.exitCode == 0 and evidence.stdout.strip() == "first",
            "first file committed; failing file rolled back; later file skipped",
            {"body": failed_body, "values": evidence.stdout.strip()})

        duplicate_dir = self.temp / "duplicate-default"
        duplicate_dir.mkdir()
        duplicate = json.loads((self.fixtures / "manifests" / "assurance-execute-v2.action.json").read_text(encoding="utf-8"))
        duplicate["version"], duplicate["enabled"], duplicate["is_default"] = 3, True, True
        (duplicate_dir / "duplicate.json").write_text(json.dumps(duplicate), encoding="utf-8")
        duplicate_result, duplicate_body = self.cli("action", "publish", "/quality/input/duplicate.json", mount=duplicate_dir)
        listed_after, listed_after_body = self.cli("action", "list")
        after_items = listed_after_body.get("result", {}).get("items", []) if isinstance(listed_after_body, dict) else []
        self.check("publish-second-default-rolls-back", "cli", duplicate_result.exitCode != 0
            and self.envelope(duplicate_body, "error") and duplicate_body.get("code") == "manifest.conflict"
            and not any(x.get("module") == "assurance" and x.get("action") == "execute" and x.get("version") == 3
                        for x in after_items if isinstance(x, dict)),
            "manifest.conflict and version 3 absent", {"body": duplicate_body, "listed": listed_after.exitCode})

    def dynamic_publication(self) -> None:
        suffix = uuid.uuid4().hex[:10]
        module, action = f"portable_{suffix}", f"probe_{suffix}"
        root = self.temp / "portable-manifest"
        root.mkdir()
        manifest = json.loads((self.fixtures / "manifests" / "assurance-optional.action.json").read_text(encoding="utf-8"))
        manifest.update({"module": module, "action": action, "idempotency_mode": "none", "idempotency_scope": "none"})
        arbitrary = root / f"unrelated-filename-{uuid.uuid4().hex}.json"
        arbitrary.write_text(json.dumps(manifest), encoding="utf-8")
        images_before = {name: self.compose("images", "-q", name).stdout.strip() for name in ("gateway", "api")}
        validated, validate_body = self.cli("action", "validate", f"/quality/input/{arbitrary.name}", mount=root)
        published, publish_body = self.cli("action", "publish", f"/quality/input/{arbitrary.name}", mount=root)
        images_after = {name: self.compose("images", "-q", name).stdout.strip() for name in ("gateway", "api")}
        response = self.request("POST", f"/api/{module}/{action}", {"value": suffix}, token(self.secret))
        exact = self.request("GET", f"/openapi/actions/{module}/{action}/1.json")
        self.check("unknown-action-published-from-arbitrary-filename", "publication",
            validated.exitCode == published.exitCode == 0 and self.envelope(validate_body, "ok")
            and self.envelope(publish_body, "ok") and publish_body.get("result", {}).get("key") == f"{module}.{action}"
            and response.status == 200 and self.result(response).get("value") == suffix
            and exact.status == 200 and images_before == images_after,
            "new route works without image rebuild and filename-derived identity",
            {"validate": validate_body, "publish": publish_body, "http": asdict(response),
             "openapi": exact.status, "imagesUnchanged": images_before == images_after})

    def http_contract(self) -> None:
        valid = token(self.secret)
        for name, bearer in {
            "missing": None, "expired": token(self.secret, expired=True),
            "wrong-signature": token("x" * 40), "numeric-sub": token(self.secret, sub=42),
            "array-scope": token(self.secret, scope=["assurance:execute", "tenant:read"]),
            "wrong-issuer": token(self.secret, issuer="other"), "wrong-audience": token(self.secret, audience="other")}.items():
            response = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "auth"}, bearer, str(uuid.uuid4()))
            self.check(f"auth-{name}", "security", response.status == 401 and self.code(response) == "auth.invalid",
                       {"http": 401, "code": "auth.invalid"}, asdict(response))

        marker = uuid.uuid4().hex
        response = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": marker}, valid, uuid.uuid4().hex)
        value = self.result(response)
        context_ok = (value.get("principal") == "quality-user" and value.get("consumer") == "quality-client"
            and value.get("scopes") == ["assurance:execute", "tenant:read"])
        try:
            uuid.UUID(value.get("correlationId", "")); dt.datetime.fromisoformat(value.get("deadline", "").replace("Z", "+00:00"))
        except (ValueError, TypeError):
            context_ok = False
        self.check("server-created-trusted-context", "security", response.status == 200 and context_ok,
                   "JWT-derived context with UUID/deadline", asdict(response))

        second = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": uuid.uuid4().hex},
                              valid, uuid.uuid4().hex)
        first_correlation = response.body.get("meta", {}).get("correlationId") if isinstance(response.body, dict) else None
        second_correlation = second.body.get("meta", {}).get("correlationId") if isinstance(second.body, dict) else None
        first_version = response.body.get("meta", {}).get("actionVersion") if isinstance(response.body, dict) else None
        second_version = second.body.get("meta", {}).get("actionVersion") if isinstance(second.body, dict) else None
        try:
            meta_ok = bool(uuid.UUID(first_correlation) and uuid.UUID(second_correlation))
        except (ValueError, TypeError, AttributeError):
            meta_ok = False
        self.check("success-envelope-meta-and-correlation", "contract", second.status == 200 and meta_ok
            and first_correlation != second_correlation and first_version == second_version == 1,
            "meta.actionVersion and a different meta.correlationId UUID for every request",
            {"first": first_correlation, "second": second_correlation,
             "firstActionVersion": first_version, "secondActionVersion": second_version})

        cases = [
            ("malformed-json", None, b"{", 400, "request.invalid"),
            ("array-payload", None, b"[]", 400, "request.invalid"),
            ("extra-property", {"mode": "ok", "value": "x", "principal": "attacker"}, None, 422, "payload.invalid"),
            ("missing-key", {"mode": "ok", "value": "x"}, None, 400, "idempotency.required")]
        for name, payload, raw, status, code in cases:
            response = self.request("POST", "/api/assurance/execute", payload, valid,
                None if name == "missing-key" else uuid.uuid4().hex, raw=raw)
            self.check(name, "contract", response.status == status and self.code(response) == code,
                       {"http": status, "code": code}, asdict(response))
        for version in ("0", "-1", "abc", "1.5"):
            response = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "bad-version"}, valid, uuid.uuid4().hex, version)
            self.check(f"invalid-version-{version}", "contract", response.status == 400 and self.code(response) == "request.invalid",
                       {"http": 400, "code": "request.invalid"}, asdict(response))

        for name, payload in (("missing-required", {"mode": "ok"}),
                              ("wrong-property-type", {"mode": "ok", "value": 7}),
                              ("unknown-enum", {"mode": "surprise", "value": "x"}),
                              ("max-length", {"mode": "ok", "value": "x" * 65})):
            response = self.request("POST", "/api/assurance/execute", payload, valid, uuid.uuid4().hex)
            self.check(f"request-schema-{name}", "contract", response.status == 422
                and self.code(response) == "payload.invalid", {"http": 422, "code": "payload.invalid"}, asdict(response))

        for mode, status, code in (("error", 400, "assurance.rejected"), ("unknown_outcome", 500, "action.contract_violation"),
                                   ("invalid_result", 500, "action.contract_violation"), ("timeout", 504, "action.timeout")):
            marker = f"{mode}-{uuid.uuid4().hex[:12]}"
            response = self.request("POST", "/api/assurance/execute", {"mode": mode, "value": marker}, valid, uuid.uuid4().hex)
            self.check(f"rollback-{mode}", "transaction", response.status == status and self.code(response) == code and self.count(marker) == 0,
                       {"http": status, "code": code, "effects": 0}, {"response": asdict(response), "effects": self.count(marker)})

        marker, key = uuid.uuid4().hex, uuid.uuid4().hex
        calls = lambda: self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": marker}, valid, key)
        with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
            responses = list(pool.map(lambda _: calls(), range(24)))
        self.check("concurrent-idempotency", "idempotency", all(x.status == 200 for x in responses) and self.count(marker) == 1,
                   "24 successes and one effect", {"statuses": [x.status for x in responses], "effects": self.count(marker)})
        changed = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": marker + "x"}, valid, key)
        self.check("idempotency-payload-conflict", "idempotency", changed.status == 409 and self.code(changed) == "idempotency.conflict",
                   {"http": 409, "code": "idempotency.conflict"}, asdict(changed))

        canonical_marker, canonical_key = uuid.uuid4().hex, uuid.uuid4().hex
        canonical_a = self.request("POST", "/api/assurance/execute", bearer=valid, key=canonical_key,
            raw=f'{{"mode":"ok","value":"{canonical_marker}"}}'.encode())
        canonical_b = self.request("POST", "/api/assurance/execute", bearer=valid, key=canonical_key,
            raw=f'{{ "value" : "{canonical_marker}", "mode" : "ok" }}'.encode())
        self.check("idempotency-canonical-json", "idempotency", canonical_a.status == canonical_b.status == 200
            and canonical_a.body == canonical_b.body and self.count(canonical_marker) == 1,
            "property order/whitespace replay the original envelope",
            {"sameEnvelope": canonical_a.body == canonical_b.body, "effects": self.count(canonical_marker)})

        principal_marker, principal_key = uuid.uuid4().hex, uuid.uuid4().hex
        principal_a = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": principal_marker},
            token(self.secret, sub="principal-one"), principal_key)
        principal_b = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": principal_marker},
            token(self.secret, sub="principal-two"), principal_key)
        self.check("principal-action-idempotency-scope", "idempotency", principal_a.status == principal_b.status == 200
            and self.count(principal_marker) == 2, "same key executes once per principal", {"effects": self.count(principal_marker)})

        denied = token(self.secret, scope="assurance:execute")
        denied_marker = uuid.uuid4().hex
        denied_response = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": denied_marker},
                                       denied, uuid.uuid4().hex)
        self.check("all-policy-scopes-required", "security", denied_response.status == 403
            and self.code(denied_response) == "access.denied" and self.count(denied_marker) == 0,
            {"http": 403, "code": "access.denied", "effects": 0}, asdict(denied_response))

        optional_marker = uuid.uuid4().hex
        optional_a = self.request("POST", "/api/assurance/optional", {"value": optional_marker}, valid)
        optional_b = self.request("POST", "/api/assurance/optional", {"value": optional_marker}, valid)
        self.check("optional-idempotency-without-key", "idempotency", optional_a.status == optional_b.status == 200
            and self.count(optional_marker) == 2, "two calls, two effects", {"effects": self.count(optional_marker)})

        consumer_marker, consumer_key = uuid.uuid4().hex, uuid.uuid4().hex
        consumer_a = self.request("POST", "/api/assurance/optional", {"value": consumer_marker},
            token(self.secret, sub="one", consumer="shared"), consumer_key)
        consumer_replay = self.request("POST", "/api/assurance/optional", {"value": consumer_marker},
            token(self.secret, sub="two", consumer="shared"), consumer_key)
        consumer_other = self.request("POST", "/api/assurance/optional", {"value": consumer_marker},
            token(self.secret, sub="two", consumer="other"), consumer_key)
        self.check("consumer-action-idempotency-scope", "idempotency",
            all(x.status == 200 for x in (consumer_a, consumer_replay, consumer_other)) and self.count(consumer_marker) == 2,
            "same consumer replays; another consumer executes", {"effects": self.count(consumer_marker)})

        global_marker, global_key = uuid.uuid4().hex, uuid.uuid4().hex
        global_a = self.request("POST", "/api/assurance/global", {"value": global_marker},
            token(self.secret, sub="global-one", consumer="one"), global_key)
        global_replay = self.request("POST", "/api/assurance/global", {"value": global_marker},
            token(self.secret, sub="global-two", consumer="two"), global_key)
        self.check("global-action-idempotency-scope", "idempotency", global_a.status == global_replay.status == 200
            and self.count(global_marker) == 1, "different identities replay globally", {"effects": self.count(global_marker)})

        for method, path, expected in (("GET", "/api/assurance/execute", 405),
                                       ("POST", "/api/assurance/execute/extra", 404),
                                       ("GET", "/admin", 404)):
            surface = self.request(method, path, bearer=valid)
            self.check(f"gateway-surface-{method.lower()}-{path.replace('/', '-')}", "gateway",
                surface.status == expected, expected, asdict(surface))

        sensitive = ("quality_assurance", "execute_v1", "host=", "npgsql", "stack trace", "postgresexception")
        violation = self.request("POST", "/api/assurance/execute", {"mode": "invalid_result", "value": uuid.uuid4().hex},
                                 valid, uuid.uuid4().hex)
        rendered = violation.text.casefold()
        error_shape = isinstance(violation.body, dict) and set(violation.body) == {
            "status", "code", "message", "retryable", "details", "meta"}
        self.check("error-envelope-does-not-leak-internals", "security", violation.status == 500 and error_shape
            and not any(value in rendered for value in sensitive),
            "exact envelope without SQL/target/stack details", asdict(violation))

    def database_boundary(self) -> None:
        marker = uuid.uuid4().hex
        context = json.dumps({"principal": "direct", "consumer": "direct", "scopes": [],
            "correlationId": str(uuid.uuid4()), "requestId": uuid.uuid4().hex,
            "deadline": (dt.datetime.now(dt.UTC) + dt.timedelta(seconds=2)).isoformat()})
        payload = json.dumps({"mode": "ok", "value": marker})
        quote = lambda value: "'" + value.replace("'", "''") + "'"
        direct = self.psql("SET ROLE course_runtime; SELECT api.invoke('assurance','execute',1,"
            f"{quote(context)}::jsonb,{quote(payload)}::jsonb)::text;")
        self.check("database-policy-recheck", "database", direct.exitCode == 0 and "access.denied" in direct.stdout
            and self.count(marker) == 0, "api.invoke denies missing scopes with no effect",
            {"exitCode": direct.exitCode, "stdout": direct.stdout, "effects": self.count(marker)})
        target = self.psql("SET ROLE course_runtime; SELECT quality_assurance.execute_v1('{}','{}');")
        mutate = self.psql("SET ROLE course_runtime; UPDATE api.quality_assurance_evidence SET marker='forbidden';")
        owner = self.psql("SELECT rolcanlogin::text FROM pg_roles WHERE rolname='course_owner';")
        self.check("runtime-cannot-bypass-invoke", "database", target.exitCode != 0, "direct target denied", target.stderr[-512:])
        self.check("runtime-cannot-mutate-evidence", "database", mutate.exitCode != 0, "view mutation denied", mutate.stderr[-512:])
        self.check("security-definer-owner-no-login", "database", owner.exitCode == 0 and owner.stdout.strip() == "false",
                   "course_owner rolcanlogin=false", owner.stdout.strip())

    def evidence_contract(self) -> None:
        expected = {
            "contract_info": {"contract_version": "text", "generated_at": "timestamp with time zone"},
            "action_definitions": {"module": "text", "action": "text", "version": "integer", "http_method": "text",
                "target_schema": "text", "target_function": "text", "outcomes": "jsonb", "enabled": "boolean", "is_default": "boolean"},
            "action_dispatches": {"correlation_id": "uuid", "request_id": "text", "module": "text", "action": "text",
                "version": "integer", "principal": "text", "payload_hash": "text", "status": "text", "outcome": "text",
                "occurred_at": "timestamp with time zone"},
            "operations": {"operation_id": "uuid", "request_id": "text", "operation_kind": "text", "amount": "numeric",
                "currency": "text", "status": "text", "process_id": "uuid", "created_at": "timestamp with time zone",
                "updated_at": "timestamp with time zone"},
            "operation_events": {"event_id": "uuid", "operation_id": "uuid", "event_type": "text", "payload_hash": "text",
                "occurred_at": "timestamp with time zone"}}
        sql = "SELECT table_name,column_name,data_type FROM information_schema.columns WHERE table_schema='autocheck' ORDER BY 1,ordinal_position;"
        columns = self.psql(sql)
        actual: dict[str, dict[str, str]] = {}
        for line in columns.stdout.splitlines():
            parts = line.split("|")
            if len(parts) == 3:
                actual.setdefault(parts[0], {})[parts[1]] = parts[2]
        info = self.psql("SELECT count(*)::text||':'||min(contract_version) FROM autocheck.contract_info;")
        self.check("autocheck-view-columns-and-types", "evidence", columns.exitCode == 0
            and all(actual.get(view) == fields for view, fields in expected.items()), expected, actual)
        self.check("autocheck-contract-info-single-row", "evidence", info.exitCode == 0
            and info.stdout.strip() == "1:course-1", "1:course-1", info.stdout.strip())
        hashes = self.psql("SELECT count(*) FROM autocheck.action_dispatches WHERE payload_hash !~ '^[0-9a-f]{64}$';")
        enums = self.psql("SELECT count(*) FROM autocheck.action_dispatches WHERE status NOT IN ('OK','ERROR') OR outcome IS NOT NULL AND outcome !~ '^[A-Z][A-Z0-9_]*$';")
        self.check("evidence-hashes-and-uppercase-values", "evidence", hashes.exitCode == enums.exitCode == 0
            and hashes.stdout.strip() == enums.stdout.strip() == "0", "no invalid hashes/status/outcomes",
            {"hashViolations": hashes.stdout.strip(), "enumViolations": enums.stdout.strip()})

    def payment_boundaries(self) -> None:
        bearer = token(self.secret, sub="payment-quality", consumer="web", scope="payment:write payment:read")
        valid_amounts = ("0.01", "0.10", "1", "1.2", "9999999999999999.99")
        for amount in valid_amounts:
            key = f"amount-{amount}-{uuid.uuid4().hex}"
            response = self.request("POST", "/api/payment/request",
                {"operationKind": "PAYMENT_APPROVAL", "amount": amount, "currency": "RUB"}, bearer, key)
            result = self.result(response)
            self.check(f"payment-valid-amount-{amount}", "payment", response.status == 200
                and result.get("amount") == amount and result.get("requestId") == key,
                "200 preserving contractual amount string", asdict(response))
        invalid_amounts = ("0", "0.00", ".01", "01.00", "1.000", "1e2", "10000000000000000.00", 1.25)
        for index, amount in enumerate(invalid_amounts):
            response = self.request("POST", "/api/payment/request",
                {"operationKind": "PAYMENT_EXECUTION", "amount": amount, "currency": "RUB"}, bearer, uuid.uuid4().hex)
            self.check(f"payment-invalid-amount-{index}", "payment", response.status == 422
                and self.code(response) == "payload.invalid", {"http": 422, "code": "payload.invalid"}, asdict(response))
        for name, payload in (("currency", {"operationKind": "PAYMENT_EXECUTION", "amount": "1.00", "currency": "USD"}),
                              ("kind", {"operationKind": "TRANSFER", "amount": "1.00", "currency": "RUB"}),
                              ("injected-state", {"operationKind": "PAYMENT_EXECUTION", "amount": "1.00", "currency": "RUB", "status": "COMPLETED"})):
            response = self.request("POST", "/api/payment/request", payload, bearer, uuid.uuid4().hex)
            self.check(f"payment-rejects-{name}", "payment", response.status == 422 and self.code(response) == "payload.invalid",
                       {"http": 422, "code": "payload.invalid"}, asdict(response))
        unknown = self.request("POST", "/api/operation/get", {"operationId": str(uuid.uuid4())}, bearer)
        malformed = self.request("POST", "/api/operation/get", {"operationId": "not-a-uuid"}, bearer)
        self.check("operation-get-unknown-is-controlled", "payment", unknown.status != 500
            and self.code(unknown) == "operation.not_found", "controlled operation.not_found, not 500", asdict(unknown))
        self.check("operation-get-invalid-uuid", "payment", malformed.status == 422
            and self.code(malformed) == "payload.invalid", {"http": 422, "code": "payload.invalid"}, asdict(malformed))

    def lifecycle(self) -> None:
        valid = token(self.secret)
        disabled = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "disabled"}, valid, version=2)
        self.check("disabled-explicit-version", "lifecycle", disabled.status == 404 and self.code(disabled) == "action.not_found",
                   {"http": 404, "code": "action.not_found"}, asdict(disabled))
        exact_v1 = self.request("GET", "/openapi/actions/assurance/execute/1.json")
        default_before = self.request("GET", "/openapi/default.json")
        before_operation = (default_before.body or {}).get("paths", {}).get("/api/assurance/execute", {}).get("post", {})
        self.check("openapi-exact-and-default-v1", "openapi", exact_v1.status == default_before.status == 200
            and isinstance(exact_v1.body, dict) and len(exact_v1.body.get("paths", {})) == 1
            and before_operation.get("parameters", [{}])[0].get("schema", {}).get("const") == 1,
            "single exact route and default version 1", {"exactStatus": exact_v1.status, "defaultOperation": before_operation})

        missing_activate, missing_body = self.cli("action", "activate", "assurance.execute", "--version", "999")
        still_v1 = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "after-bad-activate"}, valid, uuid.uuid4().hex)
        self.check("activate-missing-version-is-atomic", "lifecycle", missing_activate.exitCode != 0
            and self.envelope(missing_body, "error") and self.result(still_v1).get("revision") == "v1",
            "failure leaves v1 default", {"cli": missing_body, "http": asdict(still_v1)})
        activate, body = self.cli("action", "activate", "assurance.execute", "--version", "2")
        default = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "default-v2"}, valid)
        explicit = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "explicit-v1"}, valid, uuid.uuid4().hex, 1)
        self.check("cli-action-activate", "lifecycle", activate.exitCode == 0 and self.envelope(body, "ok")
            and self.result(default).get("revision") == "v2" and self.result(explicit).get("revision") == "v1",
            "v2 default and v1 explicit", {"cli": body, "default": asdict(default), "explicit": asdict(explicit)})
        none_marker, ignored_key = uuid.uuid4().hex, uuid.uuid4().hex
        none_a = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": none_marker}, valid, ignored_key, 2)
        none_b = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": none_marker}, valid, ignored_key, 2)
        self.check("idempotency-none-ignores-key", "idempotency", none_a.status == none_b.status == 200
            and self.count(none_marker) == 2, "same supplied key still executes twice", {"effects": self.count(none_marker)})
        default_after = self.request("GET", "/openapi/default.json")
        after_operation = (default_after.body or {}).get("paths", {}).get("/api/assurance/execute", {}).get("post", {})
        self.check("openapi-updates-after-activate", "openapi", default_after.status == 200
            and after_operation.get("parameters", [{}])[0].get("schema", {}).get("const") == 2,
            "default document changes to version 2 without rebuild", after_operation)
        disable, disable_body = self.cli("action", "disable", "assurance.execute", "--version", "2")
        self.check("cli-action-disable-requires-replacement", "lifecycle", disable.exitCode != 0 and self.envelope(disable_body, "error")
            and disable_body.get("code") == "manifest.conflict", "manifest.conflict", disable_body)
        bad_replace, bad_replace_body = self.cli("action", "disable", "assurance.execute", "--version", "2",
                                                  "--replacement-version", "999")
        remains_v2 = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "after-bad-replace"}, valid)
        self.check("disable-missing-replacement-is-atomic", "lifecycle", bad_replace.exitCode != 0
            and self.envelope(bad_replace_body, "error") and self.result(remains_v2).get("revision") == "v2",
            "failure leaves v2 enabled and default", {"cli": bad_replace_body, "http": asdict(remains_v2)})
        replace, replace_body = self.cli("action", "disable", "assurance.execute", "--version", "2", "--replacement-version", "1")
        restored = self.request("POST", "/api/assurance/execute", {"mode": "ok", "value": "restored"}, valid, uuid.uuid4().hex)
        self.check("cli-action-disable-with-replacement", "lifecycle", replace.exitCode == 0 and self.envelope(replace_body, "ok")
            and self.result(restored).get("revision") == "v1", "v1 restored as default", {"cli": replace_body, "http": asdict(restored)})

    def run(self) -> dict[str, Any]:
        try:
            if self.start():
                self.cli_contract()
                self.dynamic_publication()
                self.http_contract()
                self.database_boundary()
                self.evidence_contract()
                self.payment_boundaries()
                self.lifecycle()
        except Exception as error:
            self.check("quality-checker-error", "infrastructure", False, "no exception", f"{type(error).__name__}: {error}")
        failures = [x.name for x in self.checks if not x.passed]
        return {"manifestVersion": "course-1", "toolVersion": "quality-1.0", "startedAt": self.started.isoformat(),
                "finishedAt": dt.datetime.now(dt.UTC).isoformat(), "status": "passed" if not failures else "failed",
                "failedChecks": failures, "checks": [asdict(x) for x in self.checks],
                "commands": [asdict(x) | {"stdout": x.stdout[-4096:], "stderr": x.stderr[-4096:]} for x in self.commands]}

    def close(self) -> None:
        if not self.args.keep_stack and self.override.exists():
            self.compose("down", "-v", "--remove-orphans", timeout=180)
        shutil.rmtree(self.temp, ignore_errors=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--fixtures", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--compose-wrapper", type=Path)
    parser.add_argument("--api-url", default="http://127.0.0.1:8080")
    parser.add_argument("--project-name", default=f"moduledev-quality-{os.getpid()}")
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--keep-stack", action="store_true")
    parser.add_argument("--build-timeout", type=int, default=900)
    parser.add_argument("--ready-timeout", type=float, default=90)
    args = parser.parse_args()
    checker: Checker | None = None
    try:
        checker = Checker(args)
        report = checker.run()
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if report["status"] == "passed" else 1
    finally:
        if checker:
            checker.close()


if __name__ == "__main__":
    raise SystemExit(main())
