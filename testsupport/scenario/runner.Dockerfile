FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish runner/AgentRunner.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system safe.directory '*'
WORKDIR /opt/agent-host
COPY --from=build /app ./
COPY testsupport/scenario/fake-coding-cli.sh /opt/scenario/scenario-coding-agent.sh
COPY testsupport/scenario/scenario-runner-entrypoint.sh /opt/scenario/entrypoint.sh
RUN chmod 0755 /opt/scenario/scenario-coding-agent.sh /opt/scenario/entrypoint.sh
ENTRYPOINT ["/opt/scenario/entrypoint.sh"]
