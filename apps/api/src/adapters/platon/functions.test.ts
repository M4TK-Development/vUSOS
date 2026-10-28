import { AdapterAuthError, AdapterUnavailableError, LmsAssignment, LmsCourse, LmsEvent, LmsGradeItem, LmsSection, Announcement } from "@vusos/shared";
import { describe, expect, it } from "vitest";
import { z } from "zod";
import { createPlatonFunctions, isoFromUnix, stripPluginfilePlaceholders, toModuleType } from "./functions.js";
import { fakeTransport, fixture } from "./fixtures/helpers.js";

const BASE = "https://platon.vistula.edu.pl/";
const NOW = new Date("2026-10-28T10:00:00Z"); // 1793181600

function setup(extra: Record<string, unknown> = {}, opts: Parameters<typeof createPlatonFunctions>[1] = { baseUrl: BASE }) {
  const t = fakeTransport({
    core_webservice_get_site_info: fixture("site_info"),
    core_course_get_enrolled_courses_by_timeline_classification: fixture("enrolled_courses"),
    core_course_get_contents: fixture("course_contents"),
    mod_assign_get_assignments: fixture("assignments"),
    mod_assign_get_submission_status: (a: Record<string, unknown>) =>
      a.assignid === 8002 ? fixture("submission_status_graded") : fixture("submission_status_new"),
    core_calendar_get_action_events_by_timesort: fixture("action_events"),
    gradereport_user_get_grade_items: fixture("grade_items"),
    mod_forum_get_forums_by_courses: fixture("forums_by_courses"),
    mod_forum_get_forum_discussions: (a: Record<string, unknown>) =>
      a.forumid === 1 ? fixture("forum_discussions_site") : fixture("forum_discussions_course"),
    ...extra
  }, { rewrite: (u) => `${u}#rewritten`, headers: { Cookie: "MoodleSession=x" } });
  return { t, a: createPlatonFunctions(t, { now: () => NOW, ...opts }) };
}

describe("helpers", () => {
  it("converts unix timestamps", () => {
    expect(isoFromUnix(1793181600)).toBe("2026-10-28T10:00:00.000Z");
    expect(isoFromUnix(0)).toBeNull();
    expect(isoFromUnix(null)).toBeNull();
  });
  it("maps module types", () => {
    expect(toModuleType("assign")).toBe("assign");
    expect(toModuleType("glossary")).toBe("other");
  });
  it("strips @@PLUGINFILE@@", () => {
    expect(stripPluginfilePlaceholders('<img src="@@PLUGINFILE@@/a.png">')).toBe('<img src="a.png">');
    expect(stripPluginfilePlaceholders("")).toBeNull();
  });
});

describe("getSiteInfo", () => {
  it("maps site info", async () => {
    const { a } = setup();
    expect(await a.getSiteInfo()).toEqual({
      userId: 48213,
      fullName: "Jan Kowalski",
      siteName: "Platon - Vistula University",
      release: "4.5.2 (Build: 20250113)"
    });
  });
  it("falls back to the stored userId when site info is unavailable", async () => {
    const { a } = setup(
      { core_webservice_get_site_info: () => { throw new AdapterUnavailableError("nope"); } },
      { baseUrl: BASE, userId: 5 }
    );
    expect((await a.getSiteInfo()).userId).toBe(5);
  });
  it("rejects an unexpected response shape", async () => {
    const { a } = setup({ core_webservice_get_site_info: { foo: 1 } });
    await expect(a.getSiteInfo()).rejects.toBeInstanceOf(AdapterUnavailableError);
  });
});

describe("getCourses", () => {
  it("maps courses and validates against the shared schema", async () => {
    const { a, t } = setup();
    const courses = await a.getCourses();
    z.array(LmsCourse).parse(courses);
    expect(t.calls[0]?.args).toMatchObject({ classification: "all" });
    expect(courses).toHaveLength(2);
    expect(courses[0]).toMatchObject({
      id: "3017",
      shortName: "INF-ALG-2025Z",
      category: "Informatyka 2025/26",
      progress: 42.857142857143,
      url: `${BASE}course/view.php?id=3017`,
      usosCourseId: "INF-ALG-2025Z",
      startDate: "2026-09-28T00:00:00.000Z",
      endDate: "2027-02-14T00:00:00.000Z",
      lastAccess: null
    });
    expect(courses[1]).toMatchObject({ progress: null, endDate: null, usosCourseId: null });
  });
});

describe("getCourseContents", () => {
  it("maps sections, modules and files", async () => {
    const { a, t } = setup();
    const sections = await a.getCourseContents("3017");
    z.array(LmsSection).parse(sections);
    expect(t.calls[0]).toEqual({ fn: "core_course_get_contents", args: { courseid: 3017 } });
    expect(sections.map((s) => s.name)).toEqual(["Wprowadzenie", "Tydzień 1", "Sekcja 2"]);
    expect(sections[1]?.summaryHtml).toBe('<p><img src="banner.png" alt=""></p>');

    const forum = sections[0]?.modules[0];
    expect(forum).toMatchObject({ type: "forum", rawType: "forum", completed: null, visible: true, courseId: "3017", sectionName: "Wprowadzenie" });

    const sylabus = sections[0]?.modules[1];
    expect(sylabus?.type).toBe("resource");
    expect(sylabus?.completed).toBe(true);
    expect(sylabus?.files).toEqual([
      {
        name: "Sylabus_ASD.pdf",
        url: "https://platon.vistula.edu.pl/webservice/pluginfile.php/520002/mod_resource/content/1/Sylabus_ASD.pdf?forcedownload=1",
        mimeType: "application/pdf",
        size: 183422,
        modified: new Date(1790561000 * 1000).toISOString()
      }
    ]);

    const [folder, urlMod, assign, quiz, hidden] = sections[1]?.modules ?? [];
    expect(folder?.type).toBe("folder");
    expect(folder?.completed).toBe(false);
    expect(folder?.files).toHaveLength(2);
    expect(folder?.files[1]?.modified).toBeNull(); // timemodified 0
    expect(urlMod).toMatchObject({ type: "url", url: "https://visualgo.net/en", files: [] });
    expect(assign).toMatchObject({ type: "assign", url: `${BASE}mod/assign/view.php?id=410012`, descriptionHtml: "<p>Zaimplementuj <b>quicksort</b>.</p>" });
    expect(quiz?.type).toBe("quiz");
    expect(hidden).toMatchObject({ type: "other", rawType: "glossary", visible: false });

    const label = sections[2]?.modules[0];
    expect(label?.type).toBe("label");
    expect(label?.descriptionHtml).toBe('<p>Termin oddania: patrz kalendarz. <img src="x.png"></p>');
  });

  it("does not embed auth in file URLs by default, but can", async () => {
    const plain = await setup().a.getCourseContents("3017");
    expect(plain[0]?.modules[1]?.files[0]?.url).not.toContain("#rewritten");
    const embedded = await setup({}, { baseUrl: BASE, embedFileAuth: true }).a.getCourseContents("3017");
    expect(embedded[0]?.modules[1]?.files[0]?.url).toContain("#rewritten");
  });
});

describe("getAssignments", () => {
  it("combines assignments with submission status for the nearest-due ones", async () => {
    const { a, t } = setup();
    const list = await a.getAssignments();
    z.array(LmsAssignment).parse(list);

    // sorted by due date, no-deadline last
    expect(list.map((x) => x.id)).toEqual(["8002", "8001", "8101", "8003"]);
    const statusCalls = t.calls.filter((c) => c.fn === "mod_assign_get_submission_status");
    expect(statusCalls.map((c) => c.args.assignid).sort()).toEqual([8001, 8002, 8101]); // 8003 has no due date

    expect(list[0]).toMatchObject({
      courseId: "3017",
      courseName: "Algorytmy i struktury danych (INF-ALG-2025Z)",
      name: "Zadanie 0: Rozgrzewka",
      submitted: true,
      graded: true,
      grade: "9,00 / 10,00",
      url: `${BASE}mod/assign/view.php?id=410030`
    });
    expect(list[1]).toMatchObject({ submitted: false, graded: false, grade: null, cutoffDate: "2026-11-03T22:59:00.000Z", dueDate: "2026-10-30T21:59:00.000Z" });
    expect(list[3]).toMatchObject({ dueDate: null, submitted: false });
  });

  it("limits status calls to the 20 nearest and survives status failures", async () => {
    const many = {
      courses: [
        {
          id: 1,
          fullname: "C",
          assignments: Array.from({ length: 30 }, (_, i) => ({ id: i + 1, cmid: 100 + i, name: `A${i}`, duedate: 1793181600 + (i + 1) * 3600 }))
        }
      ]
    };
    const { a, t } = setup({
      mod_assign_get_assignments: many,
      mod_assign_get_submission_status: () => {
        throw new AdapterAuthError("accessexception");
      }
    });
    const list = await a.getAssignments();
    expect(list).toHaveLength(30);
    expect(t.calls.filter((c) => c.fn === "mod_assign_get_submission_status")).toHaveLength(20);
    expect(list.every((x) => !x.submitted)).toBe(true);
  });
});

describe("getUpcomingEvents", () => {
  it("queries the calendar window and maps events", async () => {
    const { a, t } = setup();
    const events = await a.getUpcomingEvents(7);
    z.array(LmsEvent).parse(events);
    expect(t.calls[0]?.args).toEqual({
      timesortfrom: 1793181600,
      timesortto: 1793181600 + 7 * 86400,
      limitnum: 50,
      limittononsuspendedevents: true
    });
    expect(events[0]).toEqual({
      id: "70001",
      courseId: "3017",
      courseName: "Algorytmy i struktury danych (INF-ALG-2025Z)",
      name: "Zadanie 1: Sortowanie jest do oddania",
      type: "due",
      moduleType: "assign",
      start: "2026-10-30T21:59:00.000Z",
      end: null,
      url: "https://platon.vistula.edu.pl/mod/assign/view.php?id=410012&action=editsubmission",
      actionName: "Dodaj zgłoszenie"
    });
    expect(events[1]).toMatchObject({ end: "2026-10-29T12:30:00.000Z", moduleType: "quiz" });
    expect(events[2]).toMatchObject({ courseId: null, courseName: null, moduleType: null, url: null, actionName: null, type: "course" });
  });
  it("defaults to 14 days", async () => {
    const { a, t } = setup();
    await a.getUpcomingEvents();
    expect(t.calls[0]?.args.timesortto).toBe(1793181600 + 14 * 86400);
  });
});

describe("getGrades", () => {
  it("passes courseid and the user id from site info", async () => {
    const { a, t } = setup();
    const items = await a.getGrades("3017");
    z.array(LmsGradeItem).parse(items);
    const call = t.calls.find((c) => c.fn === "gradereport_user_get_grade_items");
    expect(call?.args).toEqual({ courseid: 3017, userid: 48213 });
    expect(items).toHaveLength(3);
    expect(items[0]).toMatchObject({
      id: "123001", courseId: "3017", itemName: "Zadanie 0: Rozgrzewka", itemType: "mod",
      gradeFormatted: "9,00", gradeRaw: 9, gradeMax: 10, percentage: "90,00 %",
      feedbackHtml: "<p>Dobra robota.</p>", dateGraded: new Date(1792600000 * 1000).toISOString()
    });
    expect(items[1]).toMatchObject({ gradeFormatted: null, gradeRaw: null, percentage: null, feedbackHtml: null, dateGraded: null });
    expect(items[2]).toMatchObject({ itemName: "Suma końcowa", itemType: "course" });
  });
  it("uses the stored userId without calling site info", async () => {
    const { a, t } = setup({}, { baseUrl: BASE, userId: 77 });
    await a.getGrades("3017");
    expect(t.calls.some((c) => c.fn === "core_webservice_get_site_info")).toBe(false);
    expect(t.calls[0]?.args).toMatchObject({ userid: 77 });
  });
});

describe("getAnnouncements", () => {
  it("reads news forums of the configured courses, newest first", async () => {
    const { a, t } = setup({}, { baseUrl: BASE, newsCourseIds: [1, 3017] });
    const items = await a.getAnnouncements(10);
    z.array(Announcement).parse(items);
    expect(t.calls[0]).toEqual({ fn: "mod_forum_get_forums_by_courses", args: { courseids: [1, 3017] } });
    // only news forums (ids 1, 7001) are read
    expect(t.calls.filter((c) => c.fn === "mod_forum_get_forum_discussions").map((c) => c.args.forumid).sort()).toEqual([1, 7001]);
    expect(items.map((i) => i.id)).toEqual(["platon:16001", "platon:15001", "platon:15002"]);
    expect(items[1]).toMatchObject({
      source: "platon",
      title: "Zmiana godzin pracy dziekanatu",
      author: "Dziekanat Informatyki",
      category: "Aktualności",
      url: `${BASE}mod/forum/discuss.php?d=15001`,
      publishedAt: new Date(1793000000 * 1000).toISOString()
    });
    expect(items[2]).toMatchObject({ title: "Rejestracja na lektoraty", author: null }); // empty subject falls back to name
  });
  it("respects the limit and tolerates one failing forum", async () => {
    const { a } = setup(
      { mod_forum_get_forum_discussions: (args: Record<string, unknown>) => { if (args.forumid === 7001) throw new AdapterAuthError("x"); return fixture("forum_discussions_site"); } },
      { baseUrl: BASE, newsCourseIds: [1, 3017] }
    );
    const items = await a.getAnnouncements(1);
    expect(items).toHaveLength(1);
    expect(items[0]?.id).toBe("platon:15001");
  });
});

describe("getFileDownload", () => {
  it("delegates to the transport", async () => {
    const { a } = setup();
    expect(await a.getFileDownload(`${BASE}pluginfile.php/1/a.pdf`)).toEqual({
      url: `${BASE}pluginfile.php/1/a.pdf#rewritten`,
      headers: { Cookie: "MoodleSession=x" }
    });
  });
  it("refuses foreign origins (would leak credentials)", async () => {
    const { a } = setup();
    await expect(a.getFileDownload("https://evil.example/pluginfile.php/1/a.pdf")).rejects.toBeInstanceOf(AdapterUnavailableError);
  });
});

describe("error propagation", () => {
  it("lets auth errors from the transport through", async () => {
    const { a } = setup({ core_course_get_contents: () => { throw new AdapterAuthError(); } });
    await expect(a.getCourseContents("1")).rejects.toBeInstanceOf(AdapterAuthError);
  });
});
