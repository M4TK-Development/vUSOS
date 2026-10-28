import { addDays, format, formatDistanceToNowStrict, parseISO } from "date-fns";
import { pl } from "date-fns/locale";

export const TZ = "Europe/Warsaw";

const dayFmt = new Intl.DateTimeFormat("en-CA", { timeZone: TZ, year: "numeric", month: "2-digit", day: "2-digit" });
const timeFmt = new Intl.DateTimeFormat("pl-PL", { timeZone: TZ, hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const dateTimeFmt = new Intl.DateTimeFormat("pl-PL", {
  timeZone: TZ,
  day: "numeric",
  month: "short",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});
const dateFmt = new Intl.DateTimeFormat("pl-PL", { timeZone: TZ, day: "numeric", month: "short", year: "numeric" });

function toDate(v: string | Date): Date | null {
  const d = typeof v === "string" ? new Date(v) : v;
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Calendar day (YYYY-MM-DD) of an instant, in Europe/Warsaw. */
export function dayKey(v: string | Date): string {
  const d = toDate(v);
  return d ? dayFmt.format(d) : "";
}

export function timeOf(v: string | Date): string {
  const d = toDate(v);
  return d ? timeFmt.format(d) : "";
}

export function formatDateTime(v: string | Date | null | undefined): string {
  const d = v ? toDate(v) : null;
  return d ? dateTimeFmt.format(d) : "—";
}

export function formatDate(v: string | Date | null | undefined): string {
  const d = v ? toDate(v) : null;
  return d ? dateFmt.format(d) : "—";
}

export function fromNow(v: string | Date | null | undefined): string {
  const d = v ? toDate(v) : null;
  if (!d) return "";
  const dist = formatDistanceToNowStrict(d, { locale: pl });
  return d.getTime() > Date.now() ? `za ${dist}` : `${dist} temu`;
}

export function todayKey(): string {
  return dayKey(new Date());
}

// --- day-key arithmetic (calendar math on plain YYYY-MM-DD strings, independent of the browser tz) ---

export function keyToDate(key: string): Date {
  const [y, m, d] = key.split("-").map(Number);
  return new Date(y ?? 1970, (m ?? 1) - 1, d ?? 1);
}

export function dateToKey(d: Date): string {
  return format(d, "yyyy-MM-dd");
}

export function addDaysKey(key: string, n: number): string {
  return dateToKey(addDays(keyToDate(key), n));
}

/** Monday of the ISO week containing the day. */
export function mondayOf(key: string): string {
  const dow = keyToDate(key).getDay(); // 0 = Sunday
  return addDaysKey(key, -((dow + 6) % 7));
}

export function weekKeys(monday: string): string[] {
  return Array.from({ length: 7 }, (_, i) => addDaysKey(monday, i));
}

export function diffDays(a: string, b: string): number {
  return Math.round((keyToDate(b).getTime() - keyToDate(a).getTime()) / 86_400_000);
}

export function labelDay(key: string, pattern = "EEE d MMM"): string {
  return format(keyToDate(key), pattern, { locale: pl });
}

export function labelRange(fromKey: string, toKey: string): string {
  const a = keyToDate(fromKey);
  const b = keyToDate(toKey);
  const sameMonth = a.getMonth() === b.getMonth() && a.getFullYear() === b.getFullYear();
  return sameMonth
    ? `${format(a, "d", { locale: pl })}–${format(b, "d MMMM yyyy", { locale: pl })}`
    : `${format(a, "d MMM", { locale: pl })} – ${format(b, "d MMM yyyy", { locale: pl })}`;
}

export function isPast(v: string | null | undefined): boolean {
  const d = v ? toDate(v) : null;
  return !!d && d.getTime() < Date.now();
}

export { parseISO };
