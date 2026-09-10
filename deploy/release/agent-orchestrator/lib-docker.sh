#!/bin/sh
# Shared helpers for the Docker-target control-plane install, update, and
# rollback scripts. Sibling to lib.sh (the systemd target); the two targets
# do not share state or configuration roots.

set -eu

PACKAGE_ID=agent-orchestrator
SERVICE_USER=${AGENT_ORCHESTRATOR_DOCKER_USER:-agent-orchestrator}
CONFIG_ROOT=${AGENT_ORCHESTRATOR_CONFIG_ROOT:-/etc/agent-orchestrator}
COMPOSE_ROOT=${AGENT_ORCHESTRATOR_COMPOSE_ROOT:-/opt/agent-orchestrator/compose}
SECRETS_DIR=${AGENT_ORCHESTRATOR_SECRETS_DIR:-$CONFIG_ROOT/secrets}
ENV_FILE=${AGENT_ORCHESTRATOR_ENV_FILE:-$CONFIG_ROOT/docker.env}
COMPOSE_BIN_OVERRIDE=${COMPOSE_BIN:-}
READY_TIMEOUT_SECONDS=${READY_TIMEOUT_SECONDS:-180}
DRAIN_TIMEOUT_SECONDS=${DRAIN_TIMEOUT_SECONDS:-900}
TASK_PROTOCOL_VERSION=${TASK_PROTOCOL_VERSION:-2}

log()
{
    printf '[agent-orchestrator-docker] %s\n' "$*" >&2
}

die()
{
    printf '[agent-orchestrator-docker] ERROR: %s\n' "$*" >&2
    exit 1
}

require_root()
{
    if [ "${AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK:-0}" != "1" ] && [ "$(id -u)" -ne 0 ]; then
        die "Run this command as root."
    fi
}

resolve_compose_source()
{
    requested=$1
    script_dir=$2
    if [ -n "$requested" ]; then
        [ -d "$requested" ] || die "Compose source not found: $requested"
        (CDPATH= cd -- "$requested" && pwd)
        return
    fi
    for candidate in "$script_dir/compose" "$script_dir/../../compose/control-plane"; do
        if [ -f "$candidate/compose.yaml" ]; then
            (CDPATH= cd -- "$candidate" && pwd)
            return
        fi
    done
    die "Could not find compose.yaml next to this script or at deploy/compose/control-plane. Pass --compose-source <path>."
}

validate_compose_source()
{
    dir=$1
    for required in compose.yaml Caddyfile Caddyfile.private-ca backup-loop.sh; do
        [ -f "$dir/$required" ] || die "Compose source is incomplete; missing $required in $dir."
    done
}

detect_compose_bin()
{
    if [ -n "$COMPOSE_BIN_OVERRIDE" ]; then
        printf '%s\n' "$COMPOSE_BIN_OVERRIDE"
        return
    fi
    if docker compose version >/dev/null 2>&1; then
        printf 'docker compose\n'
        return
    fi
    die "The Docker Compose plugin is not available. Run install-docker.sh first."
}

compose_cmd()
{
    compose_bin=$(detect_compose_bin)
    $compose_bin --project-directory "$COMPOSE_ROOT" --env-file "$ENV_FILE" "$@"
}

install_docker_engine()
{
    if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
        log "Docker Engine and the Compose plugin are already installed."
        return
    fi
    command -v apt-get >/dev/null 2>&1 \
        || die "Automatic install supports apt-based hosts only. Install Docker Engine and the Compose plugin manually, then rerun."
    log "Installing Docker Engine and the Compose plugin."
    apt-get update -y
    apt-get install -y ca-certificates curl gnupg
    install -d -m 0755 /etc/apt/keyrings
    if [ ! -f /etc/apt/keyrings/docker.asc ]; then
        curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
        chmod a+r /etc/apt/keyrings/docker.asc
    fi
    # shellcheck disable=SC1091
    . /etc/os-release
    arch=$(dpkg --print-architecture)
    printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu %s stable\n' \
        "$arch" "$VERSION_CODENAME" >/etc/apt/sources.list.d/docker.list
    apt-get update -y
    apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
    systemctl enable --now docker
}

generate_secret()
{
    file=$1
    preset=${2:-}
    if [ -f "$file" ]; then
        return
    fi
    umask 077
    if [ -n "$preset" ]; then
        printf '%s\n' "$preset" >"$file"
    else
        command -v openssl >/dev/null 2>&1 || die "openssl is required to generate credentials."
        openssl rand -hex 32 >"$file"
    fi
    chmod 0600 "$file"
}

read_env_value()
{
    key=$1
    file=$2
    sed -n "s/^${key}=//p" "$file" | tail -n 1
}

write_env_value()
{
    key=$1
    value=$2
    file=$3
    if grep -q "^${key}=" "$file" 2>/dev/null; then
        tmp="$file.tmp.$$"
        sed "s|^${key}=.*|${key}=${value}|" "$file" >"$tmp"
        mv "$tmp" "$file"
    else
        printf '%s=%s\n' "$key" "$value" >>"$file"
    fi
}

wait_healthy()
{
    deadline=$(( $(date +%s) + READY_TIMEOUT_SECONDS ))
    while [ "$(date +%s)" -le "$deadline" ]; do
        total=$(compose_cmd ps --format json 2>/dev/null | grep -c '"Service":' || true)
        healthy=$(compose_cmd ps --format json 2>/dev/null | grep -o '"Health":"healthy"' | wc -l || true)
        if [ "$total" -gt 0 ] && [ "$healthy" -eq "$total" ]; then
            return 0
        fi
        sleep 2
    done
    return 1
}

# Runs one authenticated management-API request from inside the task-server
# container over its own loopback, so the operator script never needs a
# route to the WireGuard-only edge.
management_request()
{
    method=$1
    path=$2
    data=${3:-}
    remote_cmd="curl -fsS -X $method -H \"Authorization: Bearer \$(cat /run/secrets/studio_token)\" -H 'X-Actor-Id: release-updater' -H 'X-Task-Protocol-Version: $TASK_PROTOCOL_VERSION'"
    if [ -n "$data" ]; then
        remote_cmd="$remote_cmd -H 'Content-Type: application/json' --data-binary @- http://127.0.0.1:5071$path"
        printf '%s' "$data" | compose_cmd exec -T task-server sh -c "$remote_cmd"
    else
        remote_cmd="$remote_cmd http://127.0.0.1:5071$path"
        compose_cmd exec -T task-server sh -c "$remote_cmd"
    fi
}

set_mode()
{
    mode=$1
    reason=$2
    case "$mode" in
        Normal) mode_value=0 ;;
        Draining) mode_value=1 ;;
        ReadOnly) mode_value=2 ;;
        Maintenance) mode_value=3 ;;
        *) die "Unsupported Task Server mode: $mode" ;;
    esac
    management_request PUT /api/v1/management/mode \
        "{\"mode\":$mode_value,\"reason\":\"$reason\"}" >/dev/null
}

wait_ready()
{
    expected_mode=${1:-}
    deadline=$(( $(date +%s) + READY_TIMEOUT_SECONDS ))
    while [ "$(date +%s)" -le "$deadline" ]; do
        response=$(management_request GET /readyz 2>/dev/null || true)
        if printf '%s' "$response" | grep -q '"status":"ready"'; then
            if [ -z "$expected_mode" ] \
                || printf '%s' "$response" | grep -q "\"mode\":\"$expected_mode\""; then
                return 0
            fi
        fi
        sleep 1
    done
    return 1
}

drain_for_switch()
{
    reason=$1
    log "Closing new claims and entering drain mode."
    set_mode Draining "$reason"
    deadline=$(( $(date +%s) + DRAIN_TIMEOUT_SECONDS ))
    while [ "$(date +%s)" -le "$deadline" ]; do
        response=$(management_request POST /api/v1/management/prepare-shutdown \
            "{\"reason\":\"$reason\"}")
        if printf '%s' "$response" | grep -q '"safeToStop":true'; then
            log "All leases are settled; safe shutdown is prepared."
            return 0
        fi
        unresolved=$(printf '%s' "$response" \
            | sed -n 's/.*"unresolvedAttempts":\([0-9][0-9]*\).*/\1/p')
        log "Waiting for ${unresolved:-active} lease(s) to settle."
        sleep 2
    done
    set_mode Normal "update drain timed out; admission restored" || true
    die "Drain timed out after ${DRAIN_TIMEOUT_SECONDS}s; no version was changed."
}
