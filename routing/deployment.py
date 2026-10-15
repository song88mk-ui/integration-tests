"""Which services run in each environment, and what each one binds on e.central.

Everything is derived from the deployment config and the deployed images, nothing is listed
by hand:

1. infrastructure/helmfile/helmfile.yaml.gotmpl lists the argocd-apps releases. "dev" installs
   the releases named *-dev; "stg" and "prd" install the others. Each release's values files
   give the namespace, appsDir, appPrefix and the app list (helm replaces lists, so an env file
   with "apps:" replaces the common one).
2. Each app's argocd values files, merged in the order
   infrastructure/helmfile/charts/argocd-apps/templates/applications.yaml gives them, provide
   the image, the tag ArgoCD Image Updater pinned, and the env block.
3. The image's own appsettings.json (/app in the image), overlaid with the env block (A__B is
   config key A:B), gives the effective MessageBroker:RoutingKey ("|"-separated) and
   MessageBroker:Queue.

This is the declared state in git, not what is running: an app the helmfile lists but nobody
has applied yet still counts as deployed. On 2026-10-14 client-topmarket-service was in the
helmfile for stg and prd but had no ArgoCD Application in either cluster.

A service consumes from e.central if its own entry assembly subscribes (MessageReceived += on
the shared broker, or consumer.Received += on its own channel, as BG.Engine.Sportsbook does).
Scan.Assemblies reads that from the IL.
"""

import json
import re
from dataclasses import dataclass, field
from functools import cache

import yaml

import sources

ENVIRONMENTS = ("dev", "stg", "prd")
# Images built by odds-engine-2, bet-genius, backoffice-final and platform-final: the ones
# pull_images.py extracts. The prefix alone is not enough, as other repos use it too.
DOTNET_PREFIXES = ("platform-", "oev2-", "backoffice-", "bg-")
NON_DOTNET_IMAGES = {
    "platform-auth-api",  # Auth
    "platform-song88", "platform-web", "platform-web-af", "platform-web-assets",  # platform-web
    "platform-webprops-api",  # webprops-api (Go)
}
HELMFILE = "helmfile/helmfile.yaml.gotmpl"


def render(text: str, env: str) -> str:
    """Enough of the Go template language for these files: the environment name, and a
    placeholder for every other expression."""
    text = re.sub(r"\{\{-?\s*\.Environment\.Name\s*-?\}\}", env, text)
    return re.sub(r"\{\{.*?\}\}", "TEMPLATE", text)


def deep_merge(base: dict, overlay: dict) -> dict:
    merged = dict(base)
    for key, value in overlay.items():
        if isinstance(value, dict) and isinstance(merged.get(key), dict):
            merged[key] = deep_merge(merged[key], value)
        else:
            merged[key] = value
    return merged


def load_yaml(name: str, path: str, env: str) -> dict:
    text = sources.read(name, path)
    if text is None:
        return {}
    documents = [d for d in yaml.safe_load_all(render(text, env)) if d]
    return documents[0] if documents else {}


def is_dotnet(image: str) -> bool:
    return image.startswith(DOTNET_PREFIXES) and image not in NON_DOTNET_IMAGES


@dataclass
class Service:
    env: str
    app: str
    namespace: str
    image: str
    repository: str
    tag: str | None
    config: dict = field(repr=False)

    @property
    def routing_keys(self) -> tuple[str, ...]:
        raw = (self.config.get("MessageBroker") or {}).get("RoutingKey") or ""
        return tuple(k.strip() for k in str(raw).split("|") if k.strip())

    @property
    def queue(self) -> str | None:
        return (self.config.get("MessageBroker") or {}).get("Queue")

    @property
    def extracted(self) -> bool:
        return sources.image_dir(self.env, self.image) is not None

    @property
    def consumes(self) -> bool:
        return bool((sources.scan(self.env).get(self.image) or {}).get("subscribes"))


def _env_to_config(env_block: dict) -> dict:
    config: dict = {}
    for key, value in (env_block or {}).items():
        node = config
        parts = str(key).split("__")
        for part in parts[:-1]:
            node = node.setdefault(part, {})
        node[parts[-1]] = value
    return config


def _appsettings(env: str, image: str) -> dict:
    text = sources.read_image(env, image, "appsettings.json")
    if not text:
        return {}
    # A few files carry // comments, which strict JSON rejects.
    text = re.sub(r"^\s*//.*$", "", text, flags=re.MULTILINE)
    return json.loads(text)


def _releases(env: str) -> list[dict]:
    helmfile = [d for d in yaml.safe_load_all(render(sources.read("infrastructure", HELMFILE), env)) if d]
    releases = next(d["releases"] for d in helmfile if "releases" in d)
    wanted = [r for r in releases if r.get("chart") == "charts/argocd-apps"]
    if env == "dev":
        return [r for r in wanted if r["name"].endswith("-dev")]
    return [r for r in wanted if not r["name"].endswith("-dev")]


@cache
def services(env: str, with_config: bool = True) -> tuple[Service, ...]:
    """Every app deployed in env. with_config=False skips appsettings.json, for resolving
    which images to pull before any is extracted."""
    found = []
    for release in _releases(env):
        values: dict = {}
        for path in release.get("values", []):
            if isinstance(path, str):
                values = deep_merge(values, load_yaml("infrastructure", f"helmfile/{path}", env))
        apps_dir = values.get("appsDir")
        for app in values.get("apps") or []:
            config_dir = app.get("config") or app["name"]
            files = app.get("valueFilesOverride") or (
                [f"../../apps/{apps_dir}/{config_dir}/{env}.yaml",
                 f"../../argocd-image-updater/{apps_dir}/{config_dir}/{env}.yaml"]
                + [f"../../apps/{apps_dir}/{config_dir}/{vf}" for vf in app.get("valueFiles") or []]
            )
            merged: dict = {}
            for vf in files:
                # Paths are relative to argocd/charts/<chart>.
                merged = deep_merge(merged, load_yaml("argocd", vf.replace("../../", "", 1), env))
            repository = (merged.get("image") or {}).get("repository") or ""
            image = repository.rsplit("/", 1)[-1] or app.get("appImageOverride") \
                or f"{values.get('appPrefix')}-{config_dir}"
            tag = (merged.get("image") or {}).get("tag")
            appsettings = _appsettings(env, image) if with_config else {}
            config = deep_merge(appsettings, _env_to_config(merged.get("env")))
            found.append(Service(env, app["name"], values.get("namespace"), image, repository,
                                 None if tag is None else str(tag), config))
    return tuple(found)


def consumers(env: str) -> tuple[Service, ...]:
    return tuple(s for s in services(env) if s.consumes and s.routing_keys)
