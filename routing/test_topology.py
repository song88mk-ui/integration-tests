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
from topic import binding_matches

ENVIRONMENTS = deployment.ENVIRONMENTS
GAPS = yaml.safe_load((Path(__file__).parent / "known_gaps.yaml").read_text())


def _allowed(section: str, env: str) -> dict[str, str]:
    return {
        key: entry["reason"]
        for key, entry in (GAPS.get(section) or {}).items()
        if env in entry.get("envs", ENVIRONMENTS)
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


def test_every_deployed_dotnet_service_maps_to_a_project():
    unmapped = sorted({
        f"{s.env}/{s.app} ({s.image})"
        for env in ENVIRONMENTS for s in deployment.services(env)
        if s.project is None and s.image.split("-")[0] in ("platform", "oev2", "backoffice", "bg")
        and s.image not in NON_DOTNET_IMAGES
    })
    assert not unmapped, _report("Deployed images with no ci.yml matrix entry:", unmapped)


# Built outside the four .NET repos this suite reads.
NON_DOTNET_IMAGES = {
    "platform-auth-api",  # Auth
    "platform-song88", "platform-web", "platform-web-af", "platform-web-assets",  # platform-web
    "platform-webprops-api",  # webprops-api (Go)
}


def test_publisher_extraction_finds_known_publications():
    keys = {(p.repo, p.key) for p in publishers.publications()}

    assert ("odds-engine-2", "r.oe.sb.engine.onoddschange") in keys  # MessageType resolved
    assert ("bet-genius", "r.bg.receiver.onfixturechange") in keys  # OE.Shared constants
    assert ("platform-final", "r.bet.betstatus.*") in keys  # interpolated client code
    assert ("backoffice-final", "r.uof.onbetsettlement") in keys  # RabbitMQ.API gateway DTO
    assert not any(p.key == "r.bet.operator.cashout.*" for p in publishers.publications()), \
        "commented-out publish counted"


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

    assert not new, _report(f"[{env}] Bound, but nothing in the workspace publishes it:", new)
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


@pytest.mark.xfail(reason="GM-960", strict=False)
@pytest.mark.parametrize("env", ENVIRONMENTS)
def test_known_defect_GM_960_uof_client_ignores_adapter_errors(env):
    assert not [app for app in _receivers(env, "r.oe.adapter.error") if app.startswith("uof-client")]
