import { describe, expect, it } from "vitest";
import { addDays, diffDays, eachDay, mondayOf, warsawDateOf, warsawLocalToIso, weekday } from "./time.js";

describe("warsawLocalToIso", () => {
  it("uses +01:00 in winter and +02:00 in summer", () => {
    expect(warsawLocalToIso("2025-01-15 12:00:00")).toBe("2025-01-15T12:00:00+01:00");
    expect(warsawLocalToIso("2025-07-01 12:00:00")).toBe("2025-07-01T12:00:00+02:00");
  });

  it("handles the DST switch days", () => {
    expect(warsawLocalToIso("2025-03-30 01:59:00")).toBe("2025-03-30T01:59:00+01:00");
    expect(warsawLocalToIso("2025-03-30 04:00:00")).toBe("2025-03-30T04:00:00+02:00");
    expect(warsawLocalToIso("2025-10-26 01:00:00")).toBe("2025-10-26T01:00:00+02:00");
    expect(warsawLocalToIso("2025-10-26 04:00:00")).toBe("2025-10-26T04:00:00+01:00");
  });

  it("accepts T separator, missing seconds, date only, and passes through zoned strings", () => {
    expect(warsawLocalToIso("2025-01-15T08:30")).toBe("2025-01-15T08:30:00+01:00");
    expect(warsawLocalToIso("2025-01-15")).toBe("2025-01-15T00:00:00+01:00");
    expect(warsawLocalToIso("2025-01-15T08:30:00Z")).toBe("2025-01-15T08:30:00Z");
    expect(warsawLocalToIso("garbage")).toBeNull();
  });

  it("produces instants that Date parses to the right UTC time", () => {
    expect(new Date(warsawLocalToIso("2025-07-01 12:00:00") ?? "").toISOString()).toBe("2025-07-01T10:00:00.000Z");
  });
});

describe("date helpers", () => {
  it("adds/diffs days across month boundaries", () => {
    expect(addDays("2025-01-31", 1)).toBe("2025-02-01");
    expect(addDays("2025-03-01", -1)).toBe("2025-02-28");
    expect(diffDays("2025-10-06", "2025-10-13")).toBe(7);
    expect(eachDay("2025-10-06", "2025-10-08")).toEqual(["2025-10-06", "2025-10-07", "2025-10-08"]);
    expect(eachDay("2025-10-08", "2025-10-06")).toEqual([]);
  });

  it("knows weekdays and mondays", () => {
    expect(weekday("2025-10-06")).toBe(1);
    expect(mondayOf("2025-10-12")).toBe("2025-10-06");
    expect(mondayOf("2025-10-06")).toBe("2025-10-06");
  });

  it("derives the Warsaw calendar date of an instant", () => {
    expect(warsawDateOf(new Date("2025-12-31T23:30:00Z"))).toBe("2026-01-01");
  });
});
