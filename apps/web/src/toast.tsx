import { useEffect, useState } from "react";

type ToastKind = "success" | "error" | "info";
interface ToastItem { id: number; kind: ToastKind; text: string }

let nextId = 1;
const listeners = new Set<(t: ToastItem) => void>();

export function toast(text: string, kind: ToastKind = "info"): void {
  const item = { id: nextId++, kind, text };
  listeners.forEach((l) => l(item));
}

const KIND_CLS: Record<ToastKind, string> = {
  success: "border-emerald-300 bg-emerald-50 text-emerald-900 dark:border-emerald-800 dark:bg-emerald-950 dark:text-emerald-200",
  error: "border-red-300 bg-red-50 text-red-900 dark:border-red-900 dark:bg-red-950 dark:text-red-200",
  info: "border-slate-300 bg-white text-slate-900 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100",
};

export function Toaster() {
  const [items, setItems] = useState<ToastItem[]>([]);
  useEffect(() => {
    const l = (t: ToastItem) => {
      setItems((cur) => [...cur, t]);
      setTimeout(() => setItems((cur) => cur.filter((x) => x.id !== t.id)), 5000);
    };
    listeners.add(l);
    return () => void listeners.delete(l);
  }, []);
  return (
    <div className="pointer-events-none fixed inset-x-0 top-0 z-[60] flex flex-col items-center gap-2 p-4 pt-safe" aria-live="polite">
      {items.map((t) => (
        <div key={t.id} role="status" className={`pointer-events-auto max-w-sm rounded-lg border px-4 py-2 text-sm shadow-lg ${KIND_CLS[t.kind]}`}>
          {t.text}
        </div>
      ))}
    </div>
  );
}
