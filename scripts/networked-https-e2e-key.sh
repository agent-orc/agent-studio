#!/usr/bin/env bash
# A networked Playwright backend needs an explicit key outside its data workspace.

E2E_KEY_DIR=

e2e_networked_https_key_prepare() {
  E2E_KEY_DIR="$(mktemp -d "${TMPDIR:-/tmp}/studio-e2e-key-XXXXXX")"
  (umask 077; openssl rand -base64 32 > "${E2E_KEY_DIR}/secrets.kek")
  unset AGENT_STUDIO_SECRETS_KEK Secrets__KeyEnvironmentVariable
  export Secrets__KeyFile="${E2E_KEY_DIR}/secrets.kek"
}

e2e_networked_https_key_cleanup() {
  if [[ -n "${E2E_KEY_DIR:-}" ]]; then
    rm -rf -- "${E2E_KEY_DIR}"
    E2E_KEY_DIR=
  fi
}
