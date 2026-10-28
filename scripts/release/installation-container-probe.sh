#!/usr/bin/env bash
# Run the native candidate as an unprivileged first-time user in a clean image.
set -euo pipefail
candidate_dir="${1:?candidate directory required}"
report_dir="${2:?report directory required}"
if [[ "$(id -u)" -ne 0 ]]; then
  echo "Run this wrapper as root inside a disposable Linux container." >&2
  exit 2
fi
. /etc/os-release
case "$ID" in
  ubuntu|debian)
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
      bash ca-certificates coreutils curl findutils gawk git gzip hostname iproute2 libicu-dev \
      openssh-client passwd python3 procps sed tar util-linux >/dev/null
    ;;
  fedora)
    dnf install -y -q bash ca-certificates coreutils curl findutils gawk git gzip hostname iproute \
      libicu openssh-clients python3 procps sed shadow-utils tar util-linux >/dev/null
    ;;
  *) echo "Unsupported container distribution: $ID" >&2; exit 2 ;;
esac
useradd --create-home --shell /bin/bash scenario
mkdir -p "$report_dir"
chown -R scenario:scenario "$report_dir"
chmod 0755 "$candidate_dir/agent-studio-setup"
export AGENT_SETUP_SKIP_ROOT_CHECK=1
runuser -u scenario -- env SETUP_E2E_RELEASE_DIR="$candidate_dir" SETUP_E2E_CONTROL_ONLY=1 \
  bash scripts/release/setup-e2e-probe.sh "$report_dir/linux-native-distro-smoke.md"
