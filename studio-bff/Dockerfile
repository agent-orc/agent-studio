# Studio BFF (reverse proxy in front of the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f studio-bff/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# VERSION is OCI label material only; the assembly version always comes from the
# repository VERSION file through Directory.Build.props. REVISION replaces the
# csproj git probe, which cannot run because .dockerignore excludes .git.
ARG VERSION=
ARG REVISION=unknown
WORKDIR /src
COPY . ./
RUN test -z "$VERSION" || test "$VERSION" = "$(tr -d '\r\n' < VERSION)"
RUN dotnet publish studio-bff/StudioBff.csproj -c Release -o /app \
        /p:UseAppHost=false \
        /p:SourceRevisionId="$REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=
ARG REVISION=unknown
LABEL org.opencontainers.image.title="agent-studio-bff" \
      org.opencontainers.image.description="Agent Studio same-origin proxy in front of the Task Server" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio" \
      org.opencontainers.image.vendor="agent-orc" \
      org.opencontainers.image.licenses="Apache-2.0"
# curl: the HEALTHCHECK below is the container's readiness contract.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5072 \
    TaskServer__BaseUrl=http://task-server:5071
# Bounded logs need both halves: the image caps severity, the Compose json-file
# driver caps retained bytes. Either one alone still fills a long-running host.
ENV Logging__LogLevel__Default=Information
EXPOSE 5072
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=5 \
    CMD curl --fail --silent http://127.0.0.1:5072/healthz || exit 1
# AssemblyName is `agent-studio-bff` (studio-bff/StudioBff.csproj), not StudioBff.
ENTRYPOINT ["dotnet", "agent-studio-bff.dll"]
