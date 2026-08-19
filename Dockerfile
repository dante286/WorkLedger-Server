# syntax=docker/dockerfile:1

# Production image for WorkLedger.Server. Two stages so the SDK (and the source) stay out of the
# shipped image: only the publish output and the ASP.NET runtime survive to the final stage.
#
# Alpine on both sides. The runtime image has no ICU, so .NET runs in invariant-globalization mode —
# safe here because every culture-sensitive parse in the server is already pinned to InvariantCulture
# (see ReportRange, SyncTime) and the only formats emitted are numeric ISO ones. It also means busybox
# `wget` is available for HEALTHCHECK without installing curl.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Project files first, so editing source doesn't invalidate the (slow) restore layer.
COPY src/WorkLedger.Contracts/WorkLedger.Contracts.csproj src/WorkLedger.Contracts/
COPY src/WorkLedger.Server/WorkLedger.Server.csproj src/WorkLedger.Server/
RUN dotnet restore src/WorkLedger.Server/WorkLedger.Server.csproj

COPY src/ src/

# Stamped onto AssemblyInformationalVersion, which is what GET / reports — so a running container can
# be identified without guessing which tag it came from. Compose passes ${WORKLEDGER_VERSION}.
ARG VERSION=0.0.0-dev
RUN dotnet publish src/WorkLedger.Server/WorkLedger.Server.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        -p:InformationalVersion=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

# Npgsql probes for GSSAPI/Kerberos while opening a connection and, without this, prints
# "Cannot load library libgssapi_krb5.so.2" to stdout on every boot. The connection succeeds anyway
# (we authenticate with a password), but an unexplained library error is the first thing an operator
# reads in `docker compose logs` when something else is actually wrong.
RUN apk add --no-cache krb5-libs

WORKDIR /app
COPY --from=build /app .

# 8080 rather than 80: the base image's non-root user cannot bind a privileged port. Publish it to
# whatever the host wants in compose; the container port is fixed so HEALTHCHECK can hardcode it.
# ASPNETCORE_HTTP_PORTS (what the base image already uses) rather than ASPNETCORE_URLS, which would
# override it and log a startup warning about doing so.
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# /health is deliberately the whole check: it is AllowAnonymous (no token needed) and does not touch
# the database, so it answers "the process is up and serving" — which is what a container healthcheck
# is for. Postgres reachability is Postgres's own healthcheck, and the start period covers the
# migration run on first boot.
HEALTHCHECK --interval=15s --timeout=3s --start-period=40s --retries=5 \
    CMD wget --quiet --spider http://127.0.0.1:8080/health || exit 1

# APP_UID (1654) and the `app` user come from the base image — switched to after the COPY so the app
# does not own, and cannot rewrite, its own binaries.
USER $APP_UID

ENTRYPOINT ["dotnet", "WorkLedger.Server.dll"]
