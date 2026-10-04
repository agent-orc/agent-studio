#!/usr/bin/env bash
# I05 host repository proof. Probe fetch and permitted push for the project's
# registered canonical origin from this host and report the receipt. A
# runner fallback remote is never probed in its place: it cannot admit a project.
#
# Usage: probe-project-repository.sh <task-server-url> <runner-id> <project-id> <credential-file>
set -euo pipefail
[ "$#" -eq 4 ] || { echo "usage: $0 <task-server-url> <runner-id> <project-id> <credential-file>" >&2; exit 2; }
server="${1%/}" runner="$2" project="$3" credential_file="$4"
source "$(dirname "${BASH_SOURCE[0]}")/task-server-url.sh"
validate_task_server_url "$server"
umask 077
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Headers come from a private file so the bearer never appears in the process list.
printf 'Authorization: Bearer %s\nX-Task-Protocol-Version: %s\n' \
    "$(tr -d '\n' < "$credential_file")" "${TASK_PROTOCOL_VERSION:-2}" > "$work/headers"
auth=(-H "@$work/headers")
registration="$(curl "${task_server_curl_options[@]}" -fsS "${auth[@]}" "$server/api/v1/projects/$project/repository" | jq .registration)"
url="$(jq -r .repositoryUrl <<<"$registration")"
ref="$(jq -r .integrationRef <<<"$registration")"

fetch=false push=false detail=""
if git ls-remote --exit-code "$url" "refs/heads/$ref" >"$work/ls" 2>"$work/err"; then
    fetch=true
    git init -q "$work/repo"
    git -C "$work/repo" -c user.name=probe -c user.email=probe@localhost commit -q --allow-empty -m probe
    if git -C "$work/repo" push --dry-run -q "$url" "HEAD:refs/heads/agent-studio-probe/$runner" 2>"$work/err"; then
        push=true
    else
        detail="push: $(head -c 300 "$work/err" | tr '\n' ' ')"
    fi
else
    detail="fetch: $(head -c 300 "$work/err" | tr '\n' ' ')"
fi
jq -n --arg url "$url" --argjson fetch "$fetch" --argjson push "$push" --arg detail "$detail" \
    '{observedFetchUrl: $url, observedPushUrl: $url, fetchSucceeded: $fetch, pushSucceeded: $push,
      usedFallbackRemote: false, detail: (if $detail == "" then null else $detail end)}' > "$work/probe.json"
curl "${task_server_curl_options[@]}" -fsS "${auth[@]}" -H 'Content-Type: application/json' --data-binary "@$work/probe.json" \
    "$server/api/v1/runners/$runner/project-probes/$project" | jq -c '{projectId, runnerId, admitted, verdict}'
