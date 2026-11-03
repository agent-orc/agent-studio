#!/usr/bin/env bash
# Reproduces the style-rule delivery Dossier screenshots.
#
# The pinned demo stack (`worktree-test-stack.sh up --demo`) answers every API
# call with 426 https-required since AGT-2669 (public-demo-readonly profile), so
# this script uses the plain isolated test stack instead. It registers a
# temporary git repository as project "Rule Capture", creates one card through
# the public API, and captures what the real backend renders for that card.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
stack="${repo_root}/scripts/worktree-test-stack.sh"
capture_dir="${repo_root}/docs/style-rule-delivery/capture"
spec_copy="${repo_root}/frontend/e2e/visual-evidence/rule-delivery-capture.spec.ts"
fixture_repo="$(mktemp -d)"

cleanup() {
  rm -f "${spec_copy}"
  rm -rf "${fixture_repo}"
  bash "${stack}" down >/dev/null 2>&1 || true
}
trap cleanup EXIT

bash "${stack}" down >/dev/null 2>&1 || true
bash "${stack}" up --with-frontend
eval "$(bash "${stack}" env)"

git -C "${fixture_repo}" init -q -b develop
printf '# Capture fixture\n' > "${fixture_repo}/AGENTS.md"
git -C "${fixture_repo}" add AGENTS.md
git -C "${fixture_repo}" -c user.email=capture@example.invalid -c user.name=capture commit -qm init

api() {
  curl --fail-with-body --silent --show-error \
    --header 'Content-Type: application/json' \
    --header 'X-Client-Id: local-default' "$@"
}

api --request POST --data "{\"workspaceId\":\"ws-default\",\"displayName\":\"Rule Capture\",\"repositoryPath\":\"${fixture_repo}\"}" \
  "${BACKEND_URL}/api/projects" >/dev/null
watch_path="$(api "${BACKEND_URL}/api/watch-paths" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).find(w=>w.name==="Rule Capture").path))')"
api --request POST --data "{\"id\":\"\",\"title\":\"Header: keep one usage entry per subscription\",\"watchPath\":\"${watch_path}\",\"agent\":\"claude\",\"cliType\":\"claude\",\"promptMarkdown\":\"Capture fixture for the AGT-3465 Dossier. Change frontend/src/app/features/quota/components/header-quota/ so the header shows one usage entry per subscription.\",\"targetState\":\"1-preparation\",\"fixture\":false}" \
  "${BACKEND_URL}/api/tasks" >/dev/null

cp "${capture_dir}/rule-delivery-capture.spec.ts" "${spec_copy}"
cd "${repo_root}/frontend"
# The chromium project ignores e2e/visual-evidence/ outside PW_PREBUILT, so the
# copied spec runs under the visual-evidence project (same Desktop Chrome device).
PW_VISUAL_CAPTURE=marketing PW_BASE_URL="${PW_BASE_URL}" PW_BACKEND_URL="${PW_BACKEND_URL}" \
  npx playwright test e2e/visual-evidence/rule-delivery-capture.spec.ts --project=visual-evidence --retries=0
