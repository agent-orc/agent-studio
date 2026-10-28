#!/usr/bin/env bash
# Promote a complete staged image set. Restore every touched tag if any copy fails.
set -euo pipefail

version="${1:?version required}"
sha="${2:?commit SHA required}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ && "$sha" =~ ^[0-9a-f]{40}$ ]] || {
  echo 'Expected a semantic version and a full lowercase commit SHA.' >&2
  exit 2
}

names=(agent-studio-api agent-studio-web agent-task-server agent-orchestrator-engine
  agent-studio-bff agent-host agent-operations-server agent-operations-agent agent-work)
declare -A staged=() previous=()
declare -a touched=()
promoting=0

rollback() {
  local status=$?
  trap - EXIT
  if (( status != 0 && promoting )); then
    echo "Image promotion failed; restoring ${#touched[@]} touched tags." >&2
    local ref old tags restored=0
    for ((index=${#touched[@]}-1; index>=0; index--)); do
      ref="${touched[$index]}"
      old="${previous[$ref]}"
      if [[ -n "$old" ]]; then
        if ! docker buildx imagetools create --prefer-index=false -t "$ref" "${ref%:*}@$old"; then
          echo "Could not restore $ref to $old." >&2
          restored=1
        elif [[ "$(regctl image digest "$ref")" != "$old" ]]; then
          echo "Restored digest does not match $old for $ref." >&2
          restored=1
        fi
      else
        if ! tags="$(regctl tag ls "${ref%:*}")"; then
          echo "Could not list tags during rollback for $ref." >&2
          restored=1
        elif grep -Fxq "${ref##*:}" <<< "$tags"; then
          if ! regctl tag rm "$ref"; then
            echo "Could not remove newly created tag $ref." >&2
            restored=1
          elif ! tags="$(regctl tag ls "${ref%:*}")"; then
            echo "Could not verify removed tag $ref." >&2
            restored=1
          elif grep -Fxq "${ref##*:}" <<< "$tags"; then
            echo "Removed tag $ref is still present." >&2
            restored=1
          fi
        fi
      fi
    done
    if (( restored )); then
      echo 'Rollback was incomplete; operator repair is required.' >&2
    fi
  fi
  exit "$status"
}
trap rollback EXIT
trap 'exit 130' INT TERM

# Capture every old digest before changing the first tag. A failed lookup aborts
# promotion; absence is determined from the registry tag list, never inferred
# from a failed digest request.
for name in "${names[@]}"; do
  image="ghcr.io/agent-orc/$name"
  staged["$image"]="$(regctl image digest "$image:sha-${sha:0:7}")"
  [[ "${staged[$image]}" =~ ^sha256:[0-9a-f]{64}$ ]] || {
    echo "Invalid staged digest for $image." >&2
    exit 1
  }
  tags="$(regctl tag ls "$image")"
  for tag in "v$version" latest; do
    ref="$image:$tag"
    previous["$ref"]=''
    if grep -Fxq "$tag" <<< "$tags"; then
      previous["$ref"]="$(regctl image digest "$ref")"
      [[ "${previous[$ref]}" =~ ^sha256:[0-9a-f]{64}$ ]] || {
        echo "Invalid previous digest for $ref." >&2
        exit 1
      }
    fi
  done
done

promoting=1
for name in "${names[@]}"; do
  image="ghcr.io/agent-orc/$name"
  for tag in "v$version" latest; do
    ref="$image:$tag"
    [[ "${previous[$ref]}" != "${staged[$image]}" ]] || continue
    # Track before copying: a command can move a tag and still report failure.
    touched+=("$ref")
    docker buildx imagetools create --prefer-index=false -t "$ref" "$image@${staged[$image]}"
    actual="$(regctl image digest "$ref")"
    [[ "$actual" == "${staged[$image]}" ]] || {
      echo "Promoted digest mismatch for $ref." >&2
      exit 1
    }
  done
done
promoting=0
echo 'All eight image version and latest tags promoted.'
