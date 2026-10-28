/**
 * USOS adapter, mode "api": OAuth 1.0a USOS API (services/...).
 *
 * Method names and fields follow the public USOS API reference. Nothing here was run against the Vistula
 * installation (no network from the build machine) - see the TODO(verify) markers, esp. for payments,
 * USOSmail (mailclient), news and the grades/terms2 nesting.
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
  type TimetableEntry,
  type UsosAdapter,
  type UsosCredentials
} from "@vusos/shared";
import { usosFetch, UsosHttpError, type UsosApiAuth, type UsosParams } from "./client.js";
import type { UsosAdapterDeps, UsosConfig } from "./config.js";
import {
  collectLecturerIds,
  extractCourseEditions,
  isRecord,
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
  toLangText
} from "./mappers.js";
import { addDays, diffDays, toDateOnly, warsawDateOf } from "./time.js";

export const PROFILE_FIELDS =
  "id|first_name|last_name|email|student_number|photo_urls[200x200]|" +
  "student_programmes[id|programme[id|description|level_of_studies|mode_of_studies]|status]"; // TODO(verify): `stage`/`status` subfields
export const COURSE_FIELDS =
  "course_editions[course_id|course_name|term_id|user_groups[course_unit_id|class_type|group_number|lecturers[first_name|last_name]]]";
export const TT_FIELDS =
  "type|start_time|end_time|name|url|unit_id|course_id|course_name|classtype_name|group_number|room_number|building_name|lecturer_ids";
export const GRADE_FIELDS =
  "value_symbol|value_description|passes|counts_into_average|exam_session_number|date_modified|modification_author|comment"; // TODO(verify): does terms2 accept `fields`?
export const PAYMENT_FIELDS =
  "id|type|description|total_amount|saldo_amount|state|payment_deadline|interest|account_number"; // TODO(verify): names, esp. total_amount/saldo_amount
export const TERM_FIELDS = "id|name|start_date|end_date|finish_date|is_active";
// TODO(verify): the whole USOSmail ("mailclient") module - method names, params and fields are a best guess.
export const MAIL_LIST_FIELDS = "id|subject|sender|recipients|date|is_read|preview";
export const MAIL_MESSAGE_FIELDS = "id|subject|sender|recipients|date|is_read|body|attachments[name|url|size]";
// TODO(verify): news module name (news/search vs news/articles) and fields.
export const NEWS_FIELDS = "id|title|content|publication_date|author|category|url";

const MAX_TIMETABLE_DAYS = 93;
const TT_PARALLEL = 4;

function asList(raw: unknown, ...keys: string[]): unknown[] {
  if (Array.isArray(raw)) return raw;
  if (isRecord(raw)) {
    for (const k of keys) {
      const v = raw[k];
      if (Array.isArray(v)) return v;
    }
  }
  return [];
}

export function createUsosApiAdapter(
  creds: Extract<UsosCredentials, { mode: "api" }>,
  config: UsosConfig,
  deps: UsosAdapterDeps = {}
): UsosAdapter {
  const auth: UsosApiAuth = {
    baseUrl: creds.baseUrl || config.apiBaseUrl,
    consumerKey: config.consumerKey,
    consumerSecret: config.consumerSecret,
    accessToken: creds.accessToken,
    accessTokenSecret: creds.accessTokenSecret
  };
  const now = deps.now ?? (() => new Date());
  const call = <T = unknown>(path: string, params: UsosParams, method: "GET" | "POST" = "GET"): Promise<T> =>
    usosFetch<T>(path, params, auth, { method, fetchImpl: deps.fetchImpl });
  const today = (): string => warsawDateOf(now());

  // courses/user is needed by several methods (names, class types, term ids): memoise per adapter instance.
  const coursesCache = new Map<boolean, Promise<unknown>>();
  function coursesUser(activeOnly: boolean): Promise<unknown> {
    let p = coursesCache.get(activeOnly);
    if (!p) {
      p = call("services/courses/user", { fields: COURSE_FIELDS, active_terms_only: activeOnly });
      coursesCache.set(activeOnly, p);
      p.catch(() => coursesCache.delete(activeOnly)); // do not cache failures
    }
    return p;
  }

  const lecturerCache = new Map<string, string>();
  async function resolveLecturers(ids: string[]): Promise<Map<string, string>> {
    const missing = ids.filter((id) => !lecturerCache.has(id));
    for (let i = 0; i < missing.length; i += 50) {
      const chunk = missing.slice(i, i + 50);
      try {
        const res = await call<unknown>("services/users/users", { user_ids: chunk, fields: "id|first_name|last_name" });
        if (isRecord(res)) {
          for (const [id, u] of Object.entries(res)) {
            if (!isRecord(u)) continue;
            const name = [u.first_name, u.last_name].filter((s): s is string => typeof s === "string" && s !== "").join(" ");
            if (name) lecturerCache.set(id, name);
          }
        }
      } catch (err) {
        if (!(err instanceof AdapterUnavailableError)) throw err; // auth errors still propagate
        // lecturers are decoration - timetable is still useful without names
      }
    }
    return lecturerCache;
  }

  async function mail<T>(path: string, params: UsosParams, method: "GET" | "POST" = "GET"): Promise<T> {
    try {
      return await call<T>(path, params, method);
    } catch (err) {
      if (err instanceof UsosHttpError && err.isMethodNotFound) throw new AdapterUnavailableError("USOSmail API niedostępne");
      throw err;
    }
  }

  async function optional<T>(fn: () => Promise<T>, fallback: T): Promise<T> {
    try {
      return await fn();
    } catch (err) {
      // 400/404 = method or field unknown on this installation -> degrade gracefully.
      if (err instanceof UsosHttpError && (err.status === 400 || err.status === 404)) return fallback;
      throw err;
    }
  }

  return {
    async getProfile(): Promise<StudentProfile> {
      return mapProfile(await call("services/users/user", { fields: PROFILE_FIELDS }));
    },

    async getTerms(): Promise<Term[]> {
      const t = today();
      // Primary: terms attached to the user's course editions (all terms the student has ever had classes in).
      const cu = await coursesUser(false);
      const fromCourses = isRecord(cu) ? mapTerms(cu.terms, t) : [];
      if (fromCourses.length > 0) return fromCourses;
      // Fallback: search by year. TODO(verify): terms/search arguments (query, fields) and result shape.
      const year = Number(t.slice(0, 4));
      const found: Term[] = [];
      for (const q of [String(year - 1), String(year)]) {
        const res = await optional(() => call("services/terms/search", { query: q, fields: TERM_FIELDS }), []);
        found.push(...mapTerms(asList(res, "terms", "items"), t));
      }
      return [...new Map(found.map((x) => [x.id, x])).values()].sort((a, b) => b.startDate.localeCompare(a.startDate));
    },

    async getCourses(termId?: string): Promise<CourseUnit[]> {
      return mapCourseUnits(await coursesUser(termId ? false : true), termId);
    },

    async getTimetable(from: string, to: string): Promise<TimetableEntry[]> {
      const start = toDateOnly(from);
      const end0 = toDateOnly(to);
      if (!start || !end0 || end0 < start) return [];
      const end = diffDays(start, end0) >= MAX_TIMETABLE_DAYS ? addDays(start, MAX_TIMETABLE_DAYS - 1) : end0;

      const chunks: { start: string; days: number }[] = [];
      for (let s = start; s <= end; s = addDays(s, 7)) chunks.push({ start: s, days: Math.min(7, diffDays(s, end) + 1) });

      const rawLists: unknown[][] = [];
      for (let i = 0; i < chunks.length; i += TT_PARALLEL) {
        const batch = chunks.slice(i, i + TT_PARALLEL);
        const res = await Promise.all(
          batch.map((c) => call<unknown>("services/tt/user", { start: c.start, days: c.days, fields: TT_FIELDS }))
        );
        for (const r of res) rawLists.push(asList(r, "activities"));
      }
      const all = rawLists.flat();
      const names = await resolveLecturers(collectLecturerIds(all));
      const entries = mapActivities(all, names);
      return [...new Map(entries.map((e) => [e.id, e])).values()].sort((a, b) => a.start.localeCompare(b.start));
    },

    async getGrades(termIds?: string[]): Promise<Grade[]> {
      const courses = await coursesUser(false);
      const courseNames = new Map<string, LangText>();
      const unitClassTypes = new Map<string, string>();
      const knownTerms = new Set<string>();
      for (const ed of extractCourseEditions(courses)) {
        courseNames.set(ed.course_id, toLangText(ed.course_name, ed.course_id));
        if (ed.term_id) knownTerms.add(ed.term_id);
        for (const g of ed.user_groups ?? []) unitClassTypes.set(g.course_unit_id, toLangText(g.class_type).pl);
      }
      const ids = termIds && termIds.length > 0 ? termIds : [...knownTerms];
      if (ids.length === 0) return [];

      let raw: unknown;
      try {
        raw = await call("services/grades/terms2", { term_ids: ids, fields: GRADE_FIELDS });
      } catch (err) {
        // TODO(verify): if terms2 rejects `fields` (HTTP 400) retry with the defaults.
        if (err instanceof UsosHttpError && err.status === 400 && !err.isMethodNotFound) {
          raw = await call("services/grades/terms2", { term_ids: ids });
        } else throw err;
      }
      return mapGrades(raw, { courseNames, unitClassTypes });
    },

    async getPayments(): Promise<Payment[]> {
      const raw = await call("services/payments/user_payments", { fields: PAYMENT_FIELDS });
      return mapPayments(asList(raw, "payments", "items"), today());
    },

    async listMessages(opts): Promise<MessageThread[]> {
      const raw = await mail("services/mailclient/messages", {
        folder: "inbox",
        limit: opts?.limit ?? 30,
        offset: opts?.offset ?? 0,
        fields: MAIL_LIST_FIELDS
      });
      return asList(raw, "messages", "items")
        .map((m) => mapMessageThread(m))
        .filter((m): m is MessageThread => m !== null);
    },

    async getMessage(id: string): Promise<Message> {
      return mapMessage(await mail("services/mailclient/message", { message_id: id, fields: MAIL_MESSAGE_FIELDS }));
    },

    async sendMessage(to: string[], subject: string, body: string): Promise<{ id: string }> {
      // TODO(verify): recipients may need to be USOS user ids rather than e-mail addresses.
      const res = await mail<unknown>("services/mailclient/send", { recipients: to, subject, body }, "POST");
      const id = isRecord(res) ? (res.id ?? res.message_id) : undefined;
      return { id: id === undefined || id === null ? "" : String(id) };
    },

    async getDeanOfficeContacts(): Promise<Contact[]> {
      // TODO(verify): no dedicated "dean's office" method exists in USOS API; we expose the faculty card
      // (phone + address) of the user's programme(s). Returns [] when anything is missing.
      return optional(async () => {
        const user = await call<unknown>("services/users/user", {
          fields: "student_programmes[programme[faculty[id|name]]]"
        });
        const facIds = new Set<string>();
        const progs = isRecord(user) ? asList(user.student_programmes) : [];
        for (const p of progs) {
          const prog = isRecord(p) ? p.programme : undefined;
          const fac = isRecord(prog) ? prog.faculty : undefined;
          if (isRecord(fac) && (typeof fac.id === "string" || typeof fac.id === "number")) facIds.add(String(fac.id));
        }
        const contacts: Contact[] = [];
        for (const facId of facIds) {
          const raw = await call("services/fac/faculty", {
            fac_id: facId,
            fields: "id|name|postal_address|phone_numbers|homepage_url"
          });
          contacts.push(...mapFacultyContacts(raw));
        }
        return contacts;
      }, []);
    },

    async getAnnouncements(limit = 20): Promise<Announcement[]> {
      return optional(async () => {
        const raw = await call("services/news/search", { fields: NEWS_FIELDS, limit });
        return mapNews(asList(raw, "articles", "items", "news")).slice(0, limit);
      }, []);
    }
  };
}
