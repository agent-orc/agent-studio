# Studio BFF (reverse proxy in front of the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f studio-bff/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
ARG SHA=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish studio-bff/StudioBff.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$SHA"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-dev
ARG SHA=unknown
LABEL org.opencontainers.image.title="agent-studio-bff" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$SHA" \
      org.opencontainers.image.source="https://github.com/agent-orc/agent-studio"
# curl: HEALTHCHECK reads the process's own open /healthz route.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 app \
 && useradd --system --uid 10001 --gid app --home-dir /nonexistent --shell /usr/sbin/nologin app
WORKDIR /app
COPY --from=build /app ./
RUN chown -R app:app /app
ENV ASPNETCORE_URLS=http://0.0.0.0:5072 \
    TaskServer__BaseUrl=http://task-server:5071
EXPOSE 5072
USER app
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=24 \
    CMD curl --fail --silent http://127.0.0.1:5072/healthz || exit 1
ENTRYPOINT ["dotnet", "StudioBff.dll"]
