#!/usr/bin/env bash
# Backup and restore drill for the networked website stack (AGT-3365).
#
# Starts a throwaway stack without a backup destination and checks that it
# runs and reports backups as off. It then signs in, stores a sealed secret,
# adds a project checkout with a Studio-only ref and task storage, and sends
# an encrypted backup to a write-only directory. A second, empty stack is
# restored from that archive with the offline age identity and the separate
# secret store key copy, started, and signed in to. The drill also checks
# that the archive is encrypted, holds no secret store key and no checkout
# content, and that restore refuses a wrong key, a wrong identity and a
# stack that is not empty.
#
# Needs Docker, age, git, curl, python3 and the API and web images of
# VERSION (built here unless WEBSITE_REUSE_WEBSITE_IMAGES=1). The stacks use
# their own Compose project names, subnet and a loopback HTTPS port, so a
# website already running on this host is not touched.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stack="$repo_root/deploy/compose/monolith-networked"
version="$(tr -d '\r\n' < "$repo_root/VERSION")"
api_image="ghcr.io/agent-orc/agent-studio-api:v$version"
web_image="ghcr.io/agent-orc/agent-studio-web:v$version"
operations_server_image="agent-operations-server:website-smoke"
operations_agent_image="agent-operations-agent:website-smoke"
subnet="${STUDIO_TEST_SUBNET_PREFIX:-172.30.184}"
port="${STUDIO_TEST_HTTPS_PORT:-18443}"
work="$(mktemp -d)"
chmod 755 "$work"
project_a="studio-backup-drill-a-$$"
project_b="studio-backup-drill-b-$$"

for tool in age age-keygen git curl python3 docker; do
  command -v "$tool" > /dev/null || { echo "$tool is required" >&2; exit 1; }
done

compose_in() {
  local dir="$1" project="$2"
  shift 2
  (cd "$dir" && COMPOSE_PROJECT_NAME="$project" docker compose "$@")
}

finish() {
  result=$?
  if [ "$result" -ne 0 ] && [ -n "${JOB_RESULTS_DIR:-}" ]; then
    mkdir -p "$JOB_RESULTS_DIR"
    for side in a b; do
      [ -f "$work/$side/stack/.env" ] || continue
      project=$project_a; [ "$side" = b ] && project=$project_b
      compose_in "$work/$side/stack" "$project" logs --tail 150 > "$JOB_RESULTS_DIR/backup-restore-$side.log" 2>&1 || true
    done
  fi
  for side in a b; do
    [ -f "$work/$side/stack/.env" ] || continue
    project=$project_a; [ "$side" = b ] && project=$project_b
    compose_in "$work/$side/stack" "$project" down --remove-orphans > /dev/null 2>&1 || true
  done
  chmod -R u+rwx "$work" 2>/dev/null || true
  docker run --rm --user 0 -v "$work:/cleanup" --entrypoint chown caddy:2-alpine \
    -R "$(id -u):$(id -g)" /cleanup > /dev/null 2>&1 || true
  rm -rf "$work"
  exit "$result"
}
trap finish EXIT

if [ "${WEBSITE_REUSE_WEBSITE_IMAGES:-0}" != 1 ]; then
  docker build -f "$repo_root/backend/Dockerfile" --build-arg VERSION="$version" -t "$api_image" "$repo_root"
  docker build -f "$repo_root/frontend/Dockerfile" --build-arg VERSION="$version" -t "$web_image" "$repo_root"
  docker build -f "$repo_root/operations-server/Dockerfile" -t "$operations_server_image" "$repo_root"
  docker build -f "$repo_root/operations-agent/Dockerfile" -t "$operations_agent_image" "$repo_root"
fi
docker pull caddy:2-alpine >/dev/null
edge_reference="$(docker image inspect --format '{{index .RepoDigests 0}}' caddy:2-alpine)"
export STUDIO_API_DIGEST="$(openssl rand -hex 32)"
export STUDIO_WEB_DIGEST="$(openssl rand -hex 32)"
export STUDIO_EDGE_DIGEST="${edge_reference##*@sha256:}"
export STUDIO_OPERATIONS_SERVER_DIGEST="$(openssl rand -hex 32)"
export STUDIO_OPERATIONS_AGENT_DIGEST="$(openssl rand -hex 32)"
export STUDIO_ACCESS_AUTH_URL=https://access.example.test
export STUDIO_ACCESS_MFA_ENFORCED=true

# A copy of the release stack with an override that moves it off the
# production subnet and ports. Compose loads compose.override.yaml by
# default, so backup.sh and restore.sh see the same project.
new_stack() {
  local side="$1"
  mkdir -p "$work/$side"
  cp -a "$stack/." "$work/$side/stack"
  rm -f "$work/$side/stack/.env" "$work/$side/stack/.backup-last"
  sed "s/172\.30\.83\./$subnet./g" "$stack/Caddyfile.frontend" > "$work/$side/stack/Caddyfile.frontend.test"
  cat > "$work/$side/stack/compose.override.yaml" <<EOF
services:
  api:
    image: $api_image
    environment:
      Security__TrustedProxyAddresses: $subnet.3,$subnet.2
    networks: !override
      website: {ipv4_address: $subnet.4}
  frontend:
    image: $web_image
    volumes: !override
      - ./Caddyfile.frontend.test:/etc/caddy/Caddyfile:ro
    networks: !override
      website: {ipv4_address: $subnet.3}
  edge:
    ports: !override
      - '127.0.0.1:$port:443'
    networks: !override
      website: {ipv4_address: $subnet.2}
  operations-bootstrap:
    image: $operations_server_image
  operations-server:
    image: $operations_server_image
    ports: !override []
    networks: !override
      website: {ipv4_address: $subnet.6}
  operations-agent:
    image: $operations_agent_image
    networks: !override
      website: {ipv4_address: $subnet.7}
networks:
  website:
    driver_opts: !reset {}
    ipam: !override
      config:
        - subnet: $subnet.0/24
EOF
  (cd "$work/$side/stack" && bash setup.sh --private "$version" studio.test "$work/$side/data" > "$work/$side/setup.log")
}

url=https://studio.test
curl_base=(--silent --show-error --insecure --connect-to "studio.test:443:127.0.0.1:$port")

wait_ready() {
  for attempt in $(seq 1 90); do
    if curl "${curl_base[@]}" --fail "$url/api/auth/status" > "$work/status.json" 2> /dev/null; then return 0; fi
    sleep 2
  done
  echo 'Website did not become ready' >&2
  exit 1
}

json_field() {
  python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$1" "$2"
}

password='correct horse battery staple!'
login_payload="$(python3 -c 'import json,sys; print(json.dumps({"username":"drill.owner","password":sys.argv[1]}))' "$password")"

# sign_in <cookie-jar>: prints the CSRF token of the new session.
sign_in() {
  rm -f "$1"
  curl "${curl_base[@]}" --fail -c "$1" -H 'Content-Type: application/json' -H "Origin: $url" \
    -d "$login_payload" "$url/api/auth/login" > /dev/null
  awk '$6 == "__Host-agentstudio-csrf" { print $7 }' "$1"
}

in_api_image() {
  docker run --rm --user 10001:10001 --network none -e HOME=/tmp --entrypoint /bin/bash "$@"
}

echo '== Stack A: no destination configured'
new_stack a
grep -q 'Backups are off' "$work/a/setup.log"
compose_in "$work/a/stack" "$project_a" up -d --no-build
wait_ready
compose_in "$work/a/stack" "$project_a" ps --status running --services | sort > "$work/a/running"
printf 'api\nedge\nfrontend\noperations-agent\noperations-server\n' | cmp -s - "$work/a/running" || { cat "$work/a/running" >&2; exit 1; }
(cd "$work/a/stack" && COMPOSE_PROJECT_NAME="$project_a" bash backup.sh) > "$work/a/off.txt"
grep -q '^Backups are off' "$work/a/off.txt"
(cd "$work/a/stack" && COMPOSE_PROJECT_NAME="$project_a" bash backup.sh status) | grep -q '^Backups are off'
echo 'Stack runs without a destination and reports that backups are off.'

code="$(sed -n 's/^STUDIO_BOOTSTRAP_CODE=//p' "$work/a/stack/.env")"
bootstrap="$(python3 -c 'import json,sys; print(json.dumps({"username":"drill.owner","password":sys.argv[1],"displayName":"Drill Owner","bootstrapCode":sys.argv[2]}))' "$password" "$code")"
curl "${curl_base[@]}" --fail -H 'Content-Type: application/json' -H "Origin: $url" -d "$bootstrap" "$url/api/auth/bootstrap" > /dev/null
csrf="$(sign_in "$work/a/jar")"
[ -n "$csrf" ] || { echo 'No CSRF cookie after sign-in' >&2; exit 1; }
probe_value="drill-$(openssl rand -hex 12)"
curl "${curl_base[@]}" --fail -b "$work/a/jar" -H 'Content-Type: application/json' -H "Origin: $url" -H "X-CSRF-Token: $csrf" \
  -d "{\"name\":\"backup-drill\",\"value\":\"$probe_value\"}" "$url/api/secrets/workspace" > /dev/null

# A project checkout with a ref that exists only in Studio, a worktree, and
# task storage outside any checkout.
git init -q --bare "$work/remote.git"
git clone -q "$work/remote.git" "$work/seed"
git -C "$work/seed" -c user.name=Drill -c user.email=drill@example.invalid commit -q --allow-empty -m 'Remote history'
echo 'tracked content' > "$work/seed/README.md"
git -C "$work/seed" add README.md
git -C "$work/seed" -c user.name=Drill -c user.email=drill@example.invalid commit -q -m 'Add readme'
git -C "$work/seed" push -q origin HEAD:main
git --git-dir "$work/remote.git" symbolic-ref HEAD refs/heads/main
studio_ref="$(in_api_image -v "$work/a/data/projects:/data/projects" -v "$work/remote.git:$work/remote.git:ro" "$api_image" -c "
  set -e
  git clone -q '$work/remote.git' /data/projects/demo
  cd /data/projects/demo
  commit=\$(git -c user.name=Studio -c user.email=studio@example.invalid commit-tree HEAD^{tree} -p HEAD -m 'Studio lane')
  git update-ref refs/agent-studio/integration/main \$commit
  git worktree add -q /data/projects/demo-worktree -b drill-feature
  mkdir -p /data/projects/.task-storage/PROJ-1
  echo 'task card' > /data/projects/.task-storage/PROJ-1/AGT-1.md
  echo \$commit")"

echo '== Stack A: encrypted backup to a write-only destination'
mkdir -p "$work/offline" "$work/offsite"
age-keygen -o "$work/offline/identity.txt" 2> /dev/null
age-keygen -y "$work/offline/identity.txt" > "$work/a/stack/backup-recipients.txt"
(cd "$work/a/stack" && bash secret-key.sh print "$work/a/data") > "$work/offline/secrets.kek"
key_id="$(bash "$work/a/stack/secret-key.sh" id "$work/offline/secrets.kek")"
# The host may create files in the destination but not list or read them.
chmod 0300 "$work/offsite"
sed -i "s|^STUDIO_BACKUP_DESTINATION=.*|STUDIO_BACKUP_DESTINATION=$work/offsite|" "$work/a/stack/.env"

cp "$work/a/stack/backup-recipients.txt" "$work/recipients.good"
cat "$work/offline/identity.txt" >> "$work/a/stack/backup-recipients.txt"
if (cd "$work/a/stack" && COMPOSE_PROJECT_NAME="$project_a" bash backup.sh) > /dev/null 2> "$work/a/private.log"; then
  echo 'backup.sh accepted a private key in the recipients file' >&2
  exit 1
fi
grep -q 'holds a private key' "$work/a/private.log"
cp "$work/recipients.good" "$work/a/stack/backup-recipients.txt"

(cd "$work/a/stack" && COMPOSE_PROJECT_NAME="$project_a" bash backup.sh) | tee "$work/a/backup.txt"
grep -q '^ok ' "$work/a/stack/.backup-last"
compose_in "$work/a/stack" "$project_a" ps --status running --services | grep -qx api
compose_in "$work/a/stack" "$project_a" down --remove-orphans > /dev/null

# The operator later fetches the archive with read access.
chmod 0700 "$work/offsite"
archive="$(find "$work/offsite" -name 'studio-backup-*.tar.gz.age' -print -quit)"
[ -n "$archive" ] && [ -f "$archive.sha256" ] || { ls -la "$work/offsite" >&2; exit 1; }
head -c 21 "$archive" | grep -q '^age-encryption.org/v1' || { echo 'The archive is not age-encrypted' >&2; exit 1; }
age --decrypt --identity "$work/offline/identity.txt" "$archive" > "$work/plain.tar.gz"
tar -tzf "$work/plain.tar.gz" > "$work/members"
for member in studio-backup/manifest.env studio-backup/checkouts.tsv studio-backup/bundles/1.bundle \
  workspace/.security/access-state.json projects/.task-storage/PROJ-1/AGT-1.md; do
  grep -qx "$member" "$work/members" || { echo "The archive lacks $member" >&2; exit 1; }
done
if grep -Eq '^(keys/|projects/demo/|projects/demo-worktree/)' "$work/members"; then
  echo 'The archive holds checkout content or the secret store key' >&2
  exit 1
fi
if gzip -dc "$work/plain.tar.gz" | grep -aqF "$(tr -d '\r\n' < "$work/offline/secrets.kek")"; then
  echo 'The archive holds the secret store key' >&2
  exit 1
fi
tar -xzOf "$work/plain.tar.gz" studio-backup/manifest.env | grep -qx "secret_key_id=$key_id"
rm -f "$work/plain.tar.gz"
echo 'Archive is encrypted, holds workspace, access state, task storage and Studio refs, and no key or checkout content.'

echo '== Stack B: restore into an empty stack'
new_stack b
if (cd "$work/b/stack" && COMPOSE_PROJECT_NAME="$project_b" bash restore.sh --identity "$work/b/stack/.env" \
  --secret-key "$work/offline/secrets.kek" "$archive") > /dev/null 2>&1; then
  echo 'restore.sh accepted a wrong identity' >&2
  exit 1
fi
head -c 32 /dev/urandom | base64 > "$work/wrong.kek"
if (cd "$work/b/stack" && COMPOSE_PROJECT_NAME="$project_b" bash restore.sh --identity "$work/offline/identity.txt" \
  --secret-key "$work/wrong.kek" "$archive") > /dev/null 2> "$work/b/wrong-key.log"; then
  echo 'restore.sh accepted a wrong secret store key' >&2
  exit 1
fi
grep -q "needs $key_id" "$work/b/wrong-key.log"
(cd "$work/b/stack" && COMPOSE_PROJECT_NAME="$project_b" bash restore.sh --identity "$work/offline/identity.txt" \
  --secret-key "$work/offline/secrets.kek" "$archive")
if (cd "$work/b/stack" && COMPOSE_PROJECT_NAME="$project_b" bash restore.sh --identity "$work/offline/identity.txt" \
  --secret-key "$work/offline/secrets.kek" "$archive") > /dev/null 2> "$work/b/again.log"; then
  echo 'restore.sh restored into a stack that is not empty' >&2
  exit 1
fi
grep -q 'not empty' "$work/b/again.log"

compose_in "$work/b/stack" "$project_b" up -d --no-build
wait_ready
[ "$(json_field "$work/status.json" bootstrapRequired)" = False ] || { cat "$work/status.json" >&2; exit 1; }
csrf="$(sign_in "$work/b/jar")"
[ -n "$csrf" ] || { echo 'Sign-in to the restored stack failed' >&2; exit 1; }
curl "${curl_base[@]}" --fail -b "$work/b/jar" "$url/api/secrets/workspace/backup-drill" > "$work/b/secret.json"
# Rotating seals a new version, which needs the restored key.
curl "${curl_base[@]}" --fail -b "$work/b/jar" -H 'Content-Type: application/json' -H "Origin: $url" -H "X-CSRF-Token: $csrf" \
  -d "{\"value\":\"$probe_value-rotated\"}" "$url/api/secrets/workspace/backup-drill/rotate" > /dev/null
in_api_image -v "$work/b/data/projects:/data/projects:ro" "$api_image" -c "
  set -e
  test \"\$(git -C /data/projects/demo rev-parse refs/agent-studio/integration/main)\" = '$studio_ref'
  test \"\$(git -C /data/projects/demo config --get remote.origin.url)\" = '$work/remote.git'
  test \"\$(git -C /data/projects/demo symbolic-ref --short HEAD)\" = main
  test -f /data/projects/demo/README.md
  test ! -e /data/projects/demo-worktree
  grep -qx 'task card' /data/projects/.task-storage/PROJ-1/AGT-1.md"
echo 'Restored stack signs in, opens the sealed secret, and has the Studio ref and task storage back.'

message='Backup and restore drill passed: off state reported, encrypted write-only backup, restore into an empty stack, sign-in.'
echo "$message"
if [ -n "${JOB_RESULTS_DIR:-}" ]; then
  mkdir -p "$JOB_RESULTS_DIR"
  printf '%s\n' "$message" > "$JOB_RESULTS_DIR/backup-restore-drill.txt"
fi
