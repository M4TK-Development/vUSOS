import { createHmac } from "node:crypto";
import OAuth from "oauth-1.0a";
import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";

/** Minimal fetch signature we depend on (lets tests inject a fake). */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export type UsosParamValue = string | number | boolean | readonly string[] | undefined | null;
export type UsosParams = Record<string, UsosParamValue>;

/** Everything needed to sign a request as a given user. */
export interface UsosApiAuth {
  /** USOS API installation root, e.g. https://usosapps.vistula.edu.pl/ */
  baseUrl: string;
  consumerKey: string;
  consumerSecret: string;
  accessToken: string;
  accessTokenSecret: string;
}

export interface UsosFetchOptions {
  method?: "GET" | "POST";
  fetchImpl?: FetchLike;
  timeoutMs?: number;
}

/** Non-2xx response from the USOS API (other than 401, which becomes AdapterAuthError). */
export class UsosHttpError extends AdapterUnavailableError {
  constructor(
    message: string,
    readonly status: number,
    readonly body: string,
    readonly path: string
  ) {
    super(message);
  }

  /** USOS answers 400/404 "Method ... not found" for modules that are not installed/enabled. */
  get isMethodNotFound(): boolean {
    return (this.status === 400 || this.status === 404) && /method|metod|not found|nie znaleziono|nie istnieje/i.test(this.body);
  }
}

export function normalizeBaseUrl(url: string): string {
  return url.endsWith("/") ? url : `${url}/`;
}

export function createSigner(consumerKey: string, consumerSecret: string): OAuth {
  return new OAuth({
    consumer: { key: consumerKey, secret: consumerSecret },
    signature_method: "HMAC-SHA1",
    hash_function: (baseString, key) => createHmac("sha1", key).update(baseString).digest("base64")
  });
}

/** Flattens params to strings; arrays are pipe-joined (USOS list convention); undefined/null dropped. */
export function serializeParams(params: UsosParams): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null) continue;
    out[k] = Array.isArray(v) ? v.join("|") : String(v);
  }
  return out;
}

/** RFC 3986 form encoding (same as the one used for the OAuth signature base string). */
export function encodeForm(signer: OAuth, data: Record<string, string>): string {
  return Object.entries(data)
    .map(([k, v]) => `${signer.percentEncode(k)}=${signer.percentEncode(v)}`)
    .join("&");
}

function extractMessage(body: string): string {
  try {
    const parsed: unknown = JSON.parse(body);
    if (parsed && typeof parsed === "object" && "message" in parsed && typeof parsed.message === "string") {
      return parsed.message;
    }
  } catch {
    // not JSON - fall through
  }
  return body.trim().slice(0, 300);
}

/**
 * Signed request to the USOS API (OAuth 1.0a, HMAC-SHA1, consumer + access token).
 * `path` is relative to the installation root and starts with "services/" (leading slash tolerated).
 * `fields` may be passed as a string or string[] (joined with "|") like any other list parameter.
 *
 * - HTTP 401            -> AdapterAuthError (token revoked/expired -> UI "Połącz ponownie")
 * - other non-2xx       -> UsosHttpError (an AdapterUnavailableError) with the USOS error message
 * - network / bad JSON  -> AdapterUnavailableError
 */
export async function usosFetch<T = unknown>(
  path: string,
  params: UsosParams,
  auth: UsosApiAuth,
  opts: UsosFetchOptions = {}
): Promise<T> {
  const method = opts.method ?? "GET";
  const doFetch: FetchLike = opts.fetchImpl ?? ((input, init) => fetch(input, init));
  const signer = createSigner(auth.consumerKey, auth.consumerSecret);
  const flat = serializeParams(params);
  const base = `${normalizeBaseUrl(auth.baseUrl)}${path.replace(/^\/+/, "")}`;

  const signed = signer.authorize(
    { url: base, method, data: flat },
    { key: auth.accessToken, secret: auth.accessTokenSecret }
  );
  const headers: Record<string, string> = {
    ...signer.toHeader(signed),
    Accept: "application/json"
  };

  const query = encodeForm(signer, flat);
  let url = base;
  let body: string | undefined;
  if (method === "GET") {
    if (query) url = `${base}?${query}`;
  } else {
    headers["Content-Type"] = "application/x-www-form-urlencoded";
    body = query;
  }

  let res: Response;
  try {
    res = await doFetch(url, {
      method,
      headers,
      body,
      signal: AbortSignal.timeout(opts.timeoutMs ?? 20_000)
    });
  } catch (err) {
    throw new AdapterUnavailableError(
      `USOS API niedostępne (${path}): ${err instanceof Error ? err.message : String(err)}`
    );
  }

  const text = await res.text();
  if (res.status === 401) {
    throw new AdapterAuthError(`USOS: sesja wygasła lub token cofnięty (${extractMessage(text) || "401"})`);
  }
  if (!res.ok) {
    throw new UsosHttpError(`USOS API ${path}: HTTP ${res.status} ${extractMessage(text)}`.trim(), res.status, text, path);
  }
  try {
    return JSON.parse(text) as T; // shape is validated by the zod schemas in mappers.ts
  } catch {
    throw new AdapterUnavailableError(`USOS API ${path}: odpowiedź nie jest poprawnym JSON-em`);
  }
}
