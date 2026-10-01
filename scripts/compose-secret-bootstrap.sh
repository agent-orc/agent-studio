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
# owner_bootstrap_code is the I05 one-time first-owner code. The Task Server
# arms it only while no owner exists and never re-arms or rotates it.
for principal in studio engine runner review_runner owner_bootstrap; do
    target="$secret_dir/${principal}_token"
    [ "$principal" = owner_bootstrap ] && target="$secret_dir/owner_bootstrap_code"
    if [ ! -e "$target" ]; then
        temporary="$(mktemp "$secret_dir/.${principal}.XXXXXXXX")"
        head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$temporary"
        printf '\n' >> "$temporary"
        chmod 600 "$temporary"
        # A single bootstrap container owns creation. Never replace an existing token.
        if [ ! -e "$target" ]; then mv "$temporary" "$target"; else rm -f "$temporary"; fi
    fi
    chown 10001:10001 "$target"
    test -s "$target"
    test "$(stat -c %a "$target")" = 600
done
printf 'compose-secrets=ready\n'
