# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as a distinct layer for better layer caching
COPY OpenBudget.csproj ./
RUN dotnet restore

# Copy the rest and publish
COPY . .
RUN dotnet publish OpenBudget.csproj -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

# /app/data holds the SQLite files (moneymoney.db, budgets.db). Mount as a volume.
VOLUME ["/app/data"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "OpenBudget.dll"]
