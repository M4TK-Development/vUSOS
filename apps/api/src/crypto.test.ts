import { randomBytes } from "node:crypto";
import { describe, expect, it } from "vitest";
import { decryptJson, encryptJson } from "./crypto.js";

describe("crypto", () => {
  const key = randomBytes(32);

  it("roundtrips JSON payloads", () => {
    const payload = { mode: "ws", wstoken: "abc123", nested: { ż: "ółć", n: [1, 2, 3] } };
    const enc = encryptJson(payload, key);
    expect(enc).not.toContain("abc123");
    expect(decryptJson(enc, key)).toEqual(payload);
  });

  it("uses a fresh IV for every encryption", () => {
    expect(encryptJson({ a: 1 }, key)).not.toEqual(encryptJson({ a: 1 }, key));
  });

  it("fails with the wrong key", () => {
    const enc = encryptJson({ a: 1 }, key);
    expect(() => decryptJson(enc, randomBytes(32))).toThrow();
  });

  it("detects tampering", () => {
    const enc = encryptJson({ a: 1 }, key);
    const [v, b64] = enc.split(".") as [string, string];
    const raw = Buffer.from(b64, "base64");
    raw[raw.length - 1] = (raw[raw.length - 1] ?? 0) ^ 1;
    expect(() => decryptJson(`${v}.${raw.toString("base64")}`, key)).toThrow();
  });

  it("binds ciphertext to additional data", () => {
    const enc = encryptJson({ a: 1 }, key, "u1:usos");
    expect(decryptJson(enc, key, "u1:usos")).toEqual({ a: 1 });
    expect(() => decryptJson(enc, key, "u2:usos")).toThrow();
  });

  it("rejects keys of wrong length", () => {
    expect(() => encryptJson({}, randomBytes(16))).toThrow();
  });
});
