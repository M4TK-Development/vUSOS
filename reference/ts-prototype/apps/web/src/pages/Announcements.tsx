import { useMemo } from "react";
import type { Announcement } from "@vusos/shared";
import { usePlatonAnnouncements, useUsosAnnouncements } from "../api/hooks";
import { isApiError } from "../api/client";
import { ConnectPrompt } from "../components/ConnectPrompt";
import { EmptyState } from "../components/EmptyState";
import { ErrorState } from "../components/ErrorState";
import { PageHeader } from "../components/PageHeader";
import { SafeHtml } from "../components/SafeHtml";
import { SkeletonLines } from "../components/Skeleton";
import { SourceBadge } from "../components/SourceBadge";
import { formatDate } from "../lib/time";

function Item({ a }: { a: Announcement }) {
  return (
    <article className="card">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        <SourceBadge source={a.source} />
        {a.category && <span className="badge badge-gray">{a.category}</span>}
        <span className="muted text-xs">{formatDate(a.publishedAt)}{a.author ? ` · ${a.author}` : ""}</span>
      </div>
      <h2 className="font-semibold">{a.title}</h2>
      <SafeHtml html={a.bodyHtml} className="mt-1" />
      {a.url && <a href={a.url} target="_blank" rel="noreferrer" className="mt-2 inline-block text-xs">Otwórz w systemie →</a>}
    </article>
  );
}

export function Announcements() {
  const usos = useUsosAnnouncements();
  const platon = usePlatonAnnouncements();

  const merged = useMemo(
    () => [...(usos.data ?? []), ...(platon.data ?? [])].sort((a, b) => b.publishedAt.localeCompare(a.publishedAt)),
    [usos.data, platon.data],
  );

  const loading = usos.isPending || platon.isPending;
  const failures = [
    { system: "usos" as const, q: usos },
    { system: "platon" as const, q: platon },
  ].filter((x) => x.q.isError);

  return (
    <>
      <PageHeader title="Ogłoszenia" />
      <div className="mb-3 space-y-2">
        {failures.map(({ system, q }) =>
          isApiError(q.error) && q.error.needsConnection ? (
            <ConnectPrompt key={system} system={q.error.system ?? system} expired={q.error.expired} compact />
          ) : (
            <ErrorState key={system} error={q.error} onRetry={() => void q.refetch()} />
          ),
        )}
      </div>
      {loading && merged.length === 0 ? (
        <SkeletonLines rows={8} />
      ) : merged.length === 0 ? (
        failures.length === 2 ? null : <div className="card"><EmptyState title="Brak ogłoszeń" /></div>
      ) : (
        <div className="space-y-3">{merged.map((a) => <Item key={`${a.source}-${a.id}`} a={a} />)}</div>
      )}
    </>
  );
}
