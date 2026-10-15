"""Every routing key the deployed .NET images publish on e.central, read from their IL.

Scan.Assemblies walks each first-party assembly in each image (.images/<env>/scan.json). A
string counts as published when it starts with "r." and reaches a Publish / CPublish call (not
ExternalPublish, which goes to the external UOF broker) or a RoutingKey assignment, as the
RabbitMQ.API gateway DTOs do. Comparisons are consumers reading the key and never reach those
calls; commented-out code is not in the IL at all.

Constants are inlined by the compiler, so $"r.oe.sb.engine.{MessageType.ON_ODDS_CHANGE.ToLower()}"
resolves exactly. Any other interpolated or concatenated part becomes "*" (one unknown word),
so "r.bet.betstatus." + clientCode is "r.bet.betstatus.*".

Each image carries its own copy of the shared libraries (OE.Shared, Platform.Shared, ...), so a
key published from shared code counts in every environment that deploys an image shipping it.
"""

from dataclasses import dataclass
from functools import cache

import deployment
import sources


@dataclass(frozen=True)
class Publication:
    image: str
    assembly: str
    method: str
    key: str

    def __str__(self) -> str:
        return f"{self.key}  ({self.image}: {self.assembly} {self.method})"


@cache
def publications(env: str) -> tuple[Publication, ...]:
    """Every publication in every image extracted for env."""
    return tuple(
        Publication(image, p["assembly"], p["method"], p["key"])
        for image, scan in sources.scan(env).items()
        for p in scan["publications"]
    )


def published_in(env: str) -> tuple[Publication, ...]:
    """Publications of the images deployed in env."""
    deployed = {s.image for s in deployment.services(env)}
    return tuple(p for p in publications(env) if p.image in deployed)
