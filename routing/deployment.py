"""Which services run in each environment, and what each one binds on e.central.

Everything is derived from the repos, nothing is listed by hand:

1. infrastructure/helmfile/helmfile.yaml.gotmpl lists the argocd-apps releases. "dev" installs
   the releases named *-dev; "stg" and "prd" install the others. Each release's values files
   give the namespace, appsDir, appPrefix and the app list (helm replaces lists, so an env file
   with "apps:" replaces the common one).
2. Each app's argocd values files, merged in the order
   infrastructure/helmfile/charts/argocd-apps/templates/applications.yaml gives them, provide
   the image and the env block.
3. Each .NET repo's .github/workflows/ci.yml matrix maps the image to its project folder.
4. The project's appsettings.json, overlaid with the env block (A__B is config key A:B), gives
   the effective MessageBroker:RoutingKey ("|"-separated) and MessageBroker:Queue.

This is the declared state in git, not what is running: an app the helmfile lists but nobody
has applied yet still counts as deployed. On 2026-10-14 client-topmarket-service was in the
helmfile for stg and prd but had no ArgoCD Application in either cluster.

A service consumes from e.central if its own project code subscribes (MessageReceived += on
the shared broker, or consumer.Received += on its own channel, as BG.Engine.Sportsbook does).
"""

import json
import re
from dataclasses import dataclass, field
from functools import cache

import yaml

import workspace

ENVIRONMENTS = ("dev", "stg", "prd")
DOTNET_REPOS = ("backoffice-final", "bet-genius", "odds-engine-2", "platform-final")
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
    text = workspace.read(name, path)
    if text is None:
        return {}
    documents = [d for d in yaml.safe_load_all(render(text, env)) if d]
    return documents[0] if documents else {}


@dataclass(frozen=True)
class Image:
    repo: str
    project: str  # folder of the .csproj, relative to the repo root


@cache
def images() -> dict[str, Image]:
    """ECR image name -> project, from each .NET repo's ci.yml build matrix."""
    found = {}
    for name in DOTNET_REPOS:
        text = workspace.read(name, ".github/workflows/ci.yml") or ""
        prefix = re.search(r"file:\s*(\S*?)\$\{\{\s*matrix\.app\.path\s*\}\}/Dockerfile", text)
        root = prefix.group(1) if prefix else ""
        for path, image in re.findall(r"-\s*path:\s*(\S+)\s+image:\s*(\S+)", text):
            found[image] = Image(name, root + path)
    return found


@dataclass
class Service:
    env: str
    app: str
    namespace: str
    image: str
    project: Image | None
    config: dict = field(repr=False)

    @property
    def routing_keys(self) -> tuple[str, ...]:
        raw = (self.config.get("MessageBroker") or {}).get("RoutingKey") or ""
        return tuple(k.strip() for k in str(raw).split("|") if k.strip())

    @property
    def queue(self) -> str | None:
        return (self.config.get("MessageBroker") or {}).get("Queue")

    @property
    def consumes(self) -> bool:
        return self.project is not None and _subscribes(self.project.repo, self.project.project)


@cache
def _subscribes(name: str, project: str) -> bool:
    return any(
        path.startswith(project + "/")
        for path in workspace.grep_files(name, r"Received\s*\+=", "*.cs")
    )


def _env_to_config(env_block: dict) -> dict:
    config: dict = {}
    for key, value in (env_block or {}).items():
        node = config
        parts = str(key).split("__")
        for part in parts[:-1]:
            node = node.setdefault(part, {})
        node[parts[-1]] = value
    return config


def _appsettings(image: Image | None) -> dict:
    if image is None:
        return {}
    text = workspace.read(image.repo, f"{image.project}/appsettings.json")
    if not text:
        return {}
    # A few files carry // comments, which strict JSON rejects.
    text = re.sub(r"^\s*//.*$", "", text, flags=re.MULTILINE)
    return json.loads(text)


def _releases(env: str) -> list[dict]:
    helmfile = [d for d in yaml.safe_load_all(render(workspace.read("infrastructure", HELMFILE), env)) if d]
    releases = next(d["releases"] for d in helmfile if "releases" in d)
    wanted = [r for r in releases if r.get("chart") == "charts/argocd-apps"]
    if env == "dev":
        return [r for r in wanted if r["name"].endswith("-dev")]
    return [r for r in wanted if not r["name"].endswith("-dev")]


@cache
def services(env: str) -> tuple[Service, ...]:
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
            project = images().get(image)
            config = deep_merge(_appsettings(project), _env_to_config(merged.get("env")))
            found.append(Service(env, app["name"], values.get("namespace"), image, project, config))
    return tuple(found)


def consumers(env: str) -> tuple[Service, ...]:
    return tuple(s for s in services(env) if s.consumes and s.routing_keys)
