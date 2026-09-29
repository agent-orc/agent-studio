FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
COPY . .
RUN dotnet publish operations-agent/OperationsAgent.csproj -c Release -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/operations-agent && chown 1654:1654 /var/lib/operations-agent
USER 1654:1654
ENTRYPOINT ["dotnet", "operations-agent.dll"]
