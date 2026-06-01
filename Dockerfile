# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy only what's needed for server build
COPY nuget.config ./
COPY Phinix.sln ./
COPY Server/ ./Server/
COPY Common/ ./Common/
COPY Dependencies/ ./Dependencies/
COPY Extensions/ ./Extensions/
COPY libs/ ./libs/

# Restore packages (including official server extension projects)
RUN dotnet restore Server/Server.csproj && \
    dotnet restore Extensions/Chat/Server/ChatExtension.Server.csproj && \
    dotnet restore Extensions/Trade/Server/TradeExtension.Server.csproj

# Build server (extension build + copy is handled by CopyOfficialServerExtensions target)
RUN dotnet build Server/Server.csproj -c Release -o /out --no-restore && \
    cp /src/libs/netstandard2.0/LiteNetLib.dll /out/ && \
    mkdir -p /out/Extensions && \
    cp /out/ChatExtension.Server.dll /out/Extensions/ && \
    cp /out/ChatExtension.dll /out/Extensions/ && \
    cp /out/TradeExtension.Server.dll /out/Extensions/ && \
    cp /out/TradeExtension.dll /out/Extensions/

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0

# CWD is the data directory - server saves config, logs, databases here
WORKDIR /data

# Copy build output to /app (AppContext.BaseDirectory -> Extensions at /app/Extensions/)
COPY --from=build /out /app/

EXPOSE 16200/udp

CMD ["dotnet", "/app/PhinixServer.dll"]
