# Standalone Task Server (v1 run/review plane) - target-architecture component.
# Build context is the repository root: docker build -f task-server/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish task-server/TaskServer.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# Same bootstrap variables as deploy/release/agent-orchestrator/config/server.env.template.
ENV LISTEN_URL=http://0.0.0.0:5071 \
    STORE_PATH=/var/lib/agent-orchestrator/store \
    BACKUP_PATH=/var/lib/agent-orchestrator/backup
VOLUME ["/var/lib/agent-orchestrator"]
EXPOSE 5071
ENTRYPOINT ["dotnet", "task-server.dll"]
