#!/bin/sh
# Download the Linux setup executable from one release and verify it before use.
set -eu

fail() { printf 'Agent Studio install: %s\n' "$*" >&2; exit 1; }

[ "$(uname -s)" = Linux ] || fail 'Linux is required. Use agent-studio-setup.exe on Windows.'
case "$(uname -m)" in
    x86_64|amd64) ;;
    *) fail "No release binary is available for $(uname -m). The Linux installer currently supports x86_64." ;;
esac
[ -r /etc/os-release ] || fail 'Cannot identify this Linux distribution (/etc/os-release is missing).'
# os-release is a system file with shell-compatible assignments.
. /etc/os-release
case "${ID:-}" in
    debian|ubuntu|fedora) ;;
    *) fail "${PRETTY_NAME:-This distribution} is not a tested install target. Supported systems: Debian, Ubuntu, Fedora." ;;
esac

for command in curl sha256sum mktemp chmod awk; do
    command -v "$command" >/dev/null 2>&1 || fail "Missing $command. Install it with your distribution package manager."
done

release_version=DEV
if [ "$release_version" = DEV ]; then
    default_release_url=https://github.com/agent-orc/agent-studio/releases/latest/download
else
    default_release_url=https://github.com/agent-orc/agent-studio/releases/download/v$release_version
fi
release_url=${AGENT_STUDIO_RELEASE_URL:-${AGENT_STUDIO_INSTALL_BASE_URL:-${AGENT_STUDIO_RELEASE_BASE:-$default_release_url}}}
case "$release_url" in
    https://*|http://127.0.0.1:*|http://localhost:*) ;;
    *) fail 'AGENT_STUDIO_RELEASE_URL must use HTTPS (or local HTTP for tests).' ;;
esac
work=$(mktemp -d) || fail 'Could not create a temporary download directory.'
trap 'rm -rf -- "$work"' EXIT HUP INT TERM

printf 'Downloading Agent Studio setup for Linux x64 from %s\n' "$release_url"
curl -fsSL "$release_url/SHA256SUMS" -o "$work/SHA256SUMS" || fail 'Could not download SHA256SUMS.'
curl -fsSL "$release_url/agent-studio-setup" -o "$work/agent-studio-setup" || fail 'Could not download agent-studio-setup.'
(
    cd "$work"
    # Require an exact filename in the release checksum list.
    awk '$2 == "agent-studio-setup" && $1 ~ /^[[:xdigit:]]+$/ && length($1) == 64 { print; found = 1 } END { if (!found) exit 1 }' SHA256SUMS \
        >setup.sha256
    sha256sum -c setup.sha256 >/dev/null
) || fail 'SHA256SUMS verification failed for agent-studio-setup.'
chmod 0755 "$work/agent-studio-setup"
printf 'Verified agent-studio-setup against release SHA256SUMS.\n'

# A pipe supplies the script body on stdin. Restore the terminal so guided
# prompts work with both `curl ... | sh` and a downloaded script.
if ( : 2>/dev/null </dev/tty ); then
    "$work/agent-studio-setup" "$@" </dev/tty
else
    "$work/agent-studio-setup" "$@"
fi
