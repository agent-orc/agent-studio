#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
expected="$(tr -d '\r\n' < "$repo_root/VERSION")"

resolved="$(env -u AGENT_STUDIO_VERSION \
    "$repo_root/scripts/compose-smoke-test.sh" --print-build-version)"
test "$resolved" = "$expected"

resolved="$(AGENT_STUDIO_VERSION="$expected" \
    "$repo_root/scripts/compose-smoke-test.sh" --print-build-version)"
test "$resolved" = "$expected"

error_file="$(mktemp)"
config_root="$(mktemp -d)"
trap 'rm -f "$error_file"; rm -rf "$config_root"' EXIT HUP INT TERM

if AGENT_STUDIO_VERSION=9.9.9 \
    "$repo_root/scripts/compose-smoke-test.sh" --print-build-version \
    > /dev/null 2> "$error_file"; then
    echo "a mismatched explicit build version unexpectedly passed" >&2
    exit 1
fi
grep -F "does not match VERSION $expected" "$error_file" > /dev/null

# Compose must pass the resolved repository version to every dev build. The
# temporary project directory supplies the required runner.env without
# modifying the checkout; config rendering does not contact the Docker daemon.
: > "$config_root/runner.env"
compose_json="$(
    AGENT_STUDIO_VERSION="$resolved" \
    DISTRIBUTED_ENGINE_TOKEN=compose-smoke-version-test \
    docker compose \
        --project-directory "$config_root" \
        -f "$repo_root/docker-compose.yml" \
        --profile dev \
        config --format json
)"

EXPECTED_VERSION="$expected" node -e '
const config = JSON.parse(process.argv[1]);
const builds = Object.entries(config.services)
  .filter(([, service]) => service.build)
  .map(([name, service]) => ({ name, version: service.build.args?.VERSION }));
if (builds.length !== 8) {
  throw new Error(`expected 8 dev builds, found ${builds.length}`);
}
for (const build of builds) {
  if (build.version !== process.env.EXPECTED_VERSION) {
    throw new Error(`${build.name} resolved VERSION=${build.version}`);
  }
}
' "$compose_json"

printf 'Compose smoke build-version resolution passed (%s).\n' "$expected"
