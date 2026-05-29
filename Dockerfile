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
COPY .nuget/ ./.nuget/

# Restore packages (including extension server projects built by CopyOfficialServerExtensions)
RUN dotnet restore Server/Server.csproj && \
    dotnet restore Extensions/Chat/Server/ChatExtension.Server.csproj && \
    dotnet restore Extensions/Trade/Server/TradeExtension.Server.csproj

# Build server (this also builds extension Server DLLs via CopyOfficialServerExtensions target)
RUN dotnet build Server/Server.csproj -c Release -o /out --no-restore && \
    cp /src/libs/*.dll /out/

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0

# CWD is the data directory — server saves config, logs, databases here
WORKDIR /data

# Copy build output to /app (AppContext.BaseDirectory → Extensions at /app/Extensions/)
COPY --from=build /out /app/

EXPOSE 16200/udp

CMD ["dotnet", "/app/PhinixServer.dll"]
