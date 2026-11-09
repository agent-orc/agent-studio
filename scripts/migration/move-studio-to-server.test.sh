#!/bin/sh
# Proof for move-studio-to-server.sh without real hosts: a fake ssh runs the
# remote command locally, a fake docker accepts every call, a fake curl serves
# canned Studio API answers, and local bare repositories stand in for the
# workspace and project remotes.
# Run: sh scripts/migration/move-studio-to-server.test.sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
move="$script_dir/move-studio-to-server.sh"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
check() { "$@" || fail "check failed: $*"; }

mkdir -p "$work/bin" "$work/server/stack" "$work/server/data"
# Like the real ssh, the fake consumes standard input for a server-side command
# even when that command reads none of it (1 November 2026: a `while read` loop
# over the task storage roots lost every root after the first). Git transport
# commands keep their interactive pipe.
cat >"$work/bin/ssh" <<'SSH'
#!/bin/sh
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o|-p|-l|-i) shift 2 ;;
    -*) shift ;;
    *) break ;;
  esac
done
shift
case "$*" in
  *' --server-side '*)
    status=0
    sh -c "$*" || status=$?
    [ -t 0 ] || cat >/dev/null
    exit "$status" ;;
esac
exec sh -c "$*"
SSH
# FAKE_DOCKER_RUNNING names containers that `compose ps --status ... --quiet` still reports.
cat >"$work/bin/docker" <<'DOCKER'
#!/bin/sh
printf '%s\n' "$*" >>"$FAKE_LOG"
# The check after the switch asks the API from the frontend container.
case "$*" in *' exec -T frontend wget '*) printf '{"bootstrapRequired":%s}\n' "$FAKE_BOOTSTRAP"; exit 0 ;; esac
# The readiness report comes from inside the API container (loopback only).
case "$*" in *' exec -T api curl '*' http://127.0.0.1:5031/healthz/readiness') cat "$FAKE_READINESS"; exit 0 ;; esac
case "$*" in *' ps --status '*' --quiet') [ -z "${FAKE_DOCKER_RUNNING:-}" ] || echo "$FAKE_DOCKER_RUNNING" ;; esac
case "$*" in
  *'-p studio-move-rehearsal '*' down --remove-orphans'*)
    [ "${FAKE_REHEARSE_DOWN_FAIL:-0}" != 1 ] || exit 48
    rm -f "$FAKE_SERVER_ROOT/rehearsal-active" ;;
  *'-p studio-move-rehearsal '*' ps --status '*' --quiet'*)
    [ "${FAKE_REHEARSE_PS_STUCK:-0}" != 1 ] || echo stuck-container
    [ ! -f "$FAKE_SERVER_ROOT/rehearsal-active" ] || echo rehearsal-container ;;
  *'-p studio-move-rehearsal '*' up -d'*)
    [ ! -f "$FAKE_SERVER_ROOT/rehearsal-active" ] || exit 49
    [ -f "$FAKE_SERVER_ROOT/move-rehearsal/data/projects/.task-storage/PROJ-004/AGT-4/task.md" ] || exit 45
    [ -f "$FAKE_SERVER_ROOT/move-rehearsal/data/projects/.task-storage/PROJ-011/AGT-11/new.md" ] || exit 46
    [ -f "$FAKE_SERVER_ROOT/move-rehearsal/data/projects/.task-storage/PROJ-012/AGT-22/job.json" ] || exit 46
    [ "${FAKE_REHEARSE_UP_FAIL:-0}" != 1 ] || exit 47
    touch "$FAKE_SERVER_ROOT/rehearsal-active" ;;
esac
DOCKER
cat >"$work/bin/curl" <<'CURL'
#!/bin/sh
out= url= write= data=
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) out=$2; shift 2 ;;
    -w) write=$2; shift 2 ;;
    --data-binary) data=${2#@}; shift 2 ;;
    --config) printf 'CONFIG %s\n' "$(cat "$2")" >>"$FAKE_LOG"; shift 2 ;;
    -H) printf 'HEADER %s\n' "$2" >>"$FAKE_LOG"; shift 2 ;;
    -c|-b|--resolve|--max-time) shift 2 ;;
    -*) shift ;;
    *) url=$1; shift ;;
  esac
done
printf 'URL %s\n' "$url" >>"$FAKE_LOG"
[ -z "$data" ] || printf 'BODY %s\n' "$(cat "$data")" >>"$FAKE_LOG"
case "$url" in
  # Stable answers while its fake start script has run and its stop script has not.
  */healthz) [ -e "$FAKE_STABLE_UP" ] || exit 7; body=ok ;;
  */api/auth/status) body="{\"bootstrapRequired\":$FAKE_BOOTSTRAP}" ;;
  */api/auth/bootstrap|*/api/auth/login) body='{"csrfToken":"csrf-1"}' ;;
  */api/projects) body='[{"id":"PROJ-001"}]' ;;
  */api/tasks*) body='[]' ;;
  */api/auth/runner-enrollments) body='{"enrollmentCode":"enrol-1"}' ;;
  */api/auth/runner-enroll) body='{"runnerId":"runner-1","runnerName":"move-rehearsal-runner","secret":"test-secret"}' ;;
  */api/runner/claim) body='{"status":"empty"}' ;;
  *) exit 22 ;;
esac
if [ -n "$out" ]; then printf '%s' "$body" >"$out"; else printf '%s' "$body"; fi
[ -z "$write" ] || printf 200
CURL
# Git stays real except for the anonymous visibility probes of two GitHub repositories.
real_git=$(command -v git)
cat >"$work/bin/git" <<GIT
#!/bin/sh
case "\$*" in
  *'ls-remote --heads -- https://github.com/owner/private-site.git')
    printf 'LS-REMOTE %s helper=%s prompt=%s\\n' "\$*" "\$(printf '%s' "\$*" | grep -c 'credential.helper= ')" "\$GIT_TERMINAL_PROMPT" >>"\$FAKE_LOG"
    echo "fatal: could not read Username for 'https://github.com': terminal prompts disabled" >&2; exit 128 ;;
  *'ls-remote --heads -- https://github.com/owner/public-site.git') exit 0 ;;
esac
exec "$real_git" "\$@"
GIT
chmod 0755 "$work/bin/ssh" "$work/bin/docker" "$work/bin/curl" "$work/bin/git"
export FAKE_LOG="$work/fake.log" FAKE_BOOTSTRAP=true FAKE_STABLE_UP="$work/stable-up"
export FAKE_SERVER_ROOT="$work/server"
# One failing and one passing check: the move lists the failing one with its remedy.
cat >"$work/readiness.json" <<'READINESS'
{"computedAt":"2026-11-01T12:04:00+00:00","state":"fail","failing":1,"attention":0,"checks":[
 {"id":"git-identity","title":"Git identity of the service user","scope":"server","subject":null,"state":"pass","meaning":"Commits are made as Agent Studio.","remedy":null},
 {"id":"integration-lane","title":"Integration lane present","scope":"project","subject":"Demo","state":"fail","meaning":"The integration lane refs/agent-studio/integration/main is missing.","remedy":"Seed it from the published branch: git -C /data/projects/demo update-ref refs/agent-studio/integration/main origin/main"}]}
READINESS
export FAKE_READINESS="$work/readiness.json"
PATH="$work/bin:$PATH"
export PATH
GIT_AUTHOR_NAME=Test GIT_AUTHOR_EMAIL=test@example.invalid
GIT_COMMITTER_NAME=Test GIT_COMMITTER_EMAIL=test@example.invalid
export GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
export MOVE_STABLE_HEALTH_URL=http://127.0.0.1:9/healthz

printf 'name: agent-studio-website\n' >"$work/server/stack/compose.yaml"
printf 'STUDIO_VERSION=1.0.0\nSTUDIO_DOMAIN=studio.test\nSTUDIO_DATA_DIR=%s\n' "$work/server/data" >"$work/server/stack/.env"

# The real layout: workspace and devspace are siblings (C:\Projects\agent-taskboard-workspace
# and C:\Projects\agent-taskboard-devspace). The devspace holds the Stable checkout
# with the api.sh under test and the outer start/stop scripts.
projects_root="$work/Projects"
devspace="$projects_root/agent-taskboard-devspace"
stable="$devspace/agent-taskboard-stable"
mkdir -p "$stable"
cp "$script_dir/../../api.sh" "$stable/api.sh"
printf '#!/bin/sh\nrm -f "%s/stable-up"\ntouch "%s/stable-stopped"\n' "$work" "$work" >"$work/stop-stable.sh"
printf '#!/bin/sh\n[ "$DETACH" = 1 ] || exit 9\ntouch "%s/stable-started" "%s/stable-up"\n' "$work" "$work" >"$devspace/start-stable.sh"
chmod 0755 "$work/stop-stable.sh" "$devspace/start-stable.sh"
cp "$work/stop-stable.sh" "$devspace/stop-stable.sh"

# Project checkout and its remote.
git init -q --bare -b main "$work/remotes/demo.git"
git init -q -b main "$work/code/Demo"
printf 'demo\n' >"$work/code/Demo/README.md"
git -C "$work/code/Demo" add README.md
git -C "$work/code/Demo" commit -qm 'demo'
# scp-style URL: a usable remote for the planner, served through the fake ssh.
git -C "$work/code/Demo" remote add origin "git@test-server:$work/remotes/demo.git"
git -C "$work/code/Demo" push -q origin main
# Three external task storage roots, each with its own cards. A card is a folder
# with task.json or job.json; one below results/ is evidence, not a card.
# Work the remote does not have: develop one commit ahead of origin/develop, an
# integration lane and a backup under refs/agent-studio, and a branch only the
# workstation has. The workstation stands on that branch, but the project
# configures develop as its integration branch.
git -C "$work/code/Demo" checkout -q -b develop
git -C "$work/code/Demo" push -q -u origin develop
printf 'ahead\n' >"$work/code/Demo/ahead.md"
git -C "$work/code/Demo" add ahead.md
git -C "$work/code/Demo" commit -qm 'unpublished develop work'
demo_develop=$(git -C "$work/code/Demo" rev-parse develop)
printf 'lane\n' >"$work/code/Demo/lane.md"
git -C "$work/code/Demo" add lane.md
git -C "$work/code/Demo" commit -qm 'integration lane candidate'
git -C "$work/code/Demo" update-ref refs/agent-studio/lanes/develop/AGT-7 HEAD
git -C "$work/code/Demo" update-ref refs/agent-studio/backups/AGT-6 "$demo_develop"
demo_lane=$(git -C "$work/code/Demo" rev-parse HEAD)
git -C "$work/code/Demo" reset -q --hard "$demo_develop"
git -C "$work/code/Demo" checkout -q -b feature/local
printf 'local\n' >"$work/code/Demo/local.md"
git -C "$work/code/Demo" add local.md
git -C "$work/code/Demo" commit -qm 'local only'
demo_local=$(git -C "$work/code/Demo" rev-parse HEAD)
printf '.orchestrator/\n' >"$work/code/Demo/.git/info/exclude"
# A checkout Studio derives from its task storage (<repo>/.orchestrator/jobs):
# no RepositoryPath and no registry URL. The workstation stands on a local branch.
git init -q --bare -b main "$work/remotes/derived.git"
git init -q -b main "$work/code/Derived"
printf 'derived\n' >"$work/code/Derived/README.md"
git -C "$work/code/Derived" add README.md
git -C "$work/code/Derived" commit -qm 'derived'
git -C "$work/code/Derived" remote add origin "git@test-server:$work/remotes/derived.git"
git -C "$work/code/Derived" push -q origin main
git -C "$work/code/Derived" checkout -q -b work
printf '.orchestrator/\n' >"$work/code/Derived/.git/info/exclude"
storage_derived="$work/code/Derived/.orchestrator/jobs"
mkdir -p "$storage_derived/AGT-20"
printf '# AGT-20\n' >"$storage_derived/AGT-20/task.md"
printf '{"key":"AGT-20"}' >"$storage_derived/AGT-20/task.json"
storage_inside="$work/code/Demo/.orchestrator/jobs"
storage_outside="$work/code/TaskOnly/.orchestrator/jobs"
storage_third="$work/code/Third/.orchestrator/jobs"
mkdir -p "$storage_inside/AGT-4" "$storage_outside/AGT-11" "$storage_third/AGT-21" "$storage_third/AGT-22/results/nested"
printf '# AGT-4\n' >"$storage_inside/AGT-4/task.md"
printf '{"key":"AGT-4"}' >"$storage_inside/AGT-4/task.json"
printf '# AGT-11\n' >"$storage_outside/AGT-11/task.md"
printf '{"key":"AGT-11"}' >"$storage_outside/AGT-11/task.json"
printf '{"key":"AGT-21"}' >"$storage_third/AGT-21/task.json"
printf '{"key":"AGT-22"}' >"$storage_third/AGT-22/job.json"
printf '{"key":"copied evidence"}' >"$storage_third/AGT-22/results/nested/task.json"

# Workspace with pushed history, one unpushed commit, and untracked state.
ws="$projects_root/agent-taskboard-workspace"
git init -q --bare -b main "$work/remotes/workspace.git"
git init -q -b main "$ws"
mkdir -p "$ws/.metadata" "$ws/projects/demo/tasks/AGT-1" "$ws/projects/demo/tasks/AGT-2"
# The registry repo URL carries a password; it must never reach a log, a plan file, or the server.
url_password="pw-$(date +%s)"
cat >"$ws/.metadata/projects.json" <<EOF
{"Projects":[{"Id":"PROJ-001","DisplayName":"Demo","StorageLocation":"$ws/projects/demo/tasks","RepositoryPath":"$work/code/Demo",
 "Urls":[{"Id":"repo","Label":"Repository","Url":"ssh://operator:$url_password@test-server$work/remotes/demo.git"}]},
 {"Id":"PROJ-004","DisplayName":"Checkout tasks","StorageLocation":"$storage_inside","RepositoryPath":"$work/code/Demo","Urls":[{"Id":"repo","Label":"Repository","Url":"ssh://operator:$url_password@test-server$work/remotes/demo.git"}]},
 {"Id":"PROJ-011","DisplayName":"Task only","StorageLocation":"$storage_outside","Urls":[]},
 {"Id":"PROJ-012","DisplayName":"Third tasks","StorageLocation":"$storage_third","Urls":[]},
 {"Id":"PROJ-013","DisplayName":"Private site","Urls":[{"Id":"repo","Label":"Repository","Url":"git@github.com:owner/private-site.git"}]},
 {"Id":"PROJ-014","DisplayName":"Public site","Urls":[{"Id":"repo","Label":"Repository","Url":"https://github.com/owner/public-site.git"}]},
  {"Id":"PROJ-020","DisplayName":"Derived","StorageLocation":"$storage_derived","Urls":[]}]}
EOF
# Demo's visibility is set; the two sites have none and are probed anonymously.
printf '{"Demo":{"IntegrationBranch":"develop","RepositoryVisibility":"public"},"Private site":{"AutoCommit":true}}\n' >"$ws/project-settings.json"
printf '# AGT-1\n' >"$ws/projects/demo/tasks/AGT-1/task.md"
printf '{"key":"AGT-1"}' >"$ws/projects/demo/tasks/AGT-1/task.json"
printf 'open\n' >"$ws/projects/demo/tasks/AGT-1/status.md"
printf '# AGT-2\n' >"$ws/projects/demo/tasks/AGT-2/task.md"
git -C "$ws" add -A
git -C "$ws" commit -qm 'workspace'
# file:// like a network remote: a plain local path would hardlink objects with the source modes.
git -C "$ws" remote add origin "file://$work/remotes/workspace.git"
git -C "$ws" push -q origin main
printf 'note\n' >"$ws/projects/demo/tasks/AGT-1/notes.md"
git -C "$ws" add -A
git -C "$ws" commit -qm 'unpushed evidence'
head=$(git -C "$ws" rev-parse HEAD)
printf 'edited after commit\n' >"$ws/projects/demo/tasks/AGT-1/status.md"
rm "$ws/projects/demo/tasks/AGT-2/task.md"
mkdir -p "$ws/.runtime/cli-output" "$ws/logs/bus" "$ws/projects/demo/tasks/AGT-1/results" "$ws/.orchestration-occurrences"
printf '{"schemaVersion":7}' >"$ws/.metadata/attempt-authority.json"
mkdir -p "$ws/.security"
printf '{"Users":[]}' >"$ws/.security/access-state.json"
printf 'run\n' >"$ws/.runtime/cli-output/AGT-1.log"
printf '{"e":1}\n' >"$ws/logs/bus/day.jsonl"
printf '<p>ok</p>' >"$ws/projects/demo/tasks/AGT-1/results/report.html"
printf '{}' >"$ws/.orchestration-occurrences/o.json"
printf 'https://operator:%s@example.invalid\n' "fake-$(date +%s)" >"$ws/.git-credentials"
printf 'partial' >"$ws/.metadata/write.json.tmp"
printf 'RUNNER_TOKEN=%s\n' "fake-$(date +%s)" >"$ws/runner.env"
mkdir -p "$ws/.docker"
printf '{"auths":{}}' >"$ws/.docker/config.json"
# A CLI log that echoed a GitHub token, built at runtime.
printf 'token %s_%s\n' 'ghp' "$(printf 'x%.0s' $(seq 1 36))" >"$ws/.runtime/cli-output/leak.log"

run() { sh "$move" "$@" --workspace "$ws" --devspace "$devspace" --server test-server --server-root "$work/server" --state-dir "$work/state" </dev/null >"$work/out" 2>&1 || { cat "$work/out" >&2; fail "move-studio-to-server.sh $* exited non-zero"; }; }

run_fails() {
  if sh "$move" "$@" --workspace "$ws" --devspace "$devspace" --server test-server --server-root "$work/server" --state-dir "$work/state" </dev/null >"$work/out" 2>&1; then
    cat "$work/out" >&2; fail "move-studio-to-server.sh $* succeeded but should fail"
  fi
}

# The workspace is never guessed.
if sh "$move" --dry-run --server test-server --server-root "$work/server" >"$work/out" 2>&1; then fail 'ran without --workspace'; fi
check grep -q -- '--workspace is required' "$work/out"

# Dry run without --devspace names what --apply will need.
sh "$move" --dry-run --workspace "$ws" --server test-server --server-root "$work/server" --state-dir "$work/state" </dev/null >"$work/out" 2>&1 \
  || { cat "$work/out" >&2; fail 'dry run without --devspace exited non-zero'; }
check grep -q 'devspace: --devspace is not set' "$work/out"

# Dry run: inventory and preflight, nothing written on the server.
run --dry-run
check grep -qF "devspace $devspace: Stable checkout $stable, guard $devspace/.studio-moved-to-server" "$work/out"
check grep -q 'attempt-authority' "$work/out"
check grep -q 'not copied (credential): .git-credentials' "$work/out"
check grep -q 'not copied (credential): runner.env' "$work/out"
check grep -q 'not copied (credential): .docker/config.json' "$work/out"
check grep -q 'not copied (contains a github-token): .runtime/cli-output/leak.log' "$work/out"
check grep -q 'credentials removed from the URL' "$work/out"
check grep -q 'server preflight passed' "$work/out"
check grep -q "PROJ-004 $storage_inside -> /data/projects/.task-storage/PROJ-004" "$work/out"
check grep -q "PROJ-011 $storage_outside -> /data/projects/.task-storage/PROJ-011" "$work/out"
check grep -q "PROJ-012 $storage_third -> /data/projects/.task-storage/PROJ-012" "$work/out"
check grep -q 'Git Credential Manager entries' "$work/out"
check grep -q 'projects/demo <- .*; integration branch develop' "$work/out"
check grep -q 'projects/derived <- .*(PROJ-020; branch checked out on the workstation)' "$work/out"
check grep -q 'RepositoryPath /data/projects/derived is written for PROJ-020' "$work/out"
check grep -q 'visibility: public (project setting)' "$work/out"
check grep -q 'visibility: private (anonymous probe; written to the server copy)' "$work/out"
check grep -q 'visibility: unknown' "$work/out"
check grep -q 'LS-REMOTE .* helper=1 prompt=0' "$FAKE_LOG"
[ ! -e "$work/server/move-bin" ] || fail 'dry run wrote to the server'
[ ! -e "$work/server/move-staging" ] || fail 'dry run created staging'

# First pass.
run --transfer
staged="$work/server/move-staging/workspace"
check test "$(git -C "$staged" rev-parse HEAD)" = "$head"
check test "$(cat "$staged/projects/demo/tasks/AGT-1/status.md")" = 'edited after commit'
check test -f "$staged/.metadata/attempt-authority.json"
check test -f "$staged/.runtime/cli-output/AGT-1.log"
check test -f "$staged/logs/bus/day.jsonl"
check test -f "$staged/projects/demo/tasks/AGT-1/results/report.html"
check test -f "$staged/.orchestration-occurrences/o.json"
check test -f "$work/server/move-staging/storage/PROJ-004/AGT-4/task.md"
check test -f "$work/server/move-staging/storage/PROJ-011/AGT-11/task.md"
# Every storage root arrives, not only the first one the loop reads.
check test -f "$work/server/move-staging/storage/PROJ-012/AGT-21/task.json"
check test -f "$work/server/move-staging/storage/PROJ-012/AGT-22/job.json"
check test -f "$work/server/move-staging/storage/PROJ-012/AGT-22/results/nested/task.json"
check grep -q 'task storage PROJ-012 staged' "$work/out"
printf 'PROJ-001\tworkspace/projects/demo/tasks\t1\nPROJ-004\tstorage/PROJ-004\t1\nPROJ-011\tstorage/PROJ-011\t1\nPROJ-012\tstorage/PROJ-012\t2\nPROJ-020\tstorage/PROJ-020\t1\n' >"$work/cards.expected"
check cmp -s "$work/cards.expected" "$work/server/move-bin/cards.tsv"
[ ! -e "$staged/.git-credentials" ] || fail 'credential file was transferred'
[ ! -e "$staged/runner.env" ] || fail 'runner.env was transferred'
[ ! -e "$staged/.docker/config.json" ] || fail 'Docker config was transferred'
[ ! -e "$staged/.runtime/cli-output/leak.log" ] || fail 'file with a token pattern was transferred'
check test "$(stat -c %a "$work/server/move-staging")" = 700
check test "$(stat -c %a "$staged/.security")" = 700
check test "$(stat -c %a "$staged/.security/access-state.json")" = 600
for file in "$work/out" "$work/state/checkouts.tsv" "$work/state/inventory.json" "$work/state/remotes.tsv" \
  "$work/server/move-staging/projects/demo/.git/config"; do
  ! grep -q "$url_password" "$file" || fail "repository URL password leaked into $file"
done
[ ! -e "$staged/.metadata/write.json.tmp" ] || fail 'transient file was transferred'
[ ! -e "$staged/projects/demo/tasks/AGT-2/task.md" ] || fail 'tracked file deleted on the source still exists'
check grep -q 'Delta: 7 to copy, 1 to remove' "$work/out"
# The checkout stands on the configured integration branch with the
# workstation's unpublished commit; lanes, backups, and local branches arrived.
demo_staged="$work/server/move-staging/projects/demo"
check test "$(git -C "$demo_staged" symbolic-ref --short HEAD)" = develop
check test "$(git -C "$demo_staged" rev-parse HEAD)" = "$demo_develop"
check test "$(git -C "$demo_staged" rev-parse --abbrev-ref 'develop@{upstream}')" = origin/develop
check test "$(git -C "$demo_staged" rev-parse refs/agent-studio/lanes/develop/AGT-7)" = "$demo_lane"
check test "$(git -C "$demo_staged" rev-parse refs/agent-studio/backups/AGT-6)" = "$demo_develop"
check test "$(git -C "$demo_staged" rev-parse refs/heads/feature/local)" = "$demo_local"
check test -z "$(git -C "$demo_staged" status --porcelain)"
check grep -q 'checkout /data/projects/demo from .* on develop' "$work/out"
derived_staged="$work/server/move-staging/projects/derived"
check test "$(git -C "$derived_staged" symbolic-ref --short HEAD)" = work
check test -f "$work/server/move-staging/storage/PROJ-020/AGT-20/task.md"
check grep -q '"PROJ-020": "/data/projects/derived"' "$work/server/move-bin/repository-paths.json"

# Second pass sends only the delta: one changed log, one new file, one removal.
sleep 1
printf '{"e":2}\n' >>"$ws/logs/bus/day.jsonl"
printf 'new' >"$ws/.runtime/cli-output/AGT-3.log"
rm "$ws/.runtime/cli-output/AGT-1.log"
# The lane was published and deleted on the workstation: it leaves staging too.
git -C "$work/code/Demo" update-ref -d refs/agent-studio/lanes/develop/AGT-7
run --transfer
! git -C "$demo_staged" rev-parse -q --verify refs/agent-studio/lanes/develop/AGT-7 >/dev/null || fail 'a deleted lane stayed in staging'
check test "$(git -C "$demo_staged" symbolic-ref --short HEAD)" = develop
check grep -q 'Delta: 3 to copy, 2 to remove' "$work/out"
check grep -q 'Delta: 0 to copy, 0 to remove, 2 unchanged' "$work/out"
check grep -q 'Delta: 0 to copy, 0 to remove, 3 unchanged' "$work/out"
check test "$(wc -l <"$staged/logs/bus/day.jsonl")" -eq 2
check test -f "$staged/.runtime/cli-output/AGT-3.log"
[ ! -e "$staged/.runtime/cli-output/AGT-1.log" ] || fail 'removed source file still exists in staging'
check test "$(cat "$staged/projects/demo/tasks/AGT-1/status.md")" = 'edited after commit'

# The external task root has its own manifest. A later pass sends only its
# changed file, removes a vanished one, and leaves the checkout root intact.
printf '# AGT-12\n' >"$storage_outside/AGT-11/new.md"
rm "$storage_outside/AGT-11/task.md"
run --transfer
check test -f "$work/server/move-staging/storage/PROJ-011/AGT-11/new.md"
[ ! -e "$work/server/move-staging/storage/PROJ-011/AGT-11/task.md" ] || fail 'vanished external task file remained in staging'
check grep -q 'Delta: 1 to copy, 1 to remove' "$work/out"

# Unchanged third pass: the Git reset restores tracked state, so the modified
# tracked file is sent and the deleted tracked file removed again; nothing else.
run --transfer
check grep -q 'Delta: 1 to copy, 1 to remove' "$work/out"
check test "$(cat "$staged/projects/demo/tasks/AGT-1/status.md")" = 'edited after commit'

# A token appears in a file an earlier pass sent: it leaves staging. A token in
# orchestration state blocks the transfer instead of being dropped silently.
sleep 1
token="$(printf 'ghp')_$(printf 'y%.0s' $(seq 1 36))"
printf 'pushed with %s\n' "$token" >>"$ws/.runtime/cli-output/AGT-3.log"
run --transfer
check grep -q 'not copied (contains a github-token): .runtime/cli-output/AGT-3.log' "$work/out"
[ ! -e "$staged/.runtime/cli-output/AGT-3.log" ] || fail 'a file that now holds a token stayed in staging'
cp "$ws/.orchestration-occurrences/o.json" "$work/o.json"
printf '{"note":"%s"}' "$token" >"$ws/.orchestration-occurrences/o.json"
run_fails --transfer
check grep -q 'token-in-state.*\.orchestration-occurrences/o.json' "$work/out"
check grep -q 'github-token in .orchestration-occurrences/o.json' "$work/out"
check grep -q 'token hits above block the move' "$work/out"
[ ! -s "$work/state/accepted-token-hits.tsv" ] || fail 'a token hit was accepted without an answer'
if printf 'accept\n' | sh "$move" --transfer --yes --workspace "$ws" --server test-server --server-root "$work/server" \
  --state-dir "$work/state" >"$work/out" 2>&1; then fail '--yes accepted a token hit'; fi
check grep -q 'token hits above block the move' "$work/out"
cp "$work/o.json" "$ws/.orchestration-occurrences/o.json"
rm "$ws/.runtime/cli-output/AGT-3.log"

# Rehearsal: copy, relocation, separate Compose project, smoke checks, teardown.
: >"$FAKE_LOG"
run --rehearse --rehearsal-port 18443
for line in 'check: cards PROJ-001 1 on the server, as on the source' 'check: cards PROJ-012 2 on the server, as on the source' \
  'check: path relocation ok' 'check: access state parses' 'check: health ok' 'check: sign-in ok (bootstrap' \
  'check: board read ok (PROJ-001)' 'check: runner claim ok (move-rehearsal-runner, status empty)' 'rehearsal: torn down'; do
  check grep -qF "$line" "$work/out"
done
check grep -q -- '-p studio-move-rehearsal' "$FAKE_LOG"
check grep -q 'readiness: 1 failing, 0 need attention, 2 checks' "$work/out"
check grep -q -- '-p studio-move-rehearsal .* exec -T api curl .* -H Host: move-rehearsal.studio.test http://127.0.0.1:5031/healthz/readiness' "$FAKE_LOG"
check grep -q -- '-p studio-move-rehearsal .* down --remove-orphans' "$FAKE_LOG"
check grep -q 'HEADER X-CSRF-Token: csrf-1' "$FAKE_LOG"
check grep -q 'CONFIG header = "Authorization: Bearer test-secret"' "$FAKE_LOG"
check grep -q '"availableSlots":0' "$FAKE_LOG"
check grep -qF 'check: git indexes refreshed' "$work/out"
refresh_line=$(grep -n -- '-p studio-move-rehearsal .* run --rm --no-deps -T --entrypoint sh api -c ' "$FAKE_LOG" | head -n 1 | cut -d: -f1)
up_line=$(grep -n -- '-p studio-move-rehearsal .* up -d' "$FAKE_LOG" | head -n 1 | cut -d: -f1)
[ -n "$refresh_line" ] && [ "$refresh_line" -lt "$up_line" ] || fail 'the rehearsal did not refresh the git indexes before the start'
# Owner password and one-time codes reach the payloads through stdin, not node's command line.
check grep -q 'BODY {"username":"move.rehearsal.owner","password":"Rehearsal-[0-9a-f]*","displayName":"Move rehearsal owner","bootstrapCode":"[0-9a-f]*"}' "$FAKE_LOG"
check grep -qx 'BODY {"code":"enrol-1"}' "$FAKE_LOG"
[ ! -e "$work/server/move-rehearsal" ] || fail 'rehearsal copy was not torn down'
check test -f "$staged/.security/access-state.json"

: >"$FAKE_LOG"
FAKE_REHEARSE_UP_FAIL=1 run_fails --rehearse
check grep -q -- '-p studio-move-rehearsal .* logs --tail 80' "$FAKE_LOG"
check grep -q -- '-p studio-move-rehearsal .* down --remove-orphans' "$FAKE_LOG"
[ ! -e "$work/server/move-rehearsal" ] || fail 'failed rehearsal copy was not torn down'

# A teardown failure must report failure, keep its copy, and leave the old
# stack for the next rehearsal to stop before touching that copy.
: >"$FAKE_LOG"
FAKE_REHEARSE_DOWN_FAIL=1 run_fails --rehearse
check grep -q 'rehearsal: shutdown failed; copy kept' "$work/out"
! grep -q 'rehearsal: torn down' "$work/out" || fail 'failed teardown claimed success'
[ -e "$work/server/move-rehearsal/.env" ] || fail 'failed teardown discarded the rehearsal copy'
check test -f "$work/server/rehearsal-active"
: >"$FAKE_LOG"
run --rehearse
check grep -q 'rehearsal: torn down' "$work/out"
[ ! -e "$work/server/rehearsal-active" ] || fail 'prior rehearsal stack was not stopped'
[ ! -e "$work/server/move-rehearsal" ] || fail 'prior rehearsal copy was not removed'
check test "$(grep -c -- '-p studio-move-rehearsal .* down --remove-orphans' "$FAKE_LOG")" -eq 2
down_line=$(grep -n -- '-p studio-move-rehearsal .* down --remove-orphans' "$FAKE_LOG" | head -n 1 | cut -d: -f1)
up_line=$(grep -n -- '-p studio-move-rehearsal .* up -d' "$FAKE_LOG" | head -n 1 | cut -d: -f1)
[ "$down_line" -lt "$up_line" ] || fail 'prior rehearsal was removed before its stack stopped'

# A successful Compose down response is insufficient if a container remains.
FAKE_REHEARSE_PS_STUCK=1 run_fails --rehearse
check grep -q 'rehearsal: shutdown failed; copy kept' "$work/out"
[ -e "$work/server/move-rehearsal/.env" ] || fail 'running-container failure discarded the copy'
run --rehearse

# A card lost on the way stops the rehearsal before its stack starts. The
# manifest says the file was sent, so only a fresh storage manifest repairs it.
rm "$work/server/move-staging/storage/PROJ-012/AGT-22/job.json"
: >"$FAKE_LOG"
run_fails --rehearse
check grep -q 'card count differs for PROJ-012: source 2, server 1' "$work/out"
check grep -q 'the rehearsal copy holds a different number of cards than the source' "$work/out"
! grep -q -- '-p studio-move-rehearsal .* up -d' "$FAKE_LOG" || fail 'rehearsal stack started with missing cards'
[ ! -e "$work/server/move-rehearsal" ] || fail 'rehearsal copy with missing cards was not torn down'
rm "$work/state/storage-PROJ-012.manifest.json"
run --rehearse
check grep -q 'check: cards PROJ-012 2 on the server, as on the source' "$work/out"

# Apply. Stable scripts record what ran; the guard and the maintenance marker
# live in the devspace, where api.sh and the watchdog look, not beside the workspace.
guard="$devspace/.studio-moved-to-server"
maintenance="$devspace/.stable-maintenance"
data="$work/server/data"
# Registers a checkout without a repository URL while Stable stops: a blocking final inventory.
cat >"$work/stop-stable-blocking.sh" <<STOP
#!/bin/sh
rm -f "$work/stable-up"
touch "$work/stable-stopped"
node -e 'const fs=require("fs");const f=process.argv[1];const r=JSON.parse(fs.readFileSync(f,"utf8"));r.Projects.push({Id:"PROJ-002",DisplayName:"Lost",RepositoryPath:process.argv[2],Urls:[]});fs.writeFileSync(f,JSON.stringify(r));' \
  "$ws/.metadata/projects.json" "$work/code/NoRemote"
STOP
chmod 0755 "$work/stop-stable-blocking.sh"
blocking_stop() { cp "$work/stop-stable-blocking.sh" "$devspace/stop-stable.sh"; }
normal_stop() { cp "$work/stop-stable.sh" "$devspace/stop-stable.sh"; git -C "$ws" checkout -q -- .metadata/projects.json; }
mkdir -p "$data/workspace"
printf 'empty install' >"$data/workspace/README"

# Each case starts with Stable serving.
fresh() { rm -f "$work/stable-stopped" "$work/stable-started"; touch "$work/stable-up"; : >"$FAKE_LOG"; }
pre_moves() { find "$data" -maxdepth 1 -name 'workspace.pre-move-*' | wc -l | tr -d ' '; }
stable_refuses() {
  if API_REQUIRE_PREBUILT=1 bash "$stable/api.sh" start >"$work/api.out" 2>&1; then return 1; fi
  grep -q 'Studio has moved to a server' "$work/api.out"
}
untouched() {
  check test "$(cat "$data/workspace/README")" = 'empty install'
  check test "$(pre_moves)" -eq 0
  [ ! -e "$work/server/moved-to-server" ] || fail 'moved marker written by a failed apply'
}
restarted() {
  check test -f "$work/stable-started"
  [ ! -e "$guard" ] || fail 'guard left behind after Stable restarted'
  [ ! -e "$maintenance" ] || fail 'maintenance marker left behind after Stable restarted'
  check grep -q 'restarting Stable' "$work/out"
}
kept_down() {
  [ ! -e "$work/stable-started" ] || fail 'Stable restarted although it had to stay down'
  check test -f "$guard"
  check test -f "$maintenance"
  check stable_refuses
  check grep -q 'Stable stays stopped behind the guard' "$work/out"
  check grep -q 'Rollback:' "$work/out"
}
rollback() {
  command=$(sed -n '/Rollback:/{n;p;}' "$work/out" | tail -n 1)
  [ -n "$command" ] || fail 'no rollback command printed'
  sh -c "$command"
}
# Nothing may land beside the workspace, where no reader looks.
nothing_beside_workspace() {
  [ ! -e "$projects_root/.studio-moved-to-server" ] || fail 'guard written beside the workspace'
  [ ! -e "$projects_root/.stable-maintenance" ] || fail 'maintenance marker written beside the workspace'
}

# A Stable whose api.sh predates the guard: refused before Stable stops.
printf '#!/bin/sh\n' >"$stable/api.sh"
fresh
run_fails --apply --yes
check grep -q 'does not honour the moved-to-server guard' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'Stable stopped although its api.sh has no guard'
cp "$script_dir/../../api.sh" "$stable/api.sh"

# Apply needs the devspace that holds the Stable checkout and its scripts.
fresh
if sh "$move" --apply --yes --workspace "$ws" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1; then
  fail 'apply ran without --devspace'
fi
check grep -q -- '--apply needs --devspace' "$work/out"
if sh "$move" --apply --yes --workspace "$ws" --devspace "$projects_root" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1; then
  fail 'apply ran with the folder beside the workspace as devspace'
fi
check grep -q 'holds no agent-taskboard-stable/api.sh and start-stable.sh' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'Stable stopped without a valid devspace'
nothing_beside_workspace

# A blocking issue in the final inventory restarts Stable and leaves the server alone.
fresh
blocking_stop
run_fails --apply --yes
check test -f "$work/stable-stopped"
check grep -q 'missing-repository-url' "$work/out"
restarted
untouched
normal_stop

# A server stack that keeps running after stop: refused before live data moves, Stable restarts.
fresh
FAKE_DOCKER_RUNNING=website-api-1 run_fails --apply --yes
check grep -q 'the server stack still runs after stop (website-api-1)' "$work/out"
check grep -q 'ps --status running --status restarting --status paused --quiet' "$FAKE_LOG"
restarted
untouched

# Staging that lost a card is refused before the server stack stops; Stable restarts.
fresh
rm "$work/server/move-staging/storage/PROJ-012/AGT-21/task.json"
run_fails --apply --yes
check grep -q 'card count differs for PROJ-012: source 2, server 1' "$work/out"
check grep -q 'staging holds a different number of cards than the source' "$work/out"
! grep -q 'server/stack .* stop$' "$FAKE_LOG" || fail 'a card mismatch stopped the server stack'
restarted
untouched
rm "$work/state/storage-PROJ-012.manifest.json"

# Stable did not answer before the stop: a pre-switch failure keeps it down.
fresh
rm -f "$work/stable-up"
blocking_stop
run_fails --apply --yes
kept_down
check grep -qx 'status=switching' "$guard"
untouched
normal_stop
rollback
[ ! -e "$guard" ] || fail 'rollback did not lift the guard'

# Failure after the switch began: the server stack is stopped again, Stable
# stays stopped behind the guard, and the printed rollback command restores it.
fresh
FAKE_BOOTSTRAP=true run_fails --apply --yes
check grep -q 'asks for owner bootstrap' "$work/out"
check grep -q 'switch failed: server stack stopped' "$work/out"
up_line=$(grep -n 'server/stack .* up -d' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
stop_line=$(grep -n 'server/stack .* stop$' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
[ -n "$up_line" ] && [ -n "$stop_line" ] && [ "$stop_line" -gt "$up_line" ] || fail 'a failed switch did not stop the server stack'
[ ! -e "$work/stable-started" ] || fail 'Stable restarted although the server data was switched'
check grep -qx 'status=switching' "$guard"
check test -f "$maintenance"
check stable_refuses
nothing_beside_workspace
check test "$(pre_moves)" -eq 1
cp "$work/out" "$work/failed-switch.out"

# A re-run after a failed switch is refused without --force, before Stable is touched.
fresh
rm -f "$work/stable-up"
run_fails --apply --yes
check grep -q 'an earlier apply did not finish' "$work/out"
check grep -q 'workstation guard: status=switching' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'a re-run after a failed switch ran the Stable stop'
check test "$(pre_moves)" -eq 1

cp "$work/failed-switch.out" "$work/out"
rollback
check test -f "$work/stable-started"
[ ! -e "$guard" ] || fail 'rollback did not lift the guard'
[ ! -e "$maintenance" ] || fail 'rollback did not lift the maintenance marker'
check grep -q 'compose --project-directory .*/server/stack stop' "$FAKE_LOG"
API_REQUIRE_PREBUILT=1 bash "$stable/api.sh" start >"$work/api.out" 2>&1 || true
check grep -q 'api-prebuilt-required' "$work/api.out"
! grep -q 'Studio has moved' "$work/api.out" || fail 'api.sh still refuses after the rollback'

# Apply: Stable stop, final delta, switch, check, moved marker, guard, rollback hint.
# A harmless token hit turns up while Stable stops; the operator accepts it and
# the apply goes on in the same window.
fixture_log="$ws/projects/demo/tasks/AGT-1/results/fixture.log"
cat >"$devspace/stop-stable.sh" <<STOP
#!/bin/sh
rm -f "$work/stable-up"
touch "$work/stable-stopped"
printf 'test fixture %s\n' "$token" >"$fixture_log"
STOP
sleep 1
fresh
if ! printf 'move\naccept\n' | FAKE_BOOTSTRAP=false sh "$move" --apply --workspace "$ws" --devspace "$devspace" --server test-server \
  --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1; then
  cat "$work/out" >&2; fail 'apply with an accepted token hit exited non-zero'
fi
check grep -q 'github-token in projects/demo/tasks/AGT-1/results/fixture.log' "$work/out"
check grep -qF "accepted; recorded in $work/state/accepted-token-hits.tsv" "$work/out"
check grep -qx 'github-token	projects/demo/tasks/AGT-1/results/fixture.log' "$work/state/accepted-token-hits.tsv"
check test -f "$data/workspace/projects/demo/tasks/AGT-1/results/fixture.log"
normal_stop
check test -f "$work/stable-stopped"
[ ! -e "$work/stable-started" ] || fail 'Stable restarted after a successful apply'
check test "$(git -C "$data/workspace" rev-parse HEAD)" = "$head"
check test -f "$data/workspace/logs/bus/day.jsonl"
check test -d "$data/projects/demo/.git"
check test -f "$data/projects/.task-storage/PROJ-004/AGT-4/task.md"
check test -f "$data/projects/.task-storage/PROJ-011/AGT-11/new.md"
check test -f "$data/projects/.task-storage/PROJ-012/AGT-21/task.json"
[ ! -e "$data/projects/.task-storage/PROJ-011/AGT-11/task.md" ] || fail 'vanished task was copied on apply'
check grep -q 'check: cards PROJ-012 2 on the server, as on the source' "$work/out"
check test "$(pre_moves)" -eq 2
check test "$(stat -c %a "$data/workspace/.security/access-state.json")" = 600
[ -z "$(find "$data/workspace" "$data/projects" ! -type l -perm /077 | head -n 1)" ] || fail 'moved data is readable by other accounts'
check grep -q 'check: staging relocates with 0 unresolved paths' "$work/out"
check grep -q 'Repository visibility carried for 2 project(s): Private site, Public site' "$work/out"
check node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));
  if (s["Private site"].RepositoryVisibility!=="private"||s["Private site"].AutoCommit!==true
      ||s["Public site"].RepositoryVisibility!=="public"||s.Demo.RepositoryVisibility!=="public") process.exit(1);' \
  "$data/workspace/project-settings.json"
check grep -q 'check: server stack stopped' "$work/out"
check grep -q 'check: https://studio.test answers locally with the moved accounts' "$work/out"
check grep -q '^moved_at=' "$work/server/moved-to-server"
[ ! -e "$work/server/move-staging" ] || fail 'staging was not removed after the apply'
check grep -qx 'status=moved' "$guard"
check grep -q '^move-studio-to-server apply ' "$maintenance"
check stable_refuses
nothing_beside_workspace
check grep -q 'Rollback:' "$work/out"
check grep -qF "DETACH=1 '$devspace/start-stable.sh'" "$work/out"
check grep -q 'compose --project-directory .*/server/stack .*up -d' "$FAKE_LOG"
# After the switch the move lists every failing readiness check with its remedy.
check grep -q 'readiness: 1 failing, 0 need attention, 2 checks (computed 2026-11-01T12:04:00+00:00)' "$work/out"
check grep -q '  FAIL integration-lane \[Demo\]: Integration lane present' "$work/out"
check grep -q 'remedy: Seed it from the published branch: git -C /data/projects/demo update-ref' "$work/out"
! grep -q 'FAIL git-identity' "$work/out" || fail 'a passing readiness check was listed as failing'
check grep -q "readiness: report kept at $work/server/move-bin/readiness.json" "$work/out"
check grep -q -- '--env-file .*/server/stack/.env exec -T api curl .* -H Host: studio.test http://127.0.0.1:5031/healthz/readiness' "$FAKE_LOG"
check grep -q 'Fix every FAIL line of the readiness report above' "$work/out"
# Checkouts keep their branch and refs; the derived project is linked to its clone.
check test "$(git -C "$data/projects/demo" symbolic-ref --short HEAD)" = develop
check test "$(git -C "$data/projects/demo" rev-parse refs/heads/feature/local)" = "$demo_local"
check test "$(git -C "$data/projects/derived" symbolic-ref --short HEAD)" = work
check test -f "$data/projects/.task-storage/PROJ-020/AGT-20/task.md"
check grep -q 'RepositoryPath written for PROJ-020' "$work/out"
repository_path() { node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.stdout.write(String(r.Projects.find(p=>p.Id===process.argv[2]).RepositoryPath))' "$data/workspace/.metadata/projects.json" "$1"; }
check test "$(repository_path PROJ-020)" = /data/projects/derived
check test "$(repository_path PROJ-011)" = undefined
# The indexes are refreshed as the service user after the ownership change and before the stack starts.
check grep -q 'check: git indexes refreshed' "$work/out"
chown_line=$(grep -n 'run --rm --user 0 .*chown.* 10001:10001 ' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
refresh_line=$(grep -n 'server/stack .* run --rm --no-deps -T --entrypoint sh api -c ' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
up_line=$(grep -n 'server/stack .* up -d' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
[ -n "$chown_line" ] && [ -n "$refresh_line" ] && [ "$chown_line" -lt "$refresh_line" ] && [ "$refresh_line" -lt "$up_line" ] \
  || fail 'apply did not refresh the git indexes between the ownership change and the start'

# A second apply would roll the live server back to the workstation snapshot: refused before Stable stops.
fresh
rm -f "$work/stable-up"
FAKE_BOOTSTRAP=false run_fails --apply --yes
check grep -q 'Studio already moved' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'second apply stopped Stable'
check test "$(pre_moves)" -eq 2
check grep -qx 'status=moved' "$guard"

# --force with a blocking final inventory: the server is the live Studio, so
# Stable stays stopped and the moved guard stays in place.
fresh
blocking_stop
FAKE_BOOTSTRAP=false run_fails --apply --yes --force
check grep -q 'missing-repository-url' "$work/out"
check grep -q '<data>/workspace.pre-move-' "$work/out"
check grep -q 'an earlier apply left a moved marker or guard' "$work/out"
kept_down
check grep -qx 'status=moved' "$guard"
check test "$(pre_moves)" -eq 2
normal_stop

# --force replaces the server data on purpose; the existing maintenance marker stays.
sleep 1
fresh
FAKE_BOOTSTRAP=false run --apply --yes --force
check grep -q 'warning: --force' "$work/out"
check test "$(pre_moves)" -eq 3
check test "$(git -C "$data/workspace" rev-parse HEAD)" = "$head"
check test -f "$maintenance"
check grep -qx 'status=moved' "$guard"

printf 'move-studio-to-server: required workspace and devspace, read-only dry run, first pass with four storage roots, branches, lanes, a derived checkout, delta pass, idempotent repeat, rehearsal, card count checks, token acceptance, index refresh, apply guards, restart only when safe, failed-switch stop and re-run refusal, rollback, re-apply refusal, and forced apply verified.\n'
