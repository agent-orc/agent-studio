#!/usr/bin/env bash
# A disposable GitHub transport and provider CLI for the isolated Studio journey.
# The Git shim delegates every operation to real Git against a scratch bare repo.
first_run_prepare() {
  local origin="${PW_E2E_WORKSPACE}/first-run-origin.git"
  local seed="${PW_E2E_WORKSPACE}/first-run-seed"
  mkdir -p "${PW_E2E_WORKSPACE}/bin" "${seed}"
  /usr/bin/git init --quiet --bare "${origin}"
  /usr/bin/git -C "${seed}" init --quiet -b main
  printf 'First-run demo\n' > "${seed}/README.md"
  /usr/bin/git -C "${seed}" -c user.name='First Run E2E' \
    -c user.email='first-run@example.invalid' add .
  /usr/bin/git -C "${seed}" -c user.name='First Run E2E' \
    -c user.email='first-run@example.invalid' commit --quiet -m 'Seed demo'
  /usr/bin/git -C "${seed}" remote add origin "${origin}"
  /usr/bin/git -C "${seed}" push --quiet origin main
  /usr/bin/git -C "${origin}" symbolic-ref HEAD refs/heads/main
  /usr/bin/git config --file "${PW_E2E_WORKSPACE}/gitconfig" user.name 'First Run E2E'
  /usr/bin/git config --file "${PW_E2E_WORKSPACE}/gitconfig" user.email 'first-run@example.invalid'

  cat > "${PW_E2E_WORKSPACE}/bin/git" <<'GIT'
#!/usr/bin/env bash
set -euo pipefail
fixture_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
args=("$@")
network_operation=0
for arg in "${args[@]}"; do
  case "${arg}" in clone|ls-remote|push|fetch) network_operation=1;; esac
done
if (( network_operation )); then
  workdir="$PWD"
  for ((index=0; index<${#args[@]}-1; index++)); do
    if [[ "${args[$index]}" == -C ]]; then workdir="${args[$((index+1))]}"; fi
  done
  configured_origin="$(/usr/bin/git -C "${workdir}" config --get remote.origin.url 2>/dev/null || true)"
  configured_push_origin="$(/usr/bin/git -C "${workdir}" config --get remote.origin.pushurl 2>/dev/null || true)"
  named_origin=0
  for index in "${!args[@]}"; do
    if [[ "${args[$index]}" == 'https://github.com/agent-studio/first-run-e2e.git' ]]; then
      args[$index]="${fixture_root}/first-run-origin.git"
    elif [[ "${args[$index]}" == origin && ( "${configured_origin}" == 'https://github.com/agent-studio/first-run-e2e.git' || "${configured_push_origin}" == 'https://github.com/agent-studio/first-run-e2e.git' ) ]]; then
      named_origin=1
    fi
  done
  if (( named_origin )); then
    args=(-c "url.file://${fixture_root}/first-run-origin.git.insteadOf=https://github.com/agent-studio/first-run-e2e.git"
      "${args[@]}")
  fi
fi
exec /usr/bin/git "${args[@]}"
GIT
  chmod 700 "${PW_E2E_WORKSPACE}/bin/git"

  cat > "${PW_E2E_WORKSPACE}/bin/codex" <<'CODEX'
#!/usr/bin/env bash
set -euo pipefail
fixture_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == '--version' ]]; then printf 'codex 1.0.0\n'; exit 0; fi
if [[ "${1:-}" == login && "${2:-}" == status ]]; then
  printf 'Logged in using ChatGPT\n'
  exit 0
fi
if [[ "${RUNNER_ROLE:-}" == review || "$0" == */review/* ]]; then
  printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"[[ASPECT_VERDICT: status=pass; summary=Demo change verified.]]"}}'
else
  if [[ "$PWD" != "${fixture_root}/coding-work/"* ]]; then
    printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"OK"}}'
    printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
    exit 0
  fi
  mkdir -p docs
  printf 'First-run demo\n\nThis small repository verifies the Studio setup journey.\n' > docs/project-overview.md
  printf '%s\n' '{"type":"item.completed","item":{"type":"agent_message","text":"Created docs/project-overview.md. [[TASK_DONE]]"}}'
fi
printf '%s\n' '{"type":"turn.completed","usage":{"input_tokens":1,"output_tokens":1}}'
CODEX
  chmod 700 "${PW_E2E_WORKSPACE}/bin/codex"
  cat > "${PW_E2E_WORKSPACE}/bin/claude" <<'CLAUDE'
#!/usr/bin/env bash
set -euo pipefail
fixture_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == '--version' ]]; then printf 'claude 1.0.0\n'; exit 0; fi
if [[ "${1:-}" == auth && "${2:-}" == status ]]; then
  printf 'Logged in\n'
  exit 0
fi
if [[ "${RUNNER_ROLE:-}" == review || "$0" == */review/* ]]; then
  printf '[[ASPECT_VERDICT: status=pass; summary=Demo change verified.]]\n'
else
  if [[ "$PWD" != "${fixture_root}/coding-work/"* ]]; then printf 'OK\n'; exit 0; fi
  mkdir -p docs
  printf 'First-run demo\n\nThis small repository verifies the Studio setup journey.\n' > docs/project-overview.md
  printf 'Created docs/project-overview.md. [[TASK_DONE]]\n'
fi
CLAUDE
  chmod 700 "${PW_E2E_WORKSPACE}/bin/claude"
  cc -shared -fPIC -O2 -o "${PW_E2E_WORKSPACE}/review-cpu-max.so" \
    "${REPO_ROOT}/scripts/first-run-e2e-cgroup-shim.c" -ldl
  local first_run_codex_config_dir="${PW_E2E_WORKSPACE}/config/codex"
  local first_run_claude_config_dir="${PW_E2E_WORKSPACE}/config/claude"
  mkdir -p "${first_run_codex_config_dir}" "${first_run_claude_config_dir}"
  printf '{}\n' > "${first_run_codex_config_dir}/auth.json"
  printf '{}\n' > "${first_run_claude_config_dir}/.credentials.json"
  export CODEX_HOME="${first_run_codex_config_dir}"
  export CLAUDE_CONFIG_DIR="${first_run_claude_config_dir}"
  export AGENT_STUDIO_CLEAN_CONTEXT_ROOT="${PW_E2E_WORKSPACE}/clean-context"
  mkdir -p "${PW_E2E_WORKSPACE}/bin/review"
  ln -s ../codex "${PW_E2E_WORKSPACE}/bin/review/codex"
  ln -s ../claude "${PW_E2E_WORKSPACE}/bin/review/claude"
  export PATH="${PW_E2E_WORKSPACE}/bin:${PATH}"
  export Runner__Role=orchestrator
  export CodexCli__Path="${PW_E2E_WORKSPACE}/bin/codex"
  export ClaudeCli__Path="${PW_E2E_WORKSPACE}/bin/claude"
}

first_run_start_runners() {
  local role runner_id coding_slots review_slots preload cli_dir
  FIRST_RUN_RUNNER_PIDS=()
  for role in coding review; do
    runner_id="first-run-${role}"
    coding_slots=0
    review_slots=0
    if [[ "${role}" == coding ]]; then coding_slots=1; else review_slots=1; fi
    preload=''
    if [[ "${role}" == review ]]; then preload="${PW_E2E_WORKSPACE}/review-cpu-max.so"; fi
    cli_dir="${PW_E2E_WORKSPACE}/bin"
    if [[ "${role}" == review ]]; then cli_dir="${cli_dir}/review"; fi
    mkdir -p "${PW_E2E_WORKSPACE}/${role}-work" "${PW_E2E_WORKSPACE}/${role}-state"
    RUNNER_SERVER_URL="http://127.0.0.1:${PW_E2E_BACKEND_PORT}" \
    RUNNER_ID="${runner_id}" RUNNER_NAME="${runner_id}" RUNNER_HOSTNAME=first-run-host \
    RUNNER_ROLE="${role}" RUNNER_CLI_TYPE=codex \
    RUNNER_CODEX_CLI_BIN="${cli_dir}/codex" \
    RUNNER_CLAUDE_CLI_BIN="${cli_dir}/claude" \
    RUNNER_GIT_REMOTE='https://github.com/agent-studio/first-run-e2e.git' \
    RUNNER_GIT_PUSH_REMOTE='https://github.com/agent-studio/first-run-e2e.git' \
    RUNNER_WORKDIR="${PW_E2E_WORKSPACE}/${role}-work" \
    RUNNER_REVIEW_WORKDIR="${PW_E2E_WORKSPACE}/review-attempts" \
    RUNNER_STATE_DIR="${PW_E2E_WORKSPACE}/${role}-state" \
    RUNNER_POLL_SECONDS=1 RUNNER_HEARTBEAT_SECONDS=2 \
    RUNNER_CLAIM_MAX_LOAD_PER_CORE=100 \
    RUNNER_MAX_PARALLELISM=1 RUNNER_HOST_CODING_SLOTS="${coding_slots}" \
    RUNNER_HOST_REVIEW_SLOTS="${review_slots}" \
    AGENT_STUDIO_CLEAN_CONTEXT_ROOT="${PW_E2E_WORKSPACE}/${role}-clean-context" \
    LD_PRELOAD="${preload}" \
    dotnet "${REPO_ROOT}/runner/bin/e2e-publish/agent-host.dll" --poll \
      > "${PW_E2E_WORKSPACE}/${role}-runner.log" 2>&1 &
    FIRST_RUN_RUNNER_PIDS+=("$!")
  done
}

first_run_stop_runners() {
  local pid
  for pid in "${FIRST_RUN_RUNNER_PIDS[@]:-}"; do
    if [[ "${pid}" =~ ^[0-9]+$ ]]; then
      kill "${pid}" 2>/dev/null || true
      wait "${pid}" 2>/dev/null || true
    fi
  done
}
