#!/usr/bin/env bash
# Disposable recovery contract drill. The StudioBackupTests fixture creates an
# encrypted archive, restores into a fresh fake host and signs in as its owner.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runbook="$repo_root/docs/operations/setup/studio-backup-restore.md"
restore="$repo_root/deploy/compose/monolith-networked/restore-backup.sh"

if [ "${1:-}" = --live ]; then
  shift
  if [ "$#" -ne 0 ]; then
    echo 'Usage: fresh-host-recovery-drill.sh [--live]' >&2
    exit 2
  fi
  exec bash "$repo_root/scripts/monolith-networked-backup-restore.test.sh"
fi
if [ "$#" -ne 0 ]; then
  echo 'Usage: fresh-host-recovery-drill.sh [--live]' >&2
  exit 2
fi

for step in {1..11}; do
  rg -q "id=\"fresh-host-step-$step\"" "$runbook" || {
    echo "Missing owner restore step $step" >&2
    exit 1
  }
done
rg -q 'sha256sum --check <archive>.sha256' "$runbook"
rg -q 'Do not start the stack' "$runbook"
rg -q 'Sign in with an existing account' "$runbook"
bash -n "$restore"

# Exercise the mounted-directory fetch and sidecar check from step 2 with a
# disposable off-host fixture. The .NET fixture below uses a real encrypted
# Studio archive for restore and first sign-in.
drill_dir="$(mktemp -d)"
trap 'rm -rf "$drill_dir"' EXIT
mkdir -p "$drill_dir/off-host" "$drill_dir/new-host"
printf 'disposable encrypted archive fixture\n' > "$drill_dir/off-host/studio-backup-drill.tar.gz.sbk"
(cd "$drill_dir/off-host" && sha256sum studio-backup-drill.tar.gz.sbk > studio-backup-drill.tar.gz.sbk.sha256)
cp "$drill_dir/off-host/studio-backup-drill.tar.gz.sbk"{,.sha256} "$drill_dir/new-host/"
(cd "$drill_dir/new-host" && sha256sum --check studio-backup-drill.tar.gz.sbk.sha256)

dotnet test "$repo_root/backend.Tests/OrchestratorApi.Tests.csproj" --no-build \
  --filter 'FullyQualifiedName~StudioBackupTests' \
  --logger 'console;verbosity=normal'
echo 'Fresh-host recovery contract drill passed: linked steps, offline shell syntax, archive restore and owner sign-in.'
