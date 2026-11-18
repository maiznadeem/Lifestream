# Lifestream API image. Build from the repository root:
#   docker buildx build --provenance=false --sbom=false --platform linux/amd64 -f src/Lifestream.Api/Dockerfile -t <tag> .
# Single architecture, no attestation manifests, so pruning old versions in GHCR cannot orphan a manifest.
# The image holds no credential: configuration (connection string, signing key) arrives as environment variables from
# Azure Container Apps, and the database is reached with the app's managed identity.

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

# Only what the API compiles (Api -> Data -> Domain, Contracts). Tests, the desktop client and docs stay out of the context.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Lifestream.Domain src/Lifestream.Domain
COPY src/Lifestream.Contracts src/Lifestream.Contracts
COPY src/Lifestream.Data src/Lifestream.Data
COPY src/Lifestream.Api src/Lifestream.Api

RUN dotnet restore src/Lifestream.Api/Lifestream.Api.csproj
RUN dotnet publish src/Lifestream.Api/Lifestream.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
# Links the GHCR package to the repository, which lets the workflow's GITHUB_TOKEN manage (prune) its versions.
LABEL org.opencontainers.image.source="https://github.com/MaizNadeem/Lifestream-WPF"
LABEL org.opencontainers.image.title="lifestream-api"
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Non-root: the aspnet image defines this user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Lifestream.Api.dll"]
