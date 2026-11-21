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

# ReadyToRun: the app and its libraries (EF Core, SqlClient) are compiled to native code here, not by the JIT in every new container.
# The container scales to zero, so every visitor after a quiet period meets a new process, and its first minute is mostly JIT work on
# a fraction of a processor. Measured in a local container held to a quarter of a processor (2026-11-20): the sign-in warm-up 6 to 12 s
# without it, 3 s with it; a demo sign-in made at once 1.8 to 3.5 s without it, 0.8 to 1.0 s with it. It makes the image 8.5 MB (7 %)
# larger. The switch is given to the restore too, which is what fetches the compiler for it.
RUN dotnet restore src/Lifestream.Api/Lifestream.Api.csproj -r linux-x64 -p:PublishReadyToRun=true
RUN dotnet publish src/Lifestream.Api/Lifestream.Api.csproj -c Release -r linux-x64 --self-contained false -o /app --no-restore -p:UseAppHost=false -p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
# Links the GHCR package to the repository, which lets the workflow's GITHUB_TOKEN manage (prune) its versions.
LABEL org.opencontainers.image.source="https://github.com/maiznadeem/Lifestream"
LABEL org.opencontainers.image.title="lifestream-api"
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
# No profile-guided re-optimisation: it instruments the code that is still compiled at run time to collect a profile, which a process
# that lives for minutes never gets to use. Measured with the ReadyToRun image at a quarter of a processor (2026-11-20, 3 and 4 starts):
# listening 2.9 to 3.0 s after the start instead of 3.1 to 3.4 s, and the first dashboard request 0.2 to 0.3 s instead of 0.4 s.
ENV DOTNET_TieredPGO=0
EXPOSE 8080

# Non-root: the aspnet image defines this user.
USER $APP_UID
ENTRYPOINT ["dotnet", "Lifestream.Api.dll"]
