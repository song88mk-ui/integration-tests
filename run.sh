#!/usr/bin/env bash
# Runs the cross-repo contract tests (DEVOPS-409) against the sibling clones.
#
#   1. Report which commit of each sibling repo is under test.
#   2. Build and test the .NET harness. Emitters write goldens to $CONTRACT_GOLDEN_DIR from
#      the real producer code path; checkers then read them into each consumer's own class.
#   3. Run every known-defect checker, one ticket at a time, and report which now pass.
#      This pass never fails the run unless --include-known is given.
#   4. Run the routing topology tests (pytest, through uv). Their known defects are xfail
#      tests; any that now pass are reported the same way.
#
# Usage: run.sh [--include-known]
#        --include-known  a known-defect checker that still fails also fails the run
#                         (use it to confirm a fix)
# Env:   SONG88_WORKSPACE  parent of the sibling clones (default: the parent of this repo)
#        NUGET_PASSWORD    needed once emitters/checkers reference repos on the private feed
#        CONTRACT_GIT_REF  routing tests only: read every repo at this ref (e.g. origin/main)
#                          instead of its working tree
set -euo pipefail

# MSBuild worker nodes that outlive a build keep the stdout pipe of $(dotnet test ...) open,
# so the known-defect pass below would wait on them forever.
export MSBUILDDISABLENODEREUSE=1

# On 2026-10-14 the dotnet CLI (SDK 10.0.112) aborted four times, in both build and test, with
# "Fatal error. Internal CLR error. (0x80131506)" (SIGABRT, exit 134). It never repeated on the
# next run, and the cause is unknown. An abort is not a test result, so retry it once, loudly.
dotnet_retry() {
    local status=0
    dotnet "$@" || status=$?
    if [[ $status -eq 134 ]]; then
        echo "!! dotnet $1 aborted (exit 134, CLR crash); retrying once" >&2
        status=0
        dotnet "$@" || status=$?
    fi
    return $status
}

include_known=0
for arg in "$@"; do
    case "$arg" in
        --include-known) include_known=1 ;;
        *) echo "Unknown argument: $arg" >&2; exit 2 ;;
    esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sln="$here/harness/ContractTests.sln"
export SONG88_WORKSPACE="${SONG88_WORKSPACE:-$(cd "$here/.." && pwd)}"
export CONTRACT_GOLDEN_DIR="$here/goldens"

echo "== Repos under test (workspace: $SONG88_WORKSPACE)"
for name in argocd backoffice-final bet-genius infrastructure odds-engine-2 platform-final; do
    dir="$SONG88_WORKSPACE/$name"
    if [[ ! -d "$dir/.git" ]]; then
        echo "   $name: MISSING"
        continue
    fi
    branch="$(git -C "$dir" branch --show-current)"
    commit="$(git -C "$dir" rev-parse --short HEAD)"
    note=""
    [[ "$branch" != "main" ]] && note="   <-- not main; results describe this branch"
    behind="$(git -C "$dir" rev-list --count HEAD..origin/main 2>/dev/null || echo 0)"
    [[ "$behind" -gt 0 ]] && note="$note   <-- $behind commit(s) behind origin/main (as of the last fetch)"
    echo "   $name: $branch @ $commit$note"
done

echo "== .NET harness"
# A sibling repo's project in the solution would make the gating pass run that repo's own
# test suite. Plain `dotnet sln add` does this; see the README.
if grep -qE '"\.\.[\\/]' "$sln"; then
    echo "ContractTests.sln contains a project outside harness/. Remove it:" >&2
    grep -E '"\.\.[\\/]' "$sln" | cut -d'"' -f6 >&2
    exit 1
fi
# Goldens from an earlier run must never satisfy a checker.
rm -rf "$CONTRACT_GOLDEN_DIR"
mkdir -p "$CONTRACT_GOLDEN_DIR"
dotnet_retry build "$sln" -c Release --disable-build-servers
# Separate passes: dotnet test runs projects in parallel, and every golden must exist before
# any checker reads one. Emitters carry [Trait("Category", "Emit")]; known-defect checkers
# carry Category=KnownDefect and run in the next step instead.
dotnet_retry test "$sln" -c Release --no-build -m:1 --filter "Category=Emit"
dotnet_retry test "$sln" -c Release --no-build -m:1 --filter "Category!=Emit&Category!=KnownDefect"

echo "== Known defects"
# The ticket keys come from the [KnownDefectFact("...")] usages in the harness source.
mapfile -t tickets < <(grep -rhoE '\[KnownDefectFact\("[^"]+"\)' "$here/harness" --include='*.cs' \
    | sed -E 's/.*\("([^"]+)"\).*/\1/' | sort -u)

now_passing=()
known_log="$(mktemp)"
trap 'rm -f "$known_log"' EXIT
still_failing=()
if [[ ${#tickets[@]} -eq 0 ]]; then
    echo "   none"
fi
for ticket in "${tickets[@]}"; do
    # Output goes to a file, not $(...): a wedged MSBuild worker that inherits a pipe keeps
    # the command substitution open after dotnet test exits (seen 2026-10-14). -m:1 keeps the
    # --no-build run from starting worker nodes at all.
    CONTRACT_INCLUDE_KNOWN=1 dotnet_retry test "$sln" -c Release --no-build -m:1 \
        --filter "Defect=$ticket" > "$known_log" 2>&1 || true
    output="$(cat "$known_log")"
    passed="$(grep -oE 'Passed: +[0-9]+' <<<"$output" | awk '{s += $2} END {print s + 0}')"
    failed="$(grep -oE 'Failed: +[0-9]+' <<<"$output" | awk '{s += $2} END {print s + 0}')"
    if [[ "$failed" -gt 0 ]]; then
        echo "   $ticket: still failing ($failed failed, $passed passed)"
        still_failing+=("$ticket")
    elif [[ "$passed" -gt 0 ]]; then
        echo "   $ticket: NOW PASSES ($passed passed). Confirm the fix, then change its [KnownDefectFact] to [GoldenFact]."
        now_passing+=("$ticket")
    else
        echo "   $ticket: no test ran. Check the build output:"
        echo "$output" | tail -5 | sed 's/^/      /'
        still_failing+=("$ticket")
    fi
done

echo "== Routing topology"
cd "$here"
routing_log="$(mktemp)"
pytest_args=(-rxX)
# --runxfail runs the xfail-marked known defects as ordinary tests, so a failing one fails.
[[ $include_known -eq 1 ]] && pytest_args+=(--runxfail)
routing_status=0
uv run --frozen pytest "${pytest_args[@]}" 2>&1 | tee "$routing_log" || routing_status=$?
# Non-strict xfail: a known defect that now passes is reported as XPASS without failing.
while read -r ticket; do
    echo "   $ticket: NOW PASSES (routing). Confirm the fix, then remove its xfail marker."
    now_passing+=("$ticket")
done < <(grep -oE '^XPASS .* - [A-Z]+-[0-9]+' "$routing_log" | sed -E 's/.* - //' | sort -u)
rm -f "$routing_log"

if [[ $routing_status -ne 0 ]]; then
    echo "== Routing topology failed" >&2
    exit "$routing_status"
fi
if [[ ${#now_passing[@]} -gt 0 ]]; then
    echo "== Known defects that now pass: ${now_passing[*]}"
fi
if [[ $include_known -eq 1 && ${#still_failing[@]} -gt 0 ]]; then
    echo "== --include-known: still failing: ${still_failing[*]}" >&2
    exit 1
fi
