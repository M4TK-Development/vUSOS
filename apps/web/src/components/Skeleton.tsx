export function Skeleton({ className = "" }: { className?: string }) {
  return <div aria-hidden className={`animate-pulse rounded-md bg-slate-200 dark:bg-slate-800 ${className}`} />;
}

export function SkeletonLines({ rows = 4, className = "" }: { rows?: number; className?: string }) {
  return (
    <div role="status" aria-label="Ładowanie" className={`space-y-2 ${className}`}>
      {Array.from({ length: rows }, (_, i) => (
        <Skeleton key={i} className={`h-4 ${i % 3 === 2 ? "w-2/3" : "w-full"}`} />
      ))}
    </div>
  );
}

export function SkeletonCards({ count = 3, className = "h-24" }: { count?: number; className?: string }) {
  return (
    <div role="status" aria-label="Ładowanie" className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
      {Array.from({ length: count }, (_, i) => (
        <Skeleton key={i} className={className} />
      ))}
    </div>
  );
}
