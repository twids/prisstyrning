# syntax=docker/dockerfile:1
## Multi-stage Dockerfile for Prisstyrning (.NET 10 ASP.NET Core + React frontend)
## Build frontend with Node.js, then backend with .NET SDK

# Stage 1: Build frontend with Node.js
FROM node:20-alpine AS frontend-build
WORKDIR /frontend

# Copy frontend package files and install dependencies
COPY frontend/package*.json ./
RUN --mount=type=cache,target=/root/.npm npm ci

# Copy frontend source and build
COPY frontend/ ./
RUN npm run build
# Output: wwwroot artifacts will be in ../wwwroot (parent directory)

# Stage 2: Build backend with .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS backend-build
WORKDIR /src

# Pin the same SDK used locally/CI before restore, then cache the project graph.
COPY global.json Directory.Build.props Prisstyrning.csproj ./
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore Prisstyrning.csproj

# Copy the full backend source
COPY . .

# Copy built frontend from frontend build stage
COPY --from=frontend-build /wwwroot ./wwwroot

# Publish backend (framework-dependent)
ARG BUILD_CONFIG=Release
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish Prisstyrning.csproj -c $BUILD_CONFIG -o /app/publish

# Stage 3: Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "Prisstyrning.dll"]
