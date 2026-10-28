import { readFileSync } from "node:fs";

/** Test helper: reads a fixture file next to this module. */
export function fixture(name: string): string {
  return readFileSync(new URL(name, import.meta.url), "utf8");
}

export function fixtureJson(name: string): unknown {
  return JSON.parse(fixture(name));
}
