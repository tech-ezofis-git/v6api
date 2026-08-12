# Ezofis V6 API - ASP.NET Core Web API (.NET 8)
# Multi-stage build: SDK image builds/publishes, slim ASP.NET runtime image serves.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the whole source tree (modular monolith: many project references across
# src/BuildingBlocks, src/Modules, src/Api, src/Workers) and the top-level
# scripts/ folder (referenced by relative path at runtime by several services,
# e.g. WorkflowSchemaService.cs / FormService.cs load scripts/postgres/*.sql).
COPY . .

RUN dotnet restore src/Api/SaaSApp.Api.csproj
RUN dotnet publish src/Api/SaaSApp.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "SaaSApp.Api.dll"]
