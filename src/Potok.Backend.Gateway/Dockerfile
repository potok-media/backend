# Plugin-bundler build stage — a tiny Go sidecar (esbuild) baked into this image.
# It is internal-only (loopback) and launched/owned by the gateway at runtime.
FROM golang:1.23-alpine AS bundler-build
WORKDIR /bundler
RUN apk add --no-cache git
COPY ["src/Potok.Backend.PluginBundler/go.mod", "src/Potok.Backend.PluginBundler/go.sum", "./"]
RUN go mod download
COPY src/Potok.Backend.PluginBundler/ ./
RUN CGO_ENABLED=0 GOOS=linux go build -ldflags="-w -s" -o /out/potok-plugin-bundler .

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore as distinct layers
COPY ["src/Potok.Backend.Gateway/Potok.Backend.Gateway.csproj", "src/Potok.Backend.Gateway/"]
COPY ["src/Potok.Backend.Core/Potok.Backend.Core.csproj", "src/Potok.Backend.Core/"]
COPY ["src/Potok.Backend.Infrastructure/Potok.Backend.Infrastructure.csproj", "src/Potok.Backend.Infrastructure/"]

RUN dotnet restore "src/Potok.Backend.Gateway/Potok.Backend.Gateway.csproj"

# Copy the remaining source files and build/publish the application
COPY . .
WORKDIR "/src/src/Potok.Backend.Gateway"

ARG VERSION=1.0.0-dev
RUN dotnet publish "Potok.Backend.Gateway.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:Version=${VERSION}

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Internal plugin-bundler sidecar binary (loopback-only, started by the gateway).
COPY --from=bundler-build /out/potok-plugin-bundler /app/potok-plugin-bundler
RUN chmod +x /app/potok-plugin-bundler

# Default ASP.NET Core port (only the gateway is exposed; the bundler never is).
ENV PORT=5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "Potok.Backend.Gateway.dll"]
