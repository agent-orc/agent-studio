FROM mcr.microsoft.com/dotnet/sdk:10.0.301@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src
COPY . .
RUN dotnet publish operations-server/OperationsServer.csproj -c Release -o /app /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /var/lib/operations /run/operations/principals /run/operations/agent /run/operations/client \
 && chown -R 1654:1654 /var/lib/operations /run/operations
USER 1654:1654
ENV ASPNETCORE_URLS=http://+:5081
ENTRYPOINT ["dotnet", "operations-server.dll"]
