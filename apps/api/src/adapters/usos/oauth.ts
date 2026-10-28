import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";
import { createSigner, encodeForm, normalizeBaseUrl, type FetchLike } from "./client.js";

export interface UsosOAuthConfig {
  apiBaseUrl: string;
  consumerKey: string;
  consumerSecret: string;
}

export interface UsosOAuthClient {
  requestToken(callbackUrl: string, scopes: string[]): Promise<{ token: string; secret: string; authorizeUrl: string }>;
  accessToken(token: string, secret: string, verifier: string): Promise<{ token: string; secret: string }>;
}

function parseForm(body: string): URLSearchParams {
  return new URLSearchParams(body.trim());
}

/**
 * OAuth 1.0a three-legged flow against a USOS API installation.
 * Parameters (oauth_callback, scopes, oauth_verifier) are sent as an x-www-form-urlencoded POST body,
 * included in the HMAC-SHA1 signature, with the oauth_* protocol params in the Authorization header.
 */
export function createUsosOAuthClient(config: UsosOAuthConfig, fetchImpl?: FetchLike): UsosOAuthClient {
  const base = normalizeBaseUrl(config.apiBaseUrl);
  const doFetch: FetchLike = fetchImpl ?? ((input, init) => fetch(input, init));
  const signer = createSigner(config.consumerKey, config.consumerSecret);

  async function post(
    path: string,
    data: Record<string, string>,
    token?: { key: string; secret: string }
  ): Promise<{ status: number; text: string }> {
    const url = `${base}${path}`;
    const signed = signer.authorize({ url, method: "POST", data }, token);
    try {
      const res = await doFetch(url, {
        method: "POST",
        headers: {
          ...signer.toHeader(signed),
          "Content-Type": "application/x-www-form-urlencoded",
          Accept: "application/x-www-form-urlencoded, text/plain, */*"
        },
        body: encodeForm(signer, data),
        signal: AbortSignal.timeout(20_000)
      });
      return { status: res.status, text: await res.text() };
    } catch (err) {
      throw new AdapterUnavailableError(
        `USOS OAuth niedostępny (${path}): ${err instanceof Error ? err.message : String(err)}`
      );
    }
  }

  return {
    async requestToken(callbackUrl, scopes) {
      const data: Record<string, string> = { oauth_callback: callbackUrl };
      if (scopes.length > 0) data.scopes = scopes.join("|");
      const { status, text } = await post("services/oauth/request_token", data);
      if (status < 200 || status >= 300) {
        // 400/401 here means a bad consumer key/secret or an unregistered callback: a configuration problem.
        throw new AdapterUnavailableError(`USOS request_token: HTTP ${status} ${text.trim().slice(0, 300)}`);
      }
      const form = parseForm(text);
      const token = form.get("oauth_token");
      const secret = form.get("oauth_token_secret");
      if (!token || !secret) throw new AdapterUnavailableError("USOS request_token: brak oauth_token w odpowiedzi");
      const authorizeUrl = `${base}services/oauth/authorize?oauth_token=${encodeURIComponent(token)}`;
      return { token, secret, authorizeUrl };
    },

    async accessToken(token, secret, verifier) {
      const { status, text } = await post(
        "services/oauth/access_token",
        { oauth_verifier: verifier },
        { key: token, secret }
      );
      if (status === 401 || status === 400) {
        throw new AdapterAuthError(`USOS access_token odrzucony (HTTP ${status}): ${text.trim().slice(0, 200)}`);
      }
      if (status < 200 || status >= 300) {
        throw new AdapterUnavailableError(`USOS access_token: HTTP ${status} ${text.trim().slice(0, 300)}`);
      }
      const form = parseForm(text);
      const t = form.get("oauth_token");
      const s = form.get("oauth_token_secret");
      if (!t || !s) throw new AdapterUnavailableError("USOS access_token: brak oauth_token w odpowiedzi");
      return { token: t, secret: s };
    }
  };
}
