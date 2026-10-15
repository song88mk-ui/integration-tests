"""Where the suite reads what it checks. Nothing comes from a clone of a service repo.

- Deployment config (argocd, infrastructure) is a snapshot of each repo at CONTRACT_CONFIG_REF
  (default main), downloaded as a GitHub tarball into .cache/config/<repo>/ by
  scripts/pull_images.py. It says which apps run in each environment and which image tag each
  one runs.
- Service code and appsettings.json come from the deployed images: pull_images.py copies each
  image's /app into .images/<env>/<image>/, and Scan.Assemblies writes what the assemblies
  publish and subscribe to into .images/<env>/scan.json.
"""

import json
import os
import subprocess
import tarfile
import tempfile
from functools import cache
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONFIG_DIR = ROOT / ".cache" / "config"
IMAGES_DIR = Path(os.environ.get("CONTRACT_IMAGES_DIR") or ROOT / ".images")
CONFIG_REPOS = ("argocd", "infrastructure")
ORG = "FlyingCockatoo"


def config_ref() -> str:
    return os.environ.get("CONTRACT_CONFIG_REF") or "main"


def fetch_config(name: str, ref: str) -> str:
    """Download repo name at ref into .cache/config/<name>/ and return the commit SHA."""
    sha = subprocess.run(
        ["gh", "api", f"repos/{ORG}/{name}/commits/{ref}", "--jq", ".sha"],
        check=True, capture_output=True, text=True,
    ).stdout.strip()
    target = CONFIG_DIR / name
    if (CONFIG_DIR / f"{name}.sha").is_file() and (CONFIG_DIR / f"{name}.sha").read_text().strip() == sha:
        return sha
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=CONFIG_DIR) as tmp:
        archive = Path(tmp) / "repo.tar.gz"
        with archive.open("wb") as out:
            subprocess.run(["gh", "api", f"repos/{ORG}/{name}/tarball/{sha}"], check=True, stdout=out)
        extracted = Path(tmp) / "x"
        with tarfile.open(archive) as tar:
            tar.extractall(extracted, filter="data")
        (top,) = extracted.iterdir()  # GitHub wraps the tree in <org>-<repo>-<sha>/
        if target.exists():
            target.rename(Path(tmp) / "old")
        top.rename(target)
    (CONFIG_DIR / f"{name}.sha").write_text(sha + "\n")
    return sha


def config_sha(name: str) -> str | None:
    file = CONFIG_DIR / f"{name}.sha"
    return file.read_text().strip() if file.is_file() else None


@cache
def read(name: str, path: str) -> str | None:
    """A file of a config repo (argocd, infrastructure), or None when it does not exist."""
    if name not in CONFIG_REPOS:
        raise ValueError(f"{name} is not a config repo; service files come from the images")
    root = CONFIG_DIR / name
    if not root.is_dir():
        raise FileNotFoundError(f"{root} is missing. Run scripts/pull_images.py (or run.sh) first.")
    file = root / path
    return file.read_text(encoding="utf-8-sig") if file.is_file() else None


def environments() -> tuple[str, ...]:
    """The environments whose images are extracted, limited to CONTRACT_ENVS when it is set."""
    wanted = os.environ.get("CONTRACT_ENVS")
    pulled = tuple(e for e in ("dev", "stg", "prd") if (IMAGES_DIR / e / "manifest.json").is_file())
    if wanted:
        return tuple(e for e in wanted.split(",") if e in pulled)
    return pulled


@cache
def manifest(env: str) -> dict:
    """image -> {"ref", "tag", "digest", "revision"} for env."""
    return json.loads((IMAGES_DIR / env / "manifest.json").read_text())


def image_dir(env: str, image: str) -> Path | None:
    path = IMAGES_DIR / env / image
    return path if path.is_dir() else None


def read_image(env: str, image: str, path: str) -> str | None:
    root = image_dir(env, image)
    if root is None:
        return None
    file = root / path
    return file.read_text(encoding="utf-8-sig") if file.is_file() else None


@cache
def scan(env: str) -> dict:
    """image -> {"entry", "subscribes", "publications": [{"key", "assembly", "method"}]}."""
    file = IMAGES_DIR / env / "scan.json"
    if not file.is_file():
        raise FileNotFoundError(f"{file} is missing. Run Scan.Assemblies (run.sh does).")
    return json.loads(file.read_text())
