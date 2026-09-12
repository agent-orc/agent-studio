#!/usr/bin/env bash
set -euo pipefail

helper_path="${1:?expected the agent-runner-deploy path}"
fixture_root="$(mktemp -d)"
patched_helper="$fixture_root/agent-runner-deploy"
cli_root_fixture="$fixture_root/cli"
fake_npm="$fixture_root/npm"
npm_log="$fixture_root/npm.log"
probe_log="$fixture_root/probes.log"
fail_probe="$fixture_root/fail-model-probe"
fail_activation="$fixture_root/fail-activation"
codex_link_fixture="$fixture_root/bin/codex"
claude_link_fixture="$fixture_root/bin/claude"

cleanup() { rm -rf -- "$fixture_root"; }
trap cleanup EXIT
mkdir -p "$fixture_root/bin" "$cli_root_fixture/releases/previous/node_modules/.bin"
ln -sfnT "$cli_root_fixture/releases/previous" "$cli_root_fixture/current"

sed \
  -e "s#^readonly service_user=.*#readonly service_user=\"$(id -un)\"#" \
  -e "s#^readonly cli_root=.*#readonly cli_root=\"$cli_root_fixture\"#" \
  -e "s#^readonly cli_releases_root=.*#readonly cli_releases_root=\"$cli_root_fixture/releases\"#" \
  -e "s#^readonly cli_current_link=.*#readonly cli_current_link=\"$cli_root_fixture/current\"#" \
  -e "s#^readonly npm_binary=.*#readonly npm_binary=\"$fake_npm\"#" \
  -e "s#^readonly codex_launcher=.*#readonly codex_launcher=\"$codex_link_fixture\"#" \
  -e "s#^readonly claude_launcher=.*#readonly claude_launcher=\"$claude_link_fixture\"#" \
  -e 's/install -d -o root -g root -m 0755/install -d -m 0755/' \
  -e 's/^  chown -R root:root "$staging_root"/  :/' \
  "$helper_path" >"$patched_helper"

cat >"$fake_npm" <<EOF
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "\$*" >>"$npm_log"
prefix=""
while ((\$#)); do
  if [[ "\$1" == "--prefix" ]]; then prefix="\$2"; shift 2; else shift; fi
done
mkdir -p "\$prefix/node_modules/.bin"
for binary in codex claude; do
cat >"\$prefix/node_modules/.bin/\$binary" <<PROBE
#!/usr/bin/env bash
printf '%s %s\n' "\$binary" "\\\$*" >>"$probe_log"
if [[ -f "$fail_probe" && "\$binary" == codex && "\\\${1:-}" == debug ]]; then exit 42; fi
exit 0
PROBE
  chmod 0755 "\$prefix/node_modules/.bin/\$binary"
done
EOF
chmod 0755 "$fake_npm"

# shellcheck source=/dev/null
source "$patched_helper"
assert_review_restart_is_safe() { :; }
probe_staged_cli() {
  local prefix="$1"
  local binary="$2"
  shift 2
  "$prefix/node_modules/.bin/$binary" "$@" >/dev/null
}
unit_main_pid() { printf '100\n'; }
restart_unit_and_wait_for_new_main_pid() { [[ ! -f "$fail_activation" ]] && printf '101\n'; }
systemctl() { [[ ! -f "$fail_activation" ]]; }
logger() { :; }

update_clis 0.154.0 2.1.269
activated="$(readlink -f "$cli_current_link")"
[[ "$activated" == "$cli_releases_root/codex-0.154.0--claude-2.1.269" ]]
grep -Fq -- '-- @openai/codex@0.154.0 @anthropic-ai/claude-code@2.1.269' "$npm_log"
grep -Fxq 'codex --version' "$probe_log"
grep -Fxq 'claude --version' "$probe_log"
grep -Fxq 'codex login status' "$probe_log"
grep -Fxq 'claude auth status' "$probe_log"
grep -Fxq 'codex debug models' "$probe_log"
grep -Fxq 'claude models' "$probe_log"

touch "$fail_probe"
set +e
(set -e; update_clis 0.155.0 2.1.270) >/dev/null 2>&1
probe_status="$?"
set -e
[[ "$probe_status" -ne 0 ]] || { echo 'failing model probe unexpectedly activated' >&2; exit 1; }
[[ "$(readlink -f "$cli_current_link")" == "$activated" ]]
[[ ! -e "$cli_releases_root/codex-0.155.0--claude-2.1.270" ]]

rm -f "$fail_probe"
touch "$fail_activation"
set +e
(set -e; update_clis 0.156.0 2.1.271) >/dev/null 2>&1
activation_status="$?"
set -e
[[ "$activation_status" -ne 0 ]] || { echo 'failing service activation unexpectedly succeeded' >&2; exit 1; }
[[ "$(readlink -f "$cli_current_link")" == "$activated" ]]
[[ ! -e "$cli_releases_root/codex-0.156.0--claude-2.1.271" ]]

printf 'packages=fixed-codex-and-claude\n'
printf 'staged-probes=version-login-models\n'
printf 'activation=atomic-symlink\n'
printf 'failed-probe=previous-release-retained\n'
printf 'failed-activation=previous-release-restored\n'
