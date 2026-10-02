#!/usr/bin/env bash
# Exercise both installation entry points in an empty checkout projection.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT
for entry in compose-distributed-bootstrap.sh compose-runner-bootstrap.sh; do
    install="$fixture/$entry"
    mkdir -p "$install/scripts"
    cp "$repo_root/scripts/compose-distributed-bootstrap.sh" "$repo_root/scripts/compose-runner-bootstrap.sh" "$install/scripts/"
    cp "$repo_root/.env.example" "$repo_root/runner.env.template" "$repo_root/docker-compose.yml" "$install/"
    # Invoke outside the installation directory, as the compatibility command allows.
    (cd /tmp && "$install/scripts/$entry")
    test -s "$install/.env"
    test -s "$install/runner.env"
    test "$(stat -c %a "$install/.env")" = 600
    test "$(stat -c %a "$install/runner.env")" = 600
    printf '\nANTHROPIC_API_KEY=fixture-only-not-a-secret\nRUNNER_MAX_PARALLELISM=3\nRUNNER_ROLE=wrong-role\nRUNNER_ID=wrong-id\nRUNNER_SERVER_URL=http://wrong-authority\n' >> "$install/runner.env"
    before="$(sha256sum "$install/.env" "$install/runner.env")"
    (cd /tmp && "$install/scripts/$entry")
    test "$before" = "$(sha256sum "$install/.env" "$install/runner.env")"
    docker compose --project-directory "$install" -f "$install/docker-compose.yml" --profile dev config --format json |
        jq -e 'all(.services | to_entries[] | select(.key | startswith("agent-host-"));
            .value.environment.ANTHROPIC_API_KEY == "fixture-only-not-a-secret" and
            .value.environment.RUNNER_MAX_PARALLELISM == "3" and
            (.value.environment.RUNNER_SERVER_URL | startswith("http://task-server")) and
            .value.environment.RUNNER_ID != "wrong-id" and
            .value.environment.RUNNER_ROLE == (if (.key | contains("review")) then "review" else "coding" end))' >/dev/null
    test ! -e "$install/runner.token"
done
printf 'compose-bootstrap-contract=passed\n'
