#!/usr/bin/env bash
# I05 host enrolment (docs/operations/setup/identity-and-project-bootstrap.md).
# Exchange a one-time enrolment code, delivered over the administration
# channel into a restricted file, for this host's own principal credential.
# Neither secret is printed, logged, or passed on a command line.
#
# Usage: enrol-host-principal.sh <task-server-url> <expected-installation-id> <code-file> <credential-file>
set -euo pipefail
[ "$#" -eq 4 ] || { echo "usage: $0 <task-server-url> <installation-id> <code-file> <credential-file>" >&2; exit 2; }
server="${1%/}" installation="$2" code_file="$3" credential_file="$4"
source "$(dirname "${BASH_SOURCE[0]}")/task-server-url.sh"
validate_task_server_url "$server"
[ -f "$code_file" ] || { echo "code file not found" >&2; exit 2; }
if [ "$(stat -c %a "$code_file")" != 600 ] && [ "$(stat -c %a "$code_file")" != 400 ]; then
    echo "code file must be mode 600 or 400" >&2; exit 2
fi
[ ! -e "$credential_file" ] || { echo "credential file exists; rotate or revoke explicitly" >&2; exit 3; }

protocol=(-H "X-Task-Protocol-Version: ${TASK_PROTOCOL_VERSION:-2}")
observed="$(curl "${task_server_curl_options[@]}" -fsS "${protocol[@]}" "$server/api/v1/installation" | jq -r .installationId)"
[ "$observed" = "$installation" ] || { echo "installation mismatch: refusing to join $observed" >&2; exit 4; }

umask 077
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
jq -n --rawfile code "$code_file" --arg installation "$installation" \
    '{enrolmentCode: ($code | rtrimstr("\n")), expectedInstallationId: $installation}' > "$work/request.json"
status="$(curl "${task_server_curl_options[@]}" -sS -o "$work/response.json" -w '%{http_code}' "${protocol[@]}" -H 'Content-Type: application/json' \
    --data-binary "@$work/request.json" "$server/api/v1/enrolments/exchange")"
if [ "$status" != 201 ]; then
    echo "enrolment denied (HTTP $status): $(jq -r .code "$work/response.json" 2>/dev/null || echo unknown)" >&2
    exit 5
fi
jq -r .issued.credential "$work/response.json" > "$work/credential"
mv -n "$work/credential" "$credential_file"
chmod 600 "$credential_file"
# The one-time code is spent; remove it so the host keeps only its own credential.
rm -f "$code_file"
printf 'enrolled principal=%s installation=%s credential-file=%s\n' \
    "$(jq -r .issued.principal.principalId "$work/response.json")" "$installation" "$credential_file"
