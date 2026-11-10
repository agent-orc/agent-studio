#!/bin/sh

set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

release="$test_root/release"
cp -a "$repo_root/deploy/release/agent-orchestrator" "$release"
printf '1.0.0\n' >"$release/VERSION"
for binary in task-server orchestrator-engine; do
    printf '#!/bin/sh\nexit 0\n' >"$release/$binary"
    chmod 0755 "$release/$binary"
done

cat >"$test_root/systemctl" <<'SYSTEMCTL'
#!/bin/sh
exit 0
SYSTEMCTL
cat >"$test_root/curl" <<'CURL'
#!/bin/sh
for arg; do url=$arg; done
case "$url" in
    */readyz) printf '{"status":"ready","mode":"Normal"}\n' ;;
    */api/v1/management/mode) printf '{"mode":"Normal"}\n' ;;
    *) exit 1 ;;
esac
CURL
chmod 0755 "$test_root/systemctl" "$test_root/curl"

run_install()
{
    auth_mode=$1
    config_root="$test_root/$auth_mode/config"
    env \
        AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 \
        AGENT_ORCHESTRATOR_SKIP_USER_CREATE=1 \
        AGENT_ORCHESTRATOR_OPT_ROOT="$test_root/$auth_mode/opt" \
        AGENT_ORCHESTRATOR_CONFIG_ROOT="$config_root" \
        AGENT_ORCHESTRATOR_STATE_ROOT="$test_root/$auth_mode/state" \
        AGENT_ORCHESTRATOR_SYSTEMD_ROOT="$test_root/$auth_mode/systemd" \
        AGENT_ORCHESTRATOR_MANAGEMENT_URL=http://127.0.0.1:5071 \
        SYSTEMCTL_BIN="$test_root/systemctl" \
        CURL_BIN="$test_root/curl" \
        NONINTERACTIVE=1 \
        AUTH_MODE="$auth_mode" \
        READY_TIMEOUT_SECONDS=1 \
        "$release/install.sh" "$release" >"$test_root/$auth_mode.log" 2>&1
}

check_install()
{
    auth_mode=$1
    config_root="$test_root/$auth_mode/config"
    configured_path=$(sed -n 's/^OWNER_BOOTSTRAP_CODE_FILE=//p' "$config_root/server.env")
    logged_path=$(sed -n \
        's/^\[agent-orchestrator\] Owner bootstrap code: \(.*\) (read it on this host to claim the first owner account)\.$/\1/p' \
        "$test_root/$auth_mode.log")
    [ "$configured_path" = "$config_root/owner_bootstrap_code" ]
    [ "$logged_path" = "$configured_path" ]
    [ -s "$configured_path" ]
    [ "$(find "$config_root" -maxdepth 1 -type f -name '*bootstrap*' | wc -l)" -eq 1 ]
}

for auth_mode in bearer none; do
    run_install "$auth_mode"
    check_install "$auth_mode"
    code_hash=$(sha256sum "$test_root/$auth_mode/config/owner_bootstrap_code")
    config_hash=$(sha256sum "$test_root/$auth_mode/config/server.env")
    run_install "$auth_mode"
    check_install "$auth_mode"
    [ "$(sha256sum "$test_root/$auth_mode/config/owner_bootstrap_code")" = "$code_hash" ]
    [ "$(sha256sum "$test_root/$auth_mode/config/server.env")" = "$config_hash" ]
done

printf 'Owner bootstrap installer checks passed.\n'
