import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
import { clearToken } from "../lib/token";
import { isApiError } from "./client";

/**
 * Global error handling: 401 -> redirect to /login (via the supplied callback).
 * 409/424 are intentionally NOT handled here; <QueryBoundary/> renders <ConnectPrompt/> inline.
 */
export function createQueryClient(onUnauthorized: () => void): QueryClient {
  const handle = (error: unknown) => {
    if (isApiError(error) && error.status === 401) {
      clearToken();
      onUnauthorized();
    }
  };
  return new QueryClient({
    queryCache: new QueryCache({ onError: handle }),
    mutationCache: new MutationCache({ onError: handle }),
    defaultOptions: {
      queries: {
        staleTime: 60_000,
        refetchOnWindowFocus: false,
        retry: (count, error) => {
          if (isApiError(error) && error.status >= 400 && error.status < 500) return false;
          return count < 1;
        },
      },
    },
  });
}
