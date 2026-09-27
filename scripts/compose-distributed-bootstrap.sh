#!/usr/bin/env bash
# Prepare a one-box installation. The Compose bootstrap service creates and
# retains principal credentials in its named volume on the first `up`.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
umask 077
if [ ! -e "$repo_root/.env" ]; then
    cp -- "$repo_root/.env.example" "$repo_root/.env"
    printf 'created %s from .env.example\n' "$repo_root/.env"
fi
chmod 600 -- "$repo_root/.env"
(
    cd "$repo_root"
    docker compose config --quiet
)
printf 'compose-distributed-bootstrap=ok\n'
printf 'credentials=created by the Compose bootstrap service on first up\n'
