# syntax=docker/dockerfile:1

# Layer order is chosen for build caching, slowest-changing first:
#   1. runtime-base: Chromium + .NET runtime + user (depends on nothing from the app: cached across every code change)
#   2. build: NuGet restore from the project files only (cached until a dependency changes)
#   3. build: the sources, publish
#   4. final: runtime-base + the published app, copied last

# ---- the .NET runtime, copied into the Debian image below ----
# The aspnet image is Ubuntu, where chromium is only a snap stub; Debian trixie ships a real chromium package.
# There is no Debian-tagged aspnet image for 11 preview, so the runtime is taken from the Ubuntu one (self-contained
# under /usr/share/dotnet) and run on trixie, which has the libicu/libssl it needs.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.3 AS dotnet-runtime

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
        chromium fonts-liberation fonts-dejavu-core \
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
FROM mcr.microsoft.com/dotnet/sdk:11.0.100-preview.3 AS build
WORKDIR /src
# Restore layer: only what decides the package set (project file, repo-wide props with the analyzer packages, SDK pin),
# so it is cached until a dependency changes.
COPY global.json Directory.Build.props ./
COPY src/OpenBudget/OpenBudget.csproj src/OpenBudget/
RUN dotnet restore src/OpenBudget/OpenBudget.csproj
COPY . .
# No --no-restore: the SDK adds the Blazor framework assets package once .razor files are present, which they are not
# during the cached restore above (that second restore is quick: the packages are already in the layer).
RUN dotnet publish src/OpenBudget/OpenBudget.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- final ----
FROM runtime-base AS final
EXPOSE 8080
WORKDIR /app
VOLUME ["/app/data"]
COPY --from=build /app/publish .

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

# Drop root: the app only needs to read its published files and write /app/data. Chromium runs as this user too.
USER app
ENTRYPOINT ["dotnet", "OpenBudget.dll"]
