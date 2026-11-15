#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PW_E2E_WORKSPACE="$(mktemp -d "${TMPDIR:-/tmp}/studio-e2e-key-test-XXXXXX")"
trap 'e2e_networked_https_key_cleanup 2>/dev/null || true; rm -rf -- "${PW_E2E_WORKSPACE}"' EXIT
source "${repo_root}/scripts/networked-https-e2e-key.sh"

export AGENT_STUDIO_SECRETS_KEK="$(printf 'fake credential' | base64)"
export Secrets__KeyEnvironmentVariable=INHERITED_KEY_NAME
export Secrets__KeyFile="${PW_E2E_WORKSPACE}/wrong-key"
e2e_networked_https_key_prepare
[[ -z "${AGENT_STUDIO_SECRETS_KEK:-}" ]]
[[ -z "${Secrets__KeyEnvironmentVariable:-}" ]]
[[ "${Secrets__KeyFile}" == "${E2E_KEY_DIR}/secrets.kek" ]]
[[ "${E2E_KEY_DIR}" != "${PW_E2E_WORKSPACE}"/* ]]
[[ "${E2E_KEY_DIR}" != "${repo_root}"/* ]]
[[ "$(stat -c %a "${E2E_KEY_DIR}")" == 700 ]]
[[ "$(stat -c %a "${Secrets__KeyFile}")" == 600 ]]
[[ "$(base64 -d < "${Secrets__KeyFile}" | wc -c)" -eq 32 ]]
first_dir="${E2E_KEY_DIR}"
e2e_networked_https_key_cleanup
[[ ! -e "${first_dir}" ]]

e2e_networked_https_key_prepare
[[ "${E2E_KEY_DIR}" != "${first_dir}" ]]
second_dir="${E2E_KEY_DIR}"
e2e_networked_https_key_cleanup
[[ ! -e "${second_dir}" ]]
printf 'Networked HTTPS disposable key contracts passed.\n'
