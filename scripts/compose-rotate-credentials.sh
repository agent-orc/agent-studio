#!/bin/sh
# Product credential manager. Runs inside the short-lived Compose ops service.
set -eu
umask 077
role="${1:-}"
case "$role" in
    studio) principal=bootstrap-studio ;;
    engine) principal=bootstrap-engine ;;
    runner) principal=runner:distributed-runner ;;
    *) echo 'usage: compose-rotate-credentials.sh studio|engine|runner' >&2; exit 64 ;;
esac
secret_dir=/run/agent-studio-secrets
target="$secret_dir/${role}_token"
test -s "$target" || { echo "Missing $role credential; start the stack first." >&2; exit 1; }
response="$(mktemp "$secret_dir/.rotation-response.XXXXXXXX")"
replacement="$(mktemp "$secret_dir/.${role}-replacement.XXXXXXXX")"
trap 'rm -f "$response" "$replacement"' EXIT HUP INT TERM
curl --fail --silent --show-error \
    --header "Authorization: Bearer $(cat "$secret_dir/studio_token")" \
    --header 'X-Task-Protocol-Version: 2' \
    --header 'Content-Type: application/json' \
    --data '{"overlapSeconds":3600}' \
    --output "$response" \
    "${TASK_SERVER_URL:-http://task-server:5071}/api/v1/management/principals/$principal/rotate"
jq --exit-status --raw-output '.credential | select(type == "string" and length > 31)' \
    "$response" > "$replacement"
test -s "$replacement"
chmod 600 "$replacement"
chown 10001:10001 "$replacement"
mv -f "$replacement" "$target"
printf 'credential-rotated=%s; restart its service within the one-hour overlap\n' "$role"
