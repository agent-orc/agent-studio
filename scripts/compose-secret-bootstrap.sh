#!/bin/sh
# Run as root only in the one-shot container; files are owned by service uid 10001.
set -eu
umask 077
secret_dir=/run/agent-studio-secrets
mkdir -p "$secret_dir"
for data_dir in /var/lib/agent-orchestrator/store /var/lib/agent-orchestrator/backup /data/workspace /data/projects /var/lib/agent-host; do
    mkdir -p "$data_dir"
    chown 10001:10001 "$data_dir"
done
for native_dir in /run/native-auth/coding/claude /run/native-auth/coding/codex /run/native-auth/coding/gemini /run/native-auth/review/claude /run/native-auth/review/codex /run/native-auth/review/gemini; do
    mkdir -p "$native_dir"
    chown 10001:10001 "$native_dir"
    chmod 0700 "$native_dir"
done
for principal in studio engine runner review_runner; do
    role_dir="$secret_dir/$principal"
    mkdir -p "$role_dir"
    chmod 0700 "$role_dir"
    chown 10001:10001 "$role_dir"
    target="$role_dir/${principal}_token"
    legacy="$secret_dir/${principal}_token"
    if [ -e "$legacy" ] && [ ! -L "$legacy" ]; then
        [ ! -e "$target" ] || { echo "Conflicting $principal credential generations." >&2; exit 1; }
        mv "$legacy" "$target"
    fi
    if [ ! -e "$target" ]; then
        temporary="$(mktemp "$role_dir/.${principal}.XXXXXXXX")"
        head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$temporary"
        printf '\n' >> "$temporary"
        chmod 600 "$temporary"
        # A single bootstrap container owns creation. Never replace an existing token.
        if [ ! -e "$target" ]; then mv "$temporary" "$target"; else rm -f "$temporary"; fi
    fi
    chown 10001:10001 "$target"
    test -s "$target"
    test "$(stat -c %a "$target")" = 600
    if [ -L "$legacy" ]; then
        test "$(readlink "$legacy")" = "$principal/${principal}_token" || {
            echo "Unexpected legacy $principal link." >&2; exit 1;
        }
    else
        ln -s "$principal/${principal}_token" "$legacy"
    fi
done
# The first-owner code is distinct from service principal credentials. It is
# created once; restarting bootstrap must never replace it.
target="$secret_dir/owner_bootstrap_code"
if [ ! -e "$target" ]; then
    temporary="$(mktemp "$secret_dir/.owner_bootstrap.XXXXXXXX")"
    head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$temporary"
    printf '\n' >> "$temporary"
    chmod 600 "$temporary"
    if [ ! -e "$target" ]; then mv "$temporary" "$target"; else rm -f "$temporary"; fi
fi
chown 10001:10001 "$target"
test -s "$target"
test "$(stat -c %a "$target")" = 600
printf 'compose-secrets=ready\n'
