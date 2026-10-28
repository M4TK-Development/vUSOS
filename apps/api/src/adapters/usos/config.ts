import type { FetchLike } from "./client.js";

export interface UsosConfig {
  apiBaseUrl: string;
  consumerKey: string;
  consumerSecret: string;
  usoswebBaseUrl: string;
}

/** Optional test seams; production code passes nothing. */
export interface UsosAdapterDeps {
  fetchImpl?: FetchLike;
  now?: () => Date;
}
