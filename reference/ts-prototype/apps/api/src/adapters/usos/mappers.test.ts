import { describe, expect, it } from "vitest";
import { AdapterUnavailableError, Announcement, Contact, CourseUnit, Grade, Message, MessageThread, Payment, StudentProfile, Term, TimetableEntry } from "@vusos/shared";
import { fixtureJson } from "./fixtures/load.js";
import {
  collectLecturerIds,
  computeAverage,
  describeGrade,
  mapActivities,
  mapCourseUnits,
  mapFacultyContacts,
  mapGrades,
  mapMessage,
  mapMessageThread,
  mapNews,
  mapPayments,
  mapProfile,
  mapTerms,
  normalizeGradeSymbol,
  numericGrade,
  toLangText
} from "./mappers.js";

describe("toLangText", () => {
  it("accepts strings, LangText and junk", () => {
    expect(toLangText("a")).toEqual({ pl: "a" });
    expect(toLangText({ pl: "a", en: "b" })).toEqual({ pl: "a", en: "b" });
    expect(toLangText({ en: "only en" })).toEqual({ pl: "only en", en: "only en" });
    expect(toLangText(null, "fb")).toEqual({ pl: "fb" });
  });
});

describe("mapProfile", () => {
  it("maps users/user", () => {
    const p = mapProfile(fixtureJson("user.json"));
    expect(StudentProfile.parse(p)).toEqual(p);
    expect(p).toMatchObject({
      id: "1234567",
      firstName: "Anna",
      lastName: "Nowak",
      studentNumber: "61234",
      photoUrl: "https://usosapps.vistula.example/photos/1234567_200.jpg"
    });
    expect(p.programmes).toEqual([
      { id: "98765", name: { pl: "Informatyka", en: "Computer Science" }, status: "active", stage: "Studia I stopnia, stacjonarne" }
    ]);
  });

  it("nulls out invalid e-mail / photo and rejects garbage", () => {
    const p = mapProfile({ id: 1, first_name: "A", last_name: "B", email: "nope", photo_urls: { "50x50": "not a url" } });
    expect(p.email).toBeNull();
    expect(p.photoUrl).toBeNull();
    expect(() => mapProfile({ nothing: true })).toThrow(AdapterUnavailableError);
  });
});

describe("mapTerms", () => {
  const raw = (fixtureJson("courses-user.json") as { terms: unknown[] }).terms;
  it("maps, sorts newest first, skips entries without dates, flags current via finish_date", () => {
    const terms = mapTerms(raw, "2026-02-10");
    expect(terms.map((t) => t.id)).toEqual(["2025Z", "2024L"]);
    for (const t of terms) Term.parse(t);
    expect(terms[0]?.isCurrent).toBe(true); // 2026-02-10 is after end_date but before finish_date
    expect(terms[1]?.isCurrent).toBe(false);
    expect(mapTerms(raw, "2025-11-01")[0]?.isCurrent).toBe(true);
    expect(mapTerms(raw, "2026-03-01")[0]?.isCurrent).toBe(false);
  });
});

describe("mapCourseUnits", () => {
  const raw = fixtureJson("courses-user.json");
  it("flattens a dict-of-terms into one unit per user group", () => {
    const units = mapCourseUnits(raw);
    for (const u of units) CourseUnit.parse(u);
    const alg = units.filter((u) => u.courseId === "INF-ALG");
    expect(alg.map((u) => [u.id, u.classType, u.groupNumber])).toEqual([
      ["5201", "Wykład", 1],
      ["5202", "Ćwiczenia", 1]
    ]);
    expect(alg[1]?.lecturers).toEqual(["Tomasz Lis", "Jan Zając"]);
    expect(alg[0]?.termId).toBe("2025Z");
  });

  it("emits a placeholder unit for editions without groups and takes term from the dict key", () => {
    const units = mapCourseUnits(raw);
    expect(units.find((u) => u.courseId === "INF-ANG")).toMatchObject({ id: "INF-ANG@2025Z", classType: "", groupNumber: null });
    expect(units.find((u) => u.courseId === "INF-AM2")?.termId).toBe("2024L");
  });

  it("filters by term and tolerates a flat list", () => {
    expect(mapCourseUnits(raw, "2024L").map((u) => u.courseId)).toEqual(["INF-AM2"]);
    const flat = { course_editions: [{ course_id: "X", course_name: "X", term_id: "2025Z", user_groups: [] }] };
    expect(mapCourseUnits(flat, "2025Z")).toHaveLength(1);
    expect(() => mapCourseUnits("garbage")).toThrow(AdapterUnavailableError);
  });
});

describe("mapActivities", () => {
  const raw = fixtureJson("tt-user.json");
  it("converts to ISO with Warsaw offset, resolves lecturers, skips junk", () => {
    expect(collectLecturerIds(raw)).toEqual(["101", "102"]);
    const entries = mapActivities(raw, new Map([["101", "Marek Nowicki"], ["102", "Tomasz Lis"]]));
    expect(entries).toHaveLength(2);
    for (const e of entries) TimetableEntry.parse(e);
    expect(entries[0]).toMatchObject({
      source: "usos",
      start: "2025-10-06T08:30:00+02:00",
      end: "2025-10-06T10:00:00+02:00",
      courseId: "INF-ALG",
      courseName: { pl: "Algorytmy i struktury danych", en: "Algorithms and data structures" },
      classType: "Wykład",
      groupNumber: 1,
      room: "A-214",
      building: "Budynek A",
      lecturers: ["Marek Nowicki", "Tomasz Lis"]
    });
    expect(entries[1]).toMatchObject({ courseId: null, classType: null, room: null, building: null, courseName: { pl: "Egzamin próbny" } });
    expect(new Set(entries.map((e) => e.id)).size).toBe(2);
  });

  it("leaves lecturers empty when names are unresolved", () => {
    expect(mapActivities(raw)[0]?.lecturers).toEqual([]);
  });
});

describe("mapGrades", () => {
  const raw = fixtureJson("grades-terms2.json");
  const ctx = {
    courseNames: new Map([["INF-ALG", { pl: "Algorytmy i struktury danych" }]]),
    unitClassTypes: new Map([["5202", "Ćwiczenia"]])
  };
  const grades = mapGrades(raw, ctx);

  it("flattens unit and course grades, skipping nulls", () => {
    for (const g of grades) Grade.parse(g);
    expect(grades).toHaveLength(5);
    expect(new Set(grades.map((g) => g.id)).size).toBe(5);
  });

  it("maps fields", () => {
    const unitGrade = grades.find((g) => g.courseId === "INF-ALG" && g.classType === "Ćwiczenia");
    expect(unitGrade).toMatchObject({
      termId: "2025Z",
      courseName: { pl: "Algorytmy i struktury danych" },
      examSession: 1,
      valueSymbol: "4.0",
      valueDescription: { pl: "dobry", en: "good" },
      passes: true,
      countsIntoAverage: true,
      dateModified: "2025-11-19T12:05:00+01:00",
      gradedBy: "Tomasz Lis",
      comment: "Kolokwium 1: 78%"
    });
    const courseGrade = grades.find((g) => g.courseId === "INF-ALG" && g.classType === null);
    expect(courseGrade?.countsIntoAverage).toBe(true); // "Y"
    expect(grades.find((g) => g.courseId === "INF-AM2" && g.examSession === 1)).toMatchObject({
      passes: false,
      courseName: { pl: "INF-AM2" } // not in the name index -> falls back to the id
    });
  });

  it("handles list-shaped sessions and non-counting pass/fail grades", () => {
    const zal = grades.find((g) => g.courseId === "INF-ANG2");
    expect(zal).toMatchObject({ valueSymbol: "zal", countsIntoAverage: false, examSession: 1 });
  });

  it("returns [] for non-object input", () => {
    expect(mapGrades(null)).toEqual([]);
    expect(mapGrades([])).toEqual([]);
  });
});

describe("grade symbols and average", () => {
  it("normalises symbols", () => {
    expect(normalizeGradeSymbol("4,5")).toBe("4.5");
    expect(normalizeGradeSymbol("5")).toBe("5.0");
    expect(normalizeGradeSymbol("ZAL")).toBe("zal");
    expect(normalizeGradeSymbol("Niezaliczone")).toBe("nzal");
    expect(numericGrade("5!")).toBeNull();
    expect(describeGrade("2.0")).toMatchObject({ passes: false, description: { pl: "niedostateczny" } });
    expect(describeGrade("3.0").passes).toBe(true);
  });

  it("averages only counted numeric grades and uses the latest session per course", () => {
    const grades = mapGrades(fixtureJson("grades-terms2.json"), { unitClassTypes: new Map([["5202", "Ćwiczenia"]]) });
    // 2025Z: INF-ALG unit 4.0 + course 3.5 (different classType keys) ; 2024L: AM2 latest = 3.5 ; zal ignored
    expect(computeAverage(grades)).toBe(3.67);
    expect(computeAverage([])).toBeNull();
  });
});

describe("mapPayments", () => {
  const raw = fixtureJson("payments.json");
  const payments = mapPayments(raw, "2025-11-20");

  it("is schema-valid and skips junk", () => {
    for (const p of payments) Payment.parse(p);
    expect(payments.map((p) => p.id)).toEqual(["7001", "7002", "7003", "7004", "7005", "7006"]);
  });

  it("derives state, paid amount and overdue from the deadline", () => {
    const byId = Object.fromEntries(payments.map((p) => [p.id, p]));
    expect(byId["7001"]).toMatchObject({ state: "paid", paid: 1250, amount: 1250, accountNumber: "61109010140000071219812874" });
    expect(byId["7002"]).toMatchObject({ state: "overdue", paid: 0, interest: 4.73, dueDate: "2025-11-10" });
    expect(byId["7003"]).toMatchObject({ state: "partially_paid", paid: 500 }); // future deadline, partly paid
    expect(byId["7004"]).toMatchObject({ amount: 1250, description: { pl: "Czesne - rata 4/5" } });
    expect(byId["7005"]?.state).toBe("cancelled");
    expect(byId["7006"]).toMatchObject({ amount: 100, state: "paid", dueDate: null, currency: "PLN" });
  });
});

describe("messages", () => {
  const list = (fixtureJson("mail-messages.json") as { messages: unknown[] }).messages;

  it("maps a thread", () => {
    const t = mapMessageThread(list[0]);
    expect(MessageThread.parse(t)).toEqual(t);
    expect(t).toMatchObject({
      id: "5501",
      source: "usos",
      from: "Dziekanat Informatyki",
      to: ["Anna Nowak"],
      date: "2025-10-21T13:05:00+02:00",
      unread: true,
      preview: "Twój wniosek został zarejestrowany."
    });
    expect(mapMessageThread(list[1])).toMatchObject({ from: "Biuro ds. Studenckich", unread: false, preview: "Sesja zimowa rozpocznie się..." });
    expect(mapMessageThread({ nope: 1 })).toBeNull();
  });

  it("maps a full message with attachments", () => {
    const m = mapMessage(list[0]);
    expect(Message.parse(m)).toEqual(m);
    expect(m.bodyHtml).toBe("<p>Twój wniosek został zarejestrowany.</p>");
    expect(m.attachments).toEqual([{ name: "potwierdzenie.pdf", url: "https://usosapps.vistula.example/att/1", size: 20480 }]);
    expect(() => mapMessage({})).toThrow(AdapterUnavailableError);
  });
});

describe("faculty and news", () => {
  it("maps the faculty card to a dean's office contact", () => {
    const c = mapFacultyContacts(fixtureJson("faculty.json"));
    expect(c).toHaveLength(1);
    Contact.parse(c[0]);
    expect(c[0]).toMatchObject({ name: "Wydział Informatyki", role: "Dziekanat", phone: "+48 22 000 01 01", email: null });
    expect(mapFacultyContacts("x")).toEqual([]);
  });

  it("maps news newest first, handling LangText and plain strings", () => {
    const news = mapNews(fixtureJson("news.json"));
    expect(news.map((n) => n.id)).toEqual(["usos-news:9001", "usos-news:9002"]);
    for (const n of news) Announcement.parse(n);
    expect(news[0]).toMatchObject({
      title: "Zapisy na seminaria",
      bodyHtml: "<p>Zapisy od poniedziałku.</p>",
      author: "Jan Kowalski",
      category: "Dydaktyka",
      publishedAt: "2025-10-20T10:00:00+02:00"
    });
    expect(news[1]).toMatchObject({ author: null, category: null, publishedAt: "2025-10-14T00:00:00+02:00" });
  });
});
