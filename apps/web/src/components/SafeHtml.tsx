import { useMemo } from "react";
import { sanitizeHtml } from "../lib/sanitize";

export function SafeHtml({ html, className = "" }: { html: string; className?: string }) {
  const clean = useMemo(() => sanitizeHtml(html), [html]);
  return <div className={`html-content ${className}`} dangerouslySetInnerHTML={{ __html: clean }} />;
}
