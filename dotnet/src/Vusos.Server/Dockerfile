# Budowanie z katalogu dotnet/:  docker build -f src/Vusos.Server/Dockerfile -t vusos-server .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Vusos.Core/Vusos.Core.csproj src/Vusos.Core/
COPY src/Vusos.Server/Vusos.Server.csproj src/Vusos.Server/
RUN dotnet restore src/Vusos.Server/Vusos.Server.csproj
COPY src/Vusos.Core/ src/Vusos.Core/
COPY src/Vusos.Server/ src/Vusos.Server/
RUN dotnet publish src/Vusos.Server/Vusos.Server.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
# Obrazy aspnet:10.0 zawierają użytkownika nieuprzywilejowanego "app".
USER app
ENTRYPOINT ["dotnet", "Vusos.Server.dll"]
