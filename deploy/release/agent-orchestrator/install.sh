#!/bin/sh

set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$SCRIPT_DIR/lib.sh"

require_root

requested=${1:-}
if [ -z "$requested" ]; then
    if [ -f "$SCRIPT_DIR/VERSION" ]; then
        requested=$SCRIPT_DIR
    else
        die "Usage: install.sh <vX.Y.Z|release-directory>"
    fi
fi

resolve_release_source "$requested" "$SCRIPT_DIR"
trap cleanup_resolved_source EXIT HUP INT TERM
source_dir=$RESOLVED_SOURCE
version=$(source_version "$source_dir")
already_active=0
if active_target=$(current_target 2>/dev/null); then
    if [ "$(basename "$active_target")" != "$version" ]; then
        die "Version $(basename "$active_target") is active. Use update.sh for a drained version change."
    fi
    already_active=1
fi

if [ "${AGENT_ORCHESTRATOR_SKIP_USER_CREATE:-0}" != "1" ] \
    && ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --home-dir "$STATE_ROOT" --create-home \
        --shell /usr/sbin/nologin "$SERVICE_USER"
    log "Created system user $SERVICE_USER."
fi

configured_archive_path=${ARCHIVE_PATH:-$STATE_ROOT/archive}
configured_full_backup_path=${BACKUP_PATH_FULL:-$STATE_ROOT/backups/full}
install -d -m 0755 "$OPT_ROOT" "$CONFIG_ROOT" "$SYSTEMD_ROOT"
install -d -m 0750 "$STATE_ROOT" "$STATE_ROOT/backups" "$configured_full_backup_path" "$configured_archive_path"
if [ "${AGENT_ORCHESTRATOR_SKIP_USER_CREATE:-0}" != "1" ]; then
    chown -R "$SERVICE_USER:$SERVICE_USER" "$STATE_ROOT"
    chown "$SERVICE_USER:$SERVICE_USER" "$configured_full_backup_path" "$configured_archive_path"
fi

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

escape_sed()
{
    printf '%s' "$1" | sed 's/[\/&\\]/\\&/g'
}

loopback_listeners_only()
{
    listeners=$1
    old_ifs=$IFS
    IFS=';'
    set -- $listeners
    IFS=$old_ifs
    [ "$#" -gt 0 ] || return 1
    for listener
    do
        listener=$(printf '%s' "$listener" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
        case "$listener" in
            http://127.0.0.1|http://127.0.0.1:*|https://127.0.0.1|https://127.0.0.1:*|\
            http://localhost|http://localhost:*|https://localhost|https://localhost:*|\
            http://\[::1\]|http://\[::1\]:*|https://\[::1\]|https://\[::1\]:*) ;;
            *) return 1 ;;
        esac
    done
}

if [ ! -f "$CONFIG_ROOT/server.env" ]; then
    listen_url=$(prompt "Private Task Server listen URL" "${LISTEN_URL:-http://127.0.0.1:5071}")
    auth_mode=$(prompt "Authentication mode (none or bearer)" "${AUTH_MODE:-bearer}")
    archive_path=$configured_archive_path
    backup_path_full=$configured_full_backup_path
    archive_s3_endpoint=${ARCHIVE_S3_ENDPOINT:-}
    archive_s3_bucket=${ARCHIVE_S3_BUCKET:-}
    archive_s3_prefix=${ARCHIVE_S3_PREFIX:-}
    archive_s3_region=${ARCHIVE_S3_REGION:-us-east-1}
    archive_s3_credentials_file=${ARCHIVE_S3_CREDENTIALS_FILE:-}
    archive_s3_path_style=${ARCHIVE_S3_PATH_STYLE:-false}
    archive_s3_server_checksum=${ARCHIVE_S3_SERVER_SIDE_CHECKSUM:-true}
    case "$auth_mode" in
        none)
            loopback_listeners_only "$listen_url" \
                || die "Authentication mode 'none' is permitted only for loopback LISTEN_URL values."
            studio_token_file=
            engine_token_file=
            studio_token=
            engine_token=
            ;;
        bearer)
            studio_token_file="$CONFIG_ROOT/studio.token"
            engine_token_file="$CONFIG_ROOT/engine.token"
            studio_token=${STUDIO_AUTH_TOKEN:-${AUTH_TOKEN:-}}
            engine_token=${ENGINE_AUTH_TOKEN:-}
            if [ -z "$studio_token" ] || [ -z "$engine_token" ]; then
                command -v openssl >/dev/null 2>&1 \
                    || die "openssl is required to generate the initial principal credentials."
            fi
            [ -n "$studio_token" ] || studio_token=$(openssl rand -hex 32)
            [ -n "$engine_token" ] || engine_token=$(openssl rand -hex 32)
            [ "$studio_token" != "$engine_token" ] \
                || die "Studio and Engine principal credentials must be distinct."
            umask 077
            printf '%s\n' "$studio_token" >"$studio_token_file"
            printf '%s\n' "$engine_token" >"$engine_token_file"
            chown "$SERVICE_USER:$SERVICE_USER" "$studio_token_file" "$engine_token_file" 2>/dev/null || true
            chmod 0640 "$studio_token_file" "$engine_token_file"
            ;;
        *) die "Authentication mode must be 'none' or 'bearer'." ;;
    esac

    sed \
        -e "s/@LISTEN_URL@/$(escape_sed "$listen_url")/" \
        -e "s/@STORE_PATH@/$(escape_sed "$STATE_ROOT")/" \
        -e "s/@BACKUP_PATH@/$(escape_sed "$STATE_ROOT/backups")/" \
        -e "s/@ARCHIVE_PATH@/$(escape_sed "$archive_path")/" \
        -e "s/@BACKUP_PATH_FULL@/$(escape_sed "$backup_path_full")/" \
        -e "s/@ARCHIVE_S3_ENDPOINT@/$(escape_sed "$archive_s3_endpoint")/" \
        -e "s/@ARCHIVE_S3_BUCKET@/$(escape_sed "$archive_s3_bucket")/" \
        -e "s/@ARCHIVE_S3_PREFIX@/$(escape_sed "$archive_s3_prefix")/" \
        -e "s/@ARCHIVE_S3_REGION@/$(escape_sed "$archive_s3_region")/" \
        -e "s/@ARCHIVE_S3_CREDENTIALS_FILE@/$(escape_sed "$archive_s3_credentials_file")/" \
        -e "s/@ARCHIVE_S3_PATH_STYLE@/$(escape_sed "$archive_s3_path_style")/" \
        -e "s/@ARCHIVE_S3_SERVER_SIDE_CHECKSUM@/$(escape_sed "$archive_s3_server_checksum")/" \
        -e "s/@AUTH_MODE@/$(escape_sed "$auth_mode")/" \
        -e "s/@STUDIO_AUTH_TOKEN_FILE@/$(escape_sed "$studio_token_file")/" \
        -e "s/@ENGINE_AUTH_TOKEN_FILE@/$(escape_sed "$engine_token_file")/" \
        "$source_dir/config/server.env.template" >"$CONFIG_ROOT/server.env"
    chmod 0640 "$CONFIG_ROOT/server.env"

    sed \
        -e "s/@SERVER_URL@/$(escape_sed "$listen_url")/" \
        -e '/^CLIENT_CREDENTIAL=@CLIENT_CREDENTIAL@$/d' \
        "$source_dir/config/engine.env.template" >"$CONFIG_ROOT/engine.env"
    printf 'CLIENT_CREDENTIAL=%s\n' "$engine_token" >>"$CONFIG_ROOT/engine.env"
    chmod 0640 "$CONFIG_ROOT/engine.env"
    chown "$SERVICE_USER:$SERVICE_USER" \
        "$CONFIG_ROOT/server.env" "$CONFIG_ROOT/engine.env" 2>/dev/null || true
    log "Created configuration from guided templates in $CONFIG_ROOT."
else
    [ -f "$CONFIG_ROOT/engine.env" ] \
        || die "$CONFIG_ROOT/server.env exists but engine.env is missing; refusing a partial configuration."
    log "Keeping existing operator configuration in $CONFIG_ROOT."
fi

target=$(install_release_tree "$source_dir")
if [ "$already_active" -eq 0 ]; then
    atomic_link "$target" "$OPT_ROOT/current"
fi

if ! install_systemd_units "$target"; then
    if [ "$already_active" -eq 0 ]; then
        rm -f -- "$OPT_ROOT/current"
    fi
    die "Could not install or enable the systemd units."
fi
if [ "$already_active" -eq 1 ] \
    && "$SYSTEMCTL_BIN" is-active --quiet agent-task-server.service \
    && "$SYSTEMCTL_BIN" is-active --quiet agent-orchestrator-engine.service; then
    log "Release $version is already active; services and operator mode were left unchanged."
    install_result="Installation is already current at agent-orchestrator $version."
else
    if ! start_runtime; then
        "$SYSTEMCTL_BIN" stop agent-orchestrator-engine.service >/dev/null 2>&1 || true
        "$SYSTEMCTL_BIN" stop agent-task-server.service >/dev/null 2>&1 || true
        if [ "$already_active" -eq 0 ]; then
            rm -f -- "$OPT_ROOT/current"
        fi
        die "Installed release $version did not become ready; inspect journalctl -u agent-task-server."
    fi
    install_result="Installed and started agent-orchestrator $version."
fi

log "$install_result"
log "Caddy remains host infrastructure. Template: $target/config/Caddyfile.template"
