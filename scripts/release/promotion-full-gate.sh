#!/usr/bin/env bash
# Repository-wide gate for a develop -> main promotion.
#
# This command is intentionally fixed in source control. The promotion driver
# runs this trusted copy against an isolated checkout of the exact candidate
# merge commit. A green T4 verdict for the same SHA and command digest is reused.

set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage: promotion-full-gate.sh --repo <candidate-checkout> [--project <name> --verdict-api <url> --token-file <path>]

Runs every blocking build, lint, type-check, and non-machine-bound test used to
qualify an Agent Studio develop -> main promotion.
EOF
}

repo=
project=
verdict_api=
token_file=
while (($#)); do
  case "$1" in
    --repo)
      (($# >= 2)) || { usage >&2; exit 2; }
      repo=$2
      shift 2
      ;;
    --project|--verdict-api|--token-file)
      (($# >= 2)) || { usage >&2; exit 2; }
      case "$1" in
        --project) project=$2 ;;
        --verdict-api) verdict_api=$2 ;;
        --token-file) token_file=$2 ;;
      esac
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
candidate_sha=$(git -C "$repo" rev-parse HEAD)
verdict_digest=
verdict_header=
suite_attempted=0
reuse=0
publish_verdict() {
  local status=$1
  [[ -n "$verdict_digest" ]] || return 0
  local body
  body=$(python3 - "$project" "$candidate_sha" "$verdict_digest" "$status" <<'PY'
import datetime, json, sys
print(json.dumps(dict(project=sys.argv[1], commitSha=sys.argv[2], commandDigest=sys.argv[3],
    status=sys.argv[4], finishedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())))
PY
  )
  curl -fsS --max-time 30 -X PUT -H 'Content-Type: application/json' \
    -H "@$verdict_header" --data "$body" "$verdict_api/api/runner/release-suite-verdicts" >/dev/null
}
finish() {
  local rc=$?
  if ((suite_attempted)) && [[ -n "$verdict_digest" ]]; then
    if ((rc == 0)); then publish_verdict green || rc=1
    else publish_verdict red || printf '[promotion-full-gate] red verdict could not be persisted\n' >&2
    fi
  fi
  [[ -z "$verdict_header" ]] || rm -f -- "$verdict_header"
  exit "$rc"
}
trap finish EXIT

if [[ -n "$project" || -n "$verdict_api" || -n "$token_file" ]]; then
  [[ -n "$project" && -n "$verdict_api" && -r "$token_file" ]] || {
    printf 'Verdict lookup requires a project, API URL and readable token file.\n' >&2; exit 2;
  }
  verdict_api=${verdict_api%/}
  verdict_header=$(mktemp)
  chmod 0600 "$verdict_header"
  printf 'Authorization: Bearer %s\n' "$(< "$token_file")" > "$verdict_header"
  encoded_project=$(python3 - "$project" <<'PY'
import sys, urllib.parse
print(urllib.parse.quote(sys.argv[1], safe=''))
PY
  )
  plan=$(curl -fsS --max-time 30 -H "@$verdict_header" \
    "$verdict_api/api/runner/release-candidates/$encoded_project/$candidate_sha")
  read -r mode post_merge_green verdict_digest verdict_status < <(python3 -c '
import json,sys
p=json.load(sys.stdin)
print(p["mode"], str(p["postMergeGreen"]).lower(), p["commandDigest"],
      (p.get("verdict") or {}).get("status", "missing"))' <<< "$plan")
  [[ "$mode" != invalid && "$post_merge_green" == true ]] || {
    printf '[promotion-full-gate] candidate is not the green post-merge tip\n' >&2; exit 1;
  }
  if [[ "$verdict_status" == green ]]; then
    printf '[promotion-full-gate] reused green T4 verdict sha=%s digest=%s\n' "$candidate_sha" "$verdict_digest"
    reuse=1
  fi
  [[ "$mode" == off ]] && verdict_digest=
fi

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

printf '[promotion-full-gate] candidate=%s\n' "$candidate_sha"
printf '[promotion-full-gate] dotnet=%s\n' "$(dotnet --version)"
printf '[promotion-full-gate] node=%s\n' "$(node --version)"
printf '[promotion-full-gate] npm=%s\n' "$(cd "$repo/frontend" && corepack npm --version)"

run_at "$repo" 'Locked dependency identity guard' \
  bash scripts/release/locked-restore.test.sh
run_at "$repo" '.NET solution restore' \
  dotnet restore agent-taskboard.sln --locked-mode
run_at "$repo/frontend" 'Frontend dependency install' \
  corepack npm ci
run_at "$repo/frontend" 'Production dependency audit' \
  corepack npm audit --omit=dev --audit-level=high
run_at "$repo" 'Release shell contract tests' \
  bash scripts/release/release-scripts.test.sh
run_at "$repo" '.NET Release build' \
  dotnet build agent-taskboard.sln --configuration Release --no-restore --nologo
if ((reuse == 0)); then
  suite_attempted=1
  shard_dir=$(mktemp -d)
  declare -a shard_pids=()
  shard_rc=0
  for shard in backend.Tests/OrchestratorApi.Tests.csproj runner.Tests/AgentRunner.Tests.csproj \
    retention.Tests/AgentStudio.Retention.Tests.csproj task-server.Tests/TaskServer.Tests.csproj \
    orchestrator-engine.Tests/OrchestratorEngine.Tests.csproj setup.Tests/AgentOrchestratorSetup.Tests.csproj \
    demo-replay-runner.Tests/DemoReplayRunner.Tests.csproj operations-server.Tests/OperationsServer.Tests.csproj; do
    (
      cd "$repo"
      dotnet test "$shard" --configuration Release --no-build \
        --filter 'Category!=MachineBound' --logger 'console;verbosity=minimal' --nologo
    ) > "$shard_dir/$(basename "$shard").log" 2>&1 &
    shard_pids+=("$!")
    if ((${#shard_pids[@]} == 3)); then
      for shard_pid in "${shard_pids[@]}"; do wait "$shard_pid" || shard_rc=1; done
      shard_pids=()
    fi
  done
  for shard_pid in "${shard_pids[@]}"; do wait "$shard_pid" || shard_rc=1; done
  for shard_log in "$shard_dir"/*.log; do cat "$shard_log"; done
  rm -rf -- "$shard_dir"
  ((shard_rc == 0)) || exit 1
  run_at "$repo" 'Deployment regression scenario (smoke)' \
    dotnet test task-server.Tests/TaskServer.Tests.csproj --configuration Release --no-build \
      --filter 'FullyQualifiedName~TaskServer.Tests.ScenarioTests.Deployment_regression_scenario_smoke' \
      --logger 'console;verbosity=normal'
fi
run_at "$repo/frontend" 'Frontend lint' \
  corepack npm run lint:ci
run_at "$repo/frontend" 'Frontend type-check' \
  corepack npm run typecheck
if ((reuse == 0)); then
  run_at "$repo/frontend" 'Frontend unit tests with coverage' \
    corepack npm run test:release
fi
run_at "$repo/frontend" 'Frontend production build' \
  corepack npm run build

if ((reuse)); then printf '\nPROMOTION_FULL_GATE=reused\n'; fi
printf 'PROMOTION_FULL_GATE=passed\n'
