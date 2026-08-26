# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore on its own layer so a source-only change does not re-download NuGet.
COPY src/Rmv.Web/Rmv.Web.csproj src/Rmv.Web/
RUN dotnet restore src/Rmv.Web/Rmv.Web.csproj -a "$TARGETARCH"

COPY src/ src/
RUN dotnet publish src/Rmv.Web/Rmv.Web.csproj \
      -c Release \
      -a "$TARGETARCH" \
      --no-restore \
      -o /app

# ---------------------------------------------------------------------------
# Runtime
#
# Debian-based rather than chiseled on purpose: chiseled has no shell, and being
# able to exec into a misbehaving container on the homelab is worth the extra
# ~20MB. Swap to aspnet:10.0-noble-chiseled if you would rather have the smaller
# attack surface, and drop the curl healthcheck with it.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

# Stamped at build time so the running site can report which commit it is.
ARG BUILD_VERSION=local
ENV Build__Version=$BUILD_VERSION \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0

EXPOSE 8080

# APP_UID is defined by the base image (non-root, uid 64198).
USER $APP_UID

# Ready, not live: the container should not report healthy while Postgres is
# still coming up, or the tunnel will route traffic to a site that cannot query.
HEALTHCHECK --interval=20s --timeout=3s --start-period=25s --retries=3 \
  CMD curl -fsS http://localhost:8080/healthz/ready || exit 1

ENTRYPOINT ["dotnet", "Rmv.Web.dll"]
