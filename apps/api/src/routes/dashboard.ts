import type { FastifyInstance, FastifyRequest } from "fastify";
import type { Announcement, DashboardData, Grade, LmsEvent, Payment, TimetableEntry } from "@vusos/shared";
import type { AdapterRegistry } from "../adapters/registry.js";
import { currentUser } from "../auth/session.js";
import { HttpError } from "../errors.js";
import { addDays, warsawDate, warsawDateOf } from "../util/time.js";

const settledValue = <T>(r: PromiseSettledResult<T>, fallback: T, log: (err: unknown) => void): T => {
  if (r.status === "fulfilled") return r.value;
  // "Not connected" is an expected state, not worth a log line.
  if (!(r.reason instanceof HttpError && r.reason.statusCode === 409)) log(r.reason);
  return fallback;
};

const byDateDesc = (a: string | null, b: string | null): number => {
  if (a === b) return 0;
  if (!a) return 1;
  if (!b) return -1;
  return b.localeCompare(a);
};

export async function dashboardRoutes(app: FastifyInstance, registry: AdapterRegistry): Promise<void> {
  app.get("/dashboard", async (req: FastifyRequest): Promise<DashboardData> => {
    const userId = currentUser(req).id;
    const today = warsawDate();
    const log = (what: string) => (err: unknown) => req.log.warn({ err, source: what }, "dashboard source failed");

    const [profile, timetable, events, grades, payments, messages, usosNews, platonNews] = await Promise.allSettled([
      registry.usos(userId, (a) => a.getProfile(), { cacheKey: "profile" }),
      registry.usos(userId, (a) => a.getTimetable(today, addDays(today, 1)), { cacheKey: `timetable:${today}:${addDays(today, 1)}` }),
      registry.platon(userId, (a) => a.getUpcomingEvents(7), { cacheKey: "events:7" }),
      registry.usos(userId, (a) => a.getGrades(), { cacheKey: "grades:" }),
      registry.usos(userId, (a) => a.getPayments(), { cacheKey: "payments" }),
      registry.usos(userId, (a) => a.listMessages({ limit: 100 }), { cacheKey: "messages:100:" }),
      registry.usos(userId, (a) => a.getAnnouncements(5), { cacheKey: "announcements:5" }),
      registry.platon(userId, (a) => a.getAnnouncements(5), { cacheKey: "announcements:5" })
    ]);

    const todayClasses = settledValue<TimetableEntry[]>(timetable, [], log("usos.timetable"))
      .filter((e) => warsawDateOf(e.start) === today)
      .sort((a, b) => a.start.localeCompare(b.start));

    const upcomingDeadlines = settledValue<LmsEvent[]>(events, [], log("platon.events"))
      .slice()
      .sort((a, b) => a.start.localeCompare(b.start))
      .slice(0, 10);

    const recentGrades = settledValue<Grade[]>(grades, [], log("usos.grades"))
      .slice()
      .sort((a, b) => byDateDesc(a.dateModified, b.dateModified))
      .slice(0, 5);

    const paymentsDue = settledValue<Payment[]>(payments, [], log("usos.payments")).filter(
      (p) => p.state === "unpaid" || p.state === "partially_paid" || p.state === "overdue"
    );

    const unreadMessages = settledValue(messages, [], log("usos.messages")).filter((m) => m.unread).length;

    const announcements = [
      ...settledValue<Announcement[]>(usosNews, [], log("usos.announcements")),
      ...settledValue<Announcement[]>(platonNews, [], log("platon.announcements"))
    ]
      .sort((a, b) => byDateDesc(a.publishedAt, b.publishedAt))
      .slice(0, 5);

    // Computed after the calls: an auth failure above may have flipped a connection to "expired".
    const info = registry.statuses(userId);

    return {
      profile: settledValue(profile, null, log("usos.profile")),
      todayClasses,
      upcomingDeadlines,
      recentGrades,
      paymentsDue,
      unreadMessages,
      announcements,
      connections: { usos: info.usos.status, platon: info.platon.status }
    };
  });
}
