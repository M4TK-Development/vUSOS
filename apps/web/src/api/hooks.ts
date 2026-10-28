import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type {
  Announcement,
  Contact,
  DashboardData,
  Grade,
  LmsAssignment,
  LmsCourse,
  LmsEvent,
  LmsGradeItem,
  LmsSection,
  Message,
  MessageThread,
  PaymentsSummary,
  SourceSystem,
  StudentProfile,
  Term,
  TermGrades,
  TimetableEntry,
} from "@vusos/shared";
import { api } from "./client";
import { normalizeConnections, type ComposeInput, type Connections, type MeResponse } from "./types";

const q = (params: Record<string, string | number | undefined>) => {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== "") sp.set(k, String(v));
  const s = sp.toString();
  return s ? `?${s}` : "";
};

/** Accepts a bare array or a `{ items: [] }`-style wrapper. */
function asArray<T>(v: unknown): T[] {
  if (Array.isArray(v)) return v as T[];
  if (v && typeof v === "object") {
    const o = v as Record<string, unknown>;
    for (const key of ["items", "data", "results"]) if (Array.isArray(o[key])) return o[key] as T[];
  }
  return [];
}

export const keys = {
  me: ["me"] as const,
  connections: ["connections"] as const,
  dashboard: ["dashboard"] as const,
  profile: ["usos", "profile"] as const,
  terms: ["usos", "terms"] as const,
  timetable: (from: string, to: string) => ["usos", "timetable", from, to] as const,
  grades: (term: string | undefined) => ["usos", "grades", term ?? "current"] as const,
  payments: ["usos", "payments"] as const,
  messages: ["usos", "messages"] as const,
  message: (id: string) => ["usos", "messages", id] as const,
  contacts: ["usos", "contacts"] as const,
  usosAnnouncements: ["usos", "announcements"] as const,
  courses: ["platon", "courses"] as const,
  contents: (id: string) => ["platon", "courses", id, "contents"] as const,
  lmsGrades: (id: string) => ["platon", "courses", id, "grades"] as const,
  assignments: ["platon", "assignments"] as const,
  events: (days: number) => ["platon", "events", days] as const,
  platonAnnouncements: ["platon", "announcements"] as const,
};

// ---- session / connections ----

export function useMe() {
  return useQuery({
    queryKey: keys.me,
    queryFn: async (): Promise<MeResponse> => {
      const raw = await api<{ user: MeResponse["user"]; connections?: unknown }>("/me");
      return { user: raw.user, connections: normalizeConnections(raw.connections) };
    },
    retry: false,
    staleTime: 30_000,
  });
}

export function useConnections() {
  return useQuery({
    queryKey: keys.connections,
    queryFn: async (): Promise<Connections> => normalizeConnections(await api<unknown>("/connections")),
  });
}

function useInvalidateConnections() {
  const qc = useQueryClient();
  return () => {
    void qc.invalidateQueries({ queryKey: keys.me });
    void qc.invalidateQueries({ queryKey: keys.connections });
    void qc.invalidateQueries({ queryKey: keys.dashboard });
  };
}

export function useUsosOAuthStart() {
  return useMutation({
    mutationFn: () => api<{ authorizeUrl: string }>("/connections/usos/oauth/start", { method: "POST" }),
  });
}

export function useConnectUsosWeb() {
  const done = useInvalidateConnections();
  return useMutation({
    mutationFn: (cookies: string) => api<unknown>("/connections/usos/web", { method: "POST", body: { cookies } }),
    onSuccess: done,
  });
}

export function useConnectPlatonWs() {
  const done = useInvalidateConnections();
  return useMutation({
    mutationFn: (wstoken: string) => api<unknown>("/connections/platon/ws", { method: "POST", body: { wstoken } }),
    onSuccess: done,
  });
}

export function useConnectPlatonWeb() {
  const done = useInvalidateConnections();
  return useMutation({
    mutationFn: (input: { cookies: string; sesskey: string }) =>
      api<unknown>("/connections/platon/web", { method: "POST", body: input }),
    onSuccess: done,
  });
}

export function useDisconnect() {
  const qc = useQueryClient();
  const done = useInvalidateConnections();
  return useMutation({
    mutationFn: (system: SourceSystem) => api<unknown>(`/connections/${system}`, { method: "DELETE" }),
    onSuccess: (_d, system) => {
      void qc.invalidateQueries({ queryKey: [system] });
      done();
    },
  });
}

export function useLogout() {
  return useMutation({ mutationFn: () => api<unknown>("/auth/logout", { method: "POST" }) });
}

// ---- dashboard ----

export function useDashboard() {
  return useQuery({ queryKey: keys.dashboard, queryFn: () => api<DashboardData>("/dashboard"), staleTime: 30_000 });
}

// ---- USOS ----

export function useProfile() {
  return useQuery({ queryKey: keys.profile, queryFn: () => api<StudentProfile>("/usos/profile") });
}

export function useTerms() {
  return useQuery({
    queryKey: keys.terms,
    queryFn: async () => asArray<Term>(await api<unknown>("/usos/terms")),
  });
}

export function useTimetable(from: string, to: string) {
  return useQuery({
    queryKey: keys.timetable(from, to),
    queryFn: async () => asArray<TimetableEntry>(await api<unknown>(`/usos/timetable${q({ from, to })}`)),
  });
}

/** Accepts TermGrades or a bare Grade[] (then `term` is synthesised by the caller). */
export function useGrades(term: string | undefined) {
  return useQuery({
    queryKey: keys.grades(term),
    enabled: term !== undefined,
    queryFn: async (): Promise<{ grades: Grade[]; average: number | null }> => {
      const raw = await api<unknown>(`/usos/grades${q({ term })}`);
      if (Array.isArray(raw)) return { grades: raw as Grade[], average: null };
      const o = raw as Partial<TermGrades>;
      return { grades: o.grades ?? [], average: o.average ?? null };
    },
  });
}

export function usePayments() {
  return useQuery({ queryKey: keys.payments, queryFn: () => api<PaymentsSummary>("/usos/payments") });
}

export function useMessages() {
  return useQuery({
    queryKey: keys.messages,
    queryFn: async () => asArray<MessageThread>(await api<unknown>("/usos/messages")),
  });
}

export function useMessage(id: string | undefined) {
  const qc = useQueryClient();
  return useQuery({
    queryKey: keys.message(id ?? ""),
    enabled: !!id,
    queryFn: async () => {
      const m = await api<Message>(`/usos/messages/${encodeURIComponent(id ?? "")}`);
      void qc.invalidateQueries({ queryKey: keys.messages });
      void qc.invalidateQueries({ queryKey: keys.dashboard });
      return m;
    },
  });
}

export function useSendMessage() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: ComposeInput) => api<unknown>("/usos/messages", { method: "POST", body: input }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: keys.messages }),
  });
}

export function useContacts() {
  return useQuery({
    queryKey: keys.contacts,
    queryFn: async () => asArray<Contact>(await api<unknown>("/usos/contacts")),
  });
}

export function useUsosAnnouncements(enabled = true) {
  return useQuery({
    queryKey: keys.usosAnnouncements,
    enabled,
    queryFn: async () => asArray<Announcement>(await api<unknown>("/usos/announcements")),
  });
}

// ---- Platon ----

export function usePlatonCourses() {
  return useQuery({
    queryKey: keys.courses,
    queryFn: async () => asArray<LmsCourse>(await api<unknown>("/platon/courses")),
  });
}

export function useCourseContents(id: string | undefined) {
  return useQuery({
    queryKey: keys.contents(id ?? ""),
    enabled: !!id,
    queryFn: async () => asArray<LmsSection>(await api<unknown>(`/platon/courses/${encodeURIComponent(id ?? "")}/contents`)),
  });
}

export function useCourseGrades(id: string | undefined) {
  return useQuery({
    queryKey: keys.lmsGrades(id ?? ""),
    enabled: !!id,
    queryFn: async () => asArray<LmsGradeItem>(await api<unknown>(`/platon/courses/${encodeURIComponent(id ?? "")}/grades`)),
  });
}

export function useAssignments() {
  return useQuery({
    queryKey: keys.assignments,
    queryFn: async () => asArray<LmsAssignment>(await api<unknown>("/platon/assignments")),
  });
}

export function usePlatonEvents(days: number, enabled = true) {
  return useQuery({
    queryKey: keys.events(days),
    enabled,
    queryFn: async () => asArray<LmsEvent>(await api<unknown>(`/platon/events${q({ days })}`)),
  });
}

export function usePlatonAnnouncements(enabled = true) {
  return useQuery({
    queryKey: keys.platonAnnouncements,
    enabled,
    queryFn: async () => asArray<Announcement>(await api<unknown>("/platon/announcements")),
  });
}
