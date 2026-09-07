# Orchestrator engine (headless worker against the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f orchestrator-engine/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish orchestrator-engine/OrchestratorEngine.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENV SERVER_URL=http://task-server:5071
ENTRYPOINT ["dotnet", "orchestrator-engine.dll"]
