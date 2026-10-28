export const WARSAW_TZ = "Europe/Warsaw";

const fmt = new Intl.DateTimeFormat("en-CA", { timeZone: WARSAW_TZ, year: "numeric", month: "2-digit", day: "2-digit" });

/** YYYY-MM-DD of the given instant in Europe/Warsaw. */
export function warsawDate(d: Date = new Date()): string {
  return fmt.format(d);
}

/**
 * Warsaw calendar date of an ISO string. Strings without a timezone designator are taken as already local
 * (their date part is returned as-is); strings with one are converted.
 */
export function warsawDateOf(iso: string): string | null {
  if (/^\d{4}-\d{2}-\d{2}$/.test(iso)) return iso;
  if (!/(Z|[+-]\d{2}:?\d{2})$/i.test(iso)) return /^\d{4}-\d{2}-\d{2}/.test(iso) ? iso.slice(0, 10) : null;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : warsawDate(new Date(t));
}

/** Adds days to a YYYY-MM-DD date (calendar arithmetic in UTC, so DST-safe). */
export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}
