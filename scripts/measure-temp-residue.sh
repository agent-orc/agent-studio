#!/usr/bin/env bash
# Counts entries in a temp root before and after a command, per known leak
# prefix (AGT-2858).
#
# This is the outside measurement behind the "the temp-entry count before/after
# is unchanged" evidence: the in-suite guard
# (TempRootHygieneGuardTests) sees only the tests that ran before it, this sees
# the whole process including everything the runtime itself created.
#
#   scripts/measure-temp-residue.sh --label run1 --out residue.jsonl -- \
#     dotnet test backend.Tests/OrchestratorApi.Tests.csproj
set -euo pipefail

label="run"
out=""
temp_root="${TMPDIR:-/tmp}"
prefixes=(
  clr-debug-pipe
  dotnet-diagnostic
  MSBuild
  atp-
  agent-taskboard-tests-
  bus-bridge-fake-job
  studio-task-artifacts
  agent-studio-m1-pilot-cache
  ats-
)

usage() {
  printf '%s\n' \
    "Usage: measure-temp-residue.sh [--label <name>] [--out <file.jsonl>] [--temp-root <path>] -- <command...>" \
    "" \
    "Options:" \
    "  --label <name>       Label written into each measurement row (default: run)" \
    "  --out <file>         Append the two JSON rows here (default: stdout)" \
    "  --temp-root <path>   Temp root to count (default: \$TMPDIR, then /tmp)" \
    "  -h, --help           Show this help"
}

die() {
  printf 'measure-temp-residue: %s\n' "$*" >&2
  exit 2
}

while (($#)); do
  case "$1" in
    --label) label="${2:-}"; shift 2 ;;
    --out) out="${2:-}"; shift 2 ;;
    --temp-root) temp_root="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    --) shift; break ;;
    *) die "unknown argument '$1' (see --help)" ;;
  esac
done

(($# > 0)) || die "no command given (put it after --)"
[[ -d "$temp_root" ]] || die "temp root '$temp_root' does not exist"

emit() {
  local row="$1"
  if [[ -n "$out" ]]; then printf '%s\n' "$row" >> "$out"; else printf '%s\n' "$row"; fi
}

count() {
  local stage="$1" total row
  total=$(find "$temp_root" -mindepth 1 -maxdepth 1 | wc -l)
  row=$(printf '{"label":"%s","stage":"%s","tempRoot":"%s","timestamp":"%s","total":%s' \
    "$label" "$stage" "$temp_root" "$(date -Is)" "$total")
  local prefix n
  for prefix in "${prefixes[@]}"; do
    n=$(find "$temp_root" -mindepth 1 -maxdepth 1 -name "${prefix}*" | wc -l)
    row+=$(printf ',"%s":%s' "$prefix" "$n")
  done
  emit "$row}"
}

count before
status=0
"$@" || status=$?
count after
exit "$status"
