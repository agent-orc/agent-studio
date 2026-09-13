#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
lock_file="$repo_root/backend/packages.lock.json"

[[ -f "$lock_file" ]] || {
  printf 'Required NuGet lock file is missing: %s\n' "$lock_file" >&2
  exit 1
}
grep -q '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' \
  "$repo_root/backend/OrchestratorApi.csproj" || {
  printf 'OrchestratorApi.csproj does not require its NuGet lock file.\n' >&2
  exit 1
}

before=$(sha256sum "$lock_file" | cut -d' ' -f1)
dotnet restore "$repo_root/backend/OrchestratorApi.csproj" --locked-mode --nologo
after=$(sha256sum "$lock_file" | cut -d' ' -f1)
[[ "$before" == "$after" ]] || {
  printf 'Locked restore rewrote backend/packages.lock.json.\n' >&2
  exit 1
}

printf '%s\n' 'locked NuGet restore guard passed'
