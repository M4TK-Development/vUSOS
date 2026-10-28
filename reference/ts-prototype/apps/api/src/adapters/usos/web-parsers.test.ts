import { describe, expect, it } from "vitest";
import { z } from "zod";
import { AdapterAuthError, AdapterUnavailableError, Announcement, Grade, Message, MessageThread, Payment, StudentProfile, TimetableEntry } from "@vusos/shared";
import { fixture } from "./fixtures/load.js";
import { createUsosAdapter } from "./index.js";
import {
  isLoginRedirect,
  looksLikeLoginPage,
  parseGradesHtml,
  parseMessageHtml,
  parseMessagesHtml,
  parseNewsHtml,
  parsePaymentsHtml,
  parsePlanHtml,
  parseProfileHtml,
  termFromId,
  termIdForDate,
  termIdFromText
} from "./web-parsers.js";

const BASE = "https://usosweb.vistula.example/";

describe("parsePlanHtml", () => {
  it("parses the day-column table", () => {
    const entries = parsePlanHtml(fixture("plan-week.html"), { baseUrl: BASE });
    expect(entries).toHaveLength(5);
    z.array(TimetableEntry).parse(entries);
    expect(entries.map((e) => e.start)).toEqual([...entries.map((e) => e.start)].sort());
    expect(entries[0]).toMatchObject({
      start: "2025-10-06T08:30:00+02:00",
      end: "2025-10-06T10:00:00+02:00",
      courseId: "INF-ALG",
      courseName: { pl: "Algorytmy i struktury danych" },
      classType: "Wykład",
      room: "A-214",
      building: "Budynek A",
      lecturers: ["prof. dr hab. Marek Nowicki"],
      groupNumber: null,
      url: `${BASE}kontroler.php?_action=katalog2/przedmioty/pokazPrzedmiot&prz_kod=INF-ALG`
    });
    const mat = entries.find((e) => e.courseId === "INF-MAT");
    expect(mat).toMatchObject({ groupNumber: 2, classType: "Ćwiczenia", room: "B-105" });
    // single-digit hour and spaces around the dash
    expect(entries.find((e) => e.courseId === "INF-BD")).toMatchObject({
      start: "2025-10-07T09:00:00+02:00",
      end: "2025-10-07T10:30:00+02:00"
    });
    // Friday column is index 4 (empty Thursday column must not shift it)
    expect(entries.find((e) => e.courseId === "INF-ANG")?.start).toBe("2025-10-10T10:00:00+02:00");
    expect(new Set(entries.map((e) => e.id)).size).toBe(entries.length);
  });

  it("parses data-start/data-end activities", () => {
    const entries = parsePlanHtml(fixture("plan-activities.html"));
    expect(entries).toHaveLength(3);
    expect(entries[1]).toMatchObject({
      classType: "Laboratorium",
      groupNumber: 2,
      lecturers: ["Katarzyna Wójcik", "Jan Zając"],
      courseId: "INF-BD"
    });
    expect(entries[2]).toMatchObject({ courseId: null, courseName: { pl: "Egzamin próbny" } });
  });

  it("parses embedded JSON and skips broken items", () => {
    const entries = parsePlanHtml(fixture("plan-json.html"));
    expect(entries).toHaveLength(2);
    expect(entries[0]).toMatchObject({ courseName: { pl: "Algorytmy i struktury danych", en: "Algorithms and data structures" }, room: "A-214" });
    expect(entries[1]?.classType).toBe("Ćwiczenia");
  });

  it("returns [] for unknown markup and the login page", () => {
    expect(parsePlanHtml("<html><body><p>nic</p></body></html>")).toEqual([]);
    expect(parsePlanHtml("")).toEqual([]);
    expect(parsePlanHtml(fixture("login.html"))).toEqual([]);
  });
});

describe("parseGradesHtml", () => {
  const { terms, grades } = parseGradesHtml(fixture("grades.html"));

  it("finds terms from headings and ignores unrelated sections", () => {
    expect(terms).toEqual([
      { id: "2025Z", name: "Semestr zimowy 2025/26" },
      { id: "2024L", name: "Semestr letni 2024/25" }
    ]);
  });

  it("maps rows to schema-valid grades", () => {
    z.array(Grade).parse(grades);
    expect(grades).toHaveLength(5);
    expect(grades[0]).toMatchObject({
      termId: "2025Z",
      courseId: "INF-PROG1",
      courseName: { pl: "Programowanie obiektowe" },
      classType: "Laboratorium",
      examSession: 1,
      valueSymbol: "4.5",
      valueDescription: { pl: "dobry plus", en: "good plus" },
      passes: true,
      countsIntoAverage: true,
      dateModified: "2025-11-12T00:00:00+01:00",
      gradedBy: "mgr inż. Katarzyna Wójcik"
    });
    const zal = grades.find((g) => g.courseId === "INF-ANG");
    expect(zal).toMatchObject({ valueSymbol: "zal", countsIntoAverage: false, passes: true, dateModified: "2025-11-12T00:00:00+01:00" });
    const retake = grades.filter((g) => g.courseId === "INF-ANM");
    expect(retake.map((g) => [g.examSession, g.valueSymbol, g.passes])).toEqual([[1, "2.0", false], [2, "3.5", true]]);
    // course without a link falls back to a slug id
    expect(grades.find((g) => g.courseName.pl === "Podstawy programowania")?.courseId).toBe("web:podstawy-programowania");
    expect(new Set(grades.map((g) => g.id)).size).toBe(grades.length);
  });

  it("returns empty results for unknown markup", () => {
    expect(parseGradesHtml("<table><tr><td>x</td></tr></table>")).toEqual({ terms: [], grades: [] });
    expect(parseGradesHtml("")).toEqual({ terms: [], grades: [] });
  });
});

describe("parsePaymentsHtml", () => {
  const payments = parsePaymentsHtml(fixture("payments.html"), "2025-11-20");

  it("maps rows, amounts, states and the account number", () => {
    z.array(Payment).parse(payments);
    expect(payments).toHaveLength(5);
    const [r1, r2, r3, fee, r4] = payments;
    expect(r1).toMatchObject({ amount: 1250, paid: 1250, state: "paid", dueDate: "2025-10-10", type: "tuition", interest: 0 });
    expect(r2).toMatchObject({ state: "overdue", dueDate: "2025-11-10", interest: 12.4, paid: 0 });
    expect(r3).toMatchObject({ state: "partially_paid", paid: 500, dueDate: "2025-12-10" });
    expect(fee).toMatchObject({ state: "cancelled", amount: 22, type: "id_card", interest: null });
    expect(r4).toMatchObject({ state: "unpaid", dueDate: "2026-01-10" });
    expect(payments.every((p) => p.accountNumber === "12345678901234567890123456")).toBe(true);
    expect(new Set(payments.map((p) => p.id)).size).toBe(5);
  });

  it("flips an unpaid row to overdue once the deadline has passed, and tolerates junk", () => {
    const late = parsePaymentsHtml(fixture("payments.html"), "2026-01-11");
    expect(late.find((p) => p.dueDate === "2026-01-10")?.state).toBe("overdue");
    expect(parsePaymentsHtml("<p>nic</p>", "2025-11-20")).toEqual([]);
  });
});

describe("parseNewsHtml and parseProfileHtml", () => {
  it("maps news items newest first", () => {
    const news = parseNewsHtml(fixture("home.html"), { baseUrl: BASE });
    z.array(Announcement).parse(news);
    expect(news.map((n) => n.title)).toEqual(["Zapisy na seminaria dyplomowe", "Zmiana godzin pracy dziekanatu", "Stypendia rektora - wnioski"]);
    expect(news[0]).toMatchObject({
      id: "usos-web-news:9001",
      author: "Dziekanat",
      category: "Dydaktyka",
      publishedAt: "2025-10-20T00:00:00+02:00",
      url: `${BASE}kontroler.php?_action=home/aktualnosc&id=9001`
    });
    expect(news[0]?.bodyHtml).toContain("<strong>27 października</strong>");
    expect(news[1]?.publishedAt).toBe("2025-10-14T09:30:00+02:00");
    expect(news[2]).toMatchObject({ publishedAt: "2025-10-01T00:00:00+02:00", author: null, url: null });
    expect(parseNewsHtml("<div></div>")).toEqual([]);
  });

  it("parses the profile", () => {
    const p = parseProfileHtml(fixture("home.html"));
    expect(p).not.toBeNull();
    StudentProfile.parse(p);
    expect(p).toMatchObject({
      id: "61234",
      firstName: "Anna",
      lastName: "Nowak",
      studentNumber: "61234",
      programmes: [{ id: "INF-I-S", name: { pl: "Informatyka" }, stage: "studia I stopnia (stacjonarne)" }]
    });
    expect(parseProfileHtml("<html></html>")).toBeNull();
  });
});

describe("messages", () => {
  it("parses the list with unread detection, skipping rows without an id", () => {
    const list = parseMessagesHtml(fixture("messages.html"), { baseUrl: BASE });
    z.array(MessageThread).parse(list);
    expect(list.map((m) => [m.id, m.unread, m.from])).toEqual([
      ["5501", true, "Dziekanat"],
      ["5490", false, "Biuro ds. Studenckich"]
    ]);
    expect(list[0]).toMatchObject({ subject: "Potwierdzenie złożenia wniosku", date: "2025-10-21T13:05:00+02:00" });
    expect(parseMessagesHtml("<table><tr><td>x</td></tr></table>")).toEqual([]);
  });

  it("parses a single message with attachments", () => {
    const m = parseMessageHtml(fixture("message.html"), "5501", { baseUrl: BASE });
    expect(m).not.toBeNull();
    Message.parse(m);
    expect(m).toMatchObject({ subject: "Potwierdzenie złożenia wniosku", from: "Dziekanat", to: ["Anna Nowak"], date: "2025-10-21T13:05:00+02:00" });
    expect(m?.bodyHtml).toContain("W/2025/114");
    expect(m?.attachments).toEqual([
      { name: "potwierdzenie.pdf", url: `${BASE}kontroler.php?_action=dla_stud/studia/komunikacja&pobierz=77`, size: null }
    ]);
    expect(parseMessageHtml("<p>x</p>", "1")).toBeNull();
  });
});

describe("login detection and term helpers", () => {
  it("recognises login redirects and pages", () => {
    expect(isLoginRedirect("https://usosweb.vistula.example/kontroler.php?_action=logowanie/index")).toBe(true);
    expect(isLoginRedirect("https://login.microsoftonline.com/common/oauth2/authorize?x=1")).toBe(true);
    expect(isLoginRedirect("/kontroler.php?_action=home/index")).toBe(false);
    expect(looksLikeLoginPage(fixture("login.html"))).toBe(true);
    expect(looksLikeLoginPage(fixture("home.html"))).toBe(false);
    expect(looksLikeLoginPage('<meta http-equiv="refresh" content="0;url=https://login.microsoftonline.com/x">Wyloguj')).toBe(true);
  });

  it("derives term ids, dates and current term", () => {
    expect(termIdFromText("Semestr zimowy 2025/26 (2025Z)")).toBe("2025Z");
    expect(termIdFromText("Semestr letni 2024/25")).toBe("2024L");
    expect(termIdFromText("Informacje dodatkowe")).toBeNull();
    expect(termIdForDate("2025-11-20")).toBe("2025Z");
    expect(termIdForDate("2026-01-10")).toBe("2025Z");
    expect(termIdForDate("2026-04-10")).toBe("2025L");
    expect(termFromId("2025Z", "2025-11-20")).toMatchObject({ startDate: "2025-10-01", endDate: "2026-02-15", isCurrent: true });
    expect(termFromId("bogus", "2025-11-20")).toBeNull();
  });
});

// ---------- adapter with a fake fetch ----------

type Handler = (url: URL, init: RequestInit | undefined) => Response;
function webAdapter(handler: Handler, calls: { url: string; init?: RequestInit }[] = [], now = "2025-11-20T10:00:00Z") {
  return createUsosAdapter(
    { mode: "web", cookies: "PHPSESSID=abc; usos=1", baseUrl: BASE },
    { apiBaseUrl: "x", consumerKey: "k", consumerSecret: "s", usoswebBaseUrl: BASE },
    {
      now: () => new Date(now),
      fetchImpl: async (input, init) => {
        calls.push({ url: input, init });
        return handler(new URL(input), init);
      }
    }
  );
}
const html = (body: string, status = 200) => new Response(body, { status, headers: { "content-type": "text/html" } });

describe("web adapter", () => {
  it("sends cookies, requests the right action and parses the answer", async () => {
    const calls: { url: string; init?: RequestInit }[] = [];
    const a = webAdapter((u) => {
      const action = u.searchParams.get("_action");
      return html(action === "dla_stud/studia/oceny/index" ? fixture("grades.html") : fixture("payments.html"));
    }, calls);
    expect(await a.getGrades(["2025Z"])).toHaveLength(2);
    expect((await a.getPayments()).length).toBe(5);
    expect(calls[0]?.url).toContain("kontroler.php?_action=dla_stud%2Fstudia%2Foceny%2Findex");
    expect((calls[0]?.init?.headers as Record<string, string>).Cookie).toBe("PHPSESSID=abc; usos=1");
    const terms = await a.getTerms();
    expect(terms.map((t) => t.id)).toEqual(["2025Z", "2024L"]);
    expect(terms[0]?.isCurrent).toBe(true);
  });

  it("queries each week of the requested range and filters to it", async () => {
    const calls: string[] = [];
    const a = webAdapter((u) => {
      calls.push(u.searchParams.get("plan_start") ?? "");
      return html(fixture("plan-week.html"));
    });
    const out = await a.getTimetable("2025-10-07", "2025-10-08");
    expect(calls).toEqual(["2025-10-06"]);
    expect(out.map((e) => e.courseId)).toEqual(["INF-BD", "INF-SO"]);
    const two = await a.getTimetable("2025-10-06", "2025-10-13");
    expect(calls).toEqual(["2025-10-06", "2025-10-06", "2025-10-13"]);
    expect(two).toHaveLength(5);
  });

  it("derives courses from the timetable and reads messages/news", async () => {
    const a = webAdapter((u) => {
      const action = u.searchParams.get("_action");
      if (action === "home/plan") return html(fixture("plan-week.html"));
      if (action === "dla_stud/studia/komunikacja") return html(u.searchParams.get("msg_id") ? fixture("message.html") : fixture("messages.html"));
      return html(fixture("home.html"));
    }, [], "2025-10-08T10:00:00Z");
    const courses = await a.getCourses();
    expect(courses.length).toBeGreaterThanOrEqual(5);
    expect(courses.every((c) => c.termId === "2025Z")).toBe(true);
    expect(await a.getCourses("2024L")).toEqual([]);
    expect(await a.listMessages({ limit: 1 })).toHaveLength(1);
    expect((await a.getMessage("5501")).subject).toBe("Potwierdzenie złożenia wniosku");
    expect(await a.getAnnouncements(2)).toHaveLength(2);
    expect((await a.getProfile()).lastName).toBe("Nowak");
    expect(await a.getDeanOfficeContacts()).toEqual([]);
    await expect(a.sendMessage(["x"], "s", "b")).rejects.toBeInstanceOf(AdapterUnavailableError);
  });

  it("throws AdapterAuthError on login redirects, SSO redirects, login pages and 401/403", async () => {
    const redirect = (location: string): Handler => () => new Response(null, { status: 302, headers: { location } });
    await expect(webAdapter(redirect("/kontroler.php?_action=logowanie/index")).getPayments()).rejects.toBeInstanceOf(AdapterAuthError);
    await expect(webAdapter(redirect("https://login.microsoftonline.com/x")).getPayments()).rejects.toBeInstanceOf(AdapterAuthError);
    await expect(webAdapter(redirect("https://elsewhere.example/")).getPayments()).rejects.toBeInstanceOf(AdapterAuthError);
    await expect(webAdapter(() => html(fixture("login.html"))).getGrades()).rejects.toBeInstanceOf(AdapterAuthError);
    await expect(webAdapter(() => html("", 403)).getGrades()).rejects.toBeInstanceOf(AdapterAuthError);
  });

  it("follows same-origin redirects, maps server errors and loops to AdapterUnavailableError", async () => {
    let n = 0;
    const a = webAdapter((u) => {
      if (u.pathname.endsWith("kontroler.php") && n++ === 0) {
        return new Response(null, { status: 302, headers: { location: "kontroler.php?_action=dla_stud/platnosci/index&x=1" } });
      }
      return html(fixture("payments.html"));
    });
    expect(await a.getPayments()).toHaveLength(5);
    await expect(webAdapter(() => html("boom", 500)).getPayments()).rejects.toBeInstanceOf(AdapterUnavailableError);
    const loop = webAdapter(() => new Response(null, { status: 302, headers: { location: "/kontroler.php?_action=x" } }));
    await expect(loop.getPayments()).rejects.toThrow(/przekierowa/);
    const down = createUsosAdapter(
      { mode: "web", cookies: "a=b", baseUrl: BASE },
      { apiBaseUrl: "x", consumerKey: "k", consumerSecret: "s", usoswebBaseUrl: BASE },
      { fetchImpl: async () => { throw new Error("ECONNRESET"); } }
    );
    await expect(down.getPayments()).rejects.toBeInstanceOf(AdapterUnavailableError);
  });

  it("returns empty lists (not errors) when markup is unrecognised", async () => {
    const a = webAdapter(() => html("<html><body>Wyloguj <p>coś innego</p></body></html>"));
    expect(await a.getGrades()).toEqual([]);
    expect(await a.getPayments()).toEqual([]);
    expect(await a.listMessages()).toEqual([]);
    expect(await a.getAnnouncements()).toEqual([]);
    expect(await a.getTimetable("2025-10-06", "2025-10-12")).toEqual([]);
    await expect(a.getProfile()).rejects.toBeInstanceOf(AdapterUnavailableError);
  });
});
