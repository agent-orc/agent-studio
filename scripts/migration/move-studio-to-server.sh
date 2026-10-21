#!/bin/sh
# Move Studio from the workstation (Stable) to the networked monolith stack on
# the server (application lifecycle D15, option B). One repeatable procedure:
#
#   --dry-run   (default) inventory, path mapping, credential checklist, server
#               preflight. Changes nothing.
#   --transfer  only the transfer pass into server staging (Stable keeps
#               running); --rehearse and --apply run it too.
#   --rehearse  transfer into server staging while Stable keeps running, then
#               start a separate Compose project on a loopback port against a
#               copy of staging, run the smoke checks, and tear it down.
#   --apply     stop Stable, send the final delta, switch the server stack to
#               the relocated copy, check it, and print the rollback command.
#               It runs once: the server keeps a moved marker and the
#               workstation a guard that Stable's api.sh refuses to start past.
#
# Run it on the workstation from Git Bash (or any POSIX shell with Git, GNU
# tar, gzip, curl, ssh, and Node). The workstation workspace is only read.
# The planning logic lives in move-studio-to-server.mjs and is unit tested.
# Guide: docs/operations/setup/move-studio-to-server.md
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
self="$script_dir/$(basename -- "$0")"

log() { printf '[move-studio] %s\n' "$*"; }
fail() { printf '[move-studio] ERROR: %s\n' "$*" >&2; exit 1; }
# Exit 87 (distinct from tool exit codes): the server refused before it touched live data.
refuse() { printf '[move-studio] ERROR: %s\n' "$*" >&2; exit 87; }

# Single-quote one argument for the remote shell.
sq() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

safe_path() {
  case "$1" in
    /*) ;;
    *) fail "$2 must be an absolute path: $1" ;;
  esac
  case "$1" in
    *[!A-Za-z0-9._/-]*|*/../*|*/..) fail "$2 contains unsupported characters: $1" ;;
  esac
}

# ---------------------------------------------------------------------------
# Server side. The workstation copies this file to <server-root>/move-bin and
# calls it as: sh move-studio-to-server.sh --server-side <command> <args>.
# ---------------------------------------------------------------------------

env_value() { sed -n "s/^$1=//p" "$2" | tail -n 1; }

json_field() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const v=JSON.parse(s)[process.argv[1]];process.stdout.write(v===undefined||v===null?"":String(v));});' "$1"
}

# Field names on the command line, one value per stdin line: passwords and
# one-time codes never show up in a process list.
json_payload() {
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const v=s.split("\n");const o={};process.argv.slice(1).forEach((k,i)=>{o[k]=v[i]??"";});process.stdout.write(JSON.stringify(o));});' "$@"
}

random_hex() { od -An -tx1 -N"$1" /dev/urandom | tr -d ' \n'; }

as_service_owner() {
  # The API runs as UID 10001; ownership changes go through a root container
  # so the operator account needs only Docker access (same as setup.sh).
  owner=$1; shift
  base=$1; shift
  docker run --rm --user 0 -v "$base:/site" --entrypoint chown caddy:2-alpine -R "$owner" "$@" >/dev/null
}

server_preflight() {
  root=$1
  missing=
  for tool in docker git node curl tar gzip od; do
    command -v "$tool" >/dev/null 2>&1 || missing="$missing $tool"
  done
  [ -z "$missing" ] || fail "server is missing:$missing"
  docker compose version >/dev/null || fail 'docker compose is not available on the server'
  [ -f "$root/stack/compose.yaml" ] || fail "no stack at $root/stack; copy deploy/compose/monolith-networked there"
  [ -f "$root/stack/.env" ] || fail "no $root/stack/.env; run setup.sh <version> <domain> $root/data in $root/stack first"
  data_dir=$(env_value STUDIO_DATA_DIR "$root/stack/.env")
  [ -n "$data_dir" ] || fail "STUDIO_DATA_DIR is not set in $root/stack/.env"
  [ -w "$data_dir" ] || fail "$data_dir is not writable by $(id -un)"
  printf 'server: %s ok, data %s, version %s, domain %s\n' "$(hostname)" "$data_dir" \
    "$(env_value STUDIO_VERSION "$root/stack/.env")" "$(env_value STUDIO_DOMAIN "$root/stack/.env")"
  if [ -d "$root/move-staging/workspace/.git" ]; then
    printf 'staging: %s present\n' "$(du -sh "$root/move-staging" | cut -f1)"
  else
    printf 'staging: empty\n'
  fi
  if [ -f "$root/moved-to-server" ]; then
    printf 'moved: %s (a second --apply needs --force)\n' "$(tr '\n' ' ' <"$root/moved-to-server")"
  fi
  df -h "$root" | tail -n 1
}

server_moved_state() { if [ -f "$1/moved-to-server" ]; then tr '\n' ' ' <"$1/moved-to-server"; fi; }

# Staging and the data directory hold .security/access-state.json (accounts and
# sessions). Nothing below them may be readable by other accounts.
private_dir() { mkdir -p "$1" && chmod 700 "$1"; }

protect_access_state() {
  if [ -d "$1/.security" ]; then
    chmod 700 "$1/.security"
    find "$1/.security" -type f -exec chmod 600 {} +
  fi
}

server_receive_archive() {
  mkdir -p "$1"
  gzip -dc | tar -C "$1" --no-same-permissions -xf -
  protect_access_state "$1"
}

server_clone_workspace() {
  dir=$1; url=$2
  private_dir "$(dirname "$dir")"
  if [ -d "$dir/.git" ]; then
    # Also replaces a URL with credentials an earlier version stored.
    git -C "$dir" remote set-url origin "$url"
    git -C "$dir" fetch -q --prune origin
    echo existing
  else
    git clone -q "$url" "$dir" || fail "cannot clone the workspace remote on the server; give the server its own Git credential first"
    echo cloned
  fi
}

server_sync_workspace() {
  dir=$1; head=$2
  git -C "$dir" cat-file -e "$head^{commit}" || fail "source HEAD $head did not arrive on the server"
  git -C "$dir" reset -q --hard "$head"
}

server_clone_checkout() {
  dir=$1; url=$2
  mkdir -p "$(dirname "$dir")"
  if [ -d "$dir/.git" ]; then
    git -C "$dir" remote set-url origin "$url"
    git -C "$dir" fetch -q --prune origin
  else
    git clone -q "$url" "$dir" || fail "cannot clone $url on the server"
  fi
}

# relocate_paths <root> <workspace> <projects> [--apply]: without --apply a
# report only; exit 2 means unresolved Windows paths.
relocate_paths() {
  root=$1; workspace=$2; projects=$3; apply_flag=${4:-}
  set -- --workspace "$workspace" --mapping "$root/move-bin/path-map.json"
  if [ -d "$projects" ]; then
    for checkout in "$projects"/*; do
      [ -d "$checkout/.git" ] && set -- "$@" --checkout "$checkout"
    done
  fi
  [ -z "$apply_flag" ] || set -- "$@" --apply
  node "$root/move-bin/relocate-workspace-paths.mjs" "$@"
}

server_relocate() {
  root=$1
  # Dry run first: unresolved Windows paths stop here.
  relocate_paths "$@" >"$root/move-bin/relocate-report.json" \
    || fail "path relocation has unresolved paths; see $root/move-bin/relocate-report.json and add them with --extra-mapping"
  relocate_paths "$@" --apply >/dev/null
}

server_disable_push() {
  repo=$1
  for remote in $(git -C "$repo" remote); do
    git -C "$repo" remote set-url --push "$remote" no-push://move-rehearsal
  done
}

server_smoke() {
  host=$1; port=$2; work=$3
  base="https://$host:$port"
  jar="$work/cookies"
  origin_header="Origin: $base"
  c() { curl --silent --show-error --insecure --max-time 30 --resolve "$host:$port:127.0.0.1" "$@"; }

  attempt=0
  until c --fail "$base/api/auth/status" >"$work/status.json" 2>/dev/null; do
    attempt=$((attempt + 1))
    [ "$attempt" -lt 90 ] || fail 'health: the rehearsal stack did not answer /api/auth/status'
    sleep 2
  done
  echo 'check: health ok'

  if [ "$(json_field bootstrapRequired <"$work/status.json")" = true ]; then
    username=move.rehearsal.owner
    password="Rehearsal-$(random_hex 16)"
    code=$(env_value STUDIO_BOOTSTRAP_CODE "$work/.env")
    printf '%s\n' "$username" "$password" 'Move rehearsal owner' "$code" \
      | json_payload username password displayName bootstrapCode >"$work/auth.json"
    endpoint=bootstrap
  else
    username=$login_username
    password=$login_password
    [ -n "$username" ] && [ -n "$password" ] || fail 'sign-in: the copy has owners; set MOVE_REHEARSAL_OWNER_USERNAME and MOVE_REHEARSAL_OWNER_PASSWORD'
    printf '%s\n' "$username" "$password" | json_payload username password >"$work/auth.json"
    endpoint=login
  fi
  c --fail -c "$jar" -H 'Content-Type: application/json' -H "$origin_header" --data-binary "@$work/auth.json" \
    "$base/api/auth/$endpoint" >"$work/session.json" || fail "sign-in: $endpoint failed"
  rm -f "$work/auth.json"
  csrf=$(json_field csrfToken <"$work/session.json")
  [ -n "$csrf" ] || fail 'sign-in: no CSRF token in the session response'
  echo "check: sign-in ok ($endpoint as $username)"

  c --fail -b "$jar" "$base/api/projects" >"$work/projects.json" || fail 'board: GET /api/projects failed'
  board_project=$(node -e 'const p=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));const l=Array.isArray(p)?p:(p.projects??p.items??[]);process.stdout.write(l.length?String(l[0].id??l[0].Id):"");' "$work/projects.json")
  if [ -n "$board_project" ]; then
    c --fail -b "$jar" "$base/api/tasks?project=$board_project" >/dev/null || fail "board: GET /api/tasks?project=$board_project failed"
    echo "check: board read ok ($board_project)"
  else
    echo 'check: board read ok (no projects registered)'
  fi

  printf '%s' '{"name":"move-rehearsal-runner","scopes":["runner.claim"]}' >"$work/enrollment-request.json"
  c --fail -b "$jar" -H 'Content-Type: application/json' -H "$origin_header" -H "X-CSRF-Token: $csrf" \
    --data-binary "@$work/enrollment-request.json" "$base/api/auth/runner-enrollments" >"$work/enrollment.json" \
    || fail 'runner: enrollment failed'
  json_field enrollmentCode <"$work/enrollment.json" | json_payload code >"$work/enroll.json"
  c --fail -H 'Content-Type: application/json' --data-binary "@$work/enroll.json" "$base/api/auth/runner-enroll" >"$work/runner.json" \
    || fail 'runner: enrol with the one-time code failed'
  runner_id=$(json_field runnerId <"$work/runner.json")
  runner_name=$(json_field runnerName <"$work/runner.json")
  printf 'header = "Authorization: Bearer %s"\n' "$(json_field secret <"$work/runner.json")" >"$work/runner.curl"
  rm -f "$work/runner.json" "$work/enroll.json"
  # Zero free slots: the claim authenticates and records runner status but
  # leases no card, so the rehearsal never starts work on the copy.
  node -e 'process.stdout.write(JSON.stringify({runnerId:process.argv[1],runnerName:process.argv[2],hostname:"move-rehearsal",pid:1,backendName:"move-rehearsal",availableSlots:0}))' \
    "$runner_id" "$runner_name" >"$work/claim-request.json"
  status=$(c --config "$work/runner.curl" -o "$work/claim.json" -w '%{http_code}' -H 'Content-Type: application/json' \
    --data-binary "@$work/claim-request.json" "$base/api/runner/claim")
  rm -f "$work/runner.curl"
  [ "$status" = 200 ] || fail "runner: claim with the test credential returned HTTP $status"
  echo "check: runner claim ok ($runner_name, status $(json_field status <"$work/claim.json"))"
}

server_rehearse() {
  root=$1; port=$2; subnet=$3; sign_in=$4
  login_username=
  login_password=
  # Read the owner login first, before any other command can consume stdin.
  if [ "$sign_in" = login ]; then
    IFS= read -r login_username || true
    IFS= read -r login_password || true
  fi
  stack="$root/stack"
  rehearsal="$root/move-rehearsal"
  rehearsal_project=studio-move-rehearsal
  host=move-rehearsal.studio.test
  [ -d "$root/move-staging/workspace/.git" ] || fail 'staging is empty; the transfer pass did not run'
  compose() { docker compose -p "$rehearsal_project" -f "$stack/compose.yaml" -f "$rehearsal/compose.override.yaml" --env-file "$rehearsal/.env" "$@"; }
  teardown() {
    result=$?
    if [ -f "$rehearsal/.env" ]; then
      [ "$result" -eq 0 ] || compose logs --tail 80 >"$root/move-bin/rehearsal-failure.log" 2>&1 || true
      compose down --remove-orphans >/dev/null 2>&1 || true
    fi
    as_service_owner "$(id -u):$(id -g)" "$rehearsal" /site >/dev/null 2>&1 || true
    rm -rf "$rehearsal"
    if [ "$result" -eq 0 ]; then echo 'rehearsal: torn down'; else echo "rehearsal: failed and torn down; logs in $root/move-bin/rehearsal-failure.log" >&2; fi
    exit "$result"
  }
  if [ -e "$rehearsal" ]; then
    as_service_owner "$(id -u):$(id -g)" "$rehearsal" /site >/dev/null 2>&1 || true
    rm -rf "$rehearsal"
  fi
  trap teardown EXIT
  trap 'exit 130' HUP INT TERM
  mkdir -p "$rehearsal/data/home" "$rehearsal/data/caddy-data" "$rehearsal/data/caddy-config" "$rehearsal/data/projects"
  cp -a "$root/move-staging/workspace" "$rehearsal/data/workspace"
  if [ -d "$root/move-staging/projects" ]; then cp -a "$root/move-staging/projects/." "$rehearsal/data/projects/"; fi
  if [ -d "$root/move-staging/storage" ]; then
    mkdir -p "$rehearsal/data/projects/.task-storage"
    cp -a "$root/move-staging/storage/." "$rehearsal/data/projects/.task-storage/"
  fi
  server_relocate "$root" "$rehearsal/data/workspace" "$rehearsal/data/projects"
  echo 'check: path relocation ok (0 unresolved)'
  server_disable_push "$rehearsal/data/workspace"
  for checkout in "$rehearsal/data/projects"/*; do
    [ -d "$checkout/.git" ] && server_disable_push "$checkout"
  done
  access="$rehearsal/data/workspace/.security/access-state.json"
  if [ -f "$access" ] && [ "$sign_in" = bootstrap ]; then
    node -e 'JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"))' "$access" || fail 'access state: .security/access-state.json is not valid JSON'
    mv "$access" "$rehearsal/access-state.checked.json"
    echo 'check: access state parses; the copy signs in with a disposable test owner'
  fi
  grep -v -e '^STUDIO_DATA_DIR=' -e '^STUDIO_DOMAIN=' -e '^STUDIO_CADDYFILE=' -e '^STUDIO_BOOTSTRAP_CODE=' "$stack/.env" >"$rehearsal/.env"
  {
    echo "STUDIO_DATA_DIR=$rehearsal/data"
    echo "STUDIO_DOMAIN=$host"
    echo 'STUDIO_CADDYFILE=./Caddyfile.private'
    echo "STUDIO_BOOTSTRAP_CODE=$(random_hex 24)"
  } >>"$rehearsal/.env"
  chmod 600 "$rehearsal/.env"
  cat >"$rehearsal/compose.override.yaml" <<EOF
services:
  api:
    environment:
      Security__TrustedProxyAddresses: $subnet.3
      WorkspaceEvidence__Push: 'false'
    networks:
      website:
        ipv4_address: $subnet.4
  frontend:
    networks:
      website:
        ipv4_address: $subnet.3
  edge:
    ports: !override
      - '127.0.0.1:$port:443'
    networks:
      website:
        ipv4_address: $subnet.2
networks:
  website:
    ipam:
      config: !override
        - subnet: $subnet.0/24
EOF
  as_service_owner 10001:10001 "$rehearsal/data" /site/workspace /site/projects /site/home
  compose up -d --quiet-pull >/dev/null
  server_smoke "$host" "$port" "$rehearsal"
}

server_apply() {
  root=$1; stamp=$2; force=${3:-}
  stack="$root/stack"
  marker="$root/moved-to-server"
  data_dir=$(env_value STUDIO_DATA_DIR "$stack/.env")
  domain=$(env_value STUDIO_DOMAIN "$stack/.env")
  live() { docker compose --project-directory "$stack" -f "$stack/compose.yaml" --env-file "$stack/.env" "$@"; }
  if [ -f "$marker" ] && [ "$force" != force ]; then
    refuse "Studio already moved here ($(tr '\n' ' ' <"$marker")); another apply would replace the live server data with the workstation snapshot"
  fi
  [ -d "$root/move-staging/workspace/.git" ] || refuse 'staging is empty; the final pass did not run'
  relocate_paths "$root" "$root/move-staging/workspace" "$root/move-staging/projects" >"$root/move-bin/relocate-report.json" \
    || refuse "staging has unresolved Windows paths (see $root/move-bin/relocate-report.json); the live data was not touched"
  echo 'check: staging relocates with 0 unresolved paths'
  for name in workspace projects; do
    [ ! -e "$data_dir/$name.pre-move-$stamp" ] || refuse "$data_dir/$name.pre-move-$stamp already exists"
  done
  live stop >/dev/null || refuse 'docker compose stop failed; the live data was not touched'
  running=$(live ps --status running --status restarting --status paused --quiet) \
    || refuse 'cannot list the server stack after stop; the live data was not touched'
  [ -z "$running" ] || refuse "the server stack still runs after stop ($(echo $running)); the live data was not touched"
  echo 'check: server stack stopped'
  # From the first move on, a failure must not leave a stack serving half-switched data.
  switched=0
  stop_after_failure() {
    result=$?
    trap - EXIT
    if [ "$result" -ne 0 ] && [ "$switched" -eq 1 ]; then
      if live stop >/dev/null 2>&1; then
        echo 'switch failed: server stack stopped' >&2
      else
        echo 'switch failed and docker compose stop failed too; stop the server stack by hand' >&2
      fi
    fi
    exit "$result"
  }
  trap stop_after_failure EXIT
  trap 'exit 130' HUP INT TERM PIPE
  switched=1
  for name in workspace projects; do
    if [ -e "$data_dir/$name" ]; then mv "$data_dir/$name" "$data_dir/$name.pre-move-$stamp"; fi
  done
  cp -a "$root/move-staging/workspace" "$data_dir/workspace"
  mkdir -p "$data_dir/projects" "$data_dir/home"
  if [ -d "$root/move-staging/projects" ]; then cp -a "$root/move-staging/projects/." "$data_dir/projects/"; fi
  if [ -d "$root/move-staging/storage" ]; then
    mkdir -p "$data_dir/projects/.task-storage"
    cp -a "$root/move-staging/storage/." "$data_dir/projects/.task-storage/"
  fi
  server_relocate "$root" "$data_dir/workspace" "$data_dir/projects"
  echo 'check: path relocation ok (0 unresolved)'
  protect_access_state "$data_dir/workspace"
  as_service_owner 10001:10001 "$data_dir" /site/workspace /site/projects /site/home
  live up -d --quiet-pull >/dev/null
  attempt=0
  until curl --silent --fail --insecure --max-time 10 --resolve "$domain:443:127.0.0.1" "https://$domain/api/auth/status" >"$root/move-bin/status.json" 2>/dev/null; do
    attempt=$((attempt + 1))
    [ "$attempt" -lt 90 ] || fail "the server stack did not answer https://$domain/api/auth/status"
    sleep 2
  done
  if [ -f "$data_dir/workspace/.security/access-state.json" ]; then
    [ "$(json_field bootstrapRequired <"$root/move-bin/status.json")" = false ] \
      || fail 'the server stack asks for owner bootstrap although the access state was moved'
    echo "check: https://$domain answers locally with the moved accounts"
  else
    echo "check: https://$domain answers locally; the workstation had no accounts, so the first visit needs STUDIO_BOOTSTRAP_CODE from $stack/.env"
  fi
  printf 'moved_at=%s\ndata_dir=%s\n' "$stamp" "$data_dir" >"$marker"
  switched=0
  echo "moved: marker $marker written"
  # Staging is a second full copy of the access state; the data directory now has it.
  rm -rf "$root/move-staging"
  echo 'staging: removed'
  live ps --format '{{.Service}} {{.State}}'
}

if [ "${1:-}" = --server-side ]; then
  shift
  # Staging, the rehearsal copy, and the data directory hold access state,
  # owner logins, cookies, and runner secrets: nothing here is for other accounts.
  umask 077
  command=$1; shift
  case "$command" in
    preflight) server_preflight "$@" ;;
    moved-state) server_moved_state "$@" ;;
    clone-workspace) server_clone_workspace "$@" ;;
    sync-workspace) server_sync_workspace "$@" ;;
    receive-archive) server_receive_archive "$@" ;;
    ensure-dir) if [ -d "$1" ]; then echo existing; else private_dir "$1"; echo created; fi ;;
    remove-paths) cd "$1" && xargs -0 rm -f -- ;;
    clone-checkout) server_clone_checkout "$@" ;;
    rehearse) server_rehearse "$@" ;;
    apply) server_apply "$@" ;;
    *) fail "unknown server-side command: $command" ;;
  esac
  exit 0
fi

# ---------------------------------------------------------------------------
# Workstation side.
# ---------------------------------------------------------------------------

usage() {
  cat <<'EOF'
Usage: move-studio-to-server.sh [--dry-run | --transfer | --rehearse | --apply] --workspace DIR [options]
  --workspace DIR        workstation workspace (required), e.g. C:\Projects\agent-taskboard-workspace
  --devspace DIR         folder holding agent-taskboard-stable, start-stable.sh, and stop-stable.sh
                         (required for --apply), e.g. C:\Projects\agent-taskboard-devspace
  --server HOST          SSH host alias of the server (default: agent-runner-01)
  --server-root DIR      server directory holding stack/, data/, move-staging/ (default: /srv/agent-studio)
  --state-dir DIR        local plan and delta state (default: <workspace>.move-state)
  --extra-mapping FILE   JSON of additional Windows prefix -> Linux path entries
  --rehearsal-port N     loopback port for the rehearsal stack (default: 18443)
  --rehearsal-subnet P   first three octets of the rehearsal network (default: 172.30.84)
  --yes                  skip the interactive confirmation of --apply
  --force                apply again although Studio already moved (discards the server's state)
Environment: MOVE_WORKSPACE, MOVE_DEVSPACE, MOVE_STABLE_HEALTH_URL, STABLE_MAINTENANCE_FILE,
  MOVE_REHEARSAL_OWNER_USERNAME, MOVE_REHEARSAL_OWNER_PASSWORD.
EOF
}

mode=dry-run
workspace=${MOVE_WORKSPACE:-}
devspace=${MOVE_DEVSPACE:-}
server=${MOVE_SERVER:-agent-runner-01}
server_root=${MOVE_SERVER_ROOT:-/srv/agent-studio}
state_dir=${MOVE_STATE_DIR:-}
extra_mapping=${MOVE_EXTRA_MAPPING:-}
rehearsal_port=${MOVE_REHEARSAL_PORT:-18443}
rehearsal_subnet=${MOVE_REHEARSAL_SUBNET:-172.30.84}
assume_yes=0
force=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --dry-run) mode=dry-run; shift ;;
    --transfer) mode=transfer; shift ;;
    --rehearse) mode=rehearse; shift ;;
    --apply) mode=apply; shift ;;
    --yes) assume_yes=1; shift ;;
    --force) force=1; shift ;;
    --workspace|--devspace|--server|--server-root|--state-dir|--extra-mapping|--rehearsal-port|--rehearsal-subnet)
      [ "$#" -ge 2 ] || fail "$1 needs a value"
      case "$1" in
        --workspace) workspace=$2 ;;
        --devspace) devspace=$2 ;;
        --server) server=$2 ;;
        --server-root) server_root=$2 ;;
        --state-dir) state_dir=$2 ;;
        --extra-mapping) extra_mapping=$2 ;;
        --rehearsal-port) rehearsal_port=$2 ;;
        --rehearsal-subnet) rehearsal_subnet=$2 ;;
      esac
      shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "unknown option: $1" ;;
  esac
done

# The workspace and the devspace are siblings on the workstation
# (C:\Projects\agent-taskboard-workspace, C:\Projects\agent-taskboard-devspace),
# so neither is derived from the other.
[ -n "$workspace" ] || fail '--workspace is required (for example --workspace /c/Projects/agent-taskboard-workspace)'
[ -d "$workspace/.git" ] || fail "workspace is not a Git repository: $workspace"
workspace=$(CDPATH= cd -- "$workspace" && pwd)
[ -n "$state_dir" ] || state_dir="$workspace.move-state"
stable_health=${MOVE_STABLE_HEALTH_URL:-http://127.0.0.1:5031/healthz}
devspace_problem=
if [ -z "$devspace" ]; then
  devspace_problem='--devspace is not set'
elif [ ! -d "$devspace" ]; then
  devspace_problem="devspace does not exist: $devspace"
else
  devspace=$(CDPATH= cd -- "$devspace" && pwd)
  stable_stop="$devspace/stop-stable.sh"
  stable_start="$devspace/start-stable.sh"
  # watchdog-stable.sh skips restarts while ${ROOT}/.stable-maintenance exists.
  stable_maintenance=${STABLE_MAINTENANCE_FILE:-$devspace/.stable-maintenance}
  if [ ! -f "$devspace/agent-taskboard-stable/api.sh" ] || [ ! -f "$stable_start" ]; then
    devspace_problem="$devspace holds no agent-taskboard-stable/api.sh and start-stable.sh"
  else
    stable_checkout=$(CDPATH= cd -- "$devspace/agent-taskboard-stable" && pwd)
    # Exactly api.sh's STUDIO_MOVED_GUARD: "$(dirname "${SCRIPT_DIR}")/.studio-moved-to-server".
    stable_guard="$(dirname -- "$stable_checkout")/.studio-moved-to-server"
  fi
fi
[ "$mode" != apply ] || [ -z "$devspace_problem" ] \
  || fail "$devspace_problem; --apply needs --devspace DIR (for example --devspace /c/Projects/agent-taskboard-devspace)"
maintenance_ours=0
safe_path "$server_root" '--server-root'
case "$rehearsal_port" in ''|*[!0-9]*) fail '--rehearsal-port must be a number' ;; esac
case "$rehearsal_subnet" in *[!0-9.]*|'') fail '--rehearsal-subnet must be three dotted octets' ;; esac
case "$server" in -*|*[!A-Za-z0-9._@-]*|'') fail "unsupported server alias: $server" ;; esac
staging="$server_root/move-staging"

# Native (Windows) spelling of a path when running under Git Bash.
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s\n' "$1"; fi; }

server_side() {
  remote_command="sh $(sq "$server_root/move-bin/move-studio-to-server.sh") --server-side"
  for argument in "$@"; do remote_command="$remote_command $(sq "$argument")"; done
  ssh -o BatchMode=yes "$server" "$remote_command"
}

# Carriage returns are stripped: a Windows checkout may hold these files with CRLF.
upload() {
  tr -d '\r' <"$1" | ssh -o BatchMode=yes "$server" "mkdir -p $(sq "$server_root/move-bin") && cat > $(sq "$server_root/move-bin/$2")"
}

# Repository URL on stdin without credentials, for logs, plan files, and the server's Git config.
strip_url() { node "$script_dir/move-studio-to-server.mjs" strip-url; }

for tool in git node tar gzip ssh curl; do
  command -v "$tool" >/dev/null 2>&1 || fail "missing local tool: $tool"
done
mkdir -p "$state_dir"

inventory() {
  git -C "$workspace" ls-files -z >"$state_dir/tracked.z"
  git -C "$workspace" diff --name-only -z HEAD >"$state_dir/dirty.z"
  : >"$state_dir/remotes.tsv"
  node "$script_dir/move-studio-to-server.mjs" projects --workspace "$(native_path "$workspace")" >"$state_dir/projects.tsv"
  tab=$(printf '\t')
  while IFS="$tab" read -r project_id repository; do
    url=$(git -C "$repository" remote get-url origin 2>/dev/null | strip_url)
    [ -z "$url" ] || printf '%s\t%s\n' "$project_id" "$url" >>"$state_dir/remotes.tsv"
  done <"$state_dir/projects.tsv"
  set -- inventory --workspace "$(native_path "$workspace")" --tracked "$state_dir/tracked.z" --dirty "$state_dir/dirty.z" \
    --remotes "$state_dir/remotes.tsv" --home "$(native_path "$HOME")" \
    --credential-helper "$(git -C "$workspace" config --get credential.helper || true)" --out-dir "$state_dir"
  [ -z "$extra_mapping" ] || set -- "$@" --extra-mapping "$extra_mapping"
  inventory_status=0
  node "$script_dir/move-studio-to-server.mjs" "$@" || inventory_status=$?
  case "$inventory_status" in
    0) ;;
    3) [ "$mode" = dry-run ] || fail 'blocking inventory issues above; resolve the source task storage, token hit, repository URL, or path before moving' ;;
    *) fail "inventory failed with exit $inventory_status" ;;
  esac
}

transfer_pass() {
  upload "$self" move-studio-to-server.sh
  upload "$script_dir/relocate-workspace-paths.mjs" relocate-workspace-paths.mjs
  upload "$state_dir/path-map.json" path-map.json
  git -C "$workspace" remote get-url origin >/dev/null 2>&1 || fail 'the workspace has no origin remote to clone on the server'
  workspace_url=$(git -C "$workspace" remote get-url origin | strip_url)
  clone_state=$(server_side clone-workspace "$staging/workspace" "$workspace_url")
  # A fresh clone invalidates what earlier passes recorded as sent.
  [ "$clone_state" != cloned ] || rm -f "$state_dir/manifest.json"
  head=$(git -C "$workspace" rev-parse HEAD)
  # receive-pack runs outside the --server-side dispatch, so it needs its own umask.
  git -C "$workspace" push -q --receive-pack='umask 077 && git-receive-pack' "$server:$staging/workspace" "+$head:refs/move/source-head"
  server_side sync-workspace "$staging/workspace" "$head"
  log "workspace history at $head on the server ($clone_state clone)"

  set -- delta --inventory "$state_dir/inventory.json" --copy "$state_dir/copy.z" --remove "$state_dir/remove.z" \
    --manifest "$state_dir/manifest.next.json"
  [ ! -f "$state_dir/manifest.json" ] || set -- "$@" --previous "$state_dir/manifest.json"
  node "$script_dir/move-studio-to-server.mjs" "$@"
  if [ -s "$state_dir/copy.z" ]; then
    {
      tar_status=0
      tar -C "$workspace" --null -T "$state_dir/copy.z" --warning=no-file-changed -cf - || tar_status=$?
      echo "$tar_status" >"$state_dir/tar.status"
    } | gzip -1 | server_side receive-archive "$staging/workspace"
    # Exit 1 means a file changed while it was read; its new mtime sends it again next pass.
    case "$(cat "$state_dir/tar.status")" in 0|1) ;; *) fail 'tar failed while streaming the untracked state' ;; esac
  fi
  [ ! -s "$state_dir/remove.z" ] || server_side remove-paths "$staging/workspace" <"$state_dir/remove.z"
  mv "$state_dir/manifest.next.json" "$state_dir/manifest.json"

  tab=$(printf '\t')
  while IFS="$tab" read -r name url; do
    [ -n "$url" ] || fail "checkout $name has no repository URL"
    server_side clone-checkout "$staging/projects/$name" "$url" </dev/null
    log "checkout /data/projects/$name from $url"
  done <"$state_dir/checkouts.tsv"

  # Task storage in a project checkout is usually untracked. A clone cannot
  # carry it, so each external StorageLocation has its own manifest and delta.
  while IFS="$tab" read -r storage_id storage_source; do
    [ -n "$storage_id" ] || continue
    storage_local=$storage_source
    if command -v cygpath >/dev/null 2>&1; then storage_local=$(cygpath -u "$storage_source"); fi
    [ -d "$storage_local" ] || fail "task storage $storage_id disappeared before transfer: $storage_source"
    storage_stage="$staging/storage/$storage_id"
    storage_state=$(server_side ensure-dir "$storage_stage")
    storage_manifest="$state_dir/storage-$storage_id.manifest.json"
    [ "$storage_state" != created ] || rm -f "$storage_manifest"
    set -- delta --inventory "$state_dir/inventory.json" --storage-id "$storage_id" \
      --copy "$state_dir/storage-$storage_id.copy.z" --remove "$state_dir/storage-$storage_id.remove.z" \
      --manifest "$state_dir/storage-$storage_id.manifest.next.json"
    [ ! -f "$storage_manifest" ] || set -- "$@" --previous "$storage_manifest"
    node "$script_dir/move-studio-to-server.mjs" "$@"
    storage_copy="$state_dir/storage-$storage_id.copy.z"
    storage_remove="$state_dir/storage-$storage_id.remove.z"
    if [ -s "$storage_copy" ]; then
      {
        tar_status=0
        tar -C "$storage_local" --null -T "$storage_copy" --warning=no-file-changed -cf - || tar_status=$?
        echo "$tar_status" >"$state_dir/storage-$storage_id.tar.status"
      } | gzip -1 | server_side receive-archive "$storage_stage"
      case "$(cat "$state_dir/storage-$storage_id.tar.status")" in 0|1) ;; *) fail "tar failed while streaming task storage $storage_id" ;; esac
    fi
    [ ! -s "$storage_remove" ] || server_side remove-paths "$storage_stage" <"$storage_remove"
    mv "$state_dir/storage-$storage_id.manifest.next.json" "$storage_manifest"
    log "task storage $storage_id staged at /data/projects/.task-storage/$storage_id"
  done <"$state_dir/storage.tsv"
}

server_side_preflight() {
  upload "$self" move-studio-to-server.sh
  server_side preflight "$server_root"
}

stable_running() { curl --silent --fail --max-time 5 "$stable_health" >/dev/null 2>&1; }

rollback_hint() {
  lift="rm -f $(sq "$stable_guard")"
  [ "$maintenance_ours" -ne 1 ] || lift="$lift $(sq "$stable_maintenance")"
  log 'Rollback: stop the server stack, lift the guard, and restart Stable from the untouched workstation workspace:'
  printf '  ssh %s %s && %s && DETACH=1 %s\n' "$server" \
    "$(sq "docker compose --project-directory $server_root/stack stop")" "$lift" "$(sq "$stable_start")"
  [ "$maintenance_ours" -eq 1 ] || [ ! -e "$stable_maintenance" ] \
    || log "$stable_maintenance was set before this apply; remove it too once the watchdog may restart Stable."
  log "After a completed apply the server keeps $server_root/moved-to-server, so a later --apply needs --force."
  if [ "$force" -eq 1 ]; then
    log "Forced switch snapshot, if the data move began: <data>/workspace.pre-move-$stamp and <data>/projects.pre-move-$stamp (resolve <data> from STUDIO_DATA_DIR in $server_root/stack/.env)."
  fi
}

# Nothing on the server changed yet, no earlier apply left state behind, and
# Stable was serving before the stop, so Stable comes back on its own.
restart_stable() {
  log 'apply stopped before the server switch; the live server data is untouched, restarting Stable'
  rm -f "$stable_guard"
  [ "$maintenance_ours" -ne 1 ] || rm -f "$stable_maintenance"
  if [ -x "$stable_start" ] && DETACH=1 "$stable_start"; then
    log 'Stable restarted; fix the error above and run --apply again'
  else
    log "Stable did not restart; run $stable_start by hand"
  fi
}

# Otherwise the server may be the live Studio (an earlier apply, a failed
# switch) or Stable was not serving: starting it could make two Studios.
keep_stable_down() {
  if [ -n "$prior_guard" ]; then
    printf '%s\n' "$prior_guard" >"$stable_guard"
  fi
  if [ "$prior_state" -eq 1 ]; then
    log 'apply stopped before the server switch; an earlier apply left a moved marker or guard, so the server may serve Studio. Stable stays stopped behind the guard.'
  else
    log "apply stopped before the server switch; Stable did not answer at $stable_health before the stop. Stable stays stopped behind the guard."
  fi
  rollback_hint
}

apply_exit() {
  status=$?
  trap - EXIT
  if [ "$status" -ne 0 ]; then
    case "$phase" in
      guard|stop|final)
        if [ "$prior_state" -eq 0 ] && [ "$stable_was_up" -eq 1 ]; then restart_stable; else keep_stable_down; fi ;;
      switch) rollback_hint ;;
    esac
  fi
  exit "$status"
}

write_guard() {
  printf 'status=%s\nserver=%s:%s\nsince=%s\nlift=docs/operations/setup/move-studio-to-server.md#rollback\n' \
    "$1" "$server" "$server_root" "$stamp" >"$stable_guard.next"
  mv -f "$stable_guard.next" "$stable_guard"
}

log "mode: $mode; workspace $workspace; server $server:$server_root; state $state_dir"
inventory

case "$mode" in
  dry-run)
    # Streams this script instead of uploading it, so the dry run writes nothing on the server.
    if tr -d '\r' <"$self" | ssh -o BatchMode=yes "$server" "sh -s -- --server-side preflight $(sq "$server_root")"; then
      log 'server preflight passed'
    else
      log "server preflight did not pass (see above); fix it before --rehearse"
    fi
    if stable_running; then log "Stable answers at $stable_health"; else log "Stable does not answer at $stable_health"; fi
    if [ -n "$devspace_problem" ]; then
      log "devspace: $devspace_problem; --apply will refuse until --devspace names the folder with agent-taskboard-stable/api.sh and start-stable.sh"
    else
      log "devspace $devspace: Stable checkout $stable_checkout, guard $stable_guard, maintenance marker $stable_maintenance"
      [ ! -f "$stable_guard" ] || log "moved-to-server guard present: $(tr '\n' ' ' <"$stable_guard")"
    fi
    log 'Dry run only: nothing was copied, stopped, or started.'
    log 'Next: --rehearse (Stable keeps running), then --apply in the outage window.'
    ;;
  transfer)
    server_side_preflight
    transfer_pass
    log 'Transfer pass complete. Run it again any time; it sends only the delta.'
    ;;
  rehearse)
    server_side_preflight
    transfer_pass
    if [ -n "${MOVE_REHEARSAL_OWNER_USERNAME:-}" ]; then
      # The owner login travels on stdin, never on a command line.
      printf '%s\n%s\n' "$MOVE_REHEARSAL_OWNER_USERNAME" "${MOVE_REHEARSAL_OWNER_PASSWORD:-}" \
        | server_side rehearse "$server_root" "$rehearsal_port" "$rehearsal_subnet" login
    else
      server_side rehearse "$server_root" "$rehearsal_port" "$rehearsal_subnet" bootstrap </dev/null
    fi
    log 'Rehearsal passed. Staging keeps the transferred state, so --apply only sends the final delta.'
    ;;
  apply)
    server_side_preflight
    moved=$(server_side moved-state "$server_root")
    # Any guard counts: status=switching means an earlier apply failed during
    # the switch, and the server stack may still hold or serve that data.
    prior_state=0
    prior_guard=
    if [ -n "$moved" ] || [ -e "$stable_guard" ]; then
      prior_state=1
      [ ! -e "$stable_guard" ] || prior_guard=$(cat "$stable_guard")
      [ "$force" -eq 1 ] || fail "Studio already moved or an earlier apply did not finish (server: ${moved:-no marker}; workstation guard: $(if [ -n "$prior_guard" ]; then printf '%s' "$prior_guard" | tr '\n' ' '; else echo none; fi)). Another apply would replace the live server data with this workstation snapshot. Roll back first, or pass --force to discard the server's state on purpose."
      log 'warning: --force replaces the live server data with this workstation snapshot'
    fi
    [ -x "$stable_stop" ] || fail "Stable stop script is not executable: $stable_stop"
    grep -Fq 'studio-moved-to-server-guard' "$stable_checkout/api.sh" 2>/dev/null \
      || fail "Stable's api.sh ($stable_checkout/api.sh) does not honour the moved-to-server guard yet; update Stable first"
    if [ "$assume_yes" -ne 1 ]; then
      printf 'Stop Stable on this workstation and switch Studio to %s? Type "move" to continue: ' "$server"
      IFS= read -r answer || answer=
      [ "$answer" = move ] || fail 'apply cancelled'
    fi
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    stable_was_up=0
    if stable_running; then stable_was_up=1; fi
    phase=guard
    trap apply_exit EXIT
    trap 'exit 130' HUP INT TERM
    # Maintenance first, so the watchdog does not restart the backend that is stopped next.
    if [ ! -e "$stable_maintenance" ]; then
      printf 'move-studio-to-server apply %s\n' "$stamp" >"$stable_maintenance"
      maintenance_ours=1
    fi
    write_guard switching
    log "maintenance marker $stable_maintenance and guard $stable_guard set"
    phase=stop
    log 'stopping Stable'
    "$stable_stop"
    ! stable_running || fail "Stable still answers at $stable_health"
    phase=final
    log 'final inventory and delta with Stable stopped'
    inventory
    transfer_pass
    phase=switch
    apply_status=0
    if [ "$force" -eq 1 ]; then
      server_side apply "$server_root" "$stamp" force || apply_status=$?
    else
      server_side apply "$server_root" "$stamp" || apply_status=$?
    fi
    # Exit 87: refused before the live data was touched; apply_exit decides
    # whether Stable may come back. Any other failure stopped the server stack.
    [ "$apply_status" -ne 87 ] || phase=final
    [ "$apply_status" -eq 0 ] || exit "$apply_status"
    write_guard moved
    phase=done
    log 'Studio now runs on the server. Stable stays stopped: the watchdog is in maintenance and api.sh refuses to start past the guard.'
    rollback_hint
    log 'Then: sign in at the public URL, re-point remote runners, and complete the credential replacements listed above.'
    ;;
esac
