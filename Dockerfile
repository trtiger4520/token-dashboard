FROM node:24.14.0-bookworm-slim AS web-build
WORKDIR /source

COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY src/TokenDashboard.Web/package.json src/TokenDashboard.Web/package.json
RUN corepack enable \
    && corepack prepare pnpm@11.9.0 --activate \
    && pnpm install --frozen-lockfile

COPY src/TokenDashboard.Web src/TokenDashboard.Web
RUN pnpm --filter token-dashboard-web build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /source

COPY . .
COPY --from=web-build /source/src/TokenDashboard.Web/dist src/TokenDashboard.Web/dist
RUN dotnet restore src/TokenDashboard.Api/TokenDashboard.Api.csproj \
    && dotnet publish src/TokenDashboard.Api/TokenDashboard.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=dotnet-build /app/publish .
RUN mkdir /data \
    && chown "$APP_UID:$APP_UID" /data

USER $APP_UID
ENTRYPOINT ["dotnet", "TokenDashboard.Api.dll"]
