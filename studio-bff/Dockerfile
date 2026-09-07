# Studio BFF (reverse proxy in front of the Task Server) - target-architecture component.
# Build context is the repository root: docker build -f studio-bff/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
WORKDIR /src
COPY . ./
RUN dotnet publish studio-bff/StudioBff.csproj -c Release -o /app /p:UseAppHost=false \
    -p:Version="$VERSION" -p:SourceRevisionId="$COMMIT"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=0.0.0-local
ARG COMMIT=unknown
ARG BUILD_IDENTITY=agent-studio-bff/0.0.0-local+sha.unknown
LABEL org.opencontainers.image.title="Agent Studio BFF" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$COMMIT" \
      io.agent-studio.build-identity="$BUILD_IDENTITY"
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --system --gid 10001 agent-studio \
 && useradd --system --uid 10001 --gid agent-studio --home-dir /nonexistent --shell /usr/sbin/nologin agent-studio
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5072 \
    TaskServer__BaseUrl=http://task-server:5071
EXPOSE 5072
USER 10001:10001
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:5072/healthz >/dev/null || exit 1
ENTRYPOINT ["dotnet", "StudioBff.dll"]
