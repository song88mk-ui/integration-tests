#!/usr/bin/env bash
# Runs the cross-repo contract tests (DEVOPS-409) against the images deployed in one
# environment. No service repo is cloned: the code under test is the deployed images' /app.
#
#   1. Pull every .NET image the environment runs, at the tag ArgoCD pins, and copy its /app
#      into .images/<env>/ (scripts/pull_images.py). argocd and infrastructure are read through
#      the GitHub API.
#   2. Build the .NET harness against those assemblies, and scan them for what each image
#      publishes and subscribes to (Scan.Assemblies, for the routing tests).
#   3. Test the harness. Emitters write goldens to $CONTRACT_GOLDEN_DIR from the real producer
#      code path; checkers then read them into each consumer's own class.
#   4. Run every known-defect checker, one ticket at a time, and report which now pass.
#      This pass never fails the run unless --include-known is given.
#   5. Run the routing topology tests (pytest, through uv). Their known defects are xfail
#      tests; any that now pass are reported the same way.
#
# Usage: run.sh [--env dev|stg|prd] [--no-pull] [--tag IMAGE=TAG]... [--include-known]
#        --env            the environment whose deployed images are tested (default dev)
#        --no-pull        reuse what .images/<env>/ already holds (offline re-runs)
#        --tag            test this tag of an image instead of the one ArgoCD pins
#        --include-known  a known-defect checker that still fails also fails the run
#                         (use it to confirm a fix)
# Env:   CONTRACT_REGISTRY    registry holding the images (default: the environment's ECR
#                             registry, see scripts/pull_images.py). Docker must be logged in to
#                             it: scripts/ecr_login.sh <env>.
#        CONTRACT_CONFIG_REF  argocd/infrastructure ref to read (default main)
#        gh must be authenticated: argocd and infrastructure are read through the GitHub API.
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
pull=1
env=dev
pull_args=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-known) include_known=1 ;;
        --no-pull) pull=0 ;;
        --env) env="${2:?--env needs dev, stg or prd}"; shift ;;
        --tag) pull_args+=(--tag "${2:?--tag needs IMAGE=TAG}"); shift ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done
case "$env" in
    dev|stg|prd) ;;
    *) echo "--env must be dev, stg or prd, not $env" >&2; exit 2 ;;
esac

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sln="$here/harness/ContractTests.sln"
images="$here/.images/$env"
export CONTRACT_GOLDEN_DIR="$here/goldens"
export CONTRACT_ENVS="$env"
cd "$here"

echo "== Images under test ($env)"
if [[ $pull -eq 1 ]]; then
    [[ -n "${CONTRACT_REGISTRY:-}" ]] && pull_args+=(--registry "$CONTRACT_REGISTRY")
    uv run --frozen python scripts/pull_images.py --env "$env" "${pull_args[@]}"
elif [[ ! -f "$images/manifest.json" ]]; then
    echo "--no-pull, but $images/manifest.json does not exist. Run once without --no-pull." >&2
    exit 1
else
    uv run --frozen python scripts/pull_images.py --env "$env" --show
fi

echo "== .NET harness"
# Goldens from an earlier run must never satisfy a checker.
rm -rf "$CONTRACT_GOLDEN_DIR"
mkdir -p "$CONTRACT_GOLDEN_DIR"
# ContractImagesDir points every harness project at this environment's extracted images.
dotnet_retry build "$sln" -c Release --disable-build-servers -p:ContractImagesDir="$images/"
echo "== Scanning image assemblies"
dotnet_retry "$here/harness/Scan.Assemblies/bin/Release/net10.0/Scan.Assemblies.dll" "$images" > /dev/null
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

echo "== Routing topology ($env)"
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
