#!/usr/bin/env bash
# Hermetic prebuilt startup contract. Run on the remote build host.
# No .NET build or real HTTP listener is used.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d)"
active_pid=
cleanup() {
  if [[ -z "${active_pid}" && -n "${API_FAKE_PID:-}" && -f "${API_FAKE_PID}" ]]; then
    active_pid="$(cat "${API_FAKE_PID}")"
  fi
  if [[ -n "${active_pid}" ]]; then
    kill "${active_pid}" 2>/dev/null || true
    wait "${active_pid}" 2>/dev/null || true
  fi
  rm -rf -- "${test_root}"
}
trap cleanup EXIT HUP INT TERM
fixture="${test_root}/prebuilt launch-stable"
mkdir -p "${fixture}/backend/bin"
cp "${repo_root}/api.sh" "${fixture}/api.sh"
git -C "${fixture}" init -q
git -C "${fixture}" add api.sh
git -C "${fixture}" -c user.name=Fixture -c user.email=fixture@example.invalid \
  -c commit.gpgsign=false -c core.hooksPath=/dev/null commit -qm fixture
source "${fixture}/api.sh"
unset API_PREBUILT_DIR API_REQUIRE_PREBUILT ATP_BUILD_MANIFEST Release__BuildManifestPath
expected_sha="$(git -C "${fixture}" rev-parse HEAD)"
artifact="${fixture}/backend/bin/remote package"

make_artifact() {
  local destination="$1"
  mkdir -p "${destination}"
  printf 'fixture assembly\n' > "${destination}/OrchestratorApi.dll"
  printf '{}\n' > "${destination}/OrchestratorApi.deps.json"
  printf '{"runtimeOptions":{}}\n' > "${destination}/OrchestratorApi.runtimeconfig.json"
  printf '%s\n' "${expected_sha}" > "${destination}/RELEASE-SHA"
}
expect_failure() {
  local expected="$1" output
  if output="$(prepare_launch_command 2>&1)"; then
    printf 'Expected rejection: %s\n' "${expected}" >&2
    exit 1
  fi
  [[ "${output}" == *"${expected}"* ]] || {
    printf 'Expected %s, got: %s\n' "${expected}" "${output}" >&2
    exit 1
  }
}
prepare_launch_command
[[ "${API_LAUNCH_COMMAND[0]}" == dotnet && "${API_LAUNCH_COMMAND[1]}" == run ]]
API_REQUIRE_PREBUILT=1
expect_failure api-prebuilt-required
make_artifact "${artifact}"
API_PREBUILT_DIR='backend/bin/remote package'
prepare_launch_command
[[ "${API_LAUNCH_COMMAND[0]}" == dotnet ]]
[[ "${API_LAUNCH_COMMAND[1]}" == "${artifact}/OrchestratorApi.dll" ]]
[[ "${API_LAUNCH_COMMAND[2]}" == --contentRoot ]]
[[ "${API_LAUNCH_COMMAND[3]}" == "${fixture}/backend" ]]
[[ "${API_LAUNCH_DIRECTORY}" == "${fixture}/backend" ]]

for required in OrchestratorApi.dll OrchestratorApi.deps.json OrchestratorApi.runtimeconfig.json RELEASE-SHA; do
  mv "${artifact}/${required}" "${artifact}/${required}.held"
  expect_failure api-prebuilt-invalid
  mv "${artifact}/${required}.held" "${artifact}/${required}"
done
printf '%040d\n' 0 > "${artifact}/RELEASE-SHA"
expect_failure api-prebuilt-sha-mismatch
printf '%s\n' "${expected_sha}" > "${artifact}/RELEASE-SHA"
printf '{"commit":"%s"}\n' "${expected_sha}" > "${artifact}/build-manifest.json"
prepare_launch_command
printf '{"commit":"wrong"}\n' > "${artifact}/build-manifest.json"
expect_failure api-prebuilt-sha-mismatch
printf '{broken\n' > "${artifact}/build-manifest.json"
expect_failure api-prebuilt-invalid
rm "${artifact}/build-manifest.json"
ATP_BUILD_MANIFEST="${test_root}/override.json"
expect_failure api-prebuilt-invalid
printf '{"commit":"wrong"}\n' > "${ATP_BUILD_MANIFEST}"
expect_failure api-prebuilt-sha-mismatch
printf '{"commit":"%s"}\n' "${expected_sha}" > "${ATP_BUILD_MANIFEST}"
prepare_launch_command
unset ATP_BUILD_MANIFEST

outside="${test_root}/outside"
make_artifact "${outside}"
API_PREBUILT_DIR="${outside}"
expect_failure api-prebuilt-invalid
ln -s "${outside}" "${fixture}/backend/bin/escape"
API_PREBUILT_DIR='backend/bin/escape'
expect_failure api-prebuilt-invalid
stop_marker="${test_root}/stopped"
(
  cmd_stop() { : > "${stop_marker}"; }
  if cmd_restart >/dev/null 2>&1; then exit 1; fi
  [[ ! -e "${stop_marker}" ]]
)

# Exercise the real launch with a fake dotnet and HTTP discovery. Captured
# argv and cwd prove the command does not request restore or build.
API_PREBUILT_DIR="${artifact}"
fake_bin="${test_root}/fake-bin"
mkdir -p "${fake_bin}"
export API_FAKE_PID="${test_root}/server.pid"
export API_FAKE_ARGS="${test_root}/server.args"
export API_FAKE_CWD="${test_root}/server.cwd"
cat > "${fake_bin}/dotnet" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" > "${API_FAKE_ARGS}"
pwd > "${API_FAKE_CWD}"
printf '%s\n' "$$" > "${API_FAKE_PID}"
while true; do sleep 1; done
STUB
chmod +x "${fake_bin}/dotnet"
PATH="${fake_bin}:${PATH}"
require_port_inspection() { return 0; }
listener_pids() {
  if [[ -f "${API_FAKE_PID}" ]]; then cat "${API_FAKE_PID}"; fi
  return 0
}
identity_set() { return 0; }
curl() { printf 200; }
START_TIMEOUT_SECS=5
cmd_start
active_pid="$(cat "${API_FAKE_PID}")"
[[ "$(sed -n '1p' "${API_FAKE_ARGS}")" == "${artifact}/OrchestratorApi.dll" ]]
[[ "$(sed -n '2p' "${API_FAKE_ARGS}")" == --contentRoot ]]
[[ "$(cat "${API_FAKE_CWD}")" == "${fixture}/backend" ]]
[[ "$(cat "${PID_FILE}")" == "${active_pid}" ]]
printf 'Prebuilt API startup contracts passed.\n'
