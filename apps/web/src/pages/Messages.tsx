import { useState, type FormEvent } from "react";
import { ArrowLeft, Mail, MailOpen, PenSquare, Paperclip, Phone, MapPin, Clock } from "lucide-react";
import type { MessageThread } from "@vusos/shared";
import { useContacts, useMessage, useMessages, useSendMessage } from "../api/hooks";
import { Dialog } from "../components/Dialog";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SafeHtml } from "../components/SafeHtml";
import { SkeletonLines } from "../components/Skeleton";
import { formatDateTime } from "../lib/time";
import { formatBytes } from "../lib/format";
import { toast } from "../toast";

function ComposeDialog({ open, onClose, initialTo = "" }: { open: boolean; onClose: () => void; initialTo?: string }) {
  const send = useSendMessage();
  const [to, setTo] = useState(initialTo);
  const [subject, setSubject] = useState("");
  const [body, setBody] = useState("");

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const recipients = to.split(/[,;\s]+/).map((s) => s.trim()).filter(Boolean);
    if (recipients.length === 0) return;
    send.mutate(
      { to: recipients, subject, body },
      {
        onSuccess: () => {
          toast("Wiadomość wysłana.", "success");
          setSubject("");
          setBody("");
          setTo("");
          onClose();
        },
      },
    );
  };

  return (
    <Dialog open={open} title="Nowa wiadomość" onClose={onClose}>
      <form onSubmit={submit} className="space-y-3">
        <label className="block text-xs font-medium">
          Do (adresy lub identyfikatory, oddzielone przecinkami)
          <input className="input mt-1" value={to} onChange={(e) => setTo(e.target.value)} required />
        </label>
        <label className="block text-xs font-medium">
          Temat
          <input className="input mt-1" value={subject} onChange={(e) => setSubject(e.target.value)} required />
        </label>
        <label className="block text-xs font-medium">
          Treść
          <textarea className="input mt-1 min-h-40" value={body} onChange={(e) => setBody(e.target.value)} required />
        </label>
        {send.isError && <p role="alert" className="text-xs text-red-600 dark:text-red-400">{send.error.message}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" className="btn" onClick={onClose}>Anuluj</button>
          <button type="submit" className="btn btn-primary" disabled={send.isPending}>{send.isPending ? "Wysyłanie…" : "Wyślij"}</button>
        </div>
      </form>
    </Dialog>
  );
}

function MessageDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const query = useMessage(id);
  return (
    <div className="card">
      <button type="button" className="btn btn-ghost mb-2 !px-1 md:hidden" onClick={onBack}>
        <ArrowLeft size={16} aria-hidden /> Wróć do listy
      </button>
      <QueryBoundary query={query} system="usos" skeleton={<SkeletonLines rows={8} />}>
        {(m) => (
          <article>
            <h2 className="text-lg font-semibold">{m.subject}</h2>
            <p className="muted mb-3 text-xs">
              Od: {m.from} · Do: {m.to.join(", ")} · {formatDateTime(m.date)}
            </p>
            <SafeHtml html={m.bodyHtml} />
            {m.attachments.length > 0 && (
              <ul className="mt-4 space-y-1 border-t border-slate-200 pt-3 text-xs dark:border-slate-800">
                {m.attachments.map((a) => (
                  <li key={a.url} className="flex items-center gap-1.5">
                    <Paperclip size={12} aria-hidden />
                    <a href={a.url} target="_blank" rel="noreferrer">{a.name}</a>
                    <span className="muted">{formatBytes(a.size)}</span>
                  </li>
                ))}
              </ul>
            )}
          </article>
        )}
      </QueryBoundary>
    </div>
  );
}

function ThreadList({ items, selected, onSelect }: { items: MessageThread[]; selected: string | null; onSelect: (id: string) => void }) {
  return (
    <ul className="card divide-y divide-slate-100 !p-0 dark:divide-slate-800">
      {items.map((t) => (
        <li key={t.id}>
          <button
            type="button"
            onClick={() => onSelect(t.id)}
            className={`flex w-full items-start gap-2.5 px-3 py-2.5 text-left hover:bg-slate-50 dark:hover:bg-slate-800/60 ${selected === t.id ? "bg-brand-50 dark:bg-indigo-950/40" : ""}`}
          >
            <span className="mt-0.5 shrink-0 text-brand-600 dark:text-brand-500">
              {t.unread ? <Mail size={16} aria-label="Nieprzeczytana" /> : <MailOpen size={16} className="opacity-50" aria-label="Przeczytana" />}
            </span>
            <span className="min-w-0 flex-1">
              <span className="flex items-baseline justify-between gap-2">
                <span className={`truncate ${t.unread ? "font-semibold" : ""}`}>{t.from}</span>
                <span className="muted shrink-0 text-[11px]">{formatDateTime(t.date)}</span>
              </span>
              <span className={`block truncate text-[13px] ${t.unread ? "font-semibold" : ""}`}>{t.subject}</span>
              <span className="muted block truncate text-xs">{t.preview}</span>
            </span>
          </button>
        </li>
      ))}
    </ul>
  );
}

function ContactsPanel({ onWrite }: { onWrite: (email: string) => void }) {
  const query = useContacts();
  return (
    <aside className="card" aria-labelledby="h-contacts">
      <h2 id="h-contacts" className="card-title">Kontakt z dziekanatem</h2>
      <QueryBoundary
        query={query}
        system="usos"
        compact
        isEmpty={(d) => d.length === 0}
        empty={<EmptyState title="Brak danych kontaktowych" />}
      >
        {(list) => (
          <ul className="space-y-3">
            {list.map((c) => (
              <li key={c.id} className="text-xs">
                <p className="text-sm font-medium">{c.name}</p>
                <p className="muted">{c.role}</p>
                <div className="mt-1 space-y-0.5">
                  {c.email && (
                    <p className="flex items-center gap-1.5">
                      <Mail size={12} aria-hidden />
                      <a href={`mailto:${c.email}`}>{c.email}</a>
                      <button type="button" className="ml-1 underline" onClick={() => onWrite(c.email!)}>napisz w vUSOS</button>
                    </p>
                  )}
                  {c.phone && <p className="flex items-center gap-1.5"><Phone size={12} aria-hidden /><a href={`tel:${c.phone}`}>{c.phone}</a></p>}
                  {c.room && <p className="flex items-center gap-1.5"><MapPin size={12} aria-hidden />pok. {c.room}</p>}
                  {c.hours && <p className="flex items-center gap-1.5"><Clock size={12} aria-hidden />{c.hours}</p>}
                </div>
              </li>
            ))}
          </ul>
        )}
      </QueryBoundary>
    </aside>
  );
}

export function Messages() {
  const list = useMessages();
  const [selected, setSelected] = useState<string | null>(null);
  const [compose, setCompose] = useState<{ open: boolean; to: string }>({ open: false, to: "" });

  return (
    <>
      <PageHeader
        title="Wiadomości"
        actions={
          <button type="button" className="btn btn-primary" onClick={() => setCompose({ open: true, to: "" })}>
            <PenSquare size={15} aria-hidden /> Nowa wiadomość
          </button>
        }
      />
      <div className="grid gap-4 lg:grid-cols-[1fr_18rem]">
        <div className="grid min-w-0 gap-4 md:grid-cols-[18rem_1fr]">
          <div className={selected ? "hidden md:block" : ""}>
            <QueryBoundary
              query={list}
              system="usos"
              skeleton={<SkeletonLines rows={8} />}
              isEmpty={(d) => d.length === 0}
              empty={<div className="card"><EmptyState title="Brak wiadomości" /></div>}
            >
              {(items) => <ThreadList items={items} selected={selected} onSelect={setSelected} />}
            </QueryBoundary>
          </div>
          <div className={selected ? "" : "hidden md:block"}>
            {selected ? (
              <MessageDetail id={selected} onBack={() => setSelected(null)} />
            ) : (
              <div className="card"><EmptyState title="Wybierz wiadomość" hint="Treść pojawi się tutaj." /></div>
            )}
          </div>
        </div>
        <ContactsPanel onWrite={(email) => setCompose({ open: true, to: email })} />
      </div>
      {compose.open && <ComposeDialog open onClose={() => setCompose({ open: false, to: "" })} initialTo={compose.to} />}
    </>
  );
}
