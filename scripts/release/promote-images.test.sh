#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
mkdir -p "$test_root/bin" "$test_root/state"
export FAKE_REGISTRY="$test_root/state"
export FAKE_DOCKER_CALLS="$test_root/calls"
old_digest="sha256:$(printf 'b%.0s' {1..64})"
new_digest="sha256:$(printf 'a%.0s' {1..64})"
sha="$(printf '1%.0s' {1..40})"
names=(agent-studio-api agent-studio-web agent-task-server agent-orchestrator-engine
  agent-studio-bff agent-host agent-operations-server agent-operations-agent)

cat > "$test_root/bin/regctl" <<'FAKE_REGCTL'
#!/usr/bin/env bash
set -euo pipefail
key() { printf '%s' "${1//\//_}" | tr ':' '_'; }
case "$1 $2" in
  'image digest') cat "$FAKE_REGISTRY/$(key "$3")" ;;
  'tag ls')
    prefix="$(key "$3")_"
    for file in "$FAKE_REGISTRY/$prefix"*; do
      [[ -e "$file" ]] || continue
      printf '%s\n' "${file##*${prefix}}"
    done ;;
  'tag rm') rm "$FAKE_REGISTRY/$(key "$3")" ;;
  *) exit 2 ;;
esac
FAKE_REGCTL
cat > "$test_root/bin/docker" <<'FAKE_DOCKER'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1 $2 $3" == 'buildx imagetools create' ]] || exit 2
[[ "$4" == '--prefer-index=false' && "$5" == '-t' ]] || exit 2
ref="$6"
source="$7"
count=0
[[ ! -f "$FAKE_DOCKER_CALLS" ]] || count="$(cat "$FAKE_DOCKER_CALLS")"
count=$((count + 1))
printf '%s\n' "$count" > "$FAKE_DOCKER_CALLS"
source_image="${source%@*}"
source_digest="${source#*@}"
[[ "$source_image" == "${ref%:*}" ]] || exit 2
printf '%s\n' "$source_digest" > "$FAKE_REGISTRY/$(printf '%s' "${ref//\//_}" | tr ':' '_')"
if [[ "$count" == "${FAKE_FAIL_CALL:-0}" ]]; then exit 71; fi
FAKE_DOCKER
chmod +x "$test_root/bin/regctl" "$test_root/bin/docker"
export PATH="$test_root/bin:$PATH"

seed() {
  rm -f "$FAKE_REGISTRY"/* "$FAKE_DOCKER_CALLS"
  for name in "${names[@]}"; do
    prefix="ghcr.io_agent-orc_${name}"
    printf '%s\n' "$new_digest" > "$FAKE_REGISTRY/${prefix}_sha-${sha:0:7}"
    printf '%s\n' "$old_digest" > "$FAKE_REGISTRY/${prefix}_latest"
  done
}

seed
if FAKE_FAIL_CALL=4 "$repo_root/scripts/release/promote-images.sh" 0.9.5 "$sha" \
  > "$test_root/failure.log" 2>&1; then
  echo 'Expected a failed promotion.' >&2
  exit 1
fi
grep -Fq 'restoring 4 touched tags' "$test_root/failure.log"
for name in "${names[@]}"; do
  prefix="ghcr.io_agent-orc_${name}"
  [[ ! -e "$FAKE_REGISTRY/${prefix}_v0.9.5" ]]
  [[ "$(cat "$FAKE_REGISTRY/${prefix}_latest")" == "$old_digest" ]]
done

seed
"$repo_root/scripts/release/promote-images.sh" 0.9.5 "$sha" > "$test_root/success.log"
for name in "${names[@]}"; do
  prefix="ghcr.io_agent-orc_${name}"
  [[ "$(cat "$FAKE_REGISTRY/${prefix}_v0.9.5")" == "$new_digest" ]]
  [[ "$(cat "$FAKE_REGISTRY/${prefix}_latest")" == "$new_digest" ]]
done
echo 'Image promotion rollback and success tests passed.'
