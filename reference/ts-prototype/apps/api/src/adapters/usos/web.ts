/**
 * USOS adapter, mode "web": works on USOSweb session cookies (Cookie header string) and scrapes
 * kontroler.php pages with cheerio (see ./web-parsers.ts).
 *
 * UNVERIFIED: selectors and query parameters were written without access to the live Vistula USOSweb and
 * are tested only against hand-written fixtures in ./fixtures. Adjust after comparing with real pages.
 * Limitations in this mode: no message sending, no dean's office contacts, courses are derived from the
 * timetable, terms from the grades page + the academic calendar.
 */
import {
  AdapterAuthError,
  AdapterUnavailableError,
  type Announcement,
  type Contact,
  type CourseUnit,
  type Grade,
  type Message,
  type MessageThread,
  type Payment,
  type StudentProfile,
  type Term,
  type TimetableEntry,
  type UsosAdapter,
  type UsosCredentials
} from "@vusos/shared";
import type { UsosAdapterDeps, UsosConfig } from "./config.js";
import { normalizeBaseUrl } from "./client.js";
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
  planWeekParams,
  termFromId,
  termIdForDate
} from "./web-parsers.js";
import { addDays, diffDays, mondayOf, toDateOnly, warsawDateOf } from "./time.js";

// TODO(verify): all _action values below.
export const ACTIONS = {
  home: "home/index",
  plan: "home/plan",
  grades: "dla_stud/studia/oceny/index",
  payments: "dla_stud/platnosci/index",
  messages: "dla_stud/studia/komunikacja"
} as const;

const MAX_REDIRECTS = 5;
const MAX_WEEKS = 14;

export function createUsosWebAdapter(
  creds: Extract<UsosCredentials, { mode: "web" }>,
  config: UsosConfig,
  deps: UsosAdapterDeps = {}
): UsosAdapter {
  const base = normalizeBaseUrl(creds.baseUrl || config.usoswebBaseUrl);
  const origin = new URL(base).origin;
  const doFetch = deps.fetchImpl ?? ((input, init) => fetch(input, init));
  const now = deps.now ?? (() => new Date());
  const today = (): string => warsawDateOf(now());
  const cookie = creds.cookies.replace(/[\r\n]+/g, " ").trim();

  async function getHtml(action: string, extra: Record<string, string> = {}): Promise<string> {
    const qs = new URLSearchParams({ _action: action, ...extra });
    let url = `${base}kontroler.php?${qs.toString()}`;
    for (let hop = 0; hop <= MAX_REDIRECTS; hop++) {
      let res: Response;
      try {
        res = await doFetch(url, {
          method: "GET",
          redirect: "manual",
          headers: {
            Cookie: cookie,
            Accept: "text/html,application/xhtml+xml",
            "Accept-Language": "pl,en;q=0.8",
            "User-Agent": "Mozilla/5.0 (compatible; vUSOS/0.1)"
          },
          signal: AbortSignal.timeout(20_000)
        });
      } catch (err) {
        throw new AdapterUnavailableError(`USOSweb niedostępny: ${err instanceof Error ? err.message : String(err)}`);
      }

      if (res.status >= 300 && res.status < 400) {
        const location = res.headers.get("location") ?? "";
        if (!location || isLoginRedirect(location)) throw new AdapterAuthError("Sesja USOSweb wygasła");
        const next = new URL(location, url);
        if (next.origin !== origin) throw new AdapterAuthError("USOSweb przekierował poza domenę (prawdopodobnie SSO)");
        url = next.toString();
        continue;
      }
      if (res.status === 401 || res.status === 403) throw new AdapterAuthError("Sesja USOSweb wygasła");
      if (!res.ok) throw new AdapterUnavailableError(`USOSweb: HTTP ${res.status} (${action})`);

      const html = await res.text();
      if (looksLikeLoginPage(html)) throw new AdapterAuthError("Sesja USOSweb wygasła");
      return html;
    }
    throw new AdapterUnavailableError("USOSweb: zbyt wiele przekierowań");
  }

  const parseOpts = { baseUrl: base };

  async function planWeeks(from: string, to: string): Promise<TimetableEntry[]> {
    if (to < from) return [];
    const weeks: string[] = [];
    for (let m = mondayOf(from); m <= to && weeks.length < MAX_WEEKS; m = addDays(m, 7)) weeks.push(m);
    const entries: TimetableEntry[] = [];
    for (const monday of weeks) {
      const html = await getHtml(ACTIONS.plan, planWeekParams(monday));
      entries.push(...parsePlanHtml(html, parseOpts));
    }
    const inRange = entries.filter((e) => e.start.slice(0, 10) >= from && e.start.slice(0, 10) <= to);
    return [...new Map(inRange.map((e) => [e.id, e])).values()].sort((a, b) => a.start.localeCompare(b.start));
  }

  return {
    async getProfile(): Promise<StudentProfile> {
      const profile = parseProfileHtml(await getHtml(ACTIONS.home));
      if (!profile) throw new AdapterUnavailableError("Nie udało się odczytać profilu z USOSweb (zmiana układu strony?)");
      return profile;
    },

    async getTerms(): Promise<Term[]> {
      const t = today();
      const { terms } = parseGradesHtml(await getHtml(ACTIONS.grades), parseOpts);
      const ids = new Set(terms.map((x) => x.id));
      ids.add(termIdForDate(t));
      return [...ids]
        .map((id) => termFromId(id, t))
        .filter((x): x is Term => x !== null)
        .sort((a, b) => b.startDate.localeCompare(a.startDate));
    },

    async getCourses(termId?: string): Promise<CourseUnit[]> {
      // Derived from the timetable of the current and next week (USOSweb has no cheap "my courses" view).
      const t = today();
      const currentTerm = termIdForDate(t);
      if (termId && termId !== currentTerm) return [];
      const entries = await planWeeks(mondayOf(t), addDays(mondayOf(t), 13));
      const units = new Map<string, CourseUnit>();
      for (const e of entries) {
        const courseKey = e.courseId ?? e.courseName.pl;
        const id = `usos-web-unit:${courseKey}:${e.classType ?? ""}:${e.groupNumber ?? 0}`;
        const prev = units.get(id);
        if (prev) {
          for (const l of e.lecturers) if (!prev.lecturers.includes(l)) prev.lecturers.push(l);
          continue;
        }
        units.set(id, {
          id,
          courseId: courseKey,
          courseName: e.courseName,
          classType: e.classType ?? "",
          termId: currentTerm,
          groupNumber: e.groupNumber,
          lecturers: [...e.lecturers],
          ects: null,
          platonCourseId: null
        });
      }
      return [...units.values()];
    },

    async getTimetable(from: string, to: string): Promise<TimetableEntry[]> {
      const f = toDateOnly(from);
      const e = toDateOnly(to);
      if (!f || !e) return [];
      return planWeeks(f, diffDays(f, e) > 98 ? addDays(f, 98) : e);
    },

    async getGrades(termIds?: string[]): Promise<Grade[]> {
      const { grades } = parseGradesHtml(await getHtml(ACTIONS.grades), parseOpts);
      return termIds && termIds.length > 0 ? grades.filter((g) => termIds.includes(g.termId)) : grades;
    },

    async getPayments(): Promise<Payment[]> {
      return parsePaymentsHtml(await getHtml(ACTIONS.payments), today());
    },

    async listMessages(opts): Promise<MessageThread[]> {
      const all = parseMessagesHtml(await getHtml(ACTIONS.messages), parseOpts);
      const offset = opts?.offset ?? 0;
      return all.slice(offset, opts?.limit ? offset + opts.limit : undefined);
    },

    async getMessage(id: string): Promise<Message> {
      // TODO(verify): detail URL parameter (msg_id).
      const msg = parseMessageHtml(await getHtml(ACTIONS.messages, { msg_id: id }), id, parseOpts);
      if (!msg) throw new AdapterUnavailableError("Nie udało się odczytać wiadomości z USOSweb");
      return msg;
    },

    async sendMessage(): Promise<{ id: string }> {
      // TODO(verify): the compose form (field names, CSRF token) is unknown; not implemented in web mode.
      throw new AdapterUnavailableError("Wysyłanie wiadomości nie jest dostępne w trybie web (użyj USOS API)");
    },

    async getDeanOfficeContacts(): Promise<Contact[]> {
      // TODO(verify): find the page with dean's office contact data and add a parser.
      return [];
    },

    async getAnnouncements(limit = 20): Promise<Announcement[]> {
      return parseNewsHtml(await getHtml(ACTIONS.home), parseOpts).slice(0, limit);
    }
  };
}
