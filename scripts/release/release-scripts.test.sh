#!/bin/sh

set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

publish_root="$test_root/publish"
frontend_root="$test_root/frontend/browser"
output_root="$test_root/artifacts"
for directory in task-server orchestrator-engine agent-host-linux-x64 agent-host-osx-arm64 setup
do
    install -d -m 0755 "$publish_root/$directory"
done
install -d -m 0755 "$frontend_root"
for executable in \
    task-server/task-server \
    orchestrator-engine/orchestrator-engine \
    agent-host-linux-x64/agent-host \
    agent-host-osx-arm64/agent-host \
    setup/agent-orchestrator-setup
do
    printf '#!/bin/sh\nexit 0\n' >"$publish_root/$executable"
    chmod 0755 "$publish_root/$executable"
done
printf '<!doctype html><title>Agent Studio</title>\n' >"$frontend_root/index.html"

SOURCE_DATE_EPOCH=1 "$repo_root/scripts/release/package-release.sh" \
    1.2.3 0123456789abcdef "$publish_root" "$frontend_root" "$output_root"
(
    cd "$output_root"
    [ "$(find . -maxdepth 1 -name '*.tar.gz' | wc -l)" -eq 3 ]
    [ -x agent-orchestrator-setup ]
    sha256sum -c SHA256SUMS
    tar -tzf agent-orchestrator-1.2.3-linux-x64.tar.gz \
        | grep -q 'agent-orchestrator-1.2.3-linux-x64/update.sh'
    tar -tzf agent-host-1.2.3.tar.gz | grep -q 'agent-host-1.2.3/osx-arm64/agent-host'
    tar -tzf agent-studio-1.2.3.tar.gz | grep -q 'agent-studio-1.2.3/browser/index.html'
)

fake_systemctl="$test_root/systemctl"
cat >"$fake_systemctl" <<'EOF'
#!/bin/sh
set -eu
if [ "${1:-}" = "stop" ] \
    && [ -n "${FAKE_SYSTEMCTL_FAIL_STOP_FILE:-}" ] \
    && [ -f "$FAKE_SYSTEMCTL_FAIL_STOP_FILE" ]; then
    rm -f -- "$FAKE_SYSTEMCTL_FAIL_STOP_FILE"
    exit 1
fi
exit 0
EOF
chmod 0755 "$fake_systemctl"

fake_curl="$test_root/curl"
cat >"$fake_curl" <<'EOF'
#!/bin/sh
set -eu
method=GET
data=
url=
protocol=
while [ "$#" -gt 0 ]; do
    case "$1" in
        -X) method=$2; shift 2 ;;
        --data) data=$2; shift 2 ;;
        -H)
            case "$2" in
                "X-Task-Protocol-Version: "*) protocol=${2#*: } ;;
            esac
            shift 2
            ;;
        -*) shift ;;
        *) url=$1; shift ;;
    esac
done
mode_file=${FAKE_MODE_FILE:?}
mode=$(cat "$mode_file")
case "$url" in
    */readyz)
        active=$(basename "$(readlink -f "${AGENT_ORCHESTRATOR_OPT_ROOT:?}/current")")
        [ "$active" != "9.9.9" ] || exit 22
        printf '{"status":"ready","mode":"%s"}\n' "$mode"
        ;;
    */api/v1/management/mode)
        [ "$protocol" = "2" ] || {
            printf 'missing management protocol header\n' >&2
            exit 22
        }
        mode_value=$(printf '%s' "$data" | sed -n 's/.*"mode":\([0-9][0-9]*\).*/\1/p')
        case "$mode_value" in
            0) mode=Normal ;;
            1) mode=Draining ;;
            2) mode=ReadOnly ;;
            3) mode=Maintenance ;;
            *) printf 'invalid management mode payload: %s\n' "$data" >&2; exit 22 ;;
        esac
        printf '%s\n' "$mode" >"$mode_file"
        printf '{"mode":"%s"}\n' "$mode"
        ;;
    */api/v1/management/prepare-shutdown)
        [ "$protocol" = "2" ] || {
            printf 'missing management protocol header\n' >&2
            exit 22
        }
        printf '%s\n' Maintenance >"$mode_file"
        printf '{"safeToStop":true,"unresolvedAttempts":0,"mode":"Maintenance"}\n'
        ;;
    *) printf 'unexpected URL: %s\n' "$url" >&2; exit 2 ;;
esac
EOF
chmod 0755 "$fake_curl"

make_release()
{
    version=$1
    destination="$test_root/source-$version"
    cp -a "$repo_root/deploy/release/agent-orchestrator" "$destination"
    printf '%s\n' "$version" >"$destination/VERSION"
    printf '#!/bin/sh\nexit 0\n' >"$destination/task-server"
    printf '#!/bin/sh\nexit 0\n' >"$destination/orchestrator-engine"
    chmod 0755 "$destination/task-server" "$destination/orchestrator-engine" \
        "$destination/install.sh" "$destination/update.sh" "$destination/rollback.sh" "$destination/lib.sh"
    printf '%s\n' "$destination"
}

opt_root="$test_root/opt"
config_root="$test_root/etc"
state_root="$test_root/state"
systemd_root="$test_root/systemd"
mode_file="$test_root/mode"
printf '%s\n' Normal >"$mode_file"
release_1=$(make_release 1.0.0)
release_2=$(make_release 1.1.0)
release_bad=$(make_release 9.9.9)

run_env()
{
    env \
        AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 \
        AGENT_ORCHESTRATOR_SKIP_USER_CREATE=1 \
        AGENT_ORCHESTRATOR_OPT_ROOT="$opt_root" \
        AGENT_ORCHESTRATOR_CONFIG_ROOT="$config_root" \
        AGENT_ORCHESTRATOR_STATE_ROOT="$state_root" \
        AGENT_ORCHESTRATOR_SYSTEMD_ROOT="$systemd_root" \
        AGENT_ORCHESTRATOR_MANAGEMENT_URL=http://127.0.0.1:5071 \
        SYSTEMCTL_BIN="$fake_systemctl" \
        CURL_BIN="$fake_curl" \
        FAKE_MODE_FILE="$mode_file" \
        NONINTERACTIVE=1 \
        READY_TIMEOUT_SECONDS=1 \
        DRAIN_TIMEOUT_SECONDS=2 \
        "$@"
}

run_env "$release_1/install.sh" "$release_1"
config_hash=$(sha256sum "$config_root/server.env")
printf '%s\n' Draining >"$mode_file"
run_env "$release_1/install.sh" "$release_1"
[ "$(sha256sum "$config_root/server.env")" = "$config_hash" ]
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.0.0" ]
[ "$(cat "$mode_file")" = "Draining" ]
printf '%s\n' Normal >"$mode_file"

run_env "$release_2/update.sh" "$release_2"
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.1.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.0.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

if run_env "$release_bad/update.sh" "$release_bad"; then
    printf 'Unhealthy update unexpectedly succeeded.\n' >&2
    exit 1
fi
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.1.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.0.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

run_env "$release_2/rollback.sh"
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.0.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.1.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

fail_stop_file="$test_root/fail-stop-once"
: >"$fail_stop_file"
if FAKE_SYSTEMCTL_FAIL_STOP_FILE="$fail_stop_file" \
    run_env "$release_2/update.sh" "$release_2"
then
    printf 'Update with a failed service stop unexpectedly succeeded.\n' >&2
    exit 1
fi
[ "$(basename "$(readlink -f "$opt_root/current")")" = "1.0.0" ]
[ "$(basename "$(readlink -f "$opt_root/previous")")" = "1.1.0" ]
[ "$(cat "$mode_file")" = "Normal" ]

fresh_opt="$test_root/fresh-opt"
fresh_config="$test_root/fresh-etc"
fresh_state="$test_root/fresh-state"
fresh_systemd="$test_root/fresh-systemd"
if env \
    AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 \
    AGENT_ORCHESTRATOR_SKIP_USER_CREATE=1 \
    AGENT_ORCHESTRATOR_OPT_ROOT="$fresh_opt" \
    AGENT_ORCHESTRATOR_CONFIG_ROOT="$fresh_config" \
    AGENT_ORCHESTRATOR_STATE_ROOT="$fresh_state" \
    AGENT_ORCHESTRATOR_SYSTEMD_ROOT="$fresh_systemd" \
    AGENT_ORCHESTRATOR_MANAGEMENT_URL=http://127.0.0.1:5071 \
    SYSTEMCTL_BIN="$fake_systemctl" \
    CURL_BIN="$fake_curl" \
    FAKE_MODE_FILE="$mode_file" \
    NONINTERACTIVE=1 \
    AUTH_MODE=none \
    LISTEN_URL=http://0.0.0.0:5071 \
    "$release_1/install.sh" "$release_1"
then
    printf 'Unauthenticated non-loopback install unexpectedly succeeded.\n' >&2
    exit 1
fi
[ ! -e "$fresh_config/server.env" ]
[ ! -e "$fresh_opt/current" ]

printf '%s\n' Normal >"$mode_file"
if env \
    AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 \
    AGENT_ORCHESTRATOR_SKIP_USER_CREATE=1 \
    AGENT_ORCHESTRATOR_OPT_ROOT="$fresh_opt" \
    AGENT_ORCHESTRATOR_CONFIG_ROOT="$fresh_config" \
    AGENT_ORCHESTRATOR_STATE_ROOT="$fresh_state" \
    AGENT_ORCHESTRATOR_SYSTEMD_ROOT="$fresh_systemd" \
    AGENT_ORCHESTRATOR_MANAGEMENT_URL=http://127.0.0.1:5071 \
    SYSTEMCTL_BIN="$fake_systemctl" \
    CURL_BIN="$fake_curl" \
    FAKE_MODE_FILE="$mode_file" \
    NONINTERACTIVE=1 \
    READY_TIMEOUT_SECONDS=1 \
    "$release_bad/install.sh" "$release_bad"
then
    printf 'Unhealthy first install unexpectedly succeeded.\n' >&2
    exit 1
fi
[ ! -e "$fresh_opt/current" ]

incomplete_release="$test_root/source-incomplete"
cp -a "$release_1" "$incomplete_release"
rm -f -- "$incomplete_release/config/engine.env.template"
if env \
    AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 \
    AGENT_ORCHESTRATOR_SKIP_USER_CREATE=1 \
    AGENT_ORCHESTRATOR_OPT_ROOT="$test_root/incomplete-opt" \
    AGENT_ORCHESTRATOR_CONFIG_ROOT="$test_root/incomplete-etc" \
    AGENT_ORCHESTRATOR_STATE_ROOT="$test_root/incomplete-state" \
    AGENT_ORCHESTRATOR_SYSTEMD_ROOT="$test_root/incomplete-systemd" \
    NONINTERACTIVE=1 \
    "$incomplete_release/install.sh" "$incomplete_release"
then
    printf 'Incomplete release source unexpectedly succeeded.\n' >&2
    exit 1
fi
[ ! -e "$test_root/incomplete-etc/server.env" ]

bash "$repo_root/scripts/release/promote-develop-to-main.test.sh"
bash "$repo_root/scripts/supervisor/test-restart-stable-main-advance.sh"
bash "$repo_root/scripts/scenario.test.sh"

printf 'Release packaging and install/update/rollback tests passed.\n'
