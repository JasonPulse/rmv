# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build
#
# Pinned to BUILDPLATFORM, so the SDK always runs natively on the builder and
# cross-publishes for the target via `-a $TARGETARCH`. Without the pin, buildx
# would run the whole SDK under QEMU for the non-native architecture, which for
# .NET is many times slower.
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
# No RUN steps here on purpose. Anything executed in this stage would need QEMU
# emulation for the non-native architecture, which is what made the multi-arch
# build slow; the only RUN was installing curl for a Docker HEALTHCHECK, and
# Kubernetes uses httpGet probes against /healthz/live instead.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Posts ship with the image so the news section works on a deployment with no
# volume. A read-only mount at /app/content replaces this directory, which is what
# makes posting a file copy rather than a rebuild. See content/README.md.
#
# A COPY, not a RUN: the runtime stage stays free of anything needing QEMU for the
# non-native architecture.
COPY content/ ./content/

# Stamped at build time so the running site can report which commit it is.
ARG BUILD_VERSION=local
ENV Build__Version=$BUILD_VERSION \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0

EXPOSE 8080

# APP_UID is defined by the base image (non-root, uid 64198).
USER $APP_UID

ENTRYPOINT ["dotnet", "Rmv.Web.dll"]
