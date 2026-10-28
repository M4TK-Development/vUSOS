export class TokenParseError extends Error {}

export interface ParsedMobileToken {
  wstoken: string;
  privateToken?: string;
}

const RAW_TOKEN = /^[A-Za-z0-9]{16,64}$/;

function decodeBase64(input: string): string | null {
  const normalized = input.replace(/-/g, "+").replace(/_/g, "/").replace(/\s+/g, "");
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(normalized)) return null;
  try {
    return Buffer.from(normalized, "base64").toString("utf8");
  } catch {
    return null;
  }
}

/** Decodes `siteid:::token[:::privatetoken]` as produced by admin/tool/mobile/launch.php. */
function parseDecoded(decoded: string): ParsedMobileToken | null {
  if (!decoded.includes(":::")) return null;
  const parts = decoded.split(":::");
  const wstoken = parts[1]?.trim();
  if (!wstoken || !RAW_TOKEN.test(wstoken)) return null;
  const privateToken = parts[2]?.trim();
  return privateToken ? { wstoken, privateToken } : { wstoken };
}

/**
 * Accepts what a user may paste after the Moodle mobile launch flow:
 *  - a raw wstoken (32 hex chars),
 *  - the redirect URL `moodlemobile://token=BASE64` (also tolerates `moodlemobile://https://site?token=BASE64`),
 *  - the bare BASE64 payload of `siteid:::token:::privatetoken`.
 */
export function parseMobileLaunchToken(input: string): ParsedMobileToken {
  let value = input.trim();
  if (!value) throw new TokenParseError("Empty token");

  if (/^moodlemobile:\/\//i.test(value) || /(^|[?&])token=/.test(value)) {
    const m = /token=([^&\s#]+)/.exec(value);
    if (!m?.[1]) throw new TokenParseError("No token= parameter in the URL");
    try {
      value = decodeURIComponent(m[1]);
    } catch {
      value = m[1];
    }
  }

  // A 32-hex string is a raw token; check before trying base64 (hex is valid base64 alphabet too).
  if (/^[0-9a-f]{32}$/i.test(value)) return { wstoken: value };

  const decoded = decodeBase64(value);
  if (decoded !== null) {
    const parsed = parseDecoded(decoded);
    if (parsed) return parsed;
  }

  // TODO(verify): Moodle tokens are 32 hex chars; accept other alphanumeric tokens leniently.
  if (RAW_TOKEN.test(value)) return { wstoken: value };

  throw new TokenParseError("Unrecognized Moodle token format");
}
