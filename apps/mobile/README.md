# vUSOS mobile (Capacitor 6)

iOS i Android (telefon + tablet) na buildzie `apps/web`. Aplikacja rejestruje schemat `vusos://`, dzięki czemu
logowanie M365, połączenie z USOS (OAuth) i z Platonem (`launch.php?urlscheme=vusos`) wracają do aplikacji bez wklejania.

```bash
pnpm install
VITE_API_BASE_URL=https://twoj-bff.example.pl pnpm --filter @vusos/web build
pnpm --filter @vusos/mobile exec cap sync
pnpm --filter @vusos/mobile android   # Android Studio
pnpm --filter @vusos/mobile ios       # Xcode (tylko macOS)
```

## Schemat `vusos://`

Android — `android/app/src/main/AndroidManifest.xml`, w `<activity android:name=".MainActivity">`:

```xml
<intent-filter>
  <action android:name="android.intent.action.VIEW" />
  <category android:name="android.intent.category.DEFAULT" />
  <category android:name="android.intent.category.BROWSABLE" />
  <data android:scheme="vusos" />
</intent-filter>
```

iOS — `ios/App/App/Info.plist`:

```xml
<key>CFBundleURLTypes</key>
<array>
  <dict>
    <key>CFBundleURLName</key><string>pl.m4tk.vusos</string>
    <key>CFBundleURLSchemes</key><array><string>vusos</string></array>
  </dict>
</array>
```

Skrypt `scripts/patch-native.mjs` wprowadza oba wpisy automatycznie po `cap add`.
