/** Runtime detection (browser vs Capacitor / Tauri shell) and API base URL. */

declare global {
  interface Window {
    Capacitor?: { isNativePlatform?: () => boolean };
    __TAURI__?: unknown;
    __TAURI_INTERNALS__?: unknown;
    vusosHandleDeepLink?: (url: string) => void;
  }
  interface ImportMetaEnv {
    readonly VITE_API_BASE_URL?: string;
  }
}

export function isNative(): boolean {
  if (typeof window === "undefined") return false;
  return (
    window.Capacitor?.isNativePlatform?.() === true ||
    window.__TAURI__ !== undefined ||
    window.__TAURI_INTERNALS__ !== undefined
  );
}

/** Absolute origin of the BFF when native (VITE_API_BASE_URL); "" on web, where /api is proxied/same-origin. */
export function apiBase(): string {
  return (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/+$/, "");
}

/** Opens a URL in the system browser (native) or a new tab (web). */
export function openExternal(url: string): void {
  window.open(url, "_blank", "noopener,noreferrer");
}
