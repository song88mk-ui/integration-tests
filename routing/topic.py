"""AMQP topic-exchange matching, for a routing key that may itself contain wildcards.

A published key is concrete except where the producer interpolates a value it does not know
statically (a client code, a shard number). Those words become "*", meaning "some single
word", so matching asks whether any concrete key the producer could send matches the binding.
"""

from functools import cache


def split(pattern: str) -> tuple[str, ...]:
    return tuple(pattern.split("."))


def binding_matches(binding: str, published: str) -> bool:
    """True if a queue bound with binding receives at least one key published as published.

    In binding, "*" is exactly one word and "#" is zero or more words (RabbitMQ topic rules).
    In published, "*" stands for one unknown word and matches any single binding word.
    """
    return _match(split(binding), split(published))


@cache
def _match(binding: tuple[str, ...], published: tuple[str, ...]) -> bool:
    if not binding:
        return not published
    head, rest = binding[0], binding[1:]
    if head == "#":
        # Zero words, or consume one published word and stay on "#".
        return _match(rest, published) or (bool(published) and _match(binding, published[1:]))
    if not published:
        return False
    if head == "*" or published[0] == "*" or head == published[0]:
        return _match(rest, published[1:])
    return False
