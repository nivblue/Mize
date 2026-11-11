# Multi-stage build: compile with the SDK image, run on the slim ASP.NET runtime image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only project files first so the restore layer is cached until dependencies change.
COPY Mize.sln ./
# Directory.Build.props relocates bin/obj, so it must be present before restore writes obj/.
COPY src/Directory.Build.props src/
COPY tests/Directory.Build.props tests/
COPY src/Mize.Api.csproj src/
COPY tests/Mize.Tests.csproj tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/Mize.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The database lives in /data, which docker-compose mounts from the local ./data folder.
ENV ConnectionStrings__Reservations="Data Source=/data/mize.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "Mize.Api.dll"]
