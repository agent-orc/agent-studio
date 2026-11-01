#!/usr/bin/env bash
# Issues or revokes a host's key for fetching GitHub App repository tokens (local profile).
# The local API has no sign-in and is reachable through the hosts' tunnels, so the key is
# issued here, on the Studio machine: only its SHA-256 is stored, and the key itself goes
# to stdout exactly once. Put it into a file readable only by the host's service user and
# name that file in RUNNER_REPOSITORY_KEY_FILE.
#
# Usage: github-app-host-key.sh issue <workspace-repository> <runner-id> <read|write>
#        github-app-host-key.sh revoke <workspace-repository> <runner-id>
set -euo pipefail

usage() {
  sed -n '2,8p' "$0" >&2
  exit 2
}

[[ $# -ge 3 ]] || usage
action=$1 workspace=$2 runner=$3
[[ $runner =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$ ]] || { echo "Invalid runner id: $runner" >&2; exit 2; }
folder="$workspace/.security/github-app/host-keys"

case "$action" in
  issue)
    [[ $# -eq 4 ]] || usage
    access=$4
    [[ $access == read || $access == write ]] || usage
    mkdir -p "$folder"
    # A host holds one level at a time: a new key replaces the previous one.
    rm -f "$folder/$runner.read.sha256" "$folder/$runner.write.sha256"
    key=$(openssl rand -hex 32)
    printf '%s' "$key" | sha256sum | cut -d' ' -f1 > "$folder/$runner.$access.sha256"
    chmod 600 "$folder/$runner.$access.sha256"
    printf '%s\n' "$key"
    ;;
  revoke)
    rm -f "$folder/$runner.read.sha256" "$folder/$runner.write.sha256"
    echo "Revoked $runner. It loses its tokens at its next sync, within five minutes." >&2
    ;;
  *)
    usage
    ;;
esac
