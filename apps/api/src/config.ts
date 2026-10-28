import { z } from "zod";
import type { PlatonConfig } from "./adapters/platon/index.js";
import type { UsosConfig } from "./adapters/usos/index.js";

/** Empty strings in .env files mean "unset". */
const emptyToUndefined = (v: unknown): unknown => (typeof v === "string" && v.trim() === "" ? undefined : v);
const optStr = z.preprocess(emptyToUndefined, z.string().optional());
const csv = z.preprocess(
  (v) => (typeof v === "string" ? v.split(",").map((s) => s.trim()).filter(Boolean) : v),
  z.array(z.string()).default([])
);
const url = (def: string) => z.preprocess(emptyToUndefined, z.string().url().default(def));

const DEFAULT_NATIVE_ORIGINS = ["capacitor://localhost", "http://localhost", "tauri://localhost", "https://tauri.localhost"];

const EnvSchema = z.object({
  NODE_ENV: z.enum(["development", "test", "production"]).default("development"),
  LOG_LEVEL: z.enum(["fatal", "error", "warn", "info", "debug", "trace", "silent"]).default("info"),
  HOST: z.preprocess(emptyToUndefined, z.string().default("localhost")),
  PORT: z.preprocess(emptyToUndefined, z.coerce.number().int().min(0).max(65535).default(3001)),
  PUBLIC_URL: url("http://localhost:3001"),
  WEB_ORIGIN: url("http://localhost:5173"),
  // Extra CORS origins (native shells). Defaults: Capacitor iOS/Android, Tauri macOS/Linux/Windows.
  WEB_ORIGINS: z.preprocess(
    (v) => (typeof v === "string" ? (v.trim() === "" ? undefined : v.split(",").map((s) => s.trim()).filter(Boolean)) : v),
    z.array(z.string()).default(DEFAULT_NATIVE_ORIGINS)
  ),
  NATIVE_SCHEME: z.preprocess(emptyToUndefined, z.string().regex(/^[a-z][a-z0-9+.-]*$/i, "NATIVE_SCHEME: niepoprawny schemat URL").default("vusos")),
  STATIC_DIR: optStr,
  SESSION_SECRET: z.string().min(32, "SESSION_SECRET musi mieć co najmniej 32 znaki"),
  CREDENTIALS_KEY: z.string().refine((v) => {
    try {
      return Buffer.from(v, "base64").length === 32;
    } catch {
      return false;
    }
  }, "CREDENTIALS_KEY musi być base64 z dokładnie 32 bajtów (openssl rand -base64 32)"),
  DATABASE_PATH: z.preprocess(emptyToUndefined, z.string().default("./data/vusos.sqlite")),
  AUTH_MODE: z.enum(["dev", "entra"]).default("dev"),
  ENTRA_CLIENT_ID: optStr,
  ENTRA_CLIENT_SECRET: optStr,
  ENTRA_AUTHORITY: url("https://login.microsoftonline.com/organizations"),
  ALLOWED_EMAIL_DOMAINS: csv,
  ALLOWED_TENANT_IDS: csv,
  USOS_API_BASE_URL: url("https://usosapps.vistula.edu.pl/"),
  USOS_CONSUMER_KEY: optStr,
  USOS_CONSUMER_SECRET: optStr,
  USOSWEB_BASE_URL: url("https://usosweb.vistula.edu.pl/"),
  PLATON_BASE_URL: url("https://platon.vistula.edu.pl/"),
  ADAPTER_MODE: z.enum(["mock", "live"]).default("mock"),
  CACHE_TTL_SECONDS: z.preprocess(emptyToUndefined, z.coerce.number().int().min(0).default(60))
});

export interface AppConfig {
  nodeEnv: "development" | "test" | "production";
  logLevel: "fatal" | "error" | "warn" | "info" | "debug" | "trace" | "silent";
  host: string;
  port: number;
  /** No trailing slash. */
  publicUrl: string;
  /** No trailing slash. */
  webOrigin: string;
  /** All origins allowed by CORS: webOrigin plus the native shells. */
  corsOrigins: string[];
  /** Custom URL scheme of the native apps (without "://"). */
  nativeScheme: string;
  /** If set, the built web app is served from this directory (SPA fallback). */
  staticDir: string | null;
  sessionSecret: string;
  credentialsKey: Buffer;
  databasePath: string;
  authMode: "dev" | "entra";
  entra: { clientId: string; clientSecret: string; authority: string };
  allowedEmailDomains: string[];
  allowedTenantIds: string[];
  usos: UsosConfig;
  platon: PlatonConfig;
  adapterMode: "mock" | "live";
  cacheTtlMs: number;
}

const trimSlash = (s: string): string => s.replace(/\/+$/, "");

export function loadConfig(env: Record<string, string | undefined> = process.env): AppConfig {
  const parsed = EnvSchema.safeParse(env);
  if (!parsed.success) {
    const msg = parsed.error.issues.map((i) => `  ${i.path.join(".")}: ${i.message}`).join("\n");
    throw new Error(`Nieprawidłowa konfiguracja środowiska:\n${msg}`);
  }
  const e = parsed.data;
  if (e.AUTH_MODE === "entra" && (!e.ENTRA_CLIENT_ID || !e.ENTRA_CLIENT_SECRET)) {
    throw new Error("AUTH_MODE=entra wymaga ENTRA_CLIENT_ID i ENTRA_CLIENT_SECRET");
  }
  if (e.AUTH_MODE === "entra" && e.ALLOWED_EMAIL_DOMAINS.length === 0 && e.ALLOWED_TENANT_IDS.length === 0) {
    throw new Error("AUTH_MODE=entra wymaga ALLOWED_TENANT_IDS lub ALLOWED_EMAIL_DOMAINS (inaczej nikt się nie zaloguje)");
  }
  if (e.NODE_ENV === "production" && e.AUTH_MODE === "dev") {
    throw new Error("AUTH_MODE=dev jest niedozwolony w produkcji (NODE_ENV=production)");
  }
  return {
    nodeEnv: e.NODE_ENV,
    logLevel: e.LOG_LEVEL,
    host: e.HOST,
    port: e.PORT,
    publicUrl: trimSlash(e.PUBLIC_URL),
    webOrigin: trimSlash(e.WEB_ORIGIN),
    corsOrigins: [...new Set([trimSlash(e.WEB_ORIGIN), ...e.WEB_ORIGINS.map(trimSlash)])],
    nativeScheme: e.NATIVE_SCHEME.toLowerCase(),
    staticDir: e.STATIC_DIR ?? null,
    sessionSecret: e.SESSION_SECRET,
    credentialsKey: Buffer.from(e.CREDENTIALS_KEY, "base64"),
    databasePath: e.DATABASE_PATH,
    authMode: e.AUTH_MODE,
    entra: {
      clientId: e.ENTRA_CLIENT_ID ?? "",
      clientSecret: e.ENTRA_CLIENT_SECRET ?? "",
      authority: trimSlash(e.ENTRA_AUTHORITY)
    },
    allowedEmailDomains: e.ALLOWED_EMAIL_DOMAINS.map((d) => d.toLowerCase().replace(/^@/, "")),
    allowedTenantIds: e.ALLOWED_TENANT_IDS.map((t) => t.toLowerCase()),
    usos: {
      apiBaseUrl: e.USOS_API_BASE_URL,
      consumerKey: e.USOS_CONSUMER_KEY ?? "",
      consumerSecret: e.USOS_CONSUMER_SECRET ?? "",
      usoswebBaseUrl: e.USOSWEB_BASE_URL
    },
    platon: { baseUrl: e.PLATON_BASE_URL },
    adapterMode: e.ADAPTER_MODE,
    cacheTtlMs: e.CACHE_TTL_SECONDS * 1000
  };
}
