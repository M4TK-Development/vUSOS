# vUSOS — architektura (v2, .NET)

Jednolita platforma studenta Vistula: **USOSweb** (dziekanat: oceny, opłaty, plan, wiadomości, ogłoszenia) i **Platon**
(Moodle: kursy, materiały, zadania, terminy, testy) w jednej aplikacji na telefon, tablet i komputer. Logowanie kontem
M365 uczelni. Dane trzymane lokalnie: działa offline i przy przeciążeniu Platona.

Stos: C# / .NET 10. Aplikacja **.NET MAUI Blazor Hybrid** (iOS, Android, Windows, macOS), wersja przeglądarkowa
**Blazor Server** (ten sam UI), biblioteka **Vusos.Core** (cała logika), minimalny **ASP.NET Core** serwer do
podpisywania OAuth USOS. Żadnego PHP po naszej stronie; `kontroler.php`, `launch.php` itd. to adresy w systemach uczelni.

Prototyp TypeScript z pierwszej iteracji leży w `reference/ts-prototype/` i jest specyfikacją wykonawczą
(adaptery + fixture'y + ~140 testów). Port 1:1 do C#.

## Ograniczenia wyjściowe (end-user only)

1. **Jeden token M365 nie otwiera USOSweb i Platona.** Entra ID jest tylko IdP; oba systemy mają własne sesje/tokeny.
   Logowanie M365 w aplikacji (MSAL.NET, public client, multi-tenant app w tenancie M4TK) daje tożsamość i filtr
   domeny (`*.vistula.edu.pl`). Do systemów aplikacja podłącza się osobno, po jednym kliknięciu każdy; cookie SSO Entra
   w przeglądarce systemowej sprawia, że kolejne logowania są bezklikowe.
2. **USOS, tryb preferowany: USOS API (OAuth 1.0a)**, metody `users/user`, `tt/user`, `grades/terms2`,
   `payments/user_payments`, `courses/user`, `terms/search`, `mailclient/*`, `news/*`, `fac/faculty`. Consumer key
   rejestruje się samoobsługowo na `/developers/` instalacji. Podpisywanie HMAC-SHA1 wymaga consumer secret, więc
   produkcyjnie podpisuje **Vusos.Server** (proxy, nic nie przechowuje); do użytku własnego można wpisać consumer
   key/secret w konfiguracji aplikacji i podpisywać lokalnie. `USOS_API_BASE_URL` do potwierdzenia dla Vistula.
3. **USOS, fallback: tryb `web`.** Cookies sesji USOSweb + parsowanie HTML (AngleSharp). Selektory niezweryfikowane
   bez dostępu do portalu; parsery defensywne.
4. **Platon, tryb preferowany: Moodle Web Services** (`webservice/rest/server.php`, `wstoken`). Token dla konta OIDC
   przez flow aplikacji mobilnej: `admin/tool/mobile/launch.php?service=moodle_mobile_app&passport=…&urlscheme=vusos`
   → po SSO M365 Moodle przekierowuje na `vusos://token=BASE64(siteid:::token:::privatetoken)` → aplikacja łapie deep
   link. Jeśli admin wymusił `forcedurlscheme`, zostaje wklejenie tokenu.
5. **Platon, fallback: tryb `web`.** Cookie `MoodleSession` + `sesskey` → `lib/ajax/service.php` z tymi samymi
   funkcjami WS. Jedna warstwa funkcji, dwa transporty.
6. **Tryb `mock`** obu adapterów: deterministyczne dane, dev/testy/demo bez dostępu do uczelni.

## Offline i przeciążenie

- **Cache każdej odpowiedzi** (SQLite, JSON + czas pobrania). Strategia: świeże < `FreshFor` → z cache; starsze → próba
  sieci z `LiveTimeout`; błąd/timeout/5xx → dane z cache z oznaczeniem `DataOrigin.Cache` i czasem. Linki wykładowców
  (moduły `url`, `resource`, `folder`) są w cache treści kursu, więc działają, gdy Platon leży; linki zewnętrzne otwiera
  przeglądarka, pliki wewnętrzne serwowane z dysku, jeśli pobrane.
- **Materiały offline**: kolejka pobrań (plik / cały kurs), pliki w `MaterialsDirectory/{courseId}/{moduleId}/`,
  rekordy w SQLite, wznawianie, limit na sieci komórkowej (opcja). Otwieranie systemową przeglądarką plików.
- **Testy (mod_quiz)**: `mod_quiz_get_quizzes_by_courses`, `get_user_attempts`, `start_attempt`, `get_attempt_data`
  (wszystkie strony), `save_attempt`, `process_attempt`, `get_attempt_review`. Offline możliwe tylko dla quizów z
  `allowofflineattempts=1` (ustawienie wykładowcy) i tylko po rozpoczęciu podejścia online. Odpowiedzi trzymane lokalnie,
  kolejka `process_attempt(finish)` synchronizowana po powrocie sieci. Limit czasu quizu liczony lokalnie. Rendering:
  HTML pytań z Moodle w komponencie z interop JS zbierającym pola formularza `q{usage}:{slot}_*`.
- **Aplikacja startuje offline**: ostatni dashboard, plan, oceny, kursy i materiały z lokalnej bazy.

## Komponenty

```
dotnet/src/Vusos.Core      domena, adaptery (USOS api/web/mock, Platon ws/web/mock), cache, materiały, testy offline,
                           agregacja dashboardu; zależności: Microsoft.Data.Sqlite, AngleSharp. Zero UI, zero MAUI.
dotnet/src/Vusos.UI        Razor Class Library: wszystkie strony i komponenty (Blazor), PL, responsywne.
dotnet/src/Vusos.App       .NET MAUI Blazor Hybrid: iOS/Android/Windows/macOS; deep link vusos://, MSAL, SecureStorage.
dotnet/src/Vusos.WebHost   Blazor Server hostujący Vusos.UI w przeglądarce (poświadczenia per circuit, cache per użytkownik).
dotnet/src/Vusos.Server    ASP.NET Core minimal API: OAuth 1.0a USOS (request/access token, proxy podpisujący).
dotnet/tests/*             xUnit; fixture'y JSON/HTML skopiowane z reference/ts-prototype.
```

### Vusos.Core — struktura

```
Domain/            rekordy domenowe (Usos.cs, Platon.cs, Dashboard.cs, Common.cs)
Adapters/          IUsosAdapter, IPlatonAdapter, Credentials (polimorficzne JSON), Errors
Adapters/Usos/     UsosApiClient (podpisywanie przez IUsosRequestSigner: LocalSigner | RemoteSigner), UsosApiAdapter,
                   UsosWebAdapter + Parsers (AngleSharp), UsosMockAdapter, Mappers, Raw DTO (System.Text.Json)
Adapters/Platon/   IMoodleTransport (WsTransport, AjaxTransport), PlatonFunctions (IPlatonAdapter nad transportem),
                   PlatonMockAdapter, MobileLaunchToken (parser), Raw DTO
Offline/           ICacheStore/IMaterialStore/IQuizOfflineStore (kontrakty) + SqliteStores (implementacja)
Services/          IConnectionManager, ISyncStatus, IMaterialService, IQuizService, IDashboardService (kontrakty),
                   CachedUsosAdapter / CachedPlatonAdapter (dekoratory stale-while-revalidate), implementacje
Platform/          ISecureStore, IConnectivity, IAppPaths, IClock (implementacje w App / WebHost)
DependencyInjection.cs   AddVusosCore(IServiceCollection, Action<UniversityOptions>, Action<CacheOptions>)
```

### Vusos.Server — API

```
POST /usos/oauth/request_token  { callbackUrl, scopes[] }        → { token, secret, authorizeUrl }
POST /usos/oauth/access_token   { token, secret, verifier }      → { token, secret }
ANY  /usos/proxy/{**path}       nagłówki X-Usos-Token, X-Usos-Token-Secret; body/query przekazane 1:1;
                                 serwer dokłada podpis OAuth i zwraca odpowiedź USOS API bez zmian
GET  /health
```
Opcjonalny nagłówek `X-Vusos-App-Key` (shared secret z konfiguracji) ogranicza użycie proxy do naszych klientów.
Rate limiting per IP. Serwer nie przechowuje tokenów użytkowników.

## Przepływy w kliencie natywnym

- **Logowanie**: MSAL.NET `AcquireTokenInteractive` (systemowa przeglądarka, redirect `msauth`/`vusos://auth`),
  weryfikacja `tid`/domeny e-mail, profil zapisany lokalnie. Brak własnego serwera tożsamości.
- **USOS**: `request_token` (przez Vusos.Server lub lokalnie) → przeglądarka systemowa `services/oauth/authorize`
  → callback `vusos://usos?oauth_token=…&oauth_verifier=…` (USOS API dopuszcza callback z własnym schematem; w razie
  problemów Vusos.Server wystawia `/usos/oauth/callback` i przekierowuje na `vusos://`) → `access_token`
  → `UsosApiCredentials` w SecureStorage.
- **Platon**: przeglądarka systemowa na `launch.php?…&urlscheme=vusos` → `vusos://token=…` → `PlatonWsCredentials`.
- Deep linki: Android `intent-filter`, iOS `CFBundleURLTypes`, Windows `windows.protocol` w `Package.appxmanifest`,
  Mac Catalyst `CFBundleURLTypes`. Obsługa w `App` → `IDeepLinkHandler` w Core/UI.

## Bezpieczeństwo

- Poświadczenia tylko na urządzeniu (SecureStorage). Vusos.Server nie ma stanu. Nigdy nie prosimy o hasło uczelniane.
- Cache i materiały w prywatnym katalogu aplikacji; „Wyloguj” czyści bazę i pliki.
- HTML z Moodle/USOS renderowany po sanitizacji (allowlista tagów), pliki otwierane systemowo.

## Konfiguracja (appsettings / MauiProgram)

```
University:Name, UsosApiBaseUrl, UsoswebBaseUrl, PlatonBaseUrl, NativeScheme=vusos,
University:SigningServerUrl | University:UsosConsumerKey + UsosConsumerSecret,
University:EntraClientId, EntraAuthority=https://login.microsoftonline.com/organizations, AllowedEmailDomains
Cache:FreshFor=00:02:00, ServeStaleUpTo=90.00:00:00, LiveTimeout=00:00:12, DownloadOnlyOnUnmetered=false
Adapters:Mode=mock|live
```

## Decyzje odrzucone

- Hasło uczelniane + headless login po stronie serwera: MFA, odpowiedzialność, regulamin.
- React Native / Flutter / Capacitor: wymóg C#; MAUI Blazor Hybrid daje jeden kod C#/Razor na wszystkie platformy.
- Trzymanie tokenów użytkowników na serwerze: niepotrzebne przy kliencie natywnym; mniejsza odpowiedzialność.

## Vusos.WebHost — izolacja użytkowników

Core zaprojektowano dla jednego użytkownika na urządzeniu (singletony, jedna baza SQLite). W Blazor Server host tworzy
**osobny kontener Core per użytkownik** (`UserCoreFactory`): własny katalog `{Storage:Root}/users/{hash}` z bazą i
materiałami, `ISecureStore` szyfrowany Data Protection (purpose per użytkownik), `AdapterOptions.UserScope` = hash.
Serwisy w głównym kontenerze to scoped forwardery do kontenera bieżącego użytkownika (`CircuitUser` z
`AuthenticationStateProvider`). Nieużywane kontenery są zwalniane po 2 h bezczynności. Logowanie: cookie + OIDC Entra
(`Auth:Mode=entra`) albo formularz dev (`Auth:Mode=dev`, blokowany poza Development).

## Co trzeba zweryfikować na żywo

Bez dostępu sieciowego do uczelni wszystkie kształty odpowiedzi USOS API / Moodle WS i selektory HTML pochodzą z
dokumentacji i doświadczenia, nie z Vistula. Lista punktów: `grep -rn "TODO(verify)" dotnet/src`. Najważniejsze:
adres instalacji USOS API dla Vistula, moduł `mailclient` (USOSmail), pola płatności, dostępność `mod_quiz_*` i
`core_webservice_get_site_info` przez `lib/ajax/service.php`, czy admin Platona wymusił `forcedurlscheme`.
