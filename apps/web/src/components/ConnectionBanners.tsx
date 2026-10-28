import { Link } from "react-router-dom";
import { AlertCircle } from "lucide-react";
import type { ConnectionStatus, SourceSystem } from "@vusos/shared";
import { SYSTEM_NAMES } from "./ConnectPrompt";

export function ConnectionBanners({ connections }: { connections: Partial<Record<SourceSystem, ConnectionStatus>> | undefined }) {
  if (!connections) return null;
  const items = (["usos", "platon"] as const).flatMap((s) => {
    const st = connections[s];
    if (!st || st === "connected") return [];
    const expired = st === "expired" || st === "error";
    return [{ system: s, expired }];
  });
  if (items.length === 0) return null;
  return (
    <div className="mb-4 space-y-2">
      {items.map(({ system, expired }) => (
        <div
          key={system}
          role="status"
          className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-900 dark:border-amber-800 dark:bg-amber-950/40 dark:text-amber-200"
        >
          <AlertCircle size={16} aria-hidden />
          <span className="flex-1">
            {expired ? `Połączenie z systemem ${SYSTEM_NAMES[system]} wygasło.` : `System ${SYSTEM_NAMES[system]} nie jest połączony.`}
          </span>
          <Link to="/settings" className="font-semibold underline">
            {expired ? "Połącz ponownie" : "Połącz"}
          </Link>
        </div>
      ))}
    </div>
  );
}
