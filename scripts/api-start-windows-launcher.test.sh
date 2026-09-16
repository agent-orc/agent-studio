#!/usr/bin/env bash
# Hermetic regression test for the Windows Git Bash launcher-exit shape
# (AGT-2830): `dotnet run`'s launcher PID (what `$!` captures) can exit while
# the process that actually compiles and later owns the port keeps running
# under a different PID. `api.sh start` must not read that launcher exit as
# "the backend crashed" while a build/run process for this project is still
# active, and must only report a confirmed failure once neither the port nor
# such a process exists.
#
# The same hermetic harness also covers the build-manifest handoff (AGT-2847):
# an Update Service run starts the backend with ATP_BUILD_MANIFEST pointing at
# the candidate manifest in its run folder, and that variable has to reach
# `dotnet run` unchanged. A start that silently drops it boots at the
# previously installed identity and makes every upgrade fail its own
# runtime-identity check.
#
# Fakes `dotnet`, `lsof`, and `curl` on PATH so no real .NET build or network
# bind is required; the rest of api.sh (process table, pid liveness) runs for
# real against real background shell processes. Each scenario gets its own
# throwaway checkout (api.sh copied in) so pidfiles and logs never touch the
# real repository checkout.
#
# Run: bash scripts/api-start-windows-launcher.test.sh

set -u
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

test_root="$(mktemp -d 2>/dev/null || mktemp -d -t api-start-windows-launcher)"
trap 'rm -rf -- "${test_root}"' EXIT HUP INT TERM

pass=0; fail=0
ok()  { echo "  ok: $*"; pass=$((pass+1)); }
bad() { echo "FAIL: $*"; fail=$((fail+1)); }

fake_bin="${test_root}/bin"
mkdir -p "${fake_bin}"

# Reads FAKE_LISTEN_MARKER/FAKE_WORKER_PIDFILE/FAKE_PORT at runtime, so one
# stub set serves every scenario below.
cat > "${fake_bin}/lsof" <<'EOF'
#!/usr/bin/env bash
if [[ -n "${FAKE_LISTEN_MARKER:-}" && -f "${FAKE_LISTEN_MARKER}" \
   && -n "${FAKE_WORKER_PIDFILE:-}" && -f "${FAKE_WORKER_PIDFILE}" ]]; then
  pid="$(cat "${FAKE_WORKER_PIDFILE}")"
  printf 'p%s\nn127.0.0.1:%s\n' "${pid}" "${FAKE_PORT:-0}"
fi
exit 0
EOF

cat > "${fake_bin}/curl" <<'EOF'
#!/usr/bin/env bash
if [[ -n "${FAKE_LISTEN_MARKER:-}" && -f "${FAKE_LISTEN_MARKER}" ]]; then
  printf '200'
else
  printf '000'
fi
EOF

# Simulates the Windows Git Bash launcher: exits almost immediately. When
# FAKE_SPAWN_WORKER=1 it first backgrounds fake-worker (the process doing the
# "real" compiling/serving), which is what a genuine crash must NOT leave
# behind.
cat > "${fake_bin}/dotnet" <<EOF
#!/usr/bin/env bash
set -u
# Records the build-manifest environment it inherited, which is what proves
# api.sh passed the Update Service's handoff through to the backend process.
if [[ -n "\${FAKE_ENV_CAPTURE:-}" ]]; then
  printf '%s' "\${ATP_BUILD_MANIFEST:-}" > "\${FAKE_ENV_CAPTURE}"
fi
if [[ "\${FAKE_SPAWN_WORKER:-1}" == "1" ]]; then
  "${fake_bin}/fake-worker" "\$@" &
  disown
fi
sleep 0.2
exit 0
EOF

# The "real" worker: records its own PID immediately (so the fake lsof can
# report it once it starts listening), waits FAKE_WORKER_DELAY seconds to
# simulate a cold compile, then marks itself listening and keeps running.
# Its argv (via "\$@") carries the --project path api.sh launched it with,
# which is what makes it visible to api.sh's own process-table matching.
cat > "${fake_bin}/fake-worker" <<'EOF'
#!/usr/bin/env bash
set -u
echo $$ > "${FAKE_WORKER_PIDFILE}"
sleep "${FAKE_WORKER_DELAY:-0}"
: > "${FAKE_LISTEN_MARKER}"
sleep 999
EOF

chmod +x "${fake_bin}/lsof" "${fake_bin}/curl" "${fake_bin}/dotnet" "${fake_bin}/fake-worker"

# Fresh isolated checkout per scenario: name ends in -stable so the ADR-0044
# dev-backend gate does not need to be acknowledged, and its own pidfiles/logs
# never touch the real repository checkout.
new_checkout() {
  local dir="$1"
  mkdir -p "${dir}/backend"
  cp "${REPO_ROOT}/api.sh" "${dir}/api.sh"
  chmod +x "${dir}/api.sh"
  : > "${dir}/backend/OrchestratorApi.csproj"
}

echo "== scenario: launcher exits while the build is still active =="
checkout_a="${test_root}/proj-a-stable"
new_checkout "${checkout_a}"
out_a="$(cd "${checkout_a}" && env -u PORT -u API_PORT_OVERRIDE \
  PATH="${fake_bin}:${PATH}" \
  FAKE_SPAWN_WORKER=1 FAKE_WORKER_DELAY=2 \
  FAKE_LISTEN_MARKER="${test_root}/listening-a" \
  FAKE_WORKER_PIDFILE="${test_root}/worker-pid-a" \
  FAKE_PORT=5031 \
  API_START_TIMEOUT_SECS=10 \
  bash ./api.sh start 2>&1)"; rc_a=$?
if [[ "${rc_a}" -eq 0 ]] && echo "${out_a}" | grep -q "API is successfully started and healthy"; then
  ok "waits out the launcher exit and reports success once the build finishes (exit ${rc_a})"
else
  bad "expected a successful start once the build finishes; rc=${rc_a} out=${out_a}"
fi
if echo "${out_a}" | grep -qi "exited before it started listening"; then
  bad "must not report the launcher exit as a crash while the build is still active"
else
  ok "did not misreport the launcher exit as a crash"
fi

echo "== scenario: genuine crash (nothing survives the launcher) =="
checkout_b="${test_root}/proj-b-stable"
new_checkout "${checkout_b}"
start_ts=$(date +%s)
out_b="$(cd "${checkout_b}" && env -u PORT -u API_PORT_OVERRIDE \
  PATH="${fake_bin}:${PATH}" \
  FAKE_SPAWN_WORKER=0 \
  FAKE_LISTEN_MARKER="${test_root}/listening-b" \
  FAKE_WORKER_PIDFILE="${test_root}/worker-pid-b" \
  FAKE_PORT=5031 \
  API_START_TIMEOUT_SECS=10 \
  bash ./api.sh start 2>&1)"; rc_b=$?
elapsed_b=$(( $(date +%s) - start_ts ))
if [[ "${rc_b}" -eq 1 ]] && echo "${out_b}" | grep -qi "exited before it started listening"; then
  ok "still reports a confirmed crash when nothing survives the launcher (exit ${rc_b})"
else
  bad "expected a confirmed-crash failure; rc=${rc_b} out=${out_b}"
fi
if (( elapsed_b <= 5 )); then
  ok "fails fast instead of waiting out the full budget (${elapsed_b}s)"
else
  bad "took ${elapsed_b}s to report a confirmed crash; should fail fast"
fi

echo "== scenario: budget runs out while the build is still active =="
checkout_c="${test_root}/proj-c-stable"
new_checkout "${checkout_c}"
out_c="$(cd "${checkout_c}" && env -u PORT -u API_PORT_OVERRIDE \
  PATH="${fake_bin}:${PATH}" \
  FAKE_SPAWN_WORKER=1 FAKE_WORKER_DELAY=30 \
  FAKE_LISTEN_MARKER="${test_root}/listening-c" \
  FAKE_WORKER_PIDFILE="${test_root}/worker-pid-c" \
  FAKE_PORT=5031 \
  API_START_TIMEOUT_SECS=2 \
  bash ./api.sh start 2>&1)"; rc_c=$?
if [[ "${rc_c}" -eq 2 ]] && echo "${out_c}" | grep -qi "still active"; then
  ok "reports the inconclusive still-starting state (exit ${rc_c}), not a crash"
else
  bad "expected exit 2 / still-starting message; rc=${rc_c} out=${out_c}"
fi
if echo "${out_c}" | grep -qi "exited before it started listening"; then
  bad "must not report a crash while the build is still active at the timeout"
else
  ok "did not misreport the timeout as a crash"
fi

echo "== scenario: build-manifest handoff reaches the backend process =="
checkout_d="${test_root}/proj-d-stable"
new_checkout "${checkout_d}"
manifest_d="${test_root}/intended-build-manifest.json"
printf '{"schemaVersion":1}' > "${manifest_d}"
out_d="$(cd "${checkout_d}" && env -u PORT -u API_PORT_OVERRIDE \
  PATH="${fake_bin}:${PATH}" \
  FAKE_SPAWN_WORKER=1 FAKE_WORKER_DELAY=0 \
  FAKE_LISTEN_MARKER="${test_root}/listening-d" \
  FAKE_WORKER_PIDFILE="${test_root}/worker-pid-d" \
  FAKE_ENV_CAPTURE="${test_root}/env-capture-d" \
  FAKE_PORT=5031 \
  API_START_TIMEOUT_SECS=10 \
  ATP_BUILD_MANIFEST="${manifest_d}" \
  bash ./api.sh start 2>&1)"; rc_d=$?
if [[ "${rc_d}" -eq 0 ]] && [[ "$(cat "${test_root}/env-capture-d" 2>/dev/null)" == "${manifest_d}" ]]; then
  ok "passes ATP_BUILD_MANIFEST through to the launched backend"
else
  bad "expected the manifest handoff in the backend environment; rc=${rc_d} captured=$(cat "${test_root}/env-capture-d" 2>/dev/null) out=${out_d}"
fi
if echo "${out_d}" | grep -q "Build manifest override: ${manifest_d}"; then
  ok "names the manifest override it started with"
else
  bad "expected the override to be named in the start output; out=${out_d}"
fi

echo "== scenario: build-manifest handoff names a file that is not there =="
checkout_e="${test_root}/proj-e-stable"
new_checkout "${checkout_e}"
out_e="$(cd "${checkout_e}" && env -u PORT -u API_PORT_OVERRIDE \
  PATH="${fake_bin}:${PATH}" \
  FAKE_SPAWN_WORKER=1 FAKE_WORKER_DELAY=0 \
  FAKE_LISTEN_MARKER="${test_root}/listening-e" \
  FAKE_WORKER_PIDFILE="${test_root}/worker-pid-e" \
  FAKE_ENV_CAPTURE="${test_root}/env-capture-e" \
  FAKE_PORT=5031 \
  API_START_TIMEOUT_SECS=10 \
  ATP_BUILD_MANIFEST="${test_root}/gone/intended-build-manifest.json" \
  bash ./api.sh start 2>&1)"; rc_e=$?
if [[ "${rc_e}" -ne 0 ]] && echo "${out_e}" | grep -qi "not a readable file"; then
  ok "refuses to start rather than booting at the previously installed identity (exit ${rc_e})"
else
  bad "expected a refusal for an unreadable manifest handoff; rc=${rc_e} out=${out_e}"
fi
if [[ ! -e "${test_root}/env-capture-e" ]]; then
  ok "never launched the backend"
else
  bad "the backend was launched despite the unusable manifest handoff"
fi

echo
echo "== summary: ${pass} passed, ${fail} failed =="
[[ "${fail}" -eq 0 ]]
