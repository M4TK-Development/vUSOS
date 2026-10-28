# Budowanie z katalogu dotnet/:  docker build -f src/Vusos.WebHost/Dockerfile -t vusos-web .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Vusos.Core/Vusos.Core.csproj src/Vusos.Core/
COPY src/Vusos.UI/Vusos.UI.csproj src/Vusos.UI/
COPY src/Vusos.WebHost/Vusos.WebHost.csproj src/Vusos.WebHost/
RUN dotnet restore src/Vusos.WebHost/Vusos.WebHost.csproj
COPY src/Vusos.Core/ src/Vusos.Core/
COPY src/Vusos.UI/ src/Vusos.UI/
COPY src/Vusos.WebHost/ src/Vusos.WebHost/
RUN dotnet publish src/Vusos.WebHost/Vusos.WebHost.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
# Dane użytkowników (SQLite, materiały, secure.json) i klucze Data Protection: zamontuj wolumen na /data.
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    Storage__Root=/data \
    Adapters__Mode=live \
    Auth__Mode=anonymous \
    Server__RedirectToHttps=false \
    Server__UseForwardedHeaders=true
VOLUME /data
EXPOSE 8080
# Obrazy aspnet:10.0 zawierają użytkownika nieuprzywilejowanego "app".
USER app
ENTRYPOINT ["dotnet", "Vusos.WebHost.dll"]
