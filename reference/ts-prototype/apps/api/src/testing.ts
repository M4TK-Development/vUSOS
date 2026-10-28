import { randomBytes } from "node:crypto";
import type { FastifyInstance } from "fastify";
import { buildApp, type BuildAppOptions } from "./app.js";
import { loadConfig, type AppConfig } from "./config.js";
import { openDb, type Db } from "./db.js";

/** Test helpers (not used by the production code path). */
export function testConfig(over: Record<string, string | undefined> = {}): AppConfig {
  return loadConfig({
    NODE_ENV: "test",
    LOG_LEVEL: "silent",
    SESSION_SECRET: "test-session-secret-test-session-secret-0000",
    CREDENTIALS_KEY: randomBytes(32).toString("base64"),
    DATABASE_PATH: ":memory:",
    AUTH_MODE: "dev",
    ADAPTER_MODE: "mock",
    WEB_ORIGIN: "http://localhost:5173",
    PUBLIC_URL: "http://localhost:3001",
    PLATON_BASE_URL: "https://platon.example.edu/",
    USOS_API_BASE_URL: "https://usosapps.example.edu/",
    USOSWEB_BASE_URL: "https://usosweb.example.edu/",
    ...over
  });
}

export function makeApp(
  over: Record<string, string | undefined> = {},
  opts: BuildAppOptions = {}
): { app: ReturnType<typeof buildApp>; db: Db; config: AppConfig } {
  const config = testConfig(over);
  const db = openDb(":memory:");
  const app = buildApp(config, db, opts);
  return { app, db, config };
}

/** Dev-mode JSON login; returns the Cookie header value for subsequent requests. */
export async function loginDev(
  app: FastifyInstance,
  email = "jan@stu.vistula.edu.pl",
  name = "Jan Kowalski"
): Promise<string> {
  const res = await app.inject({ method: "POST", url: "/api/auth/login", payload: { email, name } });
  if (res.statusCode !== 200) throw new Error(`dev login failed: ${res.statusCode} ${res.body}`);
  const c = res.cookies.find((x) => x.name === "vusos_session");
  if (!c) throw new Error("no session cookie");
  return `vusos_session=${encodeURIComponent(c.value)}`;
}
