# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files
COPY TracingDemo.sln .
COPY src/Shared/Shared.csproj src/Shared/
COPY src/TradeService/TradeService.csproj src/TradeService/
COPY src/SettlementService/SettlementService.csproj src/SettlementService/

# Restore dependencies
RUN dotnet restore src/TradeService/TradeService.csproj

# Copy source code
COPY src/Shared/ src/Shared/
COPY src/TradeService/ src/TradeService/

# Build and publish
RUN dotnet publish src/TradeService/TradeService.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install Oracle Instant Client (required for Oracle.ManagedDataAccess)
RUN apt-get update && apt-get install -y libaio1 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "TradeService.dll"]

