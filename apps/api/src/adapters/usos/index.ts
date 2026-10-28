import { AdapterUnavailableError, type UsosAdapter, type UsosCredentials } from "@vusos/shared";
import { createUsosApiAdapter } from "./api.js";
import type { UsosAdapterDeps, UsosConfig } from "./config.js";
import { createUsosMockAdapter } from "./mock.js";
import { createUsosOAuthClient as createOAuthClient, type UsosOAuthClient } from "./oauth.js";
import { createUsosWebAdapter } from "./web.js";

export type { UsosConfig, UsosAdapterDeps } from "./config.js";
export type { UsosOAuthClient } from "./oauth.js";

/** Dispatches on `creds.mode`: "api" (OAuth 1.0a, ./api.ts), "web" (cookies + HTML, ./web.ts), "mock" (./mock.ts). */
export function createUsosAdapter(creds: UsosCredentials, config: UsosConfig, deps: UsosAdapterDeps = {}): UsosAdapter {
  switch (creds.mode) {
    case "api":
      return createUsosApiAdapter(creds, config, deps);
    case "web":
      return createUsosWebAdapter(creds, config, deps);
    case "mock":
      return createUsosMockAdapter(deps);
    default: {
      const unknownMode: never = creds;
      throw new AdapterUnavailableError(`Nieobsługiwany tryb USOS: ${JSON.stringify(unknownMode)}`);
    }
  }
}

/** OAuth 1.0a helpers used by the connections routes. */
export function createUsosOAuthClient(config: UsosConfig): UsosOAuthClient {
  return createOAuthClient(config);
}
