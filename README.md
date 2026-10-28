# vUSOS

Jednolita platforma studenta Vistula na telefon, tablet i komputer. Jedno logowanie kontem M365 uczelni, jeden
interfejs dla danych z dwóch systemów, działający także offline:

| Źródło | Co pokazujemy |
|---|---|
| **USOSweb** (`usosweb.vistula.edu.pl`) | plan zajęć, oceny, opłaty, wiadomości i kontakt z dziekanatem, ogłoszenia |
| **Platon** (`platon.vistula.edu.pl`, Moodle) | kursy, materiały (także pobrane offline), zadania i terminy, testy, oceny z kursów, ogłoszenia |

Stos: C# / .NET 10. .NET MAUI Blazor Hybrid (iOS, Android, Windows, macOS), Blazor Server dla przeglądarki, wspólna
biblioteka `Vusos.Core`, minimalny ASP.NET Core serwer do podpisywania OAuth USOS. Projekt nieoficjalny, pisany z pozycji
studenta (uprawnienia end-user). Decyzje: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Struktura

```
dotnet/src/Vusos.Core      domena, adaptery USOS/Platon (api|web|mock), cache offline, materiały, testy offline
dotnet/src/Vusos.UI        Razor Class Library — strony i komponenty (PL, responsywne)
dotnet/src/Vusos.App       .NET MAUI Blazor Hybrid — iOS / Android / Windows / macOS
dotnet/src/Vusos.WebHost   Blazor Server — ta sama aplikacja w przeglądarce
dotnet/src/Vusos.Server    ASP.NET Core — proxy OAuth 1.0a USOS (bez stanu)
dotnet/tests               xUnit
reference/ts-prototype     pierwsza iteracja (TypeScript), zachowana jako specyfikacja z fixture'ami
```

## Szybki start (tryb demo, bez dostępu do uczelni)

```bash
cd dotnet
dotnet build Vusos.Linux.slnf
dotnet test Vusos.Linux.slnf
dotnet run --project src/Vusos.WebHost        # http://localhost:5080, dane z adapterów mock
```

## Aplikacje natywne

Wymagania: .NET 10 SDK + workload `maui` (`dotnet workload install maui`), Xcode (iOS/macOS, tylko Mac), Android SDK,
Visual Studio 2022+/VS Code z C# Dev Kit (Windows).

```bash
cd dotnet/src/Vusos.App
dotnet build -f net10.0-android
dotnet build -f net10.0-ios            # Mac
dotnet build -f net10.0-maccatalyst    # Mac
dotnet build -f net10.0-windows10.0.19041.0   # Windows
```

Aplikacja rejestruje schemat `vusos://`: logowanie M365, OAuth USOS i token Platona (`launch.php?urlscheme=vusos`)
wracają do aplikacji bez wklejania czegokolwiek.

## Produkcja

1. App registration w Entra ID (public client, multi-tenant, redirect `vusos://auth`) → `University:EntraClientId`.
2. Consumer key USOS API z `/developers/` instalacji USOS → `Vusos.Server` (`Usos:ConsumerKey/Secret`) albo lokalnie w
   konfiguracji aplikacji (użytek własny).
3. `Adapters:Mode=live`. Serwer: `cd dotnet && docker build -f src/Vusos.Server/Dockerfile -t vusos-server .`

## Licencja

GPL-3.0.
