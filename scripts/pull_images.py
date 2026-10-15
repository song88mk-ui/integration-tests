"""Pull the images deployed in an environment and copy each one's /app out of it.

    uv run python scripts/pull_images.py --env dev [--registry ghcr.io/flyingcockatoo]
                                         [--tag IMAGE=TAG ...] [--list | --show]

1. Downloads argocd and infrastructure at CONTRACT_CONFIG_REF (default main) through the
   GitHub API (sources.fetch_config); no clone.
2. Resolves every app deployed in the environment and the tag ArgoCD Image Updater pinned for
   it (deployment.services), keeping the images the four .NET service repos build.
3. Pulls <registry>/<image>:<tag> for linux/amd64 and copies /app into .images/<env>/<image>/.
   An image whose digest matches the last extraction is not copied again.
4. Writes .images/<env>/manifest.json: ref, tag, digest and the commit (OCI revision label).

The registry login is the caller's: run.sh logs in to ghcr.io with CONTRACT_REGISTRY_TOKEN.
"""

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "routing"))

import deployment  # noqa: E402
import sources  # noqa: E402

DEFAULT_REGISTRY = "ghcr.io/flyingcockatoo"


def docker(*args: str, capture: bool = True) -> str:
    result = subprocess.run(["docker", *args], check=True, text=True,
                            capture_output=capture)
    return result.stdout.strip() if capture else ""


def deployed_images(env: str, overrides: dict[str, str]) -> dict[str, str]:
    """image -> tag for every .NET image deployed in env."""
    found: dict[str, str] = {}
    problems = []
    for service in deployment.services(env, with_config=False):
        if not deployment.is_dotnet(service.image):
            continue
        tag = overrides.get(service.image) or service.tag
        if not tag or tag == "latest":
            problems.append(f"{service.app} ({service.image}): no pinned tag in argocd-image-updater")
            continue
        if found.get(service.image, tag) != tag:
            problems.append(f"{service.image}: apps disagree on the tag ({found[service.image]} vs {tag})")
        found[service.image] = tag
    for problem in problems:
        print(f"   !! {problem}", file=sys.stderr)
    return dict(sorted(found.items()))


def extract(ref: str, target: Path) -> None:
    container = docker("create", "--platform", "linux/amd64", ref)
    try:
        with tempfile.TemporaryDirectory(dir=target.parent) as tmp:
            docker("cp", f"{container}:/app", tmp)
            if target.exists():
                shutil.rmtree(target)
            (Path(tmp) / "app").rename(target)
    finally:
        docker("rm", container)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--env", choices=deployment.ENVIRONMENTS, default="dev")
    parser.add_argument("--registry", default=DEFAULT_REGISTRY)
    parser.add_argument("--tag", action="append", default=[], metavar="IMAGE=TAG",
                        help="test this tag instead of the one argocd pins")
    parser.add_argument("--list", action="store_true", help="print the images and stop")
    parser.add_argument("--show", action="store_true",
                        help="print what .images/<env> holds now, without fetching anything")
    args = parser.parse_args()
    overrides = dict(t.split("=", 1) for t in args.tag)

    if args.show:
        for name in sources.CONFIG_REPOS:
            print(f"   {name}: {(sources.config_sha(name) or 'not fetched')[:9]}")
        for image, entry in sources.manifest(args.env).items():
            print(f"   {image}: {entry['tag']}  {entry['revision'][:9]}")
        print("   (--no-pull: as last extracted)")
        return 0

    for name in sources.CONFIG_REPOS:
        sha = sources.fetch_config(name, sources.config_ref())
        print(f"   {name}: {sources.config_ref()} @ {sha[:9]} (GitHub API)")

    images = deployed_images(args.env, overrides)
    if args.list:
        for image, tag in images.items():
            print(f"{image}:{tag}")
        return 0

    env_dir = sources.IMAGES_DIR / args.env
    env_dir.mkdir(parents=True, exist_ok=True)
    manifest_file = env_dir / "manifest.json"
    previous = json.loads(manifest_file.read_text()) if manifest_file.is_file() else {}
    manifest = {}
    for image, tag in images.items():
        ref = f"{args.registry}/{image}:{tag}"
        docker("pull", "--quiet", "--platform", "linux/amd64", ref)
        digest = docker("image", "inspect", "--format", "{{index .RepoDigests 0}}", ref)
        revision = docker("image", "inspect", "--format",
                          '{{index .Config.Labels "org.opencontainers.image.revision"}}', ref)
        target = env_dir / image
        if previous.get(image, {}).get("digest") != digest or not target.is_dir():
            extract(ref, target)
        manifest[image] = {"ref": ref, "tag": tag, "digest": digest,
                           "revision": revision if revision != "<no value>" else ""}
        print(f"   {image}: {tag}  {manifest[image]['revision'][:9]}")
    # Images no longer deployed must not be scanned.
    for stale in sorted(set(previous) - set(manifest)):
        shutil.rmtree(env_dir / stale, ignore_errors=True)
    manifest_file.write_text(json.dumps(manifest, indent=2) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
