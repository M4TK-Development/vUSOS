import { AdapterAuthError } from "@vusos/shared";
import type { Announcement, Grade, LmsEvent, MessageThread, Payment, StudentProfile, TimetableEntry } from "@vusos/shared";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { loginDev, makeApp } from "../testing.js";
import { addDays, warsawDate } from "../util/time.js";

// The real adapters are developed separately: replace both factories with tiny fakes.
const fakes = vi.hoisted(() => ({ usos: {} as Record<string, unknown>, platon: {} as Record<string, unknown> }));
vi.mock("../adapters/usos/index.js", () => ({
  createUsosAdapter: () => fakes.usos,
  createUsosOAuthClient: () => ({})
}));
vi.mock("../adapters/platon/index.js", () => ({ createPlatonAdapter: () => fakes.platon }));

const text = (pl: string) => ({ pl });
const today = warsawDate();

const profile: StudentProfile = {
  id: "1", firstName: "Jan", lastName: "Kowalski", email: "jan@stu.vistula.edu.pl", studentNumber: "12345", photoUrl: null, programmes: []
};
const entry = (id: string, start: string): TimetableEntry => ({
  id, source: "usos", start, end: start, courseId: "c", courseName: text("Kurs"), classType: "WYK", groupNumber: 1,
  room: null, building: null, lecturers: [], url: null
});
const grade = (id: string, dateModified: string | null): Grade => ({
  id, termId: "t", courseId: "c", courseName: text(id), classType: null, examSession: 1, valueSymbol: "5", valueDescription: null,
  passes: true, countsIntoAverage: true, dateModified, gradedBy: null, comment: null
});
const payment = (id: string, state: Payment["state"]): Payment => ({
  id, type: "tuition", description: text(id), amount: 100, paid: 0, currency: "PLN", dueDate: null, state, interest: null, accountNumber: null
});
const msg = (id: string, unread: boolean): MessageThread => ({
  id, source: "usos", subject: id, from: "x", to: [], date: "2025-01-01", preview: "", unread, url: null
});
const news = (id: string, source: "usos" | "platon", publishedAt: string): Announcement => ({
  id, source, title: id, bodyHtml: "", publishedAt, author: null, category: null, url: null
});
const event = (id: string, start: string): LmsEvent => ({
  id, courseId: "1", courseName: "K", name: id, type: "due", moduleType: "assign", start, end: null, url: null, actionName: null
});

beforeEach(() => {
  fakes.usos = {
    getProfile: async () => profile,
    getTimetable: async () => [
      entry("late", `${today}T15:00:00`),
      entry("early", `${today}T08:00:00`),
      entry("tomorrow", `${addDays(today, 1)}T09:00:00`)
    ],
    getGrades: async () => [grade("g1", "2025-01-01"), grade("g2", "2025-03-01"), grade("g3", null), ...[4, 5, 6, 7].map((n) => grade(`g${n}`, `2025-02-0${n}`))],
    getPayments: async () => [payment("p1", "paid"), payment("p2", "overdue"), payment("p3", "unpaid"), payment("p4", "cancelled")],
    listMessages: async () => [msg("m1", true), msg("m2", false), msg("m3", true)],
    getAnnouncements: async () => [news("u1", "usos", "2025-05-01"), news("u2", "usos", "2025-01-01")]
  };
  fakes.platon = {
    getUpcomingEvents: async () => [event("e2", "2025-06-02T10:00:00Z"), event("e1", "2025-06-01T10:00:00Z")],
    getAnnouncements: async () => [news("p1", "platon", "2025-04-01"), news("p2", "platon", "2025-06-01")]
  };
});

let t: ReturnType<typeof makeApp>;
afterEach(async () => {
  await t.app.close();
  t.db.close();
});

describe("GET /api/dashboard (ADAPTER_MODE=mock)", () => {
  it("requires authentication", async () => {
    t = makeApp();
    expect((await t.app.inject({ method: "GET", url: "/api/dashboard" })).statusCode).toBe(401);
  });

  it("aggregates all sources", async () => {
    t = makeApp();
    const cookie = await loginDev(t.app);
    const res = await t.app.inject({ method: "GET", url: "/api/dashboard", headers: { cookie } });
    expect(res.statusCode).toBe(200);
    const d = res.json();
    expect(d.profile.firstName).toBe("Jan");
    expect(d.todayClasses.map((e: TimetableEntry) => e.id)).toEqual(["early", "late"]);
    expect(d.upcomingDeadlines.map((e: LmsEvent) => e.id)).toEqual(["e1", "e2"]);
    expect(d.recentGrades).toHaveLength(5);
    expect(d.recentGrades[0].id).toBe("g2");
    expect(d.recentGrades.map((g: Grade) => g.id)).not.toContain("g3");
    expect(d.paymentsDue.map((p: Payment) => p.id)).toEqual(["p2", "p3"]);
    expect(d.unreadMessages).toBe(2);
    expect(d.announcements.map((a: Announcement) => a.id)).toEqual(["p2", "u1", "p1", "u2"]);
    expect(d.connections).toEqual({ usos: "connected", platon: "connected" });
  });

  it("degrades gracefully when sources fail", async () => {
    t = makeApp();
    const cookie = await loginDev(t.app);
    fakes.platon = {
      getUpcomingEvents: async () => { throw new AdapterAuthError(); },
      getAnnouncements: async () => { throw new Error("boom"); }
    };
    fakes.usos = { ...fakes.usos, getGrades: async () => { throw new Error("usos down"); } };
    const res = await t.app.inject({ method: "GET", url: "/api/dashboard", headers: { cookie } });
    expect(res.statusCode).toBe(200);
    const d = res.json();
    expect(d.upcomingDeadlines).toEqual([]);
    expect(d.recentGrades).toEqual([]);
    expect(d.announcements.map((a: Announcement) => a.id)).toEqual(["u1", "u2"]);
    expect(d.profile).not.toBeNull();
  });
});

describe("GET /api/dashboard (ADAPTER_MODE=live)", () => {
  it("returns empty data for unconnected systems instead of failing", async () => {
    t = makeApp({ ADAPTER_MODE: "live" });
    const cookie = await loginDev(t.app);
    const res = await t.app.inject({ method: "GET", url: "/api/dashboard", headers: { cookie } });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toMatchObject({
      profile: null, todayClasses: [], upcomingDeadlines: [], recentGrades: [], paymentsDue: [], unreadMessages: 0, announcements: [],
      connections: { usos: "disconnected", platon: "disconnected" }
    });
  });

  it("marks a connection expired when the adapter reports an auth failure, and maps endpoint errors to 424/409", async () => {
    t = makeApp({ ADAPTER_MODE: "live" });
    const cookie = await loginDev(t.app);
    const userId = "dev:jan@stu.vistula.edu.pl";
    t.app.ctx.store.saveConnection(userId, "usos", { mode: "web", cookies: "PHPSESSID=x", baseUrl: "https://usosweb.example.edu/" });
    fakes.usos = { ...fakes.usos, getProfile: async () => { throw new AdapterAuthError(); } };

    const prof = await t.app.inject({ method: "GET", url: "/api/usos/profile", headers: { cookie } });
    expect(prof.statusCode).toBe(424);
    expect(prof.json()).toMatchObject({ code: "ADAPTER_AUTH", system: "usos" });
    expect(t.app.ctx.store.getConnection(userId, "usos")?.status).toBe("expired");

    const plat = await t.app.inject({ method: "GET", url: "/api/platon/courses", headers: { cookie } });
    expect(plat.statusCode).toBe(409);
    expect(plat.json()).toMatchObject({ code: "NOT_CONNECTED", system: "platon" });

    const dash = await t.app.inject({ method: "GET", url: "/api/dashboard", headers: { cookie } });
    expect(dash.json().connections).toEqual({ usos: "expired", platon: "disconnected" });
  });
});

describe("response cache", () => {
  it("serves repeated calls from the TTL cache and bypasses it for TTL=0", async () => {
    t = makeApp({ CACHE_TTL_SECONDS: "60" });
    const cookie = await loginDev(t.app);
    let calls = 0;
    fakes.usos = { getProfile: async () => { calls++; return profile; } };
    await t.app.inject({ method: "GET", url: "/api/usos/profile", headers: { cookie } });
    await t.app.inject({ method: "GET", url: "/api/usos/profile", headers: { cookie } });
    expect(calls).toBe(1);
    await t.app.close();
    t.db.close();

    t = makeApp({ CACHE_TTL_SECONDS: "0" });
    const cookie2 = await loginDev(t.app);
    calls = 0;
    await t.app.inject({ method: "GET", url: "/api/usos/profile", headers: { cookie: cookie2 } });
    await t.app.inject({ method: "GET", url: "/api/usos/profile", headers: { cookie: cookie2 } });
    expect(calls).toBe(2);
  });
});
