#!/usr/bin/env bash
# DP-S3: write the digest-pinned role image manifest for the released
# agent-work image. Hosts install it with
#   agent-host work-container image-promote --role all --image <ref@sha256:...>
# which keeps the replaced digest as the rollback target.
set -euo pipefail

version="${1:?version required}"
output="${2:?output path required}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Expected a semantic version.' >&2; exit 2; }
image="ghcr.io/agent-orc/agent-work"
digest="$(regctl image digest "$image:v$version")"
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Invalid digest for $image:v$version." >&2; exit 1; }
mkdir -p "$(dirname "$output")"
pinned="$image@$digest"
printf '{\n  "schemaVersion": 1,\n  "roles": {\n' > "$output"
separator=""
for role in coding review gate; do
  printf '%s    "%s": { "current": "%s", "previous": null, "release": "%s" }' \
    "$separator" "$role" "$pinned" "$version" >> "$output"
  separator=$',\n'
done
printf '\n  }\n}\n' >> "$output"
echo "role images pinned to $pinned"
