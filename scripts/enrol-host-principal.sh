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
credential_dir="$(dirname "$credential_file")"
[ -d "$credential_dir" ] || { echo "credential directory not found" >&2; exit 2; }
[ ! -e "$credential_file" ] && [ ! -L "$credential_file" ] || { echo "credential file exists; rotate or revoke explicitly" >&2; exit 3; }

protocol=(-H "X-Task-Protocol-Version: ${TASK_PROTOCOL_VERSION:-2}")
observed="$(curl "${task_server_curl_options[@]}" -fsS "${protocol[@]}" "$server/api/v1/installation" | jq -r .installationId)"
[ "$observed" = "$installation" ] || { echo "installation mismatch: refusing to join $observed" >&2; exit 4; }

umask 077
work="$(mktemp -d)"
# Stage the credential beside its target so link(2) can install it atomically.
staged="$(mktemp "$credential_dir/.enrol-credential.XXXXXXXX")"
keep_staged=""
trap 'rm -rf "$work"; [ -n "$keep_staged" ] || rm -f "$staged"' EXIT
jq -n --rawfile code "$code_file" --arg installation "$installation" \
    '{enrolmentCode: ($code | rtrimstr("\n")), expectedInstallationId: $installation}' > "$work/request.json"
status="$(curl "${task_server_curl_options[@]}" -sS -o "$work/response.json" -w '%{http_code}' "${protocol[@]}" -H 'Content-Type: application/json' \
    --data-binary "@$work/request.json" "$server/api/v1/enrolments/exchange")"
if [ "$status" != 201 ]; then
    echo "enrolment denied (HTTP $status): $(jq -r .code "$work/response.json" 2>/dev/null || echo unknown)" >&2
    exit 5
fi
principal="$(jq -er '.issued.principal.principalId | select(type == "string" and length > 0)' "$work/response.json")" ||
    { echo "enrolment response named no principal; revoke it explicitly before retrying" >&2; exit 6; }
jq -er '.issued.credential | select(type == "string" and length > 0)' "$work/response.json" > "$staged" ||
    { echo "enrolment response carried no credential for principal=$principal; rotate or revoke it explicitly" >&2; exit 6; }
chmod 600 "$staged"
# link(2) fails when the target exists; mv -n can skip the install silently.
if ! ln -T "$staged" "$credential_file" 2>/dev/null; then
    keep_staged=1
    echo "credential file appeared during enrolment; the issued credential for principal=$principal is kept in $staged." \
        "Install it or revoke the principal explicitly; the code file is left in place." >&2
    exit 7
fi
# The one-time code is spent; remove it so the host keeps only its own credential.
rm -f "$code_file"
printf 'enrolled principal=%s installation=%s credential-file=%s\n' "$principal" "$installation" "$credential_file"
