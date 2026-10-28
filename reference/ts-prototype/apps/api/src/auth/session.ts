import type { FastifyRequest } from "fastify";
import { unauthenticated } from "../errors.js";
import type { Store, UserRow } from "../store.js";

declare module "@fastify/secure-session" {
  interface SessionData {
    userId: string;
    /** Transient Entra login state (PKCE verifier etc.). Cleared on callback. */
    entra: { state: string; nonce: string; verifier: string; client: "web" | "native" };
  }
}

declare module "fastify" {
  interface FastifyRequest {
    user: UserRow | null;
    /** The bearer token the request authenticated with (native clients), if any. */
    bearerToken: string | null;
  }
}

export function extractBearer(request: FastifyRequest): string | null {
  const h = request.headers.authorization;
  if (!h) return null;
  const m = /^Bearer\s+(\S+)$/i.exec(h);
  return m?.[1] ?? null;
}

/**
 * onRequest/preHandler hook resolving the caller to a user row, or 401.
 * `Authorization: Bearer <token>` (native clients) takes precedence over the session cookie (web client).
 */
export function createAuthGuard(store: Store) {
  return async (request: FastifyRequest): Promise<void> => {
    const bearer = extractBearer(request);
    if (bearer) {
      const user = store.resolveApiToken(bearer);
      if (!user) throw unauthenticated();
      request.user = user;
      request.bearerToken = bearer;
      return;
    }
    const userId = request.session.get("userId");
    const user = userId ? store.getUser(userId) : null;
    if (!user) {
      if (userId) request.session.delete();
      throw unauthenticated();
    }
    request.user = user;
  };
}

export function currentUser(request: FastifyRequest): UserRow {
  if (!request.user) throw unauthenticated();
  return request.user;
}
