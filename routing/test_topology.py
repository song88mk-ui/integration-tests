"""Routing topology of e.central, per environment: every published key reaches a deployed
consumer and every binding has a publisher, apart from the gaps in known_gaps.yaml.

Defects get their own test, marked xfail with the ticket key. run.sh reports any that start
passing, and --include-known makes them gate (pytest --runxfail).
"""

from pathlib import Path

import pytest
import yaml

import deployment
import publishers
import sources
from topic import binding_matches

# Only the environments whose images run.sh extracted (run.sh --env).
ENVIRONMENTS = sources.environments()
GAPS = yaml.safe_load((Path(__file__).parent / "known_gaps.yaml").read_text())


def _allowed(section: str, env: str) -> dict[str, str]:
    return {
        key: entry["reason"]
        for key, entry in (GAPS.get(section) or {}).items()
        if env in entry.get("envs", deployment.ENVIRONMENTS)
    }


def _bindings(env: str) -> list[tuple[str, str]]:
    return [(c.app, b) for c in deployment.consumers(env) for b in c.routing_keys]


def _receivers(env: str, key: str) -> list[str]:
    return sorted({app for app, binding in _bindings(env) if binding_matches(binding, key)})


def _report(title: str, items: list[str]) -> str:
    return "\n".join([title, *("  " + i for i in items)])


# --- The model reads what it should. A failure here means the extractor broke, not the estate.

@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_deployment_model_finds_known_consumers(env):
    consumers = {c.app: c for c in deployment.consumers(env)}

    assert "settlement-service" in consumers
    assert consumers["settlement-service"].routing_keys == (
        "r.bet.settlement", "r.oe.bet.settlement", "r.of.bet.settlement")
    assert consumers["sportsbook-service-00"].routing_keys == ("r.oe.adapter.#.00",)
    assert consumers["adapter-service"].queue == "q.oe.adapter"


@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_every_deployed_dotnet_image_is_extracted(env):
    scans = sources.scan(env)
    missing = sorted({
        f"{s.app} ({s.image})" for s in deployment.services(env)
        if deployment.is_dotnet(s.image) and not (s.extracted and (scans.get(s.image) or {}).get("entry"))
    })
    assert not missing, _report(f"[{env}] Deployed .NET images not extracted or with no entry assembly:", missing)


@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_publisher_extraction_finds_known_publications(env):
    keys = {(p.image, p.key) for p in publishers.publications(env)}

    assert ("oev2-sportsbook-service", "r.oe.sb.engine.onoddschange") in keys  # MessageType resolved
    assert ("bg-receiver-service", "r.bg.receiver.onfixturechange") in keys  # OE.Shared constants
    assert ("platform-placement-service", "r.bet.betstatus.*") in keys  # interpolated client code
    assert ("backoffice-bo-api", "r.uof.onbetsettlement") in keys  # RabbitMQ.API gateway DTO
    # A variable assigned on two branches is unknown, not its last value.
    assert ("platform-sportsbook-api", "r.oe.sportsbook.*") in keys


# --- The topology.

@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_every_published_key_reaches_a_consumer(env):
    allowed = _allowed("unrouted", env)
    published = {p.key: p for p in publishers.published_in(env)}
    unrouted = {key for key in published if not _receivers(env, key)}

    new = sorted(str(published[key]) for key in unrouted - allowed.keys())
    stale = sorted(allowed.keys() - unrouted)

    assert not new, _report(f"[{env}] Published, but no deployed consumer binds it:", new)
    assert not stale, _report(f"[{env}] known_gaps.yaml unrouted entries no longer unrouted:", stale)


@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_every_binding_has_a_publisher(env):
    allowed = _allowed("unbound", env)
    keys = {p.key for p in publishers.published_in(env)}
    unbound = {}
    for app, binding in _bindings(env):
        if not any(binding_matches(binding, key) for key in keys):
            unbound.setdefault(binding, []).append(app)

    new = sorted(f"{b}  ({', '.join(sorted(apps))})" for b, apps in unbound.items() if b not in allowed)
    stale = sorted(allowed.keys() - unbound.keys())

    assert not new, _report(f"[{env}] Bound, but no deployed image publishes it:", new)
    assert not stale, _report(f"[{env}] known_gaps.yaml unbound entries no longer unbound:", stale)


# --- Known defects.

@pytest.mark.xfail(reason="DEVOPS-416", strict=False)
@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_known_defect_DEVOPS_416_bare_betstatus_is_consumed(env):
    assert _receivers(env, "r.bet.betstatus")


@pytest.mark.xfail(reason="DEVOPS-440", strict=False)
@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_known_defect_DEVOPS_440_adapter_receives_translation_changes(env):
    assert "adapter-service" in _receivers(env, "r.oe.ontranslationchange")


# Was the GM-960 known defect: UOF.Client's r.oe.adapter.#.# caught the Adapter's errors. The
# error keys moved under r.error.# (odds-engine-2 #101), so this now guards against a regression.
@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_uof_client_ignores_adapter_errors(env):
    assert not [app for app in _receivers(env, "r.error.oe.adapter") if app.startswith("uof-client")]
