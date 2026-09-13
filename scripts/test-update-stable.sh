#!/usr/bin/env bash

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
updater=$script_dir/update-stable.sh
probe=$script_dir/stable-frontend-boot-probe.mjs
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t update-stable)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

remote=$test_root/remote.git
source_checkout=$test_root/source
stable_checkout=$test_root/stable
devspace=$test_root/devspace
fake_bin=$test_root/bin
stop_marker=$test_root/stop-ran
start_marker=$test_root/start-ran
task_server_start_marker=$test_root/task-server-start-ran
task_server_deploy_marker=$test_root/task-server-deploy-ran
task_server_probe=$test_root/task-server-probe.mjs

git init --bare --quiet "$remote"
git init --quiet "$source_checkout"
git -C "$source_checkout" config user.name 'Stable Update Test'
git -C "$source_checkout" config user.email 'stable-update@example.invalid'
mkdir -p "$source_checkout/frontend/scripts" "$source_checkout/backend"
printf '%s\n' '/frontend/node_modules/' '/frontend/.angular/' > "$source_checkout/.gitignore"
printf '%s\n' 'appsettings.Local.json' > "$source_checkout/backend/.gitignore"
printf '%s\n' '{"name":"fixture","private":true}' > "$source_checkout/frontend/package.json"
printf '%s\n' '{"lockfileVersion":3}' > "$source_checkout/frontend/package-lock.json"
printf '%s\n' '// initial compatibility patch' > "$source_checkout/frontend/scripts/patch-coding-agent-chat-technical-blocks.mjs"
printf '%s\n' 'base release' > "$source_checkout/release.txt"
git -C "$source_checkout" add .
git -C "$source_checkout" commit --quiet -m 'base release'
git -C "$source_checkout" branch -M main
git -C "$source_checkout" remote add origin "$remote"
git -C "$source_checkout" push --quiet -u origin main
git --git-dir="$remote" symbolic-ref HEAD refs/heads/main
git clone --quiet "$remote" "$stable_checkout"

mkdir -p \
  "$devspace" \
  "$fake_bin" \
  "$stable_checkout/frontend/node_modules/playwright-core" \
  "$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022" \
  "$stable_checkout/frontend/.angular/cache"
printf '%s\n' '{"TaskServer":{"BaseUrl":"http://127.0.0.1:5071"}}' > "$stable_checkout/backend/appsettings.Local.json"
printf '%s\n' stale-prebundle > "$stable_checkout/frontend/.angular/cache/deps.js"
printf '%s\n' unpatched > "$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs"

cat > "$stable_checkout/frontend/node_modules/playwright-core/package.json" <<'EOF'
{"name":"playwright-core","version":"0.0.0-test","main":"index.cjs"}
EOF
cat > "$stable_checkout/frontend/node_modules/playwright-core/index.cjs" <<'EOF'
const { EventEmitter } = require('node:events');
const { existsSync } = require('node:fs');

module.exports = {
  chromium: {
    async launch() {
      return {
        async newPage() {
          const page = new EventEmitter();
          page.goto = async () => {
            if (existsSync(process.env.ATP_TEST_STALE_CACHE)) {
              page.emit('pageerror', new Error(
                "coding-agent-chat_markdown.js does not provide an export named 'protectTechnicalMarkdown'",
              ));
            }
            if (process.env.ATP_TEST_PAGEERROR) {
              page.emit('pageerror', new Error(process.env.ATP_TEST_PAGEERROR));
            }
            return { ok: () => true, status: () => 200 };
          };
          return page;
        },
        async close() {},
      };
    },
  },
};
EOF

cat > "$devspace/stop-stable.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
: > "$ATP_TEST_STOP_MARKER"
EOF
cat > "$devspace/start-stable.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
: > "$ATP_TEST_START_MARKER"
EOF
cat > "$devspace/start-task-server.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
: > "$ATP_TEST_TASK_SERVER_START_MARKER"
EOF
cat > "$devspace/deploy-task-server.sh" <<'EOF'
#!/usr/bin/env sh
set -eu
test -d "$1"
test -n "$2"
: > "$ATP_TEST_TASK_SERVER_DEPLOY_MARKER"
EOF
cat > "$task_server_probe" <<'EOF'
import { appendFileSync } from 'node:fs';
appendFileSync(process.env.ATP_TEST_TASK_SERVER_PROBE_MARKER, `${process.argv.slice(2).join(' ')}\n`);
EOF
cat > "$fake_bin/npm" <<'EOF'
#!/usr/bin/env sh
set -eu
printf '%s\n' patched > "$ATP_TEST_PACKAGE_FILE"
EOF
chmod +x "$devspace/stop-stable.sh" "$devspace/start-stable.sh" \
  "$devspace/start-task-server.sh" "$devspace/deploy-task-server.sh" "$fake_bin/npm"

run_update() {
  env \
    ATP_DEVSPACE_DIR="$devspace" \
    ATP_STABLE_CHECKOUT="$stable_checkout" \
    ATP_STOP_SCRIPT="$devspace/stop-stable.sh" \
    ATP_START_SCRIPT="$devspace/start-stable.sh" \
    ATP_TASK_SERVER_START_SCRIPT="$devspace/start-task-server.sh" \
    ATP_TASK_SERVER_DEPLOY_SCRIPT="$devspace/deploy-task-server.sh" \
    ATP_TASK_SERVER_PROBE_SCRIPT="$task_server_probe" \
    ATP_BOOT_PROBE_SCRIPT="$probe" \
    ATP_BOOT_PROBE_SETTLE_MS=0 \
    ATP_TEST_STOP_MARKER="$stop_marker" \
    ATP_TEST_START_MARKER="$start_marker" \
    ATP_TEST_TASK_SERVER_START_MARKER="$task_server_start_marker" \
    ATP_TEST_TASK_SERVER_DEPLOY_MARKER="$task_server_deploy_marker" \
    ATP_TEST_TASK_SERVER_PROBE_MARKER="$test_root/task-server-probes.log" \
    ATP_TEST_PACKAGE_FILE="$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs" \
    ATP_TEST_STALE_CACHE="$stable_checkout/frontend/.angular/cache/deps.js" \
    PATH="$fake_bin:$PATH" \
    "$updater" 2>&1
}

# A changed postinstall patch causes npm install. The updater must invalidate
# the old optimizer prebundle before the browser probe observes the new export.
printf '%s\n' '// patched compatibility bridge' > "$source_checkout/frontend/scripts/patch-coding-agent-chat-technical-blocks.mjs"
git -C "$source_checkout" commit --quiet -am 'update compatibility patch'
git -C "$source_checkout" push --quiet origin main

output=$(run_update)
test -f "$stop_marker"
test -f "$start_marker"
test -f "$task_server_start_marker"
test -f "$task_server_deploy_marker"
test ! -e "$stable_checkout/frontend/.angular/cache"
grep -q '^patched$' "$stable_checkout/frontend/node_modules/coding-agent-chat/fesm2022/coding-agent-chat-markdown.mjs"
printf '%s' "$output" | grep -q 'Invalidated the Angular/Vite optimizer cache'
printf '%s' "$output" | grep -q 'Boot completed without page errors'
printf '%s' "$output" | grep -q 'Stable and Task Server started and healthy'
grep -q -- '--config-only' "$test_root/task-server-probes.log"
grep -q -- '--direct-only' "$test_root/task-server-probes.log"
grep -q -- '--expected-sha' "$test_root/task-server-probes.log"
test "$(git -C "$stable_checkout" symbolic-ref --short HEAD)" = main

# A separate release injects an application boot crash. An open port and a
# successful document response must not allow the updater to claim health.
printf '%s\n' 'release with injected crash' > "$source_checkout/release.txt"
git -C "$source_checkout" commit --quiet -am 'release with boot crash'
git -C "$source_checkout" push --quiet origin main
git -C "$stable_checkout" checkout --quiet --detach

set +e
crash_output=$(ATP_TEST_PAGEERROR='injected boot crash' run_update)
crash_rc=$?
set -e

test "$crash_rc" -ne 0
printf '%s' "$crash_output" | grep -q 'PAGEERROR'
printf '%s' "$crash_output" | grep -q 'injected boot crash'
if printf '%s' "$crash_output" | grep -q 'Stable started and healthy'; then
  printf '%s\n' 'updater reported health after an injected page error' >&2
  exit 1
fi
if git -C "$stable_checkout" symbolic-ref --quiet --short HEAD >/dev/null; then
  printf '%s\n' 'failed cutover unexpectedly unpinned Stable before verification' >&2
  exit 1
fi

# The failed cutover remains held at the target SHA. The no-op verification
# path must supervise Task Server and attach main only after all probes pass.
rm -f -- "$task_server_start_marker"
rm -f -- "$start_marker"
noop_output=$(run_update)
test -f "$task_server_start_marker"
test -f "$start_marker"
printf '%s' "$noop_output" | grep -q 'recovering and verifying the full supervised topology'
printf '%s' "$noop_output" | grep -q 'Boot completed without page errors'
test "$(git -C "$stable_checkout" symbolic-ref --short HEAD)" = main

# A process still holding a file open under frontend/node_modules (the
# leftover frontend dev server / esbuild child from the real incident) must
# fail the run before npm install runs, and must name the holder instead of
# surfacing a bare npm EPERM error.
printf '%s\n' '// another patch revision' > "$source_checkout/frontend/scripts/patch-coding-agent-chat-technical-blocks.mjs"
git -C "$source_checkout" commit --quiet -am 'another compatibility patch revision'
git -C "$source_checkout" push --quiet origin main

holder_file="$stable_checkout/frontend/node_modules/playwright-core/index.cjs"
( exec 9<"$holder_file"; sleep 30 ) &
holder_pid=$!
# Give the background holder a moment to actually open the fd before the
# updater runs its lock check.
sleep 0.2

set +e
locked_output=$(run_update)
locked_rc=$?
set -e
kill "$holder_pid" 2>/dev/null || true
wait "$holder_pid" 2>/dev/null || true

test "$locked_rc" -ne 0
printf '%s' "$locked_output" | grep -q 'frontend/node_modules is still held open by'
if printf '%s' "$locked_output" | grep -qi 'npm ERR'; then
  printf '%s\n' 'lock check did not fail before npm install ran' >&2
  exit 1
fi

# This script exercises scripts/update-stable.sh only: git fast-forward,
# frontend dependency reinstall, and the browser boot probe. It has no
# manifest install step, no release-preflight identity check, and no update
# history file; those belong to the separate update-service (.NET) pipeline
# invoked by ADR-0031 (update-service/UpdateOrchestrator.cs). The invariants
# that a failure before restart leaves the checkout and build-manifest.json
# untouched, that a slow-but-alive backend start within the extended restart
# health-wait budget is treated as success, and that update history gets
# exactly one record per run carrying both the pre-run and verified-healthy
# identity, are covered by backend.Tests/UpdateServiceIntegrationTests.cs
# (FailureBeforeRestart_LeavesCheckoutAndManifestUnchanged,
# SlowBackendStart_WithinRestartHealthWaitBudget_IsTreatedAsSuccess,
# History_GetsExactlyOneRecordPerRun_WithBeforeAndAfterIdentity). That suite
# is gated to Windows and skips on Linux/CI without git+bash.

printf '%s\n' 'update-stable tests passed'
