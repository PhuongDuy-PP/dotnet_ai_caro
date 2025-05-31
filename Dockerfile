FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# Copy solution file and the main project file
# Assuming CaroAIServer.csproj is at the root of your project
COPY CaroAIServer.sln .
COPY CaroAIServer.csproj .

# If your solution references other projects, copy their .csproj files too,
# maintaining their directory structure if necessary. For example:
# COPY OtherProject/OtherProject.csproj ./OtherProject/

# Restore dependencies for the solution. This is generally preferred as it handles all projects.
RUN dotnet restore "CaroAIServer.sln"
# Alternatively, if it's a single project or you prefer to restore the project directly:
# RUN dotnet restore "CaroAIServer.csproj"

# Copy the rest of the application files
# This includes all source code (Program.cs, Controllers/, Services/, etc.)
# and also the CaroAIServer/ directory and its contents.
COPY . .

# Publish the application
# The CaroAIServer.csproj is now at /app/CaroAIServer.csproj,
# and the WORKDIR is /app.
RUN dotnet publish "CaroAIServer.csproj" -c Release -o /app/publish

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CaroAIServer.dll"] 