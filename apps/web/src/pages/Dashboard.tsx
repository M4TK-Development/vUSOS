import { Link } from "react-router-dom";
import { AlarmClock, CalendarClock, GraduationCap, Mail, Megaphone, Wallet } from "lucide-react";
import type { DashboardData } from "@vusos/shared";
import { useDashboard } from "../api/hooks";
import { ConnectionBanners } from "../components/ConnectionBanners";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SafeHtml } from "../components/SafeHtml";
import { SkeletonCards } from "../components/Skeleton";
import { SourceBadge } from "../components/SourceBadge";
import { CLASS_BADGE, classKind, classTypeLabel, formatMoney, pick, pluralPl } from "../lib/format";
import { formatDate, formatDateTime, fromNow, isPast, labelDay, timeOf, todayKey } from "../lib/time";

function TodayClasses({ items }: { items: DashboardData["todayClasses"] }) {
  const sorted = [...items].sort((a, b) => a.start.localeCompare(b.start));
  return (
    <section className="card" aria-labelledby="h-today">
      <h2 id="h-today" className="card-title"><CalendarClock size={16} aria-hidden /> Dzisiejsze zajęcia</h2>
      {sorted.length === 0 ? (
        <EmptyState title="Dziś bez zajęć" hint="Miłego dnia!" />
      ) : (
        <ol className="relative space-y-3 border-l-2 border-slate-200 pl-4 dark:border-slate-800">
          {sorted.map((e) => {
            const now = Date.now();
            const live = new Date(e.start).getTime() <= now && now < new Date(e.end).getTime();
            return (
              <li key={e.id} className="relative">
                <span className={`absolute -left-[22px] top-1.5 h-2.5 w-2.5 rounded-full ${live ? "bg-emerald-500" : "bg-slate-400"}`} aria-hidden />
                <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
                  <span className="font-mono text-xs font-semibold">{timeOf(e.start)}–{timeOf(e.end)}</span>
                  {e.classType && <span className={`badge ${CLASS_BADGE[classKind(e.classType)]}`}>{classTypeLabel(e.classType)}</span>}
                  {live && <span className="badge badge-green">trwa</span>}
                </div>
                <p className="font-medium">{pick(e.courseName)}</p>
                <p className="muted text-xs">
                  {[e.room && `sala ${e.room}`, e.building, e.lecturers.join(", ")].filter(Boolean).join(" · ")}
                </p>
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}

function Deadlines({ items }: { items: DashboardData["upcomingDeadlines"] }) {
  return (
    <section className="card" aria-labelledby="h-deadlines">
      <h2 id="h-deadlines" className="card-title"><AlarmClock size={16} aria-hidden /> Nadchodzące terminy (Platon)</h2>
      {items.length === 0 ? (
        <EmptyState title="Brak nadchodzących terminów" />
      ) : (
        <ul className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((ev) => (
            <li key={ev.id} className="flex items-start justify-between gap-3 py-2">
              <div className="min-w-0">
                <p className="truncate font-medium">{ev.url ? <a href={ev.url} target="_blank" rel="noreferrer">{ev.name}</a> : ev.name}</p>
                <p className="muted truncate text-xs">{ev.courseName ?? "—"}</p>
              </div>
              <div className="shrink-0 text-right text-xs">
                <p className="font-medium">{formatDateTime(ev.start)}</p>
                <p className="muted">{fromNow(ev.start)}</p>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function RecentGrades({ items }: { items: DashboardData["recentGrades"] }) {
  return (
    <section className="card" aria-labelledby="h-grades">
      <h2 id="h-grades" className="card-title"><GraduationCap size={16} aria-hidden /> Ostatnie oceny</h2>
      {items.length === 0 ? (
        <EmptyState title="Brak ocen" />
      ) : (
        <ul className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((g) => (
            <li key={g.id} className="flex items-center justify-between gap-3 py-2">
              <div className="min-w-0">
                <p className="truncate font-medium">{pick(g.courseName)}</p>
                <p className="muted text-xs">{[g.classType, formatDate(g.dateModified)].filter(Boolean).join(" · ")}</p>
              </div>
              <span
                className={`badge text-sm ${g.passes === true ? "badge-green" : g.passes === false ? "badge-red" : "badge-gray"}`}
                title={pick(g.valueDescription)}
              >
                {g.valueSymbol}
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function PaymentsDue({ items }: { items: DashboardData["paymentsDue"] }) {
  return (
    <section className="card" aria-labelledby="h-pay">
      <h2 id="h-pay" className="card-title"><Wallet size={16} aria-hidden /> Płatności do uregulowania</h2>
      {items.length === 0 ? (
        <EmptyState title="Brak zaległych płatności" />
      ) : (
        <ul className="divide-y divide-slate-100 dark:divide-slate-800">
          {items.map((p) => {
            const overdue = p.state === "overdue" || (p.dueDate != null && isPast(p.dueDate) && p.state !== "paid");
            return (
              <li key={p.id} className={`flex items-center justify-between gap-3 px-1 py-2 ${overdue ? "rounded bg-red-50 dark:bg-red-950/30" : ""}`}>
                <div className="min-w-0">
                  <p className="truncate font-medium">{pick(p.description) || p.type}</p>
                  <p className={`text-xs ${overdue ? "font-medium text-red-700 dark:text-red-400" : "muted"}`}>
                    {overdue ? "Po terminie: " : "Termin: "}{formatDate(p.dueDate)}
                  </p>
                </div>
                <span className="shrink-0 font-semibold tabular-nums">{formatMoney(p.amount - p.paid, p.currency)}</span>
              </li>
            );
          })}
        </ul>
      )}
      <div className="mt-2 text-right text-xs"><Link to="/platnosci">Wszystkie płatności →</Link></div>
    </section>
  );
}

function UnreadCard({ count }: { count: number }) {
  return (
    <Link to="/wiadomosci" className="card flex items-center gap-3 no-underline hover:border-brand-500">
      <div className="rounded-full bg-brand-100 p-2.5 text-brand-700 dark:bg-indigo-950 dark:text-indigo-300"><Mail size={20} aria-hidden /></div>
      <div className="text-slate-900 dark:text-slate-100">
        <p className="text-2xl font-bold leading-none">{count}</p>
        <p className="muted mt-1 text-xs">
          nieprzeczytan{count === 1 ? "a" : "ych"} {pluralPl(count, "wiadomość", "wiadomości", "wiadomości")}
        </p>
      </div>
    </Link>
  );
}

function AnnouncementsFeed({ items }: { items: DashboardData["announcements"] }) {
  const sorted = [...items].sort((a, b) => b.publishedAt.localeCompare(a.publishedAt)).slice(0, 6);
  return (
    <section className="card" aria-labelledby="h-ann">
      <h2 id="h-ann" className="card-title"><Megaphone size={16} aria-hidden /> Ogłoszenia</h2>
      {sorted.length === 0 ? (
        <EmptyState title="Brak ogłoszeń" />
      ) : (
        <ul className="space-y-3">
          {sorted.map((a) => (
            <li key={`${a.source}-${a.id}`}>
              <div className="flex items-center gap-2">
                <SourceBadge source={a.source} />
                <span className="muted text-xs">{formatDate(a.publishedAt)}</span>
              </div>
              <p className="font-medium">{a.title}</p>
              <SafeHtml html={a.bodyHtml} className="muted line-clamp-3 text-xs" />
            </li>
          ))}
        </ul>
      )}
      <div className="mt-2 text-right text-xs"><Link to="/ogloszenia">Wszystkie ogłoszenia →</Link></div>
    </section>
  );
}

export function Dashboard() {
  const query = useDashboard();
  const today = labelDay(todayKey(), "EEEE, d MMMM");
  return (
    <>
      <PageHeader title="Start" subtitle={today} />
      <QueryBoundary query={query} skeleton={<SkeletonCards count={4} className="h-40" />}>
        {(d) => (
          <>
            <ConnectionBanners connections={d.connections} />
            <div className="grid gap-4 lg:grid-cols-2">
              <TodayClasses items={d.todayClasses} />
              <Deadlines items={d.upcomingDeadlines} />
              <RecentGrades items={d.recentGrades} />
              <PaymentsDue items={d.paymentsDue} />
              <UnreadCard count={d.unreadMessages} />
              <div className="lg:col-span-2"><AnnouncementsFeed items={d.announcements} /></div>
            </div>
          </>
        )}
      </QueryBoundary>
    </>
  );
}
