import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";

const VERSION = "v1";
const IV_BYTES = 12;
const TAG_BYTES = 16;

/**
 * AES-256-GCM of a JSON payload. Output format: `v1.<base64(iv | tag | ciphertext)>`.
 * `aad` (additional authenticated data) binds the ciphertext to its row (e.g. "userId:system").
 */
export function encryptJson(value: unknown, key: Buffer, aad?: string): string {
  if (key.length !== 32) throw new Error("CREDENTIALS_KEY must be 32 bytes");
  const iv = randomBytes(IV_BYTES);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  if (aad) cipher.setAAD(Buffer.from(aad, "utf8"));
  const ct = Buffer.concat([cipher.update(JSON.stringify(value), "utf8"), cipher.final()]);
  return `${VERSION}.${Buffer.concat([iv, cipher.getAuthTag(), ct]).toString("base64")}`;
}

export function decryptJson<T = unknown>(payload: string, key: Buffer, aad?: string): T {
  if (key.length !== 32) throw new Error("CREDENTIALS_KEY must be 32 bytes");
  const [version, b64] = payload.split(".");
  if (version !== VERSION || !b64) throw new Error("Unsupported payload format");
  const raw = Buffer.from(b64, "base64");
  if (raw.length < IV_BYTES + TAG_BYTES) throw new Error("Payload too short");
  const iv = raw.subarray(0, IV_BYTES);
  const tag = raw.subarray(IV_BYTES, IV_BYTES + TAG_BYTES);
  const ct = raw.subarray(IV_BYTES + TAG_BYTES);
  const decipher = createDecipheriv("aes-256-gcm", key, iv);
  if (aad) decipher.setAAD(Buffer.from(aad, "utf8"));
  decipher.setAuthTag(tag);
  const pt = Buffer.concat([decipher.update(ct), decipher.final()]);
  return JSON.parse(pt.toString("utf8")) as T;
}
