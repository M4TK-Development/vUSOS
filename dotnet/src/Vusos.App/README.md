# Vusos.App – aplikacja natywna (.NET MAUI Blazor Hybrid)

Jedna baza kodu C#/Razor dla **iOS**, **Androida** (telefon i tablet), **Windows** i **macOS** (Mac Catalyst).
Cały interfejs pochodzi z `Vusos.UI` (Razor Class Library), logika z `Vusos.Core`; ten projekt dokłada tylko to, co
zależy od platformy: SecureStorage, łączność, ścieżki, otwieranie plików/przeglądarki, opcjonalne logowanie MSAL i deep linki `vusos://`.

> **Ten projekt nie buduje się na Linuksie** (brak workloadu MAUI, Android SDK i Xcode) i dlatego **nie ma go w `Vusos.Linux.slnf`**.
> Jest w `Vusos.slnx`. Na Linuksie `dotnet build Vusos.slnx` zakończy się błędem workloadu – używaj `Vusos.Linux.slnf`.

## Struktura

```
Vusos.App.csproj          TFM: net10.0-android;net10.0-ios;net10.0-maccatalyst (+ net10.0-windows10.0.19041.0 na Windows)
MauiProgram.cs            konfiguracja, DI: AddVusosCore + AddVusosAdapters + AddVusosUi + usługi platformowe i hosta
App.xaml(.cs), MainPage.xaml(.cs)   BlazorWebView -> Components/Routes.razor -> Vusos.UI.AppRouter
Components/Routes.razor   korzeń Blazora; pokazuje toasty po deep linkach (ToastService jest zakresowy dla WebView)
wwwroot/index.html        strona hosta (viewport-fit=cover, _content/Vusos.UI/css/app.css, js/vusos.js, blazor.webview.js)
Services/                 MauiSecureStore, MauiConnectivity, MauiAppPaths, MauiExternalOpener, MauiConnectFlows,
                          MsalAuthSession (opcjonalny), DeepLinkRouter
Resources/Raw/appsettings.json   domyślna konfiguracja (Adapters:Mode=mock)
Platforms/<os>/           punkty wejścia i manifesty (Intent filter, Info.plist, Package.appxmanifest, ...)
```

## Wymagania

Wspólne: **.NET SDK 10** (`dotnet --version` >= 10.0) i workload MAUI:

```bash
dotnet workload install maui          # na macOS: to samo; na Linuksie MAUI nie jest wspierane
dotnet workload list                  # sprawdź: maui, android, ios, maccatalyst (windows: maui-windows)
```

| System budowania | Cele | Dodatkowo |
|---|---|---|
| **Windows 10/11** | Windows, Android | Visual Studio 2022/2026 z obciążeniem „.NET Multi-platform App UI development” (zawiera Android SDK + emulator); włączony Tryb dewelopera (MSIX) |
| **macOS** | iOS, Mac Catalyst, Android | **Xcode** (wersja wymagana przez workload `ios`; `xcode-select -s /Applications/Xcode.app`, uruchom raz i zaakceptuj licencję), Visual Studio Code z „.NET MAUI” albo JetBrains Rider; Android: Android SDK + JDK 17 (`dotnet build -t:InstallAndroidDependencies -f net10.0-android -p:AndroidSdkDirectory=$HOME/Library/Android/sdk`) |
| **Linux** | – | MAUI nieobsługiwane; zostaje `Vusos.WebHost` (Blazor Server) do pracy nad UI |

## Budowanie i uruchamianie

Z katalogu `dotnet/`:

```bash
# Android (emulator lub urządzenie z włączonym debugowaniem USB)
dotnet build src/Vusos.App -f net10.0-android
dotnet build src/Vusos.App -f net10.0-android -t:Run

# iOS (Mac + Xcode). Symulator:
dotnet build src/Vusos.App -f net10.0-ios -t:Run -p:RuntimeIdentifier=iossimulator-arm64
# iOS – urządzenie wymaga podpisu (Team ID + profil); najprościej z Visual Studio / Rider

# macOS (Mac Catalyst)
dotnet build src/Vusos.App -f net10.0-maccatalyst -t:Run

# Windows (PowerShell/cmd). Aplikacja jest pakietem MSIX (potrzebny protokół vusos://), więc uruchamiaj z Visual Studio (F5)
dotnet build src/Vusos.App -f net10.0-windows10.0.19041.0
```

Tryb domyślny to **mock** (`Adapters:Mode=mock`): aplikacja działa bez dostępu do uczelni, z danymi demonstracyjnymi.
Mock łączy oba systemy sam, więc tożsamością jest profil z adaptera mock (przez `ConnectionIdentity`, bez wbudowanego „użytkownika demo”).
Wylogowanie rozłącza USOS i Platon oraz czyści tożsamość, cache, materiały i poświadczenia.

## Logowanie: domyślnie bez rejestracji w Entra ID

Student nie ma uprawnień administratora w tenancie uczelni, a rejestracja wielotenantowa gdzie indziej może zostać zablokowana
przez politykę zgód tenanta. Dlatego **domyślnie (puste `University:EntraClientId`) aplikacja nie używa MSAL**:

- ekran logowania ma przyciski „Zaloguj przez Platon (konto M365 uczelni)” i „Zaloguj przez USOS (konto M365 uczelni)”; oba
  otwierają przeglądarkę systemową na stronie uczelni, która sama przekierowuje na `login.microsoftonline.com` (aplikacja nie widzi
  hasła, zgoda administratora niepotrzebna); powrót deep linkiem `vusos://…`;
- tożsamość (imię i nazwisko, e-mail, numer albumu z USOS; z samego Platona tylko imię i nazwisko) ustala `ConnectionIdentity`
  (`Vusos.Core.Hosting`) i zapisuje w `ISecureStore` pod `vusos.identity`, więc aplikacja otwiera się zalogowana także offline;
- zapasowo: wklejenie tokenu Platona albo cookies (te same formularze co w Ustawieniach);
- DI (`MauiProgram`): `AddVusosConnectionAuth()` → `ConnectionAuthSession` (`SupportsExternalIdentityProvider = false`);
- do czasu połączenia dostępne są tylko `/logowanie` i `/ustawienia`.

**MSAL jest trybem opcjonalnym**: gdy ustawisz `University:EntraClientId`, rejestrowany jest `MsalAuthSession`
(`SupportsExternalIdentityProvider = true`; przycisk „Zaloguj kontem uczelni (M365)”, patrz „Rejestracja aplikacji w Entra ID” niżej).

## Konfiguracja

Plik `Resources/Raw/appsettings.json` trafia do paczki aplikacji (zasób `MauiAsset`). Zmiany wymagają przebudowania.
Dodatkowo, bez przebudowy, możesz nadpisać wartości plikiem `appsettings.user.json` w katalogu danych aplikacji
(`FileSystem.AppDataDirectory`) – ten sam format, scalany po pliku z paczki.

Najważniejsze klucze:

| Klucz | Znaczenie |
|---|---|
| `Adapters:Mode` | `mock` (domyślnie) albo `live` |
| `University:EntraClientId` | OPCJONALNE: Application (client) ID z Entra ID. Puste (domyślnie) = tożsamość z połączeń USOS/Platon, bez MSAL |
| `University:EntraAuthority` | domyślnie `https://login.microsoftonline.com/organizations` |
| `University:AllowedEmailDomains` | dozwolone domeny e-mail po zalogowaniu (dokładne dopasowanie) |
| `University:SigningServerUrl` | adres `Vusos.Server` podpisującego OAuth 1.0a USOS (zalecane; aplikacja nie zawiera consumer secret) |
| `University:SigningServerAppKey` | opcjonalny `X-Vusos-App-Key` |
| `University:UsosConsumerKey` / `UsosConsumerSecret` | alternatywa: podpisywanie lokalne (tylko do testów; sekret w aplikacji da się wyciągnąć) |
| `University:NativeScheme` | schemat deep linków (`vusos`); musi zgadzać się z manifestami platform |
| `App:AllowInsecureSecureStoreFallback` | `true`/`false`; zapasowy, **nieszyfrowany** magazyn, gdy SecureStorage nie działa (domyślnie tylko w Debug) |

Tryb produkcyjny (przykład): `"Adapters": { "Mode": "live" }`, `"University": { "SigningServerUrl": "https://vusos.example.com/" }`
(`"EntraClientId": "<GUID>"` tylko jeśli masz rejestrację i chcesz logowanie MSAL).

### Rejestracja aplikacji w Entra ID (klient publiczny) – OPCJONALNA

1. portal.azure.com (lub entra.microsoft.com) -> **Microsoft Entra ID -> App registrations -> New registration**.
2. Nazwa: `vUSOS`. **Supported account types: „Accounts in any organizational directory (Any Microsoft Entra ID tenant – Multitenant)”**
   (endpoint `organizations`; ograniczenie do uczelni robi weryfikacja domeny e-mail w aplikacji).
3. Po utworzeniu skopiuj **Application (client) ID** do `University:EntraClientId`.
4. **Authentication -> Add a platform -> „Mobile and desktop applications”**, w „Custom redirect URIs” dodaj:
   - `msal{clientId}://auth` (Android i iOS; w miejsce `{clientId}` wstaw GUID z kroku 3),
   - `http://localhost` (Windows i Mac Catalyst – pętla zwrotna, MSAL sam wybiera port).

   W sekcji „Advanced settings” ustaw **Allow public client flows = Yes**. Nie dodawaj platformy „Web” ani sekretu klienta.
5. **API permissions -> Add a permission -> Microsoft Graph -> Delegated**: `openid`, `profile`, `email`, `User.Read`. Zgoda administratora
   uczelni może być wymagana (zależnie od polityki tenanta) – to użytkownik uczelni loguje się własnym kontem.
6. Wpisz `{clientId}` w miejscach z `TODO(config)` (patrz niżej).

### Miejsca z `msalYOUR_ENTRA_CLIENT_ID` (TODO(config))

| Plik | Co ustawić |
|---|---|
| `Platforms/Android/AndroidManifest.xml` | `android:scheme="msal{clientId}"` w `BrowserTabActivity` |
| `Platforms/iOS/Info.plist` | `msal{clientId}` w `CFBundleURLSchemes` |
| `Platforms/iOS/Entitlements.plist` | wymaga podpisu z Team ID (grupa Keychain `pl.m4tk.vusos` + `com.microsoft.adalcache`) |

Mac Catalyst i Windows nie potrzebują schematu `msal…` (logowanie przez `http://localhost`).

## Deep linki `vusos://`

| Platforma | Mechanizm |
|---|---|
| Android | `[IntentFilter]` w `MainActivity` (`DataScheme = "vusos"`), `LaunchMode.SingleTask`, `OnCreate`/`OnNewIntent` |
| iOS, Mac Catalyst | `CFBundleURLTypes` w `Info.plist`, `AppDelegate.OpenUrl` |
| Windows | `windows.protocol` w `Package.appxmanifest`; własny `Main` (`Platforms/Windows/Program.cs`) przekierowuje aktywację do działającej instancji (`AppInstance`) |

Wszystko trafia do `Services/DeepLinkRouter.Handle(...)`, który przełącza się na wątek główny i woła `DeepLinkDispatcher`
(`vusos://usos?oauth_token=...&oauth_verifier=...`, `vusos://token=...`). Linki `vusos://auth` i `msal…://` należą do MSAL i są pomijane.
Linki, które przyjdą przed końcem startu aplikacji, są kolejkowane.

Test z linii poleceń (Android): `adb shell am start -a android.intent.action.VIEW -d "vusos://usos?oauth_token=x&oauth_verifier=y"`;
iOS symulator: `xcrun simctl openurl booted "vusos://..."`; macOS: `open "vusos://..."`; Windows: `start vusos://...`.

## Bezpieczeństwo

- Poświadczenia USOS/Platon i profil użytkownika: `SecureStorage` (Keychain / Keystore / DPAPI). Zapasowy magazyn `Preferences` jest **nieszyfrowany**
  i domyślnie włączony tylko w Debug (emulatory bez blokady ekranu / symulator iOS bez Keychain).
- Android: `allowBackup=false` (tokeny nie trafiają do kopii zapasowych).
- Logowanie M365 w przeglądarce systemowej (na stronach USOS/Platona albo przez MSAL); hasła uczelni nigdy nie trafiają do aplikacji.
- Tożsamość z połączeń to nazwa i e-mail zwrócone przez USOS/Platon po zalogowaniu kontem uczelni; nie jest niezależnym dowodem tożsamości
  (nie ma tokenu Entra), ale dane i tak chroni logowanie do USOS/Platona. W trybie MSAL weryfikacja domeny e-mail jest po stronie klienta
  – to bramka UX, nie granica bezpieczeństwa.

## Znane punkty do sprawdzenia przy pierwszej kompilacji (`TODO(compile-check)`)

Kod powstał bez możliwości kompilacji MAUI, więc te miejsca mogą wymagać drobnych poprawek:

- `Vusos.App.csproj`: właściwość `CodesignEntitlements` (iOS/Mac Catalyst); wersja `Microsoft.Identity.Client` 4.66.2 (dla zasobów net6.0-android31.0 / net6.0-ios15.4; można podnieść do nowszej).
  Dla `net10.0-maccatalyst` i Windows NuGet wybiera zasób `net6.0` pakietu MSAL (sprawdzone narzędziem NuGet.Frameworks dla 4.66.2).
- `MauiProgram.cs` + `Vusos.UI` (`ConnectionAuthSession`, `LocalDataSignOutHandler`, `AddVusosConnectionAuth`): część UI/Core kompiluje się i jest testowana na Linuksie;
  w App sprawdź tylko rejestrację DI (wybór MSAL vs `AddVusosConnectionAuth` po `University:EntraClientId`) i wywołanie `IAuthSession.InitializeAsync()` po `Build()`.
- `Services/MsalAuthSession.cs` (tylko gdy `EntraClientId` ustawione): `WithParentActivityOrWindow(Platform.CurrentActivity / GetCurrentUIViewController())`, `WithSystemWebViewOptions`
  (gałąź Windows/Mac), `MsalError.AuthenticationCanceledError`, `AuthenticationResult.ClaimsPrincipal`. Część niezależna od platformy kompiluje się z MSAL 4.66.2 (sprawdzone osobno).
- `Services/MauiExternalOpener.cs`: `Launcher.Default.OpenAsync(OpenFileRequest)` + `ReadOnlyFile`.
- `Platforms/Android/MainActivity.cs`: sygnatura `OnActivityResult` i `AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(int, Result, Intent)`.
- `Platforms/iOS/AppDelegate.cs`, `Platforms/MacCatalyst/AppDelegate.cs`: `OpenUrl` – jeśli MAUI 10 używa cyklu życia UIScene, adres może nie trafiać do `AppDelegate`
  (wtedy `ConfigureLifecycleEvents(...AddiOS(i => i.OpenUrl(...)))` lub `SceneDelegate`). iOS: `AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(NSUrl)`.
- `Platforms/Windows/*`: `Microsoft.Windows.AppLifecycle` (`AppInstance`, `ExtendedActivationKind.Protocol`, `IProtocolActivatedEventArgs`), własny `Main`
  z `DISABLE_XAML_GENERATED_MAIN`, `BringToFront()`. Pakiet MSIX wymaga certyfikatu deweloperskiego (Visual Studio zrobi to przy F5).
- `Microsoft.Data.Sqlite` (Vusos.Core) na iOS/Android korzysta z `SQLitePCLRaw.bundle_e_sqlite3`; jeśli pojawi się `DllNotFoundException: e_sqlite3`,
  dodaj jawnie `SQLitePCLRaw.bundle_e_sqlite3` i wywołaj `SQLitePCL.Batteries_V2.Init()` w `MauiProgram`.
- Wersje NuGet: `Microsoft.Extensions.*` 10.0.14 zgodne z Vusos.Core; jeśli `Microsoft.AspNetCore.Components.WebView.Maui` zażąda wyższej wersji
  `Microsoft.AspNetCore.Components.Web` niż 10.0.13 z Vusos.UI – podnieś wersję w `Vusos.UI.csproj`.

## Znane ograniczenia

- Pamięć tokenów MSAL: Android/iOS używają trwałego cache platformy; Windows/Mac Catalyst – tylko pamięć procesu. Aplikacja nie wywołuje API z tokenem,
  a profil (imię, e-mail) jest zapisany w SecureStorage, więc po restarcie (także offline) pozostaje zalogowana.
- Wylogowanie nie czyści kolejki odpowiedzi testów offline (`IQuizOfflineStore`) – Core nie wystawia metody czyszczenia.
- Ikona i splash to prosty znak „vU” z kolorem akcentu `#4338CA`; do wymiany na docelową grafikę.
