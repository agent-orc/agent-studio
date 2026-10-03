#!/bin/sh
# AGT-2947 installation upgrade and rollback rehearsals (Dossier AGT-W63 D6,
# A7/B7/C8) against the real update-docker.sh and rollback-docker.sh, using
# tests/fake-control-plane.sh for Compose, Docker and the Task Server. Needs
# a POSIX shell and jq. Prints one "ok"/"FAIL" line per check and exits
# non-zero on any failure.
set -u

HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
SCRIPTS=$(dirname "$HERE")
command -v jq >/dev/null 2>&1 || { echo "skip: jq is required"; exit 77; }

failures=0
check()
{
    name=$1; shift
    if "$@"; then echo "ok   $name"; else echo "FAIL $name"; failures=$((failures + 1)); fi
}
m() { jq -r "$1" "$SIM/config/installation-manifest.json"; }
eq() { [ "$1" = "$2" ] || { echo "     expected '$2', got '$1'" >&2; return 1; }; }

# setup <active-version> <versions...> ; each version is "tag:schema:min:max[:fail]"
trap '[ -n "${SIM:-}" ] && rm -rf "$SIM"' EXIT
setup()
{
    [ -n "${SIM:-}" ] && rm -rf "$SIM"
    SIM=$(mktemp -d)
    export SIM
    mkdir -p "$SIM/bin" "$SIM/images" "$SIM/versions" "$SIM/config" "$SIM/compose"
    for role in compose docker curl; do ln -s "$HERE/fake-control-plane.sh" "$SIM/bin/$role"; done
    active=$1; shift
    for v in "$@"; do
        echo "$v" | awk -F: '{print $2, $3, $4, $5}' >"$SIM/versions/${v%%:*}"
    done
    echo "$active" >"$SIM/running"
    touch "$SIM/images/$active"
    awk '{print $1}' "$SIM/versions/$active" >"$SIM/store-schema"
    echo Normal >"$SIM/mode"
    echo 0 >"$SIM/active-runs"
    echo token >"$SIM/token"
    echo '[]' >"$SIM/hosts.json"
    printf 'CONTROL_PLANE_VERSION=%s\nCONTROL_PLANE_DOMAIN=cp.test\n' "$active" >"$SIM/config/docker.env"
    export AGENT_ORCHESTRATOR_SKIP_ROOT_CHECK=1 AGENT_ORCHESTRATOR_CONFIG_ROOT="$SIM/config" \
        AGENT_ORCHESTRATOR_COMPOSE_ROOT="$SIM/compose" COMPOSE_BIN="$SIM/bin/compose" \
        DOCKER_BIN="$SIM/bin/docker" READY_TIMEOUT_SECONDS=2 DRAIN_TIMEOUT_SECONDS=20
    unset AGENT_ORCHESTRATOR_CANARY_COMMAND
}
update() { sh "$SCRIPTS/update-docker.sh" "$@" >>"$SIM/log" 2>&1; }
rollback() { sh "$SCRIPTS/rollback-docker.sh" "$@" >>"$SIM/log" 2>&1; }
env_version() { sed -n 's/^CONTROL_PLANE_VERSION=//p' "$SIM/config/docker.env"; }
host_json()
{
    printf '{"hostId":"%s","runnerVersion":"r","protocolVersion":%s,"status":"active","lastSeenAt":"%s"}' "$1" "$2" "$3"
}
recent() { date -u +%Y-%m-%dT%H:%M:%S.1234567Z; }

echo "# N-1 to N upgrade with active-run drain and canary"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:1:2
echo 3 >"$SIM/active-runs"
AGENT_ORCHESTRATOR_CANARY_COMMAND=true; export AGENT_ORCHESTRATOR_CANARY_COMMAND
check "update exits 0" update 1.1.0
check "outcome succeeded" eq "$(m .progress.outcome)" succeeded
check "observed version is N" eq "$(m .observed.version)" 1.1.0
check "observed authority digest is the running N image" eq "$(m '.observed.components[0].imageDigest')" "registry.example/agent@sha256:digest-1.1.0"
check "final runtime mode Normal" eq "$(m .observed.runtimeMode)" Normal
check "prior release retained" eq "$(m .prior.version)" 1.0.0
check "verified backup recorded" eq "$(m .backup.verified)" true
check "drain waited for active runs" eq "$(cat "$SIM/active-runs")" 0
check "manifest parses as schema 1" eq "$(m .schemaVersion)" 1

echo "# drain is bounded"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:1:2
echo 5 >"$SIM/active-runs"; touch "$SIM/runs-never-settle"
DRAIN_TIMEOUT_SECONDS=1; export DRAIN_TIMEOUT_SECONDS
check "update refuses after the drain bound" eval '! update 1.1.0'
check "version unchanged" eq "$(env_version)" 1.0.0
check "admission restored" eq "$(cat "$SIM/mode")" Normal
check "no container switched" eq "$(cat "$SIM/events" 2>/dev/null)" ""

echo "# health alone is not success"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:1:2
check "update without canary exits 0" update 1.1.0
check "outcome awaiting-canary" eq "$(m .progress.outcome)" awaiting-canary
AGENT_ORCHESTRATOR_CANARY_COMMAND=true; export AGENT_ORCHESTRATOR_CANARY_COMMAND
check "rerun with canary completes" update 1.1.0
check "outcome succeeded after canary" eq "$(m .progress.outcome)" succeeded
check "only one backup taken" eq "$(cat "$SIM/backup-count")" 1

echo "# candidate boot failure"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:1:2:fail
check "update fails" eval '! update 1.1.0'
check "outcome rolled-back" eq "$(m .progress.outcome)" rolled-back
check "prior version restored" eq "$(env_version)" 1.0.0
check "prior version running" eq "$(cat "$SIM/running")" 1.0.0
check "admission resumed on prior" eq "$(cat "$SIM/mode")" Normal

echo "# candidate fails after a schema change"
setup 1.0.0 1.0.0:24:1:2 1.1.0:25:1:2
AGENT_ORCHESTRATOR_CANARY_COMMAND=false; export AGENT_ORCHESTRATOR_CANARY_COMMAND
check "update fails" eval '! update 1.1.0'
check "outcome awaiting-restore" eq "$(m .progress.outcome)" awaiting-restore
check "no downgrade attempted" eq "$(grep -c 'up 1.0.0' "$SIM/events")" 0
check "installation in Maintenance" eq "$(cat "$SIM/mode")" Maintenance
check "restore names the verified backup" eval 'm .progress.reason | grep -q bk-1'

echo "# interrupted upgrade resumes"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:1:2
AGENT_ORCHESTRATOR_CANARY_COMMAND=true; export AGENT_ORCHESTRATOR_CANARY_COMMAND
touch "$SIM/interrupt-after-up"
check "first run is interrupted" eval '! update 1.1.0'
check "manifest stays in-progress" eq "$(m .progress.outcome)" in-progress
check "a different target is refused while in progress" eval '! update 1.2.0'
check "rerun resumes" update 1.1.0
check "outcome succeeded" eq "$(m .progress.outcome)" succeeded
check "prior is the pre-upgrade release" eq "$(m .prior.version)" 1.0.0
check "resume did not take a second backup" eq "$(cat "$SIM/backup-count")" 1

echo "# offline host stays pending"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:2:3
printf '[%s,%s]' "$(host_json h-online 2 "$(recent)")" "$(host_json h-offline 1 2026-01-01T00:00:00Z)" >"$SIM/hosts.json"
check "update exits 0" update 1.1.0
check "online host current" eq "$(m '.hosts[] | select(.hostId=="h-online") | .state')" current
check "offline host recorded" eq "$(m '.hosts[] | select(.hostId=="h-offline") | .online')" false
check "offline N-1 host stays pending" eq "$(m '.hosts[] | select(.hostId=="h-offline") | .state')" pending

echo "# incompatible protocol"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:3:3
printf '[%s]' "$(host_json h-online 2 "$(recent)")" >"$SIM/hosts.json"
check "update fails" eval '! update 1.1.0'
check "outcome rolled-back" eq "$(m .progress.outcome)" rolled-back
check "reason names the protocol" eval 'm .progress.reason | grep -q protocol'
check "prior running" eq "$(cat "$SIM/running")" 1.0.0

echo "# rollback rehearsal"
setup 1.0.0 1.0.0:24:1:2 1.1.0:24:1:2
check "update exits 0" update 1.1.0
check "rollback exits 0" rollback
check "rollback outcome succeeded" eq "$(m .progress.outcome)" succeeded
check "rollback operation recorded" eq "$(m .progress.operation)" rollback
check "observed version is N-1" eq "$(m .observed.version)" 1.0.0
check "new prior is N" eq "$(m .prior.version)" 1.1.0
check "rollback took a verified backup" eq "$(m .backup.verified)" true

if [ -n "${REHEARSAL_MANIFEST_OUT:-}" ]; then
    printf '[%s]' "$(host_json h-offline 1 2026-01-01T00:00:00Z)" >"$SIM/hosts.json"
    update 1.1.0 >/dev/null 2>&1 || true
    cp "$SIM/config/installation-manifest.json" "$REHEARSAL_MANIFEST_OUT"
fi

echo "# rollback refused after a schema change"
setup 1.0.0 1.0.0:24:1:2 1.1.0:25:1:2
check "update exits 0" update 1.1.0
check "rollback refuses" eval '! rollback'
check "refusal names the schema" eval 'grep -q "store schema is 25" "$SIM/log"'
check "version unchanged" eq "$(env_version)" 1.1.0
check "explicit unrecorded target refused" eval '! rollback 0.9.0'

[ "$failures" -eq 0 ] && echo "all rehearsals passed" || echo "$failures check(s) failed"
[ "$failures" -eq 0 ]
