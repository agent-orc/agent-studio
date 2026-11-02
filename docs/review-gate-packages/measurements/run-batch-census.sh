#!/usr/bin/env bash
# Runs the read-only batch gate census (docs/review-gate-packages/batch-gate-census.py,
# Dossier AGT-3400) on every <instances>/*/batch-gates folder and writes one output
# file per instance plus a summary into the output directory.
#
# Usage: run-batch-census.sh [instances-root] [output-dir]
#   instances-root  default: ${XDG_DATA_HOME:-$HOME/.local/share}/agentstudio/instances
#   output-dir      default: the directory of this script
# Environment: CENSUS_SCRIPT (default: ../batch-gate-census.py next to this folder),
#   CENSUS_PROJECT (default: Agent Studio), CENSUS_SINCE (default: 2026-10-13).
#
# The Studio container keeps its store under its own data volume; run this as the
# account that owns it, for example with the container's home as instances root.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/agentstudio/instances}"
out="${2:-$here}"
script="${CENSUS_SCRIPT:-$here/../batch-gate-census.py}"
project="${CENSUS_PROJECT:-Agent Studio}"
since="${CENSUS_SINCE:-2026-10-13}"
host="$(hostname -s)"
stamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

if [[ ! -r "$script" ]]; then
  echo "census script not readable: $script" >&2
  exit 2
fi
if [[ ! -d "$root" || ! -r "$root" ]]; then
  echo "instances root not readable: $root" >&2
  exit 2
fi

mkdir -p "$out"
summary="$out/batch-census-$host-summary.txt"
instances=0
found=0
{
  echo "host: $host"
  echo "captured: $stamp"
  echo "instances root: $root"
  echo "command: python3 -I batch-gate-census.py <folder> --project \"$project\" --since $since"
} > "$summary"

for dir in "$root"/*/; do
  [[ -d "$dir" ]] || continue
  instances=$((instances + 1))
  gates="${dir%/}/batch-gates"
  [[ -d "$gates" ]] || continue
  found=$((found + 1))
  id="$(basename "${dir%/}")"
  file="$out/batch-census-$host-$id.txt"
  {
    echo "host: $host"
    echo "captured: $stamp"
    echo "folder: $gates"
    echo
    python3 -I "$script" "$gates" --project "$project" --since "$since"
  } > "$file"
  echo "batch-gates: $gates -> $(basename "$file")" >> "$summary"
done

{
  echo "instance folders: $instances"
  echo "instance folders with batch-gates: $found"
} >> "$summary"
cat "$summary"
