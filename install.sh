#!/bin/sh
# Download and verify the released Linux setup executable, then pass through its options.
set -eu

release_base=${AGENT_STUDIO_RELEASE_BASE:-https://github.com/agent-orc/agent-studio/releases/latest/download}
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT HUP INT TERM
curl -fsSL "$release_base/SHA256SUMS" -o "$scratch/SHA256SUMS"
curl -fsSL "$release_base/agent-studio-setup" -o "$scratch/agent-studio-setup"
(cd "$scratch" && grep '  agent-studio-setup$' SHA256SUMS | sha256sum -c -)
chmod 0755 "$scratch/agent-studio-setup"
"$scratch/agent-studio-setup" "$@"
