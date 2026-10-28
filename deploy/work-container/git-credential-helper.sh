#!/bin/sh
set -eu
[ "${1:-}" = get ] || exit 0
protocol= host= path=
while IFS='=' read -r key value; do
  [ -n "$key" ] || break
  case "$key" in
    protocol) protocol="$value" ;;
    host) host="$value" ;;
    path) path="$value" ;;
  esac
done
[ "$protocol" = https ] && [ "$host" = github.com ] || exit 0
allowed="${AGENT_REPOSITORY_SOURCE#https://github.com/}"
[ "$allowed" != "$AGENT_REPOSITORY_SOURCE" ] || exit 0
[ "$path" = "$allowed" ] || exit 0
[ -r /run/agent-secrets/github-token ] || exit 0
printf 'username=x-access-token\npassword='
cat /run/agent-secrets/github-token
printf '\n'
