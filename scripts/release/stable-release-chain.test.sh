#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t stable-release-chain)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM
seed="$test_root/seed"
remote="$test_root/remote.git"
stable="$test_root/stable"
metadata="$test_root/workspace/.metadata"
version=9.9.9-dry

git init --bare --quiet "$remote"
git init --quiet "$seed"
git -C "$seed" config user.name 'Stable Release Dry Run'
git -C "$seed" config user.email 'stable-release@example.invalid'
printf '%s\n' 'legacy checkout' > "$seed/legacy.txt"
git -C "$seed" add .
git -C "$seed" commit --quiet -m 'test: legacy stable baseline'
git -C "$seed" branch -M main
git -C "$seed" remote add origin "$remote"
git -C "$seed" push --quiet -u origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main
git clone --quiet "$remote" "$stable"

mkdir -p "$seed/.agent-studio" "$seed/backend" "$seed/frontend"
printf '%s\n' '{"version":1,"dependencies":{"net10.0":{"CodingAgentRunner":{"resolved":"0.7.0","contentHash":"drycar"}}}}' \
  > "$seed/backend/packages.lock.json"
printf '%s\n' '{"dependencies":{"coding-agent-chat":"0.4.1"}}' > "$seed/frontend/package.json"
printf '%s\n' '{"packages":{"node_modules/coding-agent-chat":{"version":"0.4.1","resolved":"https://registry.example/coding-agent-chat-0.4.1.tgz","integrity":"sha512-drycac"}}}' \
  > "$seed/frontend/package-lock.json"
printf '#!/bin/sh\nexit 0\n' > "$seed/.agent-studio/prepare"
printf '%s\n' \
  'schemaVersion: 1' \
  'stack: [dotnet, node]' \
  'toolVersions:' \
  'commands:' \
  '  prepare: .agent-studio/prepare' \
  '  build:' \
  '  test:' \
  '  lint:' \
  'testSuites:' \
  'cachePaths: [frontend/node_modules]' \
  'capabilities: [linux]' \
  'environment:' \
  '  CI: "true"' \
  'release:' \
  '  identity:' \
  '    - package: CodingAgentRunner' \
  '      ecosystem: nuget' \
  '      source: backend/packages.lock.json' \
  '    - package: coding-agent-chat' \
  '      ecosystem: npm' \
  '      source: frontend/package-lock.json' \
  '  restore:' \
  '    - test -f backend/packages.lock.json' \
  '    - test -f frontend/package-lock.json' \
  > "$seed/.agent-studio/project.yml"
git -C "$seed" add .
git -C "$seed" commit --quiet -m 'test: releasable candidate'
candidate=$(git -C "$seed" rev-parse HEAD)
git -C "$seed" tag -a "v$version" -m "Stable dry run v$version"
git -C "$seed" push --quiet origin main "refs/tags/v$version"

node "$repo_root/scripts/release/generate-build-manifest.mjs" \
  --root="$seed" --tag="v$version" --version="$version" \
  --car-version=0.7.0 --car-tag=v0.7.0 --car-commit=dry-car --car-integrity=sha512-drycar \
  --cac-version=0.4.1 --cac-tag=v0.4.1 --cac-commit=dry-cac --cac-integrity=sha512-drycac

mkdir -p "$metadata"
cp "$seed/build-manifest.json" "$metadata/stable-candidate-manifest.json"
printf '%s' "v$version" > "$metadata/stable-approved-tag"
test "$(jq -r .tag "$metadata/stable-candidate-manifest.json")" = "$(cat "$metadata/stable-approved-tag")"

git -C "$stable" fetch --quiet --no-tags origin \
  "refs/tags/v$version:refs/tags/v$version"
test "$(git -C "$stable" rev-parse "refs/tags/v$version^{commit}")" = "$candidate"
for file in .agent-studio/project.yml backend/packages.lock.json frontend/package.json frontend/package-lock.json
do
  git -C "$stable" show "$candidate:$file" >/dev/null
done
git -C "$stable" checkout --quiet --detach "$candidate"
test -f "$stable/backend/packages.lock.json"
test -f "$stable/frontend/package-lock.json"
cp "$metadata/stable-candidate-manifest.json" "$stable/build-manifest.json"
node -e "const fs=require('fs'); const a=JSON.parse(fs.readFileSync(process.argv[1])); const b=JSON.parse(fs.readFileSync(process.argv[2])); if(JSON.stringify(a)!==JSON.stringify(b)||a.commit!==process.argv[3]) process.exit(1)" \
  "$metadata/stable-candidate-manifest.json" "$stable/build-manifest.json" "$candidate"

evidence_dir=${STABLE_RELEASE_DRY_RUN_EVIDENCE_DIR:-}
if [[ -n "$evidence_dir" ]]; then
  mkdir -p "$evidence_dir"
  cp "$metadata/stable-candidate-manifest.json" "$evidence_dir/candidate-manifest.json"
  printf '%s\n' \
    "tag=v$version" \
    "candidate=$candidate" \
    "approved=$(cat "$metadata/stable-approved-tag")" \
    "tagCommit=$(git -C "$stable" rev-parse "refs/tags/v$version^{commit}")" \
    "installedCommit=$(jq -r .commit "$stable/build-manifest.json")" \
    'result=accepted' \
    > "$evidence_dir/summary.txt"
fi

printf 'stable release chain dry run passed: tag=v%s commit=%s\n' "$version" "$candidate"
