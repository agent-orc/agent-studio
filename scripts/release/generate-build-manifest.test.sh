#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t build-manifest-tests)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

initialize_fixture() {
  local fixture=$1
  local version=$2
  git init --quiet "$fixture"
  git -C "$fixture" config user.name 'Release Contract Test'
  git -C "$fixture" config user.email 'release-contract@example.invalid'
  mkdir -p "$fixture/.agent-studio"
  printf '%s\n' "$version" > "$fixture/VERSION"
}

generate() {
  local fixture=$1
  local version=$2
  local car_version=$3
  local car_integrity=$4
  local cac_version=$5
  local cac_integrity=$6
  node "$repo_root/scripts/release/generate-build-manifest.mjs" \
    --root="$fixture" --tag="v$version" --version="$version" \
    --car-version="$car_version" --car-tag="v$car_version" --car-commit=car-commit --car-integrity="$car_integrity" \
    --cac-version="$cac_version" --cac-tag="v$cac_version" --cac-commit=cac-commit --cac-integrity="$cac_integrity"
  node -e "const m=require(process.argv[1]); if(m.tag!=='v$version'||m.codingAgentRunner.version!=='$car_version'||m.codingAgentChat.version!=='$cac_version') process.exit(1)" \
    "$fixture/build-manifest.json"
}

lock_fixture="$test_root/lock-file"
initialize_fixture "$lock_fixture" 1.2.3-lock
mkdir -p "$lock_fixture/backend" "$lock_fixture/frontend"
printf '%s\n' '{"version":1,"dependencies":{"net10.0":{"CodingAgentRunner":{"resolved":"0.7.0","contentHash":"carhash"}}}}' \
  > "$lock_fixture/backend/packages.lock.json"
printf '%s\n' '{"dependencies":{"coding-agent-chat":"0.3.2"}}' \
  > "$lock_fixture/frontend/package.json"
printf '%s\n' '{"packages":{"node_modules/coding-agent-chat":{"version":"0.3.2","resolved":"https://registry.example/coding-agent-chat-0.3.2.tgz","integrity":"sha512-cachash"}}}' \
  > "$lock_fixture/frontend/package-lock.json"
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
  '    - dotnet restore backend/App.csproj --locked-mode' \
  '    - npm --prefix frontend ci' \
  > "$lock_fixture/.agent-studio/project.yml"
printf '#!/bin/sh\nexit 0\n' > "$lock_fixture/.agent-studio/prepare"
git -C "$lock_fixture" add .
git -C "$lock_fixture" commit --quiet -m 'test: lock-file release fixture'
git -C "$lock_fixture" tag -a v1.2.3-lock -m 'fixture release'
generate "$lock_fixture" 1.2.3-lock 0.7.0 sha512-carhash 0.3.2 sha512-cachash

pin_fixture="$test_root/exact-pin"
initialize_fixture "$pin_fixture" 1.2.3-pin
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
  '      version: 0.8.0' \
  '      integrity: sha512-exactcar' \
  '    - package: coding-agent-chat' \
  '      ecosystem: npm' \
  '      version: 0.4.0' \
  '      integrity: sha512-exactcac' \
  '  restore:' \
  '    - restore exact registry packages' \
  > "$pin_fixture/.agent-studio/project.yml"
printf '#!/bin/sh\nexit 0\n' > "$pin_fixture/.agent-studio/prepare"
git -C "$pin_fixture" add .
git -C "$pin_fixture" commit --quiet -m 'test: exact-pin release fixture'
git -C "$pin_fixture" tag -a v1.2.3-pin -m 'fixture release'
generate "$pin_fixture" 1.2.3-pin 0.8.0 sha512-exactcar 0.4.0 sha512-exactcac

printf '%s\n' 'build manifest generator tests passed'
