#!/bin/sh
set -eu
umask 077

seed_root="${MONOLITH_CLI_AUTH_SEED_ROOT:-/run/cli-auth}"
state_home="${MONOLITH_CLI_STATE_HOME:-/data/home}"
force=0
staged_dir=
trap 'if [ -n "$staged_dir" ] && [ -d "$staged_dir" ]; then rm -rf "$staged_dir"; fi' EXIT

if [ "${1:-}" = --force ]; then
  force=1
  shift
  if [ "$#" -eq 0 ]; then
    echo 'Specify at least one provider after --force' >&2
    exit 2
  fi
elif [ "$#" -eq 0 ]; then
  set -- claude codex gemini
fi

mkdir -p "$state_home"
for provider do
  case "$provider" in
    claude|codex|gemini) ;;
    *) echo "Unknown CLI provider: $provider" >&2; exit 2 ;;
  esac

  seed_dir="$seed_root/$provider"
  state_dir="$state_home/.$provider"
  if [ ! -d "$seed_dir" ]; then
    echo "CLI authentication seed directory is missing: $seed_dir" >&2
    exit 1
  fi

  if [ "$force" -eq 1 ] || [ ! -d "$state_dir" ] || [ -z "$(find "$state_dir" -mindepth 1 -maxdepth 1 -print -quit)" ]; then
    if [ "$force" -eq 1 ] && [ -z "$(find "$seed_dir" -mindepth 1 -maxdepth 1 -print -quit)" ]; then
      echo "Cannot replace CLI state with an empty seed: $seed_dir" >&2
      exit 1
    fi
    staged_dir="$(mktemp -d "$state_home/.$provider.seed.XXXXXX")"
    cp -R "$seed_dir/." "$staged_dir/"
    chmod -R u+rwX "$staged_dir"
    if [ "$force" -eq 1 ]; then
      rm -rf "$state_dir"
    elif [ -d "$state_dir" ]; then
      rmdir "$state_dir"
    fi
    mv "$staged_dir" "$state_dir"
    staged_dir=
  fi
done
