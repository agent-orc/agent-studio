#!/usr/bin/env bash
# Redacted Compose isolation and bind-remount regression.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture="$(mktemp -d)"
container="secret-transport-fixture-$$"
secret_volume="secret-transport-onebox-$$"
cleanup() {
    if [ -f "$fixture/distributed-compose.yml" ]; then
        docker compose -f "$fixture/distributed-compose.yml" down >/dev/null 2>&1 || true
    fi
    docker rm -f "$container" >/dev/null 2>&1 || true
    docker volume rm "$secret_volume" >/dev/null 2>&1 || true
    rm -rf "$fixture"
}
trap cleanup EXIT

mkdir -m 0700 "$fixture/secrets" "$fixture/offhost"
for name in studio engine runner; do
    printf 'fixture-%s\n' "$name" >"$fixture/secrets/$name.token"
    chmod 0600 "$fixture/secrets/$name.token"
done
(
    # shellcheck source=../deploy/release/agent-orchestrator/lib-docker.sh
    . "$repo_root/deploy/release/agent-orchestrator/lib-docker.sh"
    verify_secret_permissions "$fixture/secrets/studio.token" "$(id -u)" "$(id -g)"
)
chmod 0644 "$fixture/secrets/studio.token"
if (
    . "$repo_root/deploy/release/agent-orchestrator/lib-docker.sh"
    verify_secret_permissions "$fixture/secrets/studio.token" "$(id -u)" "$(id -g)"
) 2>/dev/null; then
    echo 'FAIL: broad file permissions passed host verification' >&2
    exit 1
fi
chmod 0600 "$fixture/secrets/studio.token"

docker volume create "$secret_volume" >/dev/null
docker run --rm --volume "$secret_volume:/run/agent-studio-secrets" \
    alpine:3.22 sh -c 'printf "legacy-fixture\\n" >/run/agent-studio-secrets/studio_token; chmod 600 /run/agent-studio-secrets/studio_token'
docker run --rm --volume "$secret_volume:/run/agent-studio-secrets" \
    --volume "$repo_root/scripts/compose-secret-bootstrap.sh:/opt/bootstrap.sh:ro" \
    alpine:3.22 sh /opt/bootstrap.sh >/dev/null
docker run --rm --volume "$secret_volume:/run/agent-studio-secrets" \
    --volume "$repo_root/scripts/compose-secret-bootstrap.sh:/opt/bootstrap.sh:ro" \
    alpine:3.22 sh /opt/bootstrap.sh >/dev/null
docker run --rm --user 10001:10001 \
    --mount "type=volume,source=$secret_volume,target=/run/agent-studio-secrets,volume-subpath=studio,readonly" \
    alpine:3.22 sh -c 'test "$(cat /run/agent-studio-secrets/studio_token)" = legacy-fixture'
docker run --rm --user 10001:10001 \
    --mount "type=volume,source=$secret_volume,target=/run/agent-studio-secrets,volume-subpath=runner,readonly" \
    alpine:3.22 sh -c 'test -s /run/agent-studio-secrets/runner_token && test ! -e /run/agent-studio-secrets/studio_token && test "$(stat -c %a /run/agent-studio-secrets/runner_token)" = 600 && ! (printf fixture >>/run/agent-studio-secrets/runner_token) 2>/dev/null'
docker run --rm --user 10001:10001 \
    --mount "type=volume,source=$secret_volume,target=/run/agent-studio-secrets,volume-subpath=studio,readonly" \
    alpine:3.22 sh -c 'test -s /run/agent-studio-secrets/studio_token && test ! -e /run/agent-studio-secrets/runner_token'
for role in engine review_runner; do
    docker run --rm --user 10001:10001 \
        --mount "type=volume,source=$secret_volume,target=/run/agent-studio-secrets,volume-subpath=$role,readonly" \
        alpine:3.22 sh -c 'test -s "/run/agent-studio-secrets/${1}_token" && test ! -e /run/agent-studio-secrets/studio_token && test ! -e /run/agent-studio-secrets/runner_token' \
        fixture "$role"
done
cat >"$fixture/docker.env" <<EOF
CONTROL_PLANE_VERSION=fixture
CONTROL_PLANE_RUNNER_ID=fixture-runner
CONTROL_PLANE_OFFHOST_BACKUP_PATH=$fixture/offhost
CONTROL_PLANE_SECRETS_DIR=$fixture/secrets
WG_ADDRESS=127.0.0.1
CONTROL_PLANE_DOMAIN=localhost
EOF

docker compose --project-directory "$repo_root/deploy/compose/control-plane" \
    --env-file "$fixture/docker.env" -f "$repo_root/deploy/compose/control-plane/compose.yaml" \
    config --format json | jq -e '
      .services as $s |
      [$s["task-server"].secrets[].source] | sort == ["engine_token", "runner_token", "studio_token"] and
      [$s["orchestrator-engine"].secrets[].source] == ["engine_token"] and
      [$s.backup.secrets[].source] == ["studio_token"] and
      ($s.edge.secrets == null or $s.edge.secrets == []) and
      ($s["task-server"].environment | keys | all(.[]; (contains("ANTHROPIC") or contains("OPENAI") or contains("CODEX")) | not))' >/dev/null

cat >"$fixture/distributed-compose.yml" <<EOF
services:
  task-server:
    image: alpine:3.22
    secrets: [studio_token, engine_token, runner_token]
  orchestrator-engine:
    image: alpine:3.22
    secrets: [engine_token]
  backup:
    image: alpine:3.22
    secrets: [studio_token]
  edge:
    image: alpine:3.22
secrets:
  studio_token:
    file: $fixture/secrets/studio.token
  engine_token:
    file: $fixture/secrets/engine.token
  runner_token:
    file: $fixture/secrets/runner.token
EOF
for role in task-server orchestrator-engine backup edge; do
    case "$role" in
        task-server) expected='studio_token engine_token runner_token' ;;
        orchestrator-engine) expected='engine_token' ;;
        backup) expected='studio_token' ;;
        edge) expected='' ;;
    esac
    docker compose -f "$fixture/distributed-compose.yml" run --rm "$role" \
        sh -c 'actual=$(ls /run/secrets 2>/dev/null || true); for name in $actual; do case " $1 " in *" $name "*) ;; *) exit 1;; esac; done; for name in $1; do test -s "/run/secrets/$name" || exit 1; done; test "$(printf "%s\n" $actual | wc -w)" = "$(printf "%s\n" $1 | wc -w)"' \
        fixture "$expected" >/dev/null
done

mkdir -m 0700 "$fixture/bin"
cat >"$fixture/bin/curl" <<'EOF'
#!/bin/sh
printf '%s\n' "$@" >> "$TEST_CURL_ARGS"
case " $* " in
    *"/healthz"*) exit 0 ;;
    *" --config - "*) cat > "$TEST_CURL_CONFIG"; printf 404 ;;
    *) printf 401 ;;
esac
EOF
chmod 0700 "$fixture/bin/curl"
cat >"$fixture/loopback.json" <<'EOF'
{"serverOrigin":"http://127.0.0.1:5071","revision":1,"runners":[{"runnerId":"runner-a","route":"loopback"}]}
EOF
printf 'fixture-bearer-123\n' >"$fixture/route.token"
chmod 0600 "$fixture/route.token"
PATH="$fixture/bin:$PATH" TEST_CURL_ARGS="$fixture/curl.args" \
    TEST_CURL_CONFIG="$fixture/curl.config" \
    sh "$repo_root/deploy/connectivity/probe-runner-route.sh" \
    "$fixture/loopback.json" runner-a "$fixture/route.token" >/dev/null
! rg -q 'fixture-bearer-123' "$fixture/curl.args"
rg -q 'Authorization: Bearer fixture-bearer-123' "$fixture/curl.config"

# Docker file-backed secrets retain their bound inode after an atomic rename.
# The replacement is visible only after the consumer is recreated.
printf 'old-fixture\n' >"$fixture/secrets/remount.token"
chmod 0600 "$fixture/secrets/remount.token"
docker run -d --name "$container" --volume "$fixture/secrets/remount.token:/run/secrets/token:ro" \
    alpine:3.22 sleep 120 >/dev/null
test "$(docker exec "$container" cat /run/secrets/token)" = old-fixture
printf 'new-fixture\n' >"$fixture/secrets/.replacement"
chmod 0600 "$fixture/secrets/.replacement"
mv "$fixture/secrets/.replacement" "$fixture/secrets/remount.token"
test "$(docker exec "$container" cat /run/secrets/token)" = old-fixture
docker rm -f "$container" >/dev/null
docker run -d --name "$container" --volume "$fixture/secrets/remount.token:/run/secrets/token:ro" \
    alpine:3.22 sleep 120 >/dev/null
test "$(docker exec "$container" cat /run/secrets/token)" = new-fixture

printf 'secret-transport-contract=passed\n'
