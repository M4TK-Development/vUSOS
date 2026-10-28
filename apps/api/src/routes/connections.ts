import { randomBytes } from "node:crypto";
import type { FastifyInstance } from "fastify";
import type { PlatonCredentials, SourceSystem, UsosCredentials } from "@vusos/shared";
import { AdapterAuthError, SourceSystem as SourceSystemSchema } from "@vusos/shared";
import { z } from "zod";
import { createUsosOAuthClient, type UsosOAuthClient } from "../adapters/usos/index.js";
import type { AdapterRegistry } from "../adapters/registry.js";
import { currentUser } from "../auth/session.js";
import type { AppConfig } from "../config.js";
import { adapterAuth, adapterUnavailable, badRequest, HttpError } from "../errors.js";
import { parsePlatonTokenInput, TokenParseError } from "../connections/platon-token.js";
import type { ConnectionInfo, Store } from "../store.js";

export const USOS_SCOPES = ["offline_access", "studies", "grades", "payments", "email", "mailclient", "personal", "photo"];

export interface ConnectionDeps {
  config: AppConfig;
  store: Store;
  registry: AdapterRegistry;
  /** Override for tests; defaults to the real OAuth 1.0a client built from config. */
  usosOAuthClient?: UsosOAuthClient;
}

interface UsosOAuthStateData {
  secret: string;
  client: "web" | "native";
}

const ClientSchema = z.enum(["web", "native"]);

function isAuthError(e: unknown): boolean {
  return e instanceof AdapterAuthError || (e instanceof Error && (e as { code?: unknown }).code === "ADAPTER_AUTH");
}

/** Where to send the browser after the USOS OAuth dance. */
function usosRedirect(config: AppConfig, client: "web" | "native", ok: boolean): string {
  if (client === "native") return `${config.nativeScheme}://connected?system=usos${ok ? "" : "&error=failed"}`;
  return ok ? `${config.webOrigin}/settings?connected=usos` : `${config.webOrigin}/settings?error=usos`;
}

/**
 * Validates credentials against the live system, then persists them. Nothing is stored if validation fails,
 * so a previously working connection is not clobbered by a mistyped token.
 */
async function connect(
  deps: ConnectionDeps,
  userId: string,
  system: SourceSystem,
  creds: UsosCredentials | PlatonCredentials,
  validate: () => Promise<Record<string, unknown> | void>
): Promise<ConnectionInfo> {
  const { store, registry } = deps;
  if (registry.mock) {
    store.saveConnection(userId, system, { mode: "mock" } as never, "connected");
  } else {
    let patch: Record<string, unknown> | void;
    try {
      patch = await validate();
    } catch (e) {
      if (isAuthError(e)) throw adapterAuth(system);
      throw adapterUnavailable(system, e instanceof Error ? e.message : undefined);
    }
    store.saveConnection(userId, system, { ...creds, ...(patch ?? {}) } as never, "connected");
  }
  registry.invalidate(userId, system);
  return registry.statuses(userId)[system];
}

const UsosWebBody = z.object({ cookies: z.string().trim().min(5).max(8000) });
const PlatonWsBody = z
  .object({
    wstoken: z.string().trim().min(1).max(4000).optional(),
    privateToken: z.string().trim().max(500).optional(),
    /** The full `vusos://token=BASE64` (or moodlemobile://...) string captured by the native shell. */
    launchUrl: z.string().trim().min(1).max(4000).optional()
  })
  .refine((b) => b.wstoken || b.launchUrl, { message: "Podaj wstoken lub launchUrl" });
const PlatonWebBody = z.object({
  cookies: z.string().trim().min(5).max(8000),
  sesskey: z.string().trim().min(3).max(200)
});
const OAuthStartBody = z.object({ client: ClientSchema.optional() }).default({});
const OAuthCallbackQuery = z.object({
  oauth_token: z.string().optional(),
  oauth_verifier: z.string().optional(),
  /** Mock mode only: the oauth state created by /start. */
  mock: z.string().optional()
});

/** Public route: the USOS callback is opened in a browser that may carry no app session (native clients). */
export async function usosOAuthCallbackRoute(app: FastifyInstance, deps: ConnectionDeps): Promise<void> {
  const { config, store, registry } = deps;

  app.get("/connections/usos/oauth/callback", async (req, reply) => {
    const q = OAuthCallbackQuery.safeParse(req.query);
    if (!q.success) return reply.redirect(usosRedirect(config, "web", false), 302);
    const state = q.data.mock ?? q.data.oauth_token;
    const found = state ? store.consumeOAuthState<UsosOAuthStateData>(state, "usos") : null;
    if (!state || !found) return reply.redirect(usosRedirect(config, "web", false), 302);
    const { userId, data } = found;
    const done = (ok: boolean) => reply.redirect(usosRedirect(config, data.client, ok), 302);

    try {
      if (registry.mock) {
        store.saveConnection(userId, "usos", { mode: "mock" }, "connected");
      } else {
        if (!q.data.oauth_verifier) return done(false); // user denied access
        const oauth = deps.usosOAuthClient ?? createUsosOAuthClient(config.usos);
        const at = await oauth.accessToken(state, data.secret, q.data.oauth_verifier);
        store.saveConnection(
          userId,
          "usos",
          { mode: "api", accessToken: at.token, accessTokenSecret: at.secret, baseUrl: config.usos.apiBaseUrl },
          "connected"
        );
        registry.invalidate(userId, "usos");
        await registry.usos(userId, (a) => a.getProfile(), { cacheKey: "profile" });
      }
      registry.invalidate(userId, "usos");
      return done(true);
    } catch (err) {
      req.log.warn({ err }, "usos oauth callback failed");
      return done(false);
    }
  });
}

export async function connectionRoutes(app: FastifyInstance, deps: ConnectionDeps): Promise<void> {
  const { config, store, registry } = deps;

  app.get("/connections", async (req) => registry.statuses(currentUser(req).id));

  app.post("/connections/usos/oauth/start", async (req) => {
    const user = currentUser(req);
    const body = OAuthStartBody.parse(req.body ?? {});
    const client = body.client ?? "web";
    const callbackUrl = `${config.publicUrl}/api/connections/usos/oauth/callback`;

    if (registry.mock) {
      const state = randomBytes(16).toString("hex");
      store.saveOAuthState(state, user.id, "usos", { secret: "mock", client } satisfies UsosOAuthStateData);
      return { authorizeUrl: `${callbackUrl}?mock=${state}` };
    }
    if (!config.usos.consumerKey || !config.usos.consumerSecret) {
      throw new HttpError(503, {
        code: "NOT_CONFIGURED",
        message: "Brak USOS_CONSUMER_KEY / USOS_CONSUMER_SECRET w konfiguracji serwera."
      });
    }
    const oauth = deps.usosOAuthClient ?? createUsosOAuthClient(config.usos);
    let rt;
    try {
      rt = await oauth.requestToken(callbackUrl, USOS_SCOPES);
    } catch (e) {
      throw adapterUnavailable("usos", e instanceof Error ? e.message : undefined);
    }
    store.saveOAuthState(rt.token, user.id, "usos", { secret: rt.secret, client } satisfies UsosOAuthStateData);
    return { authorizeUrl: rt.authorizeUrl };
  });

  app.post("/connections/usos/web", async (req) => {
    const user = currentUser(req);
    const { cookies } = UsosWebBody.parse(req.body);
    const creds: UsosCredentials = { mode: "web", cookies, baseUrl: config.usos.usoswebBaseUrl };
    return connect(deps, user.id, "usos", creds, async () => {
      await registry.buildUsos(creds).getProfile();
    });
  });

  app.post("/connections/platon/ws", async (req) => {
    const user = currentUser(req);
    const body = PlatonWsBody.parse(req.body);
    let parsed;
    try {
      parsed = parsePlatonTokenInput((body.launchUrl ?? body.wstoken) as string);
    } catch (e) {
      if (e instanceof TokenParseError) throw badRequest(e.message);
      throw e;
    }
    const privateToken = parsed.privateToken ?? body.privateToken;
    const creds: PlatonCredentials = {
      mode: "ws",
      wstoken: parsed.wstoken,
      ...(privateToken ? { privateToken } : {}),
      baseUrl: config.platon.baseUrl
    };
    return connect(deps, user.id, "platon", creds, async () => {
      const info = await registry.buildPlaton(creds).getSiteInfo();
      return { userId: info.userId };
    });
  });

  app.post("/connections/platon/web", async (req) => {
    const user = currentUser(req);
    const body = PlatonWebBody.parse(req.body);
    const creds: PlatonCredentials = { mode: "web", cookies: body.cookies, sesskey: body.sesskey, baseUrl: config.platon.baseUrl };
    return connect(deps, user.id, "platon", creds, async () => {
      const info = await registry.buildPlaton(creds).getSiteInfo();
      return { userId: info.userId };
    });
  });

  app.delete("/connections/:system", async (req) => {
    const user = currentUser(req);
    const system = SourceSystemSchema.safeParse((req.params as { system: string }).system);
    if (!system.success) throw badRequest("Nieznany system (usos | platon).");
    store.deleteConnection(user.id, system.data);
    registry.invalidate(user.id, system.data);
    return registry.statuses(user.id)[system.data];
  });
}
