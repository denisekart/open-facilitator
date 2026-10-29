# syntax=docker/dockerfile:1

# The embedded SPA: the image build owns the vite build so a CI image build
# without pnpm still carries the operator inspector.
FROM node:22-alpine AS ui
WORKDIR /ui
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml tsconfig.base.json ./
COPY src/facilitator/ui/package.json src/facilitator/ui/
RUN pnpm install --frozen-lockfile --filter @ofac/ui
COPY src/facilitator/ui/ src/facilitator/ui/
RUN pnpm --filter @ofac/ui build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config OpenFacilitator.slnx ./
COPY protocol/dotnet/Ofac.Protocol/Ofac.Protocol.csproj protocol/dotnet/Ofac.Protocol/
COPY protocol/dotnet/Ofac.Protocol/Generated/ protocol/dotnet/Ofac.Protocol/Generated/
COPY protocol/dotnet/Ofac.Protocol/Validation/ protocol/dotnet/Ofac.Protocol/Validation/
COPY protocol/json-schema/ protocol/json-schema/
COPY protocol/cloudevents-schemas/ protocol/cloudevents-schemas/
COPY src/facilitator/Facilitator.csproj src/facilitator/
RUN dotnet restore src/facilitator/Facilitator.csproj
COPY src/facilitator/ src/facilitator/
COPY --from=ui /ui/src/facilitator/wwwroot/ src/facilitator/wwwroot/
RUN dotnet publish src/facilitator/Facilitator.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "Facilitator.dll"]
