# syntax=docker/dockerfile:1

# Layer order is chosen for build caching, slowest-changing first:
#   1. runtime-base: Chromium + .NET runtime + user (depends on nothing from the app: cached across every code change)
#   2. build: NuGet restore from the project files only (cached until a dependency changes)
#   3. build: the sources (src/ only), publish
#   4. final: runtime-base + the published app, copied last

# ---- the .NET runtime, copied into the Debian image below ----
# The aspnet image is Ubuntu, where chromium is only a snap stub; Debian trixie ships a real chromium package.
# There is no Debian-tagged aspnet image for 11 preview, so the runtime is taken from the Ubuntu one (self-contained
# under /usr/share/dotnet) and run on trixie, which has the libicu/libssl it needs.
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS dotnet-runtime

# ---- runtime base: Chromium (for web logins), the .NET runtime, a non-root user ----
FROM debian:trixie-slim AS runtime-base
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
# UID 1654 is what the aspnet images call "app"; existing /app/data volumes belong to it.
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && groupadd --gid 1654 app \
    && useradd --uid 1654 --gid app --create-home --shell /usr/sbin/nologin app
# One RUN, apt lists removed again (the packages' copyright files stay, their licenses ask for it). chromium is the browser web logins stream (only started when someone signs in
# to a bank that needs it); curl backs the HEALTHCHECK; the fonts make bank pages readable.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl tzdata libicu76 libssl3t64 \
        chromium xvfb xauth fonts-liberation fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* /usr/share/man /usr/share/info
# /app/data holds the SQLite files (budgets.db). Mount as a volume; writable by the non-root app user.
RUN mkdir -p /app/data && chown -R app:app /app/data
ENV HOME=/home/app \
    CHROME_PATH=/usr/bin/chromium \
    APP_UID=1654 \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
# Restore layer: only what decides the package set (project file, repo-wide props with the analyzer packages, SDK pin),
# so it is cached until a dependency changes. The NuGet folder is a BuildKit cache mount: when a dependency does change,
# only the new packages are downloaded (the mount outlives the layer; publish below mounts it too).
COPY global.json Directory.Build.props ./
COPY src/OpenBudget/OpenBudget.csproj src/OpenBudget/
RUN --mount=type=cache,id=openbudget-nuget,target=/root/.nuget/packages \
    dotnet restore src/OpenBudget/OpenBudget.csproj
# Only the app's sources: docs, tools and the like (and the tests, see .dockerignore) don't invalidate the publish layer.
COPY src/ src/
# No --no-restore: the SDK adds the Blazor framework assets package once .razor files are present, which they are not
# during the cached restore above (that second restore is quick: the packages are already in the cache mount).
# RunAnalyzers=false: the analyzers (AnalysisMode=All + Roslynator/Sonar/Meziantou) are most of the compile time and
# are enforced by every local/CI build and test run already; compiler warnings still fail the build.
RUN --mount=type=cache,id=openbudget-nuget,target=/root/.nuget/packages \
    dotnet publish src/OpenBudget/OpenBudget.csproj -c Release -o /app/publish /p:UseAppHost=false /p:RunAnalyzers=false

# ---- final ----
FROM runtime-base AS final
LABEL org.opencontainers.image.title="OpenBudget" \
      org.opencontainers.image.description="Self-hosted personal finance app with bank sync, budgets and cashflow projections."
EXPOSE 8080
WORKDIR /app
VOLUME ["/app/data"]
COPY --from=build /app/publish .
# Which build this is, for the About page; builds that don't pass them show the version as unknown.
ARG VERSION="" REVISION="" BUILD_DATE=""
ENV OPENBUDGET_VERSION=$VERSION OPENBUDGET_REVISION=$REVISION OPENBUDGET_BUILD_DATE=$BUILD_DATE

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

# Drop root: the app only needs to read its published files and write /app/data. Chromium runs as this user too.
USER app
ENTRYPOINT ["dotnet", "OpenBudget.dll"]
