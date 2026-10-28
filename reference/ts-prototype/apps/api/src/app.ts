import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import cors from "@fastify/cors";
import secureSession from "@fastify/secure-session";
import fastifyStatic from "@fastify/static";
import Fastify, { type FastifyInstance, type FastifyServerOptions } from "fastify";
import { ZodError } from "zod";
import { createRegistry, type AdapterRegistry } from "./adapters/registry.js";
import type { UsosOAuthClient } from "./adapters/usos/index.js";
import type { EntraClient } from "./auth/entra.js";
import { authRoutes, publicUser } from "./auth/routes.js";
import { createAuthGuard, currentUser } from "./auth/session.js";
import type { AppConfig } from "./config.js";
import type { Db } from "./db.js";
import { HttpError } from "./errors.js";
import { connectionRoutes, usosOAuthCallbackRoute } from "./routes/connections.js";
import { dashboardRoutes } from "./routes/dashboard.js";
import { platonRoutes } from "./routes/platon.js";
import { usosRoutes } from "./routes/usos.js";
import { createStore, type Store } from "./store.js";

const SESSION_TTL_S = 7 * 24 * 60 * 60;

export interface BuildAppOptions {
  /** Test seams. */
  registry?: AdapterRegistry;
  usosOAuthClient?: UsosOAuthClient;
  entra?: EntraClient;
  store?: Store;
}

export interface AppContext {
  store: Store;
  registry: AdapterRegistry;
}

function loggerOptions(config: AppConfig): FastifyServerOptions["logger"] {
  if (config.logLevel === "silent") return false;
  return {
    level: config.logLevel,
    // Never log query strings (OAuth verifiers, file URLs) or headers (cookies, bearer tokens).
    serializers: {
      req: (req: { method?: string; url?: string }) => ({ method: req.method, url: req.url?.split("?")[0] })
    },
    ...(config.nodeEnv === "production"
      ? {}
      : { transport: { target: "pino-pretty", options: { translateTime: "HH:MM:ss", ignore: "pid,hostname" } } })
  };
}

const isCode = (e: unknown, code: string): boolean =>
  e instanceof Error && (e as { code?: unknown }).code === code;

/** Builds the Fastify instance (plugins are registered lazily; `inject`/`listen` await readiness). */
export function buildApp(config: AppConfig, db: Db, opts: BuildAppOptions = {}): FastifyInstance & { ctx: AppContext } {
  const store = opts.store ?? createStore(db, config.credentialsKey);
  const registry = opts.registry ?? createRegistry(config, store);
  const app = Fastify({ logger: loggerOptions(config), trustProxy: config.nodeEnv === "production" });

  // ---- Body parsing: treat an empty JSON body as {} so clients may send Content-Type without a payload.
  const defaultJson = app.getDefaultJsonParser("error", "ignore");
  app.removeContentTypeParser("application/json");
  app.addContentTypeParser("application/json", { parseAs: "string" }, (req, body, done) => {
    if (body === "") return done(null, {});
    defaultJson(req, body as string, done);
  });

  // ---- CORS + a cheap Origin check for state-changing requests.
  const allowedOrigins = new Set([...config.corsOrigins, new URL(config.publicUrl).origin]);
  void app.register(cors, {
    origin: config.corsOrigins,
    credentials: true,
    methods: ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]
  });
  app.addHook("onRequest", async (req) => {
    const origin = req.headers.origin;
    if (origin && !["GET", "HEAD", "OPTIONS"].includes(req.method) && !allowedOrigins.has(origin)) {
      throw new HttpError(403, { code: "FORBIDDEN_ORIGIN", message: "Niedozwolone źródło żądania." });
    }
  });

  // ---- Session cookie (web client).
  void app.register(secureSession, {
    key: createHash("sha256").update(`vusos-session-v1:${config.sessionSecret}`).digest(),
    cookieName: "vusos_session",
    expiry: SESSION_TTL_S,
    cookie: {
      path: "/",
      httpOnly: true,
      sameSite: "lax",
      secure: config.publicUrl.startsWith("https://"),
      maxAge: SESSION_TTL_S
    }
  });
  app.decorateRequest("user", null);
  app.decorateRequest("bearerToken", null);

  // ---- Errors.
  app.setErrorHandler((err, req, reply) => {
    if (err instanceof HttpError) return reply.code(err.statusCode).send(err.body);
    if (err instanceof ZodError || (err as Error).name === "ZodError") {
      return reply.code(400).send({ code: "BAD_REQUEST", message: "Nieprawidłowe dane żądania.", issues: (err as ZodError).issues });
    }
    if (isCode(err, "ADAPTER_AUTH")) return reply.code(424).send({ code: "ADAPTER_AUTH", message: (err as Error).message });
    if (isCode(err, "ADAPTER_UNAVAILABLE")) {
      return reply.code(502).send({ code: "ADAPTER_UNAVAILABLE", message: (err as Error).message });
    }
    const status = (err as { statusCode?: number }).statusCode;
    if (typeof status === "number" && status >= 400 && status < 500) {
      return reply.code(status).send({ code: "BAD_REQUEST", message: (err as Error).message });
    }
    req.log.error({ err }, "unhandled error");
    return reply.code(500).send({ code: "INTERNAL", message: "Wewnętrzny błąd serwera." });
  });

  // ---- Static web app (optional) with SPA fallback.
  const staticDir = config.staticDir ? resolve(config.staticDir) : null;
  if (staticDir && existsSync(staticDir)) {
    void app.register(fastifyStatic, { root: staticDir, wildcard: true });
  }
  app.setNotFoundHandler((req, reply) => {
    const isApi = req.url === "/api" || req.url.startsWith("/api/");
    if (staticDir && !isApi && (req.method === "GET" || req.method === "HEAD") && (req.headers.accept ?? "").includes("text/html")) {
      return reply.sendFile("index.html");
    }
    return reply.code(404).send({ code: "NOT_FOUND", message: "Nie znaleziono." });
  });

  // ---- Routes.
  const guard = createAuthGuard(store);
  void app.register(
    async (api) => {
      await api.register(async (pub) => {
        pub.get("/health", async () => ({ ok: true }));
        await authRoutes(pub, { config, store, ...(opts.entra ? { entra: opts.entra } : {}) });
        await usosOAuthCallbackRoute(pub, { config, store, registry, ...(opts.usosOAuthClient ? { usosOAuthClient: opts.usosOAuthClient } : {}) });
      });
      await api.register(async (priv) => {
        priv.addHook("onRequest", guard);
        priv.get("/me", async (req) => {
          const user = currentUser(req);
          return { user: publicUser(user), connections: registry.statuses(user.id) };
        });
        await connectionRoutes(priv, { config, store, registry, ...(opts.usosOAuthClient ? { usosOAuthClient: opts.usosOAuthClient } : {}) });
        await usosRoutes(priv, registry);
        await platonRoutes(priv, registry, config);
        await dashboardRoutes(priv, registry);
      });
    },
    { prefix: "/api" }
  );

  return Object.assign(app, { ctx: { store, registry } });
}
