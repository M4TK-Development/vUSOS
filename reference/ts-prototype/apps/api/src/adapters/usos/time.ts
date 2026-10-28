/**
 * Small date helpers shared by the USOS adapters. USOS returns wall-clock times in the installation's
 * time zone ("YYYY-MM-DD HH:MM:SS", no offset); for Vistula that is Europe/Warsaw.
 * Output of {@link warsawLocalToIso} is an ISO-8601 string WITH the Warsaw UTC offset
 * (e.g. "2025-10-06T08:30:00+02:00"), which `new Date()` parses unambiguously.
 */

export const WARSAW_TZ = "Europe/Warsaw";

const DAY_MS = 86_400_000;

let warsawFormatter: Intl.DateTimeFormat | undefined;
function formatter(): Intl.DateTimeFormat {
  warsawFormatter ??= new Intl.DateTimeFormat("en-GB", {
    timeZone: WARSAW_TZ,
    hourCycle: "h23",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
  return warsawFormatter;
}

function warsawParts(utcMs: number): { y: number; mo: number; d: number; h: number; mi: number; s: number } {
  const out = { y: 0, mo: 0, d: 0, h: 0, mi: 0, s: 0 };
  for (const p of formatter().formatToParts(new Date(utcMs))) {
    const v = Number(p.value);
    if (p.type === "year") out.y = v;
    else if (p.type === "month") out.mo = v;
    else if (p.type === "day") out.d = v;
    else if (p.type === "hour") out.h = v;
    else if (p.type === "minute") out.mi = v;
    else if (p.type === "second") out.s = v;
  }
  return out;
}

/** UTC offset of Europe/Warsaw at the given instant, in minutes (60 or 120). */
export function warsawOffsetMinutes(utcMs: number): number {
  const flooredMs = Math.floor(utcMs / 1000) * 1000;
  const p = warsawParts(flooredMs);
  return Math.round((Date.UTC(p.y, p.mo - 1, p.d, p.h, p.mi, p.s) - flooredMs) / 60_000);
}

function pad(n: number, w = 2): string {
  return String(n).padStart(w, "0");
}

export function formatOffset(minutes: number): string {
  const sign = minutes < 0 ? "-" : "+";
  const abs = Math.abs(minutes);
  return `${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`;
}

const LOCAL_RE = /^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?$/;
const HAS_ZONE_RE = /(?:Z|[+-]\d{2}:?\d{2})$/;

/**
 * Converts a Warsaw wall-clock string ("YYYY-MM-DD HH:MM:SS", "YYYY-MM-DDTHH:MM", or date only)
 * to ISO-8601 with offset. Strings that already carry a zone are returned unchanged (normalised to "T").
 * Returns null when the input is not recognisable.
 */
export function warsawLocalToIso(local: string): string | null {
  const trimmed = local.trim();
  if (HAS_ZONE_RE.test(trimmed) && trimmed.length > 10) return trimmed.replace(" ", "T");
  const m = LOCAL_RE.exec(trimmed);
  if (!m) return null;
  const [, ys, mos, ds, hs = "00", mis = "00", ss = "00"] = m;
  const y = Number(ys);
  const mo = Number(mos);
  const d = Number(ds);
  const h = Number(hs);
  const mi = Number(mis);
  const s = Number(ss);
  const guess = Date.UTC(y, mo - 1, d, h, mi, s);
  let off = warsawOffsetMinutes(guess);
  let utc = guess - off * 60_000;
  const off2 = warsawOffsetMinutes(utc);
  if (off2 !== off) {
    off = off2;
    utc = guess - off * 60_000;
  }
  return `${ys}-${mos}-${ds}T${pad(h)}:${pad(mi)}:${pad(s)}${formatOffset(off)}`;
}

/** First 10 chars if they look like YYYY-MM-DD, else null. */
export function toDateOnly(value: string): string | null {
  const m = /^(\d{4}-\d{2}-\d{2})/.exec(value.trim());
  return m?.[1] ?? null;
}

export function addDays(date: string, n: number): string {
  const [y, m, d] = date.split("-").map(Number) as [number, number, number];
  const t = new Date(Date.UTC(y, m - 1, d) + n * DAY_MS);
  return `${pad(t.getUTCFullYear(), 4)}-${pad(t.getUTCMonth() + 1)}-${pad(t.getUTCDate())}`;
}

/** b - a in whole days (both YYYY-MM-DD). */
export function diffDays(a: string, b: string): number {
  const pa = a.split("-").map(Number) as [number, number, number];
  const pb = b.split("-").map(Number) as [number, number, number];
  return Math.round((Date.UTC(pb[0], pb[1] - 1, pb[2]) - Date.UTC(pa[0], pa[1] - 1, pa[2])) / DAY_MS);
}

/** Inclusive list of YYYY-MM-DD between from and to (empty if to < from). */
export function eachDay(from: string, to: string): string[] {
  const n = diffDays(from, to);
  const out: string[] = [];
  for (let i = 0; i <= n; i++) out.push(addDays(from, i));
  return out;
}

/** 0 = Sunday ... 6 = Saturday for a YYYY-MM-DD date. */
export function weekday(date: string): number {
  const [y, m, d] = date.split("-").map(Number) as [number, number, number];
  return new Date(Date.UTC(y, m - 1, d)).getUTCDay();
}

/** Calendar date (YYYY-MM-DD) of an instant in Europe/Warsaw. */
export function warsawDateOf(now: Date): string {
  const p = warsawParts(now.getTime());
  return `${pad(p.y, 4)}-${pad(p.mo)}-${pad(p.d)}`;
}

/** Monday on or before the given date. */
export function mondayOf(date: string): string {
  const wd = weekday(date);
  return addDays(date, -((wd + 6) % 7));
}
