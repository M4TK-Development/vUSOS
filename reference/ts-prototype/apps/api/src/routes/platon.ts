import { Readable } from "node:stream";
import type { ReadableStream as WebReadableStream } from "node:stream/web";
import type { FastifyInstance } from "fastify";
import { AdapterAuthError } from "@vusos/shared";
import { z } from "zod";
import type { AdapterRegistry } from "../adapters/registry.js";
import { currentUser } from "../auth/session.js";
import type { AppConfig } from "../config.js";
import { badRequest, HttpError } from "../errors.js";

const IdParams = z.object({ id: z.string().min(1).max(50).regex(/^\d+$/, "Niepoprawne id kursu") });
const EventsQuery = z.object({ days: z.coerce.number().int().min(1).max(90).optional() });
const LimitQuery = z.object({ limit: z.coerce.number().int().min(1).max(50).optional() });
const FileQuery = z.object({ url: z.string().url().max(4000) });

const MAX_REDIRECTS = 5;
const HEADERS_TIMEOUT_MS = 30_000;

/** Throws unless `raw` is an http(s) URL on exactly the configured Platon host (no userinfo). */
export function assertPlatonUrl(raw: string, baseUrl: string): URL {
  let target: URL;
  try {
    target = new URL(raw);
  } catch {
    throw badRequest("Niepoprawny adres pliku.");
  }
  const base = new URL(baseUrl);
  if (target.host !== base.host || target.protocol !== base.protocol || target.username || target.password) {
    throw new HttpError(400, { code: "FORBIDDEN_HOST", message: "Adres pliku nie należy do Platona." });
  }
  return target;
}

async function fetchUpstream(start: string, headers: Record<string, string>, baseUrl: string): Promise<Response> {
  let current = start;
  for (let hop = 0; hop <= MAX_REDIRECTS; hop++) {
    assertPlatonUrl(current, baseUrl);
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), HEADERS_TIMEOUT_MS);
    let res: Response;
    try {
      res = await fetch(current, { headers, redirect: "manual", signal: ctrl.signal });
    } finally {
      clearTimeout(timer);
    }
    if (res.status >= 300 && res.status < 400) {
      const loc = res.headers.get("location");
      await res.body?.cancel();
      if (!loc) throw new HttpError(502, { code: "ADAPTER_UNAVAILABLE", system: "platon", message: "Przekierowanie bez adresu." });
      current = new URL(loc, current).toString();
      // Moodle bounces unauthenticated requests to the login page.
      if (/\/login\/(index|token)\.php/.test(new URL(current).pathname)) throw new AdapterAuthError();
      continue;
    }
    return res;
  }
  throw new HttpError(502, { code: "ADAPTER_UNAVAILABLE", system: "platon", message: "Zbyt wiele przekierowań." });
}

const ACTIVE_CONTENT = /^(text\/html|application\/xhtml|image\/svg|text\/xml|application\/xml)/i;

export async function platonRoutes(app: FastifyInstance, registry: AdapterRegistry, config: AppConfig): Promise<void> {
  const uid = (req: Parameters<typeof currentUser>[0]) => currentUser(req).id;

  app.get("/platon/courses", async (req) => registry.platon(uid(req), (a) => a.getCourses(), { cacheKey: "courses" }));

  app.get("/platon/courses/:id/contents", async (req) => {
    const { id } = IdParams.parse(req.params);
    return registry.platon(uid(req), (a) => a.getCourseContents(id), { cacheKey: `contents:${id}` });
  });

  app.get("/platon/courses/:id/grades", async (req) => {
    const { id } = IdParams.parse(req.params);
    return registry.platon(uid(req), (a) => a.getGrades(id), { cacheKey: `grades:${id}` });
  });

  app.get("/platon/assignments", async (req) => registry.platon(uid(req), (a) => a.getAssignments(), { cacheKey: "assignments" }));

  app.get("/platon/events", async (req) => {
    const { days } = EventsQuery.parse(req.query);
    return registry.platon(uid(req), (a) => a.getUpcomingEvents(days), { cacheKey: `events:${days ?? ""}` });
  });

  app.get("/platon/announcements", async (req) => {
    const { limit } = LimitQuery.parse(req.query);
    return registry.platon(uid(req), (a) => a.getAnnouncements(limit), { cacheKey: `announcements:${limit ?? ""}` });
  });

  app.get("/platon/file", async (req, reply) => {
    const { url } = FileQuery.parse(req.query);
    assertPlatonUrl(url, config.platon.baseUrl);

    const upstream = await registry.platon(
      uid(req),
      async (a) => {
        const dl = await a.getFileDownload(url);
        const res = await fetchUpstream(dl.url, dl.headers, config.platon.baseUrl);
        if (res.status === 401 || res.status === 403) {
          await res.body?.cancel();
          throw new AdapterAuthError();
        }
        return res;
      },
      { cacheKey: null }
    );

    if (!upstream.ok || !upstream.body) {
      await upstream.body?.cancel();
      throw new HttpError(upstream.status === 404 ? 404 : 502, {
        code: upstream.status === 404 ? "NOT_FOUND" : "ADAPTER_UNAVAILABLE",
        system: "platon",
        message: `Platon zwrócił status ${upstream.status}.`
      });
    }

    const contentType = upstream.headers.get("content-type") ?? "application/octet-stream";
    let disposition = upstream.headers.get("content-disposition");
    // Never let user-supplied HTML/SVG execute on the API origin (which carries the session cookie).
    if (ACTIVE_CONTENT.test(contentType) && !/^attachment/i.test(disposition ?? "")) {
      disposition = disposition ? disposition.replace(/^\s*inline/i, "attachment") : "attachment";
    }
    void reply.header("content-type", contentType);
    if (disposition) void reply.header("content-disposition", disposition);
    const length = upstream.headers.get("content-length");
    if (length) void reply.header("content-length", length);
    void reply.header("cache-control", "private, no-store");
    void reply.header("x-content-type-options", "nosniff");
    void reply.header("content-security-policy", "default-src 'none'; sandbox");
    return reply.send(Readable.fromWeb(upstream.body as unknown as WebReadableStream<Uint8Array>));
  });
}
