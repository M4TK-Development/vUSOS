import { Link } from "react-router-dom";
import { Link2 } from "lucide-react";
import type { SourceSystem } from "@vusos/shared";

export const SYSTEM_NAMES: Record<SourceSystem, string> = { usos: "USOS", platon: "Platon" };

interface Props {
  system: SourceSystem;
  /** True when a previously established session expired (HTTP 424) rather than never being connected (409). */
  expired?: boolean;
  compact?: boolean;
}

export function ConnectPrompt({ system, expired = false, compact = false }: Props) {
  const name = SYSTEM_NAMES[system];
  return (
    <div
      role="alert"
      data-testid="connect-prompt"
      className={`flex flex-col items-center gap-3 rounded-xl border border-amber-300 bg-amber-50 text-center dark:border-amber-800 dark:bg-amber-950/40 ${compact ? "p-3" : "px-4 py-8"}`}
    >
      <Link2 className="text-amber-600 dark:text-amber-400" size={compact ? 20 : 28} aria-hidden />
      <div>
        <p className="font-medium text-amber-900 dark:text-amber-200">
          {expired ? `Sesja w systemie ${name} wygasła` : `System ${name} nie jest połączony`}
        </p>
        <p className="mt-1 text-xs text-amber-800 dark:text-amber-300">
          {expired
            ? `Połącz ponownie konto ${name}, aby zobaczyć te dane.`
            : `Połącz konto ${name} w ustawieniach, aby zobaczyć te dane.`}
        </p>
      </div>
      <Link to="/settings" className="btn btn-primary no-underline">
        {expired ? "Połącz ponownie" : `Połącz z ${name}`}
      </Link>
    </div>
  );
}
