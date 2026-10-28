# vUSOS — architektura

Jednolita platforma studenta Vistula: dane z **USOSweb** (dziekanat: oceny, opłaty, plan, wiadomości, ogłoszenia)
i **Platon** (Moodle: kursy, materiały, zadania, terminy) w jednym UI. Logowanie kontem M365 uczelni.

## Ograniczenia wyjściowe (end-user only)

Mamy wyłącznie uprawnienia studenta. Nie ma dostępu admina do USOS, Moodle ani tenantu Entra ID uczelni.
Wnioski projektowe:

1. **Nie istnieje jeden token M365 otwierający USOSweb i Platon.** Oba systemy akceptują tylko własne sesje
   (USOSweb: sesja PHP po SSO; Moodle: `MoodleSession` lub token web services). Entra ID jest tylko IdP.
   Dlatego logowanie M365 do aplikacji daje *tożsamość* (kto to jest, czy to konto `*.vistula.edu.pl`),
   a do systemów dziedzinowych aplikacja podłącza się osobno, jednym kliknięciem każdy — dzięki SSO w przeglądarce
   (cookie Entra "keep me signed in") drugie i trzecie logowanie są praktycznie bezklikowe.
2. **USOS — tryb preferowany: USOS API (OAuth 1.0a).** Każda instalacja USOS wystawia USOS API
   (`/services/...`), a consumer key rejestruje się samoobsługowo na `/developers/`. Autoryzacja użytkownika
   odbywa się przez przeglądarkę (`services/oauth/authorize`) → przekierowanie do logowania USOSweb (SSO M365)
   → callback z verifierem. Scope `offline_access` daje token długoterminowy. Metody: `users/user`, `tt/user`,
   `grades/terms2`, `payments/user_payments`, `mailclient/*`, `news/*`, `courses/user`, `terms/search`.
   Adres instalacji dla Vistula jest konfigurowalny (`USOS_API_BASE_URL`); jeśli uczelnia nie wystawia API
   publicznie, fallback poniżej.
3. **USOS — fallback: tryb `web`.** Adapter pracujący na cookies sesji USOSweb (parsowanie HTML `kontroler.php`).
   Cookies dostarcza rozszerzenie przeglądarki (connector) lub użytkownik ręcznie. Wrażliwy na zmiany HTML,
   dlatego osobny moduł z fixture'ami.
4. **Platon — tryb preferowany: Moodle Web Services (`webservice/rest/server.php`, `wstoken`).** Token dla konta
   OIDC uzyskuje się flow'em aplikacji mobilnej (`admin/tool/mobile/launch.php?service=moodle_mobile_app&passport=…`),
   który po SSO przekierowuje na `moodlemobile://token=BASE64(passport:::token:::privatetoken)`. Aplikacja
   webowa nie przechwyci tego schematu, więc token wkleja użytkownik albo łapie go connector (rozszerzenie).
5. **Platon — fallback: tryb `web`.** Cookie `MoodleSession` + `sesskey` → wywołania `lib/ajax/service.php`
   z tymi samymi funkcjami WS (`core_course_get_enrolled_courses_by_timeline_classification`,
   `core_course_get_contents`, `core_calendar_get_action_events_by_timesort`, `mod_assign_get_assignments`,
   `gradereport_user_get_grade_items`, `core_webservice_get_site_info`). Jedna warstwa funkcji, dwa transporty.
6. **Tryb `mock`** dla obu adapterów — deterministyczne fixture'y, do dev/testów i demo bez dostępu do uczelni.

## Komponenty

```
apps/api        Fastify (Node 22, TS) — BFF: auth M365 (OIDC), magazyn poświadczeń (SQLite przez wbudowany `node:sqlite`, AES-256-GCM),
                adaptery USOS/Platon, cache, agregacja dashboardu, proxy pobierania plików.
apps/web        React + Vite + TS, Tailwind, TanStack Query, react-router. Jeden kod UI dla wszystkich platform.
apps/mobile     Capacitor 6: iOS + Android (telefon i tablet), ładuje build apps/web, rejestruje schemat vusos://.
apps/desktop    Tauri 2: Windows / macOS / Linux, ten sam build apps/web, deep link vusos:// (tauri-plugin-deep-link).
packages/shared Model domenowy (zod) + interfejsy adapterów. Jedyne źródło prawdy dla typów.
```

## Klienci natywni (telefon, tablet, komputer)

- Jeden frontend React, trzy powłoki: przeglądarka/PWA, Capacitor (iOS, Android), Tauri (desktop). UI responsywne:
  dolna nawigacja na telefonie, sidebar od szerokości tabletu, safe-area insets.
- Klient natywny rozmawia z BFF po HTTPS (`VITE_API_BASE_URL`), uwierzytelnia się **bearer tokenem** zamiast cookie
  (token w Keychain/Keystore przez Capacitor Preferences/SecureStorage, na desktopie w magazynie Tauri).
- Logowanie natywne: aplikacja otwiera systemową przeglądarkę na `/api/auth/login?client=native` → Entra → BFF
  wystawia jednorazowy kod i przekierowuje na `vusos://auth?code=…` → aplikacja wymienia kod na token
  (`POST /api/auth/native/exchange`).
- **Platon jednym kliknięciem**: aplikacja otwiera `admin/tool/mobile/launch.php?service=moodle_mobile_app&passport=…&urlscheme=vusos`;
  Moodle po SSO M365 przekierowuje na `vusos://token=BASE64(passport:::token:::privatetoken)`, aplikacja łapie
  deep link i zapisuje token w BFF. Bez wklejania. (Jeśli admin wymusił `forcedurlscheme`, zostaje tryb wklejania.)
- **USOS jednym kliknięciem**: OAuth 1.0a w systemowej przeglądarce; callback BFF przekierowuje na
  `vusos://connected?system=usos`.
- Schemat `vusos://` rejestrują: Capacitor (Info.plist `CFBundleURLTypes`, Android `intent-filter`), Tauri
  (`tauri-plugin-deep-link`). Na iOS dodatkowo możliwe Universal Links, nieobowiązkowe.

## Auth aplikacji (M365)

- OIDC przez `@azure/msal-node`, app registration **multi-tenant** w tenancie M4TK (nie wymaga admina Vistula;
  wymaga tylko, by tenant Vistula pozwalał użytkownikom na consent `openid profile email` — domyślnie tak).
- Po zalogowaniu weryfikujemy `tid` ∈ `ALLOWED_TENANT_IDS` lub domenę e-mail ∈ `ALLOWED_EMAIL_DOMAINS`.
- Sesja aplikacji: cookie HttpOnly, SameSite=Lax, podpisane (`@fastify/secure-session`).
- Tryb `AUTH_MODE=dev` — logowanie bez Entra (formularz z e-mailem) do pracy lokalnej.

## Bezpieczeństwo poświadczeń

- Tabela `connections(user_id, system, mode, payload_enc, status, updated_at)`; `payload_enc` = AES-256-GCM
  kluczem `CREDENTIALS_KEY` (32 B, base64). Klucz nie leży w bazie.
- BFF nigdy nie przyjmuje hasła do USOS/Platon. Tylko tokeny OAuth / cookies sesji / wstoken.
- Każdy użytkownik widzi wyłącznie swoje dane; brak kont współdzielonych; brak zapisu treści z systemów
  dziedzinowych poza cache w pamięci (TTL).

## REST API BFF (prefiks `/api`)

```
GET  /auth/login            → redirect do Entra (lub formularz dev)
GET  /auth/callback
POST /auth/logout
GET  /me                    → { user, connections: {usos, platon} }

GET  /connections                       → statusy
POST /connections/usos/oauth/start      → { authorizeUrl }
GET  /connections/usos/oauth/callback   → zapis tokenu, redirect do /settings
POST /connections/usos/web              → { cookies }               (fallback)
POST /connections/platon/ws             → { wstoken, privateToken? } (token z launch.php lub connector)
POST /connections/platon/web            → { cookies, sesskey }      (fallback)
DELETE /connections/:system

GET  /dashboard
GET  /usos/profile | /usos/terms | /usos/courses?term= | /usos/timetable?from=&to=
GET  /usos/grades?term= | /usos/payments | /usos/messages | /usos/messages/:id
POST /usos/messages       { to[], subject, body }
GET  /usos/contacts | /usos/announcements
GET  /platon/courses | /platon/courses/:id/contents | /platon/assignments | /platon/events?days=
GET  /platon/courses/:id/grades | /platon/announcements
GET  /platon/file?url=     → strumień pliku przez BFF (dokleja token/cookies)
```

Błędy: `401` brak sesji aplikacji, `409 { code: "NOT_CONNECTED", system }`, `424 { code: "ADAPTER_AUTH", system }`
gdy sesja w systemie dziedzinowym wygasła (UI pokazuje "Połącz ponownie").

## Konfiguracja (`apps/api/.env.example`)

```
PORT=3001
PUBLIC_URL=http://localhost:3001
WEB_ORIGIN=http://localhost:5173
SESSION_SECRET=            # >=32 znaki
CREDENTIALS_KEY=           # base64, 32 bajty
DATABASE_PATH=./data/vusos.sqlite
AUTH_MODE=dev|entra
ENTRA_CLIENT_ID=
ENTRA_CLIENT_SECRET=
ENTRA_AUTHORITY=https://login.microsoftonline.com/organizations
ALLOWED_EMAIL_DOMAINS=vistula.edu.pl,stu.vistula.edu.pl
ALLOWED_TENANT_IDS=
USOS_API_BASE_URL=https://usosapps.vistula.edu.pl/     # do potwierdzenia
USOS_CONSUMER_KEY=
USOS_CONSUMER_SECRET=
USOSWEB_BASE_URL=https://usosweb.vistula.edu.pl/
PLATON_BASE_URL=https://platon.vistula.edu.pl/
ADAPTER_MODE=mock|live
```

## Decyzje odrzucone

- Przechowywanie hasła uczelnianego i logowanie headless (Playwright) po stronie serwera: MFA, odpowiedzialność,
  łamie regulamin. Odrzucone.
- React Native / Flutter: osobny kod UI przy identycznym zakresie funkcji; Capacitor + Tauri reużywają jeden build.
- Rozszerzenie przeglądarki jako connector: zbędne, gdy powłoki natywne łapią deep linki `vusos://`.
