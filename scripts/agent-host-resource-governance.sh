#!/usr/bin/env bash
# Linux systemd resource policy owned by the agent-host installation path.
set -euo pipefail

role=""
cpu_count=""
profile="/etc/agent-host/profile.conf"
drop_in_dir=""
migrate_drop_ins=0
coding_slots=2
review_slots=2

usage() {
  printf '%s\n' \
    "Usage: agent-host-resource-governance.sh --role <coding|review> [options]" \
    "" \
    "Options:" \
    "  --cpu-count <n>       Host logical CPU count (default: nproc)" \
    "  --coding-slots <n>    Declared coding slots (default: 2)" \
    "  --review-slots <n>    Declared review slots (default: 2)" \
    "  --profile <path>      agent-host profile (default: /etc/agent-host/profile.conf)" \
    "  --drop-in-dir <path>  Existing service drop-in directory to inspect" \
    "  --migrate-drop-ins    Adopt resource values and remove them from drop-ins" \
    "  -h, --help            Show this help"
}

die() {
  printf 'agent-host resource governance: %s\n' "$*" >&2
  exit 2
}

while (($#)); do
  case "$1" in
    --role) role="${2:-}"; shift 2 ;;
    --cpu-count) cpu_count="${2:-}"; shift 2 ;;
    --coding-slots) coding_slots="${2:-}"; shift 2 ;;
    --review-slots) review_slots="${2:-}"; shift 2 ;;
    --profile) profile="${2:-}"; shift 2 ;;
    --drop-in-dir) drop_in_dir="${2:-}"; shift 2 ;;
    --migrate-drop-ins) migrate_drop_ins=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument '$1'" ;;
  esac
done

[[ "$role" =~ ^(coding|review)$ ]] || die "--role must be coding or review"
if [[ -z "$cpu_count" ]]; then
  command -v nproc >/dev/null || die "nproc is required when --cpu-count is omitted"
  cpu_count="$(nproc)"
fi
[[ "$cpu_count" =~ ^[1-9][0-9]*$ ]] || die "--cpu-count must be a positive integer"
[[ "$coding_slots" =~ ^[0-9]+$ ]] || die "--coding-slots must be a non-negative integer"
[[ "$review_slots" =~ ^[0-9]+$ ]] || die "--review-slots must be a non-negative integer"
((coding_slots + review_slots > 0)) || die "at least one coding or review slot is required"
[[ "$profile" == /* ]] || die "--profile must be an absolute path"
if [[ -n "$drop_in_dir" ]]; then
  [[ "$drop_in_dir" == /* ]] || die "--drop-in-dir must be an absolute path"
fi
((migrate_drop_ins == 0)) || [[ -n "$drop_in_dir" ]] || die "--migrate-drop-ins requires --drop-in-dir"

declare -A configured=()
declare -A profile_explicit=()
read_profile() {
  [[ -f "$profile" ]] || return 0
  local raw key value
  while IFS= read -r raw || [[ -n "$raw" ]]; do
    raw="${raw%%#*}"
    [[ "$raw" == *=* ]] || continue
    key="${raw%%=*}"
    value="${raw#*=}"
    key="${key//[[:space:]]/}"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    case "$key" in
      CODING_CPU_QUOTA|CODING_CPU_WEIGHT|CODING_IO_WEIGHT|CODING_MEMORY_MAX|\
      REVIEW_CPU_QUOTA|REVIEW_CPU_WEIGHT|REVIEW_IO_WEIGHT|REVIEW_MEMORY_MAX)
        configured["$key"]="$value"
        profile_explicit["$key"]=1
        ;;
    esac
  done <"$profile"
}

read_profile

profile_prefix="${role^^}"
quota_key="${profile_prefix}_CPU_QUOTA"
cpu_weight_key="${profile_prefix}_CPU_WEIGHT"
io_weight_key="${profile_prefix}_IO_WEIGHT"
memory_key="${profile_prefix}_MEMORY_MAX"

declare -A adopted=()
adopt_drop_in_values() {
  [[ -d "$drop_in_dir" ]] || return 0
  local file raw directive value profile_key
  while IFS= read -r -d '' file; do
    while IFS= read -r raw || [[ -n "$raw" ]]; do
      raw="${raw%%#*}"
      [[ "$raw" == *=* ]] || continue
      directive="${raw%%=*}"
      value="${raw#*=}"
      directive="${directive//[[:space:]]/}"
      value="${value#"${value%%[![:space:]]*}"}"
      value="${value%"${value##*[![:space:]]}"}"
      case "$directive" in
        CPUQuota) profile_key="$quota_key" ;;
        CPUWeight) profile_key="$cpu_weight_key" ;;
        IOWeight) profile_key="$io_weight_key" ;;
        MemoryMax) profile_key="$memory_key" ;;
        *) continue ;;
      esac
      if [[ -z "${profile_explicit[$profile_key]+present}" ]]; then
        if [[ -n "$value" ]]; then
          configured["$profile_key"]="$value"
          adopted["$profile_key"]="$value"
        else
          unset 'configured[$profile_key]'
          unset 'adopted[$profile_key]'
        fi
      fi
    done <"$file"
  done < <(find "$drop_in_dir" -maxdepth 1 -type f -name '*.conf' -print0 | sort -z)
}

write_adopted_profile_values() {
  ((${#adopted[@]} > 0)) || return 0
  local profile_dir profile_tmp key
  profile_dir="$(dirname "$profile")"
  mkdir -p "$profile_dir"
  profile_tmp="$(mktemp "$profile_dir/.profile.conf.XXXXXX")"
  if [[ -f "$profile" ]]; then
    cp "$profile" "$profile_tmp"
    chown --reference="$profile" "$profile_tmp"
    chmod --reference="$profile" "$profile_tmp"
  else
    printf '%s\n' \
      "# agent-host Linux resource profile." \
      "# Omitted values use host-derived defaults; explicit values are operator policy." \
      >"$profile_tmp"
    chmod 0644 "$profile_tmp"
  fi
  printf '\n# Adopted from legacy systemd drop-ins by agent-host.\n' >>"$profile_tmp"
  for key in "$quota_key" "$cpu_weight_key" "$io_weight_key" "$memory_key"; do
    [[ -n "${adopted[$key]+present}" ]] || continue
    printf '%s=%s\n' "$key" "${adopted[$key]}" >>"$profile_tmp"
  done
  mv "$profile_tmp" "$profile"
}

remove_resource_lines_from_drop_ins() {
  [[ -d "$drop_in_dir" ]] || return 0
  local file rewritten
  while IFS= read -r -d '' file; do
    rewritten="$(mktemp "${file}.XXXXXX")"
    awk '
      !/^[[:space:]]*(CPUQuota|CPUWeight|IOWeight|MemoryMax)[[:space:]]*=/ {
        print
      }
    ' "$file" >"$rewritten"
    if awk '
      /^[[:space:]]*($|#|;)/ { next }
      /^[[:space:]]*\[Service\][[:space:]]*$/ { next }
      { meaningful = 1 }
      END { exit meaningful ? 0 : 1 }
    ' "$rewritten"; then
      chmod --reference="$file" "$rewritten"
      mv "$rewritten" "$file"
    else
      rm -f "$rewritten" "$file"
    fi
  done < <(find "$drop_in_dir" -maxdepth 1 -type f -name '*.conf' -print0 | sort -z)
  rmdir "$drop_in_dir" 2>/dev/null || true
}

((migrate_drop_ins == 0)) || adopt_drop_in_values

if [[ "$role" == "coding" ]]; then
  default_quota="$((cpu_count * 100))%"
  default_cpu_weight=100
  default_io_weight=100
else
  # Review gets the host share implied by declared role slots. Coding keeps the
  # higher CPUWeight and its all-core burst ceiling, while Review can never be
  # granted the whole host by a derived default.
  review_quota=$((cpu_count * 100 * review_slots / (coding_slots + review_slots)))
  max_review_quota=$(((cpu_count - 1) * 100))
  ((max_review_quota > 0)) || max_review_quota=50
  ((review_quota >= 100)) || review_quota=100
  ((review_quota <= max_review_quota)) || review_quota=$max_review_quota
  default_quota="${review_quota}%"
  default_cpu_weight=30
  default_io_weight=30
fi

cpu_quota="${configured[$quota_key]:-$default_quota}"
cpu_weight="${configured[$cpu_weight_key]:-$default_cpu_weight}"
io_weight="${configured[$io_weight_key]:-$default_io_weight}"
memory_max="${configured[$memory_key]:-}"

[[ "$cpu_quota" =~ ^[1-9][0-9]*%?$ ]] || die "$quota_key must be a positive percentage"
[[ "$cpu_quota" == *% ]] || cpu_quota="${cpu_quota}%"
[[ "$cpu_weight" =~ ^[1-9][0-9]*$ ]] && ((cpu_weight <= 10000)) \
  || die "$cpu_weight_key must be between 1 and 10000"
[[ "$io_weight" =~ ^[1-9][0-9]*$ ]] && ((io_weight <= 10000)) \
  || die "$io_weight_key must be between 1 and 10000"
if [[ -n "$memory_max" ]]; then
  [[ "$memory_max" =~ ^([1-9][0-9]*[KMGTPE]?|infinity)$ ]] \
    || die "$memory_key must be a positive systemd size or infinity"
fi

if ((migrate_drop_ins == 1)); then
  write_adopted_profile_values
  remove_resource_lines_from_drop_ins
fi

printf 'CPUQuota=%s\n' "$cpu_quota"
printf 'CPUWeight=%s\n' "$cpu_weight"
printf 'IOWeight=%s\n' "$io_weight"
[[ -z "$memory_max" ]] || printf 'MemoryMax=%s\n' "$memory_max"
