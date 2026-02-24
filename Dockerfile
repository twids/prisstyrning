## Multi-stage Dockerfile for Prisstyrning (.NET 8 ASP.NET Core + React frontend)
## Build frontend with Node.js, then backend with .NET SDK

# Stage 1: Build frontend (v1) with Node.js
FROM node:20-alpine AS frontend-build
WORKDIR /frontend

# Copy frontend package files and install dependencies
COPY frontend/package*.json ./
RUN npm ci

# Copy frontend source and build
COPY frontend/ ./
RUN npm run build
# Output: wwwroot artifacts will be in ../wwwroot (parent directory)

# Stage 2: Build frontend-v2 with Node.js
FROM node:20-alpine AS frontend-v2-build
WORKDIR /frontend-v2

# Copy frontend-v2 package files and install dependencies
COPY frontend-v2/package*.json ./
RUN npm ci

# Copy frontend-v2 source and build
COPY frontend-v2/ ./
RUN npm run build
# Output: wwwroot-v2 artifacts will be in ../wwwroot-v2 (parent directory)

# Stage 3: Build backend with .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src

# Copy csproj and restore first for better layer caching
COPY Prisstyrning.csproj ./
RUN dotnet restore Prisstyrning.csproj

# Copy the full backend source
COPY . .

# Copy built frontends from frontend build stages
COPY --from=frontend-build /wwwroot ./wwwroot
COPY --from=frontend-v2-build /wwwroot-v2 ./wwwroot-v2

# Publish backend (framework-dependent)
ARG BUILD_CONFIG=Release
RUN dotnet publish Prisstyrning.csproj -c $BUILD_CONFIG -o /app/publish --no-restore

# Stage 4: Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "Prisstyrning.dll"]
