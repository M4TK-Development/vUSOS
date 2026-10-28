import type { SourceSystem } from "@vusos/shared";

export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly system?: SourceSystem;

  constructor(status: number, message: string, code?: string, system?: SourceSystem) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.system = system;
  }

  /** 409 NOT_CONNECTED or 424 ADAPTER_AUTH: the backing system must be (re)connected. */
  get needsConnection(): boolean {
    return this.status === 409 || this.status === 424;
  }
  /** True when the system session expired (as opposed to never connected). */
  get expired(): boolean {
    return this.status === 424 || this.code === "ADAPTER_AUTH";
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}

const isSystem = (v: unknown): v is SourceSystem => v === "usos" || v === "platon";

export interface RequestOptions {
  method?: "GET" | "POST" | "DELETE" | "PUT" | "PATCH";
  body?: unknown;
  signal?: AbortSignal;
}

export async function api<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const { method = "GET", body, signal } = opts;
  let res: Response;
  try {
    res = await fetch(`/api${path}`, {
      method,
      credentials: "include",
      signal,
      headers: {
        Accept: "application/json",
        ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") throw e;
    throw new ApiError(0, "Brak połączenia z serwerem.", "NETWORK");
  }

  const isJson = res.headers.get("content-type")?.includes("application/json") ?? false;
  const payload: unknown = res.status === 204 ? undefined : isJson ? await res.json().catch(() => undefined) : await res.text().catch(() => undefined);

  if (!res.ok) {
    const obj = payload && typeof payload === "object" ? (payload as Record<string, unknown>) : {};
    const code = typeof obj.code === "string" ? obj.code : undefined;
    const msg =
      (typeof obj.message === "string" && obj.message) ||
      (typeof obj.error === "string" && obj.error) ||
      defaultMessage(res.status);
    throw new ApiError(res.status, msg, code, isSystem(obj.system) ? obj.system : undefined);
  }
  return payload as T;
}

function defaultMessage(status: number): string {
  switch (status) {
    case 401:
      return "Sesja wygasła. Zaloguj się ponownie.";
    case 409:
      return "System nie jest połączony.";
    case 424:
      return "Sesja w systemie uczelni wygasła.";
    case 404:
      return "Nie znaleziono zasobu.";
    default:
      return `Błąd serwera (${status}).`;
  }
}

export const platonFileUrl = (url: string) => `/api/platon/file?url=${encodeURIComponent(url)}`;
