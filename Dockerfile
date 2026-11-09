FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-preview.3 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0.100-preview.3 AS build
WORKDIR /src
COPY ["src/OpenBudget/OpenBudget.csproj", "src/OpenBudget/"]
RUN dotnet restore "src/OpenBudget/OpenBudget.csproj"
COPY . .
RUN dotnet build "src/OpenBudget/OpenBudget.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/OpenBudget/OpenBudget.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# curl backs the HEALTHCHECK below; the runtime image doesn't ship it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# /app/data holds the SQLite files (budgets.db). Mount as a volume. Make it
# writable by the non-root app user (APP_UID is defined by the .NET base image).
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
VOLUME ["/app/data"]

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz || exit 1

# Drop root: the app only needs to read its published files and write /app/data.
USER $APP_UID

ENTRYPOINT ["dotnet", "OpenBudget.dll"]
