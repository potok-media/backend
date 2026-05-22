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
RUN dotnet publish "Potok.Backend.Gateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Default ASP.NET Core port
ENV PORT=5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "Potok.Backend.Gateway.dll"]
