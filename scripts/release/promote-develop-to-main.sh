#!/usr/bin/env bash
# Operator-owned, fail-closed develop -> main promotion train.
#
# This is not a worker-run Git step. Run it from a clean checkout whose HEAD is
# the fetched origin/develop tip. It fixes that exact commit as the candidate,
# runs the mandatory full gate in a temporary worktree, rechecks remote
# ancestry, then atomically pushes main and an annotated release marker. With
# --release-dir it then packages the release from the same gated worktree, so a
# deploy needs no second full gate for that SHA.

set -Eeuo pipefail

usage() {
  cat <<'EOF'
Usage:
  promote-develop-to-main.sh [--dry-run] [options]
  promote-develop-to-main.sh --execute [options]

Options:
  --dry-run                         Prepare and inspect the candidate; do not gate or push (default).
  --execute                         Run the full gate and atomically push main plus the release tag.
  --required-ancestor <sha>         Require this commit to be reachable from origin/develop.
  --tag <release/name>              Annotated marker name (default: release/<UTC timestamp>).
  --evidence-dir <directory>        Durable output directory for manifest, logs, and record.
  --release-dir <directory>         After a passed gate and a verified push, write the release
                                     package (release.tar.gz, release.sha256, full-gate.log) from
                                     the gate worktree into <directory>/<candidate sha>/.
                                     Omit to skip packaging.
  --deploy-token-file <path>        Release service credential for automatic stage queueing
                                     from the gated release package.
  --deploy-image-ref <image>        Registry tag to build and push from the gated checkout.
  --deploy-dockerfile <path>        Dockerfile within the gated checkout for that image.
  --deploy-artifact-store <dir>     Deprecated compatibility option; static bytes enter the built-in registry.
  --prefer-develop-conflicts        Deprecated compatibility option; exact-SHA promotion never merges.
  --remote <name>                   Git remote (default: origin).
  --project <name>                  Agent Studio project name. After a successful push, calls
                                     POST /api/git/branch-reclaim/promotion?project=<name> so branches
                                     for tasks now in main are reclaimed (AGT-2793). Omit to skip.
  -h, --help                        Show this help.

Environment:
  PROMOTION_TAGGER_NAME             Annotated tagger name (default: Agent Studio Promotion).
  PROMOTION_TAGGER_EMAIL            Annotated tagger email (default: promotion@agent-studio.invalid).
  ATP_API                           Backend base URL for --project (default: http://127.0.0.1:5031).
  DEPLOY_RELEASE_TOKEN_FILE         Default for --deploy-token-file.
  DEPLOY_ARTIFACT_STORE             Default for --deploy-artifact-store.
  RELEASE_GATE_*                    Gate capacity window and hot-host check around the full gate;
                                     see release-gate-window.sh --help (AGT-2982).

The execute path has no gate bypass. A non-fast-forward candidate, a red or
incomplete gate, a tag collision, or a non-atomic push leaves main unchanged.
EOF
}

mode=dry-run
remote=origin
required_ancestor=
tag_name=
evidence_dir=
release_root=
deploy_token_file="${DEPLOY_RELEASE_TOKEN_FILE:-}"
deploy_image_ref=
deploy_dockerfile=
deploy_artifact_store="${DEPLOY_ARTIFACT_STORE:-}"
deploy_header_file=
prefer_develop_conflicts=0
project=
api="${ATP_API:-http://127.0.0.1:5031}"
PROMOTION_TAGGER_NAME=${PROMOTION_TAGGER_NAME:-Agent Studio Promotion}
PROMOTION_TAGGER_EMAIL=${PROMOTION_TAGGER_EMAIL:-promotion@agent-studio.invalid}

while (($#)); do
  case "$1" in
    --dry-run)
      mode=dry-run
      shift
      ;;
    --execute)
      mode=execute
      shift
      ;;
    --required-ancestor)
      (($# >= 2)) || { usage >&2; exit 2; }
      required_ancestor=$2
      shift 2
      ;;
    --tag)
      (($# >= 2)) || { usage >&2; exit 2; }
      tag_name=$2
      shift 2
      ;;
    --evidence-dir)
      (($# >= 2)) || { usage >&2; exit 2; }
      evidence_dir=$2
      shift 2
      ;;
    --release-dir)
      (($# >= 2)) || { usage >&2; exit 2; }
      release_root=$2
      shift 2
      ;;
    --deploy-token-file)
      (($# >= 2)) || { usage >&2; exit 2; }
      deploy_token_file=$2
      shift 2
      ;;
    --deploy-image-ref)
      (($# >= 2)) || { usage >&2; exit 2; }
      deploy_image_ref=$2
      shift 2
      ;;
    --deploy-dockerfile)
      (($# >= 2)) || { usage >&2; exit 2; }
      deploy_dockerfile=$2
      shift 2
      ;;
    --deploy-artifact-store)
      (($# >= 2)) || { usage >&2; exit 2; }
      deploy_artifact_store=$2
      shift 2
      ;;
    --prefer-develop-conflicts)
      prefer_develop_conflicts=1
      shift
      ;;
    --remote)
      (($# >= 2)) || { usage >&2; exit 2; }
      remote=$2
      shift 2
      ;;
    --project)
      (($# >= 2)) || { usage >&2; exit 2; }
      project=$2
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'Unknown argument: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -n "$deploy_token_file" ]]; then
  [[ -n "$project" ]] || { printf '%s\n' 'Deploy handoff requires --project.' >&2; exit 2; }
  [[ -n "$release_root" ]] \
    || { printf '%s\n' 'Deploy handoff requires --release-dir.' >&2; exit 2; }
fi
if [[ -n "$deploy_image_ref" && -z "$deploy_token_file" ]]; then
  printf '%s\n' 'Deploy image handoff requires --deploy-token-file.' >&2
  exit 2
fi
if [[ -n "$deploy_image_ref" ]]; then
  [[ -n "$deploy_dockerfile" && "$deploy_dockerfile" != /* && "$deploy_dockerfile" != *..* ]] \
    || { printf '%s\n' 'Image handoff requires a relative --deploy-dockerfile without traversal.' >&2; exit 2; }
elif [[ -n "$deploy_dockerfile" ]]; then
  printf '%s\n' '--deploy-dockerfile requires --deploy-image-ref.' >&2
  exit 2
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(git -C "$script_dir" rev-parse --show-toplevel 2>/dev/null) || {
  printf 'Promotion command must run from a Git checkout.\n' >&2
  exit 2
}
gate_script="$script_dir/promotion-full-gate.sh"
[[ -x "$gate_script" ]] || {
  printf 'Mandatory full gate is missing or not executable: %s\n' "$gate_script" >&2
  exit 2
}
window_helper="$script_dir/release-gate-window.sh"
[[ -x "$window_helper" ]] || {
  printf 'Gate capacity window helper is missing or not executable: %s\n' "$window_helper" >&2
  exit 2
}
git -C "$repo" remote get-url "$remote" >/dev/null 2>&1 || {
  printf 'Git remote does not exist: %s\n' "$remote" >&2
  exit 2
}
if [[ -n $(git -C "$repo" status --porcelain --untracked-files=all) ]]; then
  printf 'Operator checkout must be clean before promotion: %s\n' "$repo" >&2
  exit 2
fi

git -C "$repo" fetch --prune --tags "$remote" \
  "+refs/heads/develop:refs/remotes/$remote/develop" \
  "+refs/heads/main:refs/remotes/$remote/main"

develop_ref="refs/remotes/$remote/develop"
main_ref="refs/remotes/$remote/main"
develop_sha=$(git -C "$repo" rev-parse "$develop_ref^{commit}")
candidate_sha=$develop_sha
main_sha=$(git -C "$repo" rev-parse "$main_ref^{commit}")
operator_sha=$(git -C "$repo" rev-parse HEAD)

if [[ "$operator_sha" != "$develop_sha" ]]; then
  printf 'Operator checkout HEAD must equal %s/develop. HEAD=%s develop=%s\n' \
    "$remote" "$operator_sha" "$develop_sha" >&2
  exit 2
fi
if [[ -n "$required_ancestor" ]]; then
  required_ancestor=$(git -C "$repo" rev-parse "$required_ancestor^{commit}") || {
    printf 'Required ancestor cannot be resolved: %s\n' "$required_ancestor" >&2
    exit 2
  }
  git -C "$repo" merge-base --is-ancestor "$required_ancestor" "$develop_sha" || {
    printf 'Required ancestor %s is not reachable from %s/develop %s.\n' \
      "$required_ancestor" "$remote" "$develop_sha" >&2
    exit 3
  }
fi

timestamp=$(date -u +%Y%m%d-%H%M%SZ)
[[ -n "$tag_name" ]] || tag_name="release/$timestamp"
[[ "$tag_name" == release/* ]] || {
  printf 'Release marker must use the release/ namespace: %s\n' "$tag_name" >&2
  exit 2
}
git check-ref-format "refs/tags/$tag_name" >/dev/null || {
  printf 'Invalid release tag: %s\n' "$tag_name" >&2
  exit 2
}

if [[ -z "$evidence_dir" ]]; then
  evidence_root=${PROMOTION_EVIDENCE_DIR:-${JOB_RESULTS_DIR:-$(git -C "$repo" rev-parse --git-path promotion-results)}}
  evidence_dir="$evidence_root/${tag_name#release/}"
fi
mkdir -p "$evidence_dir"
evidence_dir=$(cd "$evidence_dir" && pwd)

log() {
  printf '[develop-main-promotion] %s\n' "$*" | tee -a "$evidence_dir/promotion.log"
}

json_escape() {
  printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e ':a;N;$!ba;s/\n/\\n/g'
}

gate_window_record="$evidence_dir/gate-window.env"
rm -f -- "$gate_window_record"

# Reads one key from the window helper's record; later lines win.
gate_window_value() {
  [[ -f "$gate_window_record" ]] || return 0
  awk -v key="$1" 'index($0, key "=") == 1 { value = substr($0, length(key) + 2) } END { print value }' \
    "$gate_window_record"
}

json_number_or_null() {
  if [[ "$1" =~ ^[0-9]+([.][0-9]+)?$ ]]; then printf '%s' "$1"; else printf 'null'; fi
}

# Load, duration, and applied-quota evidence so a slow or failed gate can be
# attributed to host load (AGT-2982). Every field is null before the gate ran.
gate_window_json() {
  local unit recorded applied quotas= separator=
  for unit in agent-runner.service agent-runner-review.service; do
    recorded=$(gate_window_value "recorded:$unit")
    [[ -n "$recorded" ]] || continue
    applied=$(gate_window_value "applied:$unit")
    quotas+=$(printf '%s"%s":{"recorded":"%s","applied":%s}' "$separator" "$unit" \
      "$(json_escape "$recorded")" \
      "$([[ -n "$applied" ]] && printf '"%s"' "$(json_escape "$applied")" || printf null)")
    separator=,
  done
  printf ',"hostLoadAtGateStart":%s,"hostLoadAtGateEnd":%s,"gateDurationSeconds":%s,"appliedQuotas":{%s},"gateWindow":{"mode":"%s","cpuCount":%s,"reservedCores":%s,"loadThreshold":%s,"loadBeforeWait":%s,"loadWaitSeconds":%s,"quotasRestored":"%s","gateExit":"%s"}' \
    "$(json_number_or_null "$(gate_window_value load-at-gate-start)")" \
    "$(json_number_or_null "$(gate_window_value load-at-gate-end)")" \
    "$(json_number_or_null "$(gate_window_value gate-duration-seconds)")" \
    "$quotas" \
    "$(json_escape "$(gate_window_value window-mode)")" \
    "$(json_number_or_null "$(gate_window_value cpu-count)")" \
    "$(json_number_or_null "$(gate_window_value reserved-cores)")" \
    "$(json_number_or_null "$(gate_window_value load-threshold)")" \
    "$(json_number_or_null "$(gate_window_value load-before-window-wait)")" \
    "$(json_number_or_null "$(gate_window_value load-wait-seconds)")" \
    "$(json_escape "$(gate_window_value quotas-restored)")" \
    "$(json_escape "$(gate_window_value gate-exit)")"
}

write_record() {
  local status=$1
  local candidate_sha=${2:-}
  local gate_status=${3:-not-run}
  local conflicts=${4:-}
  local pushed=${5:-false}
  local error=${6:-}
  local gate_blob
  gate_blob=$(git -C "$repo" hash-object "$gate_script")
  printf '{"schemaVersion":1,"status":"%s","mode":"%s","remote":"%s","developSha":"%s","previousMainSha":"%s","candidateSha":"%s","releaseTag":"%s","gateRunId":"%s","requiredAncestor":"%s","conflictPolicy":"%s","conflicts":"%s","gate":"%s","gateScriptBlob":"%s","atomicPush":%s,"error":"%s","releasePackage":%s%s,"createdAtUtc":"%s"}\n' \
    "$(json_escape "$status")" \
    "$(json_escape "$mode")" \
    "$(json_escape "$remote")" \
    "$(json_escape "$develop_sha")" \
    "$(json_escape "$main_sha")" \
    "$(json_escape "$candidate_sha")" \
    "$(json_escape "$tag_name")" \
    "$(json_escape "$tag_name")" \
    "$(json_escape "$required_ancestor")" \
    exact-sha \
    "$(json_escape "$conflicts")" \
    "$(json_escape "$gate_status")" \
    "$(json_escape "$gate_blob")" \
    "$pushed" \
    "$(json_escape "$error")" \
    "$release_package_json" \
    "$(gate_window_json)" \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    > "$evidence_dir/promotion-record.json"
}

# null unless --release-dir was given and packaging was attempted after a
# verified push; then the package directory, archive path, and its sha256.
release_package_json=null
release_target=
release_staging=
release_error=

release_package_fail() {
  release_error=$1
  return 1
}

# Builds the deploy package from the gated worktree in a staging directory next
# to the target and renames it into place only when complete, so a failure
# leaves no package. The layout is the one fetch-remote-artifacts.sh validates:
# only backend/ and frontend/ in release.tar.gz, both with RELEASE-SHA and
# FULL-GATE, no links or special files.
write_release_package() {
  local browser_root="$candidate_checkout/frontend/dist/frontend/browser"
  local licenses="$candidate_checkout/frontend/dist/frontend/3rdpartylicenses.txt"
  local component required special digest
  [[ ! -e "$release_target" ]] \
    || release_package_fail "release package directory already exists: $release_target" || return
  release_staging=$(mktemp -d "$release_root/.${candidate_sha}.partial.XXXXXX") \
    || release_package_fail "cannot create a staging directory in $release_root" || return
  mkdir "$release_staging/backend" "$release_staging/frontend" \
    || release_package_fail 'cannot create the package component directories' || return
  (cd "$candidate_checkout" && dotnet publish backend/OrchestratorApi.csproj \
    --configuration Release --no-build --no-restore --nologo \
    --output "$release_staging/backend") > "$evidence_dir/release-publish.log" 2>&1 \
    || release_package_fail 'dotnet publish of the gated backend failed; see release-publish.log' || return
  for required in OrchestratorApi.dll OrchestratorApi.deps.json OrchestratorApi.runtimeconfig.json; do
    [[ -s "$release_staging/backend/$required" ]] \
      || release_package_fail "published backend is missing $required" || return
  done
  [[ -s "$browser_root/index.html" ]] \
    || release_package_fail "gated frontend build is missing: $browser_root/index.html" || return
  cp -a "$browser_root/." "$release_staging/frontend/" \
    || release_package_fail 'cannot copy the gated frontend build' || return
  if [[ -f "$licenses" ]]; then
    cp "$licenses" "$release_staging/frontend/" \
      || release_package_fail 'cannot copy 3rdpartylicenses.txt' || return
  fi
  special=$(find "$release_staging/backend" "$release_staging/frontend" ! -type f ! -type d -print -quit)
  [[ -z "$special" ]] \
    || release_package_fail "package contains a link or special file: ${special#"$release_staging"/}" || return
  for component in backend frontend; do
    printf '%s\n' "$candidate_sha" > "$release_staging/$component/RELEASE-SHA" \
      && printf 'PROMOTION_FULL_GATE=passed:%s\n' "$candidate_sha" > "$release_staging/$component/FULL-GATE" \
      || release_package_fail "cannot write the $component markers" || return
  done
  cp "$evidence_dir/full-gate.log" "$release_staging/full-gate.log" \
    || release_package_fail 'cannot copy full-gate.log' || return
  (cd "$release_staging" && tar -czf release.tar.gz backend frontend) \
    || release_package_fail 'cannot pack release.tar.gz' || return
  digest=$(cd "$release_staging" && sha256sum release.tar.gz | cut -d ' ' -f1) \
    && [[ "$digest" =~ ^[0-9a-f]{64}$ ]] \
    || release_package_fail 'cannot compute the release.tar.gz sha256' || return
  printf '%s\n' "$digest" > "$release_staging/release.sha256" \
    || release_package_fail 'cannot write release.sha256' || return
  [[ ! -e "$release_target" ]] && mv -T -- "$release_staging" "$release_target" \
    || release_package_fail "cannot move the package into place: $release_target" || return
  release_staging=
  release_package_json=$(printf '{"status":"written","directory":"%s","archive":"%s","sha256":"%s"}' \
    "$(json_escape "$release_target")" \
    "$(json_escape "$release_target/release.tar.gz")" \
    "$digest")
}

merge_base=$(git -C "$repo" merge-base "$main_sha" "$develop_sha")
{
  printf 'release-tag\t%s\n' "$tag_name"
  printf 'merge-base\t%s\n' "$merge_base"
  printf 'previous-main\t%s\n' "$main_sha"
  printf 'develop\t%s\n' "$develop_sha"
  printf 'candidate\t%s\n' "$candidate_sha"
  printf 'commits\t%s\n' "$(git -C "$repo" rev-list --count "$merge_base..$develop_sha")"
  printf 'main-only\t%s\n' "$(git -C "$repo" rev-list --count "$develop_sha..$main_sha")"
} > "$evidence_dir/manifest-summary.tsv"
git -C "$repo" log --reverse --date=iso-strict \
  --format='%H%x09%ad%x09%an%x09%s' "$merge_base..$develop_sha" \
  > "$evidence_dir/manifest-commits.tsv"
git -C "$repo" diff --stat "$merge_base..$develop_sha" \
  > "$evidence_dir/manifest-diff-stat.txt"
git -C "$repo" log --cherry-mark --right-only --format='%m %H %s' \
  "$develop_sha...$main_sha" > "$evidence_dir/main-only-patch-review.txt"

log "mode=$mode remote=$remote develop=$develop_sha candidate=$candidate_sha main=$main_sha"
log "manifest=$(wc -l < "$evidence_dir/manifest-commits.tsv" | tr -d ' ') commits merge-base=$merge_base"

if git -C "$repo" merge-base --is-ancestor "$candidate_sha" "$main_sha"; then
  log 'main already contains develop; promotion is a no-op'
  write_record no-op "$candidate_sha" not-required '' false
  exit 0
fi

if ! git -C "$repo" merge-base --is-ancestor "$main_sha" "$candidate_sha"; then
  log "candidate=$candidate_sha is not a descendant of main=$main_sha; main remains unchanged"
  write_record blocked-non-fast-forward "$candidate_sha" not-run '' false
  exit 3
fi

if [[ "$prefer_develop_conflicts" == 1 ]]; then
  log '--prefer-develop-conflicts is ignored because exact-SHA promotion does not create a merge commit'
fi

if git -C "$repo" show-ref --verify --quiet "refs/tags/$tag_name" \
  || [[ -n $(git -C "$repo" ls-remote --tags "$remote" "refs/tags/$tag_name") ]]; then
  log "release tag already exists: $tag_name"
  write_record blocked-tag-collision '' not-run '' false
  exit 3
fi

if [[ -n "$release_root" ]]; then
  if [[ "$mode" == dry-run ]]; then
    log "--release-dir is ignored in dry-run; an --execute run would package into $release_root/$candidate_sha"
  else
    # Checked before the gate so a long gate never ends in an unwritable or
    # already occupied package location.
    if ! mkdir -p "$release_root" 2>/dev/null || [[ ! -w "$release_root" ]]; then
      log "release directory is not writable: $release_root; main remains unchanged"
      write_record blocked-release-dir "$candidate_sha" not-run '' false "release directory is not writable: $release_root"
      exit 2
    fi
    release_root=$(cd "$release_root" && pwd)
    release_target="$release_root/$candidate_sha"
    if [[ -e "$release_target" ]]; then
      log "release package directory already exists: $release_target; main remains unchanged"
      write_record blocked-release-dir "$candidate_sha" not-run '' false "release package directory already exists: $release_target"
      exit 3
    fi
  fi
fi

temporary_root=$(mktemp -d 2>/dev/null || mktemp -d -t develop-main-promotion)
candidate_checkout="$temporary_root/candidate"
worktree_added=0
cleanup() {
  if [[ "$worktree_added" == 1 ]]; then
    git -C "$repo" worktree remove --force "$candidate_checkout" >/dev/null 2>&1 || true
  fi
  rm -rf -- "$temporary_root"
  if [[ -n "$release_staging" ]]; then
    rm -rf -- "$release_staging"
  fi
  if [[ -n "$deploy_header_file" ]]; then
    rm -f -- "$deploy_header_file"
  fi
}
trap cleanup EXIT HUP INT TERM

conflict_text=

git -C "$repo" worktree add --detach "$candidate_checkout" "$candidate_sha" \
  > "$evidence_dir/worktree-add.log" 2>&1
worktree_added=1

if ! git -C "$candidate_checkout" diff --check "$main_sha..$candidate_sha" \
  > "$evidence_dir/candidate-whitespace-review.txt" 2>&1; then
  log 'candidate contains committed whitespace findings; recorded for review while the mandatory full gate remains authoritative'
fi
git -C "$candidate_checkout" show --no-patch --format=fuller HEAD \
  > "$evidence_dir/candidate-commit.txt"
git -C "$candidate_checkout" diff --stat "$main_sha..$candidate_sha" \
  > "$evidence_dir/candidate-diff-stat.txt"
log "candidate=$candidate_sha"

if [[ "$mode" == dry-run ]]; then
  log 'dry-run complete; full gate and push were not run'
  write_record preview "$candidate_sha" not-run "$conflict_text" false
  exit 0
fi

set +e
"$window_helper" --record "$gate_window_record" -- \
  "$gate_script" --repo "$candidate_checkout" 2>&1 \
  | tee "$evidence_dir/full-gate.log"
gate_rc=${PIPESTATUS[0]}
set -e
log "gate window=$(gate_window_value window-mode) load start=$(gate_window_value load-at-gate-start) end=$(gate_window_value load-at-gate-end) duration=$(gate_window_value gate-duration-seconds)s"
if [[ "$(gate_window_value quotas-restored)" == failed ]]; then
  log 'WARNING: runner unit CPUQuota restore failed after the gate; see full-gate.log and restore the recorded values by hand'
fi
if [[ "$(gate_window_value window-mode)" == failed ]]; then
  log 'reserved gate capacity could not be applied (RELEASE_GATE_WINDOW=required); the gate did not run and main remains unchanged'
  write_record blocked-gate-window "$candidate_sha" not-run "$conflict_text" false
  exit 4
fi
if ((gate_rc != 0)); then
  log "mandatory full gate failed with exit code $gate_rc; main remains unchanged"
  write_record blocked-gate "$candidate_sha" failed "$conflict_text" false
  exit 4
fi
if ! grep -Fxq 'PROMOTION_FULL_GATE=passed' "$evidence_dir/full-gate.log"; then
  log 'mandatory full gate returned zero without its completion marker; main remains unchanged'
  write_record blocked-incomplete-gate "$candidate_sha" incomplete "$conflict_text" false
  exit 4
fi
if [[ -n $(git -C "$candidate_checkout" status --porcelain --untracked-files=all) ]]; then
  git -C "$candidate_checkout" status --short > "$evidence_dir/post-gate-dirty.txt"
  log 'candidate checkout became dirty during the gate; main remains unchanged'
  write_record blocked-dirty-gate "$candidate_sha" dirty "$conflict_text" false
  exit 4
fi
gated_head=$(git -C "$candidate_checkout" rev-parse HEAD)
if [[ "$gated_head" != "$candidate_sha" ]]; then
  log "candidate checkout moved during the gate: expected=$candidate_sha actual=$gated_head; main remains unchanged"
  write_record blocked-gate-subject "$candidate_sha" invalid-subject "$conflict_text" false
  exit 4
fi

git -C "$repo" fetch "$remote" \
  "+refs/heads/develop:refs/remotes/$remote/develop" \
  "+refs/heads/main:refs/remotes/$remote/main"
develop_after=$(git -C "$repo" rev-parse "$develop_ref^{commit}")
main_after=$(git -C "$repo" rev-parse "$main_ref^{commit}")
if ! git -C "$repo" merge-base --is-ancestor "$main_after" "$candidate_sha"; then
  log "gated candidate=$candidate_sha is not a descendant of current main=$main_after; main remains unchanged"
  write_record blocked-non-fast-forward "$candidate_sha" passed "$conflict_text" false
  exit 5
fi
if [[ "$develop_after" != "$develop_sha" ]]; then
  log "develop advanced to $develop_after during gate; promoting gated candidate $candidate_sha"
fi
if [[ "$main_after" != "$main_sha" ]]; then
  log "main advanced to $main_after during gate and remains an ancestor of candidate $candidate_sha"
fi

tag_message="Agent Studio develop -> main promotion

Develop: $develop_sha
Previous main: $main_sha
Candidate: $candidate_sha
Full gate: passed
Gate script blob: $(git -C "$repo" hash-object "$gate_script")
Candidate policy: exact develop SHA"
set +e
git -C "$candidate_checkout" \
  -c user.name="$PROMOTION_TAGGER_NAME" \
  -c user.email="$PROMOTION_TAGGER_EMAIL" \
  -c tag.gpgSign=false \
  tag -a "$tag_name" -m "$tag_message" "$candidate_sha" \
  > "$evidence_dir/tag.log" 2>&1
tag_rc=$?
set -e
if ((tag_rc != 0)); then
  tag_error="annotated tag creation failed with exit code $tag_rc"
  if [[ -s "$evidence_dir/tag.log" ]]; then
    tag_error+=": $(< "$evidence_dir/tag.log")"
  fi
  log "$tag_error; main remains unchanged"
  write_record blocked-tag "$candidate_sha" passed "$conflict_text" false "$tag_error"
  exit 6
fi

set +e
git -C "$candidate_checkout" push --atomic "$remote" \
  "$candidate_sha:refs/heads/main" "refs/tags/$tag_name:refs/tags/$tag_name" \
  > "$evidence_dir/push.log" 2>&1
push_rc=$?
set -e
if ((push_rc != 0)); then
  push_error="atomic main/tag push failed with exit code $push_rc"
  if [[ -s "$evidence_dir/push.log" ]]; then
    push_error+=": $(< "$evidence_dir/push.log")"
  fi
  log "$push_error; remote main and tag remain unchanged"
  write_record blocked-push "$candidate_sha" passed "$conflict_text" false "$push_error"
  exit 6
fi

remote_main=$(git -C "$candidate_checkout" ls-remote "$remote" refs/heads/main | awk 'NR == 1 { print $1 }')
remote_tag=$(git -C "$candidate_checkout" ls-remote "$remote" "refs/tags/$tag_name^{}" | awk 'NR == 1 { print $1 }')
if [[ "$remote_main" != "$candidate_sha" || "$remote_tag" != "$candidate_sha" ]]; then
  log "remote verification failed: main=${remote_main:-missing} tag=${remote_tag:-missing}"
  write_record pushed-verification-failed "$candidate_sha" passed "$conflict_text" true
  exit 7
fi

write_record promoted "$candidate_sha" passed "$conflict_text" true
log "promoted develop=$develop_sha to main=$candidate_sha with tag=$tag_name candidate=$candidate_sha"
log 'deployment handoff is ready: the main-advance cron watcher can deploy this main SHA'

# One-gate deploy package: main and the tag are already pushed and verified, so
# a packaging failure never touches them. It stays visible as exit code 8 and a
# failed releasePackage in the record.
release_rc=0
if [[ -n "$release_target" ]]; then
  if write_release_package; then
    write_record promoted "$candidate_sha" passed "$conflict_text" true
    log "release package written: $release_target/release.tar.gz sha256=$(< "$release_target/release.sha256")"
  else
    release_rc=8
    if [[ -n "$release_staging" ]]; then
      rm -rf -- "$release_staging"
      release_staging=
    fi
    release_package_json=$(printf '{"status":"failed","directory":"%s","error":"%s"}' \
      "$(json_escape "$release_target")" "$(json_escape "$release_error")")
    write_record promoted "$candidate_sha" passed "$conflict_text" true
    log "WARNING: release package was not written: $release_error; main and $tag_name stay promoted, deploy needs a separate package build"
  fi
fi

# The server reads this package and promotion record from its configured
# Deploy:ReleaseEvidenceRoot. The release credential cannot create evidence.
if [[ "$release_rc" == 0 && -n "$project" && -n "$deploy_token_file" && ( -n "$release_target" || -n "$deploy_image_ref" ) ]]; then
  [[ -r "$deploy_token_file" ]] || { log 'deploy service credential is unreadable'; exit 9; }
  deploy_digest="$(< "$release_target/release.sha256")"
  if [[ -n "$deploy_image_ref" ]]; then
    [[ -f "$candidate_checkout/$deploy_dockerfile" ]] \
      || { log 'deploy Dockerfile is absent from gated checkout'; exit 9; }
    deploy_metadata="$evidence_dir/deploy-image-metadata.json"
    docker buildx build --push --file "$candidate_checkout/$deploy_dockerfile" \
      --tag "$deploy_image_ref" --build-arg "SHA=$candidate_sha" \
      --metadata-file "$deploy_metadata" "$candidate_checkout" \
      || { log 'deploy image build or push failed'; exit 9; }
    image_digest=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["containerimage.digest"])' "$deploy_metadata") \
      || { log 'deploy image metadata has no digest'; exit 9; }
    [[ "$image_digest" =~ ^sha256:[0-9a-f]{64}$ ]] \
      || { log 'deploy image metadata has an invalid digest'; exit 9; }
    deploy_artifact="${deploy_image_ref%@*}@$image_digest"
  else
    deploy_artifact="release-package@sha256:$deploy_digest"
  fi
  printf '{"project":"%s","commit":"%s","artifact":"%s","sourceSha256":"%s"}\n' \
    "$(json_escape "$project")" "$candidate_sha" "$(json_escape "$deploy_artifact")" "$deploy_digest" \
    > "$release_target/deploy-artifact.json"
  cp "$evidence_dir/promotion-record.json" "$release_target/promotion-record.json"
  deploy_body=$(printf '{"project":"%s","artifact":"%s","commit":"%s"}' \
    "$(json_escape "$project")" "$deploy_artifact" "$candidate_sha")
  deploy_header_file="$(mktemp)"
  chmod 0600 "$deploy_header_file"
  printf 'Authorization: Bearer %s\n' "$(< "$deploy_token_file")" > "$deploy_header_file"
  deploy_http_code=$(curl -sS -o "$evidence_dir/deploy-queue-response.json" -w '%{http_code}' \
    --max-time 30 -X POST -H 'Content-Type: application/json' \
    -H "@$deploy_header_file" \
    --data "$deploy_body" "$api/api/deploy/gate-releases") || { log 'stage queue request failed'; exit 9; }
  # Main is already pushed here. A 409 with "reason":"superseded" means a
  # later commit already has a release (BD-1 release order), so this release is
  # superseded: log it and continue. Any other refusal exits 9.
  if [[ "$deploy_http_code" == 409 ]] \
    && grep -Eq '"reason"[[:space:]]*:[[:space:]]*"superseded"' "$evidence_dir/deploy-queue-response.json"; then
    log "stage queue skipped: release of commit=$candidate_sha is superseded by a newer release (HTTP 409)"
  else
    [[ "$deploy_http_code" == 202 ]] || { log "stage queue request returned HTTP $deploy_http_code"; exit 9; }
    log "stage queued for project=$project commit=$candidate_sha artifact=$deploy_artifact"
  fi
fi

# Branch reclaim (AGT-2793): task/runner/delivery/results/salvage refs whose
# tip is now reachable from main become eligible for deletion once main has
# actually moved. Best-effort and non-fatal: promotion already succeeded and
# pushed above, so a reclaim failure here only means those refs are picked up
# by the next periodic sweep instead.
if [[ -n "$project" ]]; then
  set +e
  api_auth_args=()
  if [[ -n "${AGENT_STUDIO_API_TOKEN:-}" ]]; then
    api_auth_args=(-H "Authorization: Bearer $AGENT_STUDIO_API_TOKEN")
  fi
  reclaim_http_code=$(curl -s -o "$evidence_dir/branch-reclaim-response.json" -w '%{http_code}' \
    -H 'X-Client-Id: local-default' \
    "${api_auth_args[@]}" \
    --max-time 30 -X POST -G --data-urlencode "project=$project" \
    "$api/api/git/branch-reclaim/promotion" 2>>"$evidence_dir/branch-reclaim.log")
  reclaim_rc=$?
  set -e
  if ((reclaim_rc != 0)) || [[ "$reclaim_http_code" != "200" ]]; then
    log "branch reclaim after promotion did not confirm (curl rc=$reclaim_rc http=$reclaim_http_code); the periodic sweep will retry"
  else
    log "branch reclaim after promotion requested for project=$project"
  fi
else
  log 'branch reclaim after promotion skipped: no --project given'
fi

exit "$release_rc"
