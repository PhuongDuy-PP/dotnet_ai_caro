FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy csproj and restore as distinct layers
COPY *.sln .
COPY CaroAIServer/*.csproj ./CaroAIServer/
RUN dotnet restore "CaroAIServer/CaroAIServer.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/app/CaroAIServer"
RUN dotnet publish "CaroAIServer.csproj" -c Release -o /app/publish

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CaroAIServer.dll"] 