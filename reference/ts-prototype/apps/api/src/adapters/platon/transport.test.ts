import { AdapterAuthError, AdapterUnavailableError } from "@vusos/shared";
import { describe, expect, it } from "vitest";
import { createAjaxTransport, createWsTransport, flattenMoodleArgs } from "./transport.js";

const BASE = "https://platon.vistula.edu.pl/";

interface Captured {
  url: string;
  init: RequestInit;
}

function fakeFetch(respond: () => Response | Promise<Response>): { fetch: typeof fetch; captured: Captured[] } {
  const captured: Captured[] = [];
  const f = (async (url: string | URL | Request, init?: RequestInit) => {
    captured.push({ url: String(url), init: init ?? {} });
    return respond();
  }) as typeof fetch;
  return { fetch: f, captured };
}

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });

describe("flattenMoodleArgs", () => {
  it("encodes arrays, nested objects, booleans and skips nullish", () => {
    expect(
      flattenMoodleArgs({
        classification: "inprogress",
        courseids: [12, 13],
        events: { timesortfrom: 100, nested: { a: true } },
        flag: false,
        nothing: null,
        undef: undefined,
        list: [{ id: 1, v: "x" }]
      })
    ).toEqual([
      ["classification", "inprogress"],
      ["courseids[0]", "12"],
      ["courseids[1]", "13"],
      ["events[timesortfrom]", "100"],
      ["events[nested][a]", "1"],
      ["flag", "0"],
      ["list[0][id]", "1"],
      ["list[0][v]", "x"]
    ]);
  });
});

describe("wsTransport", () => {
  it("POSTs form-encoded args to webservice/rest/server.php", async () => {
    const { fetch, captured } = fakeFetch(() => json({ ok: true }));
    const t = createWsTransport({ baseUrl: "https://platon.vistula.edu.pl", wstoken: "tok123", fetch });
    const res = await t.call<{ ok: boolean }>("core_calendar_get_action_events_by_timesort", {
      timesortfrom: 5,
      courseids: [12, 13],
      events: { x: "a b&c" }
    });
    expect(res).toEqual({ ok: true });
    const c = captured[0];
    expect(c?.url).toBe(`${BASE}webservice/rest/server.php`);
    expect(c?.init.method).toBe("POST");
    const body = new URLSearchParams(String(c?.init.body));
    expect(body.get("wstoken")).toBe("tok123");
    expect(body.get("wsfunction")).toBe("core_calendar_get_action_events_by_timesort");
    expect(body.get("moodlewsrestformat")).toBe("json");
    expect(body.get("timesortfrom")).toBe("5");
    expect(body.get("courseids[0]")).toBe("12");
    expect(body.get("courseids[1]")).toBe("13");
    expect(body.get("events[x]")).toBe("a b&c");
  });

  it.each([
    ["invalidtoken", AdapterAuthError],
    ["accessexception", AdapterAuthError],
    ["servicenotavailable", AdapterUnavailableError],
    ["invalidparameter", AdapterUnavailableError]
  ])("maps errorcode %s", async (errorcode, cls) => {
    const { fetch } = fakeFetch(() => json({ exception: "moodle_exception", errorcode, message: "boom" }));
    const t = createWsTransport({ baseUrl: BASE, wstoken: "t", fetch });
    await expect(t.call("x")).rejects.toBeInstanceOf(cls);
    await expect(t.call("x")).rejects.toThrow("boom");
  });

  it("maps HTTP and network failures", async () => {
    const t500 = createWsTransport({ baseUrl: BASE, wstoken: "t", fetch: fakeFetch(() => new Response("x", { status: 502 })).fetch });
    await expect(t500.call("x")).rejects.toBeInstanceOf(AdapterUnavailableError);
    const t403 = createWsTransport({ baseUrl: BASE, wstoken: "t", fetch: fakeFetch(() => new Response("x", { status: 403 })).fetch });
    await expect(t403.call("x")).rejects.toBeInstanceOf(AdapterAuthError);
    const tHtml = createWsTransport({ baseUrl: BASE, wstoken: "t", fetch: fakeFetch(() => new Response("<html>")).fetch });
    await expect(tHtml.call("x")).rejects.toBeInstanceOf(AdapterUnavailableError);
    const tNet = createWsTransport({
      baseUrl: BASE,
      wstoken: "t",
      fetch: (() => Promise.reject(new TypeError("fetch failed"))) as typeof fetch
    });
    await expect(tNet.call("x")).rejects.toBeInstanceOf(AdapterUnavailableError);
  });

  it("does not mistake array results for errors", async () => {
    const { fetch } = fakeFetch(() => json([{ id: 1 }]));
    const t = createWsTransport({ baseUrl: BASE, wstoken: "t", fetch });
    expect(await t.call("x")).toEqual([{ id: 1 }]);
  });

  describe("rewriteFileUrl", () => {
    const t = createWsTransport({ baseUrl: BASE, wstoken: "tok", fetch: fakeFetch(() => json({})).fetch });
    it("turns pluginfile.php into webservice/pluginfile.php with token", () => {
      expect(t.rewriteFileUrl(`${BASE}pluginfile.php/1/mod_resource/content/1/a.pdf`)).toBe(
        `${BASE}webservice/pluginfile.php/1/mod_resource/content/1/a.pdf?token=tok`
      );
    });
    it("keeps existing query and does not duplicate the token", () => {
      const once = t.rewriteFileUrl(`${BASE}webservice/pluginfile.php/1/a.pdf?forcedownload=1`);
      expect(once).toBe(`${BASE}webservice/pluginfile.php/1/a.pdf?forcedownload=1&token=tok`);
      expect(t.rewriteFileUrl(once)).toBe(once);
    });
    it("leaves non-file and foreign URLs untouched (never leaks the token)", () => {
      expect(t.rewriteFileUrl(`${BASE}mod/resource/view.php?id=1`)).toBe(`${BASE}mod/resource/view.php?id=1`);
      expect(t.rewriteFileUrl("https://evil.example/pluginfile.php/1/a.pdf")).toBe("https://evil.example/pluginfile.php/1/a.pdf");
    });
    it("sends no extra headers", () => {
      expect(t.fileHeaders()).toEqual({});
    });
  });
});

describe("ajaxTransport", () => {
  const make = (respond: () => Response | Promise<Response>) => {
    const f = fakeFetch(respond);
    return { ...f, t: createAjaxTransport({ baseUrl: BASE, cookies: "MoodleSession=abc", sesskey: "sk 1", fetch: f.fetch }) };
  };

  it("POSTs a JSON array to lib/ajax/service.php with cookie and returns data", async () => {
    const { t, captured } = make(() => json([{ error: false, data: { courses: [] } }]));
    const out = await t.call("core_course_get_enrolled_courses_by_timeline_classification", { classification: "all" });
    expect(out).toEqual({ courses: [] });
    const c = captured[0];
    expect(c?.url).toBe(
      `${BASE}lib/ajax/service.php?sesskey=sk%201&info=core_course_get_enrolled_courses_by_timeline_classification`
    );
    expect((c?.init.headers as Record<string, string>).Cookie).toBe("MoodleSession=abc");
    expect(c?.init.redirect).toBe("manual");
    expect(JSON.parse(String(c?.init.body))).toEqual([
      { index: 0, methodname: "core_course_get_enrolled_courses_by_timeline_classification", args: { classification: "all" } }
    ]);
  });

  it.each([
    ["servicerequireslogin", AdapterAuthError],
    ["invalidsesskey", AdapterAuthError],
    ["invalidparameter", AdapterUnavailableError]
  ])("maps exception.errorcode %s", async (errorcode, cls) => {
    const { t } = make(() => json([{ error: true, exception: { message: "nope", errorcode, link: "" } }]));
    await expect(t.call("x")).rejects.toBeInstanceOf(cls);
  });

  it("treats redirects and non-JSON bodies as an expired session", async () => {
    const redirect = make(() => new Response(null, { status: 302, headers: { location: `${BASE}login/index.php` } }));
    await expect(redirect.t.call("x")).rejects.toBeInstanceOf(AdapterAuthError);
    const html = make(() => new Response("<html>login</html>", { status: 200 }));
    await expect(html.t.call("x")).rejects.toBeInstanceOf(AdapterAuthError);
  });

  it("handles a bare error object and server errors", async () => {
    const bare = make(() => json({ error: "Session key is invalid" }));
    await expect(bare.t.call("x")).rejects.toBeInstanceOf(AdapterAuthError);
    const s500 = make(() => new Response("oops", { status: 500 }));
    await expect(s500.t.call("x")).rejects.toBeInstanceOf(AdapterUnavailableError);
  });

  it("uses Cookie for files and un-rewrites WS URLs", () => {
    const { t } = make(() => json([]));
    expect(t.fileHeaders()).toEqual({ Cookie: "MoodleSession=abc" });
    expect(t.rewriteFileUrl(`${BASE}webservice/pluginfile.php/1/a.pdf`)).toBe(`${BASE}pluginfile.php/1/a.pdf`);
    expect(t.rewriteFileUrl(`${BASE}pluginfile.php/1/a.pdf`)).toBe(`${BASE}pluginfile.php/1/a.pdf`);
  });
});
