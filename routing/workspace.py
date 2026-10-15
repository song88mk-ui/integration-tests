"""Locate and read the sibling service repos that the contract tests check.

The suite expects every repo it reads to be a sibling clone of integration-tests, the layout
checkout-repos.sh produces locally and the CI workflow reproduces. SONG88_WORKSPACE overrides the parent directory.

By default files are read from each clone's working tree. Set CONTRACT_GIT_REF (for example
origin/main) to read every repo at that ref instead, without touching the clones.
"""

import os
import subprocess
from functools import cache
from pathlib import Path

# Every repo the routing and message-contract layers read. infrastructure is listed because
# the deployed app list lives in its helmfile, and layer 2 builds SQL Server from its Flyway
# migrations.
REQUIRED_REPOS = (
    "argocd",
    "backoffice-final",
    "bet-genius",
    "infrastructure",
    "odds-engine-2",
    "platform-final",
)


def workspace_root() -> Path:
    override = os.environ.get("SONG88_WORKSPACE")
    if override:
        return Path(override).resolve()
    # routing/ -> integration-tests/ -> workspace
    return Path(__file__).resolve().parents[2]


def repo(name: str) -> Path:
    return workspace_root() / name


def git_ref() -> str | None:
    return os.environ.get("CONTRACT_GIT_REF") or None


def _git(name: str, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(repo(name)), *args],
        check=True, capture_output=True, text=True, encoding="utf-8",
    ).stdout


@cache
def read(name: str, path: str) -> str | None:
    """A tracked file's text, or None when it does not exist."""
    ref = git_ref()
    if ref:
        try:
            return _git(name, "show", f"{ref}:{path}")
        except subprocess.CalledProcessError:
            return None
    file = repo(name) / path
    return file.read_text(encoding="utf-8-sig") if file.is_file() else None


@cache
def files(name: str, prefix: str = "") -> tuple[str, ...]:
    """Tracked file paths under prefix (git ls-files, or ls-tree at CONTRACT_GIT_REF)."""
    ref = git_ref()
    args = ["ls-tree", "-r", "--name-only", ref] if ref else ["ls-files"]
    if prefix:
        args += ["--", prefix]
    return tuple(_git(name, *args).splitlines())


def grep_files(name: str, pattern: str, glob: str) -> tuple[str, ...]:
    """Tracked files matching glob whose text matches the extended regex pattern."""
    ref = git_ref()
    args = ["grep", "-l", "-E", pattern] + ([ref] if ref else []) + ["--", glob]
    try:
        out = _git(name, *args)
    except subprocess.CalledProcessError:
        return ()
    prefix = f"{ref}:" if ref else ""
    return tuple(line[len(prefix):] for line in out.splitlines())
