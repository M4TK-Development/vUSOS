import type { UsosAdapter, UsosCredentials } from "@vusos/shared";

export interface UsosConfig {
  apiBaseUrl: string;
  consumerKey: string;
  consumerSecret: string;
  usoswebBaseUrl: string;
}

/** Owned by the USOS adapter module. Implementation: ./api.ts (OAuth1), ./web.ts (cookies+HTML), ./mock.ts. */
export function createUsosAdapter(_creds: UsosCredentials, _config: UsosConfig): UsosAdapter {
  throw new Error("not implemented");
}

/** OAuth 1.0a helpers used by the connections routes. */
export interface UsosOAuthClient {
  requestToken(callbackUrl: string, scopes: string[]): Promise<{ token: string; secret: string; authorizeUrl: string }>;
  accessToken(token: string, secret: string, verifier: string): Promise<{ token: string; secret: string }>;
}
export function createUsosOAuthClient(_config: UsosConfig): UsosOAuthClient {
  throw new Error("not implemented");
}
