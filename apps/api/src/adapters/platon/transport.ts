import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";

/**
 * Low-level access to Moodle web service functions. The functions layer (functions.ts) is
 * transport-agnostic; there are two implementations:
 *  - wsTransport:   REST web services (`webservice/rest/server.php`, `wstoken`)
 *  - ajaxTransport: the session-cookie AJAX endpoint used by the Moodle web UI (`lib/ajax/service.php`)
 */
export interface MoodleTransport {
  call<T>(wsfunction: string, args?: Record<string, unknown>): Promise<T>;
  /** Headers to send when downloading a file through the BFF. */
  fileHeaders(): Record<string, string>;
  /** Turns a `pluginfile.php` URL into one that can be fetched with `fileHeaders()` (and the URL itself). */
  rewriteFileUrl(url: string): string;
}

export type FetchLike = typeof fetch;

export interface TransportOptions {
  baseUrl: string;
  /** Injectable for tests. Defaults to global fetch. */
  fetch?: FetchLike;
  timeoutMs?: number;
}

export interface WsTransportOptions extends TransportOptions {
  wstoken: string;
}

export interface AjaxTransportOptions extends TransportOptions {
  /** Raw `Cookie` header value, e.g. "MoodleSession=abc123". */
  cookies: string;
  sesskey: string;
}

const DEFAULT_TIMEOUT_MS = 20_000;

/**
 * Moodle error codes meaning "the credentials are no longer valid / not logged in".
 * NOTE: `accessexception` is also raised for plain "no permission for this function" - callers that
 * make best-effort calls must therefore tolerate AdapterAuthError (functions.ts does).
 */
export const AUTH_ERROR_CODES: ReadonlySet<string> = new Set([
  "invalidtoken",
  "accessexception",
  "requireloginerror",
  "servicerequireslogin",
  "invalidsesskey",
  "sessionexpired",
  "forcepasswordchangenotice"
]);

export function normalizeBaseUrl(baseUrl: string): string {
  const trimmed = baseUrl.trim();
  return trimmed.endsWith("/") ? trimmed : `${trimmed}/`;
}

/**
 * Flattens nested args into Moodle's PHP-array form encoding:
 *   { courseids: [12, 13], events: { timesortfrom: 5 } } -> courseids[0]=12, courseids[1]=13, events[timesortfrom]=5
 * Booleans become 1/0, null/undefined are dropped.
 */
export function flattenMoodleArgs(args: Record<string, unknown>): Array<[string, string]> {
  const out: Array<[string, string]> = [];
  const walk = (key: string, value: unknown): void => {
    if (value === undefined || value === null) return;
    if (Array.isArray(value)) {
      value.forEach((item: unknown, index) => walk(`${key}[${index}]`, item));
    } else if (typeof value === "object") {
      for (const [k, v] of Object.entries(value as Record<string, unknown>)) walk(`${key}[${k}]`, v);
    } else if (typeof value === "boolean") {
      out.push([key, value ? "1" : "0"]);
    } else {
      out.push([key, String(value)]);
    }
  };
  for (const [key, value] of Object.entries(args)) walk(key, value);
  return out;
}

interface MoodleErrorShape {
  errorcode?: unknown;
  message?: unknown;
  error?: unknown;
}

function moodleError(errorcode: string | undefined, message: string): Error {
  if (errorcode && AUTH_ERROR_CODES.has(errorcode)) return new AdapterAuthError(message || errorcode);
  return new AdapterUnavailableError(message || errorcode || "Moodle error");
}

function asString(v: unknown): string | undefined {
  return typeof v === "string" ? v : undefined;
}

async function doFetch(f: FetchLike, url: string, init: RequestInit, timeoutMs: number): Promise<Response> {
  try {
    return await f(url, { ...init, signal: AbortSignal.timeout(timeoutMs) });
  } catch (err) {
    throw new AdapterUnavailableError(`Platon is unreachable: ${err instanceof Error ? err.message : String(err)}`);
  }
}

export function createWsTransport(opts: WsTransportOptions): MoodleTransport {
  const base = normalizeBaseUrl(opts.baseUrl);
  const f = opts.fetch ?? fetch;
  const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const endpoint = `${base}webservice/rest/server.php`;

  return {
    async call<T>(wsfunction: string, args: Record<string, unknown> = {}): Promise<T> {
      const body = new URLSearchParams();
      body.set("wstoken", opts.wstoken);
      body.set("wsfunction", wsfunction);
      body.set("moodlewsrestformat", "json");
      for (const [k, v] of flattenMoodleArgs(args)) body.append(k, v);

      const res = await doFetch(
        f,
        endpoint,
        {
          method: "POST",
          headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
          body: body.toString()
        },
        timeoutMs
      );
      if (res.status === 401 || res.status === 403) throw new AdapterAuthError(`Platon returned HTTP ${res.status}`);
      if (!res.ok) throw new AdapterUnavailableError(`Platon returned HTTP ${res.status}`);

      const text = await res.text();
      let json: unknown;
      try {
        json = JSON.parse(text);
      } catch {
        throw new AdapterUnavailableError(`Platon returned a non-JSON response for ${wsfunction}`);
      }
      if (json !== null && typeof json === "object" && !Array.isArray(json) && "exception" in json) {
        const e = json as MoodleErrorShape;
        throw moodleError(asString(e.errorcode), asString(e.message) ?? "Moodle error");
      }
      return json as T;
    },

    fileHeaders: () => ({}),

    rewriteFileUrl(url: string): string {
      if (!isSameOrigin(url, base)) return url; // never attach the token to foreign hosts
      let u: URL;
      try {
        u = new URL(url);
      } catch {
        return url;
      }
      if (u.pathname.includes("/webservice/pluginfile.php/")) {
        // already a WS file URL
      } else if (u.pathname.includes("/pluginfile.php/")) {
        u.pathname = u.pathname.replace("/pluginfile.php/", "/webservice/pluginfile.php/");
      } else {
        return url; // not a file URL (e.g. mod/resource/view.php)
      }
      if (!u.searchParams.has("token")) u.searchParams.set("token", opts.wstoken);
      return u.toString();
    }
  };
}

interface AjaxResponseItem {
  error?: unknown;
  exception?: { errorcode?: unknown; message?: unknown } | null;
  data?: unknown;
}

export function createAjaxTransport(opts: AjaxTransportOptions): MoodleTransport {
  const base = normalizeBaseUrl(opts.baseUrl);
  const f = opts.fetch ?? fetch;
  const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;

  return {
    async call<T>(wsfunction: string, args: Record<string, unknown> = {}): Promise<T> {
      const url = `${base}lib/ajax/service.php?sesskey=${encodeURIComponent(opts.sesskey)}&info=${encodeURIComponent(wsfunction)}`;
      const res = await doFetch(
        f,
        url,
        {
          method: "POST",
          redirect: "manual", // a redirect means "go log in"
          headers: {
            "Content-Type": "application/json",
            Accept: "application/json",
            "X-Requested-With": "XMLHttpRequest",
            Cookie: opts.cookies
          },
          body: JSON.stringify([{ index: 0, methodname: wsfunction, args }])
        },
        timeoutMs
      );
      if (res.status >= 300 && res.status < 400) throw new AdapterAuthError("Platon redirected to login");
      if (res.status === 401 || res.status === 403) throw new AdapterAuthError(`Platon returned HTTP ${res.status}`);
      if (!res.ok) throw new AdapterUnavailableError(`Platon returned HTTP ${res.status}`);

      const text = await res.text();
      let json: unknown;
      try {
        json = JSON.parse(text);
      } catch {
        // HTML (login page, maintenance page behind SSO) instead of JSON
        throw new AdapterAuthError("Platon returned a non-JSON response (session probably expired)");
      }

      if (!Array.isArray(json)) {
        // TODO(verify): service.php answers an invalid sesskey with a bare object rather than an array.
        const e = (json ?? {}) as MoodleErrorShape;
        if (e.error) {
          const code = asString(e.errorcode);
          const msg = asString(e.error) ?? asString(e.message) ?? "Moodle error";
          throw code ? moodleError(code, msg) : new AdapterAuthError(msg);
        }
        throw new AdapterUnavailableError(`Unexpected AJAX response shape for ${wsfunction}`);
      }
      const first = json[0] as AjaxResponseItem | undefined;
      if (!first) throw new AdapterUnavailableError(`Empty AJAX response for ${wsfunction}`);
      if (first.error || first.exception) {
        const ex = first.exception ?? {};
        throw moodleError(asString(ex.errorcode), asString(ex.message) ?? "Moodle error");
      }
      return first.data as T;
    },

    fileHeaders: () => ({ Cookie: opts.cookies }),

    rewriteFileUrl(url: string): string {
      // Session cookies work on plain pluginfile.php; WS-style URLs would demand a token.
      if (!isSameOrigin(url, base)) return url;
      return url.replace("/webservice/pluginfile.php/", "/pluginfile.php/");
    }
  };
}

export function isSameOrigin(url: string, base: string): boolean {
  try {
    return new URL(url).origin === new URL(base).origin;
  } catch {
    return false;
  }
}
