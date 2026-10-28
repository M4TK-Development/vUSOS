# Vusos.Server

Mały, bezstanowy serwis ASP.NET Core (.NET 10), który podpisuje żądania do USOS API (OAuth 1.0a, HMAC-SHA1)
kluczem konsumenta instalacji. Dzięki temu `ConsumerSecret` nie trafia do aplikacji mobilnej/desktopowej.
Serwer **niczego nie przechowuje** – tokeny użytkownika przychodzą w nagłówkach każdego żądania i nie są logowane.

## Endpointy

| Metoda | Ścieżka | Opis |
|---|---|---|
| POST | `/usos/oauth/request_token` | `{callbackUrl, scopes[]}` → `{token, secret, authorizeUrl}` |
| POST | `/usos/oauth/access_token` | `{token, secret, verifier}` → `{token, secret}`; odrzucenie przez USOS → `401 {code:"OAUTH_REJECTED"}` |
| GET | `/usos/oauth/callback?oauth_token&oauth_verifier&scheme=vusos` | 302 na `{scheme}://usos?...` (tylko dozwolone schematy) – awaryjnie, gdy USOS nie przyjmuje callbacku z własnym schematem |
| GET/POST | `/usos/proxy/{ścieżka}` | nagłówki `X-Usos-Token`, `X-Usos-Token-Secret`; zapytanie i formularz trafiają do `{ApiBaseUrl}services/{ścieżka}`, odpowiedź USOS wraca bez zmian |
| GET | `/health` | `{status:"ok", mode}` |

Dozwolone prefiksy proxy (po `services/`): `users/`, `tt/`, `grades/`, `payments/`, `courses/`, `terms/`,
`mailclient/`, `news/`, `fac/`, `apisrv/`, `oauth/revoke_token`. Inne ścieżki → `403`.
Timeout USOS (20 s) → `504 {code:"UPSTREAM_TIMEOUT"}`. Limit: 120 żądań/min na IP (`429`).

## Konfiguracja

`appsettings.json` / zmienne środowiskowe (separator `__`):

| Klucz | Zmienna | Opis |
|---|---|---|
| `Usos:ApiBaseUrl` | `Usos__ApiBaseUrl` | korzeń instalacji, np. `https://usosapps.uczelnia.edu.pl/` |
| `Usos:ConsumerKey` | `Usos__ConsumerKey` | **wymagane** (chyba że `Usos:Mode=mock`) |
| `Usos:ConsumerSecret` | `Usos__ConsumerSecret` | **wymagane** (chyba że `Usos:Mode=mock`) |
| `Usos:AppKey` | `Usos__AppKey` | opcjonalnie: gdy ustawiony, `/usos/*` wymaga nagłówka `X-Vusos-App-Key` (poza `/usos/oauth/callback`) |
| `Usos:AllowedCallbackSchemes` | `Usos__AllowedCallbackSchemes__0=vusos` … | domyślnie `vusos`, `https`, `http` |
| `Usos:TimeoutSeconds` | `Usos__TimeoutSeconds` | domyślnie 20 |
| `Usos:Mode` | `Usos__Mode` | `live` (domyślnie) lub `mock` – pomija wymóg kluczy (tylko dev/testy) |
| `RateLimit:PermitPerMinute` | `RateLimit__PermitPerMinute` | domyślnie 120 |
| `Cors:Origins` | `Cors__Origins__0=https://...` | originy Blazor WebHost (klienci natywni CORS-u nie potrzebują) |
| `Server:TrustForwardedHeaders` | `Server__TrustForwardedHeaders=true` | za reverse proxy: bierze IP z `X-Forwarded-For` (limit per IP) |

Brak `ConsumerKey`/`ConsumerSecret` przerywa start aplikacji.

## Rejestracja klucza konsumenta USOS API

1. Zaloguj się na stronie instalacji USOS API swojej uczelni i otwórz `https://usosapps.<uczelnia>/developers/`
   (np. `https://usosapps.vistula.edu.pl/developers/`).
2. Wybierz „Zarejestruj nową aplikację” (Register a new consumer key) i podaj nazwę, opis oraz e-mail kontaktowy.
3. W polu callbacku/adresów URL wpisz adres, na który USOS może przekierować po autoryzacji. Jeśli instalacja
   akceptuje własny schemat, użyj `vusos://usos`; w przeciwnym razie `https://TWÓJ-SERWER/usos/oauth/callback?scheme=vusos`
   (serwer przekieruje wtedy na `vusos://usos?...`). Callback przekazujemy też w `request_token` (`oauth_callback`).
4. Zaznacz potrzebne zakresy (scopes): `offline_access`, `studies`, `grades`, `payments`, `mailclient`, `other_emails` itd.
   – zgodnie z tym, co aplikacja wysyła w `scopes[]`.
5. Skopiuj **Consumer Key** i **Consumer Secret** i ustaw je jako `Usos__ConsumerKey` / `Usos__ConsumerSecret`
   (nigdy w repozytorium). Klucz obowiązuje tylko dla tej instalacji USOS; część uczelni wymaga zatwierdzenia
   przez administratora USOS API.

## Uruchomienie

```bash
cd dotnet
Usos__Mode=mock dotnet run --project src/Vusos.Server           # lokalnie, bez kluczy
docker build -f src/Vusos.Server/Dockerfile -t vusos-server .
docker run -p 8080:8080 \
  -e Usos__ApiBaseUrl=https://usosapps.uczelnia.edu.pl/ \
  -e Usos__ConsumerKey=... -e Usos__ConsumerSecret=... -e Usos__AppKey=... vusos-server
dotnet test tests/Vusos.Server.Tests
```

Za reverse proxy (TLS) ustaw `Server__TrustForwardedHeaders=true`. Logi zawierają tylko metodę, ścieżkę proxy,
status i czas; nigdy tokenów, sekretów ani query stringów.
