import { useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import {
  BookOpen,
  CalendarDays,
  GraduationCap,
  LayoutDashboard,
  LogOut,
  Megaphone,
  Menu,
  Settings,
  Wallet,
  Mail,
  X,
  type LucideIcon,
} from "lucide-react";
import { useLogout, useMe } from "../api/hooks";
import { userLabel } from "../api/types";

interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  end?: boolean;
}

const NAV: NavItem[] = [
  { to: "/", label: "Start", icon: LayoutDashboard, end: true },
  { to: "/plan", label: "Plan", icon: CalendarDays },
  { to: "/oceny", label: "Oceny", icon: GraduationCap },
  { to: "/kursy", label: "Kursy", icon: BookOpen },
  { to: "/platnosci", label: "Płatności", icon: Wallet },
  { to: "/wiadomosci", label: "Wiadomości", icon: Mail },
  { to: "/ogloszenia", label: "Ogłoszenia", icon: Megaphone },
  { to: "/settings", label: "Ustawienia", icon: Settings },
];
const BOTTOM = NAV.slice(0, 4);
const MORE = NAV.slice(4);

export function Brand() {
  return (
    <div className="flex items-baseline gap-1.5">
      <span className="text-lg font-extrabold tracking-tight text-brand-600 dark:text-brand-500">vUSOS</span>
      <span className="muted text-[11px]">by M4TK</span>
    </div>
  );
}

export function DisclaimerFooter({ className = "" }: { className?: string }) {
  return <p className={`muted text-[11px] leading-snug ${className}`}>Nieoficjalny projekt studencki, niezwiązany z uczelnią</p>;
}

const desktopLink = ({ isActive }: { isActive: boolean }) =>
  `flex items-center gap-2.5 rounded-lg px-3 py-2 text-sm font-medium no-underline transition ${
    isActive
      ? "bg-brand-50 !text-brand-700 dark:bg-indigo-950 dark:!text-indigo-300"
      : "!text-slate-700 hover:bg-slate-100 dark:!text-slate-300 dark:hover:bg-slate-800"
  }`;

const mobileLink = ({ isActive }: { isActive: boolean }) =>
  `flex flex-1 flex-col items-center gap-0.5 py-2 text-[11px] font-medium no-underline ${
    isActive ? "!text-brand-600 dark:!text-brand-500" : "!text-slate-500 dark:!text-slate-400"
  }`;

function UserBox() {
  const me = useMe();
  const qc = useQueryClient();
  const navigate = useNavigate();
  const logout = useLogout();
  const label = userLabel(me.data?.user);
  return (
    <div className="space-y-2">
      {label && <p className="truncate text-xs font-medium" title={label}>{label}</p>}
      <button
        type="button"
        className="btn btn-ghost w-full !justify-start text-xs"
        disabled={logout.isPending}
        onClick={() =>
          logout.mutate(undefined, {
            onSettled: () => {
              qc.clear();
              navigate("/login");
            },
          })
        }
      >
        <LogOut size={14} aria-hidden /> Wyloguj
      </button>
    </div>
  );
}

export function Layout() {
  const [moreOpen, setMoreOpen] = useState(false);
  return (
    <div className="px-safe flex min-h-full">
      {/* desktop sidebar */}
      <aside className="pt-safe sticky top-0 hidden h-screen w-56 shrink-0 flex-col justify-between border-r border-slate-200 bg-white p-3 dark:border-slate-800 dark:bg-slate-900 md:flex">
        <div>
          <div className="px-3 py-3"><Brand /></div>
          <nav aria-label="Nawigacja główna" className="mt-2 space-y-0.5">
            {NAV.map(({ to, label, icon: Icon, end }) => (
              <NavLink key={to} to={to} end={end} className={desktopLink}>
                <Icon size={17} aria-hidden /> {label}
              </NavLink>
            ))}
          </nav>
        </div>
        <div className="space-y-3 px-1">
          <UserBox />
          <DisclaimerFooter className="px-2" />
        </div>
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        {/* mobile top bar */}
        <header className="pt-safe sticky top-0 z-30 flex items-center justify-between border-b border-slate-200 bg-white/90 px-4 py-2.5 backdrop-blur dark:border-slate-800 dark:bg-slate-900/90 md:hidden">
          <Brand />
        </header>
        <main className="mx-auto w-full max-w-6xl flex-1 px-4 pb-28 pt-4 md:px-6 md:pb-8 md:pt-6">
          <Outlet />
          <DisclaimerFooter className="mt-8 text-center md:hidden" />
        </main>
      </div>

      {/* mobile bottom nav */}
      <nav aria-label="Nawigacja mobilna" className="fixed inset-x-0 bottom-0 z-30 flex border-t border-slate-200 bg-white pb-safe px-safe dark:border-slate-800 dark:bg-slate-900 md:hidden">
        {BOTTOM.map(({ to, label, icon: Icon, end }) => (
          <NavLink key={to} to={to} end={end} className={mobileLink}>
            <Icon size={20} aria-hidden /> {label}
          </NavLink>
        ))}
        <button type="button" className={`${mobileLink({ isActive: false })} bg-transparent`} onClick={() => setMoreOpen(true)} aria-label="Więcej">
          <Menu size={20} aria-hidden /> Więcej
        </button>
      </nav>

      {moreOpen && (
        <div className="fixed inset-0 z-40 md:hidden" role="dialog" aria-modal="true" aria-label="Więcej">
          <button type="button" className="absolute inset-0 bg-slate-900/50" aria-label="Zamknij" onClick={() => setMoreOpen(false)} />
          <div className="absolute inset-x-0 bottom-0 pb-safe rounded-t-2xl border border-slate-200 bg-white p-4 pb-8 dark:border-slate-800 dark:bg-slate-900">
            <div className="mb-2 flex items-center justify-between">
              <span className="font-semibold">Więcej</span>
              <button type="button" className="btn btn-ghost !p-1.5" onClick={() => setMoreOpen(false)} aria-label="Zamknij">
                <X size={18} aria-hidden />
              </button>
            </div>
            <div className="grid grid-cols-2 gap-1">
              {MORE.map(({ to, label, icon: Icon }) => (
                <NavLink key={to} to={to} className={desktopLink} onClick={() => setMoreOpen(false)}>
                  <Icon size={17} aria-hidden /> {label}
                </NavLink>
              ))}
            </div>
            <div className="mt-3 border-t border-slate-200 pt-3 dark:border-slate-800"><UserBox /></div>
          </div>
        </div>
      )}
    </div>
  );
}
