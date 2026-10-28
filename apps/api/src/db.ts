import { mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import type { DatabaseSync } from "node:sqlite";

export type Db = DatabaseSync;

// `node:sqlite` is loaded through createRequire: Vite/Vitest cannot resolve the `node:`-only builtin via ESM import.
const require = createRequire(import.meta.url);

const SCHEMA = `
CREATE TABLE IF NOT EXISTS users (
  id         TEXT PRIMARY KEY,
  email      TEXT NOT NULL,
  name       TEXT NOT NULL,
  tenant_id  TEXT,
  created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS connections (
  user_id     TEXT NOT NULL,
  system      TEXT NOT NULL,
  mode        TEXT NOT NULL,
  payload_enc TEXT NOT NULL,
  status      TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  PRIMARY KEY (user_id, system)
);
CREATE TABLE IF NOT EXISTS oauth_states (
  state      TEXT PRIMARY KEY,
  user_id    TEXT NOT NULL,
  system     TEXT NOT NULL,
  data_enc   TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS api_tokens (
  token_hash   TEXT PRIMARY KEY,
  user_id      TEXT NOT NULL,
  client       TEXT NOT NULL,
  created_at   TEXT NOT NULL,
  last_used_at TEXT NOT NULL,
  expires_at   TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS auth_codes (
  code_hash  TEXT PRIMARY KEY,
  user_id    TEXT NOT NULL,
  created_at TEXT NOT NULL
);
`;

/** Opens (creating directories and schema as needed) a SQLite database. Use ":memory:" in tests. */
export function openDb(path: string): Db {
  const { DatabaseSync: Database } = require("node:sqlite") as typeof import("node:sqlite");
  if (path !== ":memory:") {
    mkdirSync(dirname(resolve(path)), { recursive: true });
  }
  const db = new Database(path);
  if (path !== ":memory:") db.exec("PRAGMA journal_mode = WAL;");
  db.exec("PRAGMA foreign_keys = ON;");
  db.exec(SCHEMA);
  return db;
}
