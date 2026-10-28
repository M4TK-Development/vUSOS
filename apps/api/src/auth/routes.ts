import type { FastifyInstance, FastifyReply, FastifyRequest } from "fastify";
import { z } from "zod";
import type { AppConfig } from "../config.js";
import { badRequest, HttpError } from "../errors.js";
import type { Store, UserRow } from "../store.js";
import { createEntraClient, type EntraClient } from "./entra.js";
import { devLoginPage, forbiddenPage } from "./html.js";
import { isAllowedIdentity } from "./policy.js";
import { extractBearer } from "./session.js";

export const FORBIDDEN_MESSAGE =
  "Dostęp do vUSOS mają wyłącznie konta uczelniane Uniwersytetu Vistula. Zaloguj się kontem służbowym uczelni (@vistula.edu.pl / @stu.vistula.edu.pl).";

const ClientParam = z.enum(["web", "native"]).default("web");

const DevLoginBody = z.object({
  email: z.string().trim().toLowerCase().email().max(200),
  name: z.string().trim().min(1).max(100).optional(),
  client: ClientParam.optional()
});

const ExchangeBody = z.object({ code: z.string().min(10).max(200) });

const wantsHtml = (req: FastifyRequest): boolean => (req.headers.accept ?? "").includes("text/html");

function forbidden(req: FastifyRequest, reply: FastifyReply) {
  void reply.code(403);
  if (wantsHtml(req)) return reply.type("text/html; charset=utf-8").send(forbiddenPage(FORBIDDEN_MESSAGE));
  return reply.send({ code: "FORBIDDEN", message: FORBIDDEN_MESSAGE });
}

export const publicUser = (u: UserRow) => ({ id: u.id, email: u.email, name: u.name });

export interface AuthDeps {
  config: AppConfig;
  store: Store;
  entra?: EntraClient;
}

/** Registers /auth/login, /auth/callback, /auth/native/exchange, /auth/logout (call within a `/api` prefix). */
export async function authRoutes(app: FastifyInstance, deps: AuthDeps): Promise<void> {
  const { config, store } = deps;

  // Only the dev login form posts urlencoded bodies; keep this parser scoped to the auth routes.
  app.addContentTypeParser("application/x-www-form-urlencoded", { parseAs: "string" }, (_req, body, done) => {
    done(null, Object.fromEntries(new URLSearchParams(body as string)));
  });

  /** Completes a login: cookie session for the web client, one-time code redirect for native clients. */
  function completeLogin(req: FastifyRequest, reply: FastifyReply, user: UserRow, client: "web" | "native", asJson = false, redirectStatus: 302 | 303 = 303) {
    if (client === "native") {
      const code = store.createAuthCode(user.id);
      return reply.redirect(`${config.nativeScheme}://auth?code=${encodeURIComponent(code)}`, asJson ? 302 : redirectStatus);
    }
    req.session.set("userId", user.id);
    if (asJson) return reply.send({ user: publicUser(user) });
    return reply.redirect(config.webOrigin, redirectStatus);
  }

  app.post("/auth/logout", async (req) => {
    const bearer = extractBearer(req);
    if (bearer) store.revokeApiToken(bearer);
    req.session.delete();
    return { ok: true };
  });

  app.post("/auth/native/exchange", async (req) => {
    const parsed = ExchangeBody.safeParse(req.body);
    if (!parsed.success) throw badRequest("Brak kodu logowania.");
    const userId = store.consumeAuthCode(parsed.data.code);
    const user = userId ? store.getUser(userId) : null;
    if (!user) throw new HttpError(400, { code: "INVALID_CODE", message: "Kod logowania jest nieprawidłowy lub wygasł." });
    return { token: store.createApiToken(user.id, "native"), user: publicUser(user) };
  });

  if (config.authMode === "dev") {
    app.get("/auth/login", async (req, reply) => {
      const q = ClientParam.safeParse((req.query as { client?: string }).client);
      return reply.type("text/html; charset=utf-8").send(devLoginPage(q.success ? q.data : "web"));
    });

    app.post("/auth/login", async (req, reply) => {
      const parsed = DevLoginBody.safeParse(req.body);
      if (!parsed.success) throw badRequest("Podaj poprawny e-mail i imię.");
      const { email } = parsed.data;
      const user = store.upsertUser({
        id: `dev:${email}`,
        email,
        name: parsed.data.name ?? email.split("@")[0] ?? email,
        tenantId: null
      });
      const isForm = (req.headers["content-type"] ?? "").includes("application/x-www-form-urlencoded");
      return completeLogin(req, reply, user, parsed.data.client ?? "web", !isForm);
    });
    return;
  }

  const entra = deps.entra ?? createEntraClient(config);

  app.get("/auth/login", async (req, reply) => {
    const client = ClientParam.catch("web").parse((req.query as { client?: string }).client);
    const { url, transient } = await entra.startLogin();
    // The transient state lives in the encrypted session cookie of the (system) browser that runs the login.
    req.session.set("entra", { ...transient, client });
    return reply.redirect(url, 302);
  });

  app.get("/auth/callback", async (req, reply) => {
    const q = req.query as Record<string, string | undefined>;
    const transient = req.session.get("entra");
    req.session.set("entra", undefined);
    const client = transient?.client ?? "web";
    if (q.error) {
      req.log.warn({ error: q.error }, "entra login returned error");
      const target =
        client === "native"
          ? `${config.nativeScheme}://auth?error=${encodeURIComponent(q.error)}`
          : `${config.webOrigin}/?login_error=${encodeURIComponent(q.error)}`;
      return reply.redirect(target, 302);
    }
    if (!q.code || !q.state || !transient) {
      throw new HttpError(400, { code: "BAD_REQUEST", message: "Nieprawidłowe wywołanie zwrotne logowania." });
    }

    let identity;
    try {
      identity = await entra.finishLogin({ code: q.code, state: q.state }, transient);
    } catch (err) {
      req.log.warn({ err }, "entra login failed");
      throw new HttpError(400, { code: "LOGIN_FAILED", message: "Logowanie nie powiodło się. Spróbuj ponownie." });
    }
    if (!isAllowedIdentity(config, { tenantId: identity.tenantId, email: identity.email })) {
      req.log.info({ tenantId: identity.tenantId }, "entra login rejected by tenant/domain policy");
      return forbidden(req, reply);
    }
    const user = store.upsertUser({ id: identity.id, email: identity.email, name: identity.name, tenantId: identity.tenantId });
    return completeLogin(req, reply, user, client, false, 302);
  });
}
