#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
stack="${repo_root}/scripts/worktree-test-stack.sh"
capture_scope="${1:-all}"
if [[ "${capture_scope}" != "all" && "${capture_scope}" != "--operator-only" ]]; then
  echo "Usage: $0 [--operator-only]" >&2
  exit 2
fi

cleanup() {
  bash "${stack}" down >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Resetting an isolated ADR-0056 pinned workspace and generating documentation stills"
bash "${stack}" down >/dev/null 2>&1 || true
ATP_TEST_DEMO_PROFILE=local bash "${stack}" up --demo --with-frontend
eval "$(bash "${stack}" env)"

# Central task storage does not imply a repository checkout path in the
# registry. Pin that association explicitly so repository-backed Deck rails
# (Wiki, Git, pipeline context) read the committed demo project, never the
# operator's live checkout.
pin_project_repository() {
  local project_id="$1"
  local project_slug="$2"
  local project_root="${TaskRepository}/projects/${project_slug}"
  curl --fail-with-body --silent --show-error \
    --request PUT \
    --header 'Content-Type: application/json' \
    --header 'X-Client-Id: local-default' \
    --data "{\"repositoryPath\":\"${project_root}\",\"rootPath\":\"${project_root}\"}" \
    "${BACKEND_URL}/api/projects/${project_id}" >/dev/null
}

if [[ "${capture_scope}" == "all" ]]; then
  pin_project_repository "PROJ-001" "demo-app"
  pin_project_repository "PROJ-002" "demo-platform"
fi

cd "${repo_root}/frontend"
if [[ "${capture_scope}" == "all" ]]; then
  PW_VISUAL_CAPTURE=marketing \
    PW_PRESENTATION_ANNOTATIONS="${PW_PRESENTATION_ANNOTATIONS:-1}" \
    PW_BASE_URL="${PW_BASE_URL}" \
    PW_BACKEND_URL="${PW_BACKEND_URL}" \
    npx playwright test \
      e2e/visual-evidence/presentation-capture.spec.ts \
      e2e/visual-evidence/readme-screenshots.spec.ts \
      --project=visual-evidence
fi

PW_VISUAL_CAPTURE=marketing \
  PW_BASE_URL="${PW_BASE_URL}" \
  PW_BACKEND_URL="${PW_BACKEND_URL}" \
  npx playwright test e2e/visual-evidence/operator-page-capture.spec.ts --project=visual-evidence

cd "${repo_root}"
node scripts/visual-docs/validate.mjs

echo "Pinned presentation and visual-library stills are ready."
