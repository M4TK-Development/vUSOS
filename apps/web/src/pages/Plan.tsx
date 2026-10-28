import { useMemo, useState } from "react";
import { ChevronLeft, ChevronRight, Flag, MapPin, User } from "lucide-react";
import type { LmsEvent, TimetableEntry } from "@vusos/shared";
import { usePlatonEvents, useTimetable } from "../api/hooks";
import { Drawer } from "../components/Drawer";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SkeletonCards } from "../components/Skeleton";
import { CLASS_BADGE, classKind, classTypeLabel, pick } from "../lib/format";
import { addDaysKey, dayKey, diffDays, formatDateTime, labelDay, labelRange, mondayOf, timeOf, todayKey, weekKeys } from "../lib/time";

function EntryCard({ e, onOpen }: { e: TimetableEntry; onOpen: (e: TimetableEntry) => void }) {
  const kind = classKind(e.classType);
  return (
    <button
      type="button"
      onClick={() => onOpen(e)}
      className="block w-full rounded-lg border border-slate-200 bg-white p-2 text-left transition hover:border-brand-500 dark:border-slate-800 dark:bg-slate-900"
    >
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="font-mono text-[11px] font-semibold">{timeOf(e.start)}–{timeOf(e.end)}</span>
        {e.classType && <span className={`badge ${CLASS_BADGE[kind]}`}>{classTypeLabel(e.classType)}</span>}
      </div>
      <p className="mt-1 text-[13px] font-medium leading-snug">{pick(e.courseName)}</p>
      <p className="muted mt-0.5 text-[11px] leading-snug">
        {[e.room && `sala ${e.room}`, e.lecturers.join(", ")].filter(Boolean).join(" · ")}
      </p>
    </button>
  );
}

function EventPill({ ev }: { ev: LmsEvent }) {
  const body = (
    <>
      <Flag size={11} aria-hidden className="shrink-0" />
      <span className="truncate">{timeOf(ev.start)} {ev.name}</span>
    </>
  );
  const cls = "flex items-center gap-1 rounded-full bg-amber-100 px-2 py-0.5 text-[11px] font-medium text-amber-900 no-underline dark:bg-amber-950 dark:text-amber-200";
  return ev.url ? (
    <a href={ev.url} target="_blank" rel="noreferrer" className={cls} title={`${ev.name} — ${ev.courseName ?? ""}`}>{body}</a>
  ) : (
    <span className={cls} title={`${ev.name} — ${ev.courseName ?? ""}`}>{body}</span>
  );
}

function DayColumn({
  day, entries, events, isToday, onOpen,
}: { day: string; entries: TimetableEntry[]; events: LmsEvent[]; isToday: boolean; onOpen: (e: TimetableEntry) => void }) {
  const empty = entries.length === 0 && events.length === 0;
  return (
    <section
      aria-label={labelDay(day, "EEEE d MMMM")}
      className={`min-w-0 rounded-xl border p-2 ${isToday ? "border-brand-500 bg-brand-50/50 dark:bg-indigo-950/30" : "border-slate-200 dark:border-slate-800"} ${empty && !isToday ? "hidden md:block" : ""}`}
    >
      <h3 className={`mb-2 text-xs font-semibold capitalize ${isToday ? "text-brand-700 dark:text-indigo-300" : ""}`}>
        {labelDay(day, "EEEE d MMM")}
      </h3>
      <div className="space-y-1.5">
        {events.map((ev) => <EventPill key={ev.id} ev={ev} />)}
        {entries.map((e) => <EntryCard key={e.id} e={e} onOpen={onOpen} />)}
        {empty && <p className="muted py-1 text-center text-[11px]">brak zajęć</p>}
      </div>
    </section>
  );
}

function EntryDetails({ e }: { e: TimetableEntry }) {
  const rows: [string, string | null][] = [
    ["Termin", `${formatDateTime(e.start)} – ${timeOf(e.end)}`],
    ["Typ", e.classType ? classTypeLabel(e.classType) : null],
    ["Grupa", e.groupNumber != null ? String(e.groupNumber) : null],
    ["Budynek", e.building],
    ["Sala", e.room],
  ];
  return (
    <div className="space-y-3">
      <p className="text-base font-semibold">{pick(e.courseName)}</p>
      <dl className="grid grid-cols-[6rem_1fr] gap-y-1.5 text-sm">
        {rows.filter(([, v]) => v).map(([k, v]) => (
          <div key={k} className="contents">
            <dt className="muted">{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
        {e.lecturers.length > 0 && (
          <>
            <dt className="muted">Prowadzący</dt>
            <dd className="flex items-start gap-1"><User size={14} className="mt-0.5 shrink-0" aria-hidden />{e.lecturers.join(", ")}</dd>
          </>
        )}
      </dl>
      {(e.room || e.building) && (
        <p className="muted flex items-center gap-1 text-xs"><MapPin size={12} aria-hidden /> {[e.building, e.room && `sala ${e.room}`].filter(Boolean).join(", ")}</p>
      )}
      {e.url && <a className="btn no-underline" href={e.url} target="_blank" rel="noreferrer">Otwórz w systemie</a>}
    </div>
  );
}

export function Plan() {
  const today = todayKey();
  const [monday, setMonday] = useState(() => mondayOf(today));
  const [showEvents, setShowEvents] = useState(true);
  const [selected, setSelected] = useState<TimetableEntry | null>(null);

  const days = useMemo(() => weekKeys(monday), [monday]);
  const sunday = days[6] ?? monday;
  const timetable = useTimetable(monday, sunday);

  // The Platon events endpoint looks forward from now; skip it for weeks entirely in the past.
  const eventDays = Math.max(1, diffDays(today, sunday) + 1);
  const eventsEnabled = showEvents && diffDays(today, sunday) >= 0;
  const events = usePlatonEvents(eventDays, eventsEnabled);

  const eventsByDay = useMemo(() => {
    const m = new Map<string, LmsEvent[]>();
    if (!showEvents) return m;
    for (const ev of events.data ?? []) {
      const k = dayKey(ev.start);
      m.set(k, [...(m.get(k) ?? []), ev]);
    }
    return m;
  }, [events.data, showEvents]);

  return (
    <>
      <PageHeader
        title="Plan zajęć"
        subtitle={labelRange(monday, sunday)}
        actions={
          <>
            <button type="button" className="btn !px-2" onClick={() => setMonday((m) => addDaysKey(m, -7))} aria-label="Poprzedni tydzień">
              <ChevronLeft size={16} aria-hidden />
            </button>
            <button type="button" className="btn" onClick={() => setMonday(mondayOf(today))}>Dziś</button>
            <button type="button" className="btn !px-2" onClick={() => setMonday((m) => addDaysKey(m, 7))} aria-label="Następny tydzień">
              <ChevronRight size={16} aria-hidden />
            </button>
          </>
        }
      />
      <label className="mb-3 inline-flex cursor-pointer items-center gap-2 text-xs">
        <input type="checkbox" checked={showEvents} onChange={(e) => setShowEvents(e.target.checked)} />
        Pokaż terminy z Platona
      </label>
      {showEvents && events.isError && <p className="mb-2 text-xs text-amber-700 dark:text-amber-400">Terminy z Platona są niedostępne: {events.error.message}</p>}

      <QueryBoundary query={timetable} system="usos" skeleton={<SkeletonCards count={6} className="h-32" />}>
        {(entries) => {
          const byDay = new Map<string, TimetableEntry[]>();
          for (const e of [...entries].sort((a, b) => a.start.localeCompare(b.start))) {
            const k = dayKey(e.start);
            byDay.set(k, [...(byDay.get(k) ?? []), e]);
          }
          const total = entries.length;
          return (
            <>
              {total === 0 && <EmptyState title="Brak zajęć w tym tygodniu" />}
              <div className="grid gap-2 md:grid-cols-7">
                {days.map((d) => (
                  <DayColumn key={d} day={d} entries={byDay.get(d) ?? []} events={eventsByDay.get(d) ?? []} isToday={d === today} onOpen={setSelected} />
                ))}
              </div>
            </>
          );
        }}
      </QueryBoundary>

      <Drawer open={!!selected} title="Szczegóły zajęć" onClose={() => setSelected(null)}>
        {selected && <EntryDetails e={selected} />}
      </Drawer>
    </>
  );
}
