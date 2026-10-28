import { describe, expect, it } from "vitest";
import { z } from "zod";
import { Announcement, Contact, CourseUnit, Grade, Message, MessageThread, Payment, StudentProfile, Term, TimetableEntry } from "@vusos/shared";
import { createUsosAdapter } from "./index.js";
import { computeAverage } from "./mappers.js";
import { MOCK_CURRENT_TERM } from "./mock.js";
import { diffDays, eachDay, warsawDateOf, weekday } from "./time.js";

const NOW = new Date("2025-11-24T09:00:00Z");
const config = { apiBaseUrl: "https://x/", consumerKey: "k", consumerSecret: "s", usoswebBaseUrl: "https://y/" };
const make = () => createUsosAdapter({ mode: "mock" }, config, { now: () => NOW });

describe("mock adapter data", () => {
  it("returns schema-valid data from every method", async () => {
    const a = make();
    StudentProfile.parse(await a.getProfile());
    z.array(Term).parse(await a.getTerms());
    z.array(CourseUnit).parse(await a.getCourses());
    z.array(TimetableEntry).parse(await a.getTimetable("2025-11-24", "2025-11-30"));
    z.array(Grade).parse(await a.getGrades());
    z.array(Payment).parse(await a.getPayments());
    z.array(MessageThread).parse(await a.listMessages());
    z.array(Contact).parse(await a.getDeanOfficeContacts());
    z.array(Announcement).parse(await a.getAnnouncements());
  });

  it("has the requested shape of the catalogue", async () => {
    const a = make();
    const terms = await a.getTerms();
    expect(terms.map((t) => [t.id, t.isCurrent])).toEqual([["2025Z", true], ["2024L", false], ["2024Z", false]]);
    expect(terms.filter((t) => t.isCurrent)).toHaveLength(1);

    const courses = await a.getCourses();
    expect(new Set(courses.map((c) => c.courseId)).size).toBe(6);
    expect(new Set(courses.map((c) => c.classType))).toEqual(new Set(["Wykład", "Ćwiczenia", "Laboratorium", "Lektorat"]));
    expect(courses.every((c) => c.termId === MOCK_CURRENT_TERM)).toBe(true);
    expect((await a.getCourses("2024Z")).every((c) => c.termId === "2024Z")).toBe(true);
    expect((await a.getCourses("2024Z")).length).toBeGreaterThan(0);

    expect(await a.getAnnouncements()).toHaveLength(5);
    expect(await a.getAnnouncements(2)).toHaveLength(2);
    expect(await a.getDeanOfficeContacts()).not.toHaveLength(0);
  });

  it("is deterministic: same clock -> identical output and stable ids", async () => {
    const [x, y] = [make(), make()];
    expect(await x.getPayments()).toEqual(await y.getPayments());
    expect(await x.getGrades()).toEqual(await y.getGrades());
    expect(await x.getTimetable("2025-11-24", "2025-12-07")).toEqual(await y.getTimetable("2025-11-24", "2025-12-07"));
    expect((await x.listMessages()).map((m) => m.id)).toEqual(["mock-msg-1001", "mock-msg-1002", "mock-msg-1003", "mock-msg-1004", "mock-msg-1005", "mock-msg-1006"]);
    // ids do not depend on the clock
    const later = createUsosAdapter({ mode: "mock" }, config, { now: () => new Date("2026-03-01T09:00:00Z") });
    expect((await later.getPayments()).map((p) => p.id)).toEqual((await x.getPayments()).map((p) => p.id));
  });
});

describe("mock timetable", () => {
  it("covers every weekday of the requested range and no weekends", async () => {
    const a = make();
    const from = "2025-11-17";
    const to = "2025-12-14";
    const entries = await a.getTimetable(from, to);
    const days = eachDay(from, to);
    for (const d of days) {
      const onDay = entries.filter((e) => e.start.startsWith(d));
      const wd = weekday(d);
      if (wd >= 1 && wd <= 5) expect(onDay.length, d).toBeGreaterThan(0);
      else expect(onDay, d).toHaveLength(0);
    }
    // sorted, within range, unique ids
    expect([...entries].map((e) => e.start)).toEqual(entries.map((e) => e.start).sort());
    expect(new Set(entries.map((e) => e.id)).size).toBe(entries.length);
    expect(entries.every((e) => e.start.slice(0, 10) >= from && e.end.slice(0, 10) <= to)).toBe(true);
  });

  it("has classes today (relative to the injected clock) and for arbitrary dates", async () => {
    const a = make();
    const today = warsawDateOf(NOW);
    expect(weekday(today)).toBe(1);
    const todays = await a.getTimetable(today, today);
    expect(todays.length).toBeGreaterThan(0);
    const farFuture = await a.getTimetable("2027-05-03", "2027-05-07");
    expect(farFuture.length).toBeGreaterThanOrEqual(5);
  });

  it("uses the right UTC offset across the DST change and valid intervals", async () => {
    const entries = await make().getTimetable("2026-03-25", "2026-04-02");
    const before = entries.find((e) => e.start.startsWith("2026-03-27"));
    const after = entries.find((e) => e.start.startsWith("2026-03-31"));
    expect(before?.start.endsWith("+01:00")).toBe(true);
    expect(after?.start.endsWith("+02:00")).toBe(true);
    for (const e of entries) expect(Date.parse(e.end)).toBeGreaterThan(Date.parse(e.start));
  });

  it("handles single-day, reversed and invalid ranges", async () => {
    const a = make();
    expect((await a.getTimetable("2025-11-26", "2025-11-26")).length).toBe(3);
    expect(await a.getTimetable("2025-11-27", "2025-11-26")).toEqual([]);
    expect(await a.getTimetable("nope", "2025-11-26")).toEqual([]);
    expect(diffDays("2025-11-17", "2025-12-14")).toBe(27);
  });
});

describe("mock grades", () => {
  it("uses the 2.0-5.0 scale, several grades per term and a sensible average", async () => {
    const grades = await make().getGrades();
    const byTerm = (t: string) => grades.filter((g) => g.termId === t);
    for (const t of ["2025Z", "2024L", "2024Z"]) expect(byTerm(t).length).toBeGreaterThanOrEqual(4);
    const numeric = grades.map((g) => Number(g.valueSymbol)).filter((n) => !Number.isNaN(n));
    expect(numeric.every((n) => n >= 2 && n <= 5)).toBe(true);
    expect(grades.some((g) => g.passes === false)).toBe(true);
    const avg = computeAverage(byTerm("2024Z"));
    expect(avg).not.toBeNull();
    expect(avg).toBeGreaterThanOrEqual(2);
    expect(avg).toBeLessThanOrEqual(5);
    // the retake: 2.0 in session 1, 3.5 in session 2 -> average counts only the latest
    const am2 = grades.filter((g) => g.courseId === "INF-AM2");
    expect(am2.map((g) => g.valueSymbol).sort()).toEqual(["2.0", "3.5"]);
    expect(computeAverage(byTerm("2024L"))).toBe(4.0); // (3.5 + 4.0 + 5.0 + 3.5) / 4 = 4.0
    expect((await make().getGrades(["2025Z"])).every((g) => g.termId === "2025Z")).toBe(true);
  });
});

describe("mock payments", () => {
  it("has installments with exactly one overdue and upcoming ones, dated relative to now", async () => {
    const payments = await make().getPayments();
    const today = warsawDateOf(NOW);
    const overdue = payments.filter((p) => p.state === "overdue");
    expect(overdue).toHaveLength(1);
    expect(overdue[0]?.dueDate && overdue[0].dueDate < today).toBe(true);
    expect(overdue[0]?.interest).toBeGreaterThan(0);
    const upcoming = payments.filter((p) => p.state === "unpaid");
    expect(upcoming.length).toBeGreaterThanOrEqual(1);
    expect(upcoming.every((p) => (p.dueDate ?? "") > today)).toBe(true);
    expect(payments.filter((p) => p.state === "paid").every((p) => p.paid === p.amount)).toBe(true);
  });
});

describe("mock messages", () => {
  it("has 6 messages from the two offices, with unread ones, and supports read/send", async () => {
    const a = make();
    const list = await a.listMessages();
    expect(list).toHaveLength(6);
    expect(new Set(list.map((m) => m.from))).toEqual(new Set(["Dziekanat", "Biuro ds. Studenckich"]));
    expect(list.filter((m) => m.unread)).toHaveLength(2);
    expect((await a.listMessages({ limit: 2, offset: 1 })).map((m) => m.id)).toEqual(["mock-msg-1002", "mock-msg-1003"]);

    const first = list[0];
    if (!first) throw new Error("no messages");
    const full = await a.getMessage(first.id);
    Message.parse(full);
    expect(full.bodyHtml).toContain("<p>");
    expect((await a.listMessages()).filter((m) => m.unread)).toHaveLength(1); // opening marks it read

    const { id } = await a.sendMessage(["dziekanat.informatyka@vistula.example"], "Pytanie", "Treść <b>");
    expect(id).toBe("mock-msg-sent-1");
    const after = await a.listMessages();
    expect(after).toHaveLength(7);
    expect(after[0]?.id).toBe(id);
    expect((await a.getMessage(id)).bodyHtml).toBe("<p>Treść &lt;b></p>");
    await expect(a.getMessage("nope")).rejects.toThrow();
  });
});
