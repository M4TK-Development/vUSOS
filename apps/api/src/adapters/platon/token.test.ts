import { describe, expect, it } from "vitest";
import { parseMobileLaunchToken, TokenParseError } from "./token.js";

const WS = "0123456789abcdef0123456789abcdef";
const PRIVATE = "FEDCBA9876543210fedcba9876543210";
const b64 = (s: string): string => Buffer.from(s, "utf8").toString("base64");

describe("parseMobileLaunchToken", () => {
  it("accepts a raw wstoken", () => {
    expect(parseMobileLaunchToken(`  ${WS}\n`)).toEqual({ wstoken: WS });
  });

  it("decodes moodlemobile://token=BASE64 with private token", () => {
    const payload = b64(`siteidmd5hash:::${WS}:::${PRIVATE}`);
    expect(parseMobileLaunchToken(`moodlemobile://token=${payload}`)).toEqual({ wstoken: WS, privateToken: PRIVATE });
  });

  it("handles URL-encoded padding and the site-url form", () => {
    const payload = b64(`abc:::${WS}`);
    expect(payload.endsWith("=")).toBe(true);
    expect(parseMobileLaunchToken(`moodlemobile://token=${encodeURIComponent(payload)}`)).toEqual({ wstoken: WS });
    expect(parseMobileLaunchToken(`moodlemobile://https://platon.vistula.edu.pl?token=${payload}`)).toEqual({ wstoken: WS });
  });

  it("decodes the bare base64 payload (including base64url)", () => {
    const payload = b64(`site:::${WS}:::${PRIVATE}`);
    expect(parseMobileLaunchToken(payload)).toEqual({ wstoken: WS, privateToken: PRIVATE });
    const url = payload.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    expect(parseMobileLaunchToken(url).wstoken).toBe(WS);
  });

  it("omits an empty private token", () => {
    expect(parseMobileLaunchToken(b64(`site:::${WS}:::`))).toEqual({ wstoken: WS });
  });

  it("rejects garbage", () => {
    expect(() => parseMobileLaunchToken("")).toThrow(TokenParseError);
    expect(() => parseMobileLaunchToken("moodlemobile://")).toThrow(TokenParseError);
    expect(() => parseMobileLaunchToken("not a token!!")).toThrow(TokenParseError);
    expect(() => parseMobileLaunchToken(b64("only:::"))).toThrow(TokenParseError);
  });
});
