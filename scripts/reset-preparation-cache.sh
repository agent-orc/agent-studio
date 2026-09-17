#!/usr/bin/env bash
# The one supported way to inspect or reset a project's repository-preparation
# cache (AGT-2858).
#
# The cache is product-owned and lives under the runner's cache root, never in a
# temp directory. It is bounded by age and size on every preparation, so a manual
# reset is only needed after a corrupted entry or a toolchain change that should
# not wait for the next eviction.
set -euo pipefail

project=""
cache_root="${AGENT_STUDIO_CACHE_ROOT:-}"
project_cache=""
mode="show"

usage() {
  printf '%s\n' \
    "Usage: reset-preparation-cache.sh --project <id> [--cache-root <path>] [--reset|--reset-block <block>]" \
    "" \
    "Options:" \
    "  --project <id>        Project identity whose cache is addressed (required" \
    "                        unless --project-cache is given)" \
    "  --cache-root <path>   Cache root (default: \$AGENT_STUDIO_CACHE_ROOT, then" \
    "                        /var/lib/agent-runner/cache, then \$HOME/.local/share/agent-studio/cache)" \
    "  --project-cache <p>   Address one project cache directly. Use this for a runner" \
    "                        coding cache, whose layout is" \
    "                        \$RUNNER_WORKDIR/<project>/caches/preparation" \
    "  --reset               Remove every published entry and every per-run folder" \
    "  --reset-block <block> Remove one technology block only (npm, nuget, playwright)" \
    "  -h, --help            Show this help" \
    "" \
    "Without --reset the script only reports what is there, so it is safe to run" \
    "on a busy host. A reset costs the next preparation one cold restore; it never" \
    "touches task storage, worktrees, or review workspaces."
}

die() {
  printf 'reset-preparation-cache: %s\n' "$*" >&2
  exit 2
}

block=""
while (($#)); do
  case "$1" in
    --project) project="${2:-}"; shift 2 ;;
    --cache-root) cache_root="${2:-}"; shift 2 ;;
    --project-cache) project_cache="${2:-}"; shift 2 ;;
    --reset) mode="reset"; shift ;;
    --reset-block) mode="reset-block"; block="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument '$1' (see --help)" ;;
  esac
done

[[ -n "$project" || -n "$project_cache" ]] || die "--project or --project-cache is required"
[[ "$mode" != "reset-block" || -n "$block" ]] || die "--reset-block needs a block name"

if [[ -n "$project_cache" ]]; then
  # A runner coding cache does not follow <cache root>/<project>; it is
  # $RUNNER_WORKDIR/<project>/caches/preparation. Addressing it directly keeps
  # one script for both layouts.
  project_root="$project_cache"
  cache_root="$(dirname "$project_cache")"
  project="${project:-$(basename "$(dirname "$(dirname "$project_cache")")")}"
else
  if [[ -z "$cache_root" ]]; then
    if [[ -d /var/lib/agent-runner/cache && -w /var/lib/agent-runner/cache ]]; then
      cache_root=/var/lib/agent-runner/cache
    else
      cache_root="${XDG_DATA_HOME:-$HOME/.local/share}/agent-studio/cache"
    fi
  fi
  # Mirrors ProjectPreparationPaths.ProjectCacheRoot: anything outside
  # [A-Za-z0-9._-] becomes a dash, so the shell and the product agree on the path.
  project_root="$cache_root/$(printf '%s' "$project" | sed 's/[^A-Za-z0-9._-]/-/g')"
fi

printf 'project        %s\n' "$project"
printf 'cache root     %s\n' "$cache_root"
printf 'project cache  %s\n' "$project_root"

if [[ ! -d "$project_root" ]]; then
  printf 'state          absent (the next preparation creates it)\n'
  exit 0
fi

printf 'size           %s\n' "$(du -sh "$project_root" 2>/dev/null | cut -f1)"
if [[ -d "$project_root/entries" ]]; then
  while IFS= read -r entry_block; do
    count=$(find "$project_root/entries/$entry_block" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l)
    printf 'block          %-12s entries=%s size=%s\n' \
      "$entry_block" "$count" \
      "$(du -sh "$project_root/entries/$entry_block" 2>/dev/null | cut -f1)"
  done < <(find "$project_root/entries" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null | sort)
fi
# Per-run working copies. Each is a full copy of every block the repository
# uses, so this is usually the larger half of the cache; a gate or coding run
# that was killed never released its own, and the age prune reclaims it on the
# next preparation.
runs=$(find "$project_root/.runs" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l)
printf 'run folders    %s size=%s\n' \
  "$runs" "$(du -sh "$project_root/.runs" 2>/dev/null | cut -f1)"

case "$mode" in
  show)
    printf 'action         none (pass --reset or --reset-block to remove)\n'
    ;;
  reset)
    rm -rf -- "$project_root/entries" "$project_root/.runs"
    printf 'action         reset (all blocks and run folders removed)\n'
    ;;
  reset-block)
    [[ -d "$project_root/entries/$block" ]] || die "no cached block '$block' under $project_root/entries"
    rm -rf -- "${project_root:?}/entries/$block"
    printf 'action         reset-block %s\n' "$block"
    ;;
esac
