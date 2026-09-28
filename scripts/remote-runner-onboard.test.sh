#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
onboard="$script_dir/remote-runner-onboard.sh"
bash -n "$onboard"

sshd_phase="$(sed -n "/^\"\${ssh_base\[@\]}\" -T \"\$host\" bash -s <<'REMOTE_SSHD_LIVENESS'$/,/^REMOTE_SSHD_LIVENESS$/p" "$onboard")"
[[ -n "$sshd_phase" ]]
grep -Fq '/etc/ssh/sshd_config.d/05-agent-runner-client-alive.conf' <<<"$sshd_phase"
grep -Fxq 'ClientAliveInterval 30' <<<"$sshd_phase"
grep -Fxq 'ClientAliveCountMax 3' <<<"$sshd_phase"
grep -Fq 'sudo sshd -t' <<<"$sshd_phase"
grep -Fq "grep -qx 'clientaliveinterval 30'" <<<"$sshd_phase"
grep -Fq "grep -qx 'clientalivecountmax 3'" <<<"$sshd_phase"
grep -Fq 'sudo systemctl reload' <<<"$sshd_phase"

printf 'remote-runner-onboard sshd liveness contract passed\n'
