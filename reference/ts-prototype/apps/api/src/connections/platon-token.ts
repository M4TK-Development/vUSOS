export class TokenParseError extends Error {}

export interface ParsedPlatonToken {
  wstoken: string;
  privateToken?: string;
}

const RAW_TOKEN = /^[A-Za-z0-9]{16,128}$/;

function decodeBase64(input: string): string | null {
  const normalized = input.replace(/-/g, "+").replace(/_/g, "/").replace(/\s+/g, "");
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(normalized)) return null;
  return Buffer.from(normalized, "base64").toString("utf8");
}

/**
 * Accepts whatever a user (or the native shell) hands over after the Moodle mobile launch flow:
 *  - a raw wstoken,
 *  - a redirect URL with any scheme (`moodlemobile://token=BASE64`, `vusos://token=BASE64`),
 *  - the bare BASE64 of `passport:::token[:::privatetoken]`.
 */
export function parsePlatonTokenInput(input: string): ParsedPlatonToken {
  let value = input.trim();
  if (!value) throw new TokenParseError("Pusty token");

  const m = /(?:^|[?&/])token=([^&\s#]+)/.exec(value);
  if (m?.[1]) {
    try {
      value = decodeURIComponent(m[1]);
    } catch {
      value = m[1];
    }
  } else if (/^[a-z][a-z0-9+.-]*:\/\//i.test(value)) {
    throw new TokenParseError("Brak parametru token= w adresie");
  }

  // A 32-hex string is a raw token (hex is also valid base64, so check first).
  if (/^[0-9a-f]{32}$/i.test(value)) return { wstoken: value };

  const decoded = decodeBase64(value);
  if (decoded?.includes(":::")) {
    const [, token, priv] = decoded.split(":::").map((p) => p.trim());
    if (token && RAW_TOKEN.test(token)) return priv ? { wstoken: token, privateToken: priv } : { wstoken: token };
  }
  if (RAW_TOKEN.test(value)) return { wstoken: value };
  throw new TokenParseError("Nierozpoznany format tokenu Moodle");
}
