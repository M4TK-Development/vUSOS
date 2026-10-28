import {
  Announcement, LmsAssignment, LmsCourse, LmsEvent, LmsGradeItem, LmsSection, AdapterUnavailableError
} from "@vusos/shared";
import { describe, expect, it } from "vitest";
import { z } from "zod";
import { createPlatonAdapter } from "./index.js";
import { createPlatonMock } from "./mock.js";

const NOW = new Date("2026-10-28T10:00:00Z");
const mock = () => createPlatonMock({ now: () => NOW });

describe("mock adapter", () => {
  it("is selected by createPlatonAdapter for mode mock", async () => {
    const a = createPlatonAdapter({ mode: "mock" }, { baseUrl: "https://platon.vistula.edu.pl/" });
    expect((await a.getCourses()).length).toBeGreaterThanOrEqual(6);
  });

  it("returns schema-valid courses and contents", async () => {
    const a = mock();
    const courses = z.array(LmsCourse).parse(await a.getCourses());
    expect(courses).toHaveLength(6);
    expect(courses.map((c) => c.shortName)).toContain("INF-ALG-2025Z");
    const ids = new Set<string>();
    for (const c of courses) {
      const sections = z.array(LmsSection).parse(await a.getCourseContents(c.id));
      expect(sections.length).toBeGreaterThanOrEqual(3);
      expect(sections.length).toBeLessThanOrEqual(5);
      expect(sections[0]?.name).toBe("Wprowadzenie");
      for (const s of sections) for (const m of s.modules) {
        expect(ids.has(m.id)).toBe(false); // stable, unique module ids
        ids.add(m.id);
        expect(m.courseId).toBe(c.id);
      }
    }
    const types = new Set((await Promise.all(courses.map((c) => a.getCourseContents(c.id)))).flat().flatMap((s) => s.modules.map((m) => m.type)));
    for (const t of ["resource", "folder", "url", "assign", "quiz", "forum"]) expect(types.has(t as never)).toBe(true);
    await expect(a.getCourseContents("nope")).rejects.toBeInstanceOf(AdapterUnavailableError);
  });

  it("has assignments covering due-soon, overdue and submitted+graded", async () => {
    const list = z.array(LmsAssignment).parse(await mock().getAssignments());
    const now = NOW.getTime();
    const due = (a: { dueDate: string | null }): number => Date.parse(a.dueDate ?? "");
    expect(list.filter((a) => !a.submitted && due(a) > now && due(a) < now + 7 * 86400000).length).toBeGreaterThanOrEqual(2);
    expect(list.filter((a) => !a.submitted && due(a) < now)).toHaveLength(1);
    const graded = list.filter((a) => a.submitted && a.graded);
    expect(graded).toHaveLength(1);
    expect(graded[0]?.grade).toBe("9,00 / 10,00");
    expect(new Set(list.map((a) => a.id)).size).toBe(list.length);
  });

  it("returns events within the requested window only", async () => {
    const a = mock();
    const ev = z.array(LmsEvent).parse(await a.getUpcomingEvents(14));
    expect(ev.length).toBeGreaterThanOrEqual(5);
    for (const e of ev) {
      expect(Date.parse(e.start)).toBeGreaterThanOrEqual(NOW.getTime());
      expect(Date.parse(e.start)).toBeLessThanOrEqual(NOW.getTime() + 14 * 86400000);
    }
    expect(ev.map((e) => e.start)).toEqual([...ev.map((e) => e.start)].sort());
    const week = await a.getUpcomingEvents(3);
    expect(week.length).toBeLessThan(ev.length);
  });

  it("returns grade items per course and announcements", async () => {
    const a = mock();
    for (const c of await a.getCourses()) {
      const items = z.array(LmsGradeItem).parse(await a.getGrades(c.id));
      expect(items.some((i) => i.itemType === "course")).toBe(true);
    }
    expect(await a.getGrades("unknown")).toEqual([]);
    const ann = z.array(Announcement).parse(await a.getAnnouncements());
    expect(ann).toHaveLength(4);
    expect(ann.every((x) => x.source === "platon")).toBe(true);
    expect(await a.getAnnouncements(2)).toHaveLength(2);
  });

  it("is deterministic and getFileDownload returns the URL unchanged", async () => {
    const a = mock();
    const b = mock();
    expect(await a.getAssignments()).toEqual(await b.getAssignments());
    expect(await a.getCourseContents("3101")).toEqual(await b.getCourseContents("3101"));
    expect(await a.getFileDownload("https://x/y.pdf")).toEqual({ url: "https://x/y.pdf", headers: {} });
    expect((await a.getSiteInfo()).userId).toBe(48213);
  });
});
