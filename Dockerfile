FROM node:22.13.1-bookworm-slim AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY global.json ./
COPY backend/ ./backend/
COPY --from=frontend /src/frontend/dist/ ./backend/src/NexoBar.Host/wwwroot/
RUN dotnet restore backend/src/NexoBar.Host/NexoBar.Host.csproj
RUN dotnet publish backend/src/NexoBar.Host/NexoBar.Host.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./
ENTRYPOINT ["sh", "-c", "exec dotnet NexoBar.Host.dll --urls \"http://0.0.0.0:${PORT:-8080}\"", "--"]
