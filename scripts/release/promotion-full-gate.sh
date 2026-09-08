#!/usr/bin/env bash
# Mandatory repository-wide gate for a develop -> main promotion.
#
# This command is intentionally fixed in source control. The promotion driver
# runs this trusted copy against an isolated checkout of the exact candidate
# merge commit. There is no skip, reduced-suite, or continue-on-error mode.

set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: promotion-full-gate.sh --repo <candidate-checkout>

Runs every blocking build, lint, type-check, and non-machine-bound test used to
qualify an Agent Studio develop -> main promotion.
EOF
}

repo=
while (($#)); do
  case "$1" in
    --repo)
      (($# >= 2)) || { usage >&2; exit 2; }
      repo=$2
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'Unknown argument: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

[[ -n "$repo" && -d "$repo/.git" || -f "$repo/.git" ]] || {
  printf 'Candidate checkout is not a Git worktree: %s\n' "${repo:-<missing>}" >&2
  exit 2
}
repo=$(cd "$repo" && pwd)

run_at() {
  local working_dir=$1
  local label=$2
  shift 2
  printf '\n[promotion-full-gate] %s\n' "$label"
  (
    cd "$working_dir"
    "$@"
  )
}

printf '[promotion-full-gate] candidate=%s\n' "$(git -C "$repo" rev-parse HEAD)"
printf '[promotion-full-gate] dotnet=%s\n' "$(dotnet --version)"
printf '[promotion-full-gate] node=%s\n' "$(node --version)"
printf '[promotion-full-gate] npm=%s\n' "$(npm --version)"

run_at "$repo" '.NET restore' \
  dotnet restore agent-taskboard.sln
run_at "$repo/frontend" 'Frontend dependency install' \
  npm ci
run_at "$repo/frontend" 'Production dependency audit' \
  npm audit --omit=dev --audit-level=critical
run_at "$repo" 'Release shell contract tests' \
  bash scripts/release/release-scripts.test.sh
run_at "$repo" '.NET Release build' \
  dotnet build agent-taskboard.sln --configuration Release --no-restore --nologo
run_at "$repo" '.NET full non-machine-bound suite' \
  dotnet test agent-taskboard.sln --configuration Release --no-build \
    --filter 'Category!=MachineBound' --logger 'console;verbosity=minimal' --nologo
run_at "$repo" 'Deployment regression scenario (inproc smoke)' \
  scripts/scenario.sh --target inproc --level smoke --configuration Release
run_at "$repo/frontend" 'Frontend lint' \
  npm run lint:ci
run_at "$repo/frontend" 'Frontend type-check' \
  npm run typecheck
run_at "$repo/frontend" 'Frontend unit tests' \
  npm run test:ci
run_at "$repo/frontend" 'Frontend production build' \
  npm run build

printf '\nPROMOTION_FULL_GATE=passed\n'
