#!/bin/sh
set -eu
[ "$#" -eq 1 ] || { echo 'Usage: extract-release-notes.sh <version>' >&2; exit 2; }
version=${1#v}
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
awk -v version="$version" '
  index($0, "## [" version "]") == 1 { found=1; next }
  found && /^## \[/ { exit }
  found { print }
  END { if (!found) exit 1 }
' "$repo_root/CHANGELOG.md"
