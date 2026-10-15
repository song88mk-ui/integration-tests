import pytest

from topic import binding_matches


@pytest.mark.parametrize(
    ("binding", "published", "expected"),
    [
        ("r.oe.sb.engine.onoddschange", "r.oe.sb.engine.onoddschange", True),
        ("r.oe.sb.engine.onoddschange", "r.oe.sb.engine.onbetstopglobal", False),
        ("r.bet.betstatus.*", "r.bet.betstatus.s88", True),
        # DEVOPS-416: the three-word key does not match a four-word binding.
        ("r.bet.betstatus.*", "r.bet.betstatus", False),
        ("r.bg.receiver.#", "r.bg.receiver.onfixturechange", True),
        ("r.bg.receiver.#", "r.bg.receiver", True),
        # GM-960: "#" may match zero words, so r.oe.adapter.#.# catches the error key.
        ("r.oe.adapter.#.#", "r.oe.adapter.error", True),
        ("r.oe.adapter.*.*", "r.oe.adapter.error", False),
        ("r.oe.adapter.#.00", "r.oe.adapter.onfixturechange.00", True),
        ("r.oe.adapter.#.00", "r.oe.adapter.onfixturechange.01", False),
        # An interpolated word in the published key can be any word.
        ("r.oe.adapter.#.00", "r.oe.adapter.onfixturechange.*", True),
        ("r.wallet.*", "r.wallet.*", True),
        ("#", "r.anything.at.all", True),
        ("r.uof.#", "r.oe.uof.heartbeat", False),
    ],
)
def test_binding_matches(binding, published, expected):
    assert binding_matches(binding, published) is expected
