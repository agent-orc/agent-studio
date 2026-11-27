#!/usr/bin/env bash
# Base suite (AGT-3966): one end-to-end start of the product, preceded by a
# deadline-bound start on the production-shaped fixture (AGT-4239).
#
# A merge-window run has just run commands.build (solution Debug build and the
# frontend build) on this checkout. Publish that Debug build without
# rebuilding, reuse the frontend bundle when it was built from HEAD of a clean
# checkout, then run the one product-start spec against the real backend.
# Without such a bundle the e2e runner rebuilds it, so a stand-alone run stays
# correct, only slower.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
browser="${repo_root}/frontend/dist/frontend/browser"
head_sha="$(git -C "${repo_root}" rev-parse HEAD)"
if [[ -f "${browser}/index.html" && -z "$(git -C "${repo_root}" status --porcelain --untracked-files=normal)" ]] \
  && grep -rqs --include='*.js' -- "${head_sha}" "${browser}"; then
  bash "${repo_root}/scripts/e2e-source-fingerprint.sh" > "${browser}/.source-sha"
fi

export PW_E2E_DOTNET_CONFIGURATION="${PW_E2E_DOTNET_CONFIGURATION:-Debug}"
# AGT-4239: a fresh workspace cannot show a start-up that only hangs on real
# data. Start the same build on the production-shaped fixture first, with the
# production start path and a deadline; a failure names the stuck phase.
bash "${repo_root}/scripts/product-start-fixture-check.sh"
export PW_E2E_PUBLISH_NO_BUILD="${PW_E2E_PUBLISH_NO_BUILD:-1}"
exec bash "${repo_root}/scripts/npm-frontend.sh" run e2e -- e2e/system/base-product-start.spec.ts
