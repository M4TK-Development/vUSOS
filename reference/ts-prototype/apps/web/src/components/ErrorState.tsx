import { AlertTriangle, RotateCw } from "lucide-react";

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const message = error instanceof Error ? error.message : "Coś poszło nie tak.";
  return (
    <div role="alert" className="flex flex-col items-center gap-3 rounded-xl border border-red-200 bg-red-50 px-4 py-6 text-center dark:border-red-900 dark:bg-red-950/40">
      <AlertTriangle className="text-red-600 dark:text-red-400" size={26} aria-hidden />
      <div>
        <p className="font-medium text-red-800 dark:text-red-300">Nie udało się wczytać danych</p>
        <p className="mt-1 text-xs text-red-700 dark:text-red-400">{message}</p>
      </div>
      {onRetry && (
        <button type="button" className="btn" onClick={onRetry}>
          <RotateCw size={14} aria-hidden /> Spróbuj ponownie
        </button>
      )}
    </div>
  );
}
