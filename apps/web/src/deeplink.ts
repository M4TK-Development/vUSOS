import type { QueryClient } from "@tanstack/react-query";
import { api, ApiError } from "./api/client";
import { setToken } from "./lib/token";
import { toast } from "./toast";

export type DeepLink =
  | { kind: "auth"; code: string }
  | { kind: "connected"; system: string }
  | { kind: "moodle-token"; url: string }
  | { kind: "unknown" };

/** Parses `vusos://auth?code=…`, `vusos://connected?system=usos`, `<scheme>://token=BASE64`. */
export function parseDeepLink(url: string): DeepLink {
  const m = /^[a-z][a-z0-9+.-]*:\/\/\/?(.*)$/i.exec(url.trim());
  if (!m) return { kind: "unknown" };
  const rest = m[1] ?? "";
  if (rest.startsWith("token=")) return { kind: "moodle-token", url: url.trim() };
  const qi = rest.indexOf("?");
  const head = (qi === -1 ? rest : rest.slice(0, qi)).replace(/\/$/, "");
  const params = new URLSearchParams(qi === -1 ? "" : rest.slice(qi + 1));
  if (head === "auth") {
    const code = params.get("code");
    return code ? { kind: "auth", code } : { kind: "unknown" };
  }
  if (head === "connected") return { kind: "connected", system: params.get("system") ?? "" };
  return { kind: "unknown" };
}

export interface DeepLinkDeps {
  queryClient: QueryClient;
  navigate: (to: string) => void;
}

export async function handleDeepLink(url: string, { queryClient, navigate }: DeepLinkDeps): Promise<void> {
  const link = parseDeepLink(url);
  const refreshConnections = () => {
    void queryClient.invalidateQueries({ queryKey: ["connections"] });
    void queryClient.invalidateQueries({ queryKey: ["me"] });
    void queryClient.invalidateQueries({ queryKey: ["dashboard"] });
  };
  try {
    switch (link.kind) {
      case "auth": {
        const res = await api<{ token: string }>("/auth/native/exchange", { method: "POST", body: { code: link.code } });
        setToken(res.token);
        await queryClient.invalidateQueries();
        toast("Zalogowano.", "success");
        navigate("/");
        break;
      }
      case "connected":
        refreshConnections();
        toast(link.system === "platon" ? "Połączono z Platonem." : link.system === "usos" ? "Połączono z USOS." : "Połączono.", "success");
        navigate("/settings");
        break;
      case "moodle-token":
        await api("/connections/platon/ws", { method: "POST", body: { launchUrl: link.url } });
        refreshConnections();
        toast("Połączono z Platonem.", "success");
        navigate("/settings");
        break;
      default:
        break;
    }
  } catch (e) {
    toast(e instanceof ApiError ? e.message : "Nie udało się obsłużyć linku.", "error");
  }
}

/** Exposes window.vusosHandleDeepLink and consumes a `?deeplink=` query param present on first load. */
export function installDeepLinks(deps: DeepLinkDeps): void {
  window.vusosHandleDeepLink = (url: string) => void handleDeepLink(url, deps);
  const sp = new URLSearchParams(window.location.search);
  const dl = sp.get("deeplink");
  if (dl) {
    sp.delete("deeplink");
    const qs = sp.toString();
    window.history.replaceState(null, "", window.location.pathname + (qs ? `?${qs}` : "") + window.location.hash);
    void handleDeepLink(dl, deps);
  }
}
