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

# AGT-W63 I03: the owned host record states host facts once; a flag that
# disagrees with it is refused before any SSH connection is attempted.
record_dir="$(mktemp -d)"
trap 'rm -rf "$record_dir"' EXIT
cat >"$record_dir/host.json" <<'JSON'
{"schemaVersion":1,"hostId":"build-02","hostClass":"linux","serverUrl":"http://127.0.0.1:15031",
 "gitRemote":"https://example.invalid/team/project.git","gitPushRemote":"https://example.invalid/team/project.git",
 "envelope":{"totalSlots":3,"codingSlots":2,"reviewSlots":1},
 "roles":[{"role":"coding","principalId":"rnr-build-02-coding","tokenFile":"/etc/agent-runner/coding.token"},
          {"role":"review","principalId":"rnr-build-02-review","tokenFile":"/etc/agent-runner/review.token"}]}
JSON
if conflict="$(bash "$onboard" --host-record "$record_dir/host.json" --role coding --runner-id rnr-build-01-coding \
    --host build-02 --topology tunnel 2>&1)"; then
  printf 'expected a disagreeing --runner-id to be refused\n' >&2
  exit 1
fi
grep -Fq "disagrees with host record value 'rnr-build-02-coding'" <<<"$conflict"
for slot_case in 'coding-slots 1' 'review-slots 2'; do
  read -r slot_flag slot_value <<<"$slot_case"
  if conflict="$(bash "$onboard" --host-record "$record_dir/host.json" --role coding \
      "--$slot_flag" "$slot_value" --host build-02 --topology tunnel 2>&1)"; then
    printf 'expected disagreeing --%s to be refused\n' "$slot_flag" >&2
    exit 1
  fi
  grep -Fq -- "--$slot_flag '$slot_value' disagrees with host record value" <<<"$conflict"
done
python3 - "$record_dir/host.json" "$record_dir/missing-roles.json" <<'PY'
import json, sys
record = json.load(open(sys.argv[1]))
del record["roles"]
json.dump(record, open(sys.argv[2], "w"))
PY
if invalid="$(bash "$onboard" --host-record "$record_dir/missing-roles.json" \
    --host build-02 --topology tunnel 2>&1)"; then
  printf 'expected missing roles to be refused\n' >&2
  exit 1
fi
grep -Fq 'host record has no coding role' <<<"$invalid"
# The operational path must render from the installed record and use its
# generated profile when replacing old unit resource directives.
grep -Fq 'host-record render --record /etc/agent-host/host.json' "$onboard"
grep -Fq 'cp "$rendered_env" "$env_tmp"' "$onboard"
grep -Fq 'sudo install -m 0644 -o root -g root "$render_dir/profile.conf"' "$onboard"
grep -Fq 'resource_mode=(--replace-drop-in-resources)' "$onboard"
printf 'remote-runner-onboard host-record contract passed\n'
