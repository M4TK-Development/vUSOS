/**
 * Pure mappers: raw USOS API JSON -> @vusos/shared domain types.
 * All input is `unknown` and validated leniently with the schemas in ./raw.ts; entries that do not parse are
 * skipped (lists) or reported as AdapterUnavailableError (single-object responses).
 *
 * TODO(verify): every raw field name below is based on the public USOS API docs and has not been checked
 * against the Vistula installation. Search for "TODO(verify)" in this folder for the full list.
 */
import {
  AdapterUnavailableError,
  type Announcement,
  type Contact,
  type CourseUnit,
  type Grade,
  type LangText,
  type Message,
  type MessageThread,
  type Payment,
  type StudentProfile,
  type Term,
  type TimetableEntry
} from "@vusos/shared";
import {
  RawActivity,
  RawCourseEdition,
  RawCoursesUser,
  RawFaculty,
  RawGrade,
  RawMailMessage,
  RawNewsArticle,
  RawPayment,
  RawTerm,
  RawUser
} from "./raw.js";
import { toDateOnly, warsawLocalToIso } from "./time.js";

// ---------- generic helpers ----------

export function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** "text" | {pl,en} | null -> LangText. */
export function toLangText(v: unknown, fallback = ""): LangText {
  if (typeof v === "string") return { pl: v };
  if (isRecord(v)) {
    const pl = typeof v.pl === "string" && v.pl !== "" ? v.pl : undefined;
    const en = typeof v.en === "string" && v.en !== "" ? v.en : undefined;
    const out: LangText = { pl: pl ?? en ?? fallback };
    if (en !== undefined) out.en = en;
    return out;
  }
  return { pl: fallback };
}

export function toNumber(v: unknown): number | null {
  if (typeof v === "number") return Number.isFinite(v) ? v : null;
  if (typeof v === "string") {
    const n = Number(v.trim().replace(/\s/g, "").replace(",", "."));
    return Number.isFinite(n) && v.trim() !== "" ? n : null;
  }
  return null;
}

function fullName(first: string | null | undefined, last: string | null | undefined): string {
  return [first, last].filter((s): s is string => !!s).join(" ");
}

/** Person given as string or {first_name,last_name}. */
export function personName(v: unknown): string | null {
  if (typeof v === "string") return v.trim() || null;
  if (isRecord(v)) {
    const n = fullName(
      typeof v.first_name === "string" ? v.first_name : null,
      typeof v.last_name === "string" ? v.last_name : null
    );
    return n || null;
  }
  return null;
}

/** USOS timestamp ("YYYY-MM-DD HH:MM:SS", Warsaw) or date -> ISO with offset; unparseable -> null. */
export function usosDateTimeToIso(v: string | null | undefined): string | null {
  return v ? warsawLocalToIso(v) : null;
}

function validUrl(v: string | null | undefined): string | null {
  if (!v) return null;
  return URL.canParse(v) ? v : null;
}

function validEmail(v: string | null | undefined): string | null {
  return v && /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v) ? v : null;
}

/** Entries of a dict, or of a list (keys "1", "2", ...), else []. */
function entriesOf(v: unknown): [string, unknown][] {
  if (Array.isArray(v)) return v.map((x, i) => [String(i + 1), x] as [string, unknown]);
  if (isRecord(v)) return Object.entries(v);
  return [];
}

function parseList<T>(schema: { safeParse(v: unknown): { success: true; data: T } | { success: false } }, list: unknown): T[] {
  if (!Array.isArray(list)) return [];
  const out: T[] = [];
  for (const item of list) {
    const r = schema.safeParse(item);
    if (r.success) out.push(r.data);
  }
  return out;
}

// ---------- profile ----------

export function mapProfile(raw: unknown): StudentProfile {
  const r = RawUser.safeParse(raw);
  if (!r.success) throw new AdapterUnavailableError("USOS: nieoczekiwany format odpowiedzi users/user");
  const u = r.data;
  const photo = u.photo_urls ? (u.photo_urls["200x200"] ?? Object.values(u.photo_urls).find((x) => !!x) ?? null) : null;
  return {
    id: u.id,
    firstName: u.first_name ?? "",
    lastName: u.last_name ?? "",
    email: validEmail(u.email),
    studentNumber: u.student_number === null || u.student_number === undefined ? null : String(u.student_number),
    photoUrl: validUrl(photo),
    programmes: (u.student_programmes ?? []).map((sp) => {
      const levels = [toLangText(sp.programme?.level_of_studies).pl, toLangText(sp.programme?.mode_of_studies).pl].filter(
        (s) => s !== ""
      );
      return {
        id: sp.id,
        name: toLangText(sp.programme?.description, sp.programme?.id ?? sp.id),
        status: sp.status ?? null,
        // TODO(verify): there may be no `stage` on student_programme; we fall back to level/mode of the programme.
        stage: sp.stage ?? (levels.length > 0 ? levels.join(", ") : null)
      };
    })
  };
}

// ---------- terms ----------

export function mapTerm(raw: unknown, today: string): Term | null {
  const r = RawTerm.safeParse(raw);
  if (!r.success) return null;
  const t = r.data;
  const start = toDateOnly(t.start_date);
  const end = toDateOnly(t.end_date);
  if (!start || !end) return null;
  // `finish_date` is the last day of the term including exam sessions; prefer it for "is the term running".
  const finish = (t.finish_date ? toDateOnly(t.finish_date) : null) ?? end;
  return {
    id: t.id,
    name: toLangText(t.name, t.id),
    startDate: start,
    endDate: end,
    isCurrent: start <= today && today <= finish
  };
}

export function mapTerms(raw: unknown, today: string): Term[] {
  if (!Array.isArray(raw)) return [];
  const terms = raw.map((x) => mapTerm(x, today)).filter((t): t is Term => t !== null);
  return terms.sort((a, b) => b.startDate.localeCompare(a.startDate));
}

// ---------- courses ----------

/** Flattens courses/user `course_editions` (dict term_id -> list, or flat list) into typed editions. */
export function extractCourseEditions(raw: unknown, termId?: string): RawCourseEdition[] {
  const r = RawCoursesUser.safeParse(raw);
  if (!r.success) throw new AdapterUnavailableError("USOS: nieoczekiwany format odpowiedzi courses/user");
  const ce = r.data.course_editions;
  const out: RawCourseEdition[] = [];
  const push = (item: unknown, keyTerm?: string): void => {
    const e = RawCourseEdition.safeParse(item);
    if (!e.success) return;
    const data = e.data;
    out.push({ ...data, term_id: data.term_id ?? keyTerm ?? null });
  };
  if (Array.isArray(ce)) ce.forEach((x) => push(x));
  else if (ce) for (const [key, list] of Object.entries(ce)) list.forEach((x) => push(x, key));
  return termId ? out.filter((e) => e.term_id === termId) : out;
}

export function mapCourseUnits(raw: unknown, termId?: string): CourseUnit[] {
  const units: CourseUnit[] = [];
  for (const ed of extractCourseEditions(raw, termId)) {
    const term = ed.term_id ?? "";
    const courseName = toLangText(ed.course_name, ed.course_id);
    const groups = ed.user_groups ?? [];
    if (groups.length === 0) {
      units.push({
        id: `${ed.course_id}@${term}`,
        courseId: ed.course_id,
        courseName,
        classType: "",
        termId: term,
        groupNumber: null,
        lecturers: [],
        ects: null,
        platonCourseId: null
      });
      continue;
    }
    for (const g of groups) {
      const gn = toNumber(g.group_number);
      units.push({
        id: g.course_unit_id,
        courseId: ed.course_id,
        courseName,
        classType: toLangText(g.class_type).pl,
        termId: term,
        groupNumber: gn === null ? null : Math.trunc(gn),
        lecturers: (g.lecturers ?? []).map((l) => fullName(l.first_name, l.last_name)).filter((s) => s !== ""),
        // TODO(verify): ECTS is not part of courses/user; would need courses/course_edition2 (credit_value?).
        ects: null,
        platonCourseId: null
      });
    }
  }
  return units;
}

// ---------- timetable ----------

export function collectLecturerIds(rawList: unknown): string[] {
  const ids = new Set<string>();
  for (const a of parseList(RawActivity, rawList)) for (const id of a.lecturer_ids ?? []) ids.add(id);
  return [...ids];
}

/** Maps tt/user activities. `lecturerNames` maps user id -> "First Last" (resolved via users/users). */
export function mapActivities(rawList: unknown, lecturerNames: ReadonlyMap<string, string> = new Map()): TimetableEntry[] {
  const out: TimetableEntry[] = [];
  for (const a of parseList(RawActivity, rawList)) {
    const start = usosDateTimeToIso(a.start_time);
    const end = usosDateTimeToIso(a.end_time);
    if (!start || !end) continue;
    const name = toLangText(a.name);
    const courseName = a.course_name !== undefined && a.course_name !== null ? toLangText(a.course_name, name.pl) : name;
    const classType = toLangText(a.classtype_name).pl;
    const gn = toNumber(a.group_number);
    const building = toLangText(a.building_name).pl;
    const key = a.unit_id ?? a.course_id ?? name.pl.toLowerCase().replace(/[^a-z0-9]+/g, "-");
    out.push({
      id: `usos-tt:${key}:${a.start_time.replace(/[^0-9]/g, "")}`,
      source: "usos",
      start,
      end,
      courseId: a.course_id ?? null,
      courseName,
      classType: classType || null,
      groupNumber: gn === null ? null : Math.trunc(gn),
      room: a.room_number || null,
      building: building || null,
      lecturers: (a.lecturer_ids ?? []).map((id) => lecturerNames.get(id)).filter((s): s is string => !!s),
      url: a.url || null
    });
  }
  return out;
}

// ---------- grades ----------

export interface GradeMapContext {
  /** course_id -> name */
  courseNames?: ReadonlyMap<string, LangText>;
  /** course_unit_id -> class type (e.g. "Wykład") */
  unitClassTypes?: ReadonlyMap<string, string>;
}

function parseCountsIntoAverage(v: boolean | string | null | undefined): boolean {
  if (typeof v === "boolean") return v;
  if (typeof v === "string") return /^(y|yes|t|true|1|tak)$/i.test(v.trim());
  return false;
}

/**
 * Flattens grades/terms2:
 *   { term_id: { course_id: { course_grades: {session: grade}, course_units_grades: {unit_id: {session: grade}} } } }
 * TODO(verify): exact nesting (dict vs. list per session) on the live installation; both shapes are tolerated.
 */
export function mapGrades(raw: unknown, ctx: GradeMapContext = {}): Grade[] {
  const out: Grade[] = [];
  if (!isRecord(raw)) return out;

  const addGrade = (
    rawGrade: unknown,
    termId: string,
    courseId: string,
    unitId: string | null,
    sessionKey: string
  ): void => {
    const r = RawGrade.safeParse(rawGrade);
    if (!r.success || !r.data.value_symbol) return;
    const g = r.data;
    const sessionNum = toNumber(g.exam_session_number) ?? toNumber(sessionKey);
    const desc = g.value_description === undefined || g.value_description === null ? null : toLangText(g.value_description);
    out.push({
      id: `usos-grade:${termId}:${courseId}:${unitId ?? "course"}:${sessionNum ?? sessionKey}`,
      termId,
      courseId,
      courseName: ctx.courseNames?.get(courseId) ?? { pl: courseId },
      classType: unitId ? (ctx.unitClassTypes?.get(unitId) ?? null) : null,
      examSession: sessionNum === null ? null : Math.trunc(sessionNum),
      valueSymbol: g.value_symbol,
      valueDescription: desc && desc.pl !== "" ? desc : null,
      passes: g.passes ?? null,
      countsIntoAverage: parseCountsIntoAverage(g.counts_into_average),
      dateModified: usosDateTimeToIso(g.date_modified),
      gradedBy: personName(g.modification_author),
      comment: g.comment || null
    });
  };

  for (const [termId, courses] of Object.entries(raw)) {
    if (!isRecord(courses)) continue;
    for (const [courseId, cg] of Object.entries(courses)) {
      if (!isRecord(cg)) continue;
      for (const [session, g] of entriesOf(cg.course_grades)) addGrade(g, termId, courseId, null, session);
      for (const [unitId, sessions] of entriesOf(cg.course_units_grades)) {
        if (isRecord(sessions) && "value_symbol" in sessions) addGrade(sessions, termId, courseId, unitId, "1");
        else for (const [session, g] of entriesOf(sessions)) addGrade(g, termId, courseId, unitId, session);
      }
    }
  }
  return out;
}

/** "4,5" / "4.5" / "5" -> number; "zal", "nzal", "5!" etc. -> null. */
export function numericGrade(symbol: string): number | null {
  const m = /^\s*([2-5](?:[.,]\d)?)\s*$/.exec(symbol);
  return m?.[1] ? Number(m[1].replace(",", ".")) : null;
}

/**
 * Plain (unweighted - ECTS weights are unknown) mean of numeric grades that count into the average.
 * For a course/class type graded in several sessions only the latest session counts.
 */
export function computeAverage(grades: readonly Grade[]): number | null {
  const latest = new Map<string, Grade>();
  for (const g of grades) {
    if (!g.countsIntoAverage || numericGrade(g.valueSymbol) === null) continue;
    const key = `${g.termId}|${g.courseId}|${g.classType ?? ""}`;
    const prev = latest.get(key);
    if (!prev || (g.examSession ?? 0) >= (prev.examSession ?? 0)) latest.set(key, g);
  }
  const values = [...latest.values()].map((g) => numericGrade(g.valueSymbol)).filter((n): n is number => n !== null);
  if (values.length === 0) return null;
  return Math.round((values.reduce((a, b) => a + b, 0) / values.length) * 100) / 100;
}

// ---------- payments ----------

/**
 * TODO(verify): meaning of total_amount / saldo_amount and the possible `state` values.
 * Assumption: total_amount = amount charged; saldo_amount = balance (negative = still owed, 0 = settled).
 */
export function mapPayments(rawList: unknown, today: string): Payment[] {
  const out: Payment[] = [];
  for (const p of parseList(RawPayment, rawList)) {
    const total = toNumber(p.total_amount) ?? 0;
    const saldo = toNumber(p.saldo_amount);
    const owed = saldo === null ? total : Math.max(0, -saldo);
    let paid = Math.max(0, Math.round((total - owed) * 100) / 100);
    const due = p.payment_deadline ? toDateOnly(p.payment_deadline) : null;
    const rawState = (p.state ?? "").toLowerCase();
    let state: Payment["state"];
    if (/cancel|anul|storn|umorz/.test(rawState)) state = "cancelled";
    else if (owed <= 0 && total > 0) state = "paid";
    else if (/^paid$|zap[łl]acon/.test(rawState) && saldo === null) state = "paid";
    else if (owed > 0 && due !== null && due < today) state = "overdue";
    else if (owed > 0 && paid > 0) state = "partially_paid";
    else state = "unpaid";
    if (state === "paid") paid = total;
    out.push({
      id: p.id,
      type: p.type ?? "other",
      description: toLangText(p.description, p.type ?? p.id),
      amount: total,
      paid,
      currency: p.currency ?? "PLN", // TODO(verify): currency field; Vistula tuition is billed in PLN
      dueDate: due,
      state,
      interest: toNumber(p.interest),
      accountNumber: p.account_number ?? null
    });
  }
  return out;
}

// ---------- messages (USOSmail) ----------

function recipientNames(list: readonly unknown[] | null | undefined): string[] {
  return (list ?? []).map((x) => personName(x)).filter((s): s is string => !!s);
}

function stripHtml(html: string): string {
  return html
    .replace(/<[^>]*>/g, " ")
    .replace(/&nbsp;/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

/** TODO(verify): USOSmail raw shape (subject/sender/recipients/date/is_read/preview/body/attachments). */
export function mapMessageThread(raw: unknown): MessageThread | null {
  const r = RawMailMessage.safeParse(raw);
  if (!r.success) return null;
  const m = r.data;
  const preview = m.preview ?? (m.body ? stripHtml(m.body).slice(0, 160) : "");
  return {
    id: m.id,
    source: "usos",
    subject: m.subject ?? "(bez tematu)",
    from: personName(m.sender) ?? "",
    to: recipientNames(m.recipients),
    date: usosDateTimeToIso(m.date) ?? m.date ?? "",
    preview,
    unread: m.is_read === undefined || m.is_read === null ? false : !m.is_read,
    url: null
  };
}

export function mapMessage(raw: unknown): Message {
  const thread = mapMessageThread(raw);
  const r = RawMailMessage.safeParse(raw);
  if (!thread || !r.success) throw new AdapterUnavailableError("USOSmail: nieoczekiwany format wiadomości");
  return {
    ...thread,
    bodyHtml: r.data.body ?? "",
    attachments: (r.data.attachments ?? []).map((a) => ({ name: a.name, url: a.url, size: toNumber(a.size) }))
  };
}

// ---------- dean's office ----------

/**
 * TODO(verify): fac/faculty exposes `phone_numbers` and `postal_address` but (to our knowledge) no e-mail.
 * One Contact per faculty; postal address is stored in `room` (the domain type has no address field).
 */
export function mapFacultyContacts(raw: unknown): Contact[] {
  const r = RawFaculty.safeParse(raw);
  if (!r.success) return [];
  const f = r.data;
  return [
    {
      id: `usos-fac:${f.id}`,
      name: toLangText(f.name, f.id).pl,
      role: "Dziekanat",
      email: null,
      phone: f.phone_numbers?.[0] ?? null,
      room: f.postal_address ?? null,
      hours: null
    }
  ];
}

// ---------- news ----------

/** TODO(verify): news method + fields (title/content/publication_date/author/category/url). */
export function mapNews(rawList: unknown): Announcement[] {
  const out: Announcement[] = [];
  for (const a of parseList(RawNewsArticle, rawList)) {
    out.push({
      id: `usos-news:${a.id}`,
      source: "usos",
      title: toLangText(a.title).pl,
      bodyHtml: toLangText(a.content).pl,
      publishedAt: usosDateTimeToIso(a.publication_date) ?? a.publication_date ?? "",
      author: personName(a.author),
      category: toLangText(a.category).pl || null,
      url: a.url || null
    });
  }
  return out.sort((x, y) => y.publishedAt.localeCompare(x.publishedAt));
}
