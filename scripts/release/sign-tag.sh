#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: sign-tag.sh VERSION}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
[[ "$(tr -d '\r\n' < VERSION)" == "$version" ]]
[[ -z "$(git status --porcelain --untracked-files=all)" ]]
tag="v$version"
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  printf 'Tag %s already exists.\n' "$tag" >&2
  exit 1
fi
git tag -s "$tag" -m "Release $tag"
git verify-tag "$tag"
printf 'Signed %s at %s\n' "$tag" "$(git rev-parse HEAD)"
