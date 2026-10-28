import { useEffect, type ReactNode } from "react";
import { X } from "lucide-react";

export function Dialog({ open, title, onClose, children }: { open: boolean; title: string; onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4" role="dialog" aria-modal="true" aria-label={title}>
      <button type="button" aria-label="Zamknij" className="absolute inset-0 bg-slate-900/50" onClick={onClose} />
      <div className="card relative z-10 max-h-[90vh] w-full max-w-lg overflow-y-auto !p-0 shadow-xl">
        <div className="flex items-center justify-between border-b border-slate-200 px-4 py-3 dark:border-slate-800">
          <h2 className="font-semibold">{title}</h2>
          <button type="button" className="btn btn-ghost !p-1.5" onClick={onClose} aria-label="Zamknij okno">
            <X size={18} aria-hidden />
          </button>
        </div>
        <div className="p-4">{children}</div>
      </div>
    </div>
  );
}
