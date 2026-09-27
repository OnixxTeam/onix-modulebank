import re

import pytest
from fastapi.testclient import TestClient

from perimeter.observability import (
    Counters,
    Family,
    Sample,
    create_observability_app,
    render_openmetrics,
)

SAMPLE_LINE = re.compile(r"^([A-Za-z_:][A-Za-z0-9_:]*)(\{[^}]*\})? (-?[0-9]+(?:\.[0-9]+)?)$")


def parse(document: str) -> tuple[dict[str, str], dict[str, float]]:
    """The subset of OpenMetrics this contract uses, parsed the way it is verified."""
    lines = [line for line in document.split("\n") if line]
    assert lines[-1] == "# EOF"

    types: dict[str, str] = {}
    samples: dict[str, float] = {}

    for line in lines[:-1]:
        declaration = re.fullmatch(r"# TYPE ([A-Za-z_:][A-Za-z0-9_:]*) (counter|gauge)", line)
        if declaration:
            types[declaration.group(1)] = declaration.group(2)
            continue
        if line.startswith("#"):
            continue

        sample = SAMPLE_LINE.fullmatch(line)
        assert sample, f"not a valid OpenMetrics sample: {line}"
        samples[sample.group(1)] = float(sample.group(3))

    return types, samples


def test_an_empty_document_is_still_valid():
    assert render_openmetrics([]) == "# EOF\n"


def test_a_counter_is_declared_without_the_suffix_and_sampled_with_it():
    document = render_openmetrics([Family("outbox_attempts", "counter", "", (Sample(2),))])

    types, samples = parse(document)
    assert types == {"outbox_attempts": "counter"}
    assert samples == {"outbox_attempts_total": 2}


def test_a_counter_family_may_not_carry_the_suffix_itself():
    with pytest.raises(ValueError):
        render_openmetrics([Family("outbox_attempts_total", "counter", "", (Sample(1),))])


def test_labels_are_rendered_and_the_document_parses():
    document = render_openmetrics(
        [Family("outbox_attempts", "counter", "By outcome", (Sample(3, {"outcome": "retryable"}),))]
    )

    assert 'outbox_attempts_total{outcome="retryable"} 3' in document
    parse(document)


def test_the_document_never_contains_a_carriage_return():
    document = render_openmetrics([Family("poll_cycles", "counter", "Loops", (Sample(7),))])

    assert "\r" not in document


@pytest.mark.parametrize("label", ["operation_id", "operationId", "external_request_id", "messageId"])
def test_a_forbidden_label_name_is_rejected(label: str):
    with pytest.raises(ValueError):
        render_openmetrics([Family("poll_cycles", "counter", "", (Sample(1, {label: "x"}),))])


def test_a_label_value_that_looks_like_a_unique_identifier_is_rejected():
    with pytest.raises(ValueError):
        render_openmetrics(
            [Family("poll_cycles", "counter", "", (Sample(1, {"owner": "0b9c1a3e-1f2d-4c5b-8a7e-6d5c4b3a2918"}),))]
        )


def test_a_label_value_longer_than_the_contract_limit_is_rejected():
    with pytest.raises(ValueError):
        render_openmetrics([Family("poll_cycles", "counter", "", (Sample(1, {"owner": "a" * 65}),))])


def test_more_than_twenty_values_of_one_label_in_one_scrape_is_rejected():
    samples = tuple(Sample(1, {"owner": f"owner-{index}"}) for index in range(21))

    with pytest.raises(ValueError):
        render_openmetrics([Family("poll_cycles", "counter", "", samples)])


def test_twenty_values_of_one_label_are_accepted():
    samples = tuple(Sample(1, {"owner": f"owner-{index}"}) for index in range(20))

    assert render_openmetrics([Family("poll_cycles", "counter", "", samples)]).endswith("# EOF\n")


def test_counters_accumulate_per_label_set():
    counters = Counters()
    counters.increment("attempts", {"outcome": "succeeded"})
    counters.increment("attempts", {"outcome": "succeeded"})
    counters.increment("attempts", {"outcome": "terminal"})
    counters.increment("cycles", by=5)

    attempts = {tuple(sample.labels.items()): sample.value for sample in counters.snapshot("attempts")}
    assert attempts == {(("outcome", "succeeded"),): 2, (("outcome", "terminal"),): 1}
    assert counters.snapshot("cycles")[0].value == 5


def test_liveness_says_live_in_json():
    client = TestClient(create_observability_app(readiness=lambda: True, metrics=lambda: []))

    response = client.get("/health/live")

    assert response.status_code == 200
    assert response.headers["content-type"] == "application/json"
    assert response.content == b'{"status":"live"}'


def test_readiness_says_ready_when_the_dependency_answers():
    client = TestClient(create_observability_app(readiness=lambda: True, metrics=lambda: []))

    response = client.get("/health/ready")

    assert response.status_code == 200
    assert response.content == b'{"status":"ready"}'


def test_readiness_is_five_oh_three_with_the_contract_code_when_the_dependency_is_down():
    client = TestClient(create_observability_app(readiness=lambda: False, metrics=lambda: []))

    response = client.get("/health/ready")

    assert response.status_code == 503
    assert response.headers["content-type"] == "application/json"
    assert response.content == b'{"status":"not_ready","code":"dependency.unavailable"}'


# A dependency check that raises is a dependency that is unavailable, not a 500: the
# probe has to answer something, and the contract's answer is 503.
def test_a_readiness_probe_that_raises_answers_not_ready():
    def explode() -> bool:
        raise RuntimeError("connection refused")

    client = TestClient(create_observability_app(readiness=explode, metrics=lambda: []))

    assert client.get("/health/ready").status_code == 503


# Liveness stays up while readiness is down: a probe that failed with the database
# would turn one outage into a restart loop across every service that depends on it.
def test_liveness_stays_up_while_readiness_is_down():
    client = TestClient(create_observability_app(readiness=lambda: False, metrics=lambda: []))

    assert client.get("/health/live").status_code == 200
    assert client.get("/health/ready").status_code == 503


def test_metrics_uses_the_exact_openmetrics_media_type():
    client = TestClient(
        create_observability_app(
            readiness=lambda: True,
            metrics=lambda: [Family("poll_cycles", "counter", "Loops", (Sample(4),))],
        )
    )

    response = client.get("/metrics")

    assert response.status_code == 200
    assert response.headers["content-type"] == "application/openmetrics-text; version=1.0.0; charset=utf-8"
    types, samples = parse(response.text)
    assert types == {"poll_cycles": "counter"}
    assert samples == {"poll_cycles_total": 4}
