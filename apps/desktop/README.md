# vUSOS desktop (Tauri 2)

Powłoka desktopowa (Windows, macOS, Linux) dla buildu `apps/web`. Rejestruje schemat `vusos://` i przekazuje deep linki
do frontendu (`window.vusosHandleDeepLink`).

Wymagania: Rust stable, [zależności systemowe Tauri](https://v2.tauri.app/start/prerequisites/)
(Linux: `libwebkit2gtk-4.1-dev`, `libgtk-3-dev`, `libayatana-appindicator3-dev`, `librsvg2-dev`).

```bash
pnpm install
# ikony: wygeneruj z apps/web/public/icon.svg (potrzebny plik PNG 1024x1024 jako wejście)
pnpm --filter @vusos/desktop tauri icon ../web/public/icon-1024.png
pnpm --filter @vusos/desktop dev      # frontend z Vite + okno Tauri
pnpm --filter @vusos/desktop build    # instalatory w src-tauri/target/release/bundle
```

Frontend w trybie desktop czyta adres BFF z `VITE_API_BASE_URL` (ustaw przy buildzie `apps/web`).
