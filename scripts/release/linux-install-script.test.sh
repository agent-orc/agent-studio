#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM
mkdir -p "$work/bin" "$work/release"
cat >"$work/bin/curl" <<'CURL'
#!/bin/sh
set -eu
output=
url=
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) output=$2; shift 2 ;;
        -*) shift ;;
        *) url=$1; shift ;;
    esac
done
source_file="${FAKE_RELEASE_DIR:?}/${url##*/}"
[ -f "$source_file" ] || exit 22
if [ -n "$output" ]; then cp "$source_file" "$output"; else cat "$source_file"; fi
CURL
chmod 0755 "$work/bin/curl"
cat >"$work/release/agent-studio-setup" <<'SETUP'
#!/bin/sh
set -eu
printf '%s\n' "$*" >>"${FAKE_SETUP_CALLS:?}"
SETUP
chmod 0755 "$work/release/agent-studio-setup"
cp "$repo_root/install.sh" "$work/release/install.sh"
(
    cd "$work/release"
    sha256sum agent-studio-setup install.sh >SHA256SUMS
)
export FAKE_RELEASE_DIR="$work/release" FAKE_SETUP_CALLS="$work/calls"
export AGENT_STUDIO_RELEASE_URL=https://installer.test/release
PATH="$work/bin:$PATH" sh "$work/release/install.sh" --version >/dev/null
PATH="$work/bin:$PATH" curl -fsSL https://installer.test/release/install.sh \
    | PATH="$work/bin:$PATH" sh -s -- --mode studio --target native >/dev/null
[ "$(sed -n '1p' "$work/calls")" = '--version' ]
[ "$(sed -n '2p' "$work/calls")" = '--mode studio --target native' ]

printf '# tampered\n' >>"$work/release/agent-studio-setup"
if PATH="$work/bin:$PATH" sh "$work/release/install.sh" --version >"$work/out" 2>&1; then
    echo 'Tampered setup unexpectedly ran.' >&2
    exit 1
fi
grep -q 'SHA256SUMS verification failed' "$work/out"
[ "$(wc -l <"$work/calls")" -eq 2 ]
printf 'Linux install script verified both invocation forms and rejected a tampered binary.\n'
