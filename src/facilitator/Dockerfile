# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props nuget.config OpenFacilitator.slnx ./
COPY src/facilitator/Facilitator.csproj src/facilitator/
RUN dotnet restore src/facilitator/Facilitator.csproj
COPY src/facilitator/ src/facilitator/
RUN dotnet publish src/facilitator/Facilitator.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENTRYPOINT ["dotnet", "Facilitator.dll"]
