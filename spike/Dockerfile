# Phase 0 container check: proves the OR-Tools native runtime loads under glibc
# on the exact base image the real app will ship on.
#
#   docker build -f spike/Dockerfile -t shiftreason-spike .
#   docker run --rm --cpus=4 shiftreason-spike
#
# Two things this exists to prove:
#   1. `10.0-noble` (Ubuntu 24.04, glibc) loads libortools. Alpine/musl never will
#      — no musl build of OR-Tools is published.
#   2. `-r linux-x64` prunes the four unused runtime packages. Google.OrTools
#      depends on ALL five unconditionally, so a non-RID publish drags every
#      runtimes/<rid>/native folder into the image — hundreds of MB.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY spike/Spike.csproj spike/
RUN dotnet restore spike/Spike.csproj -r linux-x64
COPY spike/ spike/
RUN dotnet publish spike/Spike.csproj \
        -c Release \
        -r linux-x64 \
        --self-contained false \
        --no-restore \
        -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
COPY --from=build /app .

# Report the image's view of the world before running the checks: CP-SAT's
# num_workers:0 would read HOST cores here and ignore the cgroup quota.
ENTRYPOINT ["dotnet", "Spike.dll"]
