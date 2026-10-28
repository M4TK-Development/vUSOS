import { createHmac } from "node:crypto";
import { describe, expect, it } from "vitest";
import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";
import type { FetchLike } from "./client.js";
import { createUsosOAuthClient } from "./oauth.js";

const config = { apiBaseUrl: "https://usosapps.vistula.example", consumerKey: "ckey", consumerSecret: "csecret" };
const enc = (s: string): string => encodeURIComponent(s).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);

interface Seen {
  url: string;
  headers: Record<string, string>;
  body: URLSearchParams;
}

function fake(status: number, text: string, seen: Seen[] = []): FetchLike {
  return async (url, init) => {
    seen.push({ url, headers: (init?.headers ?? {}) as Record<string, string>, body: new URLSearchParams(String(init?.body ?? "")) });
    return new Response(text, { status });
  };
}

function signatureValid(s: Seen, tokenSecret: string): boolean {
  const oauth: Record<string, string> = {};
  for (const m of (s.headers.Authorization ?? "").replace(/^OAuth /, "").matchAll(/(\w+)="([^"]*)"/g)) oauth[m[1] ?? ""] = decodeURIComponent(m[2] ?? "");
  const { oauth_signature: sig, ...rest } = oauth;
  const all: Record<string, string> = { ...Object.fromEntries(s.body), ...rest };
  const paramString = Object.entries(all)
    .map(([k, v]) => [enc(k), enc(v)] as const)
    .sort((a, b) => a[0].localeCompare(b[0]))
    .map(([k, v]) => `${k}=${v}`)
    .join("&");
  const base = `POST&${enc(s.url)}&${enc(paramString)}`;
  return sig === createHmac("sha1", `csecret&${tokenSecret}`).update(base).digest("base64");
}

describe("USOS OAuth client", () => {
  it("requestToken: POSTs signed callback + pipe-separated scopes and builds the authorize URL", async () => {
    const seen: Seen[] = [];
    const client = createUsosOAuthClient(config, fake(200, "oauth_token=REQ%2Ftok&oauth_token_secret=REQsecret&oauth_callback_confirmed=true", seen));
    const res = await client.requestToken("https://app.example/api/connections/usos/oauth/callback", ["offline_access", "studies", "grades"]);
    expect(res).toEqual({
      token: "REQ/tok",
      secret: "REQsecret",
      authorizeUrl: "https://usosapps.vistula.example/services/oauth/authorize?oauth_token=REQ%2Ftok"
    });
    const s = seen[0];
    expect(s?.url).toBe("https://usosapps.vistula.example/services/oauth/request_token");
    expect(s?.body.get("oauth_callback")).toBe("https://app.example/api/connections/usos/oauth/callback");
    expect(s?.body.get("scopes")).toBe("offline_access|studies|grades");
    expect(s?.headers["Content-Type"]).toBe("application/x-www-form-urlencoded");
    expect(s?.headers.Authorization).toContain('oauth_signature_method="HMAC-SHA1"');
    expect(s?.headers.Authorization).toContain('oauth_consumer_key="ckey"');
    expect(s && signatureValid(s, "")).toBe(true);
  });

  it("requestToken: errors", async () => {
    await expect(createUsosOAuthClient(config, fake(401, "bad consumer")).requestToken("https://cb", [])).rejects.toBeInstanceOf(AdapterUnavailableError);
    await expect(createUsosOAuthClient(config, fake(200, "nothing=here")).requestToken("https://cb", [])).rejects.toThrow(/oauth_token/);
    const down: FetchLike = async () => { throw new Error("ENOTFOUND"); };
    await expect(createUsosOAuthClient(config, down).requestToken("https://cb", [])).rejects.toThrow(/ENOTFOUND/);
  });

  it("requestToken omits scopes when none are requested", async () => {
    const seen: Seen[] = [];
    await createUsosOAuthClient(config, fake(200, "oauth_token=a&oauth_token_secret=b", seen)).requestToken("https://cb", []);
    expect(seen[0]?.body.has("scopes")).toBe(false);
  });

  it("accessToken: signs with the request token + verifier and parses the result", async () => {
    const seen: Seen[] = [];
    const client = createUsosOAuthClient(config, fake(200, "oauth_token=ACC&oauth_token_secret=ACCsecret", seen));
    expect(await client.accessToken("REQ", "REQsecret", "12345678")).toEqual({ token: "ACC", secret: "ACCsecret" });
    const s = seen[0];
    expect(s?.url).toBe("https://usosapps.vistula.example/services/oauth/access_token");
    expect(s?.body.get("oauth_verifier")).toBe("12345678");
    expect(s?.headers.Authorization).toContain('oauth_token="REQ"');
    expect(s && signatureValid(s, "REQsecret")).toBe(true);
  });

  it("accessToken: rejected verifier is an auth error, other failures are unavailable", async () => {
    await expect(createUsosOAuthClient(config, fake(401, "Invalid verifier")).accessToken("t", "s", "v")).rejects.toBeInstanceOf(AdapterAuthError);
    await expect(createUsosOAuthClient(config, fake(500, "oops")).accessToken("t", "s", "v")).rejects.toBeInstanceOf(AdapterUnavailableError);
    await expect(createUsosOAuthClient(config, fake(200, "")).accessToken("t", "s", "v")).rejects.toBeInstanceOf(AdapterUnavailableError);
  });
});
