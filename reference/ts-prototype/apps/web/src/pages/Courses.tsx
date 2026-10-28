import { Link } from "react-router-dom";
import { BookOpen } from "lucide-react";
import type { LmsCourse } from "@vusos/shared";
import { usePlatonCourses } from "../api/hooks";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SkeletonCards } from "../components/Skeleton";
import { fromNow } from "../lib/time";

export function ProgressBar({ value }: { value: number | null }) {
  if (value == null) return <p className="muted text-[11px]">Brak danych o postępie</p>;
  const pct = Math.max(0, Math.min(100, Math.round(value)));
  return (
    <div>
      <div className="mb-1 flex justify-between text-[11px]"><span className="muted">Postęp</span><span className="font-medium">{pct}%</span></div>
      <div className="h-1.5 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-800" role="progressbar" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}>
        <div className="h-full rounded-full bg-brand-600" style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

function CourseCard({ c }: { c: LmsCourse }) {
  return (
    <Link to={`/kursy/${c.id}`} className="card flex flex-col gap-3 no-underline transition hover:border-brand-500">
      <div className="flex items-start gap-3 text-slate-900 dark:text-slate-100">
        <div className="rounded-lg bg-brand-100 p-2 text-brand-700 dark:bg-indigo-950 dark:text-indigo-300"><BookOpen size={18} aria-hidden /></div>
        <div className="min-w-0">
          <p className="font-semibold leading-snug">{c.fullName}</p>
          <p className="muted truncate text-xs">{[c.shortName, c.category].filter(Boolean).join(" · ")}</p>
        </div>
      </div>
      <div className="mt-auto text-slate-900 dark:text-slate-100">
        <ProgressBar value={c.progress} />
        {c.lastAccess && <p className="muted mt-1.5 text-[11px]">Ostatni dostęp: {fromNow(c.lastAccess)}</p>}
      </div>
    </Link>
  );
}

export function Courses() {
  const query = usePlatonCourses();
  return (
    <>
      <PageHeader title="Kursy (Platon)" />
      <QueryBoundary
        query={query}
        system="platon"
        skeleton={<SkeletonCards count={6} className="h-32" />}
        isEmpty={(d) => d.length === 0}
        empty={<div className="card"><EmptyState title="Brak kursów" hint="Nie jesteś zapisany na żaden kurs w Platonie." /></div>}
      >
        {(list) => (
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {list.map((c) => <CourseCard key={c.id} c={c} />)}
          </div>
        )}
      </QueryBoundary>
    </>
  );
}
