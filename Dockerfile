# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PullPatrol.Api/PullPatrol.Api.csproj src/PullPatrol.Api/
RUN dotnet restore src/PullPatrol.Api/PullPatrol.Api.csproj
COPY src/ src/
RUN dotnet publish src/PullPatrol.Api/PullPatrol.Api.csproj -c Release -o /app --no-restore

# Runtime stage: small image, runs as the built-in non-root user
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PullPatrol.Api.dll"]
