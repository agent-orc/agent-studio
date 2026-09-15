#!/usr/bin/env bash
# AGT-2831 - measure whether concurrent review workspaces share a .NET build server.
#
# The Remote Review Executor roots every writable path of an attempt under the
# attempt directory (HOME, TMPDIR, package caches). That does not fence the .NET
# build servers: on Linux, Roslyn's VBCSCompiler listens on /tmp/<pipename> and
# reusable MSBuild worker nodes on /tmp/MSBuild<pid>, both host-global for one
# user and one SDK. A server started by one attempt outlives it, keeps that
# attempt's working directory, and answers later attempts from a deleted tree -
# the shape that deadlocked agent-runner-01-review at RUNNER_MAX_PARALLELISM=4.
#
# This probe runs N concurrent attempt-shaped builds twice, once without the
# fence and once with the fence the review executor now applies, and counts the
# host-global servers each mode leaves behind. Servers are attributed to this
# probe by an inherited tag (MSBuild nodes) and by working directory (compiler
# servers), so unrelated builds on the same host cannot flatter or spoil the
# measurement.
#
# Usage: scripts/review-build-server-isolation-probe.sh [workers]
# Writes a report to $JOB_RESULTS_DIR/review-build-server-isolation.txt when set.
set -euo pipefail

workers="${1:-4}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
probe_root="$(mktemp -d "${TMPDIR:-/tmp}/agent-review-isolation-probe.XXXXXX")"
probe_tag="agt2831-$$-$(date +%s)"
cleanup() {
    # Only ever reap processes this probe tagged itself.
    for pid in $(tagged_pids "$probe_tag"); do kill -9 "$pid" 2>/dev/null || true; done
    rm -rf -- "$probe_root"
}
trap cleanup EXIT

report="${JOB_RESULTS_DIR:-$probe_root}/review-build-server-isolation.txt"
mkdir -p "$(dirname "$report")"
say() { printf '%s\n' "$*" | tee -a "$report"; }

command -v dotnet >/dev/null 2>&1 || { echo "dotnet is required" >&2; exit 2; }
[ "$(uname -s)" = "Linux" ] || { echo "this probe reads /proc and /tmp; Linux only" >&2; exit 2; }

# Live processes that inherited our tag. MSBuild worker nodes inherit the
# client's environment, so a node still carrying the tag after the client exited
# is a node that outlived its attempt and is adoptable by the next one.
tagged_pids() {
    local tag="$1" pid
    for pid in $(ps -eo pid= 2>/dev/null); do
        grep -qz "AGENT_REVIEW_PROBE_TAG=$tag" "/proc/$pid/environ" 2>/dev/null || continue
        printf '%s\n' "$pid"
    done
}

tagged_nodes() {
    local pid
    for pid in $(tagged_pids "$1"); do
        ps -p "$pid" -o args= 2>/dev/null | grep -qF '/nodemode:' || continue
        printf '%s\n' "$pid"
    done
}

# Compiler servers are started detached with a scrubbed environment, so they are
# attributed by the working directory they inherited from their first client.
attempt_owned_compilers() {
    local root="$1" pid cwd
    for pid in $(ps -eo pid,args 2>/dev/null | grep -F 'VBCSCompiler' | grep -v grep | awk '{print $1}'); do
        cwd="$(readlink "/proc/$pid/cwd" 2>/dev/null || true)"
        case "$cwd" in "$root"*) printf '%s\n' "$pid" ;; esac
    done
}

compiler_sockets() {
    ps -eo args= 2>/dev/null | grep -F 'VBCSCompiler' | grep -v grep \
        | sed -n 's/.*-pipename:\([^ ]*\).*/\1/p' | sort -u
}

# The root-cause fact, and the one that does not depend on what else the host
# was building: the rendezvous points the attempts just used are host-global,
# so a per-attempt TMPDIR cannot separate two concurrent attempts.
rendezvous_report() {
    local mode_root="$1" attempt_sockets host_sockets
    host_sockets="$(ls /tmp 2>/dev/null | grep -c '^MSBuild[0-9]' || true)"
    attempt_sockets="$(find "$mode_root" -maxdepth 3 -name 'MSBuild*' -o -maxdepth 3 -name 'CoreFxPipe_*' 2>/dev/null | grep -c . || true)"
    say "  build-server rendezvous points:"
    say "    under /tmp (reachable by every attempt on this host): $host_sockets MSBuild socket(s), $(compiler_sockets | grep -c . || true) compiler socket(s)"
    say "    under the attempts' own TMPDIR: $attempt_sockets"
    say "    reusable MSBuild nodes any attempt may adopt right now: $(ps -eo args= 2>/dev/null | grep -F 'MSBuild.dll' | grep -cF '/nodemode:' || true)"
}

seed_attempt() {
    local dir="$1"
    mkdir -p "$dir/repository" "$dir/tmp" "$dir/home" "$dir/cache"
    cat >"$dir/repository/probe.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
EOF
    printf 'class Probe { static void Main() {} }\n' >"$dir/repository/Probe.cs"
}

run_mode() {
    local mode="$1" tag="$probe_tag-$1" index attempt pid
    local -a pids=()
    local mode_root="$probe_root/$mode"
    local leaked_nodes owned_compilers sockets_before sockets_after

    sockets_before="$(compiler_sockets)"
    for index in $(seq 1 "$workers"); do
        attempt="$mode_root/attempt-$index"
        seed_attempt "$attempt"
        (
            if [ "$mode" = "isolated" ]; then
                # The fence RemoteReviewWorkspace.ProcessEnvironment now applies
                # to every review command (see runner/ReviewBuildServerIsolation.cs).
                export MSBUILDDISABLENODEREUSE=1
                export DOTNET_CLI_USE_MSBUILD_SERVER=0
                export UseSharedCompilation=false
                export MSBUILDDEBUGPATH="$attempt/tmp"
            fi
            export AGENT_REVIEW_PROBE_TAG="$tag"
            export HOME="$attempt/home"
            export TMPDIR="$attempt/tmp" TMP="$attempt/tmp" TEMP="$attempt/tmp"
            export NUGET_PACKAGES="$attempt/cache/nuget"
            export DOTNET_CLI_HOME="$attempt/cache/dotnet"
            export XDG_CACHE_HOME="$attempt/cache"
            cd "$attempt/repository"
            dotnet build probe.csproj -v:q --nologo >"$attempt/build.log" 2>&1
        ) &
        pids+=("$!")
    done
    for pid in "${pids[@]}"; do wait "$pid" || say "  worker pid $pid failed (see $mode_root/*/build.log)"; done

    leaked_nodes="$(tagged_nodes "$tag" | grep -c . || true)"
    owned_compilers="$(attempt_owned_compilers "$mode_root" | grep -c . || true)"
    sockets_after="$(compiler_sockets)"

    say "mode: $mode"
    say "  MSBuild worker nodes that outlived their attempt: $leaked_nodes"
    say "  compiler servers holding an attempt working directory: $owned_compilers"
    say "  distinct compiler sockets in /tmp before/after: $(printf '%s\n' "$sockets_before" | grep -c . || true)/$(printf '%s\n' "$sockets_after" | grep -c . || true)"
    if [ "$mode" = "isolated" ]; then
        if [ "$leaked_nodes" -eq 0 ] && [ "$owned_compilers" -eq 0 ]; then
            say "  verdict: PASS - every build namespace died with its build"
        else
            say "  verdict: FAIL - the fence did not stop server reuse"
        fi
    else
        say "  verdict: $leaked_nodes node(s) left behind for a later attempt to adopt"
        say "           (zero only means the host pool below already satisfied them)"
        rendezvous_report "$mode_root"
    fi
    say ""
    # Nodes from this mode must not be inherited by the next mode's measurement.
    for pid in $(tagged_pids "$tag"); do kill -9 "$pid" 2>/dev/null || true; done
}

: >"$report"
say "review build-server isolation probe"
say "repository: $repo_root"
say "workers:    $workers"
say "sdk:        $(dotnet --version)"
say "date:       $(date -u +%Y-%m-%dT%H:%M:%SZ)"
say ""
run_mode shared
run_mode isolated
say "Reusable MSBuild nodes and the Roslyn compiler server are addressed through"
say "host-global /tmp paths, so a per-attempt TMPDIR does not separate them."
say "The isolated mode is what the review executor applies to every attempt"
say "command, in both the candidate and the baseline workspace."
say ""
say "report: $report"
