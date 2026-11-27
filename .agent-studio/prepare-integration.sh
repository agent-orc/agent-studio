#!/usr/bin/env bash
set -euo pipefail
case "${1:?Choose backend or frontend}" in
  backend)
    # Restore remains incremental and revalidates project references and lockfiles.
    dotnet restore agent-taskboard.sln --locked-mode
    ;;
  frontend)
    # The warm worktree owns this marker. Installation failure never writes it.
    inputs=$( { sha256sum frontend/package.json frontend/package-lock.json .nvmrc;
      if [ -f frontend/.npmrc ]; then sha256sum frontend/.npmrc; fi;
      node --version;
      bash scripts/npm-frontend.sh --version;
    } | sha256sum | cut -d ' ' -f 1)
    marker=frontend/node_modules/.integration-inputs
    if [ ! -f "$marker" ] || [ "$(cat "$marker")" != "$inputs" ] || [ ! -f frontend/node_modules/.package-lock.json ]; then
      bash scripts/npm-frontend.sh ci --prefer-offline
      printf '%s\n' "$inputs" > "$marker"
    fi
    ;;
  *) echo 'Choose backend or frontend' >&2; exit 2 ;;
esac
