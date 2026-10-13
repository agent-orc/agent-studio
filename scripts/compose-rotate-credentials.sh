#!/bin/sh
# Product credential manager. Runs inside the short-lived Compose ops service.
#
# Two phases follow the Task Server principal rotation contract:
#   <role>             issue a rotation under a fresh operation id, install the
#                      new bearer in the volume, and mark it delivered.
#   <role> --complete  after compose-rotate.sh recreated the consumer service,
#                      prove the installed bearer with one tasks:read call and
#                      acknowledge it, which retires the previous generation.
# The operation id stays in a private state file until completion, so a rerun
# resumes the same rotation instead of issuing a second one. No bearer value is
# printed or passed on a command line.
set -eu
umask 077

usage() {
    echo 'usage: compose-rotate-credentials.sh studio|engine|runner|review-runner [--complete]' >&2
    exit 64
}

select_role() {
    case "$1" in
        studio) principal=bootstrap-studio consumer=compose-studio ;;
        engine) principal=bootstrap-engine consumer=compose-engine ;;
        # A Runner rotation consumer must be the Runner id the principal is bound to.
        runner) principal=runner:distributed-runner consumer=distributed-runner ;;
        review-runner) principal=runner:distributed-review-runner consumer=distributed-review-runner ;;
        *) usage ;;
    esac
}

require_operation_id() {
    case "$1" in
        '' | *[!A-Za-z0-9._:-]*) echo 'Rotation operation id is invalid.' >&2; exit 64 ;;
    esac
    [ "${#1}" -le 128 ] || { echo 'Rotation operation id is invalid.' >&2; exit 64; }
}

# Request plan. These functions are the wire contract with RotatePrincipalRequest
# and the principal-rotations endpoints; --print-requests renders them for the
# Task Server contract test.
overlap_seconds=3600
required_scope=tasks:read
issue_path() { printf '/api/v1/management/principals/%s/rotate' "$principal"; }
issue_body() {
    printf '{"operationId":"%s","overlapSeconds":%s,"consumers":[{"consumerId":"%s","requiredScope":"%s"}]}' \
        "$operation" "$overlap_seconds" "$consumer" "$required_scope"
}
deliver_path() { printf '/api/v1/principal-rotations/%s/delivered' "$operation"; }
prove_path() { printf '/api/v1/workspaces'; }
acknowledge_path() { printf '/api/v1/principal-rotations/%s/ack' "$operation"; }
acknowledge_body() { printf '{"consumerId":"%s"}' "$consumer"; }

if [ "${1:-}" = --print-requests ]; then
    [ "$#" -eq 3 ] || usage
    select_role "$2"
    operation="$3"
    require_operation_id "$operation"
    printf 'consumer %s\n' "$consumer"
    printf 'issue POST %s %s\n' "$(issue_path)" "$(issue_body)"
    printf 'deliver POST %s\n' "$(deliver_path)"
    printf 'prove GET %s\n' "$(prove_path)"
    printf 'acknowledge POST %s %s\n' "$(acknowledge_path)" "$(acknowledge_body)"
    exit 0
fi

role="${1:-}"
phase="${2:-issue}"
[ "$#" -le 2 ] || usage
case "$phase" in issue | --complete) ;; *) usage ;; esac
select_role "$role"
secret_dir=/run/agent-studio-secrets
server="${TASK_SERVER_URL:-http://task-server:5071}"
if [ "$role" = review-runner ]; then
    target="$secret_dir/review_runner/review_runner_token"
else
    target="$secret_dir/${role}/${role}_token"
fi
manager="$secret_dir/studio_token"
test -s "$target" || { echo "Missing $role credential; start the stack first." >&2; exit 1; }
state_dir="$secret_dir/.rotations"
mkdir -p "$state_dir"
chmod 700 "$state_dir"
state="$state_dir/$role.operation"
work="$(mktemp -d "$secret_dir/.rotation-work.XXXXXXXX")"
replacement=''
trap 'rm -rf "$work"; [ -z "$replacement" ] || rm -f "$replacement"' EXIT HUP INT TERM

# call METHOD PATH BEARER_FILE [BODY] writes the response to $work/response.
# The bearer travels in a private header file, never in argv.
call() {
    method="$1" path="$2" bearer="$3"
    printf '%s' "${4:-}" > "$work/body"
    {
        printf 'Authorization: Bearer %s\n' "$(tr -d '\r\n' < "$bearer")"
        printf 'X-Principal-Consumer-Id: %s\n' "$consumer"
    } > "$work/headers"
    if [ "$method" = GET ]; then set --; else set -- --data-binary "@$work/body"; fi
    status="$(curl --silent --show-error --request "$method" \
        --header "@$work/headers" \
        --header 'X-Task-Protocol-Version: 2' \
        --header 'Content-Type: application/json' \
        "$@" --output "$work/response" --write-out '%{http_code}' \
        "$server$path")" || { echo "Task Server is unreachable for $method $path." >&2; exit 1; }
    rm -f "$work/headers"
    case "$status" in
        2??) ;;
        *)
            reason="$(jq -r '"\(.code // "request-failed"): \(.message // "")"' "$work/response" 2>/dev/null \
                || echo request-failed)"
            echo "Task Server rejected $method $path with HTTP $status ($reason)." >&2
            exit 1
            ;;
    esac
}

if [ "$phase" = --complete ]; then
    test -s "$state" || {
        echo "No pending $role rotation; run compose-rotate.sh $role to issue one." >&2
        exit 1
    }
    operation="$(cat "$state")"
    require_operation_id "$operation"
    # The scoped call is the consumer proof the acknowledgement requires.
    call GET "$(prove_path)" "$target"
    call POST "$(acknowledge_path)" "$target" "$(acknowledge_body)"
    outcome="$(jq -r '.state' "$work/response")"
    [ "$outcome" = retired ] || {
        echo "Rotation $operation is $outcome after acknowledgement, not retired." >&2
        exit 1
    }
    rm -f "$state"
    printf 'credential-retired=%s operation=%s; the previous generation is revoked\n' "$role" "$operation"
    exit 0
fi

if test -s "$state"; then
    operation="$(cat "$state")"
    require_operation_id "$operation"
else
    operation="compose-$role-$(date -u +%Y%m%dT%H%M%SZ)-$(od -An -N6 -tx1 /dev/urandom | tr -d ' \n')"
    printf '%s\n' "$operation" > "$state.tmp"
    mv -f "$state.tmp" "$state"
fi
call POST "$(issue_path)" "$manager" "$(issue_body)"
generation="$(jq -er '.rotation.credentialGeneration | select(type == "string" and test("^[0-9a-f]{16,64}$"))' \
    "$work/response")" || { echo 'Task Server returned no rotation receipt.' >&2; exit 1; }
if jq -e '.credential | type == "string"' "$work/response" > /dev/null; then
    replacement="$(mktemp "$(dirname "$target")/.${role}-replacement.XXXXXXXX")"
    jq -r '.credential' "$work/response" > "$replacement"
    rm -f "$work/response"
    grep -q "^ats_$generation\\." "$replacement" || {
        rm -f "$replacement"
        echo 'The issued credential does not match its rotation receipt.' >&2
        exit 1
    }
    chmod 600 "$replacement"
    chown 10001:10001 "$replacement"
    mv -f "$replacement" "$target"
elif ! grep -q "^ats_$generation\\." "$target"; then
    # Task Server replays a bearer only until its delivery is recorded.
    echo "Rotation $operation was delivered, but the volume holds another generation. See docs/operations/setup/docker.md." >&2
    exit 1
fi
call POST "$(deliver_path)" "$target"
printf 'credential-issued=%s operation=%s; recreate its service, then run --complete within the one-hour overlap\n' \
    "$role" "$operation"
