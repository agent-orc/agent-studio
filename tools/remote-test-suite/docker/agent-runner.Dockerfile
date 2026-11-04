FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN --mount=type=secret,id=sixlabors_license \
    dotnet publish runner/AgentRunner.csproj -c Release -o /agent-host /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0
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
