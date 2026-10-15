import pytest

from workspace import REQUIRED_REPOS, repo, workspace_root


@pytest.mark.parametrize("name", REQUIRED_REPOS)
def test_required_repo_is_checked_out(name):
    path = repo(name)

    assert (path / ".git").exists(), (
        f"{name} is not checked out at {path}. "
        f"Clone it next to qa-robot-automation or set SONG88_WORKSPACE (now {workspace_root()})."
    )
