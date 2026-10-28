import type { PlatonAdapter, PlatonCredentials } from "@vusos/shared";
import { createPlatonFunctions } from "./functions.js";
import { createPlatonMock } from "./mock.js";
import { createAjaxTransport, createWsTransport } from "./transport.js";

export interface PlatonConfig {
  baseUrl: string;
}

/**
 * Platon (Moodle) adapter. One functions layer (functions.ts) over two transports:
 *  - mode "ws":   REST web services with a wstoken (transport.ts: createWsTransport)
 *  - mode "web":  session cookie + sesskey against lib/ajax/service.php (transport.ts: createAjaxTransport)
 *  - mode "mock": deterministic demo dataset (mock.ts)
 */
export function createPlatonAdapter(creds: PlatonCredentials, config: PlatonConfig): PlatonAdapter {
  switch (creds.mode) {
    case "mock":
      return createPlatonMock({ baseUrl: config.baseUrl });
    case "ws": {
      const baseUrl = creds.baseUrl || config.baseUrl;
      return createPlatonFunctions(createWsTransport({ baseUrl, wstoken: creds.wstoken }), {
        baseUrl,
        userId: creds.userId
      });
    }
    case "web": {
      const baseUrl = creds.baseUrl || config.baseUrl;
      return createPlatonFunctions(createAjaxTransport({ baseUrl, cookies: creds.cookies, sesskey: creds.sesskey }), {
        baseUrl,
        userId: creds.userId
      });
    }
  }
}

export { parseMobileLaunchToken, TokenParseError, type ParsedMobileToken } from "./token.js";
export { createPlatonFunctions, type PlatonFunctionsOptions } from "./functions.js";
export { createPlatonMock, type PlatonMockOptions } from "./mock.js";
export {
  createAjaxTransport,
  createWsTransport,
  flattenMoodleArgs,
  type MoodleTransport
} from "./transport.js";
