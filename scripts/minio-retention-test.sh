#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container="agent-studio-retention-minio-${GITHUB_RUN_ID:-local}-$$"
network="${container}-net"
port="${MINIO_TEST_PORT:-19000}"

cleanup()
{
    docker rm --force "$container" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT HUP INT TERM

docker network create "$network" >/dev/null
docker run --detach --name "$container" --network "$network" \
    -p "127.0.0.1:${port}:9000" \
    -e MINIO_ROOT_USER=minioadmin -e MINIO_ROOT_PASSWORD=minioadmin \
    minio/minio:RELEASE.2025-09-07T16-13-09Z server /data >/dev/null

for _ in $(seq 1 60); do
    curl --fail --silent "http://127.0.0.1:${port}/minio/health/ready" >/dev/null 2>&1 && break
    sleep 1
done
curl --fail --silent "http://127.0.0.1:${port}/minio/health/ready" >/dev/null
docker run --rm --network "$network" --entrypoint /bin/sh minio/mc:RELEASE.2025-08-13T08-35-41Z -c \
    "mc alias set local http://${container}:9000 minioadmin minioadmin && mc mb --ignore-existing local/agent-studio-archive" >/dev/null

cd "$repo_root"
export MINIO_ENDPOINT="http://127.0.0.1:${port}"
export MINIO_BUCKET=agent-studio-archive
export MINIO_ACCESS_KEY=minioadmin
export MINIO_SECRET_KEY=minioadmin
dotnet test retention.Tests/AgentStudio.Retention.Tests.csproj --filter 'FullyQualifiedName~S3_target'
dotnet test task-server.Tests/TaskServer.Tests.csproj --filter 'FullyQualifiedName~configured_MinIO'
