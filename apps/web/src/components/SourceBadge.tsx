import type { SourceSystem } from "@vusos/shared";

export function SourceBadge({ source }: { source: SourceSystem }) {
  return <span className={`badge ${source === "usos" ? "badge-brand" : "badge-amber"}`}>{source === "usos" ? "USOS" : "Platon"}</span>;
}
