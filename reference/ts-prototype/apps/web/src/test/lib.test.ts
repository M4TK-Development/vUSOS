import { afterEach, describe, expect, it, vi } from "vitest";
import type { Grade } from "@vusos/shared";
import { api, ApiError } from "../api/client";
import { parseDeepLink } from "../deeplink";
import { gradeNumber } from "../lib/format";
import { computeAverage } from "../lib/grades";
import { addDaysKey, dayKey, mondayOf, weekKeys } from "../lib/time";
import { clearToken, getToken, setToken } from "../lib/token";

const grade = (symbol: string, counts = true): Grade => ({
  id: symbol + counts,
  termId: "t",
  courseId: "c",
  courseName: { pl: "Kurs" },
  classType: null,
  examSession: 1,
  valueSymbol: symbol,
  valueDescription: null,
  passes: true,
  countsIntoAverage: counts,
  dateModified: null,
  gradedBy: null,
  comment: null,
});

describe("grades", () => {
  it("parses polish decimals and ignores non-numeric symbols", () => {
    expect(gradeNumber("4,5")).toBe(4.5);
    expect(gradeNumber("zal")).toBeNull();
    expect(gradeNumber("")).toBeNull();
  });
  it("averages only numeric grades counting into the average", () => {
    expect(computeAverage([grade("5"), grade("4"), grade("3", false), grade("zal")])).toBe(4.5);
    expect(computeAverage([grade("zal")])).toBeNull();
  });
});

describe("time helpers (Europe/Warsaw)", () => {
  it("computes Monday-based weeks", () => {
    expect(mondayOf("2026-10-28")).toBe("2026-10-26"); // Wednesday
    expect(mondayOf("2026-11-01")).toBe("2026-10-26"); // Sunday
    expect(weekKeys("2026-10-26")).toHaveLength(7);
    expect(addDaysKey("2026-10-26", 7)).toBe("2026-11-02");
  });
  it("assigns instants to the Warsaw calendar day", () => {
    expect(dayKey("2026-10-27T23:30:00Z")).toBe("2026-10-28"); // CET, UTC+1
    expect(dayKey("2026-07-01T22:30:00Z")).toBe("2026-07-02"); // CEST, UTC+2
  });
});

describe("deep links", () => {
  it("parses auth, connected and moodle token links", () => {
    expect(parseDeepLink("vusos://auth?code=abc123")).toEqual({ kind: "auth", code: "abc123" });
    expect(parseDeepLink("vusos://connected?system=usos")).toEqual({ kind: "connected", system: "usos" });
    expect(parseDeepLink("vusos://token=QUJD==")).toEqual({ kind: "moodle-token", url: "vusos://token=QUJD==" });
    expect(parseDeepLink("moodlemobile://token=QUJD")).toMatchObject({ kind: "moodle-token" });
    expect(parseDeepLink("vusos://auth")).toEqual({ kind: "unknown" });
    expect(parseDeepLink("garbage")).toEqual({ kind: "unknown" });
  });
});

describe("api client", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    clearToken();
  });

  const jsonResponse = (status: number, body: unknown) =>
    new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

  it("throws ApiError with code and system on 409", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(409, { code: "NOT_CONNECTED", system: "platon" })));
    const err = await api("/platon/courses").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err).toMatchObject({ status: 409, code: "NOT_CONNECTED", system: "platon", needsConnection: true, expired: false });
  });

  it("marks 424 as expired", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(424, { code: "ADAPTER_AUTH", system: "usos" })));
    const err = (await api("/usos/grades").catch((e: unknown) => e)) as ApiError;
    expect(err.expired).toBe(true);
  });

  it("sends credentials and the bearer token when stored", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { ok: true }));
    vi.stubGlobal("fetch", fetchMock);
    setToken("tok");
    expect(getToken()).toBe("tok");
    await api("/me");
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/me");
    expect(init.credentials).toBe("include");
    expect((init.headers as Record<string, string>).Authorization).toBe("Bearer tok");
  });
});
