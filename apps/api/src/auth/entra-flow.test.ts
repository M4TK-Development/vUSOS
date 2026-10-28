import { afterEach, describe, expect, it } from "vitest";
import { makeApp, testConfig } from "../testing.js";
import type { EntraClient, EntraIdentity } from "./entra.js";

let t: ReturnType<typeof makeApp> | undefined;
afterEach(async () => {
  await t?.app.close();
  try {
    t?.db.close();
  } catch {
    /* already closed */
  }
});

function fakeEntra(identity: EntraIdentity | Error): EntraClient {
  return {
    startLogin: async () => ({
      url: "https://login.example/authorize?x=1",
      transient: { state: "st", nonce: "nn", verifier: "vv" }
    }),
    finishLogin: async (q, tr) => {
      if (q.state !== tr.state) throw new Error("state mismatch");
      if (identity instanceof Error) throw identity;
      return identity;
    }
  };
}

const student: EntraIdentity = { id: "tid1.oid1", tenantId: "tid1", email: "s@stu.vistula.edu.pl", name: "Student" };
const env = { AUTH_MODE: "entra", ENTRA_CLIENT_ID: "cid", ENTRA_CLIENT_SECRET: "sec", ALLOWED_EMAIL_DOMAINS: "stu.vistula.edu.pl", ALLOWED_TENANT_IDS: "" };

async function start(client?: string) {
  const res = await t.app.inject({ method: "GET", url: `/api/auth/login${client ? `?client=${client}` : ""}` });
  expect(res.statusCode).toBe(302);
  const c = res.cookies.find((x) => x.name === "vusos_session");
  return `vusos_session=${encodeURIComponent(c?.value ?? "")}`;
}

describe("entra auth flow (fake MSAL client)", () => {
  it("logs an allowed user in and redirects to WEB_ORIGIN", async () => {
    t = makeApp(env, { entra: fakeEntra(student) });
    const cookie = await start();
    const cb = await t.app.inject({ method: "GET", url: "/api/auth/callback?code=abc&state=st", headers: { cookie } });
    expect(cb.statusCode).toBe(302);
    expect(cb.headers.location).toBe("http://localhost:5173");
    const c = cb.cookies.find((x) => x.name === "vusos_session");
    const me = await t.app.inject({ method: "GET", url: "/api/me", headers: { cookie: `vusos_session=${encodeURIComponent(c?.value ?? "")}` } });
    expect(me.json().user).toMatchObject({ id: "tid1.oid1", email: "s@stu.vistula.edu.pl" });
  });

  it("answers 403 with a Polish message for foreign tenants/domains", async () => {
    t = makeApp(env, { entra: fakeEntra({ ...student, tenantId: "other", email: "x@gmail.com" }) });
    const cookie = await start();
    const cb = await t.app.inject({ method: "GET", url: "/api/auth/callback?code=abc&state=st", headers: { cookie, accept: "text/html" } });
    expect(cb.statusCode).toBe(403);
    expect(cb.body).toContain("Dostęp do vUSOS");
    const json = await t.app.inject({ method: "GET", url: "/api/auth/callback?code=abc&state=st", headers: { cookie } });
    expect(json.statusCode).toBe(403);
    expect(json.json().code).toBe("FORBIDDEN");
  });

  it("accepts a user whose tenant id is allow-listed", async () => {
    t = makeApp({ ...env, ALLOWED_EMAIL_DOMAINS: "", ALLOWED_TENANT_IDS: "TID1" }, { entra: fakeEntra({ ...student, email: "weird@alias.example" }) });
    const cookie = await start();
    const cb = await t.app.inject({ method: "GET", url: "/api/auth/callback?code=abc&state=st", headers: { cookie } });
    expect(cb.statusCode).toBe(302);
  });

  it("rejects a mismatched state", async () => {
    t = makeApp(env, { entra: fakeEntra(student) });
    const cookie = await start();
    const cb = await t.app.inject({ method: "GET", url: "/api/auth/callback?code=abc&state=WRONG", headers: { cookie } });
    expect(cb.statusCode).toBe(400);
  });

  it("keeps client=native through the flow and redirects to vusos://auth?code=", async () => {
    t = makeApp(env, { entra: fakeEntra(student) });
    const cookie = await start("native");
    const cb = await t.app.inject({ method: "GET", url: "/api/auth/callback?code=abc&state=st", headers: { cookie } });
    expect(cb.statusCode).toBe(302);
    const loc = new URL(String(cb.headers.location));
    expect(loc.protocol).toBe("vusos:");
    const ex = await t.app.inject({ method: "POST", url: "/api/auth/native/exchange", payload: { code: loc.searchParams.get("code") } });
    expect(ex.statusCode).toBe(200);
    expect(ex.json().user.email).toBe("s@stu.vistula.edu.pl");
  });

  it("refuses to start in entra mode without an allow-list", () => {
    expect(() => testConfig({ ...env, ALLOWED_EMAIL_DOMAINS: "" })).toThrow(/ALLOWED/);
  });
});
