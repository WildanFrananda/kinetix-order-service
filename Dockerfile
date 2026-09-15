FROM mcr.microsoft.com/dotnet/sdk:10.0-preview@sha256:1d5e6f2c1ece7d5826bafc8a7f2d54db2c6478a0f2bd1c995d05a37c0be4783e AS build

ARG PROTOC_VERSION=3.21.12-8.2ubuntu0.3
RUN apt-get update \
    && apt-get install -y --no-install-recommends "protobuf-compiler=${PROTOC_VERSION}" \
    && rm -rf /var/lib/apt/lists/* \
    && protoc --version

WORKDIR /src

COPY ["Kinetix.OrderService.csproj", "./"]
RUN dotnet restore "Kinetix.OrderService.csproj"

COPY . .

# The wire contracts live in kinetix-contracts, not here: bin/sync-contracts fetches them at a pinned commit
# and the .csproj compiles .contracts/proto/**. Until now this image built only when the build context
# happened to carry a .contracts directory from somebody's laptop — a fresh clone failed with
# ".contracts/proto : directory does not exist", which is exactly how CI failed on its first run.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && sh bin/sync-contracts \
    && test -d .contracts/proto

RUN PLUGIN="$(find /root/.nuget/packages/grpc.tools -name grpc_csharp_plugin -path '*linux_arm64*' -o -name grpc_csharp_plugin -path '*linux_x64*' | head -1)" \
    && test -n "$PLUGIN" \
    && dotnet publish "Kinetix.OrderService.csproj" -c Release -o /app/publish \
         -p:Protobuf_ProtocFullPath=/usr/bin/protoc \
         -p:gRPC_PluginFullPath="$PLUGIN"

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview@sha256:ed557d471a2b702b72fd1fd4835040bbbdfbd2532ae78cfc90546773d88a91d7 AS final

# curl is here for the compose healthcheck, which probes /health/ready over HTTP. Without it
# this image has no way to make an HTTP request at all, and the container could only be
# checked by "is the process alive".
RUN apt-get update && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*


WORKDIR /app
COPY --from=build /app/publish .

ENV PORT=8001

EXPOSE 8001 50055

USER app


HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8001/health/ready > /dev/null || exit 1

ENTRYPOINT ["dotnet", "Kinetix.OrderService.dll"]