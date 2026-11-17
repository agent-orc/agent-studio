#!/usr/bin/env bash
# Seeds the deploy-lifecycle Playwright profile: a second project whose develop
# branch declares Staging and Production, plus the release evidence root the
# gate-release route reads. The spec registers the gate artifact, plays the
# deploy host over HTTP and does every person's step in the UI.
# Sourced by scripts/serve-prebuilt-backend.sh.

deploy_lifecycle_prepare() {
  local repo="${PW_E2E_WORKSPACE}/projects/deploy-fixture"
  export Deploy__ReleaseEvidenceRoot="${PW_E2E_WORKSPACE}/release-evidence"
  mkdir -p "${Deploy__ReleaseEvidenceRoot}" "${repo}/docs/deployments/environments" "${repo}/.agent-studio"
  git -C "${repo}" init -q -b develop
  git -C "${repo}" config user.name 'Playwright Deploy'
  git -C "${repo}" config user.email 'playwright-deploy@example.invalid'
  # .invalid health URLs: the health monitor never leaves the isolated stack.
  cat > "${repo}/docs/deployments/environments/stage.json" <<'JSON'
{"id":"stage","name":"Staging","kind":"stage","targetHostId":"deploy-e2e-stage","address":"https://stage.deploy-e2e.invalid","template":"container","destination":"8081","healthUrl":"https://stage.deploy-e2e.invalid/health","approval":"person","secretRefs":[]}
JSON
  cat > "${repo}/docs/deployments/environments/production.json" <<'JSON'
{"id":"production","name":"Production","kind":"production","targetHostId":"deploy-e2e-production","address":"https://deploy-e2e.invalid","template":"container","destination":"8082","healthUrl":"https://deploy-e2e.invalid/health","approval":"person","secretRefs":[]}
JSON
  # There is no Task Server verdict store here, so the release gate runs without the full suite.
  { cat "${REPO_ROOT}/.agent-studio/project.yml"; printf '\ndelivery:\n  fullSuite: off\n'; } \
    > "${repo}/.agent-studio/project.yml"
  git -C "${repo}" add -A
  git -C "${repo}" commit -qm 'test: deploy lifecycle declarations'
  export WatchPaths__1__Name='Deploy Fixture'
  export WatchPaths__1__Path="${repo}"
  export WatchPaths__1__RootPath="${repo}"
  export WatchPaths__1__RepositoryPath="${repo}"
}
