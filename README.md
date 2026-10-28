# vUSOS

Jednolita platforma studenta Vistula. Jedno logowanie kontem M365 uczelni, jeden interfejs dla danych z dwóch systemów:

| Źródło | Co pokazujemy |
|---|---|
| **USOSweb** (`usosweb.vistula.edu.pl`) | plan zajęć, oceny, opłaty, wiadomości i kontakt z dziekanatem, ogłoszenia |
| **Platon** (`platon.vistula.edu.pl`, Moodle) | kursy, materiały do pobrania, zadania i terminy, oceny z kursów, ogłoszenia |

Projekt nieoficjalny, pisany z pozycji studenta (uprawnienia end-user, bez dostępu administracyjnego do USOS, Moodle czy Entra ID uczelni). Szczegóły decyzji: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Struktura

```
apps/api          Fastify BFF: auth M365, zaszyfrowany magazyn poświadczeń, adaptery USOS/Platon, agregacja
apps/web          React + Vite — jeden kod UI dla web, mobile i desktop
apps/mobile       Capacitor 6 — iOS i Android (telefon, tablet)
apps/desktop      Tauri 2 — Windows, macOS, Linux
packages/shared   model domenowy (zod) + interfejsy adapterów
docs/             architektura
deploy/           Dockerfile, docker-compose
```

## Szybki start (tryb demo, bez dostępu do uczelni)

```bash
pnpm install
cp apps/api/.env.example apps/api/.env
pnpm dev
```

`http://localhost:5173` → logowanie dev (dowolny e-mail) → dane z adapterów `mock`.

## Tryb produkcyjny

1. Rejestracja aplikacji w Entra ID (multi-tenant, redirect `PUBLIC_URL/api/auth/callback`) → `ENTRA_*`, `AUTH_MODE=entra`.
2. Consumer key USOS API z `/developers/` instalacji USOS uczelni → `USOS_CONSUMER_*`, `USOS_API_BASE_URL`.
3. `ADAPTER_MODE=live`, wygenerowane `SESSION_SECRET` i `CREDENTIALS_KEY`.
4. `docker compose -f deploy/docker-compose.yml up -d`.

Każdy użytkownik podłącza swoje konta USOS i Platon w **Ustawienia → Połączenia** (szczegóły w `apps/api/README.md`).

## Aplikacje natywne

Ten sam build `apps/web` trafia do powłok natywnych. Aplikacje rejestrują schemat `vusos://`, więc logowanie M365,
OAuth USOS i token Platona (`launch.php?urlscheme=vusos`) wracają do aplikacji bez wklejania.

```bash
VITE_API_BASE_URL=https://twoj-bff.example.pl pnpm --filter @vusos/web build
pnpm --filter @vusos/mobile add:android && pnpm --filter @vusos/mobile android   # Android Studio
pnpm --filter @vusos/mobile add:ios && pnpm --filter @vusos/mobile ios           # Xcode, macOS
pnpm --filter @vusos/desktop build                                               # instalatory Tauri
```

Szczegóły: `apps/mobile/README.md`, `apps/desktop/README.md`.

## Licencja

GPL-3.0.
