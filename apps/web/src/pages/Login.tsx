import { Navigate } from "react-router-dom";
import { LogIn } from "lucide-react";
import { useMe } from "../api/hooks";
import { apiBase, isNative, openExternal } from "../platform";
import { isApiError } from "../api/client";
import { Brand, DisclaimerFooter } from "../components/Layout";
import { ErrorState } from "../components/ErrorState";
import { Skeleton } from "../components/Skeleton";

export function Login() {
  const me = useMe();
  if (me.isSuccess) return <Navigate to="/" replace />;

  const unauthorized = me.isError && isApiError(me.error) && me.error.status === 401;
  return (
    <div className="flex min-h-full flex-col items-center justify-center gap-6 p-4">
      <div className="card w-full max-w-sm space-y-5 text-center">
        <div className="flex justify-center"><Brand /></div>
        <p className="muted text-xs">Jednolita platforma studenta: USOS i Platon w jednym miejscu.</p>
        {me.isPending && <Skeleton className="h-10" />}
        {unauthorized && (
          <a
            href={`${apiBase()}/api/auth/login`}
            className="btn btn-primary w-full !py-2.5 no-underline"
            onClick={(e) => {
              if (isNative()) {
                e.preventDefault();
                openExternal(`${apiBase()}/api/auth/login?client=native`);
              }
            }}
          >
            <LogIn size={16} aria-hidden /> Zaloguj kontem uczelni (M365)
          </a>
        )}
        {me.isError && !unauthorized && <ErrorState error={me.error} onRetry={() => void me.refetch()} />}
        <p className="muted text-[11px]">
          Logujesz się kontem Microsoft 365 uczelni. Hasła do USOS ani Platona nigdy nie trafiają do aplikacji.
        </p>
      </div>
      <DisclaimerFooter />
    </div>
  );
}
