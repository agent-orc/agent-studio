#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "$test_root"' EXIT

seed_root="$test_root/seeds"
state_home="$test_root/home"
for provider in claude codex gemini; do
  mkdir -p "$seed_root/$provider"
  printf 'initial %s seed\n' "$provider" > "$seed_root/$provider/auth-marker"
done

seed() {
  MONOLITH_CLI_AUTH_SEED_ROOT="$seed_root" MONOLITH_CLI_STATE_HOME="$state_home" \
    sh "$repo_root/scripts/monolith-seed-cli-auth.sh" "$@"
}

seed
for provider in claude codex gemini; do
  test "$(cat "$state_home/.$provider/auth-marker")" = "initial $provider seed"
  test "$(stat -c %a "$state_home/.$provider/auth-marker")" = 600
done

printf 'refreshed codex credential\n' > "$state_home/.codex/auth-marker"
printf 'new codex seed\n' > "$seed_root/codex/auth-marker"
seed
test "$(cat "$state_home/.codex/auth-marker")" = 'refreshed codex credential'

printf 'state-only file\n' > "$state_home/.codex/state-only"
mkdir -p "$test_root/empty-seed/codex"
if MONOLITH_CLI_AUTH_SEED_ROOT="$test_root/empty-seed" MONOLITH_CLI_STATE_HOME="$state_home" \
  sh "$repo_root/scripts/monolith-seed-cli-auth.sh" --force codex 2> "$test_root/empty-seed-error"; then
  echo 'Empty seed unexpectedly replaced CLI state' >&2
  exit 1
fi
test "$(cat "$state_home/.codex/auth-marker")" = 'refreshed codex credential'
seed --force codex
test "$(cat "$state_home/.codex/auth-marker")" = 'new codex seed'
test ! -e "$state_home/.codex/state-only"
test "$(cat "$state_home/.claude/auth-marker")" = 'initial claude seed'

echo 'Monolith CLI auth seed lifecycle passed'
