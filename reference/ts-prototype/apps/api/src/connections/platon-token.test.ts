import { describe, expect, it } from "vitest";
import { parsePlatonTokenInput, TokenParseError } from "./platon-token.js";

const TOKEN = "0123456789abcdef0123456789abcdef";
const b64 = (s: string): string => Buffer.from(s).toString("base64");

describe("parsePlatonTokenInput", () => {
  it("accepts a raw token", () => {
    expect(parsePlatonTokenInput(` ${TOKEN} `)).toEqual({ wstoken: TOKEN });
  });
  it("decodes bare base64 with private token", () => {
    expect(parsePlatonTokenInput(b64(`12345:::${TOKEN}:::privpriv0123456789`))).toEqual({
      wstoken: TOKEN,
      privateToken: "privpriv0123456789"
    });
  });
  it("decodes moodlemobile:// and vusos:// URLs, url-encoded base64", () => {
    const payload = b64(`12345:::${TOKEN}`);
    expect(parsePlatonTokenInput(`moodlemobile://token=${payload}`)).toEqual({ wstoken: TOKEN });
    expect(parsePlatonTokenInput(`vusos://token=${encodeURIComponent(payload)}`)).toEqual({ wstoken: TOKEN });
  });
  it("rejects garbage", () => {
    expect(() => parsePlatonTokenInput("")).toThrow(TokenParseError);
    expect(() => parsePlatonTokenInput("vusos://nothing")).toThrow(TokenParseError);
    expect(() => parsePlatonTokenInput("not a token!")).toThrow(TokenParseError);
  });
});
