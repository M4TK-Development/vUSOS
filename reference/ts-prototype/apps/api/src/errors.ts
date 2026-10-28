import type { SourceSystem } from "@vusos/shared";

/** An error with a well-defined HTTP representation (see the error contract in docs/ARCHITECTURE.md). */
export class HttpError extends Error {
  constructor(
    readonly statusCode: number,
    readonly body: { code: string; message?: string; system?: SourceSystem; [k: string]: unknown }
  ) {
    super(body.message ?? body.code);
  }
}

export const unauthenticated = (): HttpError =>
  new HttpError(401, { code: "UNAUTHENTICATED", message: "Brak aktywnej sesji. Zaloguj się." });

export const notConnected = (system: SourceSystem): HttpError =>
  new HttpError(409, { code: "NOT_CONNECTED", system, message: `System ${system} nie jest połączony.` });

export const adapterAuth = (system: SourceSystem): HttpError =>
  new HttpError(424, { code: "ADAPTER_AUTH", system, message: `Sesja w systemie ${system} wygasła. Połącz ponownie.` });

export const adapterUnavailable = (system: SourceSystem, message?: string): HttpError =>
  new HttpError(502, {
    code: "ADAPTER_UNAVAILABLE",
    system,
    message: message ?? `System ${system} jest chwilowo niedostępny.`
  });

export const badRequest = (message: string, extra: Record<string, unknown> = {}): HttpError =>
  new HttpError(400, { code: "BAD_REQUEST", message, ...extra });
