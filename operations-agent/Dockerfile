FROM mcr.microsoft.com/dotnet/sdk:10.0.301@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src
COPY . .
RUN dotnet publish operations-agent/OperationsAgent.csproj -c Release -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:a3f9f81954b9c810af2ed18849716c8dc74a4707bd0907fdbd69be81554c0f30
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/operations-agent && chown 1654:1654 /var/lib/operations-agent
USER 1654:1654
ENTRYPOINT ["dotnet", "operations-agent.dll"]
