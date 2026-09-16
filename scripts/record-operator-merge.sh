#!/usr/bin/env bash
# AGT-2817 - write the integration record for a card-scoped merge performed
# outside the pipeline.
#
# An operator card-scoped merge
# (`merge(AGT-nnnn): integrate reviewed delivery (operator card-scoped merge)`)
# and the salvage recipe both land real work on the integration branch without
# the pipeline's bookkeeping. The card is then integrated in Git and silent in
# its own record - which is how AGT-2706 and AGT-2736 ended up with a contained
# delivery and an `undefined` integration field.
#
# This is the recorded step of that recipe, not a manual afterthought: run it
# immediately after the merge, from the repository that carries the merge.
set -euo pipefail

api="${AGENT_STUDIO_API:-http://127.0.0.1:5030}"
task=""
merge=""
branch=""
project=""
actor="${USER:-operator}"

usage() {
  printf '%s\n' \
    "Usage: record-operator-merge.sh --task <AGT-nnnn> --merge <sha> [options]" \
    "" \
    "Options:" \
    "  --task <key>       Task key the merge integrated (required)" \
    "  --merge <sha>      Merge commit that carried the delivery (required)" \
    "  --branch <name>    Integration branch (default: the project's configured branch)" \
    "  --project <id>     PROJ-NNN, display name, or watch path" \
    "  --actor <name>     Who performed the merge (default: \$USER)" \
    "  --api <url>        Backend base URL (default: \$AGENT_STUDIO_API or http://127.0.0.1:5030)" \
    "  -h, --help         Show this help"
}

die() {
  printf 'record-operator-merge: %s\n' "$*" >&2
  exit 2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --task) task="${2:-}"; shift 2 ;;
    --merge) merge="${2:-}"; shift 2 ;;
    --branch) branch="${2:-}"; shift 2 ;;
    --project) project="${2:-}"; shift 2 ;;
    --actor) actor="${2:-}"; shift 2 ;;
    --api) api="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument '$1'" ;;
  esac
done

[ -n "$task" ] || die "--task is required"
[ -n "$merge" ] || die "--merge is required"
command -v git >/dev/null 2>&1 || die "git is not on PATH"
command -v curl >/dev/null 2>&1 || die "curl is not on PATH"
command -v python3 >/dev/null 2>&1 || die "python3 is not on PATH (used to encode the evidence text)"

merge_sha="$(git rev-parse --verify "${merge}^{commit}")" \
  || die "'$merge' is not a commit in this repository"
merge_subject="$(git log -1 --format=%s "$merge_sha")"
# Recorded in UTC: the API stores an instant, not the operator's wall clock.
merged_at="$(TZ=UTC git log -1 --date=format-local:%Y-%m-%dT%H:%M:%SZ --format=%cd "$merge_sha")"

# The delivery commits the merge brought in: everything reachable from the
# merge's second parent that its first parent did not already have. A
# fast-forward merge has no second parent, so the record names the commit
# itself.
if git rev-parse --verify --quiet "${merge_sha}^2" >/dev/null; then
  delivery_shas="$(git rev-list "${merge_sha}^1..${merge_sha}^2")"
else
  delivery_shas="$merge_sha"
fi
[ -n "$delivery_shas" ] || die "the merge $merge_sha introduced no commits"

sha_json="$(printf '%s\n' $delivery_shas | awk 'NF {printf "%s\"%s\"", sep, $0; sep=","}')"
key_lower="$(printf '%s' "$task" | tr '[:upper:]' '[:lower:]')"
evidence="Operator card-scoped merge $merge_sha ($merge_subject) by $actor at $merged_at."

payload="$(cat <<JSON
{
  "id": "operator-merge-${key_lower}-$(printf '%s' "$merge_sha" | cut -c1-9)",
  "classification": "integrated-verified",
  "acceptedAtUtc": "${merged_at}",
  $( [ -n "$branch" ] && printf '"integrationBranch": "%s",' "$branch" )
  "commitShas": [${sha_json}],
  "evidence": $(printf '%s' "$evidence" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')
}
JSON
)"

query=""
[ -n "$project" ] && query="?project=$(printf '%s' "$project" | sed 's/ /%20/g')"

printf 'record-operator-merge: %s <- %s\n' "$task" "$merge_sha"
curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  -H "X-Client-Id: ${actor}" \
  --data "$payload" \
  "${api}/api/tasks/${task}/integration-records${query}"
printf '\n'

# Containment, not the record, decides the card's verdict. Reconciling right
# after the write clears the `next-attempt` placeholder the shipped commits
# should no longer carry, and reports anything that still contradicts Git.
if [ -n "$project" ]; then
  printf 'record-operator-merge: reconciling %s\n' "$project"
  curl -fsS -X POST \
    -H "X-Client-Id: ${actor}" \
    "${api}/api/projects/${project}/delivery-claims/reconcile" \
    | python3 -c 'import json,sys; r=json.load(sys.stdin); print("scanned", r["scanned"], "classes", r["classes"], "findings", r["findings"])'
fi
