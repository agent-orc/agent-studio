#!/usr/bin/env bash
# Exercise the I05 host scripts against a fake Task Server. Enrolment: a clean
# install, a credential file that appears during the exchange, and a response
# without a credential; only a real install may delete the code. Probe: a
# missing credential file or registration field stops before any receipt.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin" "$work/host"
cat > "$work/bin/curl" <<'EOF'
#!/usr/bin/env bash
output="" url=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) output="$2"; shift ;;
        -w|-H|--data-binary|--noproxy) shift ;;
        http*) url="$1" ;;
    esac
    shift
done
case "$url" in
    */api/v1/installation) printf '{"installationId":"ins_test"}\n' ;;
    */api/v1/projects/*/repository) printf '{"registration":{"integrationRef":"develop"}}\n' ;;
    */project-probes/*) touch "$FAKE_PROBE_POSTED"; printf '{}\n' ;;
    */api/v1/enrolments/exchange)
        case "$FAKE_MODE" in
            collide)
                # Another process installs a credential while the exchange runs.
                printf 'other-credential\n' > "$FAKE_TARGET"
                printf '{"issued":{"principal":{"principalId":"runner:h"},"credential":"issued-credential"}}' > "$output" ;;
            no-credential)
                printf '{"issued":{"principal":{"principalId":"runner:h"}}}' > "$output" ;;
            *)
                printf '{"issued":{"principal":{"principalId":"runner:h"},"credential":"issued-credential"}}' > "$output" ;;
        esac
        printf '201' ;;
    *) exit 22 ;;
esac
EOF
chmod +x "$work/bin/curl"
export PATH="$work/bin:$PATH"
export FAKE_TARGET="$work/host/credential"

enrol() {
    printf 'enr_one-time-code\n' > "$work/host/code"
    chmod 600 "$work/host/code"
    rm -f "$FAKE_TARGET" "$work/host"/.enrol-credential.*
    FAKE_MODE="$1" "$repo/scripts/enrol-host-principal.sh" http://localhost:5031 ins_test \
        "$work/host/code" "$FAKE_TARGET" > "$work/stdout" 2> "$work/stderr"
}

enrol ok
[ "$(cat "$FAKE_TARGET")" = issued-credential ] || { echo "credential not installed" >&2; exit 1; }
[ "$(stat -c %a "$FAKE_TARGET")" = 600 ] || { echo "credential not mode 600" >&2; exit 1; }
[ ! -e "$work/host/code" ] || { echo "spent code kept after install" >&2; exit 1; }
! compgen -G "$work/host/.enrol-credential.*" > /dev/null || { echo "staged credential left behind" >&2; exit 1; }
grep -q '^enrolled principal=runner:h ' "$work/stdout" || { echo "success not reported" >&2; exit 1; }
! grep -rq issued-credential "$work/stdout" "$work/stderr" || { echo "credential printed" >&2; exit 1; }

if enrol collide; then echo "collision reported success" >&2; exit 1; fi
[ "$(cat "$FAKE_TARGET")" = other-credential ] || { echo "collision replaced the other credential" >&2; exit 1; }
[ -e "$work/host/code" ] || { echo "code deleted without an install" >&2; exit 1; }
! grep -q '^enrolled' "$work/stdout" || { echo "collision printed success" >&2; exit 1; }
staged=("$work/host"/.enrol-credential.*)
[ "$(cat "${staged[0]}")" = issued-credential ] || { echo "issued credential not kept for the operator" >&2; exit 1; }
! grep -rq issued-credential "$work/stdout" "$work/stderr" || { echo "credential printed" >&2; exit 1; }

if enrol no-credential; then echo "missing credential reported success" >&2; exit 1; fi
[ ! -e "$FAKE_TARGET" ] || { echo "missing credential installed a file" >&2; exit 1; }
[ -e "$work/host/code" ] || { echo "code deleted without an install" >&2; exit 1; }

export FAKE_PROBE_POSTED="$work/probe-posted"
printf 'bearer\n' > "$work/host/runner-token"
if "$repo/scripts/probe-project-repository.sh" http://localhost:5031 h prj "$work/host/absent" \
    > "$work/stdout" 2> "$work/stderr"; then echo "probe ran without a credential" >&2; exit 1; fi
if "$repo/scripts/probe-project-repository.sh" http://localhost:5031 h prj "$work/host/runner-token" \
    > "$work/stdout" 2> "$work/stderr"; then echo "probe ran without a registered URL" >&2; exit 1; fi
grep -q 'no registered repository URL' "$work/stderr" || { echo "missing URL not reported" >&2; exit 1; }
[ ! -e "$FAKE_PROBE_POSTED" ] || { echo "probe posted a receipt for a literal null origin" >&2; exit 1; }
echo 'identity-host-scripts: pass'
