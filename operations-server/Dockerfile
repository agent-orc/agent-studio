FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
COPY . .
RUN dotnet publish operations-server/OperationsServer.csproj -c Release -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/operations /run/operations/principals /run/operations/agent /run/operations/client \
 && chown -R 1654:1654 /var/lib/operations /run/operations
USER 1654:1654
ENV ASPNETCORE_URLS=http://+:5081
ENTRYPOINT ["dotnet", "operations-server.dll"]
