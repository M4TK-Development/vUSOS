import { afterEach, describe, expect, it } from "vitest";
import { loginDev, makeApp } from "../testing.js";

type TestApp = ReturnType<typeof makeApp>;
let t: TestApp;
afterEach(async () => {
  await t.app.close();
  t.db.close();
});

describe("dev auth", () => {
  it("renders the login form", async () => {
    t = makeApp();
    const res = await t.app.inject({ method: "GET", url: "/api/auth/login" });
    expect(res.statusCode).toBe(200);
    expect(res.headers["content-type"]).toContain("text/html");
    expect(res.body).toContain('name="email"');
    expect(res.body).toContain('name="name"');
  });

  it("logs in via form post, sets an HttpOnly Lax cookie and redirects to WEB_ORIGIN", async () => {
    t = makeApp();
    const res = await t.app.inject({
      method: "POST",
      url: "/api/auth/login",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      payload: "email=Jan%40stu.vistula.edu.pl&name=Jan+Kowalski"
    });
    expect(res.statusCode).toBe(303);
    expect(res.headers.location).toBe("http://localhost:5173");
    const raw = String(res.headers["set-cookie"]);
    expect(raw).toMatch(/HttpOnly/i);
    expect(raw).toMatch(/SameSite=Lax/i);
  });

  it("rejects /api/me without a session", async () => {
    t = makeApp();
    const res = await t.app.inject({ method: "GET", url: "/api/me" });
    expect(res.statusCode).toBe(401);
    expect(res.json()).toMatchObject({ code: "UNAUTHENTICATED" });
  });

  it("returns user and connections for a logged-in user, then logs out", async () => {
    t = makeApp();
    const cookie = await loginDev(t.app);
    const me = await t.app.inject({ method: "GET", url: "/api/me", headers: { cookie } });
    expect(me.statusCode).toBe(200);
    const body = me.json();
    expect(body.user).toMatchObject({ email: "jan@stu.vistula.edu.pl", name: "Jan Kowalski" });
    expect(body.connections.usos.status).toBe("connected"); // ADAPTER_MODE=mock
    expect(body.connections.platon.status).toBe("connected");

    const out = await t.app.inject({ method: "POST", url: "/api/auth/logout", headers: { cookie } });
    expect(out.statusCode).toBe(200);
    const clearedCookie = out.cookies.find((c) => c.name === "vusos_session");
    const after = await t.app.inject({
      method: "GET",
      url: "/api/me",
      headers: { cookie: clearedCookie?.value ? `vusos_session=${encodeURIComponent(clearedCookie.value)}` : "" }
    });
    expect(after.statusCode).toBe(401);
  });

  it("reports disconnected systems in live adapter mode", async () => {
    t = makeApp({ ADAPTER_MODE: "live" });
    const cookie = await loginDev(t.app);
    const me = await t.app.inject({ method: "GET", url: "/api/me", headers: { cookie } });
    expect(me.json().connections.usos).toMatchObject({ status: "disconnected", mode: null });
  });

  it("rejects an invalid e-mail", async () => {
    t = makeApp();
    const res = await t.app.inject({ method: "POST", url: "/api/auth/login", payload: { email: "nope", name: "x" } });
    expect(res.statusCode).toBe(400);
  });

  it("refuses state-changing requests from a foreign Origin", async () => {
    t = makeApp();
    const res = await t.app.inject({
      method: "POST",
      url: "/api/auth/login",
      headers: { origin: "https://evil.example" },
      payload: { email: "a@b.pl" }
    });
    expect(res.statusCode).toBe(403);
  });
});

describe("native login and bearer auth", () => {
  async function nativeLogin(): Promise<{ code: string; location: string }> {
    const res = await t.app.inject({
      method: "POST",
      url: "/api/auth/login",
      payload: { email: "ola@stu.vistula.edu.pl", name: "Ola", client: "native" }
    });
    expect(res.statusCode).toBe(302);
    const location = String(res.headers.location);
    expect(res.cookies.find((c) => c.name === "vusos_session")).toBeUndefined();
    const code = new URL(location).searchParams.get("code");
    expect(code).toBeTruthy();
    return { code: code as string, location };
  }

  it("serves the dev form with the native client flag", async () => {
    t = makeApp();
    const res = await t.app.inject({ method: "GET", url: "/api/auth/login?client=native" });
    expect(res.body).toContain('name="client" value="native"');
  });

  it("redirects to vusos://auth?code=... and exchanges the code once for a bearer token", async () => {
    t = makeApp();
    const { code, location } = await nativeLogin();
    expect(location.startsWith("vusos://auth?code=")).toBe(true);

    const ex = await t.app.inject({ method: "POST", url: "/api/auth/native/exchange", payload: { code } });
    expect(ex.statusCode).toBe(200);
    const { token, user } = ex.json();
    expect(token.length).toBeGreaterThanOrEqual(40);
    expect(user.email).toBe("ola@stu.vistula.edu.pl");

    const again = await t.app.inject({ method: "POST", url: "/api/auth/native/exchange", payload: { code } });
    expect(again.statusCode).toBe(400);
    expect(again.json().code).toBe("INVALID_CODE");

    const me = await t.app.inject({ method: "GET", url: "/api/me", headers: { authorization: `Bearer ${token}` } });
    expect(me.statusCode).toBe(200);
    expect(me.json().user.email).toBe("ola@stu.vistula.edu.pl");

    const bad = await t.app.inject({ method: "GET", url: "/api/me", headers: { authorization: "Bearer nope" } });
    expect(bad.statusCode).toBe(401);
  });

  it("rejects expired auth codes", async () => {
    t = makeApp();
    const { code } = await nativeLogin();
    t.db.prepare("UPDATE auth_codes SET created_at = ?").run(new Date(Date.now() - 3 * 60_000).toISOString());
    const ex = await t.app.inject({ method: "POST", url: "/api/auth/native/exchange", payload: { code } });
    expect(ex.statusCode).toBe(400);
  });

  it("stores only a hash of the token, slides expiry on use, and revokes on logout", async () => {
    t = makeApp();
    const { code } = await nativeLogin();
    const { token } = (await t.app.inject({ method: "POST", url: "/api/auth/native/exchange", payload: { code } })).json();

    const row = t.db.prepare("SELECT token_hash, expires_at FROM api_tokens").get() as { token_hash: string; expires_at: string };
    expect(row.token_hash).not.toContain(token);
    // Pretend the token was issued long ago and is close to expiry.
    const soon = new Date(Date.now() + 60_000).toISOString();
    t.db.prepare("UPDATE api_tokens SET expires_at = ?").run(soon);
    await t.app.inject({ method: "GET", url: "/api/me", headers: { authorization: `Bearer ${token}` } });
    const slid = t.db.prepare("SELECT expires_at FROM api_tokens").get() as { expires_at: string };
    expect(Date.parse(slid.expires_at)).toBeGreaterThan(Date.now() + 80 * 24 * 3600 * 1000);

    const out = await t.app.inject({ method: "POST", url: "/api/auth/logout", headers: { authorization: `Bearer ${token}` } });
    expect(out.statusCode).toBe(200);
    const after = await t.app.inject({ method: "GET", url: "/api/me", headers: { authorization: `Bearer ${token}` } });
    expect(after.statusCode).toBe(401);
  });

  it("rejects expired bearer tokens", async () => {
    t = makeApp();
    const { code } = await nativeLogin();
    const { token } = (await t.app.inject({ method: "POST", url: "/api/auth/native/exchange", payload: { code } })).json();
    t.db.prepare("UPDATE api_tokens SET expires_at = ?").run(new Date(Date.now() - 1000).toISOString());
    const res = await t.app.inject({ method: "GET", url: "/api/me", headers: { authorization: `Bearer ${token}` } });
    expect(res.statusCode).toBe(401);
  });

  it("allows native shell origins in CORS", async () => {
    t = makeApp();
    for (const origin of ["capacitor://localhost", "http://localhost", "tauri://localhost", "https://tauri.localhost", "http://localhost:5173"]) {
      const res = await t.app.inject({
        method: "OPTIONS",
        url: "/api/me",
        headers: { origin, "access-control-request-method": "GET", "access-control-request-headers": "authorization" }
      });
      expect(res.headers["access-control-allow-origin"], origin).toBe(origin);
      expect(res.headers["access-control-allow-credentials"]).toBe("true");
    }
    const evil = await t.app.inject({
      method: "OPTIONS",
      url: "/api/me",
      headers: { origin: "https://evil.example", "access-control-request-method": "GET" }
    });
    expect(evil.headers["access-control-allow-origin"]).toBeUndefined();
  });
});
