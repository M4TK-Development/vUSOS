import { describe, expect, it } from "vitest";
import { createPlatonAdapter, parseMobileLaunchToken } from "./index.js";

const config = { baseUrl: "https://platon.vistula.edu.pl/" };

describe("createPlatonAdapter", () => {
  it("builds ws and web adapters whose file downloads carry the right auth", async () => {
    const ws = createPlatonAdapter({ mode: "ws", wstoken: "tok", baseUrl: config.baseUrl }, config);
    expect(await ws.getFileDownload(`${config.baseUrl}pluginfile.php/1/a.pdf`)).toEqual({
      url: `${config.baseUrl}webservice/pluginfile.php/1/a.pdf?token=tok`,
      headers: {}
    });
    const web = createPlatonAdapter({ mode: "web", cookies: "MoodleSession=z", sesskey: "s", baseUrl: config.baseUrl }, config);
    expect(await web.getFileDownload(`${config.baseUrl}pluginfile.php/1/a.pdf`)).toEqual({
      url: `${config.baseUrl}pluginfile.php/1/a.pdf`,
      headers: { Cookie: "MoodleSession=z" }
    });
  });

  it("re-exports parseMobileLaunchToken", () => {
    expect(parseMobileLaunchToken("0123456789abcdef0123456789abcdef").wstoken).toHaveLength(32);
  });
});
