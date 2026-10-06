# Build stage
ARG DOTNET_SDK_VERSION=10.0.401
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
ARG DOTNET_SDK_VERSION

# Honor repository overrides without changing protobuf's vendored global.json.
ENV MSBuildSDKsPath=/usr/share/dotnet/sdk/${DOTNET_SDK_VERSION}/Sdks

WORKDIR /src

# Copy only what's needed for server build
COPY nuget.config ./
COPY Directory.Build.props Directory.Build.targets ./
COPY Server/ ./Server/
COPY Common/ ./Common/
COPY Dependencies/ ./Dependencies/
COPY Extensions/ ./Extensions/
COPY libs/ ./libs/

# Publish only the server graph. Official plugins use the normal Extensions discovery path.
RUN dotnet publish Server/Server.csproj -c Release -o /out --no-self-contained && \
    mkdir -p /out/Extensions && \
    cp /src/Server/bin/Release/net10.0/Extensions/*.dll /out/Extensions/ && \
    for file in PhinixServer.dll PhinixServer.deps.json PhinixServer.runtimeconfig.json \
                LiteNetLib.dll Extensions/ChatExtension.Server.dll Extensions/ChatExtension.dll \
                Extensions/TradeExtension.Server.dll Extensions/TradeExtension.dll; do \
        test -f "/out/$file" || exit 1; \
    done && \
    test -z "$(find /out -type f \( -name 'Assembly-CSharp*.dll' -o -name 'Unity*.dll' -o -name 'mscorlib.dll' \) -print)"

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0

# CWD is the data directory - server saves config, logs, databases here
WORKDIR /data

# Copy build output to /app (AppContext.BaseDirectory -> Extensions at /app/Extensions/)
COPY --from=build /out /app/

EXPOSE 16200/udp

CMD ["dotnet", "/app/PhinixServer.dll"]
