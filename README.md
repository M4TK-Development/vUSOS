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
apps/web          React + Vite PWA
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

## Licencja

GPL-3.0.
