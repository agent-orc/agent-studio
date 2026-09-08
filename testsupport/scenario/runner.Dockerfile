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
COPY testsupport/scenario/scenario-coding-agent.sh /usr/local/bin/scenario-coding-agent
RUN chmod 0755 /usr/local/bin/scenario-coding-agent
ENTRYPOINT ["dotnet", "/opt/agent-host/agent-host.dll", "--poll"]

