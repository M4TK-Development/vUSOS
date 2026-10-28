import { Navigate, Outlet } from "react-router-dom";
import { useMe } from "../api/hooks";
import { isApiError } from "../api/client";
import { ErrorState } from "./ErrorState";
import { Skeleton } from "./Skeleton";

/** Protects app routes: unauthenticated users (401 from /api/me) are sent to /login. */
export function AuthGate() {
  const me = useMe();
  if (me.isPending) {
    return (
      <div className="mx-auto max-w-md space-y-3 p-8" role="status" aria-label="Ładowanie">
        <Skeleton className="h-8 w-32" />
        <Skeleton className="h-24" />
      </div>
    );
  }
  if (me.isError) {
    if (isApiError(me.error) && me.error.status === 401) return <Navigate to="/login" replace />;
    return (
      <div className="mx-auto max-w-md p-8">
        <ErrorState error={me.error} onRetry={() => void me.refetch()} />
      </div>
    );
  }
  return <Outlet />;
}
