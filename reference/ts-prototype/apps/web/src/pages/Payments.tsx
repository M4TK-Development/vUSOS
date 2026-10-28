import { useState } from "react";
import { Check, Copy } from "lucide-react";
import type { Payment } from "@vusos/shared";
import { usePayments } from "../api/hooks";
import { EmptyState } from "../components/EmptyState";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SkeletonLines } from "../components/Skeleton";
import { formatMoney, pick } from "../lib/format";
import { formatDate } from "../lib/time";

const STATE: Record<Payment["state"], { label: string; cls: string }> = {
  unpaid: { label: "Do zapłaty", cls: "badge-amber" },
  partially_paid: { label: "Częściowo opłacone", cls: "badge-blue" },
  paid: { label: "Opłacone", cls: "badge-green" },
  overdue: { label: "Po terminie", cls: "badge-red" },
  cancelled: { label: "Anulowane", cls: "badge-gray" },
};

export function StateBadge({ state }: { state: Payment["state"] }) {
  const s = STATE[state];
  return <span className={`badge ${s.cls}`}>{s.label}</span>;
}

export function CopyButton({ text, label = "Kopiuj" }: { text: string; label?: string }) {
  const [done, setDone] = useState(false);
  return (
    <button
      type="button"
      className="btn !px-2 !py-1 text-xs"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setDone(true);
          setTimeout(() => setDone(false), 1500);
        } catch {
          window.prompt("Skopiuj numer konta:", text);
        }
      }}
    >
      {done ? <Check size={13} aria-hidden /> : <Copy size={13} aria-hidden />}
      {done ? "Skopiowano" : label}
    </button>
  );
}

export function Payments() {
  const query = usePayments();
  return (
    <>
      <PageHeader title="Płatności" />
      <QueryBoundary query={query} system="usos" skeleton={<SkeletonLines rows={6} />}>
        {(d) => {
          const accounts = [...new Set(d.payments.map((p) => p.accountNumber).filter((x): x is string => !!x))];
          return (
            <div className="space-y-4">
              <div className="grid grid-cols-2 gap-3">
                <div className="card">
                  <p className="muted text-xs">Do zapłaty</p>
                  <p className="text-2xl font-bold tabular-nums">{formatMoney(d.totalDue, d.currency)}</p>
                </div>
                <div className={`card ${d.totalOverdue > 0 ? "!border-red-300 !bg-red-50 dark:!border-red-900 dark:!bg-red-950/40" : ""}`}>
                  <p className="muted text-xs">W tym po terminie</p>
                  <p className={`text-2xl font-bold tabular-nums ${d.totalOverdue > 0 ? "text-red-700 dark:text-red-400" : ""}`}>
                    {formatMoney(d.totalOverdue, d.currency)}
                  </p>
                </div>
              </div>

              {accounts.length > 0 && (
                <div className="card space-y-2">
                  <h2 className="card-title !mb-0">Numer konta do wpłat</h2>
                  {accounts.map((a) => (
                    <div key={a} className="flex flex-wrap items-center gap-2">
                      <code className="break-all font-mono text-sm">{a}</code>
                      <CopyButton text={a} />
                    </div>
                  ))}
                </div>
              )}

              {d.payments.length === 0 ? (
                <div className="card"><EmptyState title="Brak płatności" /></div>
              ) : (
                <div className="card overflow-x-auto !p-0">
                  <table className="w-full min-w-[40rem] text-left text-sm">
                    <thead className="muted border-b border-slate-200 text-xs dark:border-slate-800">
                      <tr>
                        <th className="px-3 py-2 font-medium">Opis</th>
                        <th className="px-3 py-2 font-medium">Termin</th>
                        <th className="px-3 py-2 text-right font-medium">Kwota</th>
                        <th className="px-3 py-2 text-right font-medium">Wpłacono</th>
                        <th className="px-3 py-2 font-medium">Status</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                      {d.payments.map((p) => (
                        <tr key={p.id} className={p.state === "overdue" ? "bg-red-50 dark:bg-red-950/30" : ""}>
                          <td className="px-3 py-2">
                            <p className="font-medium">{pick(p.description) || p.type}</p>
                            <p className="muted text-xs">{p.type}</p>
                          </td>
                          <td className="whitespace-nowrap px-3 py-2">{formatDate(p.dueDate)}</td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                            {formatMoney(p.amount, p.currency)}
                            {p.interest ? <p className="text-xs text-red-600 dark:text-red-400">+ odsetki {formatMoney(p.interest, p.currency)}</p> : null}
                          </td>
                          <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">{formatMoney(p.paid, p.currency)}</td>
                          <td className="px-3 py-2"><StateBadge state={p.state} /></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          );
        }}
      </QueryBoundary>
    </>
  );
}
