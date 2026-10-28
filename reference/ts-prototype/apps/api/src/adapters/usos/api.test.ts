import { createHmac } from "node:crypto";
import { describe, expect, it } from "vitest";
import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";
import { usosFetch, UsosHttpError, type FetchLike } from "./client.js";
import { fixtureJson } from "./fixtures/load.js";
import { createUsosAdapter, createUsosOAuthClient } from "./index.js";

const BASE = "https://usosapps.vistula.example/";
const config = { apiBaseUrl: BASE, consumerKey: "ckey", consumerSecret: "csecret", usoswebBaseUrl: "https://usosweb.vistula.example/" };
const creds = { mode: "api" as const, accessToken: "atoken", accessTokenSecret: "asecret", baseUrl: BASE };

interface Call {
  method: string;
  path: string;
  params: Record<string, string>;
  headers: Record<string, string>;
}

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

function router(routes: Record<string, (c: Call) => Response>, calls: Call[] = []): FetchLike {
  return async (input, init) => {
    const url = new URL(input);
    const method = init?.method ?? "GET";
    const params: Record<string, string> = {};
    const source = method === "GET" ? url.searchParams : new URLSearchParams(String(init?.body ?? ""));
    for (const [k, v] of source) params[k] = v;
    const path = url.pathname.replace(/^\//, "");
    const call: Call = { method, path, params, headers: (init?.headers ?? {}) as Record<string, string> };
    calls.push(call);
    const handler = routes[path];
    if (!handler) return json({ message: `Method '${path}' not found` }, 400);
    return handler(call);
  };
}

function adapterWith(routes: Record<string, (c: Call) => Response>, calls: Call[] = []) {
  return createUsosAdapter(creds, config, { fetchImpl: router(routes, calls), now: () => new Date("2025-11-20T10:00:00Z") });
}

// --- independent OAuth 1.0a verification (RFC 5849), does not use the oauth-1.0a library ---
const enc = (s: string): string => encodeURIComponent(s).replace(/[!'()*]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
function verifySignature(call: Call, url: string, consumerSecret: string, tokenSecret: string): boolean {
  const header = call.headers.Authorization ?? "";
  const oauth: Record<string, string> = {};
  for (const m of header.replace(/^OAuth /, "").matchAll(/(\w+)="([^"]*)"/g)) oauth[m[1] ?? ""] = decodeURIComponent(m[2] ?? "");
  const { oauth_signature: sig, ...rest } = oauth;
  const all = { ...call.params, ...rest };
  const paramString = Object.entries(all)
    .map(([k, v]) => [enc(k), enc(v)] as const)
    .sort((a, b) => (a[0] === b[0] ? a[1].localeCompare(b[1]) : a[0].localeCompare(b[0])))
    .map(([k, v]) => `${k}=${v}`)
    .join("&");
  const base = `${call.method}&${enc(url)}&${enc(paramString)}`;
  const expected = createHmac("sha1", `${enc(consumerSecret)}&${enc(tokenSecret)}`).update(base).digest("base64");
  return sig === expected;
}

describe("usosFetch", () => {
  const auth = { baseUrl: BASE, consumerKey: "ckey", consumerSecret: "csecret", accessToken: "atoken", accessTokenSecret: "asecret" };

  it("signs GET requests (HMAC-SHA1) over the query, joins list params with | and drops undefined", async () => {
    const calls: Call[] = [];
    const out = await usosFetch<{ ok: boolean }>(
      "/services/users/user",
      { fields: ["id", "first_name"], note: "a b&c*ł", skip: undefined, flag: true, n: 3 },
      auth,
      { fetchImpl: router({ "services/users/user": () => json({ ok: true }) }, calls) }
    );
    expect(out).toEqual({ ok: true });
    const call = calls[0];
    expect(call?.params).toEqual({ fields: "id|first_name", note: "a b&c*ł", flag: "true", n: "3" });
    expect(call?.headers.Authorization).toMatch(/^OAuth /);
    expect(call?.headers.Authorization).toContain('oauth_signature_method="HMAC-SHA1"');
    expect(call?.headers.Authorization).toContain('oauth_consumer_key="ckey"');
    expect(call?.headers.Authorization).toContain('oauth_token="atoken"');
    expect(call && verifySignature(call, `${BASE}services/users/user`, "csecret", "asecret")).toBe(true);
  });

  it("signs POST requests over the form body", async () => {
    const calls: Call[] = [];
    await usosFetch("services/mailclient/send", { subject: "Cześć! (test)", body: "x y" }, auth, {
      method: "POST",
      fetchImpl: router({ "services/mailclient/send": () => json({ id: 1 }) }, calls)
    });
    const call = calls[0];
    expect(call?.method).toBe("POST");
    expect(call?.params).toEqual({ subject: "Cześć! (test)", body: "x y" });
    expect(call && verifySignature(call, `${BASE}services/mailclient/send`, "csecret", "asecret")).toBe(true);
  });

  it("maps 401 to AdapterAuthError", async () => {
    const f = router({ "services/x": () => json({ message: "Invalid token" }, 401) });
    const err = await usosFetch("services/x", {}, auth, { fetchImpl: f }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(AdapterAuthError);
    expect((err as Error).message).toContain("Invalid token");
  });

  it("maps other non-2xx to AdapterUnavailableError with the USOS message", async () => {
    const f = router({ "services/x": () => json({ message: "Boom" }, 500), "services/y": () => new Response("plain text", { status: 502 }) });
    const err = await usosFetch("services/x", {}, auth, { fetchImpl: f }).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(AdapterUnavailableError);
    expect(err).toBeInstanceOf(UsosHttpError);
    expect((err as Error).message).toContain("HTTP 500");
    expect((err as Error).message).toContain("Boom");
    await expect(usosFetch("services/y", {}, auth, { fetchImpl: f })).rejects.toThrow(/plain text/);
  });

  it("reports invalid JSON and network failures as unavailable", async () => {
    await expect(usosFetch("services/x", {}, auth, { fetchImpl: async () => new Response("<html>", { status: 200 }) })).rejects.toThrow(/JSON/);
    const net = usosFetch("services/x", {}, auth, { fetchImpl: async () => { throw new Error("ECONNREFUSED"); } });
    await expect(net).rejects.toBeInstanceOf(AdapterUnavailableError);
    await expect(net).rejects.toThrow(/ECONNREFUSED/);
  });
});

describe("api adapter", () => {
  it("getProfile requests the documented fields and maps the answer", async () => {
    const calls: Call[] = [];
    const a = adapterWith({ "services/users/user": () => json(fixtureJson("user.json")) }, calls);
    const p = await a.getProfile();
    expect(p.firstName).toBe("Anna");
    expect(calls[0]?.params.fields).toContain("photo_urls[200x200]");
    expect(calls[0]?.params.fields).toContain("student_programmes[");
  });

  it("getTerms uses the terms attached to course editions (cached request) and flags the current one", async () => {
    const calls: Call[] = [];
    const a = adapterWith({ "services/courses/user": () => json(fixtureJson("courses-user.json")) }, calls);
    const terms = await a.getTerms();
    expect(terms.map((t) => [t.id, t.isCurrent])).toEqual([["2025Z", true], ["2024L", false]]);
    await a.getTerms();
    expect(calls.filter((c) => c.path === "services/courses/user")).toHaveLength(1);
    expect(calls[0]?.params.active_terms_only).toBe("false");
  });

  it("getTerms falls back to terms/search when courses/user carries no terms", async () => {
    const calls: Call[] = [];
    const a = adapterWith(
      {
        "services/courses/user": () => json({ course_editions: {} }),
        "services/terms/search": (c) =>
          json(c.params.query === "2025" ? [{ id: "2025Z", name: { pl: "Zimowy 2025/26" }, start_date: "2025-10-01", end_date: "2026-02-15" }] : [])
      },
      calls
    );
    const terms = await a.getTerms();
    expect(terms.map((t) => t.id)).toEqual(["2025Z"]);
    expect(calls.filter((c) => c.path === "services/terms/search").map((c) => c.params.query)).toEqual(["2024", "2025"]);
  });

  it("getCourses defaults to active terms and filters by term when given", async () => {
    const calls: Call[] = [];
    const a = adapterWith({ "services/courses/user": () => json(fixtureJson("courses-user.json")) }, calls);
    await a.getCourses();
    expect(calls[0]?.params.active_terms_only).toBe("true");
    expect(calls[0]?.params.fields).toContain("user_groups[course_unit_id|class_type|group_number|lecturers[first_name|last_name]]");
    expect((await a.getCourses("2024L")).map((c) => c.courseId)).toEqual(["INF-AM2"]);
    expect(calls[1]?.params.active_terms_only).toBe("false");
  });

  it("getTimetable walks the range in 7-day chunks and resolves lecturer names once", async () => {
    const calls: Call[] = [];
    const a = adapterWith(
      {
        "services/tt/user": (c) => json(c.params.start === "2025-10-06" ? fixtureJson("tt-user.json") : []),
        "services/users/users": () => json(fixtureJson("users-users.json"))
      },
      calls
    );
    const entries = await a.getTimetable("2025-10-06", "2025-10-20");
    const tt = calls.filter((c) => c.path === "services/tt/user");
    expect(tt.map((c) => [c.params.start, c.params.days])).toEqual([["2025-10-06", "7"], ["2025-10-13", "7"], ["2025-10-20", "1"]]);
    expect(tt[0]?.params.fields).toContain("lecturer_ids");
    const users = calls.filter((c) => c.path === "services/users/users");
    expect(users).toHaveLength(1);
    expect(users[0]?.params.user_ids).toBe("101|102");
    expect(entries).toHaveLength(2);
    expect(entries[0]?.lecturers).toEqual(["Marek Nowicki", "Tomasz Lis"]);
    await a.getTimetable("2025-10-06", "2025-10-06"); // names cached
    expect(calls.filter((c) => c.path === "services/users/users")).toHaveLength(1);
  });

  it("getTimetable survives a failing lecturer lookup but not an expired token", async () => {
    const ok = adapterWith({
      "services/tt/user": () => json(fixtureJson("tt-user.json")),
      "services/users/users": () => json({ message: "x" }, 500)
    });
    expect((await ok.getTimetable("2025-10-06", "2025-10-06"))[0]?.lecturers).toEqual([]);
    const expired = adapterWith({
      "services/tt/user": () => json(fixtureJson("tt-user.json")),
      "services/users/users": () => json({ message: "expired" }, 401)
    });
    await expect(expired.getTimetable("2025-10-06", "2025-10-06")).rejects.toBeInstanceOf(AdapterAuthError);
    expect(await ok.getTimetable("2025-10-07", "2025-10-06")).toEqual([]);
  });

  it("getGrades defaults to every term of the user, passes term_ids pipe-joined and enriches names", async () => {
    const calls: Call[] = [];
    const a = adapterWith(
      {
        "services/courses/user": () => json(fixtureJson("courses-user.json")),
        "services/grades/terms2": () => json(fixtureJson("grades-terms2.json"))
      },
      calls
    );
    const grades = await a.getGrades();
    expect(calls.find((c) => c.path === "services/grades/terms2")?.params.term_ids).toBe("2025Z|2024L");
    const unit = grades.find((g) => g.courseId === "INF-ALG" && g.classType === "Ćwiczenia");
    expect(unit?.courseName).toEqual({ pl: "Algorytmy i struktury danych", en: "Algorithms and data structures" });
    await a.getGrades(["2024L"]);
    expect(calls.filter((c) => c.path === "services/grades/terms2").at(-1)?.params.term_ids).toBe("2024L");
  });

  it("getGrades retries without `fields` when terms2 rejects them", async () => {
    const calls: Call[] = [];
    const a = adapterWith(
      {
        "services/courses/user": () => json(fixtureJson("courses-user.json")),
        "services/grades/terms2": (c) => (c.params.fields ? json({ message: "Invalid argument: fields" }, 400) : json(fixtureJson("grades-terms2.json")))
      },
      calls
    );
    expect((await a.getGrades(["2025Z"])).length).toBeGreaterThan(0);
    expect(calls.filter((c) => c.path === "services/grades/terms2")).toHaveLength(2);
  });

  it("getPayments maps with the Warsaw 'today' for overdue detection", async () => {
    const a = adapterWith({ "services/payments/user_payments": () => json(fixtureJson("payments.json")) });
    const payments = await a.getPayments();
    expect(payments.find((p) => p.id === "7002")?.state).toBe("overdue");
    expect(payments.find((p) => p.id === "7004")?.state).toBe("unpaid");
  });

  it("USOSmail: lists, reads and sends; unknown module becomes a graceful AdapterUnavailableError", async () => {
    const calls: Call[] = [];
    const a = adapterWith(
      {
        "services/mailclient/messages": () => json(fixtureJson("mail-messages.json")),
        "services/mailclient/message": () => json((fixtureJson("mail-messages.json") as { messages: unknown[] }).messages[0]),
        "services/mailclient/send": () => json({ id: 777 })
      },
      calls
    );
    expect((await a.listMessages({ limit: 5, offset: 10 })).map((m) => m.id)).toEqual(["5501", "5490"]);
    expect(calls[0]?.params).toMatchObject({ limit: "5", offset: "10", folder: "inbox" });
    expect((await a.getMessage("5501")).attachments).toHaveLength(1);
    expect(calls[1]?.params.message_id).toBe("5501");
    expect(await a.sendMessage(["a@b.pl", "c@d.pl"], "Temat", "Treść")).toEqual({ id: "777" });
    expect(calls[2]).toMatchObject({ method: "POST", params: { recipients: "a@b.pl|c@d.pl", subject: "Temat", body: "Treść" } });

    const missing = adapterWith({});
    for (const run of [() => missing.listMessages(), () => missing.getMessage("1"), () => missing.sendMessage(["x"], "s", "b")]) {
      const err = await run().catch((e: unknown) => e);
      expect(err).toBeInstanceOf(AdapterUnavailableError);
      expect((err as Error).message).toBe("USOSmail API niedostępne");
    }
    const unauth = adapterWith({ "services/mailclient/messages": () => json({ message: "no" }, 401) });
    await expect(unauth.listMessages()).rejects.toBeInstanceOf(AdapterAuthError);
  });

  it("dean's office contacts come from the user's faculty; [] when unavailable", async () => {
    const calls: Call[] = [];
    const a = adapterWith(
      {
        "services/users/user": () => json({ student_programmes: [{ programme: { faculty: { id: "WI" } } }, { programme: { faculty: { id: "WI" } } }] }),
        "services/fac/faculty": () => json(fixtureJson("faculty.json"))
      },
      calls
    );
    const contacts = await a.getDeanOfficeContacts();
    expect(contacts).toHaveLength(1);
    expect(calls.filter((c) => c.path === "services/fac/faculty")).toHaveLength(1);
    expect(calls.find((c) => c.path === "services/fac/faculty")?.params.fac_id).toBe("WI");
    expect(await adapterWith({}).getDeanOfficeContacts()).toEqual([]);
    expect(await adapterWith({ "services/users/user": () => json({}) }).getDeanOfficeContacts()).toEqual([]);
  });

  it("announcements: mapped, limited, [] when the news module is missing", async () => {
    const calls: Call[] = [];
    const a = adapterWith({ "services/news/search": () => json(fixtureJson("news.json")) }, calls);
    expect(await a.getAnnouncements(1)).toHaveLength(1);
    expect(calls[0]?.params.limit).toBe("1");
    expect(await adapterWith({}).getAnnouncements()).toEqual([]);
  });

  it("propagates AdapterAuthError from any method", async () => {
    const a = adapterWith({ "services/users/user": () => json({ message: "revoked" }, 401) });
    await expect(a.getProfile()).rejects.toBeInstanceOf(AdapterAuthError);
  });
});

describe("createUsosAdapter dispatch", () => {
  it("returns an adapter for every mode and uses creds.baseUrl for api mode", async () => {
    for (const c of [creds, { mode: "web" as const, cookies: "a=b", baseUrl: "https://u/" }, { mode: "mock" as const }]) {
      const a = createUsosAdapter(c, config);
      expect(typeof a.getProfile).toBe("function");
    }
    const calls: string[] = [];
    const a = createUsosAdapter({ ...creds, baseUrl: "https://other.example/api/" }, config, {
      fetchImpl: async (u) => {
        calls.push(u);
        return json(fixtureJson("user.json"));
      }
    });
    await a.getProfile();
    expect(calls[0]).toMatch(/^https:\/\/other\.example\/api\/services\/users\/user\?/);
  });

  it("exposes createUsosOAuthClient", () => {
    const c = createUsosOAuthClient(config);
    expect(typeof c.requestToken).toBe("function");
    expect(typeof c.accessToken).toBe("function");
  });
});
