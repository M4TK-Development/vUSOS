import type { FastifyInstance } from "fastify";
import type { Payment, PaymentsSummary } from "@vusos/shared";
import { z } from "zod";
import type { AdapterRegistry } from "../adapters/registry.js";
import { currentUser } from "../auth/session.js";

const DATE = /^\d{4}-\d{2}-\d{2}(?:[T ][0-9:.]+(?:Z|[+-]\d{2}:?\d{2})?)?$/;
const MAX_RANGE_DAYS = 120;

const TimetableQuery = z
  .object({ from: z.string().regex(DATE), to: z.string().regex(DATE) })
  .superRefine((q, ctx) => {
    const f = Date.parse(q.from.slice(0, 10));
    const t = Date.parse(q.to.slice(0, 10));
    if (Number.isNaN(f) || Number.isNaN(t)) ctx.addIssue({ code: "custom", message: "Niepoprawna data" });
    else if (t < f) ctx.addIssue({ code: "custom", message: "`to` nie może być wcześniej niż `from`" });
    else if ((t - f) / 86_400_000 > MAX_RANGE_DAYS) ctx.addIssue({ code: "custom", message: `Maksymalny zakres to ${MAX_RANGE_DAYS} dni` });
  });
const TermQuery = z.object({ term: z.string().min(1).max(100).optional() });
const PagingQuery = z.object({
  limit: z.coerce.number().int().min(1).max(100).optional(),
  offset: z.coerce.number().int().min(0).max(10_000).optional()
});
const LimitQuery = z.object({ limit: z.coerce.number().int().min(1).max(50).optional() });
const IdParams = z.object({ id: z.string().min(1).max(300) });
const SendBody = z.object({
  to: z.array(z.string().min(1).max(200)).min(1).max(50),
  subject: z.string().trim().min(1).max(300),
  body: z.string().trim().min(1).max(20_000)
});

export function summarizePayments(payments: Payment[]): PaymentsSummary {
  const open = (p: Payment) => p.state === "unpaid" || p.state === "partially_paid" || p.state === "overdue";
  const remaining = (p: Payment) => Math.max(0, p.amount - p.paid);
  const round = (n: number) => Math.round(n * 100) / 100;
  return {
    payments,
    totalDue: round(payments.filter(open).reduce((s, p) => s + remaining(p), 0)),
    totalOverdue: round(payments.filter((p) => p.state === "overdue").reduce((s, p) => s + remaining(p), 0)),
    currency: payments[0]?.currency ?? "PLN"
  };
}

export async function usosRoutes(app: FastifyInstance, registry: AdapterRegistry): Promise<void> {
  const uid = (req: Parameters<typeof currentUser>[0]) => currentUser(req).id;

  app.get("/usos/profile", async (req) => registry.usos(uid(req), (a) => a.getProfile(), { cacheKey: "profile" }));

  app.get("/usos/terms", async (req) => registry.usos(uid(req), (a) => a.getTerms(), { cacheKey: "terms" }));

  app.get("/usos/courses", async (req) => {
    const { term } = TermQuery.parse(req.query);
    return registry.usos(uid(req), (a) => a.getCourses(term), { cacheKey: `courses:${term ?? ""}` });
  });

  app.get("/usos/timetable", async (req) => {
    const { from, to } = TimetableQuery.parse(req.query);
    return registry.usos(uid(req), (a) => a.getTimetable(from, to), { cacheKey: `timetable:${from}:${to}` });
  });

  app.get("/usos/grades", async (req) => {
    const { term } = TermQuery.parse(req.query);
    const termIds = term ? term.split(",").map((t) => t.trim()).filter(Boolean) : undefined;
    return registry.usos(uid(req), (a) => a.getGrades(termIds), { cacheKey: `grades:${term ?? ""}` });
  });

  app.get("/usos/payments", async (req) => {
    const payments = await registry.usos(uid(req), (a) => a.getPayments(), { cacheKey: "payments" });
    return summarizePayments(payments);
  });

  app.get("/usos/messages", async (req) => {
    const { limit, offset } = PagingQuery.parse(req.query);
    return registry.usos(uid(req), (a) => a.listMessages({ limit, offset }), { cacheKey: `messages:${limit ?? ""}:${offset ?? ""}` });
  });

  app.get("/usos/messages/:id", async (req) => {
    const { id } = IdParams.parse(req.params);
    return registry.usos(uid(req), (a) => a.getMessage(id), { cacheKey: `message:${id}` });
  });

  app.post("/usos/messages", async (req, reply) => {
    const body = SendBody.parse(req.body);
    const userId = uid(req);
    const res = await registry.usos(userId, (a) => a.sendMessage(body.to, body.subject, body.body), { cacheKey: null });
    registry.invalidate(userId, "usos");
    return reply.code(201).send(res);
  });

  app.get("/usos/contacts", async (req) => registry.usos(uid(req), (a) => a.getDeanOfficeContacts(), { cacheKey: "contacts" }));

  app.get("/usos/announcements", async (req) => {
    const { limit } = LimitQuery.parse(req.query);
    return registry.usos(uid(req), (a) => a.getAnnouncements(limit), { cacheKey: `announcements:${limit ?? ""}` });
  });
}
