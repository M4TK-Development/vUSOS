import type { ReactNode } from "react";
import type { UseQueryResult } from "@tanstack/react-query";
import type { SourceSystem } from "@vusos/shared";
import { isApiError } from "../api/client";
import { ConnectPrompt } from "./ConnectPrompt";
import { ErrorState } from "./ErrorState";
import { SkeletonLines } from "./Skeleton";

interface Props<T> {
  query: UseQueryResult<T>;
  children: (data: T) => ReactNode;
  skeleton?: ReactNode;
  /** Fallback system for the ConnectPrompt when the error carries none. */
  system?: SourceSystem;
  isEmpty?: (data: T) => boolean;
  empty?: ReactNode;
  compact?: boolean;
}

/** Renders loading / ConnectPrompt (409, 424) / error / empty / data states for a query. */
export function QueryBoundary<T>({ query, children, skeleton, system, isEmpty, empty, compact }: Props<T>) {
  if (query.isPending) return <>{skeleton ?? <SkeletonLines />}</>;
  if (query.isError) {
    const err = query.error;
    if (isApiError(err) && err.needsConnection) {
      const sys = err.system ?? system;
      if (sys) return <ConnectPrompt system={sys} expired={err.expired} compact={compact} />;
    }
    return <ErrorState error={err} onRetry={() => void query.refetch()} />;
  }
  const data = query.data as T;
  if (isEmpty?.(data)) return <>{empty ?? null}</>;
  return <>{children(data)}</>;
}
