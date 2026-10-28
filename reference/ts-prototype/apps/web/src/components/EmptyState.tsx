import type { ReactNode } from "react";
import { Inbox } from "lucide-react";

export function EmptyState({ title, hint, icon }: { title: string; hint?: string; icon?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-2 px-4 py-8 text-center">
      <div className="muted">{icon ?? <Inbox size={28} aria-hidden />}</div>
      <p className="font-medium">{title}</p>
      {hint && <p className="muted max-w-sm text-xs">{hint}</p>}
    </div>
  );
}
