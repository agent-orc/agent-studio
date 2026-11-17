#!/usr/bin/env bash
# AGT-3839: the concept-remote profile must give the coding host a Codex CLI
# named codex, and leave the review host on the first-run review CLI. The
# runner names a provider after the CLI file name, so a wrapper called
# concept-codex left provider-auth:codex unverifiable on both hosts.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
workspace="$(mktemp -d)"
trap 'rm -rf -- "${workspace}"' EXIT

(
  export REPO_ROOT="${repo_root}" PW_E2E_WORKSPACE="${workspace}" PW_E2E_BACKEND_PORT=1
  source "${repo_root}/scripts/first-run-e2e-fixture.sh"
  first_run_prepare
  source "${repo_root}/scripts/concept-e2e-fixture.sh"
  concept_e2e_prepare
  # Record what each runner would launch instead of starting a host.
  dotnet() { printf '%s %s\n' "${RUNNER_ROLE}" "${RUNNER_CODEX_CLI_BIN}" >> "${workspace}/runners"; }
  first_run_start_runners
  wait
)

coding="$(awk '$1 == "coding" { print $2 }' "${workspace}/runners")"
review="$(awk '$1 == "review" { print $2 }' "${workspace}/runners")"
fail() { echo "FAIL: $*" >&2; exit 1; }

[[ "${coding##*/}" == codex ]] || fail "coding host Codex CLI is not named codex: ${coding}"
[[ "${review##*/}" == codex ]] || fail "review host Codex CLI is not named codex: ${review}"
[[ "${coding}" == "${workspace}/bin/concept/codex" ]] || fail "coding host does not use the concept wrapper: ${coding}"
[[ "${review}" == "${workspace}/bin/review/codex" ]] || fail "review host does not use the review CLI: ${review}"
[[ ! -e "${workspace}/bin/concept-codex" ]] || fail 'a concept-codex binary is still installed'

# The auth probe runs `codex login status` outside the coding worktree.
for cli in "${coding}" "${review}"; do
  status="$(cd "${workspace}" && RUNNER_ROLE=coding "${cli}" login status)"
  [[ "${status}" == 'Logged in using ChatGPT' ]] || fail "${cli} login status answered: ${status}"
done

# A coding CLI with another name refuses to start.
refused="$(
  export REPO_ROOT="${repo_root}" PW_E2E_WORKSPACE="${workspace}" PW_E2E_BACKEND_PORT=1
  source "${repo_root}/scripts/first-run-e2e-fixture.sh"
  dotnet() { :; }
  FIRST_RUN_CODING_CLI_BIN="${workspace}/bin/concept-codex" first_run_start_runners 2>&1 || echo refused
)"
[[ "${refused}" == *'must be named codex'* && "${refused}" == *refused ]] \
  || fail "a misnamed coding CLI was accepted: ${refused}"

echo 'concept-e2e-fixture: ok'
