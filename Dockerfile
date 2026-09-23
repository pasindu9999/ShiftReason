# syntax=docker/dockerfile:1.7
#
# ShiftReason: one image, API + SignalR + built SPA, served same-origin.
#
#   docker build -t shiftreason .
#   docker run --rm -p 8080:8080 shiftreason        ->  http://localhost:8080
#
# Three stages. Each carries a decision that was measured, not assumed:
#
#   web    Builds the React app. Node never ships in the final image.
#   build  Publishes for linux-x64 ONLY. Google.OrTools depends on all five of its
#          native runtime packages unconditionally, so a publish without -r copies
#          every runtimes/<rid>/native folder in — four unused copies of the solver.
#   final  Ubuntu 24.04 (noble), i.e. glibc. OR-Tools publishes no musl build, so
#          an -alpine base can never load libortools. Not chiseled either: that is
#          worth revisiting only once the native load is proven there, because
#          debugging a failed dlopen in an image with no shell is miserable.
#
# Never add PublishTrimmed, PublishAot or PublishSingleFile. All three break the
# native library resolution SWIG relies on, and fail at the first solve rather
# than at build time.

ARG DOTNET_VERSION=10.0
ARG NODE_VERSION=24

# ---------------------------------------------------------------------------
FROM node:${NODE_VERSION}-slim AS web
WORKDIR /src/src/ShiftReason.Web

# Manifests first, so editing a component does not re-download node_modules.
COPY src/ShiftReason.Web/package.json src/ShiftReason.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/ShiftReason.Web/ ./

# Baked into the bundle at build time; optional.
ARG VITE_REPO_URL=""
ENV VITE_REPO_URL=${VITE_REPO_URL}

# The Vite config writes into the API's wwwroot for local development; inside the
# image the output goes somewhere the final stage can copy from explicitly.
RUN npm run build -- --outDir /web --emptyOutDir

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY global.json ./
COPY src/ShiftReason.Domain/ShiftReason.Domain.csproj src/ShiftReason.Domain/
COPY src/ShiftReason.Solver/ShiftReason.Solver.csproj src/ShiftReason.Solver/
COPY src/ShiftReason.Api/ShiftReason.Api.csproj      src/ShiftReason.Api/
RUN dotnet restore src/ShiftReason.Api/ShiftReason.Api.csproj -r linux-x64

COPY src/ShiftReason.Domain/ src/ShiftReason.Domain/
COPY src/ShiftReason.Solver/ src/ShiftReason.Solver/
COPY src/ShiftReason.Api/    src/ShiftReason.Api/

RUN dotnet publish src/ShiftReason.Api/ShiftReason.Api.csproj \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained false \
        --no-restore \
        --output /app \
        -p:UseAppHost=false

# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS final
WORKDIR /app

# SQLite lives outside /app: the app runs as the image's non-root user, and /app
# is owned by root. Run history is demo data — ephemeral storage is intended.
# Mount a volume at /data to keep it across restarts.
RUN mkdir -p /data && chown "$APP_UID" /data

COPY --from=build /app ./
COPY --from=web   /web ./wwwroot

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DbPath=/data/shiftreason.db

# $APP_UID is set by Microsoft's base image to the numeric id 1654 of its non-root
# user; hadolint cannot see through the variable.
# hadolint ignore=DL3066
USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "ShiftReason.Api.dll"]
