#!/usr/bin/env bash
# Disposable concept CLI layered on the first-run remote coding/review hosts.
concept_e2e_prepare() {
  export Quota__Mode=fixture-empty
  local seed="${PW_E2E_WORKSPACE}/first-run-seed"
  mkdir -p "${seed}/.agent-studio" "${seed}/scripts"
  cat > "${seed}/.agent-studio/project.yml" <<'YAML'
schemaVersion: 2
project:
  id: concept-e2e
stack: [custom]
commands:
  prepare: .agent-studio/prepare
  test:
    - ./scripts/verify-dossier.sh
YAML
  cat > "${seed}/.agent-studio/prepare" <<'PREPARE'
#!/bin/sh
set -eu
PREPARE
  cat > "${seed}/scripts/verify-dossier.sh" <<'VERIFY'
#!/bin/sh
set -eu
test -f docs/remote-concept/index.html
test -f docs/remote-concept/workbench.json
node -e 'const d=require("./docs/remote-concept/workbench.json"); if(d.implementationTasks.length !== 2) process.exit(1); const h=require("fs").readFileSync("docs/remote-concept/index.html","utf8"); if((h.match(/data-decision-id=/g)||[]).length !== 3) process.exit(1)'
if [ -f src/gate-probe.mjs ]; then ./scripts/verify-implementation.sh; fi
VERIFY
  cat > "${seed}/scripts/verify-implementation.sh" <<'VERIFY_CODE'
#!/bin/sh
set -eu
test -f src/gate-probe.mjs
node -e 'import("./src/gate-probe.mjs").then(m => { if (m.delivered !== "remote concept implementation") process.exit(1); })'
VERIFY_CODE
  chmod 700 "${seed}/.agent-studio/prepare" "${seed}/scripts/verify-dossier.sh" \
    "${seed}/scripts/verify-implementation.sh"
  /usr/bin/git -C "${seed}" add .agent-studio scripts
  /usr/bin/git -C "${seed}" -c user.name='Concept E2E' -c user.email='concept-e2e@example.invalid' \
    commit --quiet -m 'test: define disposable Dossier verification'
  /usr/bin/git -C "${seed}" push --quiet origin main
  cp "${REPO_ROOT}/scripts/concept-e2e-cli.mjs" "${PW_E2E_WORKSPACE}/concept-e2e-cli.mjs"
  cat > "${PW_E2E_WORKSPACE}/bin/concept-codex" <<'CODEX'
#!/usr/bin/env bash
set -euo pipefail
fixture_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == --version ]]; then printf 'codex 1.0.0\n'; exit 0; fi
if [[ "${1:-}" == login || "${RUNNER_ROLE:-}" == review || "$PWD" != "${fixture_root}/coding-work/"* ]]; then
  exec "${fixture_root}/bin/codex" "$@"
fi
node "${fixture_root}/concept-e2e-cli.mjs"
CODEX
  chmod 700 "${PW_E2E_WORKSPACE}/bin/concept-codex"
  cat > "${PW_E2E_WORKSPACE}/concept-summary.md" <<'SUMMARY'
# Status

- Result: Success
- Case: docs
- Duration: 1 minute

## Overview
- Problem: The concept needed a reviewed Dossier.
- Solution: The remote run delivered the Dossier and its implementation plan.

## What Was Done
- Authored the Dossier and recorded its three owner choices.
- Published the two implementation slices for review.

## Open Items
- None.
SUMMARY
  cat > "${PW_E2E_WORKSPACE}/bin/concept-host-codex" <<'HOST_CODEX'
#!/usr/bin/env bash
set -euo pipefail
fixture_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == --version ]]; then printf 'codex 1.0.0\n'; exit 0; fi
prompt="$(cat)"
if [[ "${prompt}" == *'**System instructions (summarizer)**'* ]]; then
  summary="$(cat "${fixture_root}/concept-summary.md")"
  CONCEPT_SUMMARY="${summary}" node -e 'process.stdout.write(JSON.stringify({type:"item.completed",item:{type:"agent_message",text:process.env.CONCEPT_SUMMARY}})+"\n"); process.stdout.write(JSON.stringify({type:"turn.completed",usage:{input_tokens:1,output_tokens:1}})+"\n")'
else
  printf '%s\n' "${prompt}" | "${fixture_root}/bin/codex" "$@"
fi
HOST_CODEX
  chmod 700 "${PW_E2E_WORKSPACE}/bin/concept-host-codex"
  cat > "${PW_E2E_WORKSPACE}/bin/concept-host-claude" <<'HOST_CLAUDE'
#!/usr/bin/env bash
set -euo pipefail
fixture_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == --version || "${1:-}" == auth ]]; then exec "${fixture_root}/bin/claude" "$@"; fi
prompt="$(cat)"
if [[ "${prompt}" == *'**System instructions (summarizer)**'* ]]; then
  cat "${fixture_root}/concept-summary.md"
else
  printf '%s\n' "${prompt}" | "${fixture_root}/bin/claude" "$@"
fi
HOST_CLAUDE
  chmod 700 "${PW_E2E_WORKSPACE}/bin/concept-host-claude"
  export FIRST_RUN_CODING_CLI_BIN="${PW_E2E_WORKSPACE}/bin/concept-codex"
  export CodexCli__Path="${PW_E2E_WORKSPACE}/bin/concept-host-codex"
  export ClaudeCli__Path="${PW_E2E_WORKSPACE}/bin/concept-host-claude"
  export ReviewDecisionOrchestrator__BootDelaySeconds=1
}
