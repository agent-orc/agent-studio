#!/bin/sh

set -eu

PACKAGE_ID=agent-orchestrator
SERVICE_USER=agent-orchestrator
OPT_ROOT=${AGENT_ORCHESTRATOR_OPT_ROOT:-/opt/agent-orchestrator}
CONFIG_ROOT=${AGENT_ORCHESTRATOR_CONFIG_ROOT:-/etc/agent-orchestrator}
STATE_ROOT=${AGENT_ORCHESTRATOR_STATE_ROOT:-/var/lib/agent-orchestrator}
SYSTEMD_ROOT=${AGENT_ORCHESTRATOR_SYSTEMD_ROOT:-/etc/systemd/system}
SYSTEMCTL_BIN=${SYSTEMCTL_BIN:-systemctl}
CURL_BIN=${CURL_BIN:-curl}
READY_TIMEOUT_SECONDS=${READY_TIMEOUT_SECONDS:-60}
DRAIN_TIMEOUT_SECONDS=${DRAIN_TIMEOUT_SECONDS:-900}
TASK_PROTOCOL_VERSION=${TASK_PROTOCOL_VERSION:-2}
RELEASE_BASE_URL=${AGENT_STUDIO_RELEASE_BASE_URL:-https://github.com/agent-orc/agent-studio/releases/download}

log()
{
    printf '[agent-orchestrator] %s\n' "$*" >&2
}

die()
{
    printf '[agent-orchestrator] ERROR: %s\n' "$*" >&2
    exit 1
}

require_root()
{
    if [ "${AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK:-0}" != "1" ] && [ "$(id -u)" -ne 0 ]; then
        die "Run this command as root."
    fi
}

normalize_version()
{
    candidate=${1#v}
    case "$candidate" in
        ''|*[!0-9.]*|.*|*.) die "Version must be vX.Y.Z or X.Y.Z." ;;
    esac
    old_ifs=$IFS
    IFS=.
    set -- $candidate
    IFS=$old_ifs
    [ "$#" -eq 3 ] || die "Version must contain exactly three numeric components."
    printf '%s\n' "$candidate"
}

source_version()
{
    source_dir=$1
    [ -f "$source_dir/VERSION" ] || die "Release source has no VERSION file: $source_dir"
    normalize_version "$(sed -n '1p' "$source_dir/VERSION")"
}

validate_release_tree()
{
    release_dir=$1
    for required in \
        VERSION \
        task-server \
        orchestrator-engine \
        install.sh \
        update.sh \
        rollback.sh \
        lib.sh \
        config/server.env.template \
        config/engine.env.template \
        config/Caddyfile.template \
        systemd/agent-task-server.service \
        systemd/agent-orchestrator-engine.service \
        systemd/agent-task-server-backup.service \
        systemd/agent-task-server-backup.timer
    do
        [ -f "$release_dir/$required" ] \
            || die "Release source is incomplete; missing $required in $release_dir."
    done
    for executable in task-server orchestrator-engine install.sh update.sh rollback.sh lib.sh
    do
        [ -x "$release_dir/$executable" ] \
            || die "Release source file is not executable: $release_dir/$executable"
    done
}

resolve_release_source()
{
    requested=$1
    script_dir=$2
    RESOLVED_TEMP=

    if [ -d "$requested" ]; then
        RESOLVED_SOURCE=$(CDPATH= cd -- "$requested" && pwd)
        validate_release_tree "$RESOLVED_SOURCE"
        return
    fi

    version=$(normalize_version "$requested")
    if [ -f "$script_dir/VERSION" ] \
        && [ "$(normalize_version "$(sed -n '1p' "$script_dir/VERSION")")" = "$version" ] \
        && [ -x "$script_dir/task-server" ] \
        && [ -x "$script_dir/orchestrator-engine" ]; then
        RESOLVED_SOURCE=$script_dir
        validate_release_tree "$RESOLVED_SOURCE"
        return
    fi

    command -v tar >/dev/null 2>&1 || die "tar is required to download a release."
    command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required to verify a release."
    RESOLVED_TEMP=$(mktemp -d)
    archive="agent-orchestrator-$version-linux-x64.tar.gz"
    release_url="$RELEASE_BASE_URL/v$version"
    log "Downloading $archive from $release_url"
    "$CURL_BIN" -fLSs "$release_url/$archive" -o "$RESOLVED_TEMP/$archive"
    "$CURL_BIN" -fLSs "$release_url/SHA256SUMS" -o "$RESOLVED_TEMP/SHA256SUMS"
    grep -F "  $archive" "$RESOLVED_TEMP/SHA256SUMS" >"$RESOLVED_TEMP/archive.SHA256SUMS" \
        || die "SHA256SUMS does not name $archive."
    (
        cd "$RESOLVED_TEMP"
        sha256sum -c archive.SHA256SUMS
        tar -xzf "$archive"
    )
    RESOLVED_SOURCE="$RESOLVED_TEMP/agent-orchestrator-$version-linux-x64"
    [ -d "$RESOLVED_SOURCE" ] || die "Release archive has an unexpected directory layout."
    validate_release_tree "$RESOLVED_SOURCE"
}

cleanup_resolved_source()
{
    if [ -n "${RESOLVED_TEMP:-}" ] && [ -d "$RESOLVED_TEMP" ]; then
        rm -rf -- "$RESOLVED_TEMP"
    fi
}

install_release_tree()
{
    source_dir=$1
    validate_release_tree "$source_dir"
    version=$(source_version "$source_dir")
    target="$OPT_ROOT/$version"
    install -d -m 0755 "$OPT_ROOT"

    if [ -d "$target" ]; then
        validate_release_tree "$target"
        installed_version=$(normalize_version "$(sed -n '1p' "$target/VERSION")")
        [ "$installed_version" = "$version" ] \
            || die "Existing target $target has version $installed_version, expected $version."
        log "Release $version is already staged."
        printf '%s\n' "$target"
        return
    fi

    staged="$OPT_ROOT/.${version}.staging.$$"
    rm -rf -- "$staged"
    install -d -m 0755 "$staged"
    cp -a "$source_dir/." "$staged/"
    chmod 0755 "$staged/task-server" "$staged/orchestrator-engine" \
        "$staged/install.sh" "$staged/update.sh" "$staged/rollback.sh" "$staged/lib.sh"
    mv "$staged" "$target"
    log "Staged release $version at $target."
    printf '%s\n' "$target"
}

atomic_link()
{
    target=$1
    link=$2
    temporary="$link.new.$$"
    ln -s "$target" "$temporary"
    mv -Tf "$temporary" "$link"
}

current_target()
{
    [ -L "$OPT_ROOT/current" ] || return 1
    readlink -f "$OPT_ROOT/current"
}

read_env_value()
{
    key=$1
    file=$2
    sed -n "s/^${key}=//p" "$file" | tail -n 1
}

management_url()
{
    if [ -n "${AGENT_ORCHESTRATOR_MANAGEMENT_URL:-}" ]; then
        printf '%s\n' "${AGENT_ORCHESTRATOR_MANAGEMENT_URL%/}"
        return
    fi
    listen=$(read_env_value LISTEN_URL "$CONFIG_ROOT/server.env")
    listen=${listen%%;*}
    case "$listen" in
        http://0.0.0.0:*) listen="http://127.0.0.1:${listen##*:}" ;;
        http://\[\:\:\]:*) listen="http://127.0.0.1:${listen##*:}" ;;
    esac
    printf '%s\n' "${listen%/}"
}

auth_token()
{
    token_file=$(read_env_value STUDIO_AUTH_TOKEN_FILE "$CONFIG_ROOT/server.env")
    if [ -z "$token_file" ]; then
        token_file=$(read_env_value AUTH_TOKEN_FILE "$CONFIG_ROOT/server.env")
    fi
    if [ -n "$token_file" ] && [ -f "$token_file" ]; then
        sed -n '1p' "$token_file"
    fi
}

api_request()
{
    method=$1
    path=$2
    data=${3:-}
    base=$(management_url)
    token=$(auth_token)
    if [ -n "$token" ]; then
        if [ -n "$data" ]; then
            "$CURL_BIN" -fsS -X "$method" \
                -H "Authorization: Bearer $token" \
                -H "X-Actor-Id: release-updater" \
                -H "X-Task-Protocol-Version: $TASK_PROTOCOL_VERSION" \
                -H "Content-Type: application/json" \
                --data "$data" \
                "$base$path"
        else
            "$CURL_BIN" -fsS -X "$method" \
                -H "Authorization: Bearer $token" \
                -H "X-Actor-Id: release-updater" \
                -H "X-Task-Protocol-Version: $TASK_PROTOCOL_VERSION" \
                "$base$path"
        fi
    else
        if [ -n "$data" ]; then
            "$CURL_BIN" -fsS -X "$method" \
                -H "X-Actor-Id: release-updater" \
                -H "X-Task-Protocol-Version: $TASK_PROTOCOL_VERSION" \
                -H "Content-Type: application/json" \
                --data "$data" \
                "$base$path"
        else
            "$CURL_BIN" -fsS -X "$method" \
                -H "X-Actor-Id: release-updater" \
                -H "X-Task-Protocol-Version: $TASK_PROTOCOL_VERSION" \
                "$base$path"
        fi
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
    api_request PUT /api/v1/management/mode \
        "{\"mode\":$mode_value,\"reason\":\"$reason\"}" >/dev/null
}

wait_ready()
{
    expected_mode=${1:-}
    deadline=$(( $(date +%s) + READY_TIMEOUT_SECONDS ))
    while [ "$(date +%s)" -le "$deadline" ]; do
        response=$("$CURL_BIN" -fsS "$(management_url)/readyz" 2>/dev/null || true)
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
        response=$(api_request POST /api/v1/management/prepare-shutdown \
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
    die "Drain timed out after $DRAIN_TIMEOUT_SECONDS seconds; no link was changed."
}

stop_runtime()
{
    stop_failed=0
    "$SYSTEMCTL_BIN" stop agent-orchestrator-engine.service || stop_failed=1
    "$SYSTEMCTL_BIN" stop agent-task-server.service || stop_failed=1
    [ "$stop_failed" -eq 0 ]
}

abort_after_stop_failure()
{
    log "Runtime stop failed before the link switch; restarting the current release."
    if ! start_runtime; then
        die "Runtime stop failed and the current release could not be restored."
    fi
    die "Runtime stop failed; the current release was restored and no link was changed."
}

install_systemd_units()
{
    release_dir=$1
    install -m 0644 "$release_dir/systemd/agent-task-server.service" \
        "$SYSTEMD_ROOT/agent-task-server.service" || return 1
    install -m 0644 "$release_dir/systemd/agent-orchestrator-engine.service" \
        "$SYSTEMD_ROOT/agent-orchestrator-engine.service" || return 1
    install -m 0644 "$release_dir/systemd/agent-task-server-backup.service" \
        "$SYSTEMD_ROOT/agent-task-server-backup.service" || return 1
    install -m 0644 "$release_dir/systemd/agent-task-server-backup.timer" \
        "$SYSTEMD_ROOT/agent-task-server-backup.timer" || return 1
    "$SYSTEMCTL_BIN" daemon-reload || return 1
    "$SYSTEMCTL_BIN" enable agent-task-server.service \
        agent-orchestrator-engine.service agent-task-server-backup.timer || return 1
}

start_runtime()
{
    "$SYSTEMCTL_BIN" start agent-task-server.service || return 1
    wait_ready "" || return 1
    set_mode Normal "release runtime ready" || return 1
    wait_ready Normal || return 1
    "$SYSTEMCTL_BIN" start agent-orchestrator-engine.service || return 1
    "$SYSTEMCTL_BIN" start agent-task-server-backup.timer || return 1
}

switch_with_health_gate()
{
    target=$1
    fallback=$2
    candidate_ready=1
    install_systemd_units "$target" || candidate_ready=0
    if [ "$candidate_ready" -eq 1 ]; then
        atomic_link "$target" "$OPT_ROOT/current" || candidate_ready=0
    fi
    if [ "$candidate_ready" -eq 1 ] && start_runtime; then
        return 0
    fi

    log "Candidate failed readiness; automatically restoring $fallback."
    "$SYSTEMCTL_BIN" stop agent-orchestrator-engine.service >/dev/null 2>&1 || true
    "$SYSTEMCTL_BIN" stop agent-task-server.service >/dev/null 2>&1 || true
    install_systemd_units "$fallback" \
        || die "Automatic rollback could not restore the prior systemd units."
    atomic_link "$fallback" "$OPT_ROOT/current" \
        || die "Automatic rollback could not restore the prior current symlink."
    if ! start_runtime; then
        die "Automatic rollback also failed readiness; inspect both systemd units."
    fi
    return 1
}
