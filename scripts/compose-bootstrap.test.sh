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
    docker compose --project-directory "$install" -f "$install/docker-compose.yml" --profile dev config --format json |
        jq -e '
          .services as $services |
          all(["orchestrator-engine", "orchestrator-engine-dev"][]; . as $name |
            [$services[$name].volumes[] | select(.source == "secrets") | .volume.subpath] == ["engine"]) and
          all(["studio-bff", "studio-bff-dev", "orchestrator-api", "orchestrator-api-dev"][]; . as $name |
            [$services[$name].volumes[] | select(.source == "secrets") | .volume.subpath] == ["studio"]) and
          all(["agent-host-distributed", "agent-host-distributed-dev"][]; . as $name |
            [$services[$name].volumes[] | select(.source == "secrets") | .volume.subpath] == ["runner"]) and
          all(["agent-host-review-distributed", "agent-host-review-distributed-dev"][]; . as $name |
            [$services[$name].volumes[] | select(.source == "secrets") | .volume.subpath] == ["review_runner"]) and
          all(["task-server", "task-server-dev", "orchestrator-engine", "studio-bff", "orchestrator-api", "web"][]; . as $name |
            [$services[$name].volumes[]? | .target] | all(.[]; (contains("/home/runner/.claude") or contains("/home/runner/.codex") or contains("/home/runner/.config/gemini")) | not)) and
          all(["agent-host-distributed", "agent-host-review-distributed"][]; . as $name |
            [$services[$name].volumes[] | select(.target == "/home/runner/.claude" or .target == "/home/runner/.codex" or .target == "/home/runner/.config/gemini") | .type] == ["volume", "volume", "volume"]) and
          all(["/home/runner/.claude", "/home/runner/.codex", "/home/runner/.config/gemini"][]; . as $target |
            ([$services["agent-host-distributed"].volumes[] | select(.target == $target) | .source][0]) !=
            ([$services["agent-host-review-distributed"].volumes[] | select(.target == $target) | .source][0]))' >/dev/null
    test ! -e "$install/runner.token"
done
printf 'compose-bootstrap-contract=passed\n'
