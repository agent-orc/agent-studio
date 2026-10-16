#!/bin/sh
# Usage: curl -fsSL https://github.com/agent-orc/agent-studio/releases/latest/download/install.sh | sh
set -eu

base=${AGENT_STUDIO_INSTALL_BASE_URL:-https://github.com/agent-orc/agent-studio/releases/latest/download}
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM
curl -fsSL "$base/agent-studio-setup" -o "$work/agent-studio-setup"
curl -fsSL "$base/SHA256SUMS" -o "$work/SHA256SUMS"
(cd "$work" && grep '  agent-studio-setup$' SHA256SUMS | sha256sum -c -)
chmod 0755 "$work/agent-studio-setup"
"$work/agent-studio-setup" "$@"
