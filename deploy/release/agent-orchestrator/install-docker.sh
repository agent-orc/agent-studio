#!/bin/sh
# Docker-target control-plane bootstrap: installs Docker Engine and the
# Compose plugin, creates the service user and directories, writes the
# compose env with generated principal credentials, starts the stack, and
# verifies health. Sibling to install.sh (the systemd target); run exactly
# one of the two targets on a given host. See
# docs/operations/setup/control-plane-docker.md.
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source=./lib-docker.sh
. "$SCRIPT_DIR/lib-docker.sh"

require_root

compose_source=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --compose-source)
            [ "$#" -ge 2 ] || die "--compose-source requires a value."
            compose_source=$2
            shift 2
            ;;
        *) die "Unknown option: $1" ;;
    esac
done

install_docker_engine

install -d -m 0755 "$CONFIG_ROOT" "$COMPOSE_ROOT"
install -d -m 0750 "$SECRETS_DIR"

if [ "${AGENT_ORCHESTRATOR_SKIP_USER_CREATE:-0}" != "1" ] \
    && ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --home-dir "$CONFIG_ROOT" --no-create-home \
        --shell /usr/sbin/nologin "$SERVICE_USER"
    usermod -aG docker "$SERVICE_USER"
    log "Created system user $SERVICE_USER (docker group member)."
fi

resolved_source=$(resolve_compose_source "$compose_source" "$SCRIPT_DIR")
validate_compose_source "$resolved_source"
cp -a "$resolved_source/." "$COMPOSE_ROOT/"
log "Installed compose sources to $COMPOSE_ROOT from $resolved_source."

prompt()
{
    label=$1
    default=$2
    value=
    if [ "${NONINTERACTIVE:-0}" != "1" ] && [ -r /dev/tty ]; then
        printf '%s [%s]: ' "$label" "$default" >/dev/tty
        IFS= read -r value </dev/tty || true
    fi
    [ -n "$value" ] || value=$default
    printf '%s\n' "$value"
}

if [ ! -f "$ENV_FILE" ]; then
    version=${CONTROL_PLANE_VERSION:-$(prompt "Release version (image tag)" "0.0.0")}
    runner_id=${CONTROL_PLANE_RUNNER_ID:-$(prompt "Bootstrap Runner id" "agent-runner-01")}
    wg_address=${WG_ADDRESS:-$(prompt "task-server-01 WireGuard address" "10.60.0.1")}
    domain=${CONTROL_PLANE_DOMAIN:-$(prompt "Private control-plane DNS name" "task-server-01.wg.internal")}
    offhost_path=${CONTROL_PLANE_OFFHOST_BACKUP_PATH:-$(prompt "Off-host backup mount path" "/mnt/agent-orchestrator-offhost-backup")}
    [ -d "$offhost_path" ] \
        || die "$offhost_path does not exist. Mount the off-host backup destination first, then rerun."

    cat >"$ENV_FILE" <<EOF
CONTROL_PLANE_VERSION=$version
CONTROL_PLANE_RUNNER_ID=$runner_id
WG_ADDRESS=$wg_address
CONTROL_PLANE_DOMAIN=$domain
CONTROL_PLANE_CADDYFILE=$COMPOSE_ROOT/Caddyfile
CONTROL_PLANE_SECRETS_DIR=$SECRETS_DIR
CONTROL_PLANE_OFFHOST_BACKUP_PATH=$offhost_path
BACKUP_INTERVAL_SECONDS=300
EOF
    chmod 0640 "$ENV_FILE"
    log "Wrote $ENV_FILE."
else
    log "Keeping existing operator configuration in $ENV_FILE."
fi

generate_secret "$SECRETS_DIR/studio.token" "${STUDIO_AUTH_TOKEN:-}"
generate_secret "$SECRETS_DIR/engine.token" "${ENGINE_AUTH_TOKEN:-}"
generate_secret "$SECRETS_DIR/runner.token" "${RUNNER_AUTH_TOKEN:-}"
if [ "${AGENT_ORCHESTRATOR_SKIP_USER_CREATE:-0}" != "1" ]; then
    chown -R "$SERVICE_USER:$SERVICE_USER" "$SECRETS_DIR" 2>/dev/null || true
fi

log "Pulling images and starting the control plane."
compose_cmd pull
compose_cmd up -d

if ! wait_healthy; then
    compose_cmd ps || true
    compose_cmd logs --no-color --tail 200 || true
    die "The control plane did not become healthy within ${READY_TIMEOUT_SECONDS}s."
fi

compose_cmd exec -T task-server curl --fail --silent http://127.0.0.1:5071/healthz >/dev/null \
    || die "task-server did not answer /healthz from inside its own container."

log "Installed and started the Docker control plane."
log "This step does not configure WireGuard, the host firewall, or DNS."
log "Next: deploy/compose/control-plane/wireguard/, then network/configure-firewall.sh, then network/verify-no-public-listener.sh."
