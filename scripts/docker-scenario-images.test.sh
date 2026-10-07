#!/usr/bin/env bash
# Shell contract tests for the scenario/smoke Docker residue helpers (AGT-2993):
# scripts/docker-scenario-images.sh removes exactly one project's images and
# scripts/docker-scenario-retention.sh applies its age and in-use filters and
# caps the BuildKit cache. Runs against a stateful fake Docker; no daemon.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d "${TMPDIR:-/tmp}/agent-studio-docker-residue.XXXXXX")"
trap 'rm -rf -- "$test_root"' EXIT

fake_bin="$test_root/bin"
mkdir -p "$fake_bin"
ln -s "$repo_root/scripts/fixtures/fake-docker-image-store.sh" "$fake_bin/docker"
export PATH="$fake_bin:$PATH"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
has_image() { cut -f1 "$FAKE_DOCKER_STATE/images" | grep -Fxq -- "$1"; }
assert_present() { has_image "$1" || fail "$2: $1 was removed"; }
assert_absent() { ! has_image "$1" || fail "$2: $1 is still present"; }

now=1790589600 # 2026-09-28T10:00:00Z
iso() { date -u -d "@$((now - $1))" +%Y-%m-%dT%H:%M:%S.000000000Z; }
go_time() { date -u -d "@$((now - $1))" '+%Y-%m-%d %H:%M:%S.000000000 +0000 UTC'; }
hours() { printf '%s\n' $(($1 * 3600)); }

fresh_state() {
    export FAKE_DOCKER_STATE="$test_root/state-$1"
    rm -rf -- "$FAKE_DOCKER_STATE"
    mkdir -p "$FAKE_DOCKER_STATE"
}
seed() { docker _add "$@"; }

# --- Per-run cleanup: keyed by the Compose project, nothing else. ----------
fresh_state cleanup
seed p1-task-server:local sha256:p1ts com.docker.compose.project=p1,io.agent-studio.disposable-image=scenario "$(iso 60)"
seed p1-agent-host:local sha256:p1ah com.docker.compose.project=p1 "$(iso 60)"
seed p1-unlabelled:local sha256:p1un "" "$(iso 60)"
seed p1-extra-task-server:local sha256:p1x com.docker.compose.project=p1-extra "$(iso 60)"
seed p10-task-server:local sha256:p10 com.docker.compose.project=p10 "$(iso 60)"
seed ghcr.io/agent-orc/agent-task-server:v0.9.4 sha256:ghcr "" "$(iso 60)"

"$repo_root/scripts/docker-scenario-images.sh" p1 p1-unlabelled:local p1-never-built:local 2>"$test_root/cleanup.log"
assert_absent p1-task-server:local "labelled project image"
assert_absent p1-agent-host:local "labelled project image"
assert_absent p1-unlabelled:local "explicit reference"
assert_present p1-extra-task-server:local "project whose name extends the key"
assert_present p10-task-server:local "project whose name extends the key"
assert_present ghcr.io/agent-orc/agent-task-server:v0.9.4 "published image"
grep -F 'project=p1 removed=3 failed=0' "$test_root/cleanup.log" >/dev/null \
    || fail "cleanup summary: $(cat "$test_root/cleanup.log")"

# An image a leftover container still uses is reported, never fatal.
fresh_state cleanup-in-use
seed p2-task-server:local sha256:p2ts com.docker.compose.project=p2 "$(iso 60)"
printf 'c-p2\tsha256:p2ts\n' >"$FAKE_DOCKER_STATE/containers"
"$repo_root/scripts/docker-scenario-images.sh" p2 2>"$test_root/cleanup-in-use.log" \
    || fail "cleanup failed on an image still in use"
grep -F 'project=p2 removed=0 failed=1' "$test_root/cleanup-in-use.log" >/dev/null \
    || fail "in-use cleanup summary: $(cat "$test_root/cleanup-in-use.log")"

# --- Retention: age and in-use filters. ------------------------------------
seed_retention_host() {
    fresh_state "$1"
    # Disposable and unused, older than six hours: removed.
    seed agt9999-verify-task-server:local sha256:old-labelled io.agent-studio.disposable-image=scenario "$(iso "$(hours 7)")" "$(go_time "$(hours 7)")"
    seed agent-studio-smoke-333-task-server-dev:latest sha256:old-prefix "" "$(iso "$(hours 8)")"
    seed agent-studio-scenario-444-studio-bff:local sha256:exact-limit io.agent-studio.disposable-image=scenario "$(iso "$(hours 6)")"
    # Old but used by a running or a stopped container: kept.
    seed agent-studio-scenario-111-task-server:local sha256:running io.agent-studio.disposable-image=scenario "$(iso "$(hours 10)")"
    seed agent-studio-scenario-111-agent-host:local sha256:stopped io.agent-studio.disposable-image=scenario "$(iso "$(hours 10)")"
    # Younger than six hours: kept.
    seed agent-studio-smoke-222-web-dev:latest sha256:young io.agent-studio.disposable-image=compose-smoke "$(iso "$(hours 1)")"
    # A fully cached rebuild keeps the old creation time; the fresh tag protects
    # a run that has built but not started its containers yet.
    seed agent-studio-scenario-555-task-server:local sha256:retagged io.agent-studio.disposable-image=scenario "$(iso "$(hours 48)")" "$(go_time 600)"
    # Not disposable: never a candidate, however old.
    seed ghcr.io/agent-orc/agent-task-server:v0.9.1 sha256:published "" "$(iso "$(hours 100)")"
    seed postgres:16 sha256:postgres "" "$(iso "$(hours 100)")"
    seed agt2739-verify-task-server:local sha256:legacy "" "$(iso "$(hours 30)")"
    printf 'c-running\tsha256:running\nc-exited\tsha256:stopped\n' >"$FAKE_DOCKER_STATE/containers"
}

retention() {
    DOCKER_SCENARIO_RETENTION_NOW="$now" "$repo_root/scripts/docker-scenario-retention.sh" "$@"
}

seed_retention_host retention
retention 2>"$test_root/retention.log" || fail "retention failed: $(cat "$test_root/retention.log")"
assert_absent agt9999-verify-task-server:local "old labelled image under a custom project"
assert_absent agent-studio-smoke-333-task-server-dev:latest "old unlabelled image with a disposable prefix"
assert_absent agent-studio-scenario-444-studio-bff:local "image exactly at the age limit"
assert_present agent-studio-scenario-111-task-server:local "image of a running container"
assert_present agent-studio-scenario-111-agent-host:local "image of a stopped container"
assert_present agent-studio-smoke-222-web-dev:latest "young image"
assert_present agent-studio-scenario-555-task-server:local "freshly tagged image"
assert_present ghcr.io/agent-orc/agent-task-server:v0.9.1 "published image"
assert_present postgres:16 "unrelated image"
assert_present agt2739-verify-task-server:local "unlabelled image outside the default prefixes"
grep -F 'keep agent-studio-scenario-111-task-server:local reason=in-use' "$test_root/retention.log" >/dev/null \
    || fail "in-use keep not reported"
grep -F 'keep agent-studio-smoke-222-web-dev:latest reason=younger-than-6h' "$test_root/retention.log" >/dev/null \
    || fail "young keep not reported"
grep -Fx 'builder prune --force --max-used-space 40GB' "$FAKE_DOCKER_STATE/calls" >/dev/null \
    || fail "BuildKit cache not capped at the 40GB default"

# Extra prefixes reach legacy unlabelled residue; the age limit is configurable.
seed_retention_host retention-options
DOCKER_SCENARIO_RETENTION_MAX_AGE_HOURS=12 retention --prefix agt2739- --skip-build-cache 2>/dev/null
assert_absent agt2739-verify-task-server:local "--prefix candidate"
assert_present agt9999-verify-task-server:local "7h image under a 12h limit"
assert_present agent-studio-smoke-333-task-server-dev:latest "8h image under a 12h limit"
! grep -F 'builder prune --force' "$FAKE_DOCKER_STATE/calls" >/dev/null \
    || fail "--skip-build-cache still pruned the cache"

# The cap is configurable; an older client gets --keep-storage.
seed_retention_host retention-legacy
FAKE_DOCKER_LEGACY_BUILDER=1 DOCKER_SCENARIO_BUILD_CACHE_KEEP_STORAGE=25GB retention 2>/dev/null
grep -Fx 'builder prune --force --keep-storage 25GB' "$FAKE_DOCKER_STATE/calls" >/dev/null \
    || fail "legacy client cache cap"

# Dry run changes nothing.
seed_retention_host retention-dry-run
cp "$FAKE_DOCKER_STATE/images" "$test_root/images-before"
retention --dry-run 2>"$test_root/dry-run.log"
cmp -s "$test_root/images-before" "$FAKE_DOCKER_STATE/images" || fail "--dry-run removed images"
grep -F 'would-remove agt9999-verify-task-server:local' "$test_root/dry-run.log" >/dev/null \
    || fail "--dry-run did not report its candidates"
! grep -F 'builder prune --force' "$FAKE_DOCKER_STATE/calls" >/dev/null || fail "--dry-run pruned the cache"

# If the in-use set cannot be read, nothing is removed.
seed_retention_host retention-no-daemon
cp "$FAKE_DOCKER_STATE/images" "$test_root/images-before"
if FAKE_DOCKER_PS_FAIL=1 retention 2>/dev/null; then
    fail "retention succeeded without reading the containers"
fi
cmp -s "$test_root/images-before" "$FAKE_DOCKER_STATE/images" || fail "retention removed images blind"

# Invalid configuration is refused before anything runs.
if retention --keep-storage lots 2>/dev/null; then fail "accepted --keep-storage lots"; fi
if retention --max-age-hours -1 2>/dev/null; then fail "accepted --max-age-hours -1"; fi

printf 'Docker scenario image cleanup and retention contract tests passed.\n'
