#!/usr/bin/env bash
# Identify the exact checkout contents used by a prebuilt E2E artifact.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
(
  cd "${repo_root}"
  git rev-parse HEAD
  git diff --binary HEAD -- . ':!docs/operations/test-acceleration/index.html'
  while IFS= read -r -d '' file; do
    printf '%s\0' "${file}"
    git hash-object -- "${file}"
  done < <(git ls-files --others --exclude-standard -z | sort -z)
) | sha256sum | cut -d ' ' -f 1
