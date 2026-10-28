import type { LangText } from "@vusos/shared";

export function pick(t: LangText | null | undefined): string {
  return t?.pl ?? t?.en ?? "";
}

export function formatMoney(amount: number, currency = "PLN"): string {
  try {
    return new Intl.NumberFormat("pl-PL", { style: "currency", currency }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency}`;
  }
}

export function formatBytes(n: number | null | undefined): string {
  if (n == null) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

export type ClassKind = "lecture" | "exercise" | "lab" | "seminar" | "project" | "other";

export function classKind(t: string | null | undefined): ClassKind {
  const s = (t ?? "").trim().toLowerCase();
  if (!s) return "other";
  if (s === "w" || s.startsWith("wyk") || s.startsWith("lec")) return "lecture";
  if (s.startsWith("ćw") || s.startsWith("cw") || s.startsWith("exerc") || s.startsWith("class") || s.startsWith("conv")) return "exercise";
  if (s.startsWith("lab")) return "lab";
  if (s.startsWith("sem")) return "seminar";
  if (s.startsWith("proj")) return "project";
  return "other";
}

const CLASS_LABELS: Record<ClassKind, string> = {
  lecture: "wykład",
  exercise: "ćwiczenia",
  lab: "laboratorium",
  seminar: "seminarium",
  project: "projekt",
  other: "",
};

export function classTypeLabel(t: string | null | undefined): string {
  const k = classKind(t);
  return k === "other" ? (t ?? "") : CLASS_LABELS[k];
}

export const CLASS_BADGE: Record<ClassKind, string> = {
  lecture: "badge-brand",
  exercise: "badge-blue",
  lab: "badge-green",
  seminar: "badge-amber",
  project: "badge-amber",
  other: "badge-gray",
};

/** Numeric value of a grade symbol ("4,5" / "4.5"), or null for non-numeric ones (zal, nzal, ...). */
export function gradeNumber(symbol: string): number | null {
  const n = Number(symbol.trim().replace(",", "."));
  return Number.isFinite(n) && symbol.trim() !== "" ? n : null;
}

export function average(values: number[]): number | null {
  if (values.length === 0) return null;
  return values.reduce((a, b) => a + b, 0) / values.length;
}

export function formatAverage(v: number | null): string {
  return v == null ? "—" : v.toFixed(2).replace(".", ",");
}

export function pluralPl(n: number, one: string, few: string, many: string): string {
  if (n === 1) return one;
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 >= 2 && m10 <= 4 && !(m100 >= 12 && m100 <= 14)) return few;
  return many;
}
