#!/usr/bin/env bash
# AGT-2863: --restart-review-drain must finish the running reviews before the
# promotion, so the new daemon never adopts a worker of the outgoing release.
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
fixture_root="$(mktemp -d)"
order_log="$fixture_root/order"
scenario="active"

cleanup() {
  rm -rf -- "$fixture_root"
}
trap cleanup EXIT

# shellcheck source=/dev/null
source "$helper_path"

systemctl() {
  local action="${1:-}"
  shift || true
  case "$action" in
    is-active) [[ "$scenario" == "active" ]] ;;
    *) return 64 ;;
  esac
}

drain_review_unit() {
  printf 'drain\n' >>"$order_log"
}

promote_release() {
  printf 'promote\n' >>"$order_log"
}

promote_release_after_review_drain >/dev/null
printf 'busy-order=%s\n' "$(paste -sd, "$order_log")"

: >"$order_log"
scenario="inactive"
promote_release_after_review_drain >/dev/null
printf 'idle-order=%s\n' "$(paste -sd, "$order_log")"
