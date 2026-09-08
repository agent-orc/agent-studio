# Studio BFF (reverse proxy in front of the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f studio-bff/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./
RUN dotnet publish studio-bff/StudioBff.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# The appsettings Urls key outranks ASPNETCORE_URLS in the minimal-host
# configuration stack. Override that exact key so the BFF is reachable through
# the container's published port while the host binding remains loopback-only.
ENV URLS=http://0.0.0.0:5072 \
    TaskServer__BaseUrl=http://task-server:5071
EXPOSE 5072
ENTRYPOINT ["dotnet", "agent-studio-bff.dll"]
