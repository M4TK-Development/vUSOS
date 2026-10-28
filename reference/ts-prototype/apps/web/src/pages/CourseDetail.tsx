import { useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  ArrowLeft, Book, CheckCircle2, ChevronDown, ClipboardCheck, ExternalLink, File as FileIcon, FileText, Folder, HelpCircle,
  Link2, MessageSquare, GraduationCap, Type, type LucideIcon,
} from "lucide-react";
import type { LmsAssignment, LmsModule, LmsModuleType, LmsSection } from "@vusos/shared";
import { openPlatonFileAuthed, platonFileUrl } from "../api/client";
import { useAssignments, useCourseContents, usePlatonCourses } from "../api/hooks";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SafeHtml } from "../components/SafeHtml";
import { SkeletonLines } from "../components/Skeleton";
import { Tabs } from "../components/Tabs";
import { formatBytes } from "../lib/format";
import { formatDateTime, fromNow, isPast } from "../lib/time";
import { isNative } from "../platform";
import { toast } from "../toast";

const MODULE_ICONS: Record<LmsModuleType, LucideIcon> = {
  resource: FileIcon,
  folder: Folder,
  url: Link2,
  page: FileText,
  book: Book,
  assign: ClipboardCheck,
  quiz: HelpCircle,
  forum: MessageSquare,
  label: Type,
  lesson: GraduationCap,
  other: FileIcon,
};

export function AssignmentBadges({ a }: { a: Pick<LmsAssignment, "dueDate" | "submitted" | "graded" | "grade"> }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      {a.dueDate && (
        <span className={`badge ${!a.submitted && isPast(a.dueDate) ? "badge-red" : "badge-gray"}`}>
          termin: {formatDateTime(a.dueDate)}
        </span>
      )}
      <span className={`badge ${a.submitted ? "badge-green" : "badge-amber"}`}>{a.submitted ? "oddane" : "nie oddane"}</span>
      {a.graded && <span className="badge badge-brand">ocena: {a.grade ?? "—"}</span>}
    </span>
  );
}

function FileLink({ file }: { file: LmsModule["files"][number] }) {
  const native = isNative();
  return (
    <a
      href={platonFileUrl(file.url)}
      target="_blank"
      rel="noreferrer"
      className="flex items-center gap-2 rounded-md px-2 py-1.5 text-xs no-underline hover:bg-slate-100 dark:hover:bg-slate-800"
      onClick={(e) => {
        if (native) {
          e.preventDefault();
          openPlatonFileAuthed(file.url, file.name).catch((err: unknown) => toast(err instanceof Error ? err.message : "Błąd pobierania.", "error"));
        }
      }}
    >
      <FileIcon size={14} aria-hidden className="shrink-0" />
      <span className="min-w-0 flex-1 truncate">{file.name}</span>
      <span className="muted shrink-0">{formatBytes(file.size)}</span>
      <ExternalLink size={12} aria-hidden className="shrink-0 opacity-60" />
    </a>
  );
}

function ModuleRow({ m, assignment }: { m: LmsModule; assignment?: LmsAssignment }) {
  const Icon = MODULE_ICONS[m.type] ?? FileIcon;
  if (m.type === "label") {
    return m.descriptionHtml ? <li className="py-2"><SafeHtml html={m.descriptionHtml} className="muted text-xs" /></li> : null;
  }
  return (
    <li className="py-2">
      <div className="flex items-start gap-2.5">
        <Icon size={17} aria-hidden className="mt-0.5 shrink-0 text-brand-600 dark:text-brand-500" />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
            {m.url && m.files.length === 0 ? (
              <a href={m.url} target="_blank" rel="noreferrer" className="font-medium">{m.name}</a>
            ) : (
              <span className="font-medium">{m.name}</span>
            )}
            {m.completed && <CheckCircle2 size={14} className="text-emerald-600" aria-label="Ukończone" />}
            {!m.visible && <span className="badge badge-gray">ukryte</span>}
          </div>
          {assignment && <div className="mt-1"><AssignmentBadges a={assignment} /></div>}
          {m.descriptionHtml && <SafeHtml html={m.descriptionHtml} className="muted mt-1 text-xs" />}
          {m.files.length > 0 && <div className="mt-1 space-y-0.5">{m.files.map((f) => <FileLink key={f.url} file={f} />)}</div>}
        </div>
      </div>
    </li>
  );
}

function Section({ s, assignments, defaultOpen }: { s: LmsSection; assignments: LmsAssignment[]; defaultOpen: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  const match = (m: LmsModule) =>
    m.type === "assign" ? assignments.find((a) => a.id === m.id || a.name === m.name) : undefined;
  return (
    <section className="card !p-0">
      <h3>
        <button type="button" className="flex w-full items-center justify-between gap-2 px-4 py-3 text-left font-semibold" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
          <span>{s.name || "Sekcja"}</span>
          <span className="flex items-center gap-2">
            <span className="muted text-xs font-normal">{s.modules.length}</span>
            <ChevronDown size={16} aria-hidden className={`transition ${open ? "rotate-180" : ""}`} />
          </span>
        </button>
      </h3>
      {open && (
        <div className="border-t border-slate-100 px-4 pb-2 dark:border-slate-800">
          {s.summaryHtml && <SafeHtml html={s.summaryHtml} className="muted pt-2 text-xs" />}
          {s.modules.length === 0 ? (
            <p className="muted py-3 text-xs">Pusta sekcja.</p>
          ) : (
            <ul className="divide-y divide-slate-100 dark:divide-slate-800">
              {s.modules.map((m) => <ModuleRow key={m.id} m={m} assignment={match(m)} />)}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}

function Contents({ id }: { id: string }) {
  const contents = useCourseContents(id);
  const assignments = useAssignments();
  const courseAssignments = (assignments.data ?? []).filter((a) => a.courseId === id);
  return (
    <QueryBoundary
      query={contents}
      system="platon"
      skeleton={<SkeletonLines rows={8} />}
      isEmpty={(d) => d.length === 0}
      empty={<div className="card"><EmptyState title="Brak materiałów w kursie" /></div>}
    >
      {(sections) => (
        <div className="space-y-2">
          {sections.map((s, i) => <Section key={s.id} s={s} assignments={courseAssignments} defaultOpen={i === 0} />)}
        </div>
      )}
    </QueryBoundary>
  );
}

function Tasks({ id }: { id: string }) {
  const query = useAssignments();
  return (
    <QueryBoundary
      query={query}
      system="platon"
      skeleton={<SkeletonLines rows={5} />}
      isEmpty={(d) => d.filter((a) => a.courseId === id).length === 0}
      empty={<div className="card"><EmptyState title="Brak zadań w tym kursie" /></div>}
    >
      {(all) => (
        <ul className="card divide-y divide-slate-100 dark:divide-slate-800">
          {all
            .filter((a) => a.courseId === id)
            .sort((a, b) => (a.dueDate ?? "9").localeCompare(b.dueDate ?? "9"))
            .map((a) => (
              <li key={a.id} className="space-y-1 py-2.5">
                <a href={a.url} target="_blank" rel="noreferrer" className="font-medium">{a.name}</a>
                <div><AssignmentBadges a={a} /></div>
                {a.dueDate && !a.submitted && <p className="muted text-[11px]">{fromNow(a.dueDate)}</p>}
              </li>
            ))}
        </ul>
      )}
    </QueryBoundary>
  );
}

export function CourseDetail() {
  const { id } = useParams<{ id: string }>();
  const [tab, setTab] = useState<"materialy" | "zadania">("materialy");
  const courses = usePlatonCourses();
  const course = courses.data?.find((c) => c.id === id);
  if (!id) return null;
  return (
    <>
      <Link to="/kursy" className="mb-2 inline-flex items-center gap-1 text-xs"><ArrowLeft size={14} aria-hidden /> Wszystkie kursy</Link>
      <PageHeader title={course?.fullName ?? "Kurs"} subtitle={course?.shortName} actions={course?.url ? <a className="btn no-underline" href={course.url} target="_blank" rel="noreferrer"><ExternalLink size={14} aria-hidden /> Platon</a> : undefined} />
      <Tabs tabs={[{ key: "materialy", label: "Materiały" }, { key: "zadania", label: "Zadania" }]} active={tab} onChange={setTab} />
      {tab === "materialy" ? <Contents id={id} /> : <Tasks id={id} />}
    </>
  );
}
