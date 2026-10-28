/**
 * HTML parsers for USOSweb (kontroler.php) pages.
 *
 * IMPORTANT - UNVERIFIED SELECTORS: there was no network access to the Vistula USOSweb when this was written.
 * Markup assumptions (table.grey, .news-item, .act, th/td header texts, "prz_kod" links, ...) come from general
 * knowledge of USOSweb and from the hand-written fixtures in ./fixtures/*.html - NOT from captured pages.
 * Expect to adjust selectors after comparing with the live site (save real pages over the fixtures and
 * re-run the tests). All parsers are defensive: unknown markup yields [] / null instead of throwing.
 */
import { load, type Cheerio, type CheerioAPI } from "cheerio";
import type { Announcement, Grade, MessageThread, Message, Payment, StudentProfile, Term, TimetableEntry } from "@vusos/shared";
import {
  describeGrade,
  isRecord,
  mapActivities,
  normalizeGradeSymbol,
  numericGrade,
  toNumber
} from "./mappers.js";
import { addDays, warsawLocalToIso } from "./time.js";

// cheerio's node types come from domhandler, which is not a direct dependency of this package.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Sel = Cheerio<any>;

export interface ParseOptions {
  /** Used to resolve relative hrefs (e.g. https://usosweb.vistula.edu.pl/). */
  baseUrl?: string;
}

// ---------- helpers ----------

function clean(s: string): string {
  return s.replace(/ /g, " ").replace(/\s+/g, " ").trim();
}

function textOf(el: Sel): string {
  return clean(el.text());
}

function slug(s: string): string {
  return (
    s
      .toLowerCase()
      .normalize("NFD")
      .replace(/[̀-ͯ]/g, "")
      .replace(/ł/g, "l")
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "") || "x"
  );
}

function resolveUrl(href: string | undefined, opts: ParseOptions): string | null {
  if (!href) return null;
  if (opts.baseUrl && URL.canParse(href, opts.baseUrl)) return new URL(href, opts.baseUrl).toString();
  return href;
}

function courseIdFromHref(href: string | undefined): string | null {
  const m = href ? /[?&]prz_kod=([^&#]+)/.exec(href) : null;
  return m?.[1] ? decodeURIComponent(m[1]) : null;
}

function capitalize(s: string): string {
  return s ? s.charAt(0).toUpperCase() + s.slice(1) : s;
}

/** "dd.mm.yyyy" | "yyyy-mm-dd" [+ "HH:MM[:SS]" / "THH:MM"] -> "YYYY-MM-DD[ HH:MM:SS]" or null. */
export function normalizeLocalDate(s: string): string | null {
  const t = clean(s);
  const iso = /(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?/.exec(t);
  if (iso) {
    const [, y, mo, d, h, mi, sec] = iso;
    return h ? `${y}-${mo}-${d} ${h.padStart(2, "0")}:${mi}:${sec ?? "00"}` : `${y}-${mo}-${d}`;
  }
  const pl = /(\d{1,2})\.(\d{1,2})\.(\d{4})(?:\s+(\d{1,2}):(\d{2})(?::(\d{2}))?)?/.exec(t);
  if (pl) {
    const [, d, mo, y, h, mi, sec] = pl;
    const date = `${y}-${(mo ?? "").padStart(2, "0")}-${(d ?? "").padStart(2, "0")}`;
    return h ? `${date} ${h.padStart(2, "0")}:${mi}:${sec ?? "00"}` : date;
  }
  return null;
}

function toIsoOrNull(s: string | undefined | null): string | null {
  const n = s ? normalizeLocalDate(s) : null;
  return n ? warsawLocalToIso(n) : null;
}

/** "1 250,00 zł" -> 1250. */
export function parseMoney(s: string): number | null {
  const t = clean(s).replace(/(zł|pln)/gi, "").replace(/\s/g, "");
  return t === "" ? null : toNumber(t);
}

interface TableData {
  headers: string[];
  rows: { cells: Sel[]; row: Sel }[];
}

function readTable($: CheerioAPI, table: Sel): TableData {
  const headers: string[] = [];
  const rows: TableData["rows"] = [];
  table.find("tr").each((_, tr) => {
    const row = $(tr);
    const ths = row.children("th");
    const tds = row.children("td");
    if (ths.length > 0 && tds.length === 0 && headers.length === 0) {
      ths.each((__, th) => void headers.push(textOf($(th)).toLowerCase()));
    } else if (tds.length > 0) {
      rows.push({ cells: tds.toArray().map((td) => $(td)), row });
    }
  });
  return { headers, rows };
}

function findCol(headers: readonly string[], re: RegExp): number {
  return headers.findIndex((h) => re.test(h));
}

// ---------- login detection ----------

/** True when an HTTP redirect target is the (SSO) login page. */
export function isLoginRedirect(location: string): boolean {
  return /logowanie|login\.microsoftonline\.com|\/login\b|login\.php/i.test(location);
}

/** True when a 200 response body is the login page rather than a logged-in view. */
export function looksLikeLoginPage(html: string): boolean {
  if (/login\.microsoftonline\.com/i.test(html)) return true;
  return /Zaloguj/.test(html) && !/Wyloguj/.test(html);
}

// ---------- profile ----------

export function parseProfileHtml(html: string): StudentProfile | null {
  const $ = load(html);
  const info = textOf($("#login-info, .user-info, #user-info").first());
  const m = /(?:Zalogowany(?:\/a)?(?:\s+jako)?:?)\s*([^|(]+?)\s*(?:\(|\||$)/i.exec(info);
  const name = m?.[1] ? clean(m[1]) : "";
  if (!name) return null;
  const parts = name.split(" ");
  const lastName = parts.length > 1 ? (parts[parts.length - 1] ?? "") : "";
  const firstName = parts.length > 1 ? parts.slice(0, -1).join(" ") : name;
  const album = /(?:nr|numer)\s+albumu\s*:?\s*(\d+)/i.exec(info)?.[1] ?? null;
  const programmes = $("ul.programmes li")
    .toArray()
    .map((li, i) => {
      const el = $(li);
      const label = textOf(el);
      const [name0, ...rest] = label.split(",");
      return {
        id: el.attr("data-programme-id") ?? `web-programme-${i + 1}`,
        name: { pl: clean(name0 ?? label) },
        status: null,
        stage: rest.length > 0 ? clean(rest.join(",")) : null
      };
    });
  return {
    id: album ?? `web:${slug(name)}`,
    firstName,
    lastName,
    email: null, // TODO(verify): USOSweb shows the e-mail on "Moje dane" (dla_stud/.../dane?), not on the home page
    studentNumber: album,
    photoUrl: null,
    programmes
  };
}

// ---------- timetable ----------

interface ActFields {
  start: string; // local "YYYY-MM-DD HH:MM:SS"
  end: string;
  name: string;
  courseId: string | null;
  classType: string | null;
  group: number | null;
  room: string | null;
  building: string | null;
  lecturers: string[];
  url: string | null;
}

function buildEntry(a: ActFields): TimetableEntry | null {
  const start = warsawLocalToIso(a.start);
  const end = warsawLocalToIso(a.end);
  if (!start || !end || !a.name) return null;
  return {
    id: `usos-web-tt:${a.courseId ?? slug(a.name)}:${a.start.replace(/\D/g, "")}`,
    source: "usos",
    start,
    end,
    courseId: a.courseId,
    courseName: { pl: a.name },
    classType: a.classType,
    groupNumber: a.group,
    room: a.room,
    building: a.building,
    lecturers: a.lecturers,
    url: a.url
  };
}

function planFromJson($: CheerioAPI): TimetableEntry[] {
  const out: TimetableEntry[] = [];
  $('script[type="application/json"]').each((_, s) => {
    try {
      const data: unknown = JSON.parse($(s).text());
      const list = Array.isArray(data) ? data : isRecord(data) && Array.isArray(data.activities) ? data.activities : [];
      out.push(...mapActivities(list));
    } catch {
      // not our JSON
    }
  });
  return out;
}

function planFromDataAttrs($: CheerioAPI, opts: ParseOptions): TimetableEntry[] {
  const out: TimetableEntry[] = [];
  $("[data-start][data-end]").each((_, node) => {
    const el = $(node);
    const start = normalizeLocalDate(el.attr("data-start") ?? "");
    const end = normalizeLocalDate(el.attr("data-end") ?? "");
    if (!start || !end) return;
    const group = toNumber(el.attr("data-group"));
    const entry = buildEntry({
      start: start.length === 10 ? `${start} 00:00:00` : start,
      end: end.length === 10 ? `${end} 00:00:00` : end,
      name: clean(el.attr("data-name") ?? textOf(el)),
      courseId: el.attr("data-course-id") ?? null,
      classType: el.attr("data-classtype") || null,
      group: group === null ? null : Math.trunc(group),
      room: el.attr("data-room") || null,
      building: el.attr("data-building") || null,
      lecturers: (el.attr("data-lecturers") ?? "").split(",").map(clean).filter((s) => s !== ""),
      url: resolveUrl(el.attr("data-url"), opts)
    });
    if (entry) out.push(entry);
  });
  return out;
}

const TIME_RE = /(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})/;

function parseAct($: CheerioAPI, el: Sel, date: string, opts: ParseOptions): TimetableEntry | null {
  const t = TIME_RE.exec(textOf(el.find(".time").first()) || textOf(el));
  if (!t) return null;
  const hh = (h: string | undefined, m: string | undefined): string => `${(h ?? "").padStart(2, "0")}:${m}:00`;
  const link = el.find("a").first();
  const name = textOf(link) || textOf(el.find(".name").first());
  const group = /(\d+)/.exec(textOf(el.find(".group, .grp").first()))?.[1];
  const roomRaw = textOf(el.find(".room, .loc").first()).replace(/^(sala|s\.|room)\s*/i, "");
  const lecturers = el
    .find(".lecturer, .lect")
    .toArray()
    .flatMap((l) => textOf($(l)).split(";"))
    .map(clean)
    .filter((s) => s !== "");
  const classType = textOf(el.find(".type, .classtype").first());
  return buildEntry({
    start: `${date} ${hh(t[1], t[2])}`,
    end: `${date} ${hh(t[3], t[4])}`,
    name,
    courseId: courseIdFromHref(link.attr("href")),
    classType: classType ? capitalize(classType) : null,
    group: group ? Number(group) : null,
    room: roomRaw || null,
    building: textOf(el.find(".building").first()) || null,
    lecturers,
    url: resolveUrl(link.attr("href"), opts)
  });
}

function planFromTables($: CheerioAPI, opts: ParseOptions): TimetableEntry[] {
  const out: TimetableEntry[] = [];
  $("table").each((_, tbl) => {
    const table = $(tbl);
    const dates: (string | null)[] = [];
    table
      .find("tr")
      .first()
      .children("th")
      .each((__, th) => {
        const cell = $(th);
        const d = normalizeLocalDate(cell.attr("data-date") ?? textOf(cell));
        dates.push(d ? d.slice(0, 10) : null);
      });
    if (!dates.some((d) => d !== null)) return;
    table.find("tr").each((__, tr) => {
      $(tr)
        .children("td")
        .each((idx, td) => {
          const date = dates[idx];
          if (!date) return;
          const cell = $(td);
          let acts = cell.find(".act, .activity, li");
          if (acts.length === 0 && TIME_RE.test(textOf(cell))) acts = cell;
          acts.each((___, a) => {
            const entry = parseAct($, $(a), date, opts);
            if (entry) out.push(entry);
          });
        });
    });
  });
  return out;
}

/** Tries, in order: embedded JSON, data-start/data-end elements, day-column table. First non-empty wins. */
export function parsePlanHtml(html: string, opts: ParseOptions = {}): TimetableEntry[] {
  const $ = load(html);
  const strategies = [() => planFromJson($), () => planFromDataAttrs($, opts), () => planFromTables($, opts)];
  for (const run of strategies) {
    const entries = run();
    if (entries.length > 0) {
      return [...new Map(entries.map((e) => [e.id, e])).values()].sort((a, b) => a.start.localeCompare(b.start));
    }
  }
  return [];
}

// ---------- terms (academic calendar) ----------

/** "2025Z" / "Semestr zimowy 2025/26" -> "2025Z". TODO(verify): Vistula's real term ids. */
export function termIdFromText(text: string): string | null {
  const direct = /\b(20\d{2})([ZL])\b/i.exec(text);
  if (direct?.[1] && direct[2]) return `${direct[1]}${direct[2].toUpperCase()}`;
  const yr = /\b(20\d{2})\s*\/\s*(?:20)?\d{2}\b/.exec(text);
  if (yr?.[1]) {
    if (/zimow/i.test(text)) return `${yr[1]}Z`;
    if (/letni/i.test(text)) return `${yr[1]}L`;
  }
  return null;
}

/** Approximate Polish academic calendar. TODO(verify): real term boundaries come from USOS. */
export function termFromId(id: string, today: string): Term | null {
  const m = /^(20\d{2})([ZL])$/.exec(id);
  if (!m?.[1] || !m[2]) return null;
  const y = Number(m[1]);
  const winter = m[2] === "Z";
  const startDate = winter ? `${y}-10-01` : `${y + 1}-02-16`;
  const endDate = winter ? `${y + 1}-02-15` : `${y + 1}-09-30`;
  return {
    id,
    name: { pl: `Semestr ${winter ? "zimowy" : "letni"} ${y}/${String(y + 1).slice(2)}` },
    startDate,
    endDate,
    isCurrent: startDate <= today && today <= endDate
  };
}

/** Term id containing the given date (approximate calendar). */
export function termIdForDate(date: string): string {
  const y = Number(date.slice(0, 4));
  const md = date.slice(5);
  if (md >= "10-01") return `${y}Z`;
  if (md <= "02-15") return `${y - 1}Z`;
  return `${y - 1}L`;
}

// ---------- grades ----------

export interface ParsedGrades {
  terms: { id: string; name: string }[];
  grades: Grade[];
}

function termContext($: CheerioAPI, table: Sel): { id: string; name: string } | null {
  const candidates: string[] = [];
  const dataTerm = table.closest("[data-term-id]").attr("data-term-id");
  if (dataTerm) candidates.push(dataTerm);
  candidates.push(textOf(table.find("caption").first()));
  let cur: Sel = table;
  for (let depth = 0; depth < 3 && cur.length > 0; depth++) {
    const heading = cur.prevAll("h1, h2, h3, h4, .term-header, .semestr").first();
    if (heading.length > 0) {
      candidates.push(textOf(heading));
      break;
    }
    cur = cur.parent();
  }
  for (const c of candidates) {
    const id = termIdFromText(c);
    if (id) return { id, name: c.replace(/\s*\(\s*20\d{2}[ZL]\s*\)\s*$/i, "") || id };
  }
  return null;
}

export function parseGradesHtml(html: string, opts: ParseOptions = {}): ParsedGrades {
  void opts;
  const $ = load(html);
  const terms = new Map<string, string>();
  const grades: Grade[] = [];
  $("table").each((_, tbl) => {
    const table = $(tbl);
    const ctx = termContext($, table);
    if (!ctx) return;
    const { headers, rows } = readTable($, table);
    const cCourse = findCol(headers, /przedmiot|nazwa/);
    const cGrade = findCol(headers, /ocena/);
    if (cCourse < 0 || cGrade < 0 || rows.length === 0) return;
    const cType = findCol(headers, /typ|forma|rodzaj/);
    const cSession = findCol(headers, /termin|podej|sesja/);
    const cDate = findCol(headers, /data/);
    const cBy = findCol(headers, /wystawi|nauczyciel|prowadz/);
    const cComment = findCol(headers, /komentarz|uwagi/);
    terms.set(ctx.id, ctx.name);
    for (const { cells } of rows) {
      const courseCell = cells[cCourse];
      const gradeCell = cells[cGrade];
      if (!courseCell || !gradeCell) continue;
      const rawSymbol = textOf(gradeCell);
      if (!rawSymbol || rawSymbol === "-" || rawSymbol === "—") continue;
      const symbol = normalizeGradeSymbol(rawSymbol);
      const link = courseCell.find("a").first();
      const courseName = textOf(courseCell);
      const courseId = courseIdFromHref(link.attr("href")) ?? `web:${slug(courseName)}`;
      const classType = cType >= 0 && cells[cType] ? textOf(cells[cType]) : "";
      const sessionNum = cSession >= 0 && cells[cSession] ? toNumber(/\d+/.exec(textOf(cells[cSession]))?.[0]) : null;
      const info = describeGrade(symbol);
      grades.push({
        id: `usos-web-grade:${ctx.id}:${courseId}:${slug(classType)}:${sessionNum ?? 1}`,
        termId: ctx.id,
        courseId,
        courseName: { pl: courseName },
        classType: classType || null,
        examSession: sessionNum === null ? null : Math.trunc(sessionNum),
        valueSymbol: symbol,
        valueDescription: info.description,
        passes: info.passes,
        // TODO(verify): USOSweb does not show "counts into average"; assume numeric grades do.
        countsIntoAverage: numericGrade(symbol) !== null,
        dateModified: cDate >= 0 && cells[cDate] ? toIsoOrNull(textOf(cells[cDate])) : null,
        gradedBy: cBy >= 0 && cells[cBy] ? textOf(cells[cBy]) || null : null,
        comment: cComment >= 0 && cells[cComment] ? textOf(cells[cComment]) || null : null
      });
    }
  });
  return { terms: [...terms].map(([id, name]) => ({ id, name })), grades };
}

// ---------- payments ----------

function paymentType(desc: string): string {
  if (/czesne|rata/i.test(desc)) return "tuition";
  if (/legitymac/i.test(desc)) return "id_card";
  if (/wpisow/i.test(desc)) return "enrollment_fee";
  return "other";
}

export function parsePaymentsHtml(html: string, today: string): Payment[] {
  const $ = load(html);
  const pageText = clean($("body").text());
  const account =
    /\b(\d{2}(?:\s?\d{4}){6})\b/.exec(textOf($(".account-number, .numer-konta").first()) || pageText)?.[1] ?? null;
  const out: Payment[] = [];
  $("table").each((_, tbl) => {
    const { headers, rows } = readTable($, $(tbl));
    const cDesc = findCol(headers, /opis|tytu/);
    const cAmount = findCol(headers, /kwota|nale/);
    if (cDesc < 0 || cAmount < 0) return;
    const cDue = findCol(headers, /termin|do dnia|p[łl]atne/);
    const cPaid = findCol(headers, /wp[łl]acono|zap[łl]acono/);
    const cInterest = findCol(headers, /odsetk/);
    const cState = findCol(headers, /status|stan/);
    for (const { cells } of rows) {
      const description = cells[cDesc] ? textOf(cells[cDesc]) : "";
      const amount = cells[cAmount] ? parseMoney(textOf(cells[cAmount])) : null;
      if (!description || amount === null) continue;
      const dueRaw = cDue >= 0 && cells[cDue] ? normalizeLocalDate(textOf(cells[cDue])) : null;
      const due = dueRaw ? dueRaw.slice(0, 10) : null;
      const paid = cPaid >= 0 && cells[cPaid] ? (parseMoney(textOf(cells[cPaid])) ?? 0) : 0;
      const statusText = (cState >= 0 && cells[cState] ? textOf(cells[cState]) : "").toLowerCase();
      const owed = Math.max(0, amount - paid);
      let state: Payment["state"];
      if (/anul|storn/.test(statusText)) state = "cancelled";
      else if (/cz[eę][śs]ciowo/.test(statusText) && owed > 0) state = due && due < today ? "overdue" : "partially_paid";
      else if (/zap[łl]acon|op[łl]acon/.test(statusText) && !/nie|do zap/.test(statusText)) state = "paid";
      else if (/zaleg|po terminie|przeterm/.test(statusText)) state = "overdue";
      else if (owed <= 0 && amount > 0) state = "paid";
      else if (owed > 0 && due && due < today) state = "overdue";
      else if (paid > 0) state = "partially_paid";
      else state = "unpaid";
      out.push({
        id: `usos-web-pay:${slug(description)}:${due ?? "nodue"}`,
        type: paymentType(description),
        description: { pl: description },
        amount,
        paid: state === "paid" ? amount : paid,
        currency: "PLN",
        dueDate: due,
        state,
        interest: cInterest >= 0 && cells[cInterest] ? parseMoney(textOf(cells[cInterest])) : null,
        accountNumber: account ? account.replace(/\s/g, "") : null
      });
    }
  });
  return out;
}

// ---------- news ----------

export function parseNewsHtml(html: string, opts: ParseOptions = {}): Announcement[] {
  const $ = load(html);
  const out: Announcement[] = [];
  $(".news-item, .wiadomosc, article.news").each((i, node) => {
    const el = $(node);
    const titleEl = el.find(".title, h2, h3, h4").first();
    const title = textOf(titleEl);
    if (!title) return;
    const link = titleEl.find("a").first();
    const dateText = el.find("time").first().attr("datetime") ?? textOf(el.find(".date, time, .news-date").first());
    const published = toIsoOrNull(dateText);
    const idAttr = el.attr("data-id");
    const content = el.find(".news-content, .content, .tresc").first();
    out.push({
      id: `usos-web-news:${idAttr ?? `${slug(title)}-${published?.slice(0, 10) ?? i}`}`,
      source: "usos",
      title,
      bodyHtml: (content.html() ?? "").trim(),
      publishedAt: published ?? "",
      author: textOf(el.find(".author, .autor").first()) || null,
      category: textOf(el.find(".category, .kategoria").first()) || null,
      url: resolveUrl(link.attr("href"), opts)
    });
  });
  return out.sort((a, b) => b.publishedAt.localeCompare(a.publishedAt));
}

// ---------- messages ----------

function messageIdFromHref(href: string | undefined): string | null {
  const m = href ? /[?&](?:msg_id|message_id|wiadomosc_id|mid)=([^&#]+)/.exec(href) : null;
  return m?.[1] ? decodeURIComponent(m[1]) : null;
}

export function parseMessagesHtml(html: string, opts: ParseOptions = {}): MessageThread[] {
  const $ = load(html);
  const out: MessageThread[] = [];
  $("table").each((_, tbl) => {
    const table = $(tbl);
    const { headers, rows } = readTable($, table);
    const cSubject = findCol(headers, /temat/);
    if (cSubject < 0) return;
    const cFrom = findCol(headers, /nadawca|od\b/);
    const cDate = findCol(headers, /data/);
    for (const { cells, row } of rows) {
      const subjectCell = cells[cSubject];
      if (!subjectCell) continue;
      const link = subjectCell.find("a").first();
      const id = messageIdFromHref(link.attr("href"));
      if (!id) continue;
      const unread =
        row.is(".unread, .new, .nieprzeczytana") ||
        row.attr("data-unread") === "1" ||
        subjectCell.find("strong, b").length > 0;
      out.push({
        id,
        source: "usos",
        subject: textOf(subjectCell),
        from: cFrom >= 0 && cells[cFrom] ? textOf(cells[cFrom]) : "",
        to: [],
        date: cDate >= 0 && cells[cDate] ? (toIsoOrNull(textOf(cells[cDate])) ?? "") : "",
        preview: "",
        unread,
        url: resolveUrl(link.attr("href"), opts)
      });
    }
  });
  return out;
}

export function parseMessageHtml(html: string, id: string, opts: ParseOptions = {}): Message | null {
  const $ = load(html);
  const root = $(".message").first();
  const scope: Sel = root.length > 0 ? root : $("body");
  const subject = textOf(scope.find(".subject, h1, h2").first());
  const body = scope.find(".message-body, .tresc, .body").first();
  if (!subject || body.length === 0) return null;
  const to = textOf(scope.find(".to").first());
  return {
    id,
    source: "usos",
    subject,
    from: textOf(scope.find(".from").first()),
    to: to ? to.split(/[;,]/).map(clean).filter((s) => s !== "") : [],
    date: toIsoOrNull(textOf(scope.find(".date").first())) ?? "",
    preview: clean(body.text()).slice(0, 160),
    unread: false,
    url: null,
    bodyHtml: (body.html() ?? "").trim(),
    attachments: scope
      .find("a.attachment")
      .toArray()
      .map((a) => ({
        name: textOf($(a)),
        url: resolveUrl($(a).attr("href"), opts) ?? "",
        size: null
      }))
      .filter((a) => a.url !== "")
  };
}

// ---------- week helpers used by the adapter ----------

/** Plan-page query params for a given Monday. TODO(verify): real parameter name of the week selector. */
export function planWeekParams(monday: string): Record<string, string> {
  return { plan_start: monday, plan_end: addDays(monday, 6) };
}
