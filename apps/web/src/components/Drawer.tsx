import { useEffect, type ReactNode } from "react";
import { X } from "lucide-react";

interface Props {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
}

/** Bottom sheet on mobile, right-hand panel on desktop. */
export function Drawer({ open, title, onClose, children }: Props) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center sm:items-stretch sm:justify-end" role="dialog" aria-modal="true" aria-label={title}>
      <button type="button" aria-label="Zamknij" className="absolute inset-0 bg-slate-900/50" onClick={onClose} />
      <div className="relative z-10 flex max-h-[85vh] w-full flex-col rounded-t-2xl border border-slate-200 bg-white shadow-xl dark:border-slate-800 dark:bg-slate-900 sm:max-h-none sm:w-96 sm:rounded-none sm:rounded-l-2xl">
        <div className="flex items-center justify-between gap-2 border-b border-slate-200 px-4 py-3 dark:border-slate-800">
          <h2 className="font-semibold">{title}</h2>
          <button type="button" className="btn btn-ghost !p-1.5" onClick={onClose} aria-label="Zamknij panel">
            <X size={18} aria-hidden />
          </button>
        </div>
        <div className="overflow-y-auto p-4">{children}</div>
      </div>
    </div>
  );
}
