import type { ConnectionStatus, SourceSystem } from "@vusos/shared";

export interface AppUser {
  id?: string;
  email?: string | null;
  name?: string | null;
  displayName?: string | null;
  firstName?: string | null;
  lastName?: string | null;
}

export interface ConnectionInfo {
  status: ConnectionStatus;
  mode?: string | null;
  updatedAt?: string | null;
}

export type Connections = Record<SourceSystem, ConnectionInfo>;

export interface MeResponse {
  user: AppUser;
  connections: Connections;
}

export interface ComposeInput {
  to: string[];
  subject: string;
  body: string;
}

/** Normalises either `"connected"` or `{status, mode, updatedAt}` into ConnectionInfo. */
export function normalizeConnection(v: unknown): ConnectionInfo {
  if (typeof v === "string") return { status: v as ConnectionStatus };
  if (v && typeof v === "object") {
    const o = v as Record<string, unknown>;
    const status = typeof o.status === "string" ? (o.status as ConnectionStatus) : "disconnected";
    return {
      status,
      mode: typeof o.mode === "string" ? o.mode : null,
      updatedAt: typeof o.updatedAt === "string" ? o.updatedAt : null,
    };
  }
  return { status: "disconnected" };
}

export function normalizeConnections(raw: unknown): Connections {
  const root = raw && typeof raw === "object" ? (raw as Record<string, unknown>) : {};
  const src = root.connections && typeof root.connections === "object" ? (root.connections as Record<string, unknown>) : root;
  return { usos: normalizeConnection(src.usos), platon: normalizeConnection(src.platon) };
}

export function userLabel(u: AppUser | undefined): string {
  if (!u) return "";
  const full = [u.firstName, u.lastName].filter(Boolean).join(" ");
  return u.displayName || u.name || full || u.email || "";
}
