# syntax=docker/dockerfile:1

FROM node:22-bookworm-slim AS frontend
WORKDIR /src/Nostos.Frontend
COPY Nostos.Frontend/package*.json ./
RUN npm ci
COPY Nostos.Frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY Nostos.Backend/Nostos.Backend.csproj Nostos.Backend/
COPY Nostos.Product/Nostos.Product.csproj Nostos.Product/
COPY Nostos.Shared/Nostos.Shared.csproj Nostos.Shared/
RUN dotnet restore Nostos.Backend/Nostos.Backend.csproj
COPY Nostos.Backend/ Nostos.Backend/
COPY Nostos.Product/ Nostos.Product/
COPY Nostos.Shared/ Nostos.Shared/
COPY --from=frontend /src/Nostos.Frontend/dist/Nostos.Frontend/browser/ Nostos.Backend/wwwroot/
# The commit this image is built from, stamped into the assemblies'
# informational version (1.0.0+<sha>). Declared here, after restore, so a new
# revision only rebuilds the publish layer.
ARG SOURCE_REVISION=""
RUN dotnet publish Nostos.Backend/Nostos.Backend.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    -p:SkipFrontendBuild=true \
    -p:UseAppHost=false \
    -p:SourceRevisionId="$SOURCE_REVISION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
# Installed before the application is copied in, so a source change reuses
# this layer instead of installing ffmpeg again.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /tmp/nostos \
    && chown -R "$APP_UID:$APP_UID" /app /data /tmp/nostos
COPY --chown=$APP_UID:$APP_UID --from=build /app/publish/ ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Persistence__DatabasePath=/data/nostos.db \
    DataProtection__KeysPath=/data/keys \
    Storage__BooksRoot=/data/books \
    Storage__BackupsRoot=/data/backups \
    ExternalMedia__Root=/external/media \
    TMPDIR=/tmp/nostos
EXPOSE 8080
VOLUME ["/data"]
USER $APP_UID
ENTRYPOINT ["dotnet", "Nostos.Backend.dll"]
