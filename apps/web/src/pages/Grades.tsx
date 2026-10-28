import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import type { Grade, Term } from "@vusos/shared";
import { useCourseGrades, useGrades, usePlatonCourses, useTerms } from "../api/hooks";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SafeHtml } from "../components/SafeHtml";
import { SkeletonLines } from "../components/Skeleton";
import { Tabs } from "../components/Tabs";
import { formatAverage, pick } from "../lib/format";
import { computeAverage, groupByCourse } from "../lib/grades";
import { formatDate } from "../lib/time";

export function GradeChip({ g }: { g: Grade }) {
  const cls = g.passes === true ? "badge-green" : g.passes === false ? "badge-red" : "badge-gray";
  return (
    <span className={`badge ${cls} !px-2.5 !py-1 text-sm`} title={pick(g.valueDescription) || undefined}>
      {g.valueSymbol}
    </span>
  );
}

function TermGradesView({ termId }: { termId: string }) {
  const query = useGrades(termId);
  return (
    <QueryBoundary
      query={query}
      system="usos"
      skeleton={<SkeletonLines rows={6} />}
      isEmpty={(d) => d.grades.length === 0}
      empty={<div className="card"><EmptyState title="Brak ocen w tym semestrze" /></div>}
    >
      {(d) => {
        const avg = computeAverage(d.grades);
        const groups = groupByCourse(d.grades, (g) => pick(g.courseName));
        return (
          <div className="space-y-4">
            <div className="card flex items-center justify-between">
              <div>
                <p className="muted text-xs">Średnia w semestrze</p>
                <p className="text-3xl font-bold tabular-nums" data-testid="term-average">{formatAverage(avg)}</p>
              </div>
              <p className="muted max-w-[14rem] text-right text-[11px]">
                Liczona z ocen liczbowych wliczanych do średniej (bez wag ECTS).
              </p>
            </div>
            <div className="grid gap-3 md:grid-cols-2">
              {groups.map((grp) => (
                <section key={grp.courseId} className="card">
                  <h3 className="mb-2 font-semibold">{grp.name}</h3>
                  <ul className="divide-y divide-slate-100 dark:divide-slate-800">
                    {grp.grades.map((g) => (
                      <li key={g.id} className="flex items-center justify-between gap-3 py-2">
                        <div className="min-w-0 text-xs">
                          <p className="font-medium">
                            {[g.classType, g.examSession != null && `termin ${g.examSession}`].filter(Boolean).join(" · ") || "Ocena"}
                            {!g.countsIntoAverage && <span className="badge badge-gray ml-2">poza średnią</span>}
                          </p>
                          <p className="muted">{[formatDate(g.dateModified), g.gradedBy].filter((x) => x && x !== "—").join(" · ")}</p>
                          {g.comment && <p className="muted mt-0.5 italic">{g.comment}</p>}
                        </div>
                        <GradeChip g={g} />
                      </li>
                    ))}
                  </ul>
                </section>
              ))}
            </div>
          </div>
        );
      }}
    </QueryBoundary>
  );
}

function PlatonCourseGrades({ courseId }: { courseId: string }) {
  const query = useCourseGrades(courseId);
  return (
    <QueryBoundary
      query={query}
      system="platon"
      skeleton={<SkeletonLines rows={4} />}
      isEmpty={(d) => d.length === 0}
      empty={<EmptyState title="Brak ocen w tym kursie" />}
    >
      {(items) => (
        <ul className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((it) => (
            <li key={it.id} className="flex items-start justify-between gap-3 py-2 text-sm">
              <div className="min-w-0">
                <p className="font-medium">{it.itemName}</p>
                {it.feedbackHtml && <SafeHtml html={it.feedbackHtml} className="muted text-xs" />}
                {it.dateGraded && <p className="muted text-[11px]">{formatDate(it.dateGraded)}</p>}
              </div>
              <div className="shrink-0 text-right">
                <p className="font-semibold tabular-nums">{it.gradeFormatted ?? "—"}</p>
                {it.percentage && <p className="muted text-[11px]">{it.percentage}</p>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </QueryBoundary>
  );
}

function PlatonGrades() {
  const courses = usePlatonCourses();
  return (
    <QueryBoundary
      query={courses}
      system="platon"
      isEmpty={(d) => d.length === 0}
      empty={<div className="card"><EmptyState title="Brak kursów w Platonie" /></div>}
    >
      {(list) => (
        <div className="grid gap-3 md:grid-cols-2">
          {list.map((c) => (
            <section key={c.id} className="card">
              <h3 className="mb-1 font-semibold">
                <Link to={`/kursy/${c.id}`} className="!text-inherit hover:underline">{c.fullName}</Link>
              </h3>
              <PlatonCourseGrades courseId={c.id} />
            </section>
          ))}
        </div>
      )}
    </QueryBoundary>
  );
}

function pickDefaultTerm(terms: Term[]): string | undefined {
  return (terms.find((t) => t.isCurrent) ?? terms[0])?.id;
}

export function Grades() {
  const [tab, setTab] = useState<"usos" | "platon">("usos");
  const terms = useTerms();
  const [termId, setTermId] = useState<string | undefined>();

  useEffect(() => {
    if (termId === undefined && terms.data && terms.data.length > 0) setTermId(pickDefaultTerm(terms.data));
  }, [terms.data, termId]);

  return (
    <>
      <PageHeader
        title="Oceny"
        actions={
          tab === "usos" && terms.data && terms.data.length > 0 ? (
            <select className="input !w-auto" aria-label="Semestr" value={termId ?? ""} onChange={(e) => setTermId(e.target.value)}>
              {[...terms.data].sort((a, b) => b.startDate.localeCompare(a.startDate)).map((t) => (
                <option key={t.id} value={t.id}>{pick(t.name)}{t.isCurrent ? " (bieżący)" : ""}</option>
              ))}
            </select>
          ) : undefined
        }
      />
      <Tabs
        tabs={[{ key: "usos", label: "Oceny z USOS" }, { key: "platon", label: "Oceny z Platona" }]}
        active={tab}
        onChange={setTab}
      />
      {tab === "usos" ? (
        <QueryBoundary
          query={terms}
          system="usos"
          isEmpty={(t) => t.length === 0}
          empty={<div className="card"><EmptyState title="Brak semestrów" /></div>}
        >
          {() => (termId ? <TermGradesView termId={termId} /> : <SkeletonLines rows={6} />)}
        </QueryBoundary>
      ) : (
        <PlatonGrades />
      )}
    </>
  );
}
