import { createHash, randomBytes, randomUUID } from "node:crypto";
import type { ConnectionStatus, PlatonCredentials, SourceSystem, UsosCredentials } from "@vusos/shared";
import { decryptJson, encryptJson } from "./crypto.js";
import type { Db } from "./db.js";

export interface UserRow {
  id: string;
  email: string;
  name: string;
  tenantId: string | null;
  createdAt: string;
}

export interface CredentialsBySystem {
  usos: UsosCredentials;
  platon: PlatonCredentials;
}

export interface StoredConnection<S extends SourceSystem> {
  system: S;
  creds: CredentialsBySystem[S];
  status: ConnectionStatus;
  mode: string;
  updatedAt: string;
}

/** Public (secret-free) view of a connection. */
export interface ConnectionInfo {
  system: SourceSystem;
  status: ConnectionStatus;
  mode: string | null;
  updatedAt: string | null;
}

export const API_TOKEN_TTL_MS = 90 * 24 * 60 * 60 * 1000;
export const AUTH_CODE_TTL_MS = 2 * 60 * 1000;
const sha256 = (v: string): string => createHash("sha256").update(v).digest("hex");
const newSecret = (): string => randomBytes(32).toString("base64url");

const OAUTH_STATE_TTL_MS = 15 * 60 * 1000;
const SYSTEMS: SourceSystem[] = ["usos", "platon"];

type Row = Record<string, unknown>;
const str = (v: unknown): string => String(v ?? "");

export function createStore(db: Db, key: Buffer) {
  const connAad = (userId: string, system: SourceSystem): string => `conn:${userId}:${system}`;
  const stateAad = (state: string, userId: string): string => `oauth:${state}:${userId}`;

  const toUser = (r: Row): UserRow => ({
    id: str(r.id),
    email: str(r.email),
    name: str(r.name),
    tenantId: r.tenant_id == null ? null : str(r.tenant_id),
    createdAt: str(r.created_at)
  });

  return {
    upsertUser(u: { id: string; email: string; name: string; tenantId: string | null }): UserRow {
      db.prepare(
        `INSERT INTO users (id, email, name, tenant_id, created_at) VALUES (?, ?, ?, ?, ?)
         ON CONFLICT(id) DO UPDATE SET email = excluded.email, name = excluded.name, tenant_id = excluded.tenant_id`
      ).run(u.id, u.email, u.name, u.tenantId, new Date().toISOString());
      const row = this.getUser(u.id);
      if (!row) throw new Error("user upsert failed");
      return row;
    },

    getUser(id: string): UserRow | null {
      const r = db.prepare("SELECT * FROM users WHERE id = ?").get(id) as Row | undefined;
      return r ? toUser(r) : null;
    },

    saveConnection<S extends SourceSystem>(
      userId: string,
      system: S,
      creds: CredentialsBySystem[S],
      status: ConnectionStatus = "connected"
    ): void {
      db.prepare(
        `INSERT INTO connections (user_id, system, mode, payload_enc, status, updated_at) VALUES (?, ?, ?, ?, ?, ?)
         ON CONFLICT(user_id, system) DO UPDATE SET
           mode = excluded.mode, payload_enc = excluded.payload_enc, status = excluded.status, updated_at = excluded.updated_at`
      ).run(userId, system, creds.mode, encryptJson(creds, key, connAad(userId, system)), status, new Date().toISOString());
    },

    /** Returns null when missing or when the payload cannot be decrypted (e.g. the key was rotated). */
    getConnection<S extends SourceSystem>(userId: string, system: S): StoredConnection<S> | null {
      const r = db.prepare("SELECT * FROM connections WHERE user_id = ? AND system = ?").get(userId, system) as
        | Row
        | undefined;
      if (!r) return null;
      try {
        const creds = decryptJson<CredentialsBySystem[S]>(str(r.payload_enc), key, connAad(userId, system));
        return { system, creds, status: str(r.status) as ConnectionStatus, mode: str(r.mode), updatedAt: str(r.updated_at) };
      } catch {
        return null;
      }
    },

    setConnectionStatus(userId: string, system: SourceSystem, status: ConnectionStatus): void {
      db.prepare("UPDATE connections SET status = ?, updated_at = ? WHERE user_id = ? AND system = ?").run(
        status,
        new Date().toISOString(),
        userId,
        system
      );
    },

    deleteConnection(userId: string, system: SourceSystem): void {
      db.prepare("DELETE FROM connections WHERE user_id = ? AND system = ?").run(userId, system);
    },

    listConnections(userId: string): Record<SourceSystem, ConnectionInfo> {
      const out = {} as Record<SourceSystem, ConnectionInfo>;
      for (const system of SYSTEMS) out[system] = { system, status: "disconnected", mode: null, updatedAt: null };
      const rows = db.prepare("SELECT system, mode, status, updated_at FROM connections WHERE user_id = ?").all(userId) as Row[];
      for (const r of rows) {
        const system = str(r.system) as SourceSystem;
        if (system in out) {
          out[system] = { system, status: str(r.status) as ConnectionStatus, mode: str(r.mode), updatedAt: str(r.updated_at) };
        }
      }
      return out;
    },

    saveOAuthState(state: string, userId: string, system: SourceSystem, data: unknown): void {
      const cutoff = new Date(Date.now() - OAUTH_STATE_TTL_MS).toISOString();
      db.prepare("DELETE FROM oauth_states WHERE created_at < ?").run(cutoff);
      db.prepare(
        `INSERT INTO oauth_states (state, user_id, system, data_enc, created_at) VALUES (?, ?, ?, ?, ?)
         ON CONFLICT(state) DO UPDATE SET user_id = excluded.user_id, system = excluded.system,
           data_enc = excluded.data_enc, created_at = excluded.created_at`
      ).run(state, userId, system, encryptJson(data, key, stateAad(state, userId)), new Date().toISOString());
    },

    /**
     * One-shot lookup by state (the row is deleted whether or not it is valid). The state itself is the proof of
     * the flow, so no session is needed (native clients complete the callback in a system browser).
     */
    consumeOAuthState<T>(state: string, system: SourceSystem): { userId: string; data: T } | null {
      const r = db.prepare("SELECT * FROM oauth_states WHERE state = ?").get(state) as Row | undefined;
      if (!r) return null;
      db.prepare("DELETE FROM oauth_states WHERE state = ?").run(state);
      const userId = str(r.user_id);
      if (str(r.system) !== system) return null;
      if (Date.now() - Date.parse(str(r.created_at)) > OAUTH_STATE_TTL_MS) return null;
      try {
        return { userId, data: decryptJson<T>(str(r.data_enc), key, stateAad(state, userId)) };
      } catch {
        return null;
      }
    },

    // ---- Bearer tokens (native clients) ----

    /** Returns the plaintext token (shown once); only its sha256 is stored. */
    createApiToken(userId: string, client: string): string {
      const now = Date.now();
      db.prepare("DELETE FROM api_tokens WHERE expires_at < ?").run(new Date(now).toISOString());
      const token = newSecret();
      db.prepare(
        "INSERT INTO api_tokens (token_hash, user_id, client, created_at, last_used_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)"
      ).run(
        sha256(token),
        userId,
        client,
        new Date(now).toISOString(),
        new Date(now).toISOString(),
        new Date(now + API_TOKEN_TTL_MS).toISOString()
      );
      return token;
    },

    /** Resolves a bearer token to its user and slides the expiry. Null if unknown/expired. */
    resolveApiToken(token: string): UserRow | null {
      const hash = sha256(token);
      const r = db
        .prepare("SELECT user_id, expires_at FROM api_tokens WHERE token_hash = ?")
        .get(hash) as Row | undefined;
      if (!r) return null;
      const now = Date.now();
      if (Date.parse(str(r.expires_at)) < now) {
        db.prepare("DELETE FROM api_tokens WHERE token_hash = ?").run(hash);
        return null;
      }
      const user = this.getUser(str(r.user_id));
      if (!user) return null;
      db.prepare("UPDATE api_tokens SET last_used_at = ?, expires_at = ? WHERE token_hash = ?").run(
        new Date(now).toISOString(),
        new Date(now + API_TOKEN_TTL_MS).toISOString(),
        hash
      );
      return user;
    },

    revokeApiToken(token: string): void {
      db.prepare("DELETE FROM api_tokens WHERE token_hash = ?").run(sha256(token));
    },

    // ---- One-time native login codes ----

    createAuthCode(userId: string): string {
      db.prepare("DELETE FROM auth_codes WHERE created_at < ?").run(new Date(Date.now() - AUTH_CODE_TTL_MS).toISOString());
      const code = newSecret();
      db.prepare("INSERT INTO auth_codes (code_hash, user_id, created_at) VALUES (?, ?, ?)").run(
        sha256(code),
        userId,
        new Date().toISOString()
      );
      return code;
    },

    /** One-shot: returns the user id and deletes the code, or null if unknown/expired. */
    consumeAuthCode(code: string): string | null {
      const hash = sha256(code);
      const r = db.prepare("SELECT user_id, created_at FROM auth_codes WHERE code_hash = ?").get(hash) as Row | undefined;
      if (!r) return null;
      db.prepare("DELETE FROM auth_codes WHERE code_hash = ?").run(hash);
      if (Date.now() - Date.parse(str(r.created_at)) > AUTH_CODE_TTL_MS) return null;
      return str(r.user_id);
    },

    newId(): string {
      return randomUUID();
    }
  };
}

export type Store = ReturnType<typeof createStore>;
