export interface TabDef<K extends string> {
  key: K;
  label: string;
}

export function Tabs<K extends string>({ tabs, active, onChange }: { tabs: TabDef<K>[]; active: K; onChange: (k: K) => void }) {
  return (
    <div role="tablist" className="mb-4 flex gap-1 border-b border-slate-200 dark:border-slate-800">
      {tabs.map((t) => (
        <button
          key={t.key}
          type="button"
          role="tab"
          aria-selected={active === t.key}
          className={`tab ${active === t.key ? "tab-active" : ""}`}
          onClick={() => onChange(t.key)}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}
