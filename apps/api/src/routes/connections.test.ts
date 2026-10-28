import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { AdapterAuthError } from "@vusos/shared";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { loginDev, makeApp } from "../testing.js";

const h = vi.hoisted(() => ({
  usos: {} as Record<string, unknown>,
  platon: {} as Record<string, unknown>,
  built: [] as unknown[]
}));
vi.mock("../adapters/usos/index.js", () => ({
  createUsosAdapter: (creds: unknown) => { h.built.push(creds); return h.usos; },
  createUsosOAuthClient: () => ({})
}));
vi.mock("../adapters/platon/index.js", () => ({
  createPlatonAdapter: (creds: unknown) => { h.built.push(creds); return h.platon; }
}));

const TOKEN = "0123456789abcdef0123456789abcdef";
const UID = "dev:jan@stu.vistula.edu.pl";
let t: ReturnType<typeof makeApp>;
const post = (url: string, cookie: string, payload: unknown) => t.app.inject({ method: "POST", url, headers: { cookie }, payload: payload as object });

beforeEach(() => {
  h.built.length = 0;
  h.usos = { getProfile: async () => ({ id: "1" }) };
  h.platon = { getSiteInfo: async () => ({ userId: 42, fullName: "Jan", siteName: "Platon", release: null }) };
});
afterEach(async () => {
  vi.unstubAllGlobals();
  await t.app.close();
  t.db.close();
});

describe("platon connections (live mode)", () => {
  beforeEach(() => { t = makeApp({ ADAPTER_MODE: "live" }); });

  it("accepts a raw wstoken, validates it and stores it encrypted with the Moodle user id", async () => {
    const cookie = await loginDev(t.app);
    const res = await post("/api/connections/platon/ws", cookie, { wstoken: TOKEN });
    expect(res.statusCode).toBe(200);
    expect(res.json()).toMatchObject({ system: "platon", status: "connected", mode: "ws" });
    expect(t.app.ctx.store.getConnection(UID, "platon")?.creds).toEqual({
      mode: "ws", wstoken: TOKEN, baseUrl: "https://platon.example.edu/", userId: 42
    });
    const raw = t.db.prepare("SELECT payload_enc FROM connections").get() as { payload_enc: string };
    expect(raw.payload_enc).not.toContain(TOKEN);
  });

  it("decodes a vusos://token=BASE64 launchUrl including the private token", async () => {
    const cookie = await loginDev(t.app);
    const b64 = Buffer.from(`12345:::${TOKEN}:::privateprivate0123`).toString("base64");
    const res = await post("/api/connections/platon/ws", cookie, { launchUrl: `vusos://token=${b64}` });
    expect(res.statusCode).toBe(200);
    expect(t.app.ctx.store.getConnection(UID, "platon")?.creds).toMatchObject({ wstoken: TOKEN, privateToken: "privateprivate0123" });
  });

  it("returns 400 for garbage and 424 (without storing) when validation hits an auth error", async () => {
    const cookie = await loginDev(t.app);
    expect((await post("/api/connections/platon/ws", cookie, { wstoken: "!!!" })).statusCode).toBe(400);
    expect((await post("/api/connections/platon/ws", cookie, {})).statusCode).toBe(400);

    h.platon = { getSiteInfo: async () => { throw new AdapterAuthError(); } };
    const res = await post("/api/connections/platon/ws", cookie, { wstoken: TOKEN });
    expect(res.statusCode).toBe(424);
    expect(res.json()).toMatchObject({ code: "ADAPTER_AUTH", system: "platon" });
    expect(t.app.ctx.store.getConnection(UID, "platon")).toBeNull();
  });

  it("stores web fallbacks and lists / deletes connections", async () => {
    const cookie = await loginDev(t.app);
    expect((await post("/api/connections/platon/web", cookie, { cookies: "MoodleSession=abc", sesskey: "sesskey1" })).statusCode).toBe(200);
    expect((await post("/api/connections/usos/web", cookie, { cookies: "PHPSESSID=xyz" })).statusCode).toBe(200);
    const list = await t.app.inject({ method: "GET", url: "/api/connections", headers: { cookie } });
    expect(list.json()).toMatchObject({ usos: { status: "connected", mode: "web" }, platon: { status: "connected", mode: "web" } });
    expect(JSON.stringify(list.json())).not.toContain("abc");

    const del = await t.app.inject({ method: "DELETE", url: "/api/connections/platon", headers: { cookie } });
    expect(del.statusCode).toBe(200);
    expect(del.json()).toMatchObject({ system: "platon", status: "disconnected" });
    expect((await t.app.inject({ method: "DELETE", url: "/api/connections/nope", headers: { cookie } })).statusCode).toBe(400);
  });
});

describe("USOS OAuth (live mode, fake OAuth client)", () => {
  const oauth = {
    requestToken: vi.fn(async (_cb: string, _scopes: string[]) => ({ token: "REQTOK", secret: "REQSEC", authorizeUrl: "https://usos.example/authorize?oauth_token=REQTOK" })),
    accessToken: vi.fn(async () => ({ token: "ACCTOK", secret: "ACCSEC" }))
  };
  beforeEach(() => {
    oauth.requestToken.mockClear();
    oauth.accessToken.mockClear();
    t = makeApp({ ADAPTER_MODE: "live", USOS_CONSUMER_KEY: "ck", USOS_CONSUMER_SECRET: "cs" }, { usosOAuthClient: oauth });
  });

  it("start -> callback (web) saves the access token and redirects to /settings", async () => {
    const cookie = await loginDev(t.app);
    const start = await post("/api/connections/usos/oauth/start", cookie, {});
    expect(start.json()).toEqual({ authorizeUrl: "https://usos.example/authorize?oauth_token=REQTOK" });
    expect(oauth.requestToken).toHaveBeenCalledWith("http://localhost:3001/api/connections/usos/oauth/callback", [
      "offline_access", "studies", "grades", "payments", "email", "mailclient", "personal", "photo"
    ]);
    const st = t.db.prepare("SELECT data_enc FROM oauth_states").get() as { data_enc: string };
    expect(st.data_enc).not.toContain("REQSEC");

    // The callback is opened without our session cookie on purpose: the state row identifies the user.
    const cb = await t.app.inject({ method: "GET", url: "/api/connections/usos/oauth/callback?oauth_token=REQTOK&oauth_verifier=VER" });
    expect(cb.statusCode).toBe(302);
    expect(cb.headers.location).toBe("http://localhost:5173/settings?connected=usos");
    expect(oauth.accessToken).toHaveBeenCalledWith("REQTOK", "REQSEC", "VER");
    expect(t.app.ctx.store.getConnection(UID, "usos")?.creds).toEqual({
      mode: "api", accessToken: "ACCTOK", accessTokenSecret: "ACCSEC", baseUrl: "https://usosapps.example.edu/"
    });

    // one-shot
    const replay = await t.app.inject({ method: "GET", url: "/api/connections/usos/oauth/callback?oauth_token=REQTOK&oauth_verifier=VER" });
    expect(replay.headers.location).toBe("http://localhost:5173/settings?error=usos");
  });

  it("redirects native clients to vusos://connected", async () => {
    const cookie = await loginDev(t.app);
    await post("/api/connections/usos/oauth/start", cookie, { client: "native" });
    const cb = await t.app.inject({ method: "GET", url: "/api/connections/usos/oauth/callback?oauth_token=REQTOK&oauth_verifier=VER" });
    expect(cb.headers.location).toBe("vusos://connected?system=usos");
  });

  it("reports a failed/denied authorization to the right client", async () => {
    const cookie = await loginDev(t.app);
    await post("/api/connections/usos/oauth/start", cookie, { client: "native" });
    const cb = await t.app.inject({ method: "GET", url: "/api/connections/usos/oauth/callback?oauth_token=REQTOK" });
    expect(cb.headers.location).toBe("vusos://connected?system=usos&error=failed");
    expect(t.app.ctx.store.getConnection(UID, "usos")).toBeNull();
  });
});

describe("USOS OAuth (mock mode)", () => {
  it("round-trips without calling USOS", async () => {
    t = makeApp({ ADAPTER_MODE: "mock" });
    const cookie = await loginDev(t.app);
    const { authorizeUrl } = (await post("/api/connections/usos/oauth/start", cookie, {})).json();
    const cb = await t.app.inject({ method: "GET", url: authorizeUrl.replace("http://localhost:3001", "") });
    expect(cb.headers.location).toBe("http://localhost:5173/settings?connected=usos");
  });

  it("returns 503 NOT_CONFIGURED in live mode without a consumer key", async () => {
    t = makeApp({ ADAPTER_MODE: "live" });
    const cookie = await loginDev(t.app);
    const res = await post("/api/connections/usos/oauth/start", cookie, {});
    expect(res.statusCode).toBe(503);
    expect(res.json().code).toBe("NOT_CONFIGURED");
  });
});

describe("GET /api/platon/file", () => {
  beforeEach(() => { t = makeApp({ ADAPTER_MODE: "mock" }); });

  it("rejects URLs on other hosts and bad schemes", async () => {
    const cookie = await loginDev(t.app);
    for (const url of ["https://evil.example/file.pdf", "http://platon.example.edu/f.pdf", "https://platon.example.edu.evil.com/f", "https://u:p@platon.example.edu/f"]) {
      const res = await t.app.inject({ method: "GET", url: `/api/platon/file?url=${encodeURIComponent(url)}`, headers: { cookie } });
      expect(res.statusCode, url).toBe(400);
    }
    expect((await t.app.inject({ method: "GET", url: "/api/platon/file", headers: { cookie } })).statusCode).toBe(400);
  });

  it("streams the file with forwarded headers and an authenticated upstream request", async () => {
    const cookie = await loginDev(t.app);
    h.platon = { getFileDownload: async (u: string) => ({ url: `${u}?token=T`, headers: { Authorization: "x" } }) };
    const fetchMock = vi.fn(async () =>
      new Response("PDFDATA", { status: 200, headers: { "content-type": "application/pdf", "content-disposition": 'inline; filename="a.pdf"' } })
    );
    vi.stubGlobal("fetch", fetchMock);
    const file = "https://platon.example.edu/pluginfile.php/1/mod_resource/content/1/a.pdf";
    const res = await t.app.inject({ method: "GET", url: `/api/platon/file?url=${encodeURIComponent(file)}`, headers: { cookie } });
    expect(res.statusCode).toBe(200);
    expect(res.body).toBe("PDFDATA");
    expect(res.headers["content-type"]).toBe("application/pdf");
    expect(res.headers["content-disposition"]).toContain("a.pdf");
    expect(res.headers["x-content-type-options"]).toBe("nosniff");
    expect(fetchMock).toHaveBeenCalledWith(`${file}?token=T`, expect.objectContaining({ headers: { Authorization: "x" }, redirect: "manual" }));
  });

  it("forces download of active content and maps a login redirect to ADAPTER_AUTH", async () => {
    const cookie = await loginDev(t.app);
    h.platon = { getFileDownload: async (u: string) => ({ url: u, headers: {} }) };
    const file = "https://platon.example.edu/pluginfile.php/1/x.html";
    vi.stubGlobal("fetch", vi.fn(async () => new Response("<script>1</script>", { status: 200, headers: { "content-type": "text/html" } })));
    const res = await t.app.inject({ method: "GET", url: `/api/platon/file?url=${encodeURIComponent(file)}`, headers: { cookie } });
    expect(res.headers["content-disposition"]).toBe("attachment");

    vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 303, headers: { location: "/login/index.php" } })));
    const res2 = await t.app.inject({ method: "GET", url: `/api/platon/file?url=${encodeURIComponent(file)}`, headers: { cookie } });
    expect(res2.statusCode).toBe(424);
  });
});

describe("static serving", () => {
  it("serves the SPA with index.html fallback but keeps /api JSON 404s", async () => {
    const dir = mkdtempSync(join(tmpdir(), "vusos-static-"));
    try {
      writeFileSync(join(dir, "index.html"), "<!doctype html><title>vUSOS</title>");
      writeFileSync(join(dir, "app.js"), "console.log(1)");
      t = makeApp({ STATIC_DIR: dir });
      const html = { accept: "text/html" };
      expect((await t.app.inject({ method: "GET", url: "/", headers: html })).body).toContain("vUSOS");
      expect((await t.app.inject({ method: "GET", url: "/app.js" })).statusCode).toBe(200);
      expect((await t.app.inject({ method: "GET", url: "/settings/deep/link", headers: html })).body).toContain("vUSOS");
      const api = await t.app.inject({ method: "GET", url: "/api/nope", headers: html });
      expect(api.statusCode).toBe(404);
      expect(api.json().code).toBe("NOT_FOUND");
      expect((await t.app.inject({ method: "GET", url: "/api/health" })).json()).toEqual({ ok: true });
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
