#!/usr/bin/env bash
# Identify the exact checkout contents used by a prebuilt E2E artifact.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
(
  cd "${repo_root}"
  git rev-parse HEAD
  git diff --binary HEAD -- \
    backend contracts retention cli-hosting tools scripts \
    frontend/src frontend/e2e frontend/scripts \
    frontend/angular.json frontend/package.json frontend/package-lock.json \
    'frontend/tsconfig*.json' 'frontend/playwright*.ts' 'frontend/vitest*.ts' \
    Directory.Build.props Directory.Packages.props global.json
  while IFS= read -r -d '' file; do
    printf '%s\0' "${file}"
    git hash-object -- "${file}"
  done < <(git ls-files --others --exclude-standard -z -- \
    backend contracts retention cli-hosting tools scripts \
    frontend/src frontend/e2e frontend/scripts \
    'frontend/*.ts' 'frontend/*.json' | sort -z)
) | sha256sum | cut -d ' ' -f 1
