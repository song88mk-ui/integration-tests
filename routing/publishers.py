"""Every routing key the .NET repos publish on e.central, read from the code.

Producers never take their routing key from config: it is a string literal (or interpolated
string) at the publish site. A literal counts as published when its statement is a Publish /
CPublish call (not ExternalPublish, which goes to the external UOF broker), or assigns a
RoutingKey for the RabbitMQ.API gateways. Comparisons (==, StartsWith, case) are consumers
reading the key, and commented-out code is ignored.

Interpolations become "*" (one unknown word), except MessageType constants, which are
resolved from the repo's MessageType classes so the Sportsbook service's
$"r.oe.sb.engine.{MessageType.ON_ODDS_CHANGE.ToLower()}" stays exact.
"""

import re
from dataclasses import dataclass
from functools import cache

import deployment
import workspace

LITERAL = re.compile(r'(\$?)@?"(r\.[^"]*)"')
CONSTANT = re.compile(r'const\s+string\s+(\w+)\s*=\s*"([^"]*)"')
SHARED = re.compile(r"(^|/)(OE|Platform|Backoffice|BG)\.Shared/")


@dataclass(frozen=True)
class Publication:
    repo: str
    path: str
    line: int
    key: str
    project: str | None  # folder of the owning .csproj

    @property
    def shared(self) -> bool:
        return self.project is not None and bool(SHARED.search(self.project + "/"))

    def __str__(self) -> str:
        return f"{self.key}  ({self.repo}/{self.path}:{self.line})"


def _strip_comments(text: str) -> str:
    """Blank out comments, keeping line numbers. Full-line // only, so URLs in strings survive."""
    text = re.sub(r"/\*.*?\*/", lambda m: "\n" * m.group(0).count("\n"), text, flags=re.DOTALL)
    return re.sub(r"^\s*//.*$", "", text, flags=re.MULTILINE)


# bet-genius has no MessageType class of its own: it uses OE.Shared's, through the OE.Shared
# NuGet package (0.0.2-92, BG.Shared.csproj). The package can lag odds-engine-2 main, so a
# renamed constant would resolve to the newer value here.
CONSTANTS_FROM = {"bet-genius": ("bet-genius", "odds-engine-2")}


@cache
def _message_types(name: str) -> dict[str, str]:
    constants = {}
    for source in CONSTANTS_FROM.get(name, (name,)):
        constants.update(_constants_in(source))
    return constants


@cache
def _constants_in(name: str) -> dict[str, str]:
    constants = {}
    for path in workspace.files(name):
        if re.search(r"(^|/)MessageTypes?\.cs$", path):
            constants.update(CONSTANT.findall(workspace.read(name, path) or ""))
    return constants


def _resolve(name: str, literal: str, interpolated: bool) -> str:
    if not interpolated:
        return literal

    def replace(match: re.Match) -> str:
        expression = match.group(1)
        constant = re.fullmatch(r"MessageType\.(\w+)(\.ToLower\(\))?", expression)
        if constant and constant.group(1) in _message_types(name):
            value = _message_types(name)[constant.group(1)]
            return value.lower() if constant.group(2) else value
        return "*"

    return re.sub(r"\{([^{}]*)\}", replace, literal)


def _is_publish(statement: str, literal_start: int) -> bool:
    before = statement[:literal_start]
    if re.search(r"(==|!=|StartsWith\(|EndsWith\(|Contains\(|\bcase\s*)\s*$", before):
        return False
    if re.search(r"\b(C?Publish)\s*(<[^>]*>)?\s*\(", statement) and "ExternalPublish" not in statement:
        return True
    return bool(re.search(r"\b(RoutingKey|routingKey|\w*[Rr]outingKey)\s*=\s*$", before))


@cache
def _projects(name: str) -> tuple[str, ...]:
    dirs = {p.rsplit("/", 1)[0] for p in workspace.files(name) if p.endswith(".csproj") and "/" in p}
    return tuple(sorted(dirs, key=len, reverse=True))


def _project_of(name: str, path: str) -> str | None:
    return next((d for d in _projects(name) if path.startswith(d + "/")), None)


def _is_test(path: str) -> bool:
    return bool(re.search(r"(^|/)[^/]*\.Tests?(/|$)|(^|/)tests?/", path, flags=re.IGNORECASE))


@cache
def publications() -> tuple[Publication, ...]:
    found = []
    for name in deployment.DOTNET_REPOS:
        for path in workspace.grep_files(name, r'"r\.[a-z]', "*.cs"):
            if _is_test(path):
                continue
            text = _strip_comments(workspace.read(name, path) or "")
            offset = 0
            for statement in text.split(";"):
                for match in LITERAL.finditer(statement):
                    if not _is_publish(statement, match.start()):
                        continue
                    key = _resolve(name, match.group(2), bool(match.group(1)))
                    if key.endswith("."):  # "r.bet.betstatus." + clientCode
                        key += "*"
                    line = text.count("\n", 0, offset + match.start()) + 1
                    found.append(Publication(name, path, line, key, _project_of(name, path)))
                offset += len(statement) + 1
    return tuple(found)


def published_in(env: str) -> tuple[Publication, ...]:
    """Publications whose producer is deployed in env. Shared-library code counts everywhere."""
    deployed = {(s.project.repo, s.project.project) for s in deployment.services(env) if s.project}
    return tuple(p for p in publications() if p.shared or (p.repo, p.project) in deployed)
