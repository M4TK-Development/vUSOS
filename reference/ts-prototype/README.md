# Prototyp TypeScript (referencyjny)

Pierwsza iteracja vUSOS (Fastify BFF + React + Capacitor/Tauri). Zastąpiona implementacją .NET w `dotnet/`.
Zostaje w repo jako specyfikacja wykonawcza: adaptery USOS i Platon z fixture'ami i testami (ok. 140 testów)
opisują dokładnie, które metody USOS API / Moodle WS są używane i jak mapują się na model domenowy.

```bash
cd reference/ts-prototype && pnpm install && pnpm --filter @vusos/api test
```
