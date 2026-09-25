#!/usr/bin/env bash
# Create the two local files for the explicit legacy API runner pair.
# Neither file is ever committed and neither reaches a build context.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_path="$repo_root/runner.env"
token_path="$repo_root/runner.token"
template_path="$repo_root/runner.env.template"

# The runner refuses a token file that is readable by other users
# (runner/RunnerOptions.cs), so create both files under a strict umask.
umask 077

if [ ! -f "$template_path" ]; then
    printf 'missing template: %s\n' "$template_path" >&2
    exit 2
fi

created=""

if [ -e "$env_path" ]; then
    printf 'keep    %s (already present)\n' "$env_path"
else
    cp -- "$template_path" "$env_path"
    chmod 600 -- "$env_path"
    created="$created env"
    printf 'created %s\n' "$env_path"
fi

if [ -e "$token_path" ]; then
    printf 'keep    %s (already present)\n' "$token_path"
else
    # The legacy pair terminates the runner protocol at orchestrator-api,
    # which does not verify this bearer. It must still be a real, private,
    # non-empty value because the runner requires one for a non-loopback URL.
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 32 > "$token_path"
    else
        head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$token_path"
        printf '\n' >> "$token_path"
    fi
    chmod 600 -- "$token_path"
    created="$created token"
    printf 'created %s\n' "$token_path"
fi

if [ -n "$created" ]; then
    printf '\nEdit %s before starting the legacy runner services:\n' "$env_path"
    printf '  - RUNNER_GIT_REMOTE / RUNNER_GIT_PUSH_REMOTE\n'
    printf '  - CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY\n'
fi

printf '\n'
docker compose --profile legacy config --quiet
printf 'compose-runner-bootstrap=ok\n'
printf 'env=%s\n' "$env_path"
printf 'token=%s\n' "$token_path"
