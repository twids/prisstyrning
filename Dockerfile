## Multi-stage Dockerfile for Prisstyrning (.NET 8 ASP.NET Core)
## Fix: use aspnet runtime (was plain runtime -> missing Microsoft.AspNetCore.App)

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore first for better layer caching
COPY Prisstyrning.csproj ./
RUN dotnet restore Prisstyrning.csproj

# Copy the full source
COPY . .

# Publish (framework-dependent); pass configuration via build arg if needed
ARG BUILD_CONFIG=Release
RUN dotnet publish Prisstyrning.csproj -c %BUILD_CONFIG% -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Prisstyrning.dll"]
