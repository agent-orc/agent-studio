#!/usr/bin/env bash
# Canonical updater for the side-by-side Agent Studio Stable checkout.
#
# The script can run from this repository or be copied unchanged to the
# devspace root. It keeps Stable on a fast-forward-only main line, reinstalls
# frontend dependencies when their inputs change, and only reports a healthy
# start after a real browser has loaded the frontend without a page error.
#
# Environment overrides:
#   ATP_DEVSPACE_DIR             parent of the dev and stable checkouts
#   ATP_STABLE_CHECKOUT          Stable checkout to update
#   ATP_STABLE_REMOTE            Git remote to fetch (default: origin)
#   ATP_STABLE_BRANCH            remote branch to deploy (default: main)
#   ATP_STOP_SCRIPT              external Stable stop wrapper
#   ATP_START_SCRIPT             external Stable start wrapper
#   ATP_STABLE_FRONTEND_URL      page loaded by the boot probe
#   ATP_BOOT_PROBE_SCRIPT        browser probe implementation
#   ATP_BOOT_PROBE_TIMEOUT_MS    total browser probe deadline
#   ATP_BOOT_PROBE_SETTLE_MS     time to collect deferred page errors
#   ATP_TASK_SERVER_START_SCRIPT host-owned Scheduled Task start wrapper
#   ATP_TASK_SERVER_DEPLOY_SCRIPT host-owned package/install wrapper
#   ATP_TASK_SERVER_URL          standalone Task Server origin
#   ATP_STABLE_BACKEND_URL       Stable API origin used for proxy verification
#   ATP_TASK_SERVER_PROBE_SCRIPT config, readiness, and proxy probe

set -Eeuo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [ -d "$script_dir/../frontend" ] && [ -d "$script_dir/../.git" ]; then
  source_checkout=$(cd "$script_dir/.." && pwd)
  default_devspace=$(cd "$source_checkout/.." && pwd)
else
  default_devspace=$script_dir
fi

devspace_dir=${ATP_DEVSPACE_DIR:-$default_devspace}
stable_checkout=${ATP_STABLE_CHECKOUT:-$devspace_dir/agent-taskboard-stable}
stable_remote=${ATP_STABLE_REMOTE:-origin}
stable_branch=${ATP_STABLE_BRANCH:-main}
stop_script=${ATP_STOP_SCRIPT:-$devspace_dir/stop-stable.sh}
start_script=${ATP_START_SCRIPT:-$devspace_dir/start-stable.sh}
task_server_start_script=${ATP_TASK_SERVER_START_SCRIPT:-$devspace_dir/start-task-server.sh}
task_server_deploy_script=${ATP_TASK_SERVER_DEPLOY_SCRIPT:-$devspace_dir/deploy-task-server.sh}
frontend_url=${ATP_STABLE_FRONTEND_URL:-http://127.0.0.1:4011}
backend_url=${ATP_STABLE_BACKEND_URL:-http://127.0.0.1:5031}
task_server_url=${ATP_TASK_SERVER_URL:-http://127.0.0.1:5071}
# Cold compile / cold npm install after many changed commits can take several
# minutes; 10 minutes gives that headroom instead of failing fast on a boot
# that is merely slow rather than actually broken.
probe_timeout_ms=${ATP_BOOT_PROBE_TIMEOUT_MS:-600000}
probe_settle_ms=${ATP_BOOT_PROBE_SETTLE_MS:-2000}

log() {
  printf '[update-stable] %s\n' "$*"
}

fail() {
  printf '[update-stable] ERROR: %s\n' "$*" >&2
  exit 1
}

[ -e "$stable_checkout/.git" ] || fail "Stable checkout not found: $stable_checkout"
stable_checkout=$(cd "$stable_checkout" && pwd)

[ -x "$stop_script" ] || fail "Stable stop script is not executable: $stop_script"
[ -x "$start_script" ] || fail "Stable start script is not executable: $start_script"
[ -x "$task_server_start_script" ] || fail "Task Server start script is not executable: $task_server_start_script"
[ -x "$task_server_deploy_script" ] || fail "Task Server deploy script is not executable: $task_server_deploy_script"

probe_script=${ATP_BOOT_PROBE_SCRIPT:-$stable_checkout/scripts/stable-frontend-boot-probe.mjs}
task_server_probe_script=${ATP_TASK_SERVER_PROBE_SCRIPT:-$script_dir/task-server-cutover-probe.mjs}
task_server_config=${ATP_TASK_SERVER_CONFIG:-$stable_checkout/backend/appsettings.Local.json}
[ -f "$probe_script" ] || fail "Frontend boot probe not found: $probe_script"
[ -f "$task_server_probe_script" ] || fail "Task Server cutover probe not found: $task_server_probe_script"
[ -f "$task_server_config" ] || fail "Stable Task Server proxy configuration not found: $task_server_config"

if [ -n "$(git -C "$stable_checkout" status --porcelain)" ]; then
  fail "Stable checkout has local changes; refusing to update."
fi

head_before=$(git -C "$stable_checkout" rev-parse HEAD)
log "Fetching $stable_remote/$stable_branch"
git -C "$stable_checkout" fetch --quiet "$stable_remote" "$stable_branch"
target=$(git -C "$stable_checkout" rev-parse "$stable_remote/$stable_branch")

node "$task_server_probe_script" \
  --config "$task_server_config" \
  --task-server-url "$task_server_url" \
  --config-only

if [ "$head_before" = "$target" ]; then
  log "Stable already matches $stable_remote/$stable_branch; recovering and verifying the full supervised topology."
  "$task_server_start_script"
  node "$task_server_probe_script" \
    --config "$task_server_config" \
    --task-server-url "$task_server_url" \
    --expected-sha "$target" \
    --direct-only
  DETACH=1 "$start_script"
  node "$probe_script" \
    --frontend-dir "$stable_checkout/frontend" \
    --url "$frontend_url" \
    --timeout-ms "$probe_timeout_ms" \
    --settle-ms "$probe_settle_ms"
  node "$task_server_probe_script" \
    --config "$task_server_config" \
    --task-server-url "$task_server_url" \
    --backend-url "$backend_url" \
    --expected-sha "$target"
  stable_head_branch=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)
  if [ -z "$stable_head_branch" ]; then
    git -C "$stable_checkout" checkout --quiet -B "$stable_branch" "$target"
    log "Attached the verified Stable checkout to $stable_branch"
  elif [ "$stable_head_branch" != "$stable_branch" ]; then
    fail "Stable is attached to unexpected branch '$stable_head_branch'; expected '$stable_branch'."
  fi
  log "Stable and Task Server are healthy at $target"
  exit 0
fi

if ! git -C "$stable_checkout" merge-base --is-ancestor "$head_before" "$target"; then
  fail "Stable cannot fast-forward from $head_before to $target."
fi

install_frontend=0
if ! git -C "$stable_checkout" diff --quiet "$head_before" "$target" -- \
  frontend/package.json \
  frontend/package-lock.json \
  'frontend/scripts/patch-coding-agent-chat-*.mjs'; then
  install_frontend=1
fi

log "Stopping Stable"
"$stop_script"

log "Fast-forwarding Stable to $target"
stable_head_branch=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)
if [ -z "$stable_head_branch" ]; then
  git -C "$stable_checkout" checkout --quiet --detach "$target"
  log "Kept the held Stable checkout detached until cutover verification completes"
elif [ "$stable_head_branch" = "$stable_branch" ]; then
  git -C "$stable_checkout" merge --quiet --ff-only "$target"
else
  fail "Stable is attached to unexpected branch '$stable_head_branch'; expected '$stable_branch'."
fi

if [ "$install_frontend" -eq 1 ]; then
  # A leftover frontend dev server (or one of its esbuild/vite children) can
  # still hold files open under frontend/node_modules right after stop_script
  # returns, and npm install then fails with EPERM trying to unlink them.
  # Renaming the directory and back is a cheap way to surface that sharing
  # violation before npm does, and to name the still-running holder instead
  # of failing with a bare npm error.
  node_modules_dir="$stable_checkout/frontend/node_modules"
  if [ -d "$node_modules_dir" ]; then
    holder=""
    if command -v lsof >/dev/null 2>&1; then
      # lsof exits non-zero when it finds no open files under the path; that
      # is the common (unlocked) case, not a script error.
      holder=$(lsof +D "$node_modules_dir" 2>/dev/null | awk 'NR==2{print "pid="$2" command="$1}') || true
    fi
    if [ -z "$holder" ]; then
      # lsof is unavailable, or found nothing: fall back to a rename probe.
      # On Windows a directory containing an open file handle cannot be
      # renamed, which is exactly the esbuild.exe-still-running case this
      # check exists for; on POSIX this is a best-effort secondary signal.
      probe_dir="${node_modules_dir}.lock-probe"
      rm -rf -- "$probe_dir"
      if ! mv "$node_modules_dir" "$probe_dir" 2>/dev/null || ! mv "$probe_dir" "$node_modules_dir" 2>/dev/null; then
        holder="an unknown process (rename of frontend/node_modules failed)"
      fi
    fi
    if [ -n "$holder" ]; then
      fail "frontend/node_modules is still held open by ${holder}; refusing to run npm install"
    fi
  fi

  log "Installing frontend dependencies"
  npm --prefix "$stable_checkout/frontend" install

  # postinstall patches coding-agent-chat in place. Angular's Vite optimizer
  # does not include those resulting bytes in its cache key, so any prebundle
  # created before the patch is unsafe even when the package version is equal.
  angular_cache="$stable_checkout/frontend/.angular/cache"
  case "$angular_cache" in
    "$stable_checkout"/frontend/.angular/cache)
      rm -rf -- "$angular_cache"
      ;;
    *)
      fail "Refusing to remove unexpected Angular cache path: $angular_cache"
      ;;
  esac
  log "Invalidated the Angular/Vite optimizer cache after npm install"
else
  log "Frontend dependency inputs are unchanged; skipping npm install"
fi

log "Packaging and installing Task Server $target"
"$task_server_deploy_script" "$stable_checkout" "$target"

log "Starting supervised Task Server before Stable API"
"$task_server_start_script"
node "$task_server_probe_script" \
  --config "$task_server_config" \
  --task-server-url "$task_server_url" \
  --expected-sha "$target" \
  --direct-only

log "Starting Stable"
DETACH=1 "$start_script"

log "Loading $frontend_url in a headless browser"
node "$probe_script" \
  --frontend-dir "$stable_checkout/frontend" \
  --url "$frontend_url" \
  --timeout-ms "$probe_timeout_ms" \
  --settle-ms "$probe_settle_ms"

node "$task_server_probe_script" \
  --config "$task_server_config" \
  --task-server-url "$task_server_url" \
  --backend-url "$backend_url" \
  --expected-sha "$target"

stable_head_branch=$(git -C "$stable_checkout" symbolic-ref --quiet --short HEAD || true)
if [ -z "$stable_head_branch" ]; then
  git -C "$stable_checkout" checkout --quiet -B "$stable_branch" "$target"
  log "Attached the verified Stable checkout to $stable_branch"
fi

log "Stable and Task Server started and healthy at $target"
