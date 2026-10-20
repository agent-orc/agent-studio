#!/usr/bin/env bash

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver_source="$repo_root/scripts/release/promote-develop-to-main.sh"
window_source="$repo_root/scripts/release/release-gate-window.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-tests)
trap 'rm -rf -- "$test_root"' EXIT HUP INT TERM

# The gate capacity window must never touch the real runner units of the host
# that runs these tests (they run inside the promotion gate itself). A fake
# systemctl reports both units as absent unless a scenario seeds their quota.
fake_window_bin="$test_root/fake-window-bin"
mkdir -p "$fake_window_bin"
cat > "$fake_window_bin/systemctl" <<'FAKE'
#!/usr/bin/env bash
set -eu
state=${FAKE_SYSTEMCTL_STATE:-/nonexistent}
case "$1" in
  show)
    if [[ ! -f "$state/$5.quota" ]]; then
      [[ "$3" == LoadState ]] && { printf 'not-found\n'; exit 0; }
      exit 1
    fi
    [[ "$3" == LoadState ]] && { printf 'loaded\n'; exit 0; }
    cat "$state/$5.quota"
    ;;
  set-property)
    printf '%s %s\n' "$3" "$4" >> "$state/calls.log"
    value=${4#CPUQuota=}
    if [[ -z "$value" ]]; then printf 'infinity\n'; else printf '%ss\n' "$((${value%\%} / 100))"; fi \
      > "$state/$3.quota"
    ;;
  *) exit 64 ;;
esac
FAKE
chmod +x "$fake_window_bin/systemctl"
printf '1.25 1.00 1.00 1/100 1\n' > "$test_root/fake-loadavg"
export RELEASE_GATE_SYSTEMCTL="$fake_window_bin/systemctl"
export RELEASE_GATE_SUDO=
export RELEASE_GATE_LOADAVG_FILE="$test_root/fake-loadavg"
export RELEASE_GATE_CPU_COUNT=12
export RELEASE_GATE_SETTLE_SECONDS=0
export FAKE_SYSTEMCTL_STATE="$test_root/no-runner-units"

# The release package step publishes the gated backend. A fake dotnet records
# each call and writes the runtime files fetch-remote-artifacts.sh requires;
# FAKE_DOTNET_FAIL=1 simulates a failed publish. It is on PATH only for the
# scenarios that pass it explicitly.
fake_dotnet_bin="$test_root/fake-dotnet-bin"
mkdir -p "$fake_dotnet_bin"
cat > "$fake_dotnet_bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
set -eu
printf '%s|%s\n' "$PWD" "$*" >> "${FAKE_DOTNET_LOG:?}"
if [[ "${FAKE_DOTNET_FAIL:-}" == 1 ]]; then
  printf '%s\n' 'fixture publish failed' >&2
  exit 1
fi
test "$1" = publish
output=
while (($#)); do
  if [[ "$1" == --output ]]; then output=$2; shift 2; else shift; fi
done
mkdir -p "$output"
for file in OrchestratorApi.dll OrchestratorApi.deps.json OrchestratorApi.runtimeconfig.json; do
  printf 'fixture %s\n' "$file" > "$output/$file"
done
FAKE
chmod +x "$fake_dotnet_bin/dotnet"

make_fixture() {
  local name=$1
  local gate_mode=$2
  local topology_mode=${3:-linear}
  local fixture="$test_root/$name"
  local remote="$fixture/remote.git"
  local seed="$fixture/seed"
  local operator="$fixture/operator"

  mkdir -p "$fixture"
  git init --bare --quiet "$remote"
  git init --quiet "$seed"
  git -C "$seed" config user.name 'Promotion Test'
  git -C "$seed" config user.email 'promotion-test@example.invalid'
  mkdir -p "$seed/scripts/release"
  cp "$driver_source" "$seed/scripts/release/promote-develop-to-main.sh"
  cp "$window_source" "$seed/scripts/release/release-gate-window.sh"
  chmod +x "$seed/scripts/release/promote-develop-to-main.sh" \
    "$seed/scripts/release/release-gate-window.sh"

  case "$gate_mode" in
    pass)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
test -d "${2:-}"
# Like the real gate's final step, leave an ignored production frontend build.
mkdir -p "$2/frontend/dist/frontend/browser/media"
printf '%s\n' '<!doctype html>' > "$2/frontend/dist/frontend/browser/index.html"
printf '%s\n' 'fixture asset' > "$2/frontend/dist/frontend/browser/media/asset.txt"
printf '%s\n' 'fixture licenses' > "$2/frontend/dist/frontend/3rdpartylicenses.txt"
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    fail)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' 'fixture gate failed' >&2
exit 9
EOF
      ;;
    move-develop)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
candidate=$2
remote_url=$(git -C "$candidate" remote get-url origin)
advance_checkout=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-develop-advance)
trap 'rm -rf -- "$advance_checkout"' EXIT HUP INT TERM
git clone --quiet --branch develop "$remote_url" "$advance_checkout"
git -C "$advance_checkout" config user.name 'Promotion Test Gate'
git -C "$advance_checkout" config user.email 'promotion-test-gate@example.invalid'
git -C "$advance_checkout" commit --quiet --allow-empty -m 'develop advances during gate'
git -C "$advance_checkout" push --quiet origin HEAD:refs/heads/develop
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    move-main)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
test "${1:-}" = --repo
candidate=$2
remote_url=$(git -C "$candidate" remote get-url origin)
advance_checkout=$(mktemp -d 2>/dev/null || mktemp -d -t promotion-main-advance)
trap 'rm -rf -- "$advance_checkout"' EXIT HUP INT TERM
git clone --quiet --branch main "$remote_url" "$advance_checkout"
git -C "$advance_checkout" config user.name 'Promotion Test Gate'
git -C "$advance_checkout" config user.email 'promotion-test-gate@example.invalid'
git -C "$advance_checkout" commit --quiet --allow-empty -m 'main advances during gate'
git -C "$advance_checkout" push --quiet origin HEAD:refs/heads/main
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
      ;;
    incomplete)
      cat > "$seed/scripts/release/promotion-full-gate.sh" <<'EOF'
#!/usr/bin/env bash
set -eu
printf '%s\n' 'fixture omitted the completion marker'
EOF
      ;;
    *)
      printf 'Unknown gate fixture: %s\n' "$gate_mode" >&2
      return 2
      ;;
  esac
  chmod +x "$seed/scripts/release/promotion-full-gate.sh"
  printf '%s\n' base > "$seed/payload.txt"
  printf 'FROM scratch\nARG SHA\nLABEL org.opencontainers.image.revision="$SHA"\n' > "$seed/Dockerfile"
  printf '%s\n' 'frontend/dist/' > "$seed/.gitignore"
  git -C "$seed" add .
  git -C "$seed" commit --quiet -m 'base'
  git -C "$seed" branch -M main
  git -C "$seed" remote add origin "$remote"
  git -C "$seed" push --quiet -u origin main
  git --git-dir="$remote" symbolic-ref HEAD refs/heads/main

  if [[ "$topology_mode" == diverged ]]; then
    printf '%s\n' main > "$seed/payload.txt"
    git -C "$seed" commit --quiet -am 'main-only change'
    git -C "$seed" push --quiet origin main
    git -C "$seed" checkout --quiet -b develop HEAD~1
  else
    git -C "$seed" checkout --quiet -b develop
  fi
  printf '%s\n' develop > "$seed/payload.txt"
  printf '%s\n' "develop-$name" > "$seed/develop.txt"
  # Historical integrated content may contain whitespace findings. Promotion
  # records them for review but leaves pass/fail authority with the full gate.
  printf '%s  \n' "historical-$name" > "$seed/historical-whitespace.txt"
  git -C "$seed" add .
  git -C "$seed" commit --quiet -m 'develop work'
  git -C "$seed" push --quiet -u origin develop

  git clone --quiet --branch develop "$remote" "$operator"
  git -C "$operator" config user.name 'Promotion Operator'
  git -C "$operator" config user.email 'promotion-operator@example.invalid'
  printf '%s\n' "$operator"
}

run_expect_rc() {
  local expected=$1
  shift
  set +e
  "$@"
  local actual=$?
  set -e
  if [[ "$actual" != "$expected" ]]; then
    printf 'Expected rc=%s, got rc=%s: %s\n' "$expected" "$actual" "$*" >&2
    return 1
  fi
}

# A dry run against a local bare remote records the exact candidate without
# running the gate or changing either remote ref.
preview_operator=$(make_fixture preview pass)
preview_remote="$test_root/preview/remote.git"
preview_evidence="$test_root/preview/evidence"
preview_main=$(git --git-dir="$preview_remote" rev-parse refs/heads/main)
"$preview_operator/scripts/release/promote-develop-to-main.sh" \
  --dry-run --tag release/test-preview --evidence-dir "$preview_evidence" >/dev/null
test "$(git --git-dir="$preview_remote" rev-parse refs/heads/main)" = "$preview_main"
! git --git-dir="$preview_remote" show-ref --verify --quiet refs/tags/release/test-preview
grep -q '"status":"preview"' "$preview_evidence/promotion-record.json"
grep -q '"gate":"not-run"' "$preview_evidence/promotion-record.json"
grep -q '"hostLoadAtGateStart":null,"hostLoadAtGateEnd":null,"gateDurationSeconds":null,"appliedQuotas":{}' \
  "$preview_evidence/promotion-record.json"
printf '%s\n' 'local bare remote dry-run passed'

# A normal execute run promotes the exact develop tip, produces one annotated
# marker, records full-gate evidence, and performs an atomic remote update.
green_operator=$(make_fixture green pass)
green_remote="$test_root/green/remote.git"
green_evidence="$test_root/green/evidence"
green_units="$test_root/green/runner-units"
mkdir -p "$green_units"
printf 'infinity\n' > "$green_units/agent-runner.service.quota"
printf '6s\n' > "$green_units/agent-runner-review.service.quota"
old_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
develop=$(git --git-dir="$green_remote" rev-parse refs/heads/develop)
green_dotnet_log="$test_root/green/dotnet.log"
FAKE_SYSTEMCTL_STATE="$green_units" FAKE_DOTNET_LOG="$green_dotnet_log" \
  PATH="$fake_dotnet_bin:$PATH" \
  "$green_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-green --required-ancestor "$develop" \
  --evidence-dir "$green_evidence" >/dev/null
new_main=$(git --git-dir="$green_remote" rev-parse refs/heads/main)
test "$new_main" = "$develop"
git --git-dir="$green_remote" merge-base --is-ancestor "$old_main" "$new_main"
test "$(git --git-dir="$green_remote" cat-file -t refs/tags/release/test-green)" = tag
test "$(git --git-dir="$green_remote" rev-parse refs/tags/release/test-green^{})" = "$new_main"
grep -q '"status":"promoted"' "$green_evidence/promotion-record.json"
grep -q '"atomicPush":true' "$green_evidence/promotion-record.json"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$green_evidence/full-gate.log"
grep -q 'historical-whitespace.txt' "$green_evidence/candidate-whitespace-review.txt"
# Without --release-dir nothing is published or packaged.
test ! -e "$green_dotnet_log"
test ! -e "$green_evidence/release-publish.log"
grep -q '"releasePackage":null' "$green_evidence/promotion-record.json"
# The gate ran inside the capacity window: both units were throttled, then
# restored, and the record attributes the gate to its host load (AGT-2982).
test "$(cat "$green_units/agent-runner.service.quota")" = infinity
test "$(cat "$green_units/agent-runner-review.service.quota")" = 6s
test "$(tr '\n' '|' < "$green_units/calls.log")" = \
  'agent-runner.service CPUQuota=200%|agent-runner-review.service CPUQuota=500%|agent-runner.service CPUQuota=|agent-runner-review.service CPUQuota=600%|'
grep -q '"hostLoadAtGateStart":1.25,"hostLoadAtGateEnd":1.25,"gateDurationSeconds":[0-9][0-9]*,' \
  "$green_evidence/promotion-record.json"
grep -Fq '"appliedQuotas":{"agent-runner.service":{"recorded":"infinity","applied":"200%"},"agent-runner-review.service":{"recorded":"600%","applied":"500%"}}' \
  "$green_evidence/promotion-record.json"
grep -Fq '"gateWindow":{"mode":"applied","cpuCount":12,"reservedCores":5,"loadThreshold":24.00,"loadBeforeWait":1.25,"loadWaitSeconds":0,"quotasRestored":"restored","gateExit":"0"}' \
  "$green_evidence/promotion-record.json"
printf '%s\n' 'local bare remote execute passed'

# Annotated tags use the promotion identity even when HOME is empty and neither
# repository nor process environment provides a Git identity.
identity_operator=$(make_fixture no-identity pass)
identity_remote="$test_root/no-identity/remote.git"
identity_evidence="$test_root/no-identity/evidence"
identity_home="$test_root/no-identity/empty-home"
identity_xdg="$test_root/no-identity/empty-xdg"
mkdir -p "$identity_home" "$identity_xdg"
git -C "$identity_operator" config --unset-all user.name
git -C "$identity_operator" config --unset-all user.email
test -z "$(HOME="$identity_home" XDG_CONFIG_HOME="$identity_xdg" \
  GIT_CONFIG_NOSYSTEM=1 git -C "$identity_operator" config --get user.name || true)"
test -z "$(HOME="$identity_home" XDG_CONFIG_HOME="$identity_xdg" \
  GIT_CONFIG_NOSYSTEM=1 git -C "$identity_operator" config --get user.email || true)"
env -u GIT_AUTHOR_NAME -u GIT_AUTHOR_EMAIL \
  -u GIT_COMMITTER_NAME -u GIT_COMMITTER_EMAIL -u EMAIL \
  HOME="$identity_home" XDG_CONFIG_HOME="$identity_xdg" \
  GIT_CONFIG_NOSYSTEM=1 \
  "$identity_operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag release/test-no-identity \
    --evidence-dir "$identity_evidence" >/dev/null
identity_candidate=$(git --git-dir="$identity_remote" rev-parse refs/heads/develop)
test "$(git --git-dir="$identity_remote" rev-parse refs/heads/main)" = "$identity_candidate"
test "$(git --git-dir="$identity_remote" cat-file -t refs/tags/release/test-no-identity)" = tag
test "$(git --git-dir="$identity_remote" for-each-ref \
  --format='%(taggername)|%(taggeremail)' refs/tags/release/test-no-identity)" \
  = 'Agent Studio Promotion|<promotion@agent-studio.invalid>'
grep -q '"status":"promoted"' "$identity_evidence/promotion-record.json"
printf '%s\n' 'empty-HOME annotated tag execute passed'

# A tag creation failure after a passing gate leaves the remote unchanged and
# writes the failure, gate result, and Git error to the durable record.
tag_failure_operator=$(make_fixture tag-failure pass)
tag_failure_remote="$test_root/tag-failure/remote.git"
tag_failure_evidence="$test_root/tag-failure/evidence"
tag_failure_main=$(git --git-dir="$tag_failure_remote" rev-parse refs/heads/main)
run_expect_rc 6 env PROMOTION_TAGGER_NAME='<' \
  "$tag_failure_operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag release/test-tag-failure \
    --evidence-dir "$tag_failure_evidence" >/dev/null 2>&1
test "$(git --git-dir="$tag_failure_remote" rev-parse refs/heads/main)" = "$tag_failure_main"
! git --git-dir="$tag_failure_remote" show-ref --verify --quiet \
  refs/tags/release/test-tag-failure
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$tag_failure_evidence/full-gate.log"
grep -q '"status":"blocked-tag"' "$tag_failure_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$tag_failure_evidence/promotion-record.json"
grep -q '"atomicPush":false' "$tag_failure_evidence/promotion-record.json"
grep -q '"error":"annotated tag creation failed with exit code 128:' \
  "$tag_failure_evidence/promotion-record.json"
grep -q 'name consists only of disallowed characters' "$tag_failure_evidence/tag.log"
printf '%s\n' 'simulated tag failure record passed'

# An atomic push rejection is likewise a complete post-gate record with the
# remote hook's error and without either remote ref changing.
push_failure_operator=$(make_fixture push-failure pass)
push_failure_remote="$test_root/push-failure/remote.git"
push_failure_evidence="$test_root/push-failure/evidence"
push_failure_main=$(git --git-dir="$push_failure_remote" rev-parse refs/heads/main)
cat > "$push_failure_remote/hooks/pre-receive" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' 'fixture push rejected' >&2
exit 1
EOF
chmod +x "$push_failure_remote/hooks/pre-receive"
run_expect_rc 6 "$push_failure_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-push-failure \
  --evidence-dir "$push_failure_evidence" >/dev/null 2>&1
test "$(git --git-dir="$push_failure_remote" rev-parse refs/heads/main)" = "$push_failure_main"
! git --git-dir="$push_failure_remote" show-ref --verify --quiet \
  refs/tags/release/test-push-failure
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$push_failure_evidence/full-gate.log"
grep -q '"status":"blocked-push"' "$push_failure_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$push_failure_evidence/promotion-record.json"
grep -q '"atomicPush":false' "$push_failure_evidence/promotion-record.json"
grep -q '"error":"atomic main/tag push failed with exit code' \
  "$push_failure_evidence/promotion-record.json"
grep -q 'fixture push rejected' "$push_failure_evidence/push.log"
printf '%s\n' 'simulated atomic push failure record passed'

# With --release-dir a promoted run packages the release from the gated
# worktree in exactly the layout fetch-remote-artifacts.sh validates, and the
# record names the archive and its sha256.
package_operator=$(make_fixture package pass)
package_remote="$test_root/package/remote.git"
package_evidence="$test_root/package/evidence"
package_releases="$test_root/package/releases"
package_dotnet_log="$test_root/package/dotnet.log"
package_sha=$(git --git-dir="$package_remote" rev-parse refs/heads/develop)
FAKE_DOTNET_LOG="$package_dotnet_log" PATH="$fake_dotnet_bin:$PATH" \
  "$package_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-package --evidence-dir "$package_evidence" \
  --release-dir "$package_releases" >/dev/null
package_dir="$package_releases/$package_sha"
test "$(git --git-dir="$package_remote" rev-parse refs/heads/main)" = "$package_sha"
test "$(find "$package_releases" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')" = 1
test "$(wc -l < "$package_dotnet_log" | tr -d ' ')" = 1
# The publish ran in the gate worktree (removed by now) into a staging
# directory that was renamed into place.
publish_call=$(cat "$package_dotnet_log")
publish_cwd=${publish_call%%|*}
[[ "$publish_cwd" == */candidate && ! -e "$publish_cwd" ]]
[[ "${publish_call#*|}" == "publish backend/OrchestratorApi.csproj --configuration Release --no-build --no-restore --nologo --output $package_releases/.$package_sha.partial."* ]]
test "$(tr -d '\r\n' < "$package_dir/release.sha256")" = \
  "$(sha256sum "$package_dir/release.tar.gz" | cut -d ' ' -f1)"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$package_dir/full-gate.log"
tar -tzf "$package_dir/release.tar.gz" | while IFS= read -r entry; do
  [[ "$entry" == backend/* || "$entry" == frontend/* ]] || { printf 'Unexpected archive path: %s\n' "$entry" >&2; exit 1; }
done
! tar -tvzf "$package_dir/release.tar.gz" | grep -E '^[^d-]' >/dev/null
package_unpack="$test_root/package/unpack"
mkdir -p "$package_unpack"
tar -xzf "$package_dir/release.tar.gz" -C "$package_unpack"
for component in backend frontend; do
  test "$(cat "$package_unpack/$component/RELEASE-SHA")" = "$package_sha"
  test "$(cat "$package_unpack/$component/FULL-GATE")" = "PROMOTION_FULL_GATE=passed:$package_sha"
done
for file in backend/OrchestratorApi.dll backend/OrchestratorApi.deps.json \
  backend/OrchestratorApi.runtimeconfig.json frontend/index.html \
  frontend/media/asset.txt frontend/3rdpartylicenses.txt; do
  test -s "$package_unpack/$file"
done
grep -q '"status":"promoted"' "$package_evidence/promotion-record.json"
grep -Fq "\"releasePackage\":{\"status\":\"written\",\"directory\":\"$package_dir\",\"archive\":\"$package_dir/release.tar.gz\",\"sha256\":\"$(cat "$package_dir/release.sha256")\"}" \
  "$package_evidence/promotion-record.json"
grep -Fq "release package written: $package_dir/release.tar.gz" "$package_evidence/promotion.log"
printf '%s\n' 'release package from the gate worktree passed'

# A configured gate handoff registers only the bytes produced by that gate and
# queues stage without a browser session.
auto_operator=$(make_fixture package-auto pass)
auto_root="$test_root/package-auto"
auto_sha=$(git --git-dir="$auto_root/remote.git" rev-parse refs/heads/develop)
auto_token="$auto_root/deploy.token"
auto_store="$auto_root/artifact-store"
auto_curl_bin="$auto_root/fake-curl-bin"
mkdir -p "$auto_curl_bin"
printf '%s\n' test-gate-credential > "$auto_token"
cat > "$auto_curl_bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -eu
body=
url=
while (($#)); do
  case "$1" in
    --data) body="$2"; shift 2 ;;
    -o) printf '{}\n' > "$2"; shift 2 ;;
    -H) if [[ "$2" == @* ]]; then grep -Fq 'Authorization: Bearer test-gate-credential' "${2#@}"; fi; shift 2 ;;
    http*) url="$1"; shift ;;
    *) shift ;;
  esac
done
if [[ "$url" == */api/deploy/gate-releases ]]; then
  printf '%s\n' "$body" > "${FAKE_DEPLOY_LOG:?}"
  printf 202
else
  printf 200
fi
FAKE
chmod +x "$auto_curl_bin/curl"
FAKE_DOTNET_LOG="$auto_root/dotnet.log" FAKE_DEPLOY_LOG="$auto_root/deploy-call.json" \
  PATH="$auto_curl_bin:$fake_dotnet_bin:$PATH" \
  "$auto_operator/scripts/release/promote-develop-to-main.sh" --execute \
  --tag release/test-package-auto --evidence-dir "$auto_root/evidence" \
  --release-dir "$auto_root/releases" --deploy-artifact-store "$auto_store" \
  --deploy-token-file "$auto_token" --project Demo >/dev/null
auto_digest=$(cat "$auto_root/releases/$auto_sha/release.sha256")
test "$(sha256sum "$auto_store/$auto_digest" | cut -d ' ' -f1)" = "$auto_digest"
grep -Fq "\"artifact\":\"release-package@sha256:$auto_digest\",\"commit\":\"$auto_sha\"" \
  "$auto_root/deploy-call.json"
grep -Fq "\"artifact\":\"release-package@sha256:$auto_digest\",\"sourceSha256\":\"$auto_digest\"" \
  "$auto_root/releases/$auto_sha/deploy-artifact.json"
grep -Fq '"gate":"passed"' "$auto_root/releases/$auto_sha/promotion-record.json"
printf '%s\n' 'gate-built artifact queued to stage passed'

# A 409 for a superseded release comes after main is pushed: the driver logs
# it and continues instead of exiting 9.
superseded_operator=$(make_fixture package-superseded pass)
superseded_root="$test_root/package-superseded"
superseded_curl_bin="$superseded_root/fake-curl-bin"
mkdir -p "$superseded_curl_bin"
printf '%s\n' test-gate-credential > "$superseded_root/deploy.token"
cat > "$superseded_curl_bin/curl" <<'FAKE'
#!/usr/bin/env bash
set -eu
out=
url=
while (($#)); do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    http*) url="$1"; shift ;;
    *) shift ;;
  esac
done
if [[ "$url" == */api/deploy/gate-releases ]]; then
  printf '%s\n' '{"error":"Commit 1234567 is an ancestor of commit 89abcde of the newest release 3."}' > "$out"
  printf 409
else
  printf '{}\n' > "$out"
  printf 200
fi
FAKE
chmod +x "$superseded_curl_bin/curl"
superseded_output=$(FAKE_DOTNET_LOG="$superseded_root/dotnet.log" \
  PATH="$superseded_curl_bin:$fake_dotnet_bin:$PATH" \
  "$superseded_operator/scripts/release/promote-develop-to-main.sh" --execute \
  --tag release/test-package-superseded --evidence-dir "$superseded_root/evidence" \
  --release-dir "$superseded_root/releases" --deploy-artifact-store "$superseded_root/artifact-store" \
  --deploy-token-file "$superseded_root/deploy.token" --project Demo 2>&1)
grep -Fq 'superseded by a newer release (HTTP 409)' <<<"$superseded_output"
printf '%s\n' 'superseded release 409 continues passed'

# The image handoff builds from the gated checkout and uses the build output
# digest. A caller-supplied image reference is only a destination tag.
image_operator=$(make_fixture image-auto pass)
image_root="$test_root/image-auto"
image_sha=$(git --git-dir="$image_root/remote.git" rev-parse refs/heads/develop)
image_docker_bin="$image_root/fake-docker-bin"
mkdir -p "$image_docker_bin"
cat > "$image_docker_bin/docker" <<'FAKE'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$*" > "${FAKE_DOCKER_LOG:?}"
test "$1" = buildx && test "$2" = build
metadata=
while (($#)); do
  if [[ "$1" == --metadata-file ]]; then metadata=$2; shift 2; else shift; fi
done
printf '{"containerimage.digest":"sha256:%s"}\n' "${FAKE_DOCKER_DIGEST:?}" > "$metadata"
FAKE
chmod +x "$image_docker_bin/docker"
image_digest=$(printf 'b%.0s' {1..64})
FAKE_DOTNET_LOG="$image_root/dotnet.log" FAKE_DEPLOY_LOG="$image_root/deploy-call.json" \
  FAKE_DOCKER_LOG="$image_root/docker-call.log" FAKE_DOCKER_DIGEST="$image_digest" \
  PATH="$auto_curl_bin:$image_docker_bin:$fake_dotnet_bin:$PATH" \
  "$image_operator/scripts/release/promote-develop-to-main.sh" --execute \
  --tag release/test-image-auto --evidence-dir "$image_root/evidence" \
  --release-dir "$image_root/releases" --deploy-token-file "$auto_token" \
  --deploy-image-ref registry.example/app:release --deploy-dockerfile Dockerfile \
  --project Demo >/dev/null
grep -Fq -- "--build-arg SHA=$image_sha" "$image_root/docker-call.log"
grep -Fq "\"artifact\":\"registry.example/app:release@sha256:$image_digest\"" \
  "$image_root/releases/$image_sha/deploy-artifact.json"
printf '%s\n' 'gate-built image queued to stage passed'

# A red gate with --release-dir leaves no package and publishes nothing.
red_package_operator=$(make_fixture package-red fail)
red_package_releases="$test_root/package-red/releases"
red_package_dotnet_log="$test_root/package-red/dotnet.log"
run_expect_rc 4 env FAKE_DOTNET_LOG="$red_package_dotnet_log" PATH="$fake_dotnet_bin:$PATH" \
  "$red_package_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-package-red --evidence-dir "$test_root/package-red/evidence" \
  --release-dir "$red_package_releases" >/dev/null 2>&1
test -z "$(ls -A "$red_package_releases")"
test ! -e "$red_package_dotnet_log"
grep -q '"status":"blocked-gate"' "$test_root/package-red/evidence/promotion-record.json"
grep -q '"releasePackage":null' "$test_root/package-red/evidence/promotion-record.json"

# A rejected push with --release-dir likewise leaves no package.
push_package_operator=$(make_fixture package-push pass)
push_package_releases="$test_root/package-push/releases"
cp "$push_failure_remote/hooks/pre-receive" "$test_root/package-push/remote.git/hooks/pre-receive"
run_expect_rc 6 env FAKE_DOTNET_LOG="$test_root/package-push/dotnet.log" PATH="$fake_dotnet_bin:$PATH" \
  "$push_package_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-package-push --evidence-dir "$test_root/package-push/evidence" \
  --release-dir "$push_package_releases" >/dev/null 2>&1
test -z "$(ls -A "$push_package_releases")"
test ! -e "$test_root/package-push/dotnet.log"
grep -q '"status":"blocked-push"' "$test_root/package-push/evidence/promotion-record.json"

# A failed publish after the verified push keeps main promoted, leaves no
# partial package, and reports the failure as exit code 8 and in the record.
publish_failure_operator=$(make_fixture package-publish-failure pass)
publish_failure_remote="$test_root/package-publish-failure/remote.git"
publish_failure_evidence="$test_root/package-publish-failure/evidence"
publish_failure_releases="$test_root/package-publish-failure/releases"
publish_failure_sha=$(git --git-dir="$publish_failure_remote" rev-parse refs/heads/develop)
run_expect_rc 8 env FAKE_DOTNET_FAIL=1 FAKE_DOTNET_LOG="$test_root/package-publish-failure/dotnet.log" \
  PATH="$fake_dotnet_bin:$PATH" \
  "$publish_failure_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-package-publish-failure --evidence-dir "$publish_failure_evidence" \
  --release-dir "$publish_failure_releases" >/dev/null 2>&1
test "$(git --git-dir="$publish_failure_remote" rev-parse refs/heads/main)" = "$publish_failure_sha"
test "$(git --git-dir="$publish_failure_remote" rev-parse refs/tags/release/test-package-publish-failure^{})" = "$publish_failure_sha"
test -z "$(ls -A "$publish_failure_releases")"
grep -q 'fixture publish failed' "$publish_failure_evidence/release-publish.log"
grep -q '"status":"promoted"' "$publish_failure_evidence/promotion-record.json"
grep -q '"atomicPush":true' "$publish_failure_evidence/promotion-record.json"
grep -q '"releasePackage":{"status":"failed","directory":"[^"]*","error":"dotnet publish of the gated backend failed; see release-publish.log"}' \
  "$publish_failure_evidence/promotion-record.json"

# An occupied package directory is refused before the gate, so main never
# advances toward a package location that cannot be written.
occupied_operator=$(make_fixture package-occupied pass)
occupied_remote="$test_root/package-occupied/remote.git"
occupied_evidence="$test_root/package-occupied/evidence"
occupied_main=$(git --git-dir="$occupied_remote" rev-parse refs/heads/main)
occupied_sha=$(git --git-dir="$occupied_remote" rev-parse refs/heads/develop)
mkdir -p "$test_root/package-occupied/releases/$occupied_sha"
run_expect_rc 3 "$occupied_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-package-occupied --evidence-dir "$occupied_evidence" \
  --release-dir "$test_root/package-occupied/releases" >/dev/null 2>&1
test "$(git --git-dir="$occupied_remote" rev-parse refs/heads/main)" = "$occupied_main"
test ! -e "$occupied_evidence/full-gate.log"
grep -q '"status":"blocked-release-dir"' "$occupied_evidence/promotion-record.json"
printf '%s\n' 'release package failure paths passed'

# A red or nominally green but incomplete gate cannot advance either ref.
for gate_mode in fail incomplete; do
  operator=$(make_fixture "gate-$gate_mode" "$gate_mode")
  remote="$test_root/gate-$gate_mode/remote.git"
  evidence="$test_root/gate-$gate_mode/evidence"
  before=$(git --git-dir="$remote" rev-parse refs/heads/main)
  run_expect_rc 4 "$operator/scripts/release/promote-develop-to-main.sh" \
    --execute --tag "release/test-$gate_mode" --evidence-dir "$evidence" >/dev/null 2>&1
  test "$(git --git-dir="$remote" rev-parse refs/heads/main)" = "$before"
  ! git --git-dir="$remote" show-ref --verify --quiet "refs/tags/release/test-$gate_mode"
done

# A required capacity window that cannot be applied never runs the gate and
# leaves main unchanged with a distinct record status (AGT-2982).
window_operator=$(make_fixture window-required pass)
window_remote="$test_root/window-required/remote.git"
window_evidence="$test_root/window-required/evidence"
window_main=$(git --git-dir="$window_remote" rev-parse refs/heads/main)
run_expect_rc 4 env RELEASE_GATE_WINDOW=required \
  "$window_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-window-required --evidence-dir "$window_evidence" >/dev/null 2>&1
test "$(git --git-dir="$window_remote" rev-parse refs/heads/main)" = "$window_main"
! grep -Fxq 'PROMOTION_FULL_GATE=passed' "$window_evidence/full-gate.log"
grep -q '"status":"blocked-gate-window"' "$window_evidence/promotion-record.json"
grep -q '"gate":"not-run"' "$window_evidence/promotion-record.json"
grep -q '"gateWindow":{"mode":"failed"' "$window_evidence/promotion-record.json"

# A develop advance during the gate is informational. The exact candidate that
# started the gate is promoted, while the newer develop commit waits for the
# next train.
move_operator=$(make_fixture ref-moved move-develop)
move_remote="$test_root/ref-moved/remote.git"
move_evidence="$test_root/ref-moved/evidence"
move_main=$(git --git-dir="$move_remote" rev-parse refs/heads/main)
move_candidate=$(git --git-dir="$move_remote" rev-parse refs/heads/develop)
"$move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-ref-moved --evidence-dir "$move_evidence" >/dev/null
advanced_develop=$(git --git-dir="$move_remote" rev-parse refs/heads/develop)
test "$advanced_develop" != "$move_candidate"
test "$(git --git-dir="$move_remote" rev-parse refs/heads/main)" = "$move_candidate"
test "$(git --git-dir="$move_remote" rev-parse refs/tags/release/test-ref-moved^{})" = "$move_candidate"
git --git-dir="$move_remote" merge-base --is-ancestor "$move_main" "$move_candidate"
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$move_evidence/full-gate.log"
grep -Fq "develop advanced to $advanced_develop during gate; promoting gated candidate $move_candidate" \
  "$move_evidence/promotion.log"
grep -Fq "promoted develop=$move_candidate to main=$move_candidate with tag=release/test-ref-moved candidate=$move_candidate" \
  "$move_evidence/promotion.log"
grep -q '"status":"promoted"' "$move_evidence/promotion-record.json"

# A concurrent main advance that is not an ancestor of the gated candidate
# fails the final ancestry check. The external main commit remains untouched.
main_move_operator=$(make_fixture main-moved move-main)
main_move_remote="$test_root/main-moved/remote.git"
main_move_evidence="$test_root/main-moved/evidence"
main_move_candidate=$(git --git-dir="$main_move_remote" rev-parse refs/heads/develop)
run_expect_rc 5 "$main_move_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-main-moved --evidence-dir "$main_move_evidence" >/dev/null 2>&1
main_after_gate=$(git --git-dir="$main_move_remote" rev-parse refs/heads/main)
test "$main_after_gate" != "$main_move_candidate"
! git --git-dir="$main_move_remote" merge-base --is-ancestor "$main_after_gate" "$main_move_candidate"
! git --git-dir="$main_move_remote" show-ref --verify --quiet refs/tags/release/test-main-moved
grep -Fxq 'PROMOTION_FULL_GATE=passed' "$main_move_evidence/full-gate.log"
grep -q '"status":"blocked-non-fast-forward"' "$main_move_evidence/promotion-record.json"
grep -q '"gate":"passed"' "$main_move_evidence/promotion-record.json"

# A develop tip that is not a descendant of main is never gated or pushed.
diverged_operator=$(make_fixture diverged pass diverged)
diverged_remote="$test_root/diverged/remote.git"
diverged_evidence="$test_root/diverged/evidence"
diverged_main=$(git --git-dir="$diverged_remote" rev-parse refs/heads/main)
diverged_candidate=$(git --git-dir="$diverged_remote" rev-parse refs/heads/develop)
if git --git-dir="$diverged_remote" merge-base --is-ancestor "$diverged_main" "$diverged_candidate"; then
  printf '%s\n' 'Diverged fixture unexpectedly produced a fast-forward candidate.' >&2
  exit 1
fi
run_expect_rc 3 "$diverged_operator/scripts/release/promote-develop-to-main.sh" \
  --execute --tag release/test-diverged --evidence-dir "$diverged_evidence" >/dev/null 2>&1
test "$(git --git-dir="$diverged_remote" rev-parse refs/heads/main)" = "$diverged_main"
! git --git-dir="$diverged_remote" show-ref --verify --quiet refs/tags/release/test-diverged
grep -q '"status":"blocked-non-fast-forward"' "$diverged_evidence/promotion-record.json"
test ! -e "$diverged_evidence/full-gate.log"

if [[ -n ${PROMOTION_TEST_ARTIFACT_DIR:-} ]]; then
  mkdir -p "$PROMOTION_TEST_ARTIFACT_DIR"
  cp "$preview_evidence/promotion.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-dry-run.log"
  cp "$preview_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-dry-run-record.json"
  cp "$green_evidence/promotion.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-execute.log"
  cp "$green_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/local-bare-execute-record.json"
  cp "$identity_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/empty-home-execute-record.json"
  cp "$tag_failure_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-tag-failure-record.json"
  cp "$tag_failure_evidence/tag.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-tag-failure.log"
  cp "$push_failure_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-push-failure-record.json"
  cp "$push_failure_evidence/push.log" \
    "$PROMOTION_TEST_ARTIFACT_DIR/simulated-push-failure.log"
  cp "$package_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/release-package-execute-record.json"
  cp "$publish_failure_evidence/promotion-record.json" \
    "$PROMOTION_TEST_ARTIFACT_DIR/release-package-publish-failure-record.json"
fi

printf '%s\n' 'develop -> main promotion tests passed'
