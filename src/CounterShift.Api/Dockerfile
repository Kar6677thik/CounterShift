# syntax=docker/dockerfile:1
# Build context is the repository root (it needs global.json and the Directory.*.props files).

# ---- build stage: full SDK, thrown away afterwards -------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
# Restore from the project files alone first: this layer stays cached until dependencies change,
# so editing a .cs file doesn't re-download packages.
COPY src/CounterShift.Domain/CounterShift.Domain.csproj src/CounterShift.Domain/
COPY src/CounterShift.Storage/CounterShift.Storage.csproj src/CounterShift.Storage/
COPY src/CounterShift.Storage.Postgres/CounterShift.Storage.Postgres.csproj src/CounterShift.Storage.Postgres/
COPY src/CounterShift.Storage.Redis/CounterShift.Storage.Redis.csproj src/CounterShift.Storage.Redis/
COPY src/CounterShift.Api/CounterShift.Api.csproj src/CounterShift.Api/
RUN dotnet restore src/CounterShift.Api/CounterShift.Api.csproj
COPY src/ src/
RUN dotnet publish src/CounterShift.Api/CounterShift.Api.csproj -c Release -o /app --no-restore

# ---- runtime stage: ASP.NET runtime only, no compiler or SDK -----------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The base image ships a non-root "app" user; never run the service as root.
USER $APP_UID
EXPOSE 8080
# The same binary also runs the backfill: docker run <image> backfill --from postgres --to redis ...
ENTRYPOINT ["dotnet", "CounterShift.Api.dll"]
