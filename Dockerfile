FROM mcr.microsoft.com/dotnet/aspnet:11.0-preview AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
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

# /app/data holds the SQLite files (moneymoney.db, budgets.db). Mount as a volume.
VOLUME ["/app/data"]

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "OpenBudget.dll"]
