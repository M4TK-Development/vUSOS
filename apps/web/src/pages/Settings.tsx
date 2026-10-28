import { useState, type FormEvent, type ReactNode } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, ExternalLink, LogOut, Plug, Unplug } from "lucide-react";
import type { SourceSystem } from "@vusos/shared";
import {
  DEFAULT_CONFIG, useConfig, useConnections, useConnectPlatonWeb, useConnectPlatonWs, useConnectUsosWeb, useDisconnect,
  useLogout, useMe, useUsosOAuthStart,
} from "../api/hooks";
import type { ConnectionInfo } from "../api/types";
import { userLabel } from "../api/types";
import { SYSTEM_NAMES } from "../components/ConnectPrompt";
import { PageHeader } from "../components/PageHeader";
import { QueryBoundary } from "../components/QueryBoundary";
import { SkeletonCards } from "../components/Skeleton";
import { formatDateTime } from "../lib/time";
import { isNative, openExternal } from "../platform";
import { toast } from "../toast";

const STATUS: Record<ConnectionInfo["status"], { label: string; cls: string }> = {
  connected: { label: "Połączono", cls: "badge-green" },
  expired: { label: "Sesja wygasła", cls: "badge-amber" },
  error: { label: "Błąd połączenia", cls: "badge-red" },
  disconnected: { label: "Niepołączono", cls: "badge-gray" },
};

const MODE_LABELS: Record<string, string> = {
  api: "USOS API (OAuth)", oauth: "USOS API (OAuth)", web: "tryb awaryjny (cookies)", ws: "Web Services (token)", mock: "dane demonstracyjne",
};

function ErrorLine({ error }: { error: unknown }) {
  if (!error) return null;
  return <p role="alert" className="text-xs text-red-600 dark:text-red-400">{error instanceof Error ? error.message : "Błąd."}</p>;
}

function Collapsible({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details className="rounded-lg border border-slate-200 px-3 py-2 dark:border-slate-800">
      <summary className="cursor-pointer text-xs font-medium">{title}</summary>
      <div className="mt-3 space-y-3">{children}</div>
    </details>
  );
}

function CardShell({ system, info, children }: { system: SourceSystem; info: ConnectionInfo; children: ReactNode }) {
  const disconnect = useDisconnect();
  const st = STATUS[info.status];
  return (
    <section className="card space-y-4" aria-labelledby={`h-${system}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id={`h-${system}`} className="text-base font-semibold">{SYSTEM_NAMES[system]}</h2>
        <span className={`badge ${st.cls}`}>{st.label}</span>
      </div>
      {info.status !== "disconnected" && (
        <p className="muted text-xs">
          {info.mode && <>Tryb: {MODE_LABELS[info.mode] ?? info.mode}. </>}
          {info.updatedAt && <>Zaktualizowano: {formatDateTime(info.updatedAt)}.</>}
        </p>
      )}
      {children}
      {info.status !== "disconnected" && (
        <div className="border-t border-slate-100 pt-3 dark:border-slate-800">
          <button
            type="button"
            className="btn btn-danger"
            disabled={disconnect.isPending}
            onClick={() => disconnect.mutate(system, { onSuccess: () => toast(`Rozłączono ${SYSTEM_NAMES[system]}.`, "info") })}
          >
            <Unplug size={14} aria-hidden /> Rozłącz {SYSTEM_NAMES[system]}
          </button>
          <ErrorLine error={disconnect.error} />
        </div>
      )}
    </section>
  );
}

function UsosCard({ info }: { info: ConnectionInfo }) {
  const start = useUsosOAuthStart();
  const web = useConnectUsosWeb();
  const [cookies, setCookies] = useState("");
  const connect = () =>
    start.mutate(undefined, {
      onSuccess: ({ authorizeUrl }) => {
        if (isNative()) openExternal(authorizeUrl);
        else window.location.href = authorizeUrl;
      },
    });
  const submitCookies = (e: FormEvent) => {
    e.preventDefault();
    web.mutate(cookies.trim(), { onSuccess: () => { setCookies(""); toast("Zapisano cookies USOSweb.", "success"); } });
  };
  return (
    <CardShell system="usos" info={info}>
      <div className="space-y-2">
        <p className="muted text-xs">
          Zostaniesz przekierowany do USOS, aby zatwierdzić dostęp (logowanie kontem uczelni). Aplikacja nie widzi Twojego hasła.
        </p>
        <button type="button" className="btn btn-primary" onClick={connect} disabled={start.isPending}>
          <Plug size={14} aria-hidden /> {info.status === "connected" ? "Połącz ponownie z USOS" : "Połącz z USOS"}
        </button>
        <ErrorLine error={start.error} />
      </div>
      <Collapsible title="Tryb awaryjny (cookies)">
        <form onSubmit={submitCookies} className="space-y-2">
          <p className="muted text-xs">
            Jeśli USOS API jest niedostępne: zaloguj się do USOSweb w przeglądarce i wklej tutaj zawartość nagłówka <code>Cookie</code> sesji
            (np. z narzędzi deweloperskich: Network → dowolne żądanie do usosweb → Request Headers → Cookie).
          </p>
          <textarea className="input min-h-24 font-mono text-xs" value={cookies} onChange={(e) => setCookies(e.target.value)} placeholder="PHPSESSID=…; ..." required />
          <button type="submit" className="btn" disabled={web.isPending || !cookies.trim()}>Zapisz cookies</button>
          <ErrorLine error={web.error} />
        </form>
      </Collapsible>
    </CardShell>
  );
}

function randomPassport(): string {
  const a = new Uint32Array(2);
  crypto.getRandomValues(a);
  return `${a[0]}${a[1]}`;
}

export function buildLaunchUrl(platonBase: string, passport: string, scheme: string): string {
  const base = platonBase.endsWith("/") ? platonBase : `${platonBase}/`;
  return `${base}admin/tool/mobile/launch.php?service=moodle_mobile_app&passport=${encodeURIComponent(passport)}&urlscheme=${encodeURIComponent(scheme)}`;
}

function PlatonCard({ info }: { info: ConnectionInfo }) {
  const config = useConfig();
  const ws = useConnectPlatonWs();
  const web = useConnectPlatonWeb();
  const [passport] = useState(randomPassport);
  const [pasted, setPasted] = useState("");
  const [cookies, setCookies] = useState("");
  const [sesskey, setSesskey] = useState("");
  const cfg = config.data ?? DEFAULT_CONFIG;
  const native = isNative();
  const launchUrl = buildLaunchUrl(cfg.platonBaseUrl, passport, native ? cfg.nativeScheme : "moodlemobile");
  const connected = info.status === "connected";

  const submitToken = (e: FormEvent) => {
    e.preventDefault();
    ws.mutate(pasted.trim(), { onSuccess: () => { setPasted(""); toast("Połączono z Platonem.", "success"); } });
  };
  const submitWeb = (e: FormEvent) => {
    e.preventDefault();
    web.mutate({ cookies: cookies.trim(), sesskey: sesskey.trim() }, { onSuccess: () => { setCookies(""); setSesskey(""); toast("Zapisano sesję Platona.", "success"); } });
  };

  return (
    <CardShell system="platon" info={info}>
      {native ? (
        <div className="space-y-2">
          <p className="muted text-xs">Otworzy się przeglądarka z logowaniem kontem uczelni. Po zalogowaniu wrócisz do aplikacji automatycznie.</p>
          <button type="button" className="btn btn-primary" onClick={() => openExternal(launchUrl)}>
            <Plug size={14} aria-hidden /> {connected ? "Połącz ponownie z Platonem" : "Połącz z Platonem"}
          </button>
        </div>
      ) : (
        <form onSubmit={submitToken} className="space-y-3">
          <ol className="list-decimal space-y-2 pl-5 text-xs">
            <li>
              Otwórz{" "}
              <a href={launchUrl} target="_blank" rel="noreferrer" className="break-all font-medium">
                link logowania Platona <ExternalLink size={11} className="inline" aria-hidden />
              </a>{" "}
              w nowej karcie.
            </li>
            <li>Zaloguj się kontem uczelni (M365).</li>
            <li>
              Po zalogowaniu przeglądarka spróbuje otworzyć adres zaczynający się od <code>moodlemobile://token=…</code> — zobaczysz błąd
              lub pustą stronę. Skopiuj cały ten adres z paska adresu (lub ze strony błędu).
            </li>
            <li>Wklej go poniżej i zatwierdź.</li>
          </ol>
          <textarea className="input min-h-20 font-mono text-xs" value={pasted} onChange={(e) => setPasted(e.target.value)} placeholder="moodlemobile://token=…" required aria-label="Adres moodlemobile://token=" />
          <button type="submit" className="btn btn-primary" disabled={ws.isPending || !pasted.trim()}>
            <CheckCircle2 size={14} aria-hidden /> Połącz z Platonem
          </button>
          <ErrorLine error={ws.error} />
        </form>
      )}
      <Collapsible title="Tryb awaryjny (cookies + sesskey)">
        <form onSubmit={submitWeb} className="space-y-2">
          <p className="muted text-xs">
            Zaloguj się do Platona w przeglądarce. Wklej cookie <code>MoodleSession</code> (nagłówek Cookie) oraz wartość <code>sesskey</code> (widoczna np. w linku „Wyloguj”).
          </p>
          <textarea className="input min-h-20 font-mono text-xs" value={cookies} onChange={(e) => setCookies(e.target.value)} placeholder="MoodleSession=…" required aria-label="Cookies Platona" />
          <input className="input font-mono text-xs" value={sesskey} onChange={(e) => setSesskey(e.target.value)} placeholder="sesskey" required aria-label="sesskey" />
          <button type="submit" className="btn" disabled={web.isPending || !cookies.trim() || !sesskey.trim()}>Zapisz sesję</button>
          <ErrorLine error={web.error} />
        </form>
      </Collapsible>
    </CardShell>
  );
}

export function SettingsPage() {
  const connections = useConnections();
  const me = useMe();
  const [params, setParams] = useSearchParams();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const logout = useLogout();
  const connected = params.get("connected");
  const error = params.get("error");

  return (
    <>
      <PageHeader title="Ustawienia" subtitle="Połączenia" />
      {(connected === "usos" || connected === "platon") && (
        <div role="status" className="mb-4 flex items-center justify-between gap-2 rounded-lg border border-emerald-300 bg-emerald-50 px-3 py-2 text-xs text-emerald-900 dark:border-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-200">
          <span className="flex items-center gap-2"><CheckCircle2 size={16} aria-hidden /> Połączono z systemem {SYSTEM_NAMES[connected]}.</span>
          <button type="button" className="underline" onClick={() => setParams({}, { replace: true })}>Zamknij</button>
        </div>
      )}
      {error && (
        <div role="alert" className="mb-4 rounded-lg border border-red-300 bg-red-50 px-3 py-2 text-xs text-red-900 dark:border-red-900 dark:bg-red-950/40 dark:text-red-200">
          Połączenie nie powiodło się: {error}
        </div>
      )}
      <QueryBoundary query={connections} skeleton={<SkeletonCards count={2} className="h-48" />}>
        {(c) => (
          <div className="grid gap-4 lg:grid-cols-2">
            <UsosCard info={c.usos} />
            <PlatonCard info={c.platon} />
          </div>
        )}
      </QueryBoundary>

      <section className="card mt-4 flex flex-wrap items-center justify-between gap-3" aria-labelledby="h-account">
        <div>
          <h2 id="h-account" className="text-base font-semibold">Konto</h2>
          <p className="muted text-xs">{userLabel(me.data?.user) || "Zalogowany użytkownik"}</p>
        </div>
        <button
          type="button"
          className="btn"
          disabled={logout.isPending}
          onClick={() => logout.mutate(undefined, { onSettled: () => { qc.clear(); navigate("/login"); } })}
        >
          <LogOut size={14} aria-hidden /> Wyloguj
        </button>
      </section>
    </>
  );
}
