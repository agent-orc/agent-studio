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
DOCKER_BIN=${DOCKER_BIN:-docker}
# AGT-2947: one versioned installation manifest per installation (Dossier
# AGT-W63 D6 option A). UPGRADE_STATE_FILE holds the in-flight upgrade so an
# interrupted run resumes; MANIFEST_FILE is rendered from it after each phase.
MANIFEST_FILE=${AGENT_ORCHESTRATOR_MANIFEST_FILE:-$CONFIG_ROOT/installation-manifest.json}
UPGRADE_STATE_FILE=${AGENT_ORCHESTRATOR_UPGRADE_STATE_FILE:-$CONFIG_ROOT/installation-upgrade.env}
HOSTS_FILE=${AGENT_ORCHESTRATOR_HOSTS_FILE:-$CONFIG_ROOT/installation-hosts.json}
HOST_ONLINE_WINDOW_SECONDS=${HOST_ONLINE_WINDOW_SECONDS:-300}
CANARY_COMMAND=${AGENT_ORCHESTRATOR_CANARY_COMMAND:-}
MANIFEST_SCHEMA_VERSION=1

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

verify_secret_permissions()
{
    file=$1
    expected_uid=${2:-10001}
    expected_gid=${3:-10001}
    [ -f "$file" ] && [ ! -L "$file" ] && [ -s "$file" ] \
        || die "Secret file is missing, empty or linked: $file"
    actual=$(stat -c '%u:%g:%a' "$file")
    [ "$actual" = "$expected_uid:$expected_gid:600" ] \
        || die "Secret file has unsafe ownership or mode: $file ($actual)"
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

# Runs a management request and filters its JSON with jq inside the
# task-server container (the image ships jq; the host need not).
management_jq()
{
    method=$1
    path=$2
    filter=$3
    remote_cmd="curl -fsS -X $method -H \"Authorization: Bearer \$(cat /run/secrets/studio_token)\" -H 'X-Actor-Id: release-updater' -H 'X-Task-Protocol-Version: $TASK_PROTOCOL_VERSION' http://127.0.0.1:5071$path | jq -r '$filter'"
    compose_cmd exec -T task-server sh -c "$remote_cmd"
}

state_get()
{
    [ -f "$UPGRADE_STATE_FILE" ] || return 0
    read_env_value "$1" "$UPGRADE_STATE_FILE"
}

state_set()
{
    [ -f "$UPGRADE_STATE_FILE" ] || : >"$UPGRADE_STATE_FILE"
    write_env_value "$1" "$2" "$UPGRADE_STATE_FILE"
}

now_utc()
{
    date -u +%Y-%m-%dT%H:%M:%SZ
}

# Prints "<version>\t<storeSchema>\t<protoCurrent>\t<protoMin>\t<protoMax>"
# for the running authority.
observe_authority()
{
    management_jq GET /api/v1/management/status \
        '[.serverVersion, .schemaVersion, .protocol.current, .protocol.minimumSupported, .protocol.maximumSupported] | @tsv'
}

observe_mode()
{
    management_request GET /readyz 2>/dev/null | sed -n 's/.*"mode":"\([A-Za-z]*\)".*/\1/p'
}

# Digest of the image a running Compose service actually uses: the registry
# digest when the image was pulled, otherwise the local image id. A pulled
# but unused image never counts.
running_digest()
{
    service=$1
    container=$(compose_cmd ps -q "$service" 2>/dev/null | head -n 1)
    [ -n "$container" ] || return 0
    image_id=$($DOCKER_BIN inspect --format '{{.Image}}' "$container" 2>/dev/null) || return 0
    $DOCKER_BIN image inspect --format '{{if .RepoDigests}}{{index .RepoDigests 0}}{{else}}{{.Id}}{{end}}' "$image_id" 2>/dev/null || true
}

task_server_image()
{
    image=$(read_env_value TASK_SERVER_IMAGE "$ENV_FILE")
    printf '%s\n' "${image:-ghcr.io/agent-orc/agent-studio-task-server}"
}

engine_image()
{
    image=$(read_env_value ORCHESTRATOR_ENGINE_IMAGE "$ENV_FILE")
    printf '%s\n' "${image:-ghcr.io/agent-orc/agent-studio-orchestrator-engine}"
}

# The prior release is retained only while its images are still present.
release_images_present()
{
    tag=$1
    $DOCKER_BIN image inspect "$(task_server_image):$tag" >/dev/null 2>&1 \
        && $DOCKER_BIN image inspect "$(engine_image):$tag" >/dev/null 2>&1
}

# Records the running release under state keys with the given prefix.
record_observed_release()
{
    prefix=$1
    line=$(observe_authority) || return 1
    tab=$(printf '\t')
    old_ifs=$IFS
    IFS=$tab
    # shellcheck disable=SC2086
    set -- $line
    IFS=$old_ifs
    state_set "${prefix}_VERSION" "$1"
    state_set "${prefix}_SCHEMA" "$2"
    state_set "${prefix}_PROTO_CURRENT" "$3"
    state_set "${prefix}_PROTO_MIN" "$4"
    state_set "${prefix}_PROTO_MAX" "$5"
    state_set "${prefix}_MODE" "$(observe_mode)"
    state_set "${prefix}_AT" "$(now_utc)"
    state_set "${prefix}_DIGEST_AUTHORITY" "$(running_digest task-server)"
    state_set "${prefix}_DIGEST_ENGINE" "$(running_digest orchestrator-engine)"
    state_set "${prefix}_DIGEST_EDGE" "$(running_digest edge)"
}

# Takes a Task Server backup and proves it with a verify-only restore.
verified_backup()
{
    reason=$1
    line=$(management_jq POST /api/v1/management/backups '[.backupId, .sha256] | @tsv' 2>/dev/null) \
        || die "Could not create a pre-upgrade backup ($reason); no version was changed."
    backup_id=$(printf '%s' "$line" | cut -f1)
    backup_sha=$(printf '%s' "$line" | cut -f2)
    [ -n "$backup_id" ] || die "The backup response had no backupId; no version was changed."
    verified=$(management_request POST /api/v1/management/restore \
        "{\"backupId\":\"$backup_id\",\"verifyOnly\":true}" | grep -c '"verified":true' || true)
    [ "$verified" -ge 1 ] || die "Backup $backup_id did not verify; no version was changed."
    state_set BACKUP_ID "$backup_id"
    state_set BACKUP_SHA "$backup_sha"
    state_set BACKUP_SCHEMA "$(state_get FROM_SCHEMA)"
    state_set BACKUP_AT "$(now_utc)"
    log "Verified backup $backup_id."
}

# Writes the authority's view of every runner host. Hosts not seen within
# HOST_ONLINE_WINDOW_SECONDS stay pending whatever their protocol: the Task
# Server's protocol admission checks them when they return. Online hosts
# outside the candidate's protocol range are incompatible.
record_hosts()
{
    pmin=$1
    pmax=$2
    filter="[.[] | select(.status != \"retired\") | {hostId, runnerVersion, protocolVersion, lastSeenAt, online: ((now - ((.lastSeenAt[0:19] + \"Z\") | fromdateiso8601)) <= $HOST_ONLINE_WINDOW_SECONDS)} | .state = (if (.online | not) then \"pending\" elif (.protocolVersion < $pmin or .protocolVersion > $pmax) then \"incompatible\" else \"current\" end)]"
    management_jq GET /api/v1/management/remote-hosts "$filter" >"$HOSTS_FILE.tmp" 2>/dev/null \
        || printf '[]\n' >"$HOSTS_FILE.tmp"
    mv "$HOSTS_FILE.tmp" "$HOSTS_FILE"
}

incompatible_online_hosts()
{
    grep -c '"state": *"incompatible"' "$HOSTS_FILE" 2>/dev/null || true
}

json_string_or_null()
{
    if [ -n "$1" ]; then printf '"%s"' "$1"; else printf 'null'; fi
}

json_number_or_null()
{
    if [ -n "$1" ]; then printf '%s' "$1"; else printf 'null'; fi
}

release_json()
{
    prefix=$1
    version=$(state_get "${prefix}_VERSION")
    if [ -z "$version" ]; then printf 'null'; return; fi
    printf '{"version":"%s","storeSchemaVersion":%s,' "$version" "$(json_number_or_null "$(state_get "${prefix}_SCHEMA")")"
    if [ -n "$(state_get "${prefix}_PROTO_CURRENT")" ]; then
        printf '"protocol":{"current":%s,"minimumSupported":%s,"maximumSupported":%s},' \
            "$(state_get "${prefix}_PROTO_CURRENT")" "$(state_get "${prefix}_PROTO_MIN")" "$(state_get "${prefix}_PROTO_MAX")"
    fi
    printf '"runtimeMode":%s,"observedAt":%s,"components":[' \
        "$(json_string_or_null "$(state_get "${prefix}_MODE")")" "$(json_string_or_null "$(state_get "${prefix}_AT")")"
    printf '{"role":"authority","image":"%s:%s","imageDigest":%s},' "$(task_server_image)" "$version" \
        "$(json_string_or_null "$(state_get "${prefix}_DIGEST_AUTHORITY")")"
    printf '{"role":"engine","image":"%s:%s","imageDigest":%s},' "$(engine_image)" "$version" \
        "$(json_string_or_null "$(state_get "${prefix}_DIGEST_ENGINE")")"
    printf '{"role":"edge","image":"caddy:2-alpine","imageDigest":%s}]}' \
        "$(json_string_or_null "$(state_get "${prefix}_DIGEST_EDGE")")"
}

# Renders the installation manifest from the upgrade state. Desired is the
# target; Observed is what the running containers report; Prior is the
# retained release a rollback may return to.
write_manifest()
{
    installation_id=$(read_env_value CONTROL_PLANE_DOMAIN "$ENV_FILE")
    backup=null
    if [ -n "$(state_get BACKUP_ID)" ]; then
        backup=$(printf '{"backupId":"%s","sha256":"%s","verified":true,"storeSchemaVersion":%s,"createdAt":"%s"}' \
            "$(state_get BACKUP_ID)" "$(state_get BACKUP_SHA)" \
            "$(json_number_or_null "$(state_get BACKUP_SCHEMA)")" "$(state_get BACKUP_AT)")
    fi
    hosts='[]'
    [ -s "$HOSTS_FILE" ] && hosts=$(cat "$HOSTS_FILE")
    tmp="$MANIFEST_FILE.tmp.$$"
    {
        printf '{"schemaVersion":%s,"installationId":"%s","placement":"installed-compose","updater":"docker-lifecycle",\n' \
            "$MANIFEST_SCHEMA_VERSION" "${installation_id:-control-plane}"
        printf '"desired":{"version":"%s","components":[]},\n' "$(state_get TARGET)"
        printf '"observed":%s,\n' "$(release_json OBSERVED)"
        printf '"prior":%s,\n' "$(release_json PRIOR)"
        printf '"backup":%s,\n' "$backup"
        printf '"progress":{"operation":"%s","targetVersion":"%s","phase":"%s","outcome":"%s","reason":%s,"updatedAt":"%s"},\n' \
            "$(state_get OPERATION)" "$(state_get TARGET)" "$(state_get PHASE)" "$(state_get OUTCOME)" \
            "$(json_string_or_null "$(state_get REASON)")" "$(now_utc)"
        printf '"hosts":%s}\n' "$hosts"
    } >"$tmp"
    mv "$tmp" "$MANIFEST_FILE"
}

set_phase()
{
    state_set PHASE "$1"
    write_manifest
    log "Upgrade phase: $1."
}

finish_upgrade()
{
    outcome=$1
    reason=$2
    state_set OUTCOME "$outcome"
    state_set REASON "$reason"
    write_manifest
}

# Copies the FROM_* observation to PRIOR_* (retained release) on success.
retain_prior_release()
{
    for key in VERSION SCHEMA PROTO_CURRENT PROTO_MIN PROTO_MAX MODE AT DIGEST_AUTHORITY DIGEST_ENGINE DIGEST_EDGE; do
        state_set "PRIOR_$key" "$(state_get "FROM_$key")"
    done
    write_env_value CONTROL_PLANE_PREVIOUS_VERSION "$(state_get FROM_VERSION)" "$ENV_FILE"
    write_env_value CONTROL_PLANE_PREVIOUS_SCHEMA "$(state_get FROM_SCHEMA)" "$ENV_FILE"
}

# Runs the operator's detached canary. Without one the upgrade is recorded
# as awaiting-canary, never as succeeded.
run_canary()
{
    if [ -z "$CANARY_COMMAND" ]; then
        return 2
    fi
    sh -c "$CANARY_COMMAND"
}
