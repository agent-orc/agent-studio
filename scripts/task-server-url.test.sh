#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"
cat > "$work/bin/curl" <<'EOF'
#!/usr/bin/env bash
touch "$CURL_MARKER"
printf '%s\n' "$@" > "$CURL_ARGS_FILE"
exit 75
EOF
chmod +x "$work/bin/curl"
printf 'one-time-code\n' > "$work/code"
printf 'bearer\n' > "$work/credential"
chmod 600 "$work/code" "$work/credential"
export CURL_MARKER="$work/curl-called"
export CURL_ARGS_FILE="$work/curl-args"
export PATH="$work/bin:$PATH"

for script in enrol-host-principal.sh probe-project-repository.sh; do
    for url in \
        http://localhost.evil.example \
        http://localhost@evil.example \
        http://127.0.0.1.evil.example \
        'http://[::1].evil.example' \
        http://example.com \
        'https://localhost@evil.example' \
        'https://example.com/path'; do
        rm -f "$CURL_MARKER"
        if [ "$script" = enrol-host-principal.sh ]; then
            args=("$url" installation "$work/code" "$work/new-credential")
        else
            args=("$url" runner project "$work/credential")
        fi
        if "$repo/scripts/$script" "${args[@]}" > "$work/stdout" 2> "$work/stderr"; then
            echo "$script accepted $url" >&2
            exit 1
        fi
        [ ! -e "$CURL_MARKER" ] || { echo "$script sent a credential to $url" >&2; exit 1; }
    done
    for url in https://example.com http://localhost:5031 http://127.0.0.1:5031 'http://[::1]:5031'; do
        rm -f "$CURL_MARKER"
        if [ "$script" = enrol-host-principal.sh ]; then
            args=("$url" installation "$work/code" "$work/new-credential")
        else
            args=("$url" runner project "$work/credential")
        fi
        "$repo/scripts/$script" "${args[@]}" > "$work/stdout" 2> "$work/stderr" || true
        [ -e "$CURL_MARKER" ] || { echo "$script rejected permitted origin $url" >&2; exit 1; }
        [ "$(head -n 1 "$CURL_ARGS_FILE")" = -q ] || { echo "$script read curl user config" >&2; exit 1; }
        if [[ "$url" == http://* ]]; then
            grep -Fxq -- '--noproxy' "$CURL_ARGS_FILE" || { echo "$script allowed a loopback proxy" >&2; exit 1; }
            grep -Fxq -- '*' "$CURL_ARGS_FILE" || { echo "$script allowed a loopback proxy" >&2; exit 1; }
        fi
    done
done
echo 'task-server-url: pass'
