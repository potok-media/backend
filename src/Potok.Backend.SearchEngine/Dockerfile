# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore as distinct layers
COPY ["src/Potok.Backend.SearchEngine/Potok.Backend.SearchEngine.csproj", "src/Potok.Backend.SearchEngine/"]
COPY ["src/Potok.Backend.Core/Potok.Backend.Core.csproj", "src/Potok.Backend.Core/"]
COPY ["src/Potok.Backend.Infrastructure/Potok.Backend.Infrastructure.csproj", "src/Potok.Backend.Infrastructure/"]

RUN dotnet restore "src/Potok.Backend.SearchEngine/Potok.Backend.SearchEngine.csproj"

# Copy the remaining source files and build/publish the application
COPY . .
WORKDIR "/src/src/Potok.Backend.SearchEngine"
RUN dotnet publish "Potok.Backend.SearchEngine.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Default SearchEngine port
ENV PORT=6000
EXPOSE 6000

ENTRYPOINT ["dotnet", "Potok.Backend.SearchEngine.dll"]
