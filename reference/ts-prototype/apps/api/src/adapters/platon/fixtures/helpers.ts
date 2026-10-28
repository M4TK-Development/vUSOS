import { readFileSync } from "node:fs";
import type { MoodleTransport } from "../transport.js";

export function fixture(name: string): unknown {
  return JSON.parse(readFileSync(new URL(`./${name}.json`, import.meta.url), "utf8")) as unknown;
}

export interface RecordedCall {
  fn: string;
  args: Record<string, unknown>;
}

type Handler = (args: Record<string, unknown>) => unknown;

/** Fake transport: answers wsfunctions from a table of raw Moodle JSON (or handlers) and records calls. */
export function fakeTransport(
  responses: Record<string, unknown | Handler>,
  opts: { rewrite?: (u: string) => string; headers?: Record<string, string> } = {}
): MoodleTransport & { calls: RecordedCall[] } {
  const calls: RecordedCall[] = [];
  return {
    calls,
    call<T>(fn: string, args: Record<string, unknown> = {}): Promise<T> {
      calls.push({ fn, args });
      if (!(fn in responses)) return Promise.reject(new Error(`unexpected call ${fn}`));
      const r = responses[fn];
      try {
        return Promise.resolve((typeof r === "function" ? (r as Handler)(args) : r) as T);
      } catch (e) {
        return Promise.reject(e);
      }
    },
    fileHeaders: () => opts.headers ?? {},
    rewriteFileUrl: opts.rewrite ?? ((u) => u)
  };
}
