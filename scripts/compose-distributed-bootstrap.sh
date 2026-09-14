#!/usr/bin/env bash
# Mint the three bearer credentials `docker compose --profile distributed`
# needs (DISTRIBUTED_STUDIO_TOKEN, DISTRIBUTED_ENGINE_TOKEN,
# DISTRIBUTED_RUNNER_TOKEN) into .env, so a first run needs no manual token
# generation or copying. Never overwrites a value that is already set.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_path="$repo_root/.env"
example_path="$repo_root/.env.example"

# .env itself may hold other operator secrets once populated; keep it
# owner-only from the moment this script creates or touches it.
umask 077

if [ ! -e "$env_path" ]; then
    if [ ! -f "$example_path" ]; then
        printf 'missing template: %s\n' "$example_path" >&2
        exit 2
    fi
    cp -- "$example_path" "$env_path"
    printf 'created %s from .env.example\n' "$env_path"
fi
chmod 600 -- "$env_path"

generate_token() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -hex 32
    else
        head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n'
    fi
}

# Upsert VAR=value in .env: replace an empty/missing assignment, leave any
# already-set non-empty value untouched, append if the key isn't present.
upsert_if_empty() {
    var_name="$1"
    current="$(sed -n "s/^${var_name}=\(.*\)$/\1/p" "$env_path" | tail -n1)"
    if [ -n "$current" ]; then
        printf 'keep    %s (already set)\n' "$var_name"
        return
    fi
    value="$(generate_token)"
    if grep -q "^${var_name}=" "$env_path"; then
        tmp="$(mktemp "$repo_root/.env.XXXXXX")"
        sed "s/^${var_name}=.*/${var_name}=${value}/" "$env_path" > "$tmp"
        mv -- "$tmp" "$env_path"
    else
        printf '%s=%s\n' "$var_name" "$value" >> "$env_path"
    fi
    chmod 600 -- "$env_path"
    printf 'generated %s\n' "$var_name"
}

upsert_if_empty DISTRIBUTED_STUDIO_TOKEN
upsert_if_empty DISTRIBUTED_ENGINE_TOKEN
upsert_if_empty DISTRIBUTED_RUNNER_TOKEN

printf '\n'
docker compose --profile distributed config --quiet
printf 'compose-distributed-bootstrap=ok\n'
printf 'env=%s\n' "$env_path"
