import type { PlatonAdapter, PlatonCredentials, SourceSystem, UsosAdapter, UsosCredentials } from "@vusos/shared";
import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";
import type { ConnectionStatus } from "@vusos/shared";
import type { AppConfig } from "../config.js";
import { adapterAuth, adapterUnavailable, notConnected } from "../errors.js";
import type { ConnectionInfo, Store } from "../store.js";
import { createPlatonAdapter } from "./platon/index.js";
import { createUsosAdapter } from "./usos/index.js";

export interface CallOptions {
  /** Cache key within (user, system). Omit/null to bypass the cache (mutations). */
  cacheKey?: string | null;
  /** Override the default TTL (ms). */
  ttlMs?: number;
}

export interface AdapterRegistry {
  usos<T>(userId: string, fn: (a: UsosAdapter) => Promise<T>, opts?: CallOptions): Promise<T>;
  platon<T>(userId: string, fn: (a: PlatonAdapter) => Promise<T>, opts?: CallOptions): Promise<T>;
  /** Builds an adapter from explicit (not yet persisted) credentials. Used to validate fresh connections. */
  buildUsos(creds: UsosCredentials): UsosAdapter;
  buildPlaton(creds: PlatonCredentials): PlatonAdapter;
  /** Effective connection status (mock mode reports "connected"). */
  statuses(userId: string): Record<SourceSystem, ConnectionInfo>;
  /** Drops cached responses of a user (all systems, or one). */
  invalidate(userId: string, system?: SourceSystem): void;
  readonly mock: boolean;
}

interface CacheEntry {
  expires: number;
  value: Promise<unknown>;
}

const MAX_CACHE_ENTRIES = 2000;

const isAuthError = (e: unknown): boolean =>
  e instanceof AdapterAuthError || (e instanceof Error && (e as { code?: unknown }).code === "ADAPTER_AUTH");
const isUnavailable = (e: unknown): boolean =>
  e instanceof AdapterUnavailableError || (e instanceof Error && (e as { code?: unknown }).code === "ADAPTER_UNAVAILABLE");

export function createRegistry(config: AppConfig, store: Store): AdapterRegistry {
  const mock = config.adapterMode === "mock";
  const cache = new Map<string, CacheEntry>();

  const buildUsos = (creds: UsosCredentials): UsosAdapter => createUsosAdapter(creds, config.usos);
  const buildPlaton = (creds: PlatonCredentials): PlatonAdapter => createPlatonAdapter(creds, config.platon);

  function prune(now: number): void {
    if (cache.size < MAX_CACHE_ENTRIES) return;
    for (const [k, v] of cache) if (v.expires <= now) cache.delete(k);
    // Still too big: drop the oldest insertions.
    for (const k of cache.keys()) {
      if (cache.size < MAX_CACHE_ENTRIES) break;
      cache.delete(k);
    }
  }

  async function call<S extends SourceSystem, A, T>(
    userId: string,
    system: S,
    build: () => A,
    fn: (a: A) => Promise<T>,
    opts: CallOptions
  ): Promise<T> {
    const run = async (): Promise<T> => {
      try {
        return await fn(build());
      } catch (e) {
        if (isAuthError(e)) {
          if (!mock) store.setConnectionStatus(userId, system, "expired");
          const prefix = `${userId}\u0000${system}\u0000`;
          for (const k of cache.keys()) if (k.startsWith(prefix)) cache.delete(k);
          throw adapterAuth(system);
        }
        if (isUnavailable(e)) throw adapterUnavailable(system, e instanceof Error ? e.message : undefined);
        throw e;
      }
    };

    const ttl = opts.ttlMs ?? config.cacheTtlMs;
    const cacheKey = opts.cacheKey;
    const cacheable = !!cacheKey && ttl > 0;
    const fullKey = `${userId}\u0000${system}\u0000${cacheKey ?? ""}`;
    const now = Date.now();
    if (cacheable) {
      const hit = cache.get(fullKey);
      if (hit && hit.expires > now) return hit.value as Promise<T>;
    }
    const p = run();
    if (cacheable) {
      prune(now);
      cache.set(fullKey, { expires: now + ttl, value: p });
      p.catch(() => {
        if (cache.get(fullKey)?.value === p) cache.delete(fullKey);
      });
    }
    return p;
  }

  return {
    mock,
    buildUsos,
    buildPlaton,

    usos(userId, fn, opts = {}) {
      return call(
        userId,
        "usos",
        () => {
          if (mock) return buildUsos({ mode: "mock" });
          const conn = store.getConnection(userId, "usos");
          if (!conn) throw notConnected("usos");
          if (conn.status === "expired") throw adapterAuth("usos");
          return buildUsos(conn.creds);
        },
        fn,
        opts
      );
    },

    platon(userId, fn, opts = {}) {
      return call(
        userId,
        "platon",
        () => {
          if (mock) return buildPlaton({ mode: "mock" });
          const conn = store.getConnection(userId, "platon");
          if (!conn) throw notConnected("platon");
          if (conn.status === "expired") throw adapterAuth("platon");
          return buildPlaton(conn.creds);
        },
        fn,
        opts
      );
    },

    statuses(userId) {
      const real = store.listConnections(userId);
      if (!mock) return real;
      const status: ConnectionStatus = "connected";
      return {
        usos: { ...real.usos, status, mode: "mock" },
        platon: { ...real.platon, status, mode: "mock" }
      };
    },

    invalidate(userId, system) {
      const prefix = system ? `${userId}\u0000${system}\u0000` : `${userId}\u0000`;
      for (const k of cache.keys()) if (k.startsWith(prefix)) cache.delete(k);
    }
  };
}
