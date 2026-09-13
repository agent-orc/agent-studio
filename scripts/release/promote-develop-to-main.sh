#!/usr/bin/env bash
# Operator-owned, fail-closed develop -> main promotion train.
#
# This is not a worker-run Git step. Run it from a clean checkout whose HEAD is
# the fetched origin/develop tip. It fixes that exact commit as the candidate,
# runs the mandatory full gate in a temporary worktree, rechecks remote
# ancestry, then atomically pushes main and an annotated release marker.

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
  --prefer-develop-conflicts        Deprecated compatibility option; exact-SHA promotion never merges.
  --remote <name>                   Git remote (default: origin).
  -h, --help                        Show this help.

Environment:
  PROMOTION_TAGGER_NAME             Annotated tagger name (default: Agent Studio Promotion).
  PROMOTION_TAGGER_EMAIL            Annotated tagger email (default: promotion@agent-studio.invalid).

The execute path has no gate bypass. A non-fast-forward candidate, a red or
incomplete gate, a tag collision, or a non-atomic push leaves main unchanged.
EOF
}

mode=dry-run
remote=origin
required_ancestor=
tag_name=
evidence_dir=
prefer_develop_conflicts=0
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
    --prefer-develop-conflicts)
      prefer_develop_conflicts=1
      shift
      ;;
    --remote)
      (($# >= 2)) || { usage >&2; exit 2; }
      remote=$2
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

write_record() {
  local status=$1
  local candidate_sha=${2:-}
  local gate_status=${3:-not-run}
  local conflicts=${4:-}
  local pushed=${5:-false}
  local error=${6:-}
  local gate_blob
  gate_blob=$(git -C "$repo" hash-object "$gate_script")
  printf '{"schemaVersion":1,"status":"%s","mode":"%s","remote":"%s","developSha":"%s","previousMainSha":"%s","candidateSha":"%s","releaseTag":"%s","requiredAncestor":"%s","conflictPolicy":"%s","conflicts":"%s","gate":"%s","gateScriptBlob":"%s","atomicPush":%s,"error":"%s","createdAtUtc":"%s"}\n' \
    "$(json_escape "$status")" \
    "$(json_escape "$mode")" \
    "$(json_escape "$remote")" \
    "$(json_escape "$develop_sha")" \
    "$(json_escape "$main_sha")" \
    "$(json_escape "$candidate_sha")" \
    "$(json_escape "$tag_name")" \
    "$(json_escape "$required_ancestor")" \
    exact-sha \
    "$(json_escape "$conflicts")" \
    "$(json_escape "$gate_status")" \
    "$(json_escape "$gate_blob")" \
    "$pushed" \
    "$(json_escape "$error")" \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    > "$evidence_dir/promotion-record.json"
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

temporary_root=$(mktemp -d 2>/dev/null || mktemp -d -t develop-main-promotion)
candidate_checkout="$temporary_root/candidate"
worktree_added=0
cleanup() {
  if [[ "$worktree_added" == 1 ]]; then
    git -C "$repo" worktree remove --force "$candidate_checkout" >/dev/null 2>&1 || true
  fi
  rm -rf -- "$temporary_root"
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
"$gate_script" --repo "$candidate_checkout" 2>&1 \
  | tee "$evidence_dir/full-gate.log"
gate_rc=${PIPESTATUS[0]}
set -e
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
