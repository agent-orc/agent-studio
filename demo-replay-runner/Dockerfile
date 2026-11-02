# Replay-only Runner for the public demo instance (AGT-W34 slice S3).
#
# Build from the repository root:
#   docker build -f demo-replay-runner/Dockerfile -t demo-replay-runner .
#
# The image deliberately contains no coding CLI, no Node.js, no Git, no
# repository checkout, no Docker socket, and no credential. The runtime base
# image is the plain .NET runtime, not the SDK, so nothing here can build,
# clone, or execute project code. The replay credential and the trace are
# mounted read-only at deploy time and never baked into a layer.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . ./
RUN dotnet publish demo-replay-runner/DemoReplayRunner.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30
WORKDIR /opt/demo-replay
COPY --from=build /app ./

# Unprivileged service account. The process owns nothing writable in the image.
RUN useradd --system --create-home --shell /usr/sbin/nologin demo-replay \
    && mkdir -p /opt/demo-replay/trace \
    && chown -R root:root /opt/demo-replay
USER demo-replay

ENV DEMO_REPLAY_SERVER_URL=http://orchestrator-api:5031 \
    DEMO_REPLAY_TRACE_FILE=/opt/demo-replay/trace/replay-trace.json \
    DEMO_REPLAY_RUNNER_ID=demo-runner-replay \
    DOTNET_EnableDiagnostics=0

# Egress is default-deny at the network layer on the demo VM. The process
# enforces the same allowlist internally: one origin, two paths.
HEALTHCHECK --interval=60s --timeout=10s --start-period=15s --retries=3 \
    CMD ["dotnet", "/opt/demo-replay/demo-replay-runner.dll", "--health-check"]

ENTRYPOINT ["dotnet", "/opt/demo-replay/demo-replay-runner.dll"]
