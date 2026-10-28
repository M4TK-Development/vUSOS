import type { PlatonAdapter, PlatonCredentials } from "@vusos/shared";

export interface PlatonConfig {
  baseUrl: string;
}

/** Owned by the Platon adapter module. Implementation: ./ws.ts (wstoken REST), ./web.ts (cookies+ajax), ./mock.ts. */
export function createPlatonAdapter(_creds: PlatonCredentials, _config: PlatonConfig): PlatonAdapter {
  throw new Error("not implemented");
}
