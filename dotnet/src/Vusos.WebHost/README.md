# Vusos.WebHost

Blazor Server hostujący ten sam interfejs (`Vusos.UI`) co aplikacje natywne, ale w przeglądarce. Serwer uruchamia
`Vusos.Core` **osobno dla każdego zalogowanego użytkownika** (patrz „Izolacja użytkowników”).

```bash
cd dotnet
dotnet run --project src/Vusos.WebHost --urls http://localhost:5081   # Development: Adapters:Mode=mock, Auth:Mode=dev
```

Wejście na `/` bez sesji przekierowuje na `/logowanie`. W trybie `dev` przycisk logowania prowadzi do formularza
z adresem e-mail (`@vistula.edu.pl` / `@stu.vistula.edu.pl`); każdy adres ma własne dane. Sesja to cookie `vusos.auth`,
więc odświeżenie strony jej nie gubi.

## Tryby

| Klucz | Wartości | Znaczenie |
|---|---|---|
| `Adapters:Mode` | `fake` | Dane w pamięci (`Dev/Fake*`), bez Vusos.Core; wspólne dla wszystkich; panel `/dev`. |
| | `mock` (domyślny) | Prawdziwy Core z adapterami mock: deterministyczne dane, pełny stos (SQLite, cache, materiały, testy offline). Połączenie z USOS/Platonem jest od razu „Połączono”. |
| | `live` | Prawdziwy Core z USOS API/Platon WS. Użytkownik łączy systemy w Ustawieniach. |
| `Auth:Mode` | `dev` | Logowanie samym adresem e-mail z dozwolonej domeny (`University:AllowedEmailDomains`). **Tylko Development/Testing**; w Production aplikacja nie wystartuje, chyba że `Auth:AllowDevInProduction=true`. |
| | `entra` | OpenID Connect z Microsoft Entra ID (patrz niżej). |

`Dev:AutoSignInEmail` (tylko `Auth:Mode=dev`): `/auth/login` loguje od razu tym adresem, bez formularza.

## Logowanie Entra ID (`Auth:Mode=entra`)

1. Entra admin center → *App registrations* → *New registration*: typ konta **Accounts in any organizational directory
   (multitenant)**, platforma **Web**, redirect URI `{PublicUrl}/signin-oidc` (np. `https://vusos.example.pl/signin-oidc`).
2. *Certificates & secrets* → nowy **client secret**.
3. Konfiguracja (najlepiej zmienne środowiskowe / sekrety, nie plik w repozytorium):

```
Auth__Mode=entra
Auth__ClientId=<Application (client) ID>
Auth__ClientSecret=<client secret>
Auth__Authority=https://login.microsoftonline.com/organizations   # domyślnie University:EntraAuthority
Auth__AllowedTenantIds__0=<opcjonalnie tid tenanta uczelni>        # dodatkowe zawężenie
Server__PublicUrl=https://vusos.example.pl
```

Przepływ: kod autoryzacji + PKCE, scope `openid profile email`. Wystawca jest walidowany luźno (każdy tenant Entra
`https://login.microsoftonline.com/{tid}/v2.0`, bo `organizations` jest wielotenantowe). W `OnTokenValidated` host wymaga,
by `preferred_username`/`upn`/`email` należał do `University:AllowedEmailDomains` (inaczej logowanie kończy się komunikatem
„To konto nie należy do uczelni…”). Tożsamością użytkownika jest `entra:{tid}:{oid}`. Wylogowanie kończy tylko sesję
aplikacji (cookie), nie sesję Microsoft 365 w przeglądarce.

## USOS: podpisywanie OAuth 1.0a

Consumer key rejestruje się na `/developers/` instalacji USOS; callback to `{PublicUrl}/connect/usos/callback`.
Jedna z opcji (sekcja `University`):

- `SigningServerUrl` (+ opcjonalnie `SigningServerAppKey`) – adres **Vusos.Server**, który podpisuje żądania (zalecane);
- albo `UsosConsumerKey` + `UsosConsumerSecret` – podpisywanie lokalne w WebHost (użytek własny).

Bez żadnej z nich przycisk „Połącz z USOS” zgłosi brak konfiguracji (w `mock` łączy dane demonstracyjne).
Platon: przeglądarka nie złapie `moodlemobile://`, więc „Połącz z Platonem” pokazuje komunikat
„W przeglądarce wklej token z launch.php…” z adresem `launch.php`; użytkownik wkleja adres `moodlemobile://token=…`
w polu „Wklej token”.

## Izolacja użytkowników

Core projektowano dla jednego urządzenia (singletony, jedna baza). Host nie zmienia Core, tylko daje **każdemu
użytkownikowi własny kontener DI** (`UserCoreFactory`, tworzony leniwie raz na identyfikator; `AddVusosCore` +
`AddVusosAdapters`, własny `IAppPaths`, `ISecureStore`, `IConnectivity`, `AdapterOptions.UserScope`) i uruchamia w nim
`BackgroundSyncCoordinator`. W kontenerze hosta usługi Core (`IConnectionManager`, `ISyncStatus`, `IMaterialService`,
`IQuizService`, `IDashboardService`, `IConnectivity`, `IAppPaths`, `UsosConnectFlow`, `PlatonConnectFlow`) są *scoped
forwarderami* do kontenera bieżącego użytkownika. Identyfikator użytkownika zapamiętuje `CircuitUser` na starcie obwodu
(z `AuthenticationStateProvider`; `HttpContext` nie jest wiarygodny po pierwszym renderze). Kilka kart tego samego
użytkownika dzieli jeden stan. Dane są zwalniane z pamięci po `Server:UserIdleTimeout` (domyślnie 2 h) bez dostępu i bez
otwartego obwodu; pliki zostają na dysku.

## Katalog danych (`Storage:Root`, domyślnie `./data`)

```
{Storage:Root}/keys/                        klucze ASP.NET Core Data Protection (cookie, tokeny /dl, secure.json) – KOPIA ZAPASOWA
{Storage:Root}/users/{sha256(userId)[..16]}/vusos.db      cache, materiały, testy offline (SQLite)
{Storage:Root}/users/{hash}/secure.json     poświadczenia USOS/Platon, zaszyfrowane Data Protection (purpose vusos:secure:{userId})
{Storage:Root}/users/{hash}/materials/      pobrane pliki ({courseId}/{moduleId}/…)
```

Utrata `keys/` = użytkownicy muszą się zalogować i ponownie połączyć systemy. Na Linuksie klucze leżą na dysku bez szyfrowania
(`No XML encryptor configured`) – chroń katalog uprawnieniami/wolumenem. Pobieranie plików: `/dl/{token}`; token jest
krótkotrwały, związany z użytkownikiem i ważny tylko dla pliku w jego `materials/`.

## Inne ustawienia

- `Server:PublicUrl` – publiczny adres (callbacki OIDC i USOS); pusty = adres z żądania.
- `Server:RedirectToHttps` (domyślnie `true`, poza Development) i `Server:UseForwardedHeaders` – za reverse proxy
  terminującym TLS ustaw `RedirectToHttps=false`, `UseForwardedHeaders=true`.
- `University`, `Cache` – jak w Vusos.Core (patrz `docs/ARCHITECTURE.md`).

## Docker

```bash
cd dotnet
docker build -f src/Vusos.WebHost/Dockerfile -t vusos-web .
docker run -p 8080:8080 -v vusos-data:/data \
  -e Auth__ClientId=… -e Auth__ClientSecret=… -e Server__PublicUrl=https://vusos.example.pl \
  -e University__SigningServerUrl=https://vusos-server.example.pl/ vusos-web
```

Obraz domyślnie ma `Adapters__Mode=live`, `Auth__Mode=entra`, dane w wolumenie `/data`. Do demonstracji:
`-e Adapters__Mode=mock -e Auth__Mode=dev -e Auth__AllowDevInProduction=true`.
