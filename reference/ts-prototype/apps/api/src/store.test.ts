import { randomBytes } from "node:crypto";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { openDb } from "./db.js";
import { createStore } from "./store.js";

const setup = () => {
  const key = randomBytes(32);
  const db = openDb(":memory:");
  return { key, db, store: createStore(db, key) };
};

describe("store", () => {
  it("roundtrips encrypted connections and never stores plaintext", () => {
    const { db, store } = setup();
    const user = store.upsertUser({ id: "u1", email: "a@b.pl", name: "A", tenantId: null });
    expect(store.listConnections(user.id).usos.status).toBe("disconnected");

    store.saveConnection("u1", "platon", { mode: "ws", wstoken: "SECRET-WSTOKEN-0123456789", baseUrl: "https://p/" });
    store.saveConnection("u1", "usos", { mode: "api", accessToken: "AT", accessTokenSecret: "ATS", baseUrl: "https://u/" });

    const got = store.getConnection("u1", "platon");
    expect(got?.creds).toEqual({ mode: "ws", wstoken: "SECRET-WSTOKEN-0123456789", baseUrl: "https://p/" });
    expect(got?.status).toBe("connected");

    const raw = db.prepare("SELECT payload_enc, mode FROM connections WHERE system = 'platon'").get() as { payload_enc: string; mode: string };
    expect(raw.payload_enc).not.toContain("SECRET-WSTOKEN");
    expect(raw.mode).toBe("ws");

    expect(store.listConnections("u1")).toMatchObject({ usos: { mode: "api", status: "connected" }, platon: { mode: "ws" } });
    expect(store.listConnections("someone-else").platon.status).toBe("disconnected");

    store.setConnectionStatus("u1", "platon", "expired");
    expect(store.getConnection("u1", "platon")?.status).toBe("expired");

    store.saveConnection("u1", "platon", { mode: "mock" });
    expect(store.getConnection("u1", "platon")?.creds).toEqual({ mode: "mock" });

    store.deleteConnection("u1", "platon");
    expect(store.getConnection("u1", "platon")).toBeNull();
  });

  it("cannot decrypt with a different key or when the row is moved to another user", () => {
    const { db, store } = setup();
    store.saveConnection("u1", "usos", { mode: "web", cookies: "a=b", baseUrl: "https://u/" });
    expect(createStore(db, randomBytes(32)).getConnection("u1", "usos")).toBeNull();
    db.prepare("UPDATE connections SET user_id = 'u2'").run();
    expect(store.getConnection("u2", "usos")).toBeNull();
  });

  it("keeps oauth state one-shot and system-bound", () => {
    const { store } = setup();
    store.saveOAuthState("tok", "u1", "usos", { secret: "s3", client: "native" });
    expect(store.consumeOAuthState("tok", "platon")).toBeNull();
    store.saveOAuthState("tok", "u1", "usos", { secret: "s3", client: "native" });
    expect(store.consumeOAuthState("tok", "usos")).toEqual({ userId: "u1", data: { secret: "s3", client: "native" } });
    expect(store.consumeOAuthState("tok", "usos")).toBeNull();
  });

  it("persists to a file database in a freshly created directory", () => {
    const dir = mkdtempSync(join(tmpdir(), "vusos-"));
    try {
      const path = join(dir, "nested", "dir", "db.sqlite");
      const db = openDb(path);
      createStore(db, randomBytes(32)).upsertUser({ id: "x", email: "e@e.pl", name: "E", tenantId: "t" });
      db.close();
      const again = openDb(path);
      expect((again.prepare("SELECT COUNT(*) AS n FROM users").get() as { n: number }).n).toBe(1);
      again.close();
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
