FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . ./
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /agent-host /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
ARG HARNESS_REVISION=unknown
ARG HARNESS_IDENTITY=unset
LABEL org.opencontainers.image.revision="${HARNESS_REVISION}" \
      com.agentstudio.remote-harness.identity="${HARNESS_IDENTITY}"
RUN apt-get update \
 && apt-get install -y --no-install-recommends nodejs \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /harness
COPY --from=build /agent-host /opt/agent-host/
COPY tools/remote-test-suite/compose-agent.mjs ./
ENTRYPOINT ["node"]
