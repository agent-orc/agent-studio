#!/usr/bin/env bash
# Deployment regression scenario: one definition, three targets.
#
# The scenario is the regression suite for every deployment card and for the
# release. It runs the same ordered steps against a local process topology, a
# docker-compose control plane, or an already running deployment.
#
#   scripts/scenario.sh --target inproc  --level smoke
#   scripts/scenario.sh --target compose --level full
#   scripts/scenario.sh --target remote  --level smoke \
#       --server-url https://tasks.example --token "$TOKEN"
#
# Exit codes are the runner's own and are stable:
#   0 passed  1 step failed  2 usage error  3 invalid document  4 target unavailable

set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${SCENARIO_CONFIGURATION:-Debug}"
runner_dll="$repo_root/scenario/bin/$configuration/net10.0/agent-studio-scenario.dll"

usage()
{
    cat <<'EOF'
Usage: scenario.sh --target inproc|compose|remote [options]

Options:
  --target <kind>       Required. inproc, compose, or remote.
  --level <level>       smoke (default) or full.
  --scenario <path>     Scenario document. Default:
                        testsupport/scenario/deployment-regression.json
  --out <dir>           Report and evidence directory. Default:
                        JOB_RESULTS_DIR, else artifacts/scenario.
  --server-url <url>    Task Server base URL. Required for --target remote.
  --studio-url <url>    Studio BFF base URL. Optional for --target remote.
  --token <credential>  Management credential. Required for --target remote.
  --keep-evidence       Keep the temporary run directories after the run.
  --no-build            Do not build before running.
  -h, --help            Print this help.

Environment:
  SCENARIO_CONFIGURATION  Build configuration to run. Default: Debug.
EOF
}

build=1
forwarded=()
target=

while (($#)); do
    case "$1" in
        --no-build)
            build=0
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --target)
            (($# >= 2)) || { usage >&2; exit 2; }
            target=$2
            forwarded+=("$1" "$2")
            shift 2
            ;;
        *)
            forwarded+=("$1")
            shift
            ;;
    esac
done

if [ -z "$target" ]; then
    printf 'scenario.sh: --target is required.\n\n' >&2
    usage >&2
    exit 2
fi

if [ "$target" = "compose" ]; then
    # Fail with the operator's real problem rather than a compose stack trace.
    # `docker info` needs the daemon, which is what actually distinguishes "no
    # Docker installed" from "installed but this user cannot reach the socket".
    docker compose version >/dev/null 2>&1 || {
        printf 'scenario.sh: the compose target needs Docker Engine with the Compose plugin.\n' >&2
        exit 4
    }
    docker info >/dev/null 2>&1 || {
        printf 'scenario.sh: the Docker daemon is not reachable for user %s.\n' "$(id -un)" >&2
        printf 'scenario.sh: add the user to the docker group (an operator step) and retry.\n' >&2
        exit 4
    }
fi

if [ "$build" -eq 1 ]; then
    # The inproc and compose targets start the built deployables as siblings, so
    # the whole solution has to be current, not just the scenario runner.
    dotnet build "$repo_root/agent-taskboard.sln" \
        --configuration "$configuration" --nologo --verbosity quiet
elif [ ! -f "$runner_dll" ]; then
    printf 'scenario.sh: %s does not exist; run without --no-build.\n' "$runner_dll" >&2
    exit 4
fi

exec dotnet "$runner_dll" --repo-root "$repo_root" "${forwarded[@]}"
